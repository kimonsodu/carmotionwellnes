package com.orbital.phone

import kotlin.math.cos
import kotlin.math.sin

/**
 * Synthetic IMU source for "Simulation Mode" — produces raw device-frame accel + gyro that mimic a
 * real driving frame, so the WHOLE motion pipeline (gravity split, axis learn, gates, jerk limit,
 * deadband, cue render) is exercised at a desk WITHOUT driving. It is a *source*: when sim is ON,
 * [OverlayService] feeds these samples into the SAME [VehicleFilter] entry points the real sensors
 * use (see SIM_SPEC.md). Identical physics to the Windows build so both platforms test the same.
 *
 * Frame: device flat / screen up = world ENU. x=right(East), y=forward(North), z=up,
 * G=9.81. Resting accel (gravity reaction) = (0,0,+G). Accel reading = pitched-gravity + linear
 * accel rotated into the device frame by the seating heading psi. Each sample ALSO carries a
 * device->world rotation matrix R built from the SAME grade+seat, so the filter's R-based grade
 * path sees the exact pitch the accel encodes — like a real rotation-vector phone, NOT identity
 * (identity claims "flat" and zeroes the hill cue even though accel leans).
 *
 * Single-threaded: [step]/[setScenario] are called from OverlayService's sensor HandlerThread.
 */
class MotionSimulator {

    companion object {
        // scenario codes — persisted as SettingsStore.K_SIM_SCENARIO (0..MAX). Seating (side/rear-facing)
        // is a SEPARATE orthogonal toggle (seatPsiDeg), applied to whichever scenario runs.
        const val OFF = 0; const val ALL = 1
        const val ACCELERATE = 2; const val BRAKE = 3; const val TURN_LEFT = 4; const val TURN_RIGHT = 5
        const val UPHILL = 6; const val DOWNHILL = 7
        // Combined / conflicting-cue scenarios (tilt vs accel/decel) for GROSS TUNING: drive a real
        // longitudinal accel AND a real road grade (and, for COMBO, a turn) simultaneously so the
        // separate accel and hill cues fire at once and can be balanced against each other.
        const val ACCEL_UPHILL = 8; const val ACCEL_DOWNHILL = 9
        const val BRAKE_UPHILL = 10; const val BRAKE_DOWNHILL = 11
        const val COMBO = 12
        const val MAX = COMBO

        private const val G = 9.81f
        private const val DT = 1f / 62f    // synthetic step matches the ~62 Hz drive loop
        // Every maneuver is driven as a LINEAR ramp at a FIXED SLOPE: a steady rate of change makes the
        // band-pass/gravity-split emit a CONSTANT cue -> the dots drift steadily ONE way for the whole
        // active window (speed is independent of how long that window is). On entering a REST we drive 0
        // AND fire a one-shot filter reset, so the absorbed gravity is cleared and the dots simply STOP
        // (no reverse drift). SLOPE_REF sets the drift speed; ACTIVE/REST set the on/off durations.
        private const val SLOPE_REF = 3.0f     // fixed ramp slope -> sets the one-direction drift speed
        private const val ACTIVE = 9.0f        // active window seconds (single scenarios)
        private const val REST = 4.0f          // rest window seconds (single scenarios)
    }

    /** One driving phase. dur is only used by the looping "All" script (single-hold ignores it). */
    private class Phase(
        @JvmField val name: String, @JvmField val dur: Float,
        @JvmField val aFwd: Float, @JvmField val aRight: Float, @JvmField val yawDeg: Float,
        @JvmField val gradeDeg: Float, @JvmField val psiDeg: Float
    )

    /** Synthetic sample: device-frame accel incl. gravity (m/s^2) + gyro (rad/s) + phase label +
     *  device->world (ENU) rotation matrix R (row-major) consistent with the baked-in grade+seat.
     *  [reset] is true ONLY on the frame that enters a Rest -> the caller clears the filter so the
     *  absorbed gravity is dropped and the 0-drive rest produces no reverse cue. */
    class Sample(
        @JvmField val accel: FloatArray, @JvmField val gyro: FloatArray, @JvmField val phase: String,
        @JvmField val R: FloatArray, @JvmField val reset: Boolean
    )

    // The "All" script (table in SIM_SPEC.md). Loops forever; after the last Rest it returns to #1
    // (index 0's initial settle plays only once at start).
    private val script = arrayOf(
        Phase("Rest", 1.5f, 0f, 0f, 0f, 0f, 0f),
        Phase("Accelerate", 3.0f, 2.5f, 0f, 0f, 0f, 0f),
        Phase("Rest", 1.5f, 0f, 0f, 0f, 0f, 0f),
        Phase("Brake", 3.0f, -3.5f, 0f, 0f, 0f, 0f),
        Phase("Rest", 1.5f, 0f, 0f, 0f, 0f, 0f),
        Phase("Turn left", 3.0f, 0f, 2.8f, 28f, 0f, 0f),
        Phase("Rest", 1.5f, 0f, 0f, 0f, 0f, 0f),
        Phase("Turn right", 3.0f, 0f, -2.8f, -28f, 0f, 0f),
        Phase("Rest", 1.5f, 0f, 0f, 0f, 0f, 0f),
        Phase("Uphill", 3.5f, 0.4f, 0f, 0f, 9f, 0f),
        Phase("Rest", 1.5f, 0f, 0f, 0f, 0f, 0f),
        Phase("Downhill", 3.5f, -0.4f, 0f, 0f, -9f, 0f),
        Phase("Rest", 1.5f, 0f, 0f, 0f, 0f, 0f),
        Phase("Accel + downhill", 3.5f, 2.5f, 0f, 0f, -9f, 0f),  // CONFLICT: accel pushes back, hill leans nose-down
        Phase("Rest", 1.5f, 0f, 0f, 0f, 0f, 0f),
        Phase("Combo", 3.5f, 2.0f, 1.8f, 18f, 7f, 0f),          // accel + turn + hill all firing at once
        Phase("Rest", 2.0f, 0f, 0f, 0f, 0f, 0f)                // loop back to #1
    )
    // Seating (side/rear-facing) is an orthogonal toggle (seatPsiDeg) applied to every phase, so each
    // motion can be tested from any seat — no dedicated sideways/rear phases needed.

    private var scenario = OFF
    private var single = singleFor(OFF)
    // Seating heading (deg), set from the app's seat toggles: 0 forward, 90 side-facing (train),
    // 180 rear-facing, 270 both. Applied to whatever scenario runs.
    @Volatile @JvmField var seatPsiDeg = 0f
    // Direction flips (mirror the DotsView Flip ↕/↔/⛰ controls). Applied here in the VEHICLE frame,
    // BEFORE the seat rotation, so each flip reverses its MANEUVER in every seat — unlike the
    // screen-axis flips in DotsView, which become no-ops in side seats (where accel/turn land on the
    // other screen axis). OverlayService keeps these in sync with the live settings and tells
    // DotsView to neutralise its screen-axis flips while a sim runs, so they aren't double-applied.
    @Volatile @JvmField var flipV = false       // reverse accel/brake
    @Volatile @JvmField var flipH = false       // reverse turn
    @Volatile @JvmField var flipGrade = false   // reverse ONLY the hill/grade cue
    private var idx = 0                 // current "All" phase
    private var clock = 0f              // seconds into the current phase (or into the held ramp)
    private var wasRest = false         // previous frame's rest state -> one-shot reset on rest entry
    private var prevGradeRad = 0f       // for the analytic pitch-rate gyro about device-x
    @Volatile @JvmField var currentPhase = "Off"   // surfaced to the UI/status (read off-thread)

    /** Pick a scenario (resets the clock). OFF disables sim; the service then uses real sensors. */
    fun setScenario(s: Int) {
        scenario = s.coerceIn(OFF, MAX)
        single = singleFor(scenario)
        idx = 0; clock = 0f; wasRest = false; prevGradeRad = 0f
        currentPhase = if (scenario == ALL) script[0].name else single.name
    }

    /** Advance one ~62 Hz tick and emit the synthetic sample. */
    fun step(): Sample {
        val aFwd: Float; val aRight: Float; val yawDeg: Float; val gradeDeg: Float; val psiDeg: Float
        val name: String
        val isRest: Boolean
        if (scenario == ALL) {
            clock += DT
            var ph = script[idx]
            if (clock >= ph.dur) {                            // advance; after the last phase, loop to #1
                clock -= ph.dur
                idx = if (idx >= script.lastIndex) 1 else idx + 1
                ph = script[idx]
            }
            isRest = ph.name == "Rest"
            // LINEAR ramp at a fixed slope on active phases; 0 on Rest (no reverse drift).
            val s = if (isRest) 0f else clock / SLOPE_REF
            name = ph.name
            aFwd = ph.aFwd * s; aRight = ph.aRight * s; yawDeg = ph.yawDeg * s; gradeDeg = ph.gradeDeg * s
        } else {
            clock += DT
            val cyc = clock % (ACTIVE + REST)                  // single scenario: active window -> rest, repeat
            isRest = cyc >= ACTIVE
            // LINEAR up at a fixed slope -> constant, steady one-direction cue; 0 during rest -> dots STOP.
            val s = if (isRest) 0f else cyc / SLOPE_REF
            name = if (isRest) "Rest" else single.name
            aFwd = single.aFwd * s; aRight = single.aRight * s; yawDeg = single.yawDeg * s; gradeDeg = single.gradeDeg * s
        }
        val reset = isRest && !wasRest                        // one-shot on the frame that ENTERS a rest
        wasRest = isRest
        psiDeg = seatPsiDeg                                   // seating applies to whatever scenario is running

        // Direction flips act HERE, in the vehicle frame, BEFORE the seat rotation below — so each one
        // reverses its MANEUVER (accel/brake, turn, hill) in EVERY seat, matching the UI's promise.
        val aFwdM = aFwd * (if (flipV) -1f else 1f)
        val aRightM = aRight * (if (flipH) -1f else 1f)
        val yawDegM = yawDeg * (if (flipH) -1f else 1f)
        val gradeDegM = gradeDeg * (if (flipGrade) -1f else 1f)

        // Seating heading psi rotates the vehicle linear accel into the device frame (psi=0 -> x=right,
        // y=forward; psi=90 -> fwd accel lands on device-x, proving sideways/train seating is handled).
        val psi = Math.toRadians(psiDeg.toDouble())
        val sinP = sin(psi).toFloat(); val cosP = cos(psi).toFloat()
        val gradeRad = Math.toRadians(gradeDegM.toDouble()).toFloat()
        // Road grade pitches gravity about device-x: g' = (0, -G·sinθ, G·cosθ) — a longitudinal grav
        // component drives the fore cue via the gravity path (distinct from accel/brake linear accel).
        // The road grade leans gravity along the VEHICLE-FORWARD axis, which the seating heading
        // rotates too — so a side-facing seat puts the hill cue on the lateral axis (90°), just like
        // accel/brake. leanFwd is that longitudinal gravity component; (sinP,cosP) is vehicle-forward
        // in the device frame.
        val sinG = sin(gradeRad); val cosG = cos(gradeRad)
        val leanFwd = -G * sinG
        val ax = aFwdM * sinP + aRightM * cosP + leanFwd * sinP
        val ay = aFwdM * cosP - aRightM * sinP + leanFwd * cosP
        val az = G * cosG
        // device->world (ENU) row-major: rows = world East / North / Up expressed in device coords.
        // Row 2 (Up) = the gravity-reaction direction baked into (ax,ay,az)/G, so the filter's
        // R-based grade path reads the SAME pitch the accel encodes (flat -> identity, no regression;
        // hill -> leans, so the cue fires; rear-facing psi=180 flips its sign, just like real life).
        val R = floatArrayOf(
            cosP,         -sinP,        0f,
            cosG * sinP,   cosG * cosP, sinG,
           -sinG * sinP,  -sinG * cosP, cosG
        )
        // gyro rad/s: pitchRate is the grade ramp's derivative about the vehicle-RIGHT axis
        // (cosP,-sinP) so it rotates with the seat too; yawRate is the cornering rate about z.
        val pitchRate = (gradeRad - prevGradeRad) / DT; prevGradeRad = gradeRad
        val yawRate = Math.toRadians(yawDegM.toDouble()).toFloat()

        currentPhase = name
        return Sample(floatArrayOf(ax, ay, az), floatArrayOf(pitchRate * cosP, -pitchRate * sinP, yawRate), name, R, reset)
    }

    // Target hold for a single picked scenario (dur unused; ramps in then holds indefinitely).
    private fun singleFor(s: Int): Phase = when (s) {
        ACCELERATE -> Phase("Accelerate", 0f, 2.5f, 0f, 0f, 0f, 0f)
        BRAKE -> Phase("Brake", 0f, -3.5f, 0f, 0f, 0f, 0f)
        TURN_LEFT -> Phase("Turn left", 0f, 0f, 2.8f, 28f, 0f, 0f)
        TURN_RIGHT -> Phase("Turn right", 0f, 0f, -2.8f, -28f, 0f, 0f)
        UPHILL -> Phase("Uphill", 0f, 0.4f, 0f, 0f, 9f, 0f)
        DOWNHILL -> Phase("Downhill", 0f, -0.4f, 0f, 0f, -9f, 0f)
        // Combined cues — real accel AND a real grade at once, so the longitudinal accel cue and the
        // hill/pitch cue drive together and can be gross-tuned against each other. "uphill" reinforces
        // (climb while speeding up / descend while braking); "downhill" conflicts (accel while nosing
        // down, brake while nosing up). COMBO adds a turn so all channels fire simultaneously.
        ACCEL_UPHILL -> Phase("Accel + uphill", 0f, 2.5f, 0f, 0f, 9f, 0f)
        ACCEL_DOWNHILL -> Phase("Accel + downhill", 0f, 2.5f, 0f, 0f, -9f, 0f)
        BRAKE_UPHILL -> Phase("Brake + uphill", 0f, -3.5f, 0f, 0f, 9f, 0f)
        BRAKE_DOWNHILL -> Phase("Brake + downhill", 0f, -3.5f, 0f, 0f, -9f, 0f)
        COMBO -> Phase("Combo (accel+turn+hill)", 0f, 2.0f, 1.8f, 18f, 7f, 0f)
        else -> Phase("Off", 0f, 0f, 0f, 0f, 0f, 0f)
    }
}
