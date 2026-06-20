package com.steady.phone

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
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
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.Locale

/**
 * Foreground service that reads the phone's accelerometer + gyroscope and streams
 * one JSON datagram per sample to the laptop over UDP. A foreground service plus a
 * partial wake lock keeps the sensors delivering with the screen off.
 *
 * Wire format matches the browser WS frame the PC already parses:
 *   {"ax":..,"ay":..,"az":..,"gx":..,"gy":..,"gz":..,"g":0|1}
 * accel = m/s^2 (incl. gravity, TYPE_ACCELEROMETER); gyro = deg/s (converted from rad/s).
 */
class SensorService : Service(), SensorEventListener {

    companion object {
        @Volatile var running = false
        @Volatile var statusLine = ""
        @Volatile var readout = ""
        private const val CHANNEL = "steady"
        private const val NOTIF_ID = 7
    }

    private var sm: SensorManager? = null
    private var wake: PowerManager.WakeLock? = null
    private var thread: HandlerThread? = null
    private var handler: Handler? = null

    private var socket: DatagramSocket? = null
    private var addr: InetAddress? = null
    private var port = 8443

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
        val host = intent?.getStringExtra("host")?.trim() ?: ""
        port = intent?.getIntExtra("port", 8443) ?: 8443

        startForegroundNotification()

        thread = HandlerThread("steady-sensors").also { it.start() }
        handler = Handler(thread!!.looper)

        // resolve + open socket on the worker thread (off the main thread)
        handler!!.post {
            try {
                addr = InetAddress.getByName(host)
                socket = DatagramSocket()
                statusLine = "streaming to $host:$port"
            } catch (e: Exception) {
                statusLine = "bad PC address \"$host\": ${e.message}"
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
            setReferenceCounted(false)
            acquire()
        }

        running = true
        return START_STICKY
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
        val s = socket ?: return
        val a = addr ?: return
        // Locale.US so decimals use '.', not a locale comma (would break JSON on the PC)
        val json = String.format(
            Locale.US,
            "{\"ax\":%.3f,\"ay\":%.3f,\"az\":%.3f,\"gx\":%.2f,\"gy\":%.2f,\"gz\":%.2f,\"g\":%d}",
            ax, ay, az, gx, gy, gz, haveGyro
        )
        try {
            val b = json.toByteArray()
            s.send(DatagramPacket(b, b.size, a, port))
            sent++
        } catch (_: Exception) {
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
        try { socket?.close() } catch (_: Exception) {}
        try { thread?.quitSafely() } catch (_: Exception) {}
        statusLine = ""
        readout = ""
        super.onDestroy()
    }
}
