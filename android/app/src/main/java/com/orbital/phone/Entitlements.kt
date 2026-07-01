package com.orbital.phone

import android.content.Context

/**
 * Cached subscription entitlement for the paid feature: REMOTE STREAMING to the Windows app
 * (SensorService). The on-phone cue overlay (OverlayService) and the Windows app are always FREE —
 * only the laptop stream checks this.
 *
 * Client-side only: Orbital has no backend, so the Google Play Billing library's local cache is the
 * source of truth. [BillingManager] reports Play's DEFINITIVE answers here; this mirror lets a paid
 * user keep streaming OFFLINE (a moving car has no internet). Google recommends server-side
 * verification for high-value goods — noted as a known limitation for this no-backend app.
 *
 * Stored in its OWN prefs file ("orbit_ent"), excluded from Android backup (see res/xml backup rules),
 * so the entitlement can't be cloned onto another device via cloud/adb restore. The user's config
 * (host/port/settings) stays in the normal, backed-up "orbit" prefs.
 *
 * Revocation is DEBOUNCED: a single "no active purchase" answer (e.g. the user temporarily switches
 * the Play Store's active Google account) does NOT revoke — only a persistent run does. A transient
 * empty result rides out the offline grace instead of instantly locking out a paying rider.
 */
object Entitlements {
    private const val PREFS = "orbit_ent"
    private const val K_ACTIVE = "sub_active"
    private const val K_SINCE = "sub_since_ms"
    private const val K_EMPTY = "sub_empty_streak"

    // Honour a cached ACTIVE subscription this long since Play last confirmed it, so a subscriber in a
    // tunnel/car with no data isn't locked out. Every online app open re-confirms and refreshes the stamp.
    private const val GRACE_MS = 14L * 24 * 3600 * 1000
    // Consecutive "OK but no active purchase" results required before we actually revoke. Absorbs a
    // transient account-switch / stale-cache blip (which returns empty for the wrong account) without
    // gifting a genuinely-cancelled user more than a few app sessions of access.
    private const val EMPTY_REVOKE_STREAK = 3

    private fun prefs(c: Context) = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    /** Play definitively confirmed an ACTIVE subscription. Refreshes the grace stamp; clears the streak. */
    fun confirmActive(c: Context) {
        prefs(c).edit()
            .putBoolean(K_ACTIVE, true)
            .putLong(K_SINCE, System.currentTimeMillis())
            .putInt(K_EMPTY, 0)
            .apply()
    }

    /** Play returned a successful result with NO active subscription. Debounced: only the Nth
     *  consecutive empty answer actually revokes, so an account-switch blip doesn't lock out a payer. */
    fun confirmInactive(c: Context) {
        val p = prefs(c)
        val streak = p.getInt(K_EMPTY, 0) + 1
        val e = p.edit().putInt(K_EMPTY, streak)
        if (streak >= EMPTY_REVOKE_STREAK) e.putBoolean(K_ACTIVE, false)
        e.apply()
    }

    /** True if remote streaming is unlocked: Play confirmed an active subscription within the grace
     *  window. The `now >= since` guard rejects a cache stamped in the future (clock skew / rollback),
     *  forcing a fresh online re-verify rather than trusting it. */
    fun isRemoteUnlocked(c: Context): Boolean {
        val p = prefs(c)
        if (!p.getBoolean(K_ACTIVE, false)) return false
        val elapsed = System.currentTimeMillis() - p.getLong(K_SINCE, 0L)
        return elapsed in 0 until GRACE_MS
    }
}
