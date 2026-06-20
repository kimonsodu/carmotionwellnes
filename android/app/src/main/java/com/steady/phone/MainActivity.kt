package com.steady.phone

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.RadioGroup
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast

class MainActivity : Activity() {

    private lateinit var modeGroup: RadioGroup
    private lateinit var rbWifi: View
    private lateinit var rbBt: View
    private lateinit var wifiBox: View
    private lateinit var btBox: View
    private lateinit var host: EditText
    private lateinit var port: EditText
    private lateinit var btnPick: Button
    private lateinit var tvBt: TextView
    private lateinit var toggle: Button
    private lateinit var status: TextView
    private lateinit var readout: TextView
    private lateinit var dotsTarget: RadioGroup
    private lateinit var rbLaptop: View
    private lateinit var rbPhone: View
    private lateinit var laptopBox: View
    private lateinit var phoneBox: View
    private lateinit var btnOverlay: Button
    private lateinit var seekStrength: SeekBar
    private lateinit var seekSize: SeekBar
    private lateinit var rgColor: RadioGroup
    private lateinit var cbAutoHide: CheckBox
    private lateinit var tvStrengthVal: TextView
    private lateinit var tvSizeVal: TextView
    private lateinit var tvOverlayPerm: TextView
    private var pendingOverlayStart = false
    private val ui = Handler(Looper.getMainLooper())

    private fun prefs() = getSharedPreferences("steady", Context.MODE_PRIVATE)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        modeGroup = findViewById(R.id.modeGroup)
        rbWifi = findViewById(R.id.rbWifi)
        rbBt = findViewById(R.id.rbBt)
        wifiBox = findViewById(R.id.wifiBox)
        btBox = findViewById(R.id.btBox)
        host = findViewById(R.id.host)
        port = findViewById(R.id.port)
        btnPick = findViewById(R.id.btnPick)
        tvBt = findViewById(R.id.tvBt)
        toggle = findViewById(R.id.toggle)
        status = findViewById(R.id.status)
        readout = findViewById(R.id.readout)
        dotsTarget = findViewById(R.id.dotsTarget)
        rbLaptop = findViewById(R.id.rbLaptop)
        rbPhone = findViewById(R.id.rbPhone)
        laptopBox = findViewById(R.id.laptopBox)
        phoneBox = findViewById(R.id.phoneBox)
        btnOverlay = findViewById(R.id.btnOverlay)
        seekStrength = findViewById(R.id.seekStrength)
        seekSize = findViewById(R.id.seekSize)
        rgColor = findViewById(R.id.rgDotColor)
        cbAutoHide = findViewById(R.id.cbAutoHide)
        tvStrengthVal = findViewById(R.id.tvStrengthVal)
        tvSizeVal = findViewById(R.id.tvSizeVal)
        tvOverlayPerm = findViewById(R.id.tvOverlayPerm)

        val p = prefs()
        host.setText(p.getString("host", ""))
        port.setText(p.getString("port", "8443"))
        tvBt.text = p.getString("btName", null)?.let { "PC: $it" } ?: "(none chosen)"
        val bt = p.getString("mode", "bt") == "bt"   // Bluetooth is the default
        (if (bt) findViewById<View>(R.id.rbBt) else findViewById<View>(R.id.rbWifi))
            .let { (it as android.widget.RadioButton).isChecked = true }
        applyMode(bt)

        // Laptop (stream sensor to PC) vs Phone (draw dots on this phone) — Laptop is the default
        val phoneDots = p.getString("dots", "laptop") == "phone"
        (if (phoneDots) rbPhone else rbLaptop).let { (it as android.widget.RadioButton).isChecked = true }
        applyDotsTarget(phoneDots)
        dotsTarget.setOnCheckedChangeListener { _, id ->
            val phone = id == R.id.rbPhone
            applyDotsTarget(phone)
            p.edit().putString("dots", if (phone) "phone" else "laptop").apply()
        }
        bindPhoneSettings()
        btnOverlay.setOnClickListener { onOverlayToggle() }

        requestPerms()

        modeGroup.setOnCheckedChangeListener { _, id ->
            val useBt = id == R.id.rbBt
            applyMode(useBt)
            p.edit().putString("mode", if (useBt) "bt" else "wifi").apply()
        }
        btnPick.setOnClickListener { pickDevice() }
        toggle.setOnClickListener { onToggle() }
        handleDeepLink(intent)
        tick()
    }

    override fun onNewIntent(intent: Intent?) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleDeepLink(intent)
    }

    // steady://connect?host=<ip>&port=<port> from the QR/link: switch to WiFi and prefill
    private fun handleDeepLink(intent: Intent?) {
        val data = intent?.data ?: return
        if (data.scheme != "steady") return
        setIntent(Intent())                       // one-shot: don't re-apply on rotation/recreate
        if (SensorService.running) { toast("Stop streaming first to switch to WiFi"); return }
        val h = (data.getQueryParameter("host") ?: "").trim()
        if (h.isEmpty()) return
        val pt = (data.getQueryParameter("port") ?: "8443").trim().ifEmpty { "8443" }
        (rbWifi as android.widget.RadioButton).isChecked = true   // fires listener -> mode=wifi
        applyMode(false)
        host.setText(h); port.setText(pt)
        prefs().edit().putString("mode", "wifi").putString("host", h).putString("port", pt).apply()
        toast("WiFi address filled — tap Start")
    }

    private fun applyMode(bt: Boolean) {
        wifiBox.visibility = if (bt) View.GONE else View.VISIBLE
        btBox.visibility = if (bt) View.VISIBLE else View.GONE
    }

    private fun applyDotsTarget(phone: Boolean) {
        laptopBox.visibility = if (phone) View.GONE else View.VISIBLE
        phoneBox.visibility = if (phone) View.VISIBLE else View.GONE
    }

    // --- Phone-mode overlay settings (write through SettingsStore; running overlay updates live) ---
    private fun bindPhoneSettings() {
        val str = SettingsStore.strength(this)
        val sz = SettingsStore.dotSize(this)
        seekStrength.max = 570; seekStrength.progress = ((str - 0.3f) * 100).toInt().coerceIn(0, 570)
        seekSize.max = 260; seekSize.progress = ((sz - 0.4f) * 100).toInt().coerceIn(0, 260)
        tvStrengthVal.text = String.format(java.util.Locale.US, "%.1f×", str)
        tvSizeVal.text = String.format(java.util.Locale.US, "%.1f×", sz)
        seekStrength.setOnSeekBarChangeListener(simpleSeek { prog ->
            val v = 0.3f + prog / 100f
            SettingsStore.setStrength(this, v)
            tvStrengthVal.text = String.format(java.util.Locale.US, "%.1f×", v)
        })
        seekSize.setOnSeekBarChangeListener(simpleSeek { prog ->
            val v = 0.4f + prog / 100f
            SettingsStore.setDotSize(this, v)
            tvSizeVal.text = String.format(java.util.Locale.US, "%.1f×", v)
        })
        rgColor.check(when (SettingsStore.dotColor(this)) { 1 -> R.id.rbMixed; 2 -> R.id.rbDark; else -> R.id.rbLight })
        rgColor.setOnCheckedChangeListener { _, id ->
            SettingsStore.setDotColor(this, when (id) { R.id.rbMixed -> 1; R.id.rbDark -> 2; else -> 0 })
        }
        cbAutoHide.isChecked = SettingsStore.autoHide(this)
        cbAutoHide.setOnCheckedChangeListener { _, on -> SettingsStore.setAutoHide(this, on) }
    }

    private inline fun simpleSeek(crossinline onChange: (Int) -> Unit) =
        object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar, p: Int, fromUser: Boolean) { if (fromUser) onChange(p) }
            override fun onStartTrackingTouch(sb: SeekBar) {}
            override fun onStopTrackingTouch(sb: SeekBar) {}
        }

    private fun onOverlayToggle() {
        if (OverlayService.running) {
            stopService(Intent(this, OverlayService::class.java))
            return
        }
        if (!android.provider.Settings.canDrawOverlays(this)) { requestOverlayPermission(); return }
        // Lazily ask for location (Phone mode only) — enables the GPS "fade when parked" gate; the
        // overlay works fully without it. Don't block the start on the result.
        if (checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.ACCESS_FINE_LOCATION,
                Manifest.permission.ACCESS_COARSE_LOCATION), 2)
        startForegroundService(Intent(this, OverlayService::class.java))
    }

    private fun requestOverlayPermission() {
        pendingOverlayStart = true                 // auto-start on return if granted (onResume)
        toast("Allow Steady to draw over other apps, then come back")
        startActivity(Intent(android.provider.Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
            android.net.Uri.parse("package:$packageName")))
    }

    override fun onResume() {
        super.onResume()
        if (pendingOverlayStart) {
            pendingOverlayStart = false
            if (android.provider.Settings.canDrawOverlays(this) && !OverlayService.running)
                startForegroundService(Intent(this, OverlayService::class.java))
        }
    }

    private fun requestPerms() {
        val want = ArrayList<String>()
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            want.add(Manifest.permission.POST_NOTIFICATIONS)
        if (Build.VERSION.SDK_INT >= 31 &&
            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED)
            want.add(Manifest.permission.BLUETOOTH_CONNECT)
        // NOTE: location is requested lazily in onOverlayToggle (Phone mode only) so laptop-streaming
        // users are never prompted and their PC auto-hide gate is unchanged.
        if (want.isNotEmpty()) requestPermissions(want.toTypedArray(), 1)
    }

    private fun hasBtPerm(): Boolean =
        Build.VERSION.SDK_INT < 31 ||
            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED

    private fun pickDevice() {
        if (!hasBtPerm()) { requestPerms(); toast("Grant the Bluetooth permission, then tap again"); return }
        val adapter = (getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager).adapter
        if (adapter == null || !adapter.isEnabled) { toast("Turn on Bluetooth first"); return }
        val devices = try { adapter.bondedDevices.toList() } catch (e: SecurityException) { emptyList() }
        if (devices.isEmpty()) { toast("Pair the laptop in Android Bluetooth settings first"); return }
        val names = devices.map { (it.name ?: it.address) }.toTypedArray()
        AlertDialog.Builder(this)
            .setTitle("Pick paired PC")
            .setItems(names) { _, i ->
                val d = devices[i]
                prefs().edit().putString("btMac", d.address).putString("btName", names[i]).apply()
                tvBt.text = "PC: ${names[i]}"
            }
            .show()
    }

    private fun onToggle() {
        if (SensorService.running) {
            stopService(Intent(this, SensorService::class.java))
            return
        }
        val useBt = modeGroup.checkedRadioButtonId == R.id.rbBt
        val p = prefs()
        val i = Intent(this, SensorService::class.java).putExtra("mode", if (useBt) "bt" else "wifi")
        if (useBt) {
            val mac = p.getString("btMac", "") ?: ""
            if (mac.isEmpty()) { toast("Choose the paired PC first"); return }
            if (!hasBtPerm()) { requestPerms(); return }
            i.putExtra("btMac", mac)
        } else {
            val h = host.text.toString().trim()
            val pt = port.text.toString().trim().ifEmpty { "8443" }
            if (h.isEmpty()) { toast("Enter the PC address"); return }
            p.edit().putString("host", h).putString("port", pt).apply()
            i.putExtra("host", h).putExtra("port", pt.toIntOrNull() ?: 8443)
        }
        // persist the mode actually launched, so a START_STICKY restart recovers the right transport
        p.edit().putString("mode", if (useBt) "bt" else "wifi").apply()
        startForegroundService(i)
    }

    private fun tick() {
        val running = SensorService.running
        toggle.text = if (running) "Stop streaming" else "Start streaming"
        toggle.setBackgroundResource(if (running) R.drawable.btn_secondary else R.drawable.btn_primary)
        toggle.setTextColor(getColor(if (running) R.color.accent else R.color.bg))
        status.text = if (running) SensorService.statusLine else "stopped"
        readout.text = SensorService.readout
        val en = !running
        // setEnabled on the RadioGroup doesn't reach its children — disable the buttons directly
        rbWifi.isEnabled = en; rbBt.isEnabled = en
        host.isEnabled = en; port.isEnabled = en; btnPick.isEnabled = en
        // don't let the Laptop/Phone switch hide the Stop button mid-stream
        rbLaptop.isEnabled = en; rbPhone.isEnabled = en

        // phone-mode overlay button + permission notice
        val ov = OverlayService.running
        btnOverlay.text = if (ov) "Stop dots overlay" else "Start dots overlay"
        btnOverlay.setBackgroundResource(if (ov) R.drawable.btn_secondary else R.drawable.btn_primary)
        btnOverlay.setTextColor(getColor(if (ov) R.color.accent else R.color.bg))
        tvOverlayPerm.visibility =
            if (!ov && !android.provider.Settings.canDrawOverlays(this)) View.VISIBLE else View.GONE

        ui.postDelayed({ tick() }, 200)
    }

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()

    override fun onDestroy() {
        ui.removeCallbacksAndMessages(null)
        super.onDestroy()
    }
}
