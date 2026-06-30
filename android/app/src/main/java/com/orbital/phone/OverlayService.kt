package com.orbital.phone

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
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
import android.os.PowerManager
import android.provider.Settings
import android.view.Gravity
import android.view.WindowManager

/**
 * System-wide Phone-mode overlay: drifting cue dots float on top of every app, driven by THIS
 * phone's own accelerometer. A click-through TYPE_APPLICATION_OVERLAY window hosts a [DotsView];
 * a foreground (specialUse) service keeps it alive with a Stop notification while other apps are
 * in the foreground. Cosmetic settings live in SharedPreferences("orbit") and apply live.
 */
class OverlayService : Service(), SensorEventListener,
    SharedPreferences.OnSharedPreferenceChangeListener {

    companion object {
        @Volatile var running = false
        @Volatile var simPhase = ""          // active simulation phase name ("" = not simulating); shown in the app
        private const val CHANNEL = "orbit_overlay"
        private const val NOTIF_ID = 8                       // SensorService uses 7
        const val ACTION_STOP = "com.orbital.phone.OVERLAY_STOP"

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
    // ---- Simulation Mode (test aid): synthetic IMU OVERRIDES the real sensors while ON ----
    private val sim = MotionSimulator()
    @Volatile private var simScenario = MotionSimulator.OFF   // 0 = Off -> original real-sensor behaviour
    @Volatile private var simRunning = false
    @Volatile private var simSeatLabel = "facing forward"     // seat name shown in the on-cue note
    private var lm: LocationManager? = null
    private var locListener: LocationListener? = null
    @Volatile private var sensing = false       // true while screen-on and the IMU/GPS are live

    // Phone mode: dots are invisible while the display is off, so sensing + GPS are pure waste then.
    private val screenReceiver = object : android.content.BroadcastReceiver() {
        override fun onReceive(c: Context?, i: Intent?) = when (i?.action) {
            Intent.ACTION_SCREEN_OFF -> pauseSensing()
            Intent.ACTION_SCREEN_ON -> resumeSensing()
            else -> Unit
        }
    }

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
        // Read the sim scenario BEFORE the first resumeSensing so it picks the sim-vs-real feed path.
        simScenario = SettingsStore.simScenario(this); sim.setScenario(simScenario); applySimSeat(); applySimFlips()
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
            syncScreenRot()                    // screen-relative cue needs the live display rotation (or 0 under sim)
            true
        } catch (e: Exception) {
            try { v.stop() } catch (_: Exception) {}
            false
        }
    }

    private fun startSensors() {
        thread = HandlerThread("orbit-overlay-sensor").also { it.start() }
        sensorHandler = Handler(thread!!.looper)
        sm = getSystemService(SENSOR_SERVICE) as SensorManager
        // Pause/resume the whole IMU + GPS + dot loop with the display so a screen-off phone in a
        // pocket burns nothing. Dynamic registration is mandatory: SCREEN_ON/OFF can't be declared
        // in the manifest.
        registerReceiver(screenReceiver, IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_ON)
            addAction(Intent.ACTION_SCREEN_OFF)
        })
        // Come up now only if the screen is actually on (the user normally taps Start with it on);
        // otherwise the receiver brings sensing up on the next SCREEN_ON.
        val pm = getSystemService(POWER_SERVICE) as PowerManager
        if (pm.isInteractive) resumeSensing()
    }

    /** Register the IMU listeners on the sensor thread. Safe to call repeatedly (resume path). */
    private fun registerSensors() {
        val m = sm ?: return
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
    }

    /** Screen ON: bring the IMU + GPS + dot loop up — or the sim loop when a scenario is picked.
     *  Idempotent. */
    private fun resumeSensing() {
        if (sensing) return
        sensing = true
        if (simScenario != MotionSimulator.OFF) startSimLoop()   // sim OVERRIDES the real sensors
        else { syncScreenRot(); registerSensors(); startGps() }
        ui.post { view?.start() }
    }

    /** Screen OFF: tear the IMU/GPS (or sim) + dot loop down (dots are invisible anyway). Idempotent. */
    private fun pauseSensing() {
        if (!sensing) return
        sensing = false
        stopSimLoop()
        try { sm?.unregisterListener(this) } catch (_: Exception) {}
        stopGps()
        ui.post { view?.stop() }
        haveR = false   // force a fresh orientation matrix before feeding again on resume
    }

    // ---- Simulation drive loop: posts synthetic samples on the sensor thread at ~62 Hz and feeds
    // them through the SAME VehicleFilter entry points as the real accel/gyro path. ----
    private val simRunnable = object : Runnable {
        override fun run() {
            if (!simRunning) return
            vf.onGps(15f, 0f, false)                       // ~15 m/s so the in-vehicle gate enables
            val s = sim.step()
            if (s.phase != simPhase) {                     // phase changed -> update the on-cue note (no per-frame alloc)
                simPhase = s.phase
                view?.setSimLabel(if (s.phase == "Rest") "— Rest —" else "${s.phase} · $simSeatLabel")
            }
            vf.onGyro(s.gyro[0], s.gyro[1], s.gyro[2])
            if (s.reset) vf.reset()                        // rest entry: drop the absorbed gravity so 0-drive produces no reverse cue
            val o = vf.onAccel(s.accel, false, s.R, System.nanoTime())   // raw accel + R from sim (grade+seat), so the hill cue fires like a real rotation-vector phone
            view?.feed(o.screenX, o.screenY, o.gradeX, o.gradeY, o.enable)
            sensorHandler?.postDelayed(this, 16L)          // ~62 Hz self-repost
        }
    }

    private fun startSimLoop() {
        if (simRunning) return
        simRunning = true
        syncScreenRot()                                   // sim is flat-phone framed -> pin screenRot 0 (cue rides the window)
        view?.setSimActive(true)                          // flips act in the vehicle frame now -> neutralise DotsView's
        sensorHandler?.post(simRunnable)
    }

    private fun stopSimLoop() {
        simRunning = false
        simPhase = ""
        ui.post { view?.setSimLabel(null); view?.setSimActive(false) }   // clear the note; restore screen-axis flips
        sensorHandler?.removeCallbacks(simRunnable)
    }

    /** Optional GPS in-vehicle gate (framework LocationManager only — NO Play Services). No-op if
     *  ACCESS_FINE_LOCATION isn't granted, so the overlay fully works inertial-only. Idempotent:
     *  reuses one listener and clears any prior registration so resume can't stack listeners. */
    private fun startGps() {
        if (checkSelfPermission(android.Manifest.permission.ACCESS_FINE_LOCATION)
            != PackageManager.PERMISSION_GRANTED) return
        val lmgr = (lm ?: (getSystemService(LOCATION_SERVICE) as LocationManager)).also { lm = it }
        val cb = locListener ?: object : LocationListener {
            override fun onLocationChanged(loc: Location) {
                vf.onGps(if (loc.hasSpeed()) loc.speed else -1f, loc.bearing, loc.hasBearing())
            }
            override fun onProviderDisabled(p: String) {}
            override fun onProviderEnabled(p: String) {}
            @Deprecated("kept for older API levels")
            override fun onStatusChanged(p: String?, s: Int, e: android.os.Bundle?) {}
        }.also { locListener = it }
        try { lmgr.removeUpdates(cb) } catch (_: Exception) {}   // never stack duplicate registrations
        try { lmgr.requestLocationUpdates(LocationManager.GPS_PROVIDER, 1000L, 0f, cb, thread!!.looper) }
        catch (_: Exception) {}
    }

    /** Stop GPS updates without dropping the listener (so resume can re-register it). */
    private fun stopGps() {
        try { locListener?.let { lm?.removeUpdates(it) } } catch (_: Exception) {}
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
                view?.feed(o.screenX, o.screenY, o.gradeX, o.gradeY, o.enable)
            }
            Sensor.TYPE_ACCELEROMETER -> {
                val o = vf.onAccel(e.values, false, if (haveR) Rmat else null, e.timestamp)
                view?.feed(o.screenX, o.screenY, o.gradeX, o.gradeY, o.enable)
            }
        }
    }

    override fun onAccuracyChanged(s: Sensor?, a: Int) {}

    // Live settings: any overlay key changes -> push a fresh snapshot on the UI thread.
    override fun onSharedPreferenceChanged(sp: SharedPreferences?, key: String?) {
        // A sim scenario OR seat change restarts the source cleanly on the sensor thread (serialises
        // with the running loop). The seat case also REPLAYS the maneuver from the start with a fresh
        // filter — the accel/turn cue is a transient that decays as gravity is absorbed, so flipping
        // the seat mid-hold would otherwise just flip an already-zero residual (looks like "no change /
        // both seats the same"). Replaying re-fires the transient in the new seat's mirrored direction.
        if (key == SettingsStore.K_SIM_SCENARIO || key == SettingsStore.K_SIM_SEAT) {
            val seatOnly = key == SettingsStore.K_SIM_SEAT
            sensorHandler?.post {
                simScenario = SettingsStore.simScenario(this)
                applySimSeat()
                sim.setScenario(simScenario)                // clock back to 0 -> the maneuver replays
                // A seat-only change while in real-sensor mode (scenario=Off) is a no-op — don't tear
                // down the live IMU/GPS. Otherwise restart the source (sim<->real switch, or replay).
                if (sensing && !(seatOnly && simScenario == MotionSimulator.OFF)) {
                    stopSimLoop()
                    try { sm?.unregisterListener(this) } catch (_: Exception) {}
                    stopGps()
                    vf.reset(); haveR = false               // fresh gravity/baseline so the replay isn't masked by stale state
                    if (simScenario != MotionSimulator.OFF) startSimLoop() else { syncScreenRot(); registerSensors(); startGps() }
                }
            }
        }
        if (key in SettingsStore.LIVE_KEYS) {
            applySimFlips()                       // a Flip ↕/↔/⛰ toggle takes effect live in the sim source too
            val snap = SettingsStore.snapshot(this)
            ui.post { view?.applyParams(snap) }
        }
    }

    /** Seating heading for the simulator: 0 Auto / 1 Forward / 2 Left (90°) / 3 Right (270°) / 4 Rear (180°).
     *  Auto and Forward are both 0° here (the phone's real cue is screen-relative, so Auto needs no angle).
     *  Side seats are exact mirrors (180° apart); labels stay keyed to the SEAT index. This shapes THIS
     *  phone's simulated cue only — a laptop stream applies the laptop's own seat (see SensorService). */
    private fun applySimSeat() {
        val seat = SettingsStore.simSeat(this)
        sim.seatPsiDeg = when (seat) { 2 -> 90f; 3 -> 270f; 4 -> 180f; else -> 0f }
        simSeatLabel = when (seat) { 1 -> "facing forward"; 2 -> "facing left"; 3 -> "facing right"; 4 -> "facing rear"; else -> "auto" }
    }

    /** Keep the sim's vehicle-frame flips in step with the live settings. The simulator applies them
     *  before the seat rotation, so each reverses its maneuver in every seat (DotsView neutralises its
     *  own screen-axis flips while the sim runs — see setSimActive — so they aren't double-applied). */
    private fun applySimFlips() {
        sim.flipV = SettingsStore.flipV(this)
        sim.flipH = SettingsStore.flipH(this)
        sim.flipGrade = SettingsStore.flipGrade(this)
    }

    private fun startForegroundNotification() {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Orbital overlay", NotificationManager.IMPORTANCE_LOW))
        val stop = PendingIntent.getService(
            this, 0, Intent(this, OverlayService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val open = PendingIntent.getActivity(
            this, 1, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stopIcon = Icon.createWithResource(this, R.drawable.ic_orbit_notify)
        val n = Notification.Builder(this, CHANNEL)
            .setContentTitle("Orbital — dots on screen")
            .setContentText("Drifting cue dots over your apps")
            .setSmallIcon(R.drawable.ic_orbit_notify)
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
        syncScreenRot()                    // keep the cue aligned after a rotate (real path only — see below)
    }

    /** The SIM feeds a FLAT-phone synthetic IMU (device-y = forward), so the real display rotation must
     *  NOT re-project it — that lands accel on the wrong screen axis in landscape. Pin screenRot to 0 and
     *  let the overlay window (which rotates with the screen) carry the cue, so accel stays screen-vertical
     *  in BOTH orientations. Real sensors use the live rotation (they need the true device orientation). */
    private fun syncScreenRot() {
        vf.screenRot = if (simScenario != MotionSimulator.OFF) 0 else currentRotation()
    }

    /** Display rotation of the overlay's screen (Surface.ROTATION_0/90/180/270) for the
     *  screen-relative cue. Falls back to ROTATION_0 (portrait) if the display can't be read. */
    private fun currentRotation(): Int = try {
        (getSystemService(DISPLAY_SERVICE) as android.hardware.display.DisplayManager)
            .getDisplay(android.view.Display.DEFAULT_DISPLAY).rotation
    } catch (_: Exception) { 0 }

    override fun onDestroy() {
        running = false
        sensing = false
        stopSimLoop()                                     // no orphaned self-reposting sim runnable
        try { unregisterReceiver(screenReceiver) } catch (_: Exception) {}
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
