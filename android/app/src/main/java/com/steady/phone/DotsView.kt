package com.steady.phone

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.util.AttributeSet
import android.view.Choreographer
import android.view.View
import kotlin.math.PI
import kotlin.math.sin

/**
 * On-phone cue dots for Phone mode: drifting teal dots down both screen edges, driven by THIS
 * phone's accelerometer (fed via [feed]). Mirrors the PC overlay / browser-page engine:
 *   high-pass the accel (strip gravity) -> vel = vel*decay - a*gain -> off += vel,
 * then wrap a column of dots vertically with the fore/aft drift and lean it with lateral accel.
 * Self-contained: a Choreographer loop integrates + redraws every vsync, decoupled from the
 * (uneven) sensor cadence. No laptop / network involved.
 */
class DotsView(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {

    @Volatile private var rawAx = 0f
    @Volatile private var rawAy = 0f

    private var lpX = 0f
    private var lpY = 0f
    private var haveLp = false
    private var velX = 0f
    private var velY = 0f
    private var offX = 0f
    private var offY = 0f

    private val dots = 28
    private val d = resources.displayMetrics.density
    private val dot = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.argb(217, 0x6F, 0xD8, 0xC6) }
    private val hint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#6A7486")
        textSize = android.util.TypedValue.applyDimension(
            android.util.TypedValue.COMPLEX_UNIT_SP, 13f, resources.displayMetrics)
        textAlign = Paint.Align.CENTER
    }
    private val bg = Color.rgb(0x10, 0x15, 0x1E)

    private var running = false
    private val choreographer = Choreographer.getInstance()
    private val frame = object : Choreographer.FrameCallback {
        override fun doFrame(frameTimeNanos: Long) {
            step()
            invalidate()
            if (running) choreographer.postFrameCallback(this)
        }
    }

    /** Latest accelerometer sample (m/s^2, incl. gravity). ax = lateral, ay = up the screen. */
    fun feed(ax: Float, ay: Float) { rawAx = ax; rawAy = ay }

    fun start() { if (!running) { running = true; choreographer.postFrameCallback(frame) } }
    fun stop() { running = false; choreographer.removeFrameCallback(frame) }

    private fun step() {
        val ax = rawAx
        val ay = rawAy
        if (!haveLp) { lpX = ax; lpY = ay; haveLp = true }
        lpX += (ax - lpX) * 0.04f          // slow low-pass ~= gravity bias
        lpY += (ay - lpY) * 0.04f
        val hx = ax - lpX                   // high-pass: motion only
        val hy = ay - lpY
        velX = velX * 0.94f - hx * 0.10f    // mirror the PC pipeline feel
        velY = velY * 0.94f - hy * 0.10f
        val vmax = 22f
        velX = velX.coerceIn(-vmax, vmax)
        velY = velY.coerceIn(-vmax, vmax)
        offX += velX
        offY += velY
    }

    override fun onDraw(c: Canvas) {
        c.drawColor(bg)
        val w = width.toFloat()
        val h = height.toFloat()
        if (w <= 0f || h <= 0f) return
        val span = h + 120f * d
        val spacing = span / dots
        var driftY = (offY * 0.6f * d) % spacing
        if (driftY < 0) driftY += spacing
        val driftX = (offX * 0.5f * d).coerceIn(-22f * d, 22f * d)
        val lx = 18f * d + driftX
        val rx = w - 18f * d + driftX
        for (i in 0 until dots) {
            val y = i * spacing + driftY - 60f * d
            val t = i.toFloat() / dots
            val r = (3.2f + 2.6f * sin(t * PI).toFloat()) * d
            c.drawCircle(lx, y, r, dot)
            c.drawCircle(rx, y, r, dot)
        }
        c.drawText("Tap to exit · Phone mode", w / 2f, 40f * d, hint)
    }
}
