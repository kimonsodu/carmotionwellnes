package com.steady.phone

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.widget.Button
import android.widget.EditText
import android.widget.TextView

class MainActivity : Activity() {

    private lateinit var host: EditText
    private lateinit var port: EditText
    private lateinit var toggle: Button
    private lateinit var status: TextView
    private lateinit var readout: TextView
    private val ui = Handler(Looper.getMainLooper())

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        host = findViewById(R.id.host)
        port = findViewById(R.id.port)
        toggle = findViewById(R.id.toggle)
        status = findViewById(R.id.status)
        readout = findViewById(R.id.readout)

        val prefs = getSharedPreferences("steady", Context.MODE_PRIVATE)
        host.setText(prefs.getString("host", ""))
        port.setText(prefs.getString("port", "8443"))

        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }

        toggle.setOnClickListener {
            if (SensorService.running) {
                stopService(Intent(this, SensorService::class.java))
            } else {
                val h = host.text.toString().trim()
                val p = port.text.toString().trim().ifEmpty { "8443" }
                prefs.edit().putString("host", h).putString("port", p).apply()
                val i = Intent(this, SensorService::class.java)
                    .putExtra("host", h)
                    .putExtra("port", p.toIntOrNull() ?: 8443)
                startForegroundService(i)   // minSdk 26
            }
        }
        tick()
    }

    // poll the service's published state for the UI (no IPC plumbing needed)
    private fun tick() {
        val running = SensorService.running
        toggle.text = if (running) "Stop streaming" else "Start streaming"
        status.text = if (running) SensorService.statusLine else "stopped"
        readout.text = SensorService.readout
        host.isEnabled = !running
        port.isEnabled = !running
        ui.postDelayed({ tick() }, 200)
    }

    override fun onDestroy() {
        ui.removeCallbacksAndMessages(null)
        super.onDestroy()
    }
}
