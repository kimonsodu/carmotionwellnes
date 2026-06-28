package com.orbit.phone

import android.hardware.SensorManager
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Shared Apple-Vehicle-Motion-Cues-grade estimator. Turns raw IMU (+optional orientation & GPS)
 * into hand-rejected vehicle-motion channels. Used by BOTH OverlayService (drives DotsView) and
 * SensorService (streams to the laptop) so phone and PC apply IDENTICAL hand rejection.
 *
 * Pipeline per accel sample (~62 Hz):
 *   A. linear accel (gravity removed: sensor-provided, or self-split if no fused linear sensor)
 *   B. project into ENU world frame via rotation matrix R (device->world); tilt -> 0 horizontal accel
 *   B2. if GPS bearing is valid (moving), de-rotate ENU -> VEHICLE frame (forward / right) so the
 *       longitudinal vs lateral split holds regardless of compass heading
 *   C. band-pass each horizontal axis to the vehicle band (LPF 0.8 Hz, HPF 0.08 Hz auto-recenter)
 *   D. soft gyro gate (hand motion is rotation-rich; vehicle accel is rotationally quiet)
 *   E. yaw-rate turn cue (real cornering yaws; drives lateral directly so the gate can't kill turns)
 *   F. jerk slew-limit + soft deadband (kills <200 ms hand jolts and idle noise)
 *   G. GPS in-vehicle gain (enable 0..1); returned SEPARATELY — callers apply it once.
 *
 * Single-threaded: every onXxx() is called from the owning service's sensor HandlerThread.
 */
class VehicleFilter {

    /** Cleaned vehicle channels (UNSCALED by enable — caller applies enable once). */
    class Out(
        @JvmField val lon: Float, @JvmField val lat: Float, @JvmField val enable: Float,
        @JvmField val yawDegPerSec: Float, @JvmField val gate: Float, @JvmField val inVehicle: Boolean
    )

    // ---- tunables (tune in-car) ----
    private val A_LP = 0.0773f      // LPF: fc 0.8 Hz @62 Hz (anti-tremor / road buzz)
    private val A_HP = 0.0030f      // HPF: fc ~0.03 Hz (tau~5 s) — was 2 s; gradual bus accel/brake was auto-cancelled before it could drive the dots
    private val W_LO = 0.30f        // gyro gate full-trust below 0.30 rad/s (~17 deg/s) — was 0.15; bus/road + handling kept the accel channel permanently gated off
    private val W_HI = 1.10f        // gyro gate full-cut above 1.10 rad/s (~63 deg/s) — was 0.50; only a real hand twist should fully cut now
    private val YAW_K = 6.0f        // lateral cue per rad/s of yaw
    private val JERK = 4.0f         // jerk cap m/s^3
    private val DEADBAND = 0.12f    // soft deadband m/s^2 — was 0.18; gentle bus accel was below it
    private val SPD_ON = 5.0f       // GPS speed (m/s) to latch in-vehicle (hysteresis)
    private val SPD_OFF = 2.0f
    private val GRAV_A = 0.024f     // self-split gravity EMA (tau~0.7 s)

    // ---- state ----
    private val grav = FloatArray(3); private var gravInit = false
    private var lpF = 0f; private var lpR = 0f          // low-passed forward / right (vehicle or ENU axes)
    private var baseF = 0f; private var baseR = 0f      // slow baselines (HPF)
    private var hdgE = 0f; private var hdgN = 1f        // inertial forward-axis unit estimate (ENU); default North
    private var yawLp = 0f                 // rad/s, low-passed
    private var gmag = 0f                  // |gyro| rad/s
    private var prevLon = 0f; private var prevLat = 0f
    private var moveOn = false; private var moveFrames = 0
    private var gainEnv = 0f
    private var lastNs = 0L
    @Volatile private var gpsSpeed = -1f       // m/s, -1 = unknown (written from GPS callback thread)
    @Volatile private var gpsBearingRad = 0f
    @Volatile private var haveBearing = false

    /** Gyro sample (rad/s, device frame). z ~ vehicle vertical -> yaw. */
    fun onGyro(gx: Float, gy: Float, gz: Float) {
        val m = sqrt(gx * gx + gy * gy + gz * gz)
        gmag += (m - gmag) * 0.25f         // smooth: brief handling/road spikes must not fully gate accel
        yawLp += (gz - yawLp) * 0.12f      // ~0.8 Hz LPF on yaw
    }

    /** Latest GPS fix: speed m/s (or -1 if no fix) + bearing (deg from North, cw) if valid. */
    fun onGps(mps: Float, bearingDeg: Float, hasBearing: Boolean) {
        gpsSpeed = mps
        haveBearing = hasBearing
        if (hasBearing) gpsBearingRad = Math.toRadians(bearingDeg.toDouble()).toFloat()
    }

    /**
     * Feed one acceleration sample.
     * @param a              sensor values (m/s^2). If [gravityRemoved], already linear; else raw accel.
     * @param gravityRemoved true for TYPE_LINEAR_ACCELERATION, false for raw TYPE_ACCELEROMETER.
     * @param R              3x3 row-major device->world (ENU) rotation matrix, or null if none.
     * @param tsNanos        event.timestamp (for dt).
     */
    fun onAccel(a: FloatArray, gravityRemoved: Boolean, R: FloatArray?, tsNanos: Long): Out {
        val dt = if (lastNs == 0L) 1f / 62f
                 else ((tsNanos - lastNs) / 1e9f).coerceIn(1f / 120f, 1f / 20f)
        lastNs = tsNanos

        // (A) device-frame linear accel
        val lx: Float; val ly: Float; val lz: Float
        if (gravityRemoved) { lx = a[0]; ly = a[1]; lz = a[2] }
        else {
            if (!gravInit) { grav[0] = a[0]; grav[1] = a[1]; grav[2] = a[2]; gravInit = true }
            for (i in 0..2) grav[i] += (a[i] - grav[i]) * GRAV_A
            lx = a[0] - grav[0]; ly = a[1] - grav[1]; lz = a[2] - grav[2]
        }

        // (B) world-frame horizontal accel (East, North). Row-major R maps device->world.
        val hE: Float; val hN: Float
        if (R != null) {
            hE = R[0] * lx + R[1] * ly + R[2] * lz
            hN = R[3] * lx + R[4] * ly + R[5] * lz
        } else {
            // No orientation matrix. We need a gravity estimate to project tilt out; if we don't
            // have one (e.g. fused linear-accel present but no rotation sensor), the sample is
            // unusable for tilt removal — emit no drive rather than leaking device-frame tilt.
            if (!gravInit) return Out(0f, 0f, gainEnv, yawLp * 57.29578f, 0f, moveOn)
            val gx = grav[0]; val gy = grav[1]; val gz = grav[2]
            val gn = sqrt(gx * gx + gy * gy + gz * gz).coerceAtLeast(1e-3f)
            val ux = gx / gn; val uy = gy / gn; val uz = gz / gn
            val dot = lx * ux + ly * uy + lz * uz
            hE = lx - dot * ux
            hN = ly - dot * uy
        }

        // (B2) Resolve a VEHICLE forward axis. Prefer GPS bearing when moving; otherwise estimate
        // forward INERTIALLY from the direction of straight-line accel/brake, so longitudinal is
        // never zeroed by an unknown compass heading (the old `forward = North` default silently
        // killed accel/brake for any E/W heading — the dominant cause of "turns work, accel dead").
        val fAxis: Float; val rAxis: Float
        if (haveBearing && gpsSpeed >= SPD_OFF) {
            val b = gpsBearingRad; val cb = cos(b); val sb = sin(b)
            hdgE = sb; hdgN = cb                // keep the inertial estimate aligned while GPS is good
            fAxis = hN * cb + hE * sb          // forward (along heading)
            rAxis = hE * cb - hN * sb          // right (90 deg cw of heading)
        } else {
            // Learn the forward axis from straight-line horizontal accel. Exclude turns (gmag) so
            // cornering's lateral accel can't pull the estimate sideways. Sign-stabilised onto the
            // current axis: accelerate and brake share the same travel line.
            val hm = sqrt(hE * hE + hN * hN)
            if (hm > 0.20f && gmag < W_LO) {
                var dE = hE / hm; var dN = hN / hm
                if (dE * hdgE + dN * hdgN < 0f) { dE = -dE; dN = -dN }   // fold onto current axis
                hdgE += (dE - hdgE) * 0.02f                              // ~multi-second EMA
                hdgN += (dN - hdgN) * 0.02f
                val nrm = sqrt(hdgE * hdgE + hdgN * hdgN).coerceAtLeast(1e-3f)
                hdgE /= nrm; hdgN /= nrm
            }
            fAxis = hE * hdgE + hN * hdgN       // signed projection onto the estimated forward axis
            rAxis = hE * hdgN - hN * hdgE       // perpendicular = lateral
        }

        // (C) band-pass: LPF then subtract slow baseline (HPF) -> zero-mean vehicle-band accel
        lpF += (fAxis - lpF) * A_LP; lpR += (rAxis - lpR) * A_LP
        baseF += (lpF - baseF) * A_HP; baseR += (lpR - baseR) * A_HP
        var bF = lpF - baseF; var bR = lpR - baseR

        // (D) soft gyro gate on the linear-accel channels (turns are handled by yaw, see E)
        val gate = (1f - (gmag - W_LO) / (W_HI - W_LO)).coerceIn(0f, 1f)
        bF *= gate; bR *= gate

        // (E) channels: forward = longitudinal; right = lateral; blend yaw into lateral.
        var lon = bF
        var lat = bR + YAW_K * yawLp

        // (F) jerk slew-limit + soft deadband (on the 2D drive vector)
        val jmax = JERK * dt
        lon = prevLon + (lon - prevLon).coerceIn(-jmax, jmax)
        lat = prevLat + (lat - prevLat).coerceIn(-jmax, jmax)
        prevLon = lon; prevLat = lat
        val m = sqrt(lon * lon + lat * lat)
        if (m < DEADBAND) { lon = 0f; lat = 0f }
        else { val k = (m - DEADBAND) / m; lon *= k; lat *= k }

        // (G) GPS in-vehicle gate with hysteresis; spd<0 (no GPS) -> always enabled (inertial-only)
        val spd = gpsSpeed
        val target: Float
        if (spd < 0f) { moveOn = true; target = 1f }
        else {
            if (spd >= SPD_ON) { if (++moveFrames > 120) moveOn = true }    // ~2 s sustained
            else if (spd < SPD_OFF) { moveOn = false; moveFrames = 0 }
            target = if (moveOn) 1f else 0f
        }
        gainEnv += (target - gainEnv) * (if (target > gainEnv) 0.066f else 0.024f)  // attack/release

        // UNSCALED lon/lat; caller multiplies by enable exactly once (avoids enable^2 double-gate).
        return Out(lon, lat, gainEnv, yawLp * 57.29578f, gate, moveOn)
    }

    companion object {
        /** Build a row-major device->world matrix from a rotation-vector event. */
        fun rotationFrom(values: FloatArray, out: FloatArray) =
            SensorManager.getRotationMatrixFromVector(out, values)
    }
}
