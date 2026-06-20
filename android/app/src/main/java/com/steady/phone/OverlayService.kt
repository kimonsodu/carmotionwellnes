package com.steady.phone

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.graphics.PixelFormat
import android.graphics.drawable.Icon
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.Looper
import android.provider.Settings
import android.view.Gravity
import android.view.WindowManager

/**
 * System-wide Phone-mode overlay: drifting cue dots float on top of every app, driven by THIS
 * phone's own accelerometer. A click-through TYPE_APPLICATION_OVERLAY window hosts a [DotsView];
 * a foreground (specialUse) service keeps it alive with a Stop notification while other apps are
 * in the foreground. Cosmetic settings live in SharedPreferences("steady") and apply live.
 */
class OverlayService : Service(), SensorEventListener,
    SharedPreferences.OnSharedPreferenceChangeListener {

    companion object {
        @Volatile var running = false
        private const val CHANNEL = "steady_overlay"
        private const val NOTIF_ID = 8                       // SensorService uses 7
        const val ACTION_STOP = "com.steady.phone.OVERLAY_STOP"

        fun start(ctx: Context) = ctx.startForegroundService(Intent(ctx, OverlayService::class.java))
        fun stop(ctx: Context) = ctx.stopService(Intent(ctx, OverlayService::class.java))
    }

    private var wm: WindowManager? = null
    @Volatile private var view: DotsView? = null   // written on main, read on the sensor thread
    private var lp: WindowManager.LayoutParams? = null
    private var sm: SensorManager? = null
    private var thread: HandlerThread? = null
    private var sensorHandler: Handler? = null
    private val ui = Handler(Looper.getMainLooper())
    private val vf = VehicleFilter()
    private val Rmat = FloatArray(9)
    @Volatile private var haveR = false
    private var lm: LocationManager? = null
    private var locListener: LocationListener? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) { stopSelf(); return START_NOT_STICKY }
        if (running) return START_STICKY                     // idempotent re-start

        // Post the foreground notification BEFORE any early stopSelf() so a STICKY recreate with a
        // revoked overlay permission (or a failed addView) can't crash with
        // ForegroundServiceDidNotStartInTimeException — stopSelf is then a legal foreground->stop.
        startForegroundNotification()
        // The overlay permission can be revoked any time, incl. between a kill and a START_STICKY
        // restart; addView would throw BadTokenException without it (and on some OEMs even with it).
        if (!Settings.canDrawOverlays(this) || !addOverlay()) { stopSelf(); return START_NOT_STICKY }
        startSensors()
        SettingsStore.prefs(this).registerOnSharedPreferenceChangeListener(this)
        running = true
        return START_STICKY
    }

    private fun addOverlay(): Boolean {
        wm = getSystemService(WINDOW_SERVICE) as WindowManager
        val v = DotsView(this)
        v.applyParams(SettingsStore.snapshot(this))
        val p = WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,   // API 26+
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or   // fully click-through
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON,    // screen stays on (no wake lock)
            PixelFormat.TRANSLUCENT
        )
        p.gravity = Gravity.TOP or Gravity.START
        if (Build.VERSION.SDK_INT >= 28)                          // API 28 guard (minSdk is 26)
            p.layoutInDisplayCutoutMode =
                WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
        // Only start the Choreographer loop after a SUCCESSFUL add, and stop it on failure, so a
        // failed addView can't orphan a self-reposting frame callback (view stays null in onDestroy).
        return try {
            wm!!.addView(v, p)
            v.start()
            view = v; lp = p
            true
        } catch (e: Exception) {
            try { v.stop() } catch (_: Exception) {}
            false
        }
    }

    private fun startSensors() {
        thread = HandlerThread("steady-overlay-sensor").also { it.start() }
        sensorHandler = Handler(thread!!.looper)
        sm = getSystemService(SENSOR_SERVICE) as SensorManager
        val m = sm!!
        val us = 16000   // ~62 Hz; under the Android 12 200 Hz cap (no HIGH_SAMPLING_RATE_SENSORS)

        // Orientation source, best -> fallback. GAME_ROTATION_VECTOR avoids the magnetometer (immune
        // to a car's steel body); GEOMAGNETIC is the gyro-less budget-phone fallback.
        val rot = m.getDefaultSensor(Sensor.TYPE_ROTATION_VECTOR)
            ?: m.getDefaultSensor(Sensor.TYPE_GAME_ROTATION_VECTOR)
            ?: m.getDefaultSensor(Sensor.TYPE_GEOMAGNETIC_ROTATION_VECTOR)
        rot?.let { m.registerListener(this, it, us, sensorHandler) }

        // Accel source. With a rotation sensor, prefer fused LINEAR_ACCELERATION (R handles the
        // frame). WITHOUT one, we MUST use raw ACCELEROMETER so VehicleFilter self-splits gravity
        // and its gravity-projection fallback works (linear-accel alone can't reconstruct gravity).
        val lin = if (rot != null) m.getDefaultSensor(Sensor.TYPE_LINEAR_ACCELERATION) else null
        if (lin != null) m.registerListener(this, lin, us, sensorHandler)
        else m.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
            ?.let { m.registerListener(this, it, us, sensorHandler) }

        m.getDefaultSensor(Sensor.TYPE_GYROSCOPE)?.let { m.registerListener(this, it, us, sensorHandler) }

        startGps()
    }

    /** Optional GPS in-vehicle gate (framework LocationManager only — NO Play Services). No-op if
     *  ACCESS_FINE_LOCATION isn't granted, so the overlay fully works inertial-only. */
    private fun startGps() {
        if (checkSelfPermission(android.Manifest.permission.ACCESS_FINE_LOCATION)
            != PackageManager.PERMISSION_GRANTED) return
        lm = getSystemService(LOCATION_SERVICE) as LocationManager
        val cb = object : LocationListener {
            override fun onLocationChanged(loc: Location) {
                vf.onGps(if (loc.hasSpeed()) loc.speed else -1f, loc.bearing, loc.hasBearing())
            }
            override fun onProviderDisabled(p: String) {}
            override fun onProviderEnabled(p: String) {}
            @Deprecated("kept for older API levels")
            override fun onStatusChanged(p: String?, s: Int, e: android.os.Bundle?) {}
        }
        locListener = cb
        try { lm!!.requestLocationUpdates(LocationManager.GPS_PROVIDER, 1000L, 0f, cb, thread!!.looper) }
        catch (_: Exception) {}
    }

    // Callbacks arrive on the HandlerThread; VehicleFilter state is single-threaded here. feed() only
    // writes volatiles (single writer), so the Choreographer thread reads them safely.
    override fun onSensorChanged(e: SensorEvent) {
        when (e.sensor.type) {
            Sensor.TYPE_ROTATION_VECTOR,
            Sensor.TYPE_GAME_ROTATION_VECTOR,
            Sensor.TYPE_GEOMAGNETIC_ROTATION_VECTOR -> {
                SensorManager.getRotationMatrixFromVector(Rmat, e.values); haveR = true
            }
            Sensor.TYPE_GYROSCOPE ->
                vf.onGyro(e.values[0], e.values[1], e.values[2])
            Sensor.TYPE_LINEAR_ACCELERATION -> {
                val o = vf.onAccel(e.values, true, if (haveR) Rmat else null, e.timestamp)
                view?.feed(o.lon, o.lat, o.enable)
            }
            Sensor.TYPE_ACCELEROMETER -> {
                val o = vf.onAccel(e.values, false, if (haveR) Rmat else null, e.timestamp)
                view?.feed(o.lon, o.lat, o.enable)
            }
        }
    }

    override fun onAccuracyChanged(s: Sensor?, a: Int) {}

    // Live settings: any overlay key changes -> push a fresh snapshot on the UI thread.
    override fun onSharedPreferenceChanged(sp: SharedPreferences?, key: String?) {
        if (key in SettingsStore.LIVE_KEYS) {
            val snap = SettingsStore.snapshot(this)
            ui.post { view?.applyParams(snap) }
        }
    }

    private fun startForegroundNotification() {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Steady overlay", NotificationManager.IMPORTANCE_LOW))
        val stop = PendingIntent.getService(
            this, 0, Intent(this, OverlayService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val open = PendingIntent.getActivity(
            this, 1, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stopIcon = Icon.createWithResource(this, R.drawable.ic_steady_notify)
        val n = Notification.Builder(this, CHANNEL)
            .setContentTitle("Steady — dots on screen")
            .setContentText("Drifting cue dots over your apps")
            .setSmallIcon(R.drawable.ic_steady_notify)
            .setOngoing(true)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(stopIcon, "Stop", stop).build())
            .build()
        // On API 34 the FGS type must match held permissions: only OR in LOCATION when a location
        // permission is actually granted, else startForeground throws SecurityException.
        if (Build.VERSION.SDK_INT >= 34) {
            var type = ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
            if (hasLocationPerm()) type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_LOCATION
            startForeground(NOTIF_ID, n, type)
        } else {
            startForeground(NOTIF_ID, n)
        }
    }

    // FINE only — must match startGps() (GPS_PROVIDER needs FINE), so the declared FGS LOCATION
    // type can never disagree with whether location is actually used.
    private fun hasLocationPerm(): Boolean =
        checkSelfPermission(android.Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED

    override fun onConfigurationChanged(newConfig: android.content.res.Configuration) {
        super.onConfigurationChanged(newConfig)
        // MATCH_PARENT + FLAG_LAYOUT_NO_LIMITS already track rotation; force a re-measure.
        view?.let { v -> lp?.let { p -> try { wm?.updateViewLayout(v, p) } catch (_: Exception) {} } }
    }

    override fun onDestroy() {
        running = false
        try { SettingsStore.prefs(this).unregisterOnSharedPreferenceChangeListener(this) } catch (_: Exception) {}
        try { sm?.unregisterListener(this) } catch (_: Exception) {}
        try { locListener?.let { lm?.removeUpdates(it) } } catch (_: Exception) {}
        try { view?.stop() } catch (_: Exception) {}
        try { view?.let { wm?.removeView(it) } } catch (_: Exception) {}   // avoid a leaked window
        try { thread?.quitSafely() } catch (_: Exception) {}
        view = null; lp = null
        super.onDestroy()
    }
}
