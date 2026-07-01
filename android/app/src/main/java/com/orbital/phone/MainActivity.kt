package com.orbital.phone

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
import android.view.HapticFeedbackConstants
import android.view.View
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.SeekBar
import android.widget.Spinner
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
    private lateinit var cbLaptop: CheckBox
    private lateinit var cbPhone: CheckBox
    private lateinit var laptopBox: View
    private lateinit var phoneBox: View
    private lateinit var btnOverlay: Button
    private lateinit var seekStrength: SeekBar
    private lateinit var seekLon: SeekBar
    private lateinit var seekGrade: SeekBar
    private lateinit var tvGradeVal: TextView
    private lateinit var cbFlipV: CheckBox
    private lateinit var cbFlipGrade: CheckBox
    private lateinit var cbFlipH: CheckBox
    private lateinit var cbSwap: CheckBox
    private lateinit var spSeat: Spinner
    private lateinit var seekSize: SeekBar
    private lateinit var rgColor: RadioGroup
    private lateinit var cbAutoHide: CheckBox
    private lateinit var tvStrengthVal: TextView
    private lateinit var tvLonVal: TextView
    private lateinit var tvSizeVal: TextView
    private lateinit var tvOverlayPerm: TextView
    private lateinit var advancedToggle: TextView
    private lateinit var advancedBox: View
    // --- new customization surface ---
    private lateinit var dotsPreview: DotsView
    private lateinit var rgPreset: RadioGroup
    private lateinit var customBox: View
    private lateinit var seekOpacity: SeekBar
    private lateinit var tvOpacityVal: TextView
    private lateinit var seekDensity: SeekBar
    private lateinit var tvDensityVal: TextView
    private lateinit var rgCueModel: RadioGroup
    private lateinit var rgPlacement: RadioGroup
    private lateinit var seekDecay: SeekBar
    private lateinit var tvDecayVal: TextView
    private lateinit var seekHide: SeekBar
    private lateinit var tvHideVal: TextView
    private lateinit var btnReset: Button
    private lateinit var spSim: Spinner
    private lateinit var accentRow: View
    private lateinit var styleChips: List<TextView>
    private lateinit var accentChips: List<TextView>
    private var pendingOverlayStart = false
    private val ui = Handler(Looper.getMainLooper())

    // Google Play Billing — gates remote streaming (SensorService) behind the subscription.
    private lateinit var billing: BillingManager
    private var wasUnlocked = false
    private var paywall: AlertDialog? = null       // shown paywall, so it can rebuild/dismiss as billing loads
    private var paywallWasEmpty = false            // the shown paywall rendered the "plans couldn't load" state

    private fun prefs() = getSharedPreferences("orbit", Context.MODE_PRIVATE)

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
        cbLaptop = findViewById(R.id.cbLaptop)
        cbPhone = findViewById(R.id.cbPhone)
        laptopBox = findViewById(R.id.laptopBox)
        phoneBox = findViewById(R.id.phoneBox)
        btnOverlay = findViewById(R.id.btnOverlay)
        seekStrength = findViewById(R.id.seekStrength)
        seekLon = findViewById(R.id.seekLon)
        seekGrade = findViewById(R.id.seekGrade)
        tvGradeVal = findViewById(R.id.tvGradeVal)
        cbFlipV = findViewById(R.id.cbFlipV)
        cbFlipGrade = findViewById(R.id.cbFlipGrade)
        cbFlipH = findViewById(R.id.cbFlipH)
        cbSwap = findViewById(R.id.cbSwap)
        spSeat = findViewById(R.id.spSeat)
        seekSize = findViewById(R.id.seekSize)
        rgColor = findViewById(R.id.rgDotColor)
        cbAutoHide = findViewById(R.id.cbAutoHide)
        tvStrengthVal = findViewById(R.id.tvStrengthVal)
        tvLonVal = findViewById(R.id.tvLonVal)
        tvSizeVal = findViewById(R.id.tvSizeVal)
        tvOverlayPerm = findViewById(R.id.tvOverlayPerm)
        advancedToggle = findViewById(R.id.advancedToggle)
        advancedBox = findViewById(R.id.advancedBox)
        dotsPreview = findViewById(R.id.dotsPreview)
        rgPreset = findViewById(R.id.rgPreset)
        customBox = findViewById(R.id.customBox)
        seekOpacity = findViewById(R.id.seekOpacity)
        tvOpacityVal = findViewById(R.id.tvOpacityVal)
        seekDensity = findViewById(R.id.seekDensity)
        tvDensityVal = findViewById(R.id.tvDensityVal)
        rgCueModel = findViewById(R.id.rgCueModel)
        rgPlacement = findViewById(R.id.rgPlacement)
        seekDecay = findViewById(R.id.seekDecay)
        tvDecayVal = findViewById(R.id.tvDecayVal)
        seekHide = findViewById(R.id.seekHide)
        tvHideVal = findViewById(R.id.tvHideVal)
        btnReset = findViewById(R.id.btnReset)
        spSim = findViewById(R.id.spSim)
        accentRow = findViewById(R.id.accentRow)
        styleChips = listOf(
            findViewById(R.id.styleDots), findViewById(R.id.styleStreaks), findViewById(R.id.styleRails),
            findViewById(R.id.styleHorizon), findViewById(R.id.styleGrid), findViewById(R.id.styleChevrons))
        accentChips = listOf(
            findViewById(R.id.chipAccentTeal), findViewById(R.id.chipAccentAmber), findViewById(R.id.chipAccentBlue))
        dotsPreview.setDemoMode(true)
        dotsPreview.applyParams(SettingsStore.snapshot(this))
        advancedToggle.setOnClickListener {
            val show = advancedBox.visibility != View.VISIBLE
            advancedBox.visibility = if (show) View.VISIBLE else View.GONE
            advancedToggle.text = if (show) "Advanced ▴" else "Advanced ▾"
        }

        val p = prefs()
        host.setText(p.getString("host", ""))
        port.setText(p.getString("port", "8443"))
        tvBt.text = p.getString("btName", null)?.let { "PC: $it" } ?: "(none chosen)"
        val bt = p.getString("mode", "bt") == "bt"   // Bluetooth is the default
        (if (bt) findViewById<View>(R.id.rbBt) else findViewById<View>(R.id.rbWifi))
            .let { (it as android.widget.RadioButton).isChecked = true }
        applyMode(bt)

        // Stream-to-laptop and cue-on-this-phone are now INDEPENDENT — either or both. (Old single
        // "dots" choice migrates: phone-only -> phone on / laptop off; anything else -> laptop on.)
        val legacy = p.getString("dots", null)
        val wantLaptop = p.getBoolean("dotsLaptop", legacy != "phone")
        val wantPhone = p.getBoolean("dotsPhone", legacy == "phone")
        cbLaptop.isChecked = wantLaptop
        cbPhone.isChecked = wantPhone
        applyDotsBoxes()
        cbLaptop.setOnCheckedChangeListener { _, on ->
            p.edit().putBoolean("dotsLaptop", on).apply(); applyDotsBoxes()
        }
        cbPhone.setOnCheckedChangeListener { _, on ->
            p.edit().putBoolean("dotsPhone", on).apply(); applyDotsBoxes()
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

        wasUnlocked = Entitlements.isRemoteUnlocked(this)
        billing = BillingManager(this) { onEntitlementChanged() }
        billing.start()

        handleDeepLink(intent)
        tick()
    }

    /** Play billing pushed a fresh entitlement/plan state (main thread). Nudge the user when a purchase
     *  just unlocked streaming; tick() reflects the locked/unlocked status otherwise. */
    private fun onEntitlementChanged() {
        val unlocked = Entitlements.isRemoteUnlocked(this)
        if (unlocked && !wasUnlocked) toast("Orbital Premium active — tap Start streaming")
        wasUnlocked = unlocked
        // Keep a shown paywall live: dismiss it once purchased, or rebuild it once plans finally load
        // (so its Retry state doesn't get stuck after billing connects).
        paywall?.let { dlg ->
            if (unlocked) dlg.dismiss()
            else if (paywallWasEmpty && billing.plans().isNotEmpty()) { dlg.dismiss(); showPaywall() }
        }
    }

    override fun onNewIntent(intent: Intent?) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleDeepLink(intent)
    }

    // orbit://connect?host=<ip>&port=<port> from the QR/link: switch to WiFi and prefill
    private fun handleDeepLink(intent: Intent?) {
        val data = intent?.data ?: return
        if (data.scheme != "orbit") return
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

    private fun applyDotsBoxes() {
        laptopBox.visibility = if (cbLaptop.isChecked) View.VISIBLE else View.GONE
        phoneBox.visibility = if (cbPhone.isChecked) View.VISIBLE else View.GONE
        if (cbPhone.isChecked) dotsPreview.start() else dotsPreview.stop()   // live preview only when the phone section is shown
    }

    private fun refreshPreview() { dotsPreview.applyParams(SettingsStore.snapshot(this)) }
    private fun haptic(v: View) = v.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)

    private fun rebindFineSliders() {
        val L = java.util.Locale.US
        val str = SettingsStore.strength(this)
        seekStrength.progress = ((str - 0.3f) * 100).toInt().coerceIn(0, 570)
        tvStrengthVal.text = String.format(L, "%.1f×", str)
        val op = SettingsStore.opacity(this)
        seekOpacity.progress = ((op - 0.2f) * 100).toInt().coerceIn(0, 80)
        tvOpacityVal.text = "${(op * 100).toInt()}%"
        val den = SettingsStore.density(this)
        seekDensity.progress = ((den - 0.5f) * 100).toInt().coerceIn(0, 150)
        tvDensityVal.text = String.format(L, "%.1f×", den)
    }

    // --- Phone-mode overlay settings (write through SettingsStore; running overlay + preview update live) ---
    private fun bindPhoneSettings() {
        val L = java.util.Locale.US

        // ---- Cue style gallery ----
        fun highlightStyle(sel: Int) = styleChips.forEachIndexed { i, v -> v.isSelected = i == sel }
        highlightStyle(SettingsStore.cueStyle(this))
        styleChips.forEachIndexed { i, v ->
            v.setOnClickListener { SettingsStore.setCueStyle(this, i); highlightStyle(i); refreshPreview(); haptic(v) }
        }

        // ---- Feel presets (write the existing fine keys; reveal sliders only under Custom) ----
        val preset = SettingsStore.preset(this)
        customBox.visibility = if (preset == 3) View.VISIBLE else View.GONE
        rgPreset.setOnCheckedChangeListener(null)
        rgPreset.check(when (preset) { 0 -> R.id.rbPresetCalm; 2 -> R.id.rbPresetStrong; 3 -> R.id.rbPresetCustom; else -> R.id.rbPresetBalanced })
        rgPreset.setOnCheckedChangeListener { _, id ->
            val pr = when (id) { R.id.rbPresetCalm -> 0; R.id.rbPresetStrong -> 2; R.id.rbPresetCustom -> 3; else -> 1 }
            if (pr == 3) { SettingsStore.setPreset(this, 3); customBox.visibility = View.VISIBLE }
            else { SettingsStore.applyPreset(this, pr); customBox.visibility = View.GONE; rebindFineSliders() }
            refreshPreview()
        }

        // ---- Fine sliders (Movement / Opacity / Density), shown under Custom ----
        seekStrength.max = 570; seekOpacity.max = 80; seekDensity.max = 150
        rebindFineSliders()
        seekStrength.setOnSeekBarChangeListener(simpleSeek { prog ->
            val v = 0.3f + prog / 100f; SettingsStore.setStrength(this, v)
            tvStrengthVal.text = String.format(L, "%.1f×", v); refreshPreview()
        })
        seekOpacity.setOnSeekBarChangeListener(simpleSeek { prog ->
            val v = (0.2f + prog / 100f).coerceIn(0.2f, 1.0f); SettingsStore.setOpacity(this, v)
            tvOpacityVal.text = "${(v * 100).toInt()}%"; refreshPreview()
        })
        seekDensity.setOnSeekBarChangeListener(simpleSeek { prog ->
            val v = (0.5f + prog / 100f).coerceIn(0.5f, 2.0f); SettingsStore.setDensity(this, v)
            tvDensityVal.text = String.format(L, "%.1f×", v); refreshPreview()
        })

        // ---- Appearance: size ----
        val sz = SettingsStore.dotSize(this)
        seekSize.max = 260; seekSize.progress = ((sz - 0.4f) * 100).toInt().coerceIn(0, 260)
        tvSizeVal.text = String.format(L, "%.1f×", sz)
        seekSize.setOnSeekBarChangeListener(simpleSeek { prog ->
            val v = 0.4f + prog / 100f; SettingsStore.setDotSize(this, v)
            tvSizeVal.text = String.format(L, "%.1f×", v); refreshPreview()
        })

        // ---- Appearance: colour (+ custom accent swatches) ----
        val teal = getColor(R.color.accent); val amber = getColor(R.color.accent_amber); val blue = getColor(R.color.accent_blue)
        val accentVals = listOf(teal, amber, blue)
        fun highlightAccent() { val a = SettingsStore.accentColor(this); accentChips.forEachIndexed { i, v -> v.isSelected = a == accentVals[i] } }
        val colInit = SettingsStore.dotColor(this)
        accentRow.visibility = if (colInit == 3) View.VISIBLE else View.GONE
        highlightAccent()
        rgColor.setOnCheckedChangeListener(null)
        rgColor.check(when (colInit) { 1 -> R.id.rbMixed; 2 -> R.id.rbDark; 3 -> R.id.rbColorCustom; else -> R.id.rbLight })
        rgColor.setOnCheckedChangeListener { _, id ->
            val c = when (id) { R.id.rbMixed -> 1; R.id.rbDark -> 2; R.id.rbColorCustom -> 3; else -> 0 }
            if (c == 3 && SettingsStore.accentColor(this) == 0) SettingsStore.setAccentColor(this, teal)
            SettingsStore.setDotColor(this, c)
            accentRow.visibility = if (c == 3) View.VISIBLE else View.GONE
            highlightAccent(); refreshPreview()
        }
        accentChips.forEachIndexed { i, v ->
            v.setOnClickListener {
                SettingsStore.setAccentColor(this, accentVals[i]); SettingsStore.setDotColor(this, 3)
                rgColor.check(R.id.rbColorCustom); accentRow.visibility = View.VISIBLE
                highlightAccent(); refreshPreview(); haptic(v)
            }
        }

        // ---- Comfort ----
        cbAutoHide.setOnCheckedChangeListener(null)
        cbAutoHide.isChecked = SettingsStore.autoHide(this)
        cbAutoHide.setOnCheckedChangeListener { _, on -> SettingsStore.setAutoHide(this, on); refreshPreview() }

        // ---- Advanced: accel/brake (SIGNED -4..4; centre = off, left = reversed — mirrors Windows) ----
        val lon = SettingsStore.lonGain(this)
        seekLon.max = 80; seekLon.progress = ((lon + 4f) * 10f).toInt().coerceIn(0, 80)
        tvLonVal.text = fmtGrade(lon)
        seekLon.setOnSeekBarChangeListener(simpleSeek { prog ->
            val v = prog / 10f - 4f; SettingsStore.setLonGain(this, v); tvLonVal.text = fmtGrade(v); refreshPreview()
        })

        // ---- Advanced: hill / grade (SIGNED -4..4; centre = off, left = reversed) ----
        val grade = SettingsStore.gradeGain(this)
        seekGrade.max = 80; seekGrade.progress = ((grade + 4f) * 10f).toInt().coerceIn(0, 80)
        tvGradeVal.text = fmtGrade(grade)
        seekGrade.setOnSeekBarChangeListener(simpleSeek { prog ->
            val v = prog / 10f - 4f; SettingsStore.setGradeGain(this, v); tvGradeVal.text = fmtGrade(v); refreshPreview()
        })

        // ---- Advanced: manual direction overrides (Flip ↕ / Flip ↔ / Swap) ----
        cbFlipV.setOnCheckedChangeListener(null)
        cbFlipV.isChecked = SettingsStore.flipV(this)
        cbFlipV.setOnCheckedChangeListener { _, on -> SettingsStore.setFlipV(this, on); refreshPreview() }
        cbFlipGrade.setOnCheckedChangeListener(null)
        cbFlipGrade.isChecked = SettingsStore.flipGrade(this)
        cbFlipGrade.setOnCheckedChangeListener { _, on -> SettingsStore.setFlipGrade(this, on); refreshPreview() }
        cbFlipH.setOnCheckedChangeListener(null)
        cbFlipH.isChecked = SettingsStore.flipH(this)
        cbFlipH.setOnCheckedChangeListener { _, on -> SettingsStore.setFlipH(this, on); refreshPreview() }
        cbSwap.setOnCheckedChangeListener(null)
        cbSwap.isChecked = SettingsStore.swap(this)
        cbSwap.setOnCheckedChangeListener { _, on -> SettingsStore.setSwap(this, on); refreshPreview() }

        // ---- Advanced: cue motion model / coverage ----
        rgCueModel.setOnCheckedChangeListener(null)
        rgCueModel.check(if (SettingsStore.cueModel(this) == 1) R.id.rbModelPulse else R.id.rbModelFlow)
        rgCueModel.setOnCheckedChangeListener { _, id -> SettingsStore.setCueModel(this, if (id == R.id.rbModelPulse) 1 else 0); refreshPreview() }
        rgPlacement.setOnCheckedChangeListener(null)
        rgPlacement.check(if (SettingsStore.placement(this) == 1) R.id.rbPlaceFull else R.id.rbPlaceStrips)
        rgPlacement.setOnCheckedChangeListener { _, id -> SettingsStore.setPlacement(this, if (id == R.id.rbPlaceFull) 1 else 0); refreshPreview() }

        // ---- Advanced: smoothness / hide-sensitivity ----
        val dec = SettingsStore.decay(this)
        seekDecay.max = 17; seekDecay.progress = ((dec - 0.80f) * 100).toInt().coerceIn(0, 17)
        tvDecayVal.text = String.format(L, "%.2f", dec)
        seekDecay.setOnSeekBarChangeListener(simpleSeek { prog ->
            val v = (0.80f + prog / 100f).coerceIn(0.80f, 0.97f); SettingsStore.setDecay(this, v)
            tvDecayVal.text = String.format(L, "%.2f", v); refreshPreview()
        })
        val hs = SettingsStore.hideSensitivity(this)
        seekHide.max = 150; seekHide.progress = ((hs - 0.5f) * 100).toInt().coerceIn(0, 150)
        tvHideVal.text = String.format(L, "%.1f×", hs)
        seekHide.setOnSeekBarChangeListener(simpleSeek { prog ->
            val v = (0.5f + prog / 100f).coerceIn(0.5f, 2.0f); SettingsStore.setHideSensitivity(this, v)
            tvHideVal.text = String.format(L, "%.1f×", v)   // auto-hide gate is bypassed in the demo, so no preview change
        })

        // ---- Advanced: Simulation (test the cue WITHOUT driving — synthetic motion overrides the
        // real sensors while a running overlay is up). Persisted via K_SIM_SCENARIO (in LIVE_KEYS). ----
        val simNames = listOf(
            "Off (real sensors)", "All scenarios", "Accelerate", "Brake", "Turn left",
            "Turn right", "Uphill", "Downhill",
            // Combined / conflicting cues (tilt vs accel/decel) for gross tuning — must stay in the
            // same order as MotionSimulator's ACCEL_UPHILL..COMBO scenario codes.
            "Accel + uphill", "Accel + downhill", "Brake + uphill", "Brake + downhill",
            "Combo (accel+turn+hill)")
        val simAdapter = android.widget.ArrayAdapter(this, android.R.layout.simple_spinner_item, simNames)
        simAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item)
        spSim.adapter = simAdapter
        spSim.onItemSelectedListener = null
        spSim.setSelection(SettingsStore.simScenario(this), false)   // before the listener -> no spurious write
        spSim.onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
            override fun onItemSelected(p: android.widget.AdapterView<*>?, v: View?, pos: Int, id: Long) {
                SettingsStore.setSimScenario(this@MainActivity, pos)   // OverlayService reacts live (LIVE_KEYS)
            }
            override fun onNothingSelected(p: android.widget.AdapterView<*>?) {}
        }
        // Seating orientation: applied to ANY sim scenario above (Auto / Forward / Left / Right / Rear).
        val seatNames = listOf("Auto", "Facing forward", "Facing left", "Facing right", "Facing rear")
        val seatAdapter = android.widget.ArrayAdapter(this, android.R.layout.simple_spinner_item, seatNames)
        seatAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item)
        spSeat.adapter = seatAdapter
        spSeat.onItemSelectedListener = null
        spSeat.setSelection(SettingsStore.simSeat(this), false)
        spSeat.onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
            override fun onItemSelected(p: android.widget.AdapterView<*>?, v: View?, pos: Int, id: Long) {
                SettingsStore.setSimSeat(this@MainActivity, pos)
            }
            override fun onNothingSelected(p: android.widget.AdapterView<*>?) {}
        }

        // ---- Advanced: reset (confirm first — it wipes every tuned setting) ----
        btnReset.setOnClickListener {
            haptic(it)
            AlertDialog.Builder(this)
                .setTitle("Reset to defaults?")
                .setMessage("Restores every Orbital setting on this screen to its default. This can't be undone.")
                .setPositiveButton("Reset") { _, _ ->
                    SettingsStore.resetToDefaults(this)
                    bindPhoneSettings()                 // re-read every control from defaults
                    refreshPreview()
                    toast("Reset to defaults")
                }
                .setNegativeButton("Cancel", null)
                .show()
        }
    }

    // signed trim format: "off" near centre, else "+1.0×" / "-1.0×" (sign = direction).
    // Shared by Accel/brake and Hill/grade — both are signed, independent controls (Windows parity).
    private fun fmtGrade(v: Float): String =
        if (kotlin.math.abs(v) < 0.05f) "off"
        else String.format(java.util.Locale.US, "%+.1f×", v)

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
        toast("Allow Orbital to draw over other apps, then come back")
        startActivity(Intent(android.provider.Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
            android.net.Uri.parse("package:$packageName")))
    }

    override fun onResume() {
        super.onResume()
        if (::billing.isInitialized) billing.refreshPurchases()   // re-verify sub on foreground (post-checkout, renewals)
        if (phoneBox.visibility == View.VISIBLE) dotsPreview.start()   // resume the live preview
        if (pendingOverlayStart) {
            pendingOverlayStart = false
            if (android.provider.Settings.canDrawOverlays(this) && !OverlayService.running)
                startForegroundService(Intent(this, OverlayService::class.java))
        }
    }

    override fun onPause() {
        dotsPreview.stop()   // don't burn a Choreographer loop while backgrounded
        super.onPause()
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
        // Remote streaming to the Windows app is the paid feature — gate the start behind the sub.
        // (The on-phone cue overlay and the Windows app are free and never reach here.)
        if (!Entitlements.isRemoteUnlocked(this)) { showPaywall(); return }
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

    /** Subscription paywall for remote streaming. Lists the base plans loaded from Play; if they haven't
     *  loaded (offline / not signed in / product not configured yet), offers a retry. "Restore" just
     *  re-queries Play (owned subs unlock automatically). */
    private fun showPaywall() {
        val plans = billing.plans()
        paywallWasEmpty = plans.isEmpty()
        val b = AlertDialog.Builder(this).setTitle("Orbital Premium — remote to Windows")
        if (plans.isEmpty()) {
            billing.start()   // kick a (re)connect so plans have a chance to load (onEntitlementChanged rebuilds)
            b.setMessage(
                "Streaming your phone's motion to the Windows app is a subscription.\n\n" +
                    "Plans couldn't load — check your internet, that you're signed in to Google Play, " +
                    "and try again.\n\nThe on-phone cue overlay and the Windows app are free.")
                .setPositiveButton("Retry") { _, _ -> billing.start() }   // reload product details, not just purchases
                .setNegativeButton("Close", null)
        } else {
            val labels = plans.map { "${it.label} — ${it.price}" }.toTypedArray()
            b.setItems(labels) { _, i ->
                if (!billing.launch(this, plans[i].offerToken)) toast("Couldn't start checkout — try again")
            }.setNeutralButton("Restore") { _, _ ->
                billing.refreshPurchases(); toast("Checking your subscription…")
            }.setNegativeButton("Close", null)
        }
        paywall = b.create()
        paywall!!.setOnDismissListener { paywall = null }
        paywall!!.show()
    }

    private fun tick() {
        val running = SensorService.running
        toggle.text = if (running) "Stop streaming" else "Start streaming"
        toggle.setBackgroundResource(if (running) R.drawable.btn_secondary else R.drawable.btn_primary)
        toggle.setTextColor(getColor(if (running) R.color.accent else R.color.bg))
        status.text = when {
            running -> SensorService.statusLine
            !Entitlements.isRemoteUnlocked(this) -> "stopped · remote streaming is a subscription"
            else -> "stopped"
        }
        // While the cue overlay is simulating, show the live scenario phase; else the stream readout.
        val sp = OverlayService.simPhase
        readout.text = if (OverlayService.running && sp.isNotEmpty()) "Simulating: $sp" else SensorService.readout
        val en = !running
        // setEnabled on the RadioGroup doesn't reach its children — disable the buttons directly
        rbWifi.isEnabled = en; rbBt.isEnabled = en
        host.isEnabled = en; port.isEnabled = en; btnPick.isEnabled = en
        // don't let a toggle hide a section whose Stop button is live: lock Stream while streaming,
        // lock Cue-on-phone while the overlay runs.
        cbLaptop.isEnabled = en; cbPhone.isEnabled = !OverlayService.running

        // phone-mode overlay button + permission notice
        val ov = OverlayService.running
        btnOverlay.text = if (ov) "Stop cue overlay" else "Start cue overlay"
        btnOverlay.setBackgroundResource(if (ov) R.drawable.btn_secondary else R.drawable.btn_primary)
        btnOverlay.setTextColor(getColor(if (ov) R.color.accent else R.color.bg))
        tvOverlayPerm.visibility =
            if (!ov && !android.provider.Settings.canDrawOverlays(this)) View.VISIBLE else View.GONE

        ui.postDelayed({ tick() }, 200)
    }

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()

    override fun onDestroy() {
        ui.removeCallbacksAndMessages(null)
        dotsPreview.stop()
        if (::billing.isInitialized) billing.end()
        super.onDestroy()
    }
}
