package com.orbital.phone

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.ServiceInfo
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.PowerManager
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.Locale
import java.util.UUID

/**
 * Foreground service: reads accelerometer + gyroscope and streams one JSON object per
 * sample to the laptop. Two transports, chosen by the app:
 *   - "wifi": UDP datagram to host:port (needs a shared local link / USB tether)
 *   - "bt":   Bluetooth RFCOMM/SPP, newline-delimited, to a paired PC (no network at all)
 * A foreground service + partial wake lock keeps sampling with the screen off.
 *
 * Frame (m/s^2 accel incl. gravity; deg/s gyro), matching every PC ingest path:
 *   {"ax":..,"ay":..,"az":..,"gx":..,"gy":..,"gz":..,"g":0|1}\n
 */
class SensorService : Service(), SensorEventListener,
    SharedPreferences.OnSharedPreferenceChangeListener {

    companion object {
        @Volatile var running = false
        @Volatile var statusLine = ""
        @Volatile var readout = ""
        val BT_UUID: UUID = UUID.fromString("b1a7e94c-1c3a-4e7e-9b2a-0a1b2c3d4e5f")
        private const val CHANNEL = "orbit"
        private const val NOTIF_ID = 7
    }

    private interface Transport { fun send(bytes: ByteArray); fun close() }

    private var sm: SensorManager? = null
    private var wake: PowerManager.WakeLock? = null
    private var thread: HandlerThread? = null
    private var handler: Handler? = null
    @Volatile private var transport: Transport? = null

    @Volatile private var ax = 0f
    @Volatile private var ay = 0f
    @Volatile private var az = 0f
    @Volatile private var gx = 0f
    @Volatile private var gy = 0f
    @Volatile private var gz = 0f
    @Volatile private var haveGyro = 0

    // shared vehicle-motion filter -> additive cleaned channels streamed to the laptop
    private val vf = VehicleFilter()
    private val Rmat = FloatArray(9)
    @Volatile private var haveR = false
    @Volatile private var vlong = 0f
    @Volatile private var vlat = 0f
    @Volatile private var yawDeg = 0f
    @Volatile private var gateVal = 1f
    @Volatile private var vehFlag = 0
    @Volatile private var spdMps = -1f
    private var lm: android.location.LocationManager? = null
    private var locListener: android.location.LocationListener? = null

    // ---- Simulation Mode for the STREAM: synthetic IMU OVERRIDES the real sensors and is streamed to
    // the laptop, so the PC overlay can be tested with no driving (mirrors OverlayService's on-phone
    // sim). Shares scenario / seat with the on-phone cue; flips are applied by the laptop's own cue. ----
    private val sim = MotionSimulator()
    @Volatile private var simScenario = MotionSimulator.OFF
    @Volatile private var simRunning = false

    private var lastSendNs = 0L
    private var rateMark = 0L
    private var sent = 0L
    private val RAD2DEG = 57.29578f

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Recover target from SharedPreferences when the intent is null (START_STICKY restart).
        val prefs = getSharedPreferences("orbit", Context.MODE_PRIVATE)
        val mode = intent?.getStringExtra("mode") ?: prefs.getString("mode", "wifi") ?: "wifi"
        var host = (intent?.getStringExtra("host")?.trim()).orEmpty()
        if (host.isEmpty()) host = (prefs.getString("host", "") ?: "").trim()
        val port = intent?.getIntExtra("port", 0)?.takeIf { it > 0 }
            ?: (prefs.getString("port", "8443")?.toIntOrNull() ?: 8443)
        var btMac = (intent?.getStringExtra("btMac")).orEmpty()
        if (btMac.isEmpty()) btMac = prefs.getString("btMac", "") ?: ""

        if (mode == "wifi" && host.isEmpty()) return fail("no PC address — open the app and enter it")
        if (mode == "bt" && btMac.isEmpty()) return fail("no Bluetooth device — open the app and pick the PC")

        startForegroundNotification()

        thread = HandlerThread("orbit-sensors").also { it.start() }
        handler = Handler(thread!!.looper)

        // open the transport on the worker thread (BT connect() blocks)
        handler!!.post {
            try {
                transport = if (mode == "bt") openBt(btMac) else openUdp(host, port)
                statusLine = if (mode == "bt") "Bluetooth — streaming" else "WiFi — streaming to $host:$port"
            } catch (e: Exception) {
                transport = null
                statusLine = "connect failed: ${e.message}"
                running = false          // don't sit foregrounded with a dead stream + held wake lock
                stopSelf()               // -> onDestroy releases sensors/wake lock; UI flips to Start
            }
        }

        sm = getSystemService(Context.SENSOR_SERVICE) as SensorManager
        // Sim OVERRIDES the real sensors: when a scenario is picked we stream SYNTHETIC IMU instead of
        // registering the accelerometer/gyro, so the laptop overlay can be tested with no driving. The
        // sim shares its scenario/seat with the on-phone cue (flips owned by the laptop) and updates live.
        simScenario = SettingsStore.simScenario(this); sim.setScenario(simScenario); applySimSeat(); applySimFlips()
        SettingsStore.prefs(this).registerOnSharedPreferenceChangeListener(this)
        if (simScenario != MotionSimulator.OFF) startSimLoop()
        else { registerRealSensors(); startGps() }

        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wake = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "orbit:stream").apply {
            setReferenceCounted(false); acquire()
        }

        running = true
        return START_STICKY
    }

    private fun fail(msg: String): Int {
        statusLine = msg
        running = false
        stopSelf()
        return START_NOT_STICKY
    }

    /** Optional GPS speed for the vehicle filter + stream (framework LocationManager, no Play
     *  Services). No-op if ACCESS_FINE_LOCATION isn't granted. */
    private fun startGps() {
        if (checkSelfPermission(android.Manifest.permission.ACCESS_FINE_LOCATION)
            != android.content.pm.PackageManager.PERMISSION_GRANTED) return
        lm = getSystemService(Context.LOCATION_SERVICE) as android.location.LocationManager
        stopGps()                                  // never stack duplicate registrations across sim<->real switches
        val cb = object : android.location.LocationListener {
            override fun onLocationChanged(loc: android.location.Location) {
                spdMps = if (loc.hasSpeed()) loc.speed else -1f
                vf.onGps(spdMps, loc.bearing, loc.hasBearing())
            }
            override fun onProviderDisabled(p: String) {}
            override fun onProviderEnabled(p: String) {}
            @Deprecated("kept for older API levels")
            override fun onStatusChanged(p: String?, s: Int, e: android.os.Bundle?) {}
        }
        locListener = cb
        try {
            lm!!.requestLocationUpdates(
                android.location.LocationManager.GPS_PROVIDER, 1000L, 0f, cb, handler!!.looper)
        } catch (_: Exception) {}
    }

    /** Register the real accelerometer/gyro/orientation listeners (skipped while the sim drives). */
    private fun registerRealSensors() {
        val s = sm ?: return
        val us = 16000 // ~62 Hz request
        val accel = s.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
        accel?.let { s.registerListener(this, it, us, handler) }
        s.getDefaultSensor(Sensor.TYPE_GYROSCOPE)?.let { s.registerListener(this, it, us, handler) }
        // Orientation for the world-frame vehicle filter (mirrors OverlayService); optional.
        (s.getDefaultSensor(Sensor.TYPE_ROTATION_VECTOR)
            ?: s.getDefaultSensor(Sensor.TYPE_GAME_ROTATION_VECTOR)
            ?: s.getDefaultSensor(Sensor.TYPE_GEOMAGNETIC_ROTATION_VECTOR))
            ?.let { s.registerListener(this, it, us, handler) }
        if (accel == null) statusLine = "no accelerometer on this phone"
    }

    /** Seat for the laptop stream is owned by the WINDOWS cue (it rotates the cue vector by the chosen
     *  seat), NOT the phone — same rationale as flips (see applySimFlips). Stream a FORWARD-neutral
     *  maneuver so the laptop applies its own seat from its own selector. The on-phone OverlayService cue
     *  keeps its own seat (that's the phone's local cue). */
    private fun applySimSeat() {
        sim.seatPsiDeg = 0f
    }
    /** Flips for the laptop stream are owned by the WINDOWS cue (Invert accel/turn/hill), NOT the phone.
     *  The laptop renders raw IMU through its own pipeline and applies flips at cue level, so baking them
     *  at the source here would double-apply — and the laptop's forward-direction estimator can wash a
     *  source flip out anyway. Keep the streamed maneuver unflipped, matching the real-drive stream
     *  (never source-flipped). The on-phone OverlayService cue is screen-relative and still source-flips;
     *  that path is unchanged. */
    private fun applySimFlips() {
        sim.flipV = false
        sim.flipH = false
        sim.flipGrade = false
    }

    // Sim drive loop: synthesize one IMU frame at ~62 Hz, run it through the SAME VehicleFilter as the
    // real path, then stream it — so the PC receives synthetic raw accel/gyro indistinguishable from a
    // real phone (it runs its own pipeline on ax/ay/az/gx/gy/gz).
    private val simRunnable = object : Runnable {
        override fun run() {
            if (!simRunning) return
            vf.onGps(15f, 0f, false)                          // ~15 m/s so the in-vehicle gate enables
            val s = sim.step()
            vf.onGyro(s.gyro[0], s.gyro[1], s.gyro[2])
            if (s.reset) vf.reset()                           // rest entry: drop absorbed gravity (no reverse cue)
            val o = vf.onAccel(s.accel, false, s.R, System.nanoTime())
            ax = s.accel[0]; ay = s.accel[1]; az = s.accel[2] // stream the SYNTHETIC raw frame (what the PC ingests)
            gx = s.gyro[0] * RAD2DEG; gy = s.gyro[1] * RAD2DEG; gz = s.gyro[2] * RAD2DEG; haveGyro = 1
            vlong = o.lon; vlat = o.lat; yawDeg = o.yawDegPerSec; gateVal = o.gate; vehFlag = if (o.inVehicle) 1 else 0
            spdMps = 15f                                      // tell the PC's gate we're moving
            maybeSend()
            handler?.postDelayed(this, 16L)                  // ~62 Hz self-repost
        }
    }

    private fun startSimLoop() {
        if (simRunning) return
        simRunning = true
        handler?.post(simRunnable)
    }

    private fun stopSimLoop() {
        simRunning = false
        handler?.removeCallbacks(simRunnable)
    }

    /** Live sim changes while streaming: switch between real-sensor and sim feed, and replay the
     *  maneuver on a seat/scenario change — mirrors OverlayService. (Flips are owned by the laptop cue.) */
    override fun onSharedPreferenceChanged(sp: SharedPreferences?, key: String?) {
        when (key) {
            SettingsStore.K_SIM_SCENARIO -> handler?.post {
                simScenario = SettingsStore.simScenario(this)
                applySimSeat(); applySimFlips()
                sim.setScenario(simScenario)                 // clock back to 0 -> the maneuver replays
                stopSimLoop()
                try { sm?.unregisterListener(this) } catch (_: Exception) {}
                vf.reset(); haveR = false                    // fresh gravity/baseline so the replay isn't masked
                if (simScenario != MotionSimulator.OFF) { stopGps(); startSimLoop() }
                else { registerRealSensors(); startGps() }
            }
            // K_FLIP_* and K_SIM_SEAT intentionally ignored here: flips AND seat for the laptop stream are
            // owned by the Windows cue (see applySimFlips / applySimSeat), so phone-side flip/seat toggles
            // must not change the streamed maneuver — the laptop applies its own from its own selectors.
        }
    }

    /** Stop GPS updates (sim provides its own speed). Safe if GPS was never started. */
    private fun stopGps() {
        try { locListener?.let { lm?.removeUpdates(it) } } catch (_: Exception) {}
    }

    private fun openUdp(host: String, port: Int): Transport {
        val a = InetAddress.getByName(host)
        if (a.isLoopbackAddress || a.isAnyLocalAddress)
            throw java.net.UnknownHostException("\"$host\" is localhost, not the laptop")
        val sock = DatagramSocket()
        return object : Transport {
            override fun send(bytes: ByteArray) = sock.send(DatagramPacket(bytes, bytes.size, a, port))
            override fun close() { try { sock.close() } catch (_: Exception) {} }
        }
    }

    @SuppressLint("MissingPermission")
    private fun openBt(mac: String): Transport {
        val adapter = (getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager).adapter
            ?: throw IllegalStateException("no Bluetooth on this phone")
        if (!adapter.isEnabled) throw IllegalStateException("turn on Bluetooth")
        val dev = adapter.getRemoteDevice(mac)
        try { adapter.cancelDiscovery() } catch (_: Exception) {}
        val sock = dev.createRfcommSocketToServiceRecord(BT_UUID)
        sock.connect()                 // blocking
        val out: OutputStream = sock.outputStream
        return object : Transport {
            override fun send(bytes: ByteArray) = out.write(bytes)
            override fun close() { try { sock.close() } catch (_: Exception) {} }
        }
    }

    private fun startForegroundNotification() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Orbital streaming", NotificationManager.IMPORTANCE_LOW)
        )
        val n: Notification = Notification.Builder(this, CHANNEL)
            .setContentTitle("Orbital")
            .setContentText("Streaming motion to the laptop")
            .setSmallIcon(R.drawable.ic_orbit_notify)
            .setOngoing(true)
            .build()
        // On API 34 only OR in LOCATION when a location permission is held, else startForeground throws.
        if (Build.VERSION.SDK_INT >= 34) {
            var type = ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC
            val loc = checkSelfPermission(android.Manifest.permission.ACCESS_FINE_LOCATION) ==
                android.content.pm.PackageManager.PERMISSION_GRANTED   // FINE only — matches startGps()
            if (loc) type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_LOCATION
            startForeground(NOTIF_ID, n, type)
        } else if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIF_ID, n)
        }
    }

    // callbacks arrive on the HandlerThread, so sends never touch the main thread
    override fun onSensorChanged(e: SensorEvent) {
        when (e.sensor.type) {
            Sensor.TYPE_ROTATION_VECTOR,
            Sensor.TYPE_GAME_ROTATION_VECTOR,
            Sensor.TYPE_GEOMAGNETIC_ROTATION_VECTOR -> {
                SensorManager.getRotationMatrixFromVector(Rmat, e.values); haveR = true
            }
            Sensor.TYPE_ACCELEROMETER -> {
                ax = e.values[0]; ay = e.values[1]; az = e.values[2]
                // Raw accel still streamed as-is (mounted-laptop path unchanged). Also run the filter
                // (self-splits gravity) so vlong/vlat/yaw/gate/veh mirror the on-phone overlay.
                val o = vf.onAccel(e.values, false, if (haveR) Rmat else null, e.timestamp)
                vlong = o.lon; vlat = o.lat; yawDeg = o.yawDegPerSec
                gateVal = o.gate; vehFlag = if (o.inVehicle) 1 else 0
                maybeSend()
            }
            Sensor.TYPE_GYROSCOPE -> {
                gx = e.values[0] * RAD2DEG; gy = e.values[1] * RAD2DEG; gz = e.values[2] * RAD2DEG
                haveGyro = 1
                vf.onGyro(e.values[0], e.values[1], e.values[2])   // rad/s for the filter
            }
        }
    }

    private fun maybeSend() {
        val now = System.nanoTime()
        if (now - lastSendNs < 14_000_000L) return   // ~66 Hz cap
        lastSendNs = now
        val t = transport ?: return
        // Locale.US so decimals use '.', not a locale comma (would break JSON on the PC).
        // Trailing '\n' delimits frames for the Bluetooth stream; harmless for UDP datagrams.
        val json = String.format(
            Locale.US,
            "{\"ax\":%.3f,\"ay\":%.3f,\"az\":%.3f,\"gx\":%.2f,\"gy\":%.2f,\"gz\":%.2f,\"g\":%d," +
                "\"vlong\":%.3f,\"vlat\":%.3f,\"yaw\":%.2f,\"gate\":%.2f,\"veh\":%d,\"spd\":%.2f}\n",
            ax, ay, az, gx, gy, gz, haveGyro,
            vlong, vlat, yawDeg, gateVal, vehFlag, spdMps
        )
        try {
            t.send(json.toByteArray())
            sent++
        } catch (e: Exception) {
            statusLine = "send failed: ${e.message}"
        }
        if (now - rateMark >= 500_000_000L) {
            val hz = if (rateMark == 0L) 0 else sent * 1_000_000_000L / (now - rateMark)
            sent = 0; rateMark = now
            readout = String.format(
                Locale.US, "a %.1f %.1f %.1f   g %.0f %.0f %.0f   %d Hz", ax, ay, az, gx, gy, gz, hz
            )
        }
    }

    override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) {}

    override fun onDestroy() {
        running = false
        stopSimLoop()
        try { SettingsStore.prefs(this).unregisterOnSharedPreferenceChangeListener(this) } catch (_: Exception) {}
        try { sm?.unregisterListener(this) } catch (_: Exception) {}
        try { locListener?.let { lm?.removeUpdates(it) } } catch (_: Exception) {}
        try { wake?.release() } catch (_: Exception) {}
        try { transport?.close() } catch (_: Exception) {}
        try { thread?.quitSafely() } catch (_: Exception) {}
        statusLine = ""
        readout = ""
        super.onDestroy()
    }
}
