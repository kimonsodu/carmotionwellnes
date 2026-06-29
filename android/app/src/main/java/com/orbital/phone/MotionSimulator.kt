package com.orbital.phone

import kotlin.math.cos
import kotlin.math.min
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
        // scenario codes — persisted as SettingsStore.K_SIM_SCENARIO (0..7). Seating (side/rear-facing)
        // is a SEPARATE orthogonal toggle (seatPsiDeg), applied to whichever scenario runs.
        const val OFF = 0; const val ALL = 1
        const val ACCELERATE = 2; const val BRAKE = 3; const val TURN_LEFT = 4; const val TURN_RIGHT = 5
        const val UPHILL = 6; const val DOWNHILL = 7
        const val MAX = DOWNHILL

        private const val G = 9.81f
        private const val RAMP = 0.6f      // smoothstep ease-in/out so the cue glides (spec)
        private const val DT = 1f / 62f    // synthetic step matches the ~62 Hz drive loop
        // A SINGLE held scenario LINEARLY ramps the maneuver up (a steady rate of change makes the
        // band-pass/gravity-split emit a CONSTANT cue -> the dots drift steadily ONE way for the whole
        // active window, instead of a brief spike that snaps back). Then a slower release ramps it down
        // ("Rest") so the reverse is gentle. Repeats so seat mirroring stays visible.
        private const val SINGLE_ON = 3.0f     // steady one-direction drift, labelled with the maneuver
        private const val SINGLE_OFF = 4.0f    // slower release -> gentle settle, labelled "Rest"
    }

    /** One driving phase. dur is only used by the looping "All" script (single-hold ignores it). */
    private class Phase(
        @JvmField val name: String, @JvmField val dur: Float,
        @JvmField val aFwd: Float, @JvmField val aRight: Float, @JvmField val yawDeg: Float,
        @JvmField val gradeDeg: Float, @JvmField val psiDeg: Float
    )

    /** Synthetic sample: device-frame accel incl. gravity (m/s^2) + gyro (rad/s) + phase label +
     *  device->world (ENU) rotation matrix R (row-major) consistent with the baked-in grade+seat. */
    class Sample(
        @JvmField val accel: FloatArray, @JvmField val gyro: FloatArray, @JvmField val phase: String,
        @JvmField val R: FloatArray
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
        Phase("Rest", 2.0f, 0f, 0f, 0f, 0f, 0f)                // loop back to #1
    )
    // Seating (side/rear-facing) is an orthogonal toggle (seatPsiDeg) applied to every phase, so each
    // motion can be tested from any seat — no dedicated sideways/rear phases needed.

    private var scenario = OFF
    private var single = singleFor(OFF)
    // Seating heading (deg), set from the app's seat toggles: 0 forward, 90 side-facing (train),
    // 180 rear-facing, 270 both. Applied to whatever scenario runs.
    @Volatile @JvmField var seatPsiDeg = 0f
    private var idx = 0                 // current "All" phase
    private var clock = 0f              // seconds into the current phase (or into the held ramp)
    private var prevGradeRad = 0f       // for the analytic pitch-rate gyro about device-x
    @Volatile @JvmField var currentPhase = "Off"   // surfaced to the UI/status (read off-thread)

    /** Pick a scenario (resets the clock). OFF disables sim; the service then uses real sensors. */
    fun setScenario(s: Int) {
        scenario = s.coerceIn(OFF, MAX)
        single = singleFor(scenario)
        idx = 0; clock = 0f; prevGradeRad = 0f
        currentPhase = if (scenario == ALL) script[0].name else single.name
    }

    /** Advance one ~62 Hz tick and emit the synthetic sample. */
    fun step(): Sample {
        val aFwd: Float; val aRight: Float; val yawDeg: Float; val gradeDeg: Float; val psiDeg: Float
        val name: String
        if (scenario == ALL) {
            clock += DT
            var ph = script[idx]
            if (clock >= ph.dur) {                            // advance; after the last phase, loop to #1
                clock -= ph.dur
                idx = if (idx >= script.lastIndex) 1 else idx + 1
                ph = script[idx]
            }
            val s = min(smoothstep(clock / RAMP), smoothstep((ph.dur - clock) / RAMP))  // ease in AND out
            name = ph.name
            aFwd = ph.aFwd * s; aRight = ph.aRight * s; yawDeg = ph.yawDeg * s; gradeDeg = ph.gradeDeg * s
        } else {
            clock += DT
            val cyc = clock % (SINGLE_ON + SINGLE_OFF)         // single scenario: ramp up -> slow release, repeat
            val s: Float
            if (cyc < SINGLE_ON) { s = cyc / SINGLE_ON; name = single.name }   // LINEAR up -> constant, steady cue
            else { s = 1f - (cyc - SINGLE_ON) / SINGLE_OFF; name = "Rest" }     // slow linear release
            aFwd = single.aFwd * s; aRight = single.aRight * s; yawDeg = single.yawDeg * s; gradeDeg = single.gradeDeg * s
        }
        psiDeg = seatPsiDeg                                   // seating applies to whatever scenario is running

        // Seating heading psi rotates the vehicle linear accel into the device frame (psi=0 -> x=right,
        // y=forward; psi=90 -> fwd accel lands on device-x, proving sideways/train seating is handled).
        val psi = Math.toRadians(psiDeg.toDouble())
        val sinP = sin(psi).toFloat(); val cosP = cos(psi).toFloat()
        val gradeRad = Math.toRadians(gradeDeg.toDouble()).toFloat()
        // Road grade pitches gravity about device-x: g' = (0, -G·sinθ, G·cosθ) — a longitudinal grav
        // component drives the fore cue via the gravity path (distinct from accel/brake linear accel).
        // The road grade leans gravity along the VEHICLE-FORWARD axis, which the seating heading
        // rotates too — so a side-facing seat puts the hill cue on the lateral axis (90°), just like
        // accel/brake. leanFwd is that longitudinal gravity component; (sinP,cosP) is vehicle-forward
        // in the device frame.
        val sinG = sin(gradeRad); val cosG = cos(gradeRad)
        val leanFwd = -G * sinG
        val ax = aFwd * sinP + aRight * cosP + leanFwd * sinP
        val ay = aFwd * cosP - aRight * sinP + leanFwd * cosP
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
        val yawRate = Math.toRadians(yawDeg.toDouble()).toFloat()

        currentPhase = name
        return Sample(floatArrayOf(ax, ay, az), floatArrayOf(pitchRate * cosP, -pitchRate * sinP, yawRate), name, R)
    }

    // Hermite smoothstep, clamped — gentle ease so the grade ramp stays under Windows' tilt-reject.
    private fun smoothstep(x: Float): Float { val t = x.coerceIn(0f, 1f); return t * t * (3f - 2f * t) }

    // Target hold for a single picked scenario (dur unused; ramps in then holds indefinitely).
    private fun singleFor(s: Int): Phase = when (s) {
        ACCELERATE -> Phase("Accelerate", 0f, 2.5f, 0f, 0f, 0f, 0f)
        BRAKE -> Phase("Brake", 0f, -3.5f, 0f, 0f, 0f, 0f)
        TURN_LEFT -> Phase("Turn left", 0f, 0f, 2.8f, 28f, 0f, 0f)
        TURN_RIGHT -> Phase("Turn right", 0f, 0f, -2.8f, -28f, 0f, 0f)
        UPHILL -> Phase("Uphill", 0f, 0.4f, 0f, 0f, 9f, 0f)
        DOWNHILL -> Phase("Downhill", 0f, -0.4f, 0f, 0f, -9f, 0f)
        else -> Phase("Off", 0f, 0f, 0f, 0f, 0f, 0f)
    }
}
