package com.steady.phone

import android.content.Context
import android.content.SharedPreferences

/**
 * Phone-mode overlay settings, persisted in SharedPreferences("steady"). Defaults mirror the
 * Windows app's feel: Strength 1.8x, Dot size 1.0x. Dot colour defaults to Mixed (per-dot
 * light/dark) so the dots contrast against any app/wallpaper behind them; Auto-hide defaults
 * off so the dots don't vanish during a first try at a desk.
 */
object SettingsStore {
    const val PREFS = "steady"

    const val K_STRENGTH = "ph_strength"     // Float 0.3..6.0  (drift sensitivity multiplier)
    const val K_DOT_SIZE = "ph_dotSize"      // Float 0.4..3.0  (dot radius scale)
    const val K_DOT_COLOR = "ph_dotColor"    // Int   0/1/2 = Light / Mixed / Dark
    const val K_AUTO_HIDE = "ph_autoHide"    // Bool  fade dots when the phone is still

    const val DEF_STRENGTH = 1.8f
    const val DEF_DOT_SIZE = 1.0f
    const val DEF_DOT_COLOR = 1              // Mixed — contrasts over any app
    const val DEF_AUTO_HIDE = false

    fun prefs(c: Context): SharedPreferences = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    fun strength(c: Context) = prefs(c).getFloat(K_STRENGTH, DEF_STRENGTH).coerceIn(0.3f, 6.0f)
    fun dotSize(c: Context) = prefs(c).getFloat(K_DOT_SIZE, DEF_DOT_SIZE).coerceIn(0.4f, 3.0f)
    fun dotColor(c: Context) = prefs(c).getInt(K_DOT_COLOR, DEF_DOT_COLOR).coerceIn(0, 2)
    fun autoHide(c: Context) = prefs(c).getBoolean(K_AUTO_HIDE, DEF_AUTO_HIDE)

    fun setStrength(c: Context, v: Float) = prefs(c).edit().putFloat(K_STRENGTH, v.coerceIn(0.3f, 6.0f)).apply()
    fun setDotSize(c: Context, v: Float) = prefs(c).edit().putFloat(K_DOT_SIZE, v.coerceIn(0.4f, 3.0f)).apply()
    fun setDotColor(c: Context, v: Int) = prefs(c).edit().putInt(K_DOT_COLOR, v.coerceIn(0, 2)).apply()
    fun setAutoHide(c: Context, v: Boolean) = prefs(c).edit().putBoolean(K_AUTO_HIDE, v).apply()

    /** Keys that should make a running overlay re-read its params. */
    val LIVE_KEYS = setOf(K_STRENGTH, K_DOT_SIZE, K_DOT_COLOR, K_AUTO_HIDE)

    /** Immutable snapshot the overlay consumes when settings change. */
    data class Params(
        val strength: Float,
        val dotSize: Float,
        val colorMode: Int,   // 0 Light, 1 Mixed, 2 Dark
        val autoHide: Boolean
    )

    fun snapshot(c: Context) = Params(strength(c), dotSize(c), dotColor(c), autoHide(c))
}
