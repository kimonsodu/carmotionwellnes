package com.steady.phone

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.util.AttributeSet
import android.view.Choreographer
import android.view.View
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.floor
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Overlay-grade drifting cue dots: transparent and click-through-friendly (handles no touch),
 * driven by THIS phone's accelerometer via [feed]. Parametrized for the Phone-mode settings —
 * strength (drift), size (radius), colour (Light/Mixed/Dark), auto-hide (fade when still). Mirrors
 * the Windows overlay engine: high-pass the accel, vel = vel*0.94 - h*gain, off += vel.
 */
class DotsView(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {

    // --- cleaned vehicle-motion drive (computed by the shared VehicleFilter in OverlayService) ---
    @Volatile private var inLon = 0f     // longitudinal accel m/s^2: +accelerate / -brake
    @Volatile private var inLat = 0f     // lateral cue m/s^2: +right (vehicle turning left)
    @Volatile private var inEnable = 1f  // 0..1 in-vehicle/GPS enable (also dot fade target)

    // --- live params (defaults mirror the Windows app) ---
    private var strength = SettingsStore.DEF_STRENGTH
    private var sizeScale = SettingsStore.DEF_DOT_SIZE
    private var colorMode = SettingsStore.DEF_DOT_COLOR
    private var autoHide = SettingsStore.DEF_AUTO_HIDE

    // --- flow integrator state ---
    private var velX = 0f
    private var velY = 0f
    private var offX = 0f
    private var offY = 0f

    // --- auto-hide gate (accel-only, mirrors the PC jitterEma fallback) ---
    private var magSlow = 0f
    private var jitterEma = 0f
    private var activityEma = 0.3f
    private var stillFrames = 0
    private var autoStill = false
    private var dotsAlpha = 1f          // eased 0..1, multiplies dot opacity

    private val dots = 28
    private val d = resources.displayMetrics.density
    private val light = Color.argb(217, 0xEC, 0xE7, 0xD7)   // warm off-white (matches PC light dot)
    private val dark = Color.argb(217, 0x12, 0x16, 0x1E)    // near-bg dark (matches PC dark dot)
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)

    private var running = false
    private val choreographer = Choreographer.getInstance()
    private val frame = object : Choreographer.FrameCallback {
        override fun doFrame(frameTimeNanos: Long) {
            step(); invalidate()
            if (running) choreographer.postFrameCallback(this)
        }
    }

    init { setLayerType(LAYER_TYPE_HARDWARE, null) }

    /**
     * Cleaned, world-frame, band-passed + gyro-gated vehicle accel from VehicleFilter (m/s^2).
     * lon = +accelerate/-brake, lat = +right cue, enable = 0..1 in-vehicle confidence.
     */
    fun feed(lon: Float, lat: Float, enable: Float) { inLon = lon; inLat = lat; inEnable = enable }

    fun applyParams(p: SettingsStore.Params) {
        strength = p.strength
        sizeScale = p.dotSize
        colorMode = p.colorMode
        if (autoHide != p.autoHide) {          // turning auto-hide off must not leave dots stuck hidden
            autoHide = p.autoHide
            if (!autoHide) { autoStill = false; stillFrames = 0 }
        }
    }

    fun start() { if (!running) { running = true; choreographer.postFrameCallback(frame) } }
    fun stop() { running = false; choreographer.removeFrameCallback(frame) }

    private fun step() {
        val lon = inLon
        val lat = inLat
        val enable = inEnable.coerceIn(0f, 1f)

        // Inputs are ALREADY world-frame, band-passed, gyro-gated, jerk-limited, deadbanded by
        // VehicleFilter — DotsView no longer estimates tilt; it only integrates to drift. Apple
        // mapping: accelerate (lon>0) -> dots DOWN; brake -> UP; vehicle turn-left -> dots RIGHT.
        val gain = 0.12f * strength         // input is small clean m/s^2 (was tilt lean at 0.06)
        val driveLat = lat * enable
        val driveLon = lon * enable
        velX = velX * 0.92f - driveLat * gain
        velY = velY * 0.92f - driveLon * gain
        val vmax = 22f
        velX = velX.coerceIn(-vmax, vmax)
        velY = velY.coerceIn(-vmax, vmax)

        // --- auto-hide energy gate (driven by the cleaned magnitude; ~0 unless real vehicle motion) ---
        val mag = sqrt(driveLat * driveLat + driveLon * driveLon)
        val hf = abs(mag - magSlow)
        magSlow += (mag - magSlow) * 0.10f
        jitterEma += (hf - jitterEma) * 0.05f
        val energy = jitterEma * 1.5f + mag
        activityEma += (energy - activityEma) * 0.06f
        if (autoHide) {
            if (activityEma > 0.30f) { autoStill = false; stillFrames = 0 }       // thresholds lowered:
            else if (activityEma < 0.12f) { if (++stillFrames > 45) autoStill = true } // clean signal is smaller
            else stillFrames = 0
        }
        // Fade when auto-hidden OR when GPS says we're parked (enable~0).
        val targetAlpha = if ((autoHide && autoStill) || enable < 0.05f) 0f else 1f
        dotsAlpha += (targetAlpha - dotsAlpha) * 0.06f
        if (targetAlpha == 0f) { velX *= 0.85f; velY *= 0.85f }   // settle while hidden

        offX += velX
        offY += velY
    }

    // deterministic per-dot colour for Mixed mode (stable across frames so fades look right)
    private fun dotColorFor(i: Int): Int = when (colorMode) {
        0 -> light
        2 -> dark
        else -> if (i and 1 == 0) light else dark   // Mixed: alternate so some dots always contrast
    }

    override fun onDraw(c: Canvas) {
        // NO drawColor — transparent overlay.
        val w = width.toFloat()
        val h = height.toFloat()
        if (w <= 0f || h <= 0f) return
        val a = dotsAlpha.coerceIn(0f, 1f)
        if (a <= 0.01f) return
        val span = h + 120f * d
        val spacing = span / dots
        // Vertical scroll in dot-units. k = how many dots have wrapped through the top/bottom
        // "portal"; colouring by (i - k) gives each physical dot a STABLE identity, so a dot keeps
        // its colour as it wraps (Mixed mode no longer flips colour at the seam).
        val scrollDots = offY * 0.6f * d / spacing
        val k = floor(scrollDots).toInt()
        val driftY = (scrollDots - k) * spacing            // fractional part * spacing, in [0, spacing)
        val driftX = (offX * 0.5f * d).coerceIn(-22f * d, 22f * d)
        val lx = 18f * d + driftX
        val rx = w - 18f * d + driftX
        for (i in 0 until dots) {
            val y = i * spacing + driftY - 60f * d
            val t = i.toFloat() / dots
            val r = (3.2f + 2.6f * sin(t * PI).toFloat()) * d * sizeScale   // size by screen slot (fade fat-in-middle)
            val col = dotColorFor(i - k)                                    // colour by stable per-dot identity
            paint.color = col
            paint.alpha = (Color.alpha(col) * a).toInt()
            c.drawCircle(lx, y, r, paint)
            c.drawCircle(rx, y, r, paint)
        }
    }
}
