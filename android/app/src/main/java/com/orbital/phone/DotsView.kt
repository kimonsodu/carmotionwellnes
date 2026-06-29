package com.orbital.phone

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.util.AttributeSet
import android.view.Choreographer
import android.view.View
import kotlin.math.abs
import kotlin.math.floor
import kotlin.math.min
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Overlay-grade cue renderer: transparent + click-through (handles no touch), driven by THIS phone's
 * accelerometer via [feed] (already screen-relative + cleaned by VehicleFilter). One shared physics
 * loop (step) integrates the felt accel into a flow (offX/offY, velX/velY); onDraw() dispatches that
 * SAME drive into one of several swappable cue STYLES so every style stays a vestibular-matched
 * peripheral optic-flow cue. Parametrized live for the Phone-mode settings.
 *
 * Also doubles as the in-app live PREVIEW: [setDemoMode] makes step() synthesize a gentle motion so
 * the settings screen can show the chosen style animating at a desk (real motion is car-only).
 */
class DotsView(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {

    // --- cleaned vehicle-motion drive (computed by the shared VehicleFilter in OverlayService) ---
    @Volatile private var inScreenX = 0f // screen-relative accel m/s^2: + = felt accel toward screen-right
    @Volatile private var inScreenY = 0f // screen-relative accel m/s^2: + = felt accel toward screen-forward (accelerate)
    @Volatile private var inGrade = 0f   // road-grade (hill) cue m/s^2, baselined; drives the vertical axis
    @Volatile private var inEnable = 1f  // 0..1 in-vehicle/GPS enable (also cue fade target)

    // --- live params (defaults mirror the original behaviour) ---
    private var strength = SettingsStore.DEF_STRENGTH
    private var lonGain = SettingsStore.DEF_LON_GAIN   // fore/aft (accel/brake) sensitivity; magnitude only
    private var gradeGain = SettingsStore.DEF_GRADE_GAIN // hill/grade sensitivity; SIGNED (direction)
    private var invertX = 1f             // -1 = flip horizontal (turn) direction
    private var invertY = 1f             // -1 = flip vertical (accel/brake) direction
    private var swapAxes = false         // swap which axis drives vertical vs horizontal
    private var sizeScale = SettingsStore.DEF_DOT_SIZE
    private var colorMode = SettingsStore.DEF_DOT_COLOR
    private var autoHide = SettingsStore.DEF_AUTO_HIDE
    private var cueStyle = SettingsStore.DEF_CUE_STYLE
    private var opacity = SettingsStore.DEF_OPACITY
    private var density = SettingsStore.DEF_DENSITY
    private var accentColor = SettingsStore.DEF_ACCENT_COLOR
    private var cueModel = SettingsStore.DEF_CUE_MODEL
    private var placement = SettingsStore.DEF_PLACEMENT
    private var decay = SettingsStore.DEF_DECAY
    private var hideSensitivity = SettingsStore.DEF_HIDE_SENS

    // --- flow integrator state ---
    private var velX = 0f
    private var velY = 0f
    private var offX = 0f
    private var offY = 0f
    private var accelEnv = 0f            // smoothed 0..1 accel envelope (Acceleration-pulse model)

    // --- demo (live preview) ---
    private var demoMode = false
    private var demoT = 0

    // --- auto-hide gate (accel-only, mirrors the PC jitterEma fallback) ---
    private var magSlow = 0f
    private var jitterEma = 0f
    private var activityEma = 0.3f
    private var stillFrames = 0
    private var dwellFrames = 999       // frames since the last visibility toggle (min-dwell lockout); init high so first reveal isn't delayed
    private var autoStill = false
    private var dotsAlpha = 1f          // eased 0..1, multiplies cue opacity

    private val d = resources.displayMetrics.density
    private val light = Color.argb(217, 0xEC, 0xE7, 0xD7)   // warm off-white (matches PC light dot)
    private val dark = Color.argb(217, 0x12, 0x16, 0x1E)    // near-bg dark (matches PC dark dot)
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)

    // --- scattered field (shared by Dots / Streaks; the strips also anchor Chevrons/Rails) ---
    // side: 0 left strip, 1 right strip, 2 top band, 3 bottom band (bands only when placement=Full).
    private class Dot(
        val side: Int, val lx: Float, val y: Float, val r: Float,
        val alpha: Float, val depth: Float, val pick: Int
    )
    private val field = ArrayList<Dot>()
    private var bandW = 0f
    private var bandH = 0f
    private var builtW = 0
    private var builtH = 0
    private val rnd = java.util.Random()

    // --- per-frame scratch (set once in onDraw, read by the render* funcs) ---
    private var frW = 0f; private var frH = 0f
    private var frA = 0f; private var frOp = 1f
    private var frSX = 0f; private var frSY = 0f; private var frKFlow = 0f; private var frIntens = 1f

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
     * Cleaned, SCREEN-relative, band-passed + gyro-gated felt accel from VehicleFilter (m/s^2).
     * screenX = + toward screen-right, screenY = + toward screen-forward (accelerate), enable = 0..1.
     * Screen-relative means seating orientation (forward/back/sideways) is already handled — no sign.
     */
    fun feed(screenX: Float, screenY: Float, grade: Float, enable: Float) { inScreenX = screenX; inScreenY = screenY; inGrade = grade; inEnable = enable }

    /** Preview only: synthesize a gentle motion so the settings screen animates at a desk. Never set
     *  by OverlayService, so the real overlay can't run on synthetic values. */
    fun setDemoMode(on: Boolean) { demoMode = on }

    fun applyParams(p: SettingsStore.Params) {
        val rebuild = density != p.density || placement != p.placement   // geometry (field counts/bands) changed
        strength = p.strength
        lonGain = p.lonGain
        gradeGain = p.gradeGain
        invertX = if (p.flipH) -1f else 1f
        invertY = if (p.flipV) -1f else 1f
        swapAxes = p.swap
        sizeScale = p.dotSize
        colorMode = p.colorMode
        cueStyle = p.cueStyle
        opacity = p.opacity
        density = p.density
        accentColor = p.accentColor
        cueModel = p.cueModel
        placement = p.placement
        decay = p.decay
        hideSensitivity = p.hideSensitivity
        if (autoHide != p.autoHide) {          // turning auto-hide off must not leave dots stuck hidden
            autoHide = p.autoHide
            if (!autoHide) { autoStill = false; stillFrames = 0; dwellFrames = 999 }
        }
        if (rebuild) { builtW = 0; builtH = 0 }   // force buildField on the next onDraw (counts are baked in)
    }

    fun start() { if (!running) { running = true; choreographer.postFrameCallback(frame) } }
    fun stop() { running = false; choreographer.removeFrameCallback(frame) }

    private fun step() {
        val sx: Float; val sy: Float; val enable: Float
        if (demoMode) {
            // Gentle ~8-10s loop: lateral turn sweep (offX) out of phase with accel/brake (offY).
            demoT++
            val t = demoT / 60f
            sx = 1.2f * sin(t * 0.9f)
            sy = 1.0f * sin(t * 0.62f + 1.1f)
            enable = 1f
        } else {
            sx = inScreenX; sy = inScreenY; enable = inEnable.coerceIn(0f, 1f)
        }
        val grade = if (demoMode) 0f else inGrade   // hill cue (0 in the desk preview)

        // Inputs are ALREADY screen-relative, band-passed, gyro-gated, jerk-limited, deadbanded by
        // VehicleFilter — DotsView only integrates to drift. Direction is AUTOMATIC for any seat:
        // accelerate -> dots DOWN; brake -> UP; vehicle turn-right -> dots LEFT (felt force opposite).
        val gain = 0.12f * strength
        // Manual direction overrides (default no-op): Swap exchanges the lateral/forward axes; Flip
        // ↔/↕ reverse the horizontal/vertical sense. Grade keeps its own signed gain on the vertical.
        var sxx = sx; var syy = sy
        if (swapAxes) { val t = sxx; sxx = syy; syy = t }
        val driveX = sxx * enable * invertX
        val driveY = syy * enable
        val driveG = grade * enable
        velX = velX * decay - driveX * gain
        // lonGain scales ONLY the screen-vertical axis to preserve the original fore/aft-vs-lateral feel.
        // The hill/grade cue rides the SAME vertical axis with its own SIGNED gain (gradeGain), so a
        // slope drifts the dots even at steady speed; sign flips uphill/downhill direction.
        velY = velY * decay + driveY * lonGain * gain * invertY + driveG * gradeGain * gain
        val vmax = 22f
        velX = velX.coerceIn(-vmax, vmax)
        velY = velY.coerceIn(-vmax, vmax)

        // --- auto-hide energy gate (driven by the cleaned magnitude; ~0 unless real vehicle motion) ---
        // include the grade drive so a sustained slope at steady speed keeps the cue awake.
        val mag = sqrt(driveX * driveX + driveY * driveY + driveG * driveG)
        // accel envelope for the Acceleration-pulse model — fed from the already band-passed/deadbanded
        // mag (never raw), and smoothed, so brightness can't strobe (photosensitivity guard).
        accelEnv += ((mag / 3.0f).coerceIn(0f, 1f) - accelEnv) * 0.20f
        val hf = abs(mag - magSlow)
        magSlow += (mag - magSlow) * 0.10f
        jitterEma += (hf - jitterEma) * 0.05f
        val energy = jitterEma * 1.5f + mag
        activityEma += (energy - activityEma) * 0.06f
        if (autoHide && !demoMode) {
            // Wide hysteresis: separate show/hide knees so one bump can't cross both. hideSensitivity
            // (>1) biases toward hiding sooner.
            val showK = 0.36f * hideSensitivity
            val hideK = 0.10f * hideSensitivity
            var wantStill = autoStill
            if (activityEma > showK) wantStill = false
            else if (activityEma < hideK) wantStill = true
            if (dwellFrames < 75) dwellFrames++
            if (wantStill != autoStill && dwellFrames >= 75) {    // ~1.25s lockout after a toggle (60fps)
                if (++stillFrames >= 18) { autoStill = wantStill; stillFrames = 0; dwellFrames = 0 } // ~0.3s confirm
            } else stillFrames = 0
        }
        // Fade when auto-hidden OR when GPS says we're parked (enable~0). Demo never fades.
        val targetAlpha = if (demoMode) 1f else if ((autoHide && autoStill) || enable < 0.05f) 0f else 1f
        val alphaEase = if (targetAlpha > dotsAlpha) 0.045f else 0.025f
        dotsAlpha += (targetAlpha - dotsAlpha) * alphaEase
        if (targetAlpha == 0f) { velX *= 0.85f; velY *= 0.85f }   // settle while hidden

        offX += velX
        offY += velY
    }

    private fun colorOf(pick: Int): Int = when (colorMode) {
        0 -> light
        2 -> dark
        3 -> if (accentColor != 0) accentColor else light   // Custom accent (falls back to light if unset)
        else -> if (pick == 0) light else dark              // Mixed: each dot keeps its own pick (stable on wrap)
    }

    private fun wrap(v: Float, m: Float): Float { var x = v % m; if (x < 0f) x += m; return x }
    private fun cornerFade(y: Float, h: Float): Float = 0.35f + 0.65f * min(1f, min(y, h - y) / (h * 0.14f))
    private fun smoothstep(e0: Float, e1: Float, x: Float): Float { val t = ((x - e0) / (e1 - e0)).coerceIn(0f, 1f); return t * t * (3f - 2f * t) }
    private fun centreFade(y: Float, h: Float): Float = smoothstep(0f, 1f, abs(y - h * 0.5f) / (h * 0.5f))

    private fun buildField(w: Float, h: Float) {
        field.clear()
        bandW = (w * 0.22f).coerceIn(56f * d, 130f * d)         // width of each side strip
        bandH = (h * 0.16f).coerceIn(56f * d, 160f * d)         // height of each top/bottom band (Full placement)
        val colArea = bandW * h * 2f
        val nNear = (colArea / (7000f * d * d) * density).toInt().coerceIn(8, 200)
        val nFar = (colArea / (5200f * d * d) * density).toInt().coerceIn(8, 260)
        fun makeStrip(n: Int, rMin: Float, rJit: Float, alpha: Float, depth: Float) {
            for (i in 0 until n) field.add(Dot(
                i and 1, rnd.nextFloat() * bandW, rnd.nextFloat() * h,
                (rMin + rnd.nextFloat() * rJit) * d, alpha, depth, if (rnd.nextBoolean()) 0 else 1))
        }
        makeStrip(nFar, 0.9f, 0.8f, 0.18f, 0.55f)              // faint far layer
        makeStrip(nNear, 1.9f, 1.3f, 0.9f, 1.0f)              // bold near layer
        if (placement == 1) {                                  // Full peripheral frame: add top+bottom bands
            val bandArea = w * bandH * 2f
            val bNear = (bandArea / (7000f * d * d) * density).toInt().coerceIn(6, 200)
            val bFar = (bandArea / (5200f * d * d) * density).toInt().coerceIn(6, 260)
            fun makeBand(n: Int, rMin: Float, rJit: Float, alpha: Float, depth: Float) {
                for (i in 0 until n) field.add(Dot(
                    if (i and 1 == 0) 2 else 3, rnd.nextFloat() * w, rnd.nextFloat() * bandH,
                    (rMin + rnd.nextFloat() * rJit) * d, alpha, depth, if (rnd.nextBoolean()) 0 else 1))
            }
            makeBand(bFar, 0.9f, 0.8f, 0.18f, 0.55f)
            makeBand(bNear, 1.9f, 1.3f, 0.9f, 1.0f)
        }
    }

    /** Per-dot head position for the current flow (handles side strips + top/bottom bands). */
    private var phx = 0f; private var phy = 0f
    private fun dotHead(dot: Dot, sX: Float, sY: Float, w: Float, h: Float) {
        when (dot.side) {
            0 -> { phx = wrap(dot.lx + sX * dot.depth, bandW); phy = wrap(dot.y + sY * dot.depth, h) }
            1 -> { phx = w - bandW + wrap(dot.lx + sX * dot.depth, bandW); phy = wrap(dot.y + sY * dot.depth, h) }
            2 -> { phx = wrap(dot.lx + sX * dot.depth, w); phy = wrap(dot.y + sY * dot.depth, bandH) }
            else -> { phx = wrap(dot.lx + sX * dot.depth, w); phy = h - bandH + wrap(dot.y + sY * dot.depth, bandH) }
        }
    }

    override fun onDraw(c: Canvas) {
        // NO drawColor — transparent overlay.
        val w = width.toFloat()
        val h = height.toFloat()
        if (w <= 0f || h <= 0f) return
        val a = dotsAlpha.coerceIn(0f, 1f)
        if (a <= 0.01f) return
        if (field.isEmpty() || builtW != width || builtH != height) {
            buildField(w, h); builtW = width; builtH = height
        }
        // shared per-frame setup
        frW = w; frH = h; frA = a; frOp = opacity; frKFlow = 0.6f * d
        frSX = offX * frKFlow; frSY = offY * frKFlow
        frIntens = if (cueModel == 1) accelEnv else 1f
        when (cueStyle) {
            1 -> renderStreaks(c)
            2 -> renderRails(c)
            3 -> renderHorizon(c)
            4 -> renderFlowGrid(c)
            5 -> renderChevrons(c)
            else -> renderDots(c)
        }
    }

    // ---- styles --------------------------------------------------------------------------------

    private fun renderDots(c: Canvas) {
        paint.style = Paint.Style.FILL
        for (dot in field) {
            dotHead(dot, frSX, frSY, frW, frH)
            val fade = cornerFade(phy, frH)
            val col = colorOf(dot.pick)
            paint.color = col
            paint.alpha = (Color.alpha(col) * frA * frOp * dot.alpha * fade * frIntens).toInt().coerceIn(0, 255)
            c.drawCircle(phx, phy, dot.r * sizeScale, paint)
        }
    }

    private fun renderStreaks(c: Canvas) {
        val spd = sqrt(velX * velX + velY * velY)
        if (spd < 1e-3f) { renderDots(c); return }   // steady cruise -> collapses to dots (continuity)
        val ux = velX / spd; val uy = velY / spd
        paint.style = Paint.Style.STROKE
        paint.strokeCap = Paint.Cap.ROUND
        for (dot in field) {
            dotHead(dot, frSX, frSY, frW, frH)
            val len = (spd * frKFlow * dot.depth * 2.2f * (if (cueModel == 1) (0.4f + accelEnv) else 1f)).coerceIn(2f * d, 26f * d)
            val tx = phx - ux * len; val ty = phy - uy * len
            val fade = cornerFade(phy, frH)
            val col = colorOf(dot.pick)
            paint.color = col
            paint.strokeWidth = (dot.r * sizeScale * 0.9f).coerceAtLeast(1f)
            paint.alpha = (Color.alpha(col) * frA * frOp * dot.alpha * fade * frIntens).toInt().coerceIn(0, 255)
            c.drawLine(phx, phy, tx, ty, paint)
        }
    }

    private fun renderRails(c: Canvas) {
        val w = frW; val h = frH
        val nBars = (6 * density).toInt().coerceIn(3, 12)
        val barH = h / nBars
        val phase = wrap(frSY * 0.02f, barH)
        val col = colorOf(0)
        val k = 1f / 8f
        val leftLit = ((-velX) * k).coerceIn(0f, 1f)
        val rightLit = (velX * k).coerceIn(0f, 1f)
        paint.style = Paint.Style.FILL
        for (side in 0..1) {
            val x0 = if (side == 0) w * 0.012f else w - bandW + w * 0.012f
            val x1 = if (side == 0) bandW - w * 0.012f else w - w * 0.012f
            val sideI = if (side == 0) leftLit else rightLit
            for (i in -1..nBars) {
                val cyTop = i * barH + phase
                val segH = barH * 0.6f
                val midY = cyTop + segH * 0.5f
                val col2 = col
                paint.color = col2
                paint.alpha = (Color.alpha(col2) * frA * frOp * (0.30f + 0.70f * sideI) * cornerFade(midY, h) * frIntens).toInt().coerceIn(0, 255)
                c.drawRoundRect(x0, cyTop, x1, cyTop + segH, 4f * d, 4f * d, paint)
            }
        }
    }

    private fun renderHorizon(c: Canvas) {
        val w = frW; val h = frH
        val pitch = (velY * 4.0f).coerceIn(-h * 0.18f, h * 0.18f)
        val rollDeg = (velX * 1.6f).coerceIn(-16f, 16f)
        val cy = h * 0.5f + pitch
        val col = colorOf(0)
        c.save()
        c.rotate(rollDeg, w * 0.5f, cy)
        paint.style = Paint.Style.STROKE
        paint.strokeCap = Paint.Cap.ROUND
        // main rails (centre kept open for reading)
        paint.strokeWidth = (2.4f * d) * sizeScale
        paint.color = col
        paint.alpha = (Color.alpha(col) * frA * frOp * frIntens).toInt().coerceIn(0, 255)
        val inset = w * 0.06f; val gapHalf = w * 0.15f
        c.drawLine(inset, cy, w * 0.5f - gapHalf, cy, paint)
        c.drawLine(w * 0.5f + gapHalf, cy, w - inset, cy, paint)
        // pitch ladder (scrolls under accel/brake so it's never static)
        val rungs = (3 * density).toInt().coerceIn(2, 6)
        val sp = 26f * d
        val phase = wrap(frSY * 0.10f, sp)
        paint.strokeWidth = (1.4f * d) * sizeScale
        var n = -rungs
        while (n <= rungs) {
            val yy = cy + n * sp + phase - sp
            val ladderA = 1f - abs(yy - cy) / ((rungs + 1) * sp)
            if (ladderA > 0f) {
                val half = w * 0.035f * (1f + abs(n) * 0.12f)
                paint.alpha = (Color.alpha(col) * frA * frOp * 0.5f * ladderA * frIntens).toInt().coerceIn(0, 255)
                c.drawLine(w * 0.5f - half, yy, w * 0.5f + half, yy, paint)
            }
            n++
        }
        c.restore()
    }

    private fun renderFlowGrid(c: Canvas) {
        val w = frW; val h = frH
        paint.style = Paint.Style.STROKE
        paint.strokeWidth = (0.8f * d) * sizeScale
        val col = colorOf(0)
        val horizonY = h * 0.46f
        val vanishX = w * 0.5f + frSX * 0.30f
        val scroll = frSY * 0.012f
        val rows = (9 * density).toInt().coerceIn(4, 16)
        val cols = (7 * density).toInt().coerceIn(3, 14)
        // receding floor rows
        for (i in 0 until rows) {
            var z = ((i + scroll) % rows) / rows
            if (z <= 0f) z += 1f
            val y = horizonY + (h - horizonY) * z * z
            val bank = (vanishX - w * 0.5f) * (1f - z)
            val rowA = z * centreFade(y, h)
            paint.color = col
            paint.alpha = (Color.alpha(col) * frA * frOp * rowA * 0.5f * frIntens).toInt().coerceIn(0, 255)
            c.drawLine(bank, y, w + bank, y, paint)
        }
        // perspective verticals fanning from the vanishing point
        var kk = -cols
        while (kk <= cols) {
            val xB = w * 0.5f + kk * (w * 0.5f / cols) + (vanishX - w * 0.5f)
            paint.color = col
            paint.alpha = (Color.alpha(col) * frA * frOp * 0.35f * frIntens).toInt().coerceIn(0, 255)
            c.drawLine(vanishX, horizonY, xB, h, paint)
            kk++
        }
    }

    private fun renderChevrons(c: Canvas) {
        val w = frW; val h = frH
        val spd = sqrt(velX * velX + velY * velY)
        if (spd < 1e-3f) return
        val mag = (spd / 8f).coerceIn(0f, 1f)
        val magVis = if (mag > 0.25f) mag else 0.25f      // comfort floor while a maneuver IS happening
        val ux = velX / spd; val uy = velY / spd
        val s = (8f * d) * sizeScale
        val k = (5 * density).toInt().coerceIn(3, 9)
        val col = colorOf(0)
        paint.style = Paint.Style.STROKE
        paint.strokeCap = Paint.Cap.ROUND
        paint.strokeWidth = (2.4f * d) * sizeScale
        for (side in 0..1) {
            val cx = if (side == 0) bandW * 0.5f else w - bandW * 0.5f
            for (i in 0 until k) {
                var f = i.toFloat() / k + frSY * 0.004f
                f -= floor(f)
                val cyy = f * h
                val edge = min(1f, min(cyy, h - cyy) / (h * 0.12f))
                paint.color = col
                paint.alpha = (Color.alpha(col) * frA * frOp * magVis * edge * frIntens).toInt().coerceIn(0, 255)
                val tx = cx + ux * s; val ty = cyy + uy * s          // arrow tip points where the force pushes
                val px = -uy; val py = ux                            // perpendicular for the two wings
                val bx = cx - ux * s * 0.25f; val by = cyy - uy * s * 0.25f
                c.drawLine(bx + px * s, by + py * s, tx, ty, paint)
                c.drawLine(bx - px * s, by - py * s, tx, ty, paint)
            }
        }
    }
}
