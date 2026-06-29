package com.orbital.phone

import android.content.Context
import android.content.SharedPreferences

/**
 * Phone-mode overlay settings, persisted in SharedPreferences("orbit"). Defaults mirror the
 * Windows app's feel: Strength 1.8x, Size 1.0x. Colour defaults to Mixed (per-element light/dark)
 * so the cue contrasts against any app/wallpaper behind it; Auto-hide defaults ON so the cue fades
 * at stops with no GPS (subway/tunnel) for a hands-off ride.
 *
 * EVERYTHING here is additive + back-compatible: a fresh install with no stored prefs behaves
 * exactly like the original (Dots style, velocity-flow, full opacity, side strips). New cue styles
 * and customization opt in via the new keys; the runtime cue itself stays fully automatic.
 */
object SettingsStore {
    const val PREFS = "orbit"

    // ---- existing keys ----
    const val K_STRENGTH = "ph_strength"     // Float 0.3..6.0  (drift sensitivity multiplier)
    const val K_LON_GAIN = "ph_lonGain"      // Float -4..4 fore/aft (accel/brake) trim; SIGN = direction (accelerate = dots down), |v| = sensitivity, 0 = off. Mirrors Windows; independent of Hill/grade.
    const val K_GRADE_GAIN = "ph_gradeGain"  // Float -4..4 hill/grade sensitivity; SIGN = direction (uphill/downhill), 0 = off. Independent of accel/brake.
    const val K_DOT_SIZE = "ph_dotSize"      // Float 0.4..3.0  (element size scale)
    const val K_DOT_COLOR = "ph_dotColor"    // Int   0/1/2/3 = Light / Mixed / Dark / Custom(accent)
    const val K_AUTO_HIDE = "ph_autoHide"    // Bool  fade the cue when the phone is still

    // ---- new customization keys (all default to original behaviour) ----
    const val K_CUE_STYLE = "ph_cueStyle"        // Int 0..5 = Dots / Streaks / Rails / Horizon / Flow / Chevrons
    const val K_OPACITY = "ph_opacity"           // Float 0.2..1.0 overall alpha multiplier
    const val K_DENSITY = "ph_density"           // Float 0.5..2.0 element-count multiplier
    const val K_ACCENT_COLOR = "ph_accentColor"  // Int packed ARGB; 0 = unset (use the palette)
    const val K_CUE_MODEL = "ph_cueModel"        // Int 0/1 = Velocity-flow / Acceleration-pulse
    const val K_PLACEMENT = "ph_placement"       // Int 0/1 = Side strips / Full peripheral frame
    const val K_DECAY = "ph_decay"               // Float 0.80..0.97 flow damping (smoothness / trail)
    const val K_HIDE_SENS = "ph_hideSensitivity" // Float 0.5..2.0 auto-hide knee scale
    const val K_PRESET = "ph_preset"             // Int 0..3 = Calm / Balanced / Strong / Custom (UI-only)
    const val K_ONBOARDED = "ph_onboarded"       // Bool first-run seen (UI-only)
    // ---- manual direction overrides (the cue is screen-relative/automatic by default; these flip it) ----
    const val K_FLIP_V = "ph_flipV"              // Bool reverse the vertical (accel/brake) cue direction
    const val K_FLIP_GRADE = "ph_flipGrade"      // Bool reverse ONLY the hill/grade cue (independent of accel/brake)
    const val K_FLIP_H = "ph_flipH"              // Bool reverse the horizontal (turn) cue direction
    const val K_SWAP = "ph_swap"                 // Bool swap which axis drives vertical vs horizontal
    const val K_SIM_SCENARIO = "ph_simScenario"  // Int 0..7 = Off / All / Accelerate / Brake / TurnLeft / TurnRight / Uphill / Downhill (test-only synthetic motion)
    const val K_SIM_SEAT = "ph_simSeat"          // Int 0..3 sim seating orientation: 0 Forward / 1 Left side / 2 Right side / 3 Rear — applies to any scenario

    // ---- defaults ----
    const val DEF_STRENGTH = 1.8f
    const val DEF_LON_GAIN = 1.5f            // fore/aft sensitivity magnitude; direction is automatic
    const val DEF_GRADE_GAIN = 1.0f          // hill/grade sensitivity (signed); flip the sign to reverse uphill/downhill
    const val DEF_DOT_SIZE = 1.0f
    const val DEF_DOT_COLOR = 1              // Mixed — contrasts over any app
    const val DEF_AUTO_HIDE = true          // hands-off: fade at stops even with no GPS (subway/tunnel)

    const val DEF_CUE_STYLE = 0             // Dots = the original visual
    const val DEF_OPACITY = 1.0f
    const val DEF_DENSITY = 1.0f
    const val DEF_ACCENT_COLOR = 0          // sentinel: use the Light/Mixed/Dark palette
    const val DEF_CUE_MODEL = 0             // Velocity-flow = the original integrate-to-drift
    const val DEF_PLACEMENT = 0             // Side strips = the original layout
    const val DEF_DECAY = 0.92f             // == the original hardcoded velX/velY damping
    const val DEF_HIDE_SENS = 1.0f          // == the original auto-hide knees
    const val DEF_PRESET = 1                // Balanced == the current defaults (existing users land here)
    const val DEF_ONBOARDED = false
    const val DEF_SIM_SCENARIO = 0          // Off — sim disabled; the overlay uses the real sensors
    const val DEF_FLIP_V = false            // direction is automatic by default; flips are opt-in overrides
    const val DEF_FLIP_GRADE = false
    const val DEF_FLIP_H = false
    const val DEF_SWAP = false
    const val DEF_SIM_SEAT = 0              // sim seating defaults to forward-facing

    fun prefs(c: Context): SharedPreferences = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    fun strength(c: Context) = prefs(c).getFloat(K_STRENGTH, DEF_STRENGTH).coerceIn(0.3f, 6.0f)
    // SIGNED accel/brake trim (mirrors Windows LonGain): sign sets direction, |v| sets sensitivity,
    // centre = off. Separate from the signed Hill/grade trim, so accel and hill flip independently.
    fun lonGain(c: Context) = prefs(c).getFloat(K_LON_GAIN, DEF_LON_GAIN).coerceIn(-4.0f, 4.0f)
    fun gradeGain(c: Context) = prefs(c).getFloat(K_GRADE_GAIN, DEF_GRADE_GAIN).coerceIn(-4.0f, 4.0f)   // signed
    fun dotSize(c: Context) = prefs(c).getFloat(K_DOT_SIZE, DEF_DOT_SIZE).coerceIn(0.4f, 3.0f)
    fun dotColor(c: Context) = prefs(c).getInt(K_DOT_COLOR, DEF_DOT_COLOR).coerceIn(0, 3)
    fun autoHide(c: Context) = prefs(c).getBoolean(K_AUTO_HIDE, DEF_AUTO_HIDE)

    fun cueStyle(c: Context) = prefs(c).getInt(K_CUE_STYLE, DEF_CUE_STYLE).coerceIn(0, 5)
    fun opacity(c: Context) = prefs(c).getFloat(K_OPACITY, DEF_OPACITY).coerceIn(0.2f, 1.0f)
    fun density(c: Context) = prefs(c).getFloat(K_DENSITY, DEF_DENSITY).coerceIn(0.5f, 2.0f)
    fun accentColor(c: Context) = prefs(c).getInt(K_ACCENT_COLOR, DEF_ACCENT_COLOR)   // 0 = unset
    fun cueModel(c: Context) = prefs(c).getInt(K_CUE_MODEL, DEF_CUE_MODEL).coerceIn(0, 1)
    fun placement(c: Context) = prefs(c).getInt(K_PLACEMENT, DEF_PLACEMENT).coerceIn(0, 1)
    fun decay(c: Context) = prefs(c).getFloat(K_DECAY, DEF_DECAY).coerceIn(0.80f, 0.97f)
    fun hideSensitivity(c: Context) = prefs(c).getFloat(K_HIDE_SENS, DEF_HIDE_SENS).coerceIn(0.5f, 2.0f)
    fun preset(c: Context) = prefs(c).getInt(K_PRESET, DEF_PRESET).coerceIn(0, 3)
    fun onboarded(c: Context) = prefs(c).getBoolean(K_ONBOARDED, DEF_ONBOARDED)
    fun simScenario(c: Context) = prefs(c).getInt(K_SIM_SCENARIO, DEF_SIM_SCENARIO).coerceIn(0, 7)
    fun simSeat(c: Context) = prefs(c).getInt(K_SIM_SEAT, DEF_SIM_SEAT).coerceIn(0, 3)
    fun flipV(c: Context) = prefs(c).getBoolean(K_FLIP_V, DEF_FLIP_V)
    fun flipGrade(c: Context) = prefs(c).getBoolean(K_FLIP_GRADE, DEF_FLIP_GRADE)
    fun flipH(c: Context) = prefs(c).getBoolean(K_FLIP_H, DEF_FLIP_H)
    fun swap(c: Context) = prefs(c).getBoolean(K_SWAP, DEF_SWAP)

    fun setStrength(c: Context, v: Float) = prefs(c).edit().putFloat(K_STRENGTH, v.coerceIn(0.3f, 6.0f)).apply()
    fun setSimSeat(c: Context, v: Int) = prefs(c).edit().putInt(K_SIM_SEAT, v.coerceIn(0, 3)).apply()
    fun setFlipV(c: Context, v: Boolean) = prefs(c).edit().putBoolean(K_FLIP_V, v).apply()
    fun setFlipGrade(c: Context, v: Boolean) = prefs(c).edit().putBoolean(K_FLIP_GRADE, v).apply()
    fun setFlipH(c: Context, v: Boolean) = prefs(c).edit().putBoolean(K_FLIP_H, v).apply()
    fun setSwap(c: Context, v: Boolean) = prefs(c).edit().putBoolean(K_SWAP, v).apply()
    fun setLonGain(c: Context, v: Float) = prefs(c).edit().putFloat(K_LON_GAIN, v.coerceIn(-4.0f, 4.0f)).apply()
    fun setGradeGain(c: Context, v: Float) = prefs(c).edit().putFloat(K_GRADE_GAIN, v.coerceIn(-4.0f, 4.0f)).apply()
    fun setDotSize(c: Context, v: Float) = prefs(c).edit().putFloat(K_DOT_SIZE, v.coerceIn(0.4f, 3.0f)).apply()
    fun setDotColor(c: Context, v: Int) = prefs(c).edit().putInt(K_DOT_COLOR, v.coerceIn(0, 3)).apply()
    fun setAutoHide(c: Context, v: Boolean) = prefs(c).edit().putBoolean(K_AUTO_HIDE, v).apply()

    fun setCueStyle(c: Context, v: Int) = prefs(c).edit().putInt(K_CUE_STYLE, v.coerceIn(0, 5)).apply()
    fun setOpacity(c: Context, v: Float) = prefs(c).edit().putFloat(K_OPACITY, v.coerceIn(0.2f, 1.0f)).apply()
    fun setDensity(c: Context, v: Float) = prefs(c).edit().putFloat(K_DENSITY, v.coerceIn(0.5f, 2.0f)).apply()
    fun setAccentColor(c: Context, v: Int) = prefs(c).edit().putInt(K_ACCENT_COLOR, v).apply()
    fun setCueModel(c: Context, v: Int) = prefs(c).edit().putInt(K_CUE_MODEL, v.coerceIn(0, 1)).apply()
    fun setPlacement(c: Context, v: Int) = prefs(c).edit().putInt(K_PLACEMENT, v.coerceIn(0, 1)).apply()
    fun setDecay(c: Context, v: Float) = prefs(c).edit().putFloat(K_DECAY, v.coerceIn(0.80f, 0.97f)).apply()
    fun setHideSensitivity(c: Context, v: Float) = prefs(c).edit().putFloat(K_HIDE_SENS, v.coerceIn(0.5f, 2.0f)).apply()
    fun setPreset(c: Context, v: Int) = prefs(c).edit().putInt(K_PRESET, v.coerceIn(0, 3)).apply()
    fun setOnboarded(c: Context, v: Boolean) = prefs(c).edit().putBoolean(K_ONBOARDED, v).apply()
    fun setSimScenario(c: Context, v: Int) = prefs(c).edit().putInt(K_SIM_SCENARIO, v.coerceIn(0, 7)).apply()

    /**
     * Intensity presets write the EXISTING fine keys (all in LIVE_KEYS), so a running overlay updates
     * automatically via onSharedPreferenceChanged with NO preset-specific plumbing. Custom (3) writes
     * nothing — it just records that the user is hand-tuning. Presets are a one-time SETUP choice, not
     * a per-trip control, so the zero-touch-in-car philosophy is preserved.
     */
    fun applyPreset(c: Context, preset: Int) {
        val e = prefs(c).edit()
        when (preset) {
            0 -> { e.putFloat(K_STRENGTH, 1.0f); e.putFloat(K_LON_GAIN, 1.0f); e.putFloat(K_DENSITY, 0.7f); e.putFloat(K_OPACITY, 0.6f) }   // Calm
            1 -> { e.putFloat(K_STRENGTH, 1.8f); e.putFloat(K_LON_GAIN, 1.5f); e.putFloat(K_DENSITY, 1.0f); e.putFloat(K_OPACITY, 1.0f) }   // Balanced
            2 -> { e.putFloat(K_STRENGTH, 3.0f); e.putFloat(K_LON_GAIN, 2.2f); e.putFloat(K_DENSITY, 1.4f); e.putFloat(K_OPACITY, 1.0f) }   // Strong
            // 3 = Custom: leave the fine keys as the user set them
        }
        e.putInt(K_PRESET, preset.coerceIn(0, 3))
        e.apply()
    }

    /** Restore every overlay value to its default (and Balanced preset). UI-driven. */
    fun resetToDefaults(c: Context) {
        prefs(c).edit()
            .putFloat(K_STRENGTH, DEF_STRENGTH).putFloat(K_LON_GAIN, DEF_LON_GAIN)
            .putFloat(K_DOT_SIZE, DEF_DOT_SIZE).putInt(K_DOT_COLOR, DEF_DOT_COLOR)
            .putBoolean(K_AUTO_HIDE, DEF_AUTO_HIDE).putInt(K_CUE_STYLE, DEF_CUE_STYLE)
            .putFloat(K_OPACITY, DEF_OPACITY).putFloat(K_DENSITY, DEF_DENSITY)
            .putInt(K_ACCENT_COLOR, DEF_ACCENT_COLOR).putInt(K_CUE_MODEL, DEF_CUE_MODEL)
            .putInt(K_PLACEMENT, DEF_PLACEMENT).putFloat(K_DECAY, DEF_DECAY)
            .putFloat(K_HIDE_SENS, DEF_HIDE_SENS).putInt(K_PRESET, DEF_PRESET)
            .putFloat(K_GRADE_GAIN, DEF_GRADE_GAIN)
            .putBoolean(K_FLIP_V, DEF_FLIP_V).putBoolean(K_FLIP_GRADE, DEF_FLIP_GRADE)
            .putBoolean(K_FLIP_H, DEF_FLIP_H).putBoolean(K_SWAP, DEF_SWAP)
            .apply()
    }

    /** Keys that should make a running overlay re-read its params. (Not K_PRESET/K_ONBOARDED — the
     *  overlay never reads those; presets write the fine keys, which ARE live.) */
    val LIVE_KEYS = setOf(
        K_STRENGTH, K_LON_GAIN, K_DOT_SIZE, K_DOT_COLOR, K_AUTO_HIDE,
        K_CUE_STYLE, K_OPACITY, K_DENSITY, K_ACCENT_COLOR, K_CUE_MODEL, K_PLACEMENT, K_DECAY, K_HIDE_SENS,
        K_GRADE_GAIN, K_FLIP_V, K_FLIP_GRADE, K_FLIP_H, K_SWAP,
        K_SIM_SCENARIO,  // a running overlay switches between real-sensor and sim feed when this flips
        K_SIM_SEAT       // sim seating orientation — overlay re-reads and re-applies to the sim source
    )

    /** Immutable snapshot the overlay (and the in-app preview) consume when settings change. */
    data class Params(
        val strength: Float,
        val lonGain: Float,    // fore/aft (accel/brake) sensitivity (magnitude; direction is automatic)
        val gradeGain: Float,  // hill/grade sensitivity (signed; direction = sign)
        val dotSize: Float,
        val colorMode: Int,    // 0 Light, 1 Mixed, 2 Dark, 3 Custom(accent)
        val autoHide: Boolean,
        val cueStyle: Int,     // 0 Dots, 1 Streaks, 2 Rails, 3 Horizon, 4 Flow, 5 Chevrons
        val opacity: Float,
        val density: Float,
        val accentColor: Int,  // packed ARGB, 0 = unset
        val cueModel: Int,     // 0 Velocity-flow, 1 Acceleration-pulse
        val placement: Int,    // 0 Side strips, 1 Full frame
        val decay: Float,
        val hideSensitivity: Float,
        val flipV: Boolean,    // reverse vertical (accel/brake) direction
        val flipGrade: Boolean,// reverse ONLY the hill/grade cue (independent of accel/brake)
        val flipH: Boolean,    // reverse horizontal (turn) direction
        val swap: Boolean      // swap vertical <-> horizontal axes
    )

    fun snapshot(c: Context) = Params(
        strength(c), lonGain(c), gradeGain(c), dotSize(c), dotColor(c), autoHide(c),
        cueStyle(c), opacity(c), density(c), accentColor(c), cueModel(c), placement(c), decay(c), hideSensitivity(c),
        flipV(c), flipGrade(c), flipH(c), swap(c)
    )
}
