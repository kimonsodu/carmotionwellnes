package com.steady.phone

import android.app.Activity
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.Bundle
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager

/**
 * Phone mode: full-screen drifting cue dots driven by THIS phone's accelerometer — for using the
 * phone itself in a moving vehicle (no laptop involved). Tap anywhere to exit.
 */
class DotsActivity : Activity(), SensorEventListener {

    private lateinit var view: DotsView
    private var sm: SensorManager? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        view = DotsView(this)
        setContentView(view)
        sm = getSystemService(SENSOR_SERVICE) as SensorManager
    }

    @Suppress("DEPRECATION")
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            // immersive: hide status + nav bars (legacy flags — works on minSdk 26 without AppCompat)
            window.decorView.systemUiVisibility =
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_FULLSCREEN or
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
        }
    }

    override fun onResume() {
        super.onResume()
        sm?.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)?.let {
            sm!!.registerListener(this, it, 16000)   // ~62 Hz
        }
        view.start()
    }

    override fun onPause() {
        super.onPause()
        sm?.unregisterListener(this)
        view.stop()
    }

    override fun onSensorChanged(e: SensorEvent) {
        if (e.sensor.type == Sensor.TYPE_ACCELEROMETER) view.feed(e.values[0], e.values[1])
    }

    override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) {}

    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (event.action == MotionEvent.ACTION_DOWN) { finish(); return true }
        return super.onTouchEvent(event)
    }
}
