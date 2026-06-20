package com.steady.phone

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.Intent
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
class SensorService : Service(), SensorEventListener {

    companion object {
        @Volatile var running = false
        @Volatile var statusLine = ""
        @Volatile var readout = ""
        val BT_UUID: UUID = UUID.fromString("b1a7e94c-1c3a-4e7e-9b2a-0a1b2c3d4e5f")
        private const val CHANNEL = "steady"
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

    private var lastSendNs = 0L
    private var rateMark = 0L
    private var sent = 0L
    private val RAD2DEG = 57.29578f

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Recover target from SharedPreferences when the intent is null (START_STICKY restart).
        val prefs = getSharedPreferences("steady", Context.MODE_PRIVATE)
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

        thread = HandlerThread("steady-sensors").also { it.start() }
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
        val accel = sm!!.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
        val gyro = sm!!.getDefaultSensor(Sensor.TYPE_GYROSCOPE)
        val us = 16000 // ~62 Hz request
        accel?.let { sm!!.registerListener(this, it, us, handler) }
        gyro?.let { sm!!.registerListener(this, it, us, handler) }
        if (accel == null) statusLine = "no accelerometer on this phone"

        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wake = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "steady:stream").apply {
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
            NotificationChannel(CHANNEL, "Steady streaming", NotificationManager.IMPORTANCE_LOW)
        )
        val n: Notification = Notification.Builder(this, CHANNEL)
            .setContentTitle("Steady")
            .setContentText("Streaming motion to the laptop")
            .setSmallIcon(android.R.drawable.ic_menu_compass)
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 29)
            startForeground(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        else
            startForeground(NOTIF_ID, n)
    }

    // callbacks arrive on the HandlerThread, so sends never touch the main thread
    override fun onSensorChanged(e: SensorEvent) {
        when (e.sensor.type) {
            Sensor.TYPE_ACCELEROMETER -> {
                ax = e.values[0]; ay = e.values[1]; az = e.values[2]
                maybeSend()
            }
            Sensor.TYPE_GYROSCOPE -> {
                gx = e.values[0] * RAD2DEG; gy = e.values[1] * RAD2DEG; gz = e.values[2] * RAD2DEG
                haveGyro = 1
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
            "{\"ax\":%.3f,\"ay\":%.3f,\"az\":%.3f,\"gx\":%.2f,\"gy\":%.2f,\"gz\":%.2f,\"g\":%d}\n",
            ax, ay, az, gx, gy, gz, haveGyro
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
        try { sm?.unregisterListener(this) } catch (_: Exception) {}
        try { wake?.release() } catch (_: Exception) {}
        try { transport?.close() } catch (_: Exception) {}
        try { thread?.quitSafely() } catch (_: Exception) {}
        statusLine = ""
        readout = ""
        super.onDestroy()
    }
}
