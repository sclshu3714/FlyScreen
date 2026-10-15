package com.xuanniao.flyscreen.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.ArrayAdapter
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.xuanniao.flyscreen.databinding.ActivityMainBinding
import com.xuanniao.flyscreen.host.AndroidAppHost
import com.xuanniao.flyscreen.service.FlyScreenForegroundService

class MainActivity : AppCompatActivity() {
    private lateinit var binding: ActivityMainBinding
    private lateinit var host: AndroidAppHost

    private val notifPermission = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { /* optional */ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        host = AndroidAppHost.get(this)
        binding.deviceClassText.text =
            "本机 ${host.localMachine} · DeviceClass=${host.deviceClass} · Caps=AbsPointer,ClipboardText,DirectTransfer"
        binding.keyInput.setText(host.securityKey())
        binding.peerInput.setText(host.targetPeer())

        host.onStatus = { msg -> runOnUiThread { binding.statusText.text = msg } }
        host.onPeersUpdated = { runOnUiThread { refreshPeerSpinner() } }

        binding.touchpad.onMove = { dx, dy -> host.sendMoveRelative(dx, dy) }
        binding.touchpad.onWheel = { d -> host.sendWheel(d) }
        binding.touchpad.onTap = { host.sendClick(0) }
        binding.leftClick.setOnClickListener { host.sendClick(0) }
        binding.rightClick.setOnClickListener { host.sendClick(1) }

        binding.applyButton.setOnClickListener {
            val peer = binding.peerInput.text?.toString()?.trim().orEmpty()
                .ifBlank { binding.peerSpinner.selectedItem?.toString().orEmpty() }
            host.applyAndConnect(binding.keyInput.text?.toString().orEmpty(), peer)
            startKeepAliveService()
        }

        ensureNotificationPermission()
        host.start()
        startKeepAliveService()
        refreshPeerSpinner()
    }

    private fun refreshPeerSpinner() {
        val names = host.discoveredPeerNames()
        val adapter = ArrayAdapter(
            this,
            android.R.layout.simple_spinner_dropdown_item,
            if (names.isEmpty()) listOf("(尚未发现电脑)") else names
        )
        binding.peerSpinner.adapter = adapter
        binding.peerSpinner.setOnItemClickListenerCompat { name ->
            if (!name.startsWith("(")) binding.peerInput.setText(name)
        }
    }

    private fun startKeepAliveService() {
        val intent = Intent(this, FlyScreenForegroundService::class.java)
        ContextCompat.startForegroundService(this, intent)
    }

    private fun ensureNotificationPermission() {
        if (Build.VERSION.SDK_INT >= 33) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED
            ) {
                notifPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }
}

/** Spinner 无标准 item click；选中后同步到输入框。 */
private fun android.widget.Spinner.setOnItemClickListenerCompat(block: (String) -> Unit) {
    onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
        override fun onItemSelected(
            parent: android.widget.AdapterView<*>?,
            view: android.view.View?,
            position: Int,
            id: Long
        ) {
            block(parent?.getItemAtPosition(position)?.toString().orEmpty())
        }

        override fun onNothingSelected(parent: android.widget.AdapterView<*>?) {}
    }
}
