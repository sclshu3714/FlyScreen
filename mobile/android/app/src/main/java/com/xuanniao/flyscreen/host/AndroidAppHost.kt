package com.xuanniao.flyscreen.host

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import android.provider.Settings
import com.xuanniao.flyscreen.net.ConnectionManager
import com.xuanniao.flyscreen.protocol.MessageCodec
import com.xuanniao.flyscreen.protocol.MessageType
import com.xuanniao.flyscreen.protocol.MouseButtonPayload
import com.xuanniao.flyscreen.protocol.MouseMovePayload
import com.xuanniao.flyscreen.protocol.MouseWheelPayload
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import java.net.Inet4Address
import java.net.NetworkInterface
import kotlin.math.roundToInt

/**
 * Android 业务宿主：发现 / 握手 / 触控板输入。UI 与前台服务共用。
 */
class AndroidAppHost private constructor(context: Context) {
    private val appContext = context.applicationContext
    private val prefs = AppPreferences(appContext)
    private val ioScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    val deviceClass: String = DeviceClassDetector.detect(appContext)
    val localMachine: String = buildLocalName(appContext)

    val connections = ConnectionManager(
        scope = ioScope,
        securityKey = prefs.securityKey.ifBlank { "changeme" },
        localMachine = localMachine,
        deviceClass = deviceClass
    )

    var onStatus: ((String) -> Unit)? = null
        set(value) {
            field = value
            connections.onStatus = value
        }

    var onPeersUpdated: (() -> Unit)? = null
        set(value) {
            field = value
            connections.onPeersUpdated = value
        }

    init {
        connections.onStatus = { onStatus?.invoke(it) }
        connections.onPeersUpdated = { onPeersUpdated?.invoke() }
    }

    fun securityKey(): String = prefs.securityKey
    fun targetPeer(): String = prefs.targetPeer

    fun start() {
        val ip = resolveLocalIpv4() ?: "0.0.0.0"
        connections.start(ip)
        val target = prefs.targetPeer
        if (target.isNotBlank()) {
            connections.startReconnectLoop(target) {
                connections.discovery.getPeers()
                    .firstOrNull { it.machineName.equals(target, true) }?.ip
            }
        }
    }

    fun applyAndConnect(key: String, peer: String) {
        prefs.securityKey = key.trim()
        prefs.targetPeer = peer.trim()
        connections.securityKey = prefs.securityKey
        connections.stop()
        start()
        val discovered = connections.discovery.getPeers()
            .firstOrNull { it.machineName.equals(peer, true) }
        if (discovered != null) {
            connections.connectTo(discovered.machineName, discovered.ip, discovered.tcpPort)
        } else if (peer.isNotBlank()) {
            onStatus?.invoke("已保存；等待发现 $peer …")
            connections.startReconnectLoop(peer) {
                connections.discovery.getPeers()
                    .firstOrNull { it.machineName.equals(peer, true) }?.ip
            }
        }
    }

    fun sendMoveRelative(dx: Float, dy: Float) {
        val payload = MouseMovePayload(
            relative = true,
            dx = dx.roundToInt(),
            dy = dy.roundToInt()
        )
        connections.sendToActive(MessageCodec.encodeJson(MessageType.MouseMove, payload))
    }

    fun sendClick(button: Int) {
        val down = MouseButtonPayload(button, true)
        val up = MouseButtonPayload(button, false)
        connections.sendToActive(MessageCodec.encodeJson(MessageType.MouseButton, down))
        connections.sendToActive(MessageCodec.encodeJson(MessageType.MouseButton, up))
    }

    fun sendWheel(delta: Int) {
        val payload = MouseWheelPayload(delta = delta, horizontal = false)
        connections.sendToActive(MessageCodec.encodeJson(MessageType.MouseWheel, payload))
    }

    fun discoveredPeerNames(): List<String> =
        connections.discovery.getPeers().map { it.machineName }

    fun dispose() {
        connections.stop()
        ioScope.cancel()
    }

    private fun resolveLocalIpv4(): String? {
        try {
            val wifi = appContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            @Suppress("DEPRECATION")
            val ipInt = wifi?.connectionInfo?.ipAddress ?: 0
            if (ipInt != 0) {
                return String.format(
                    "%d.%d.%d.%d",
                    ipInt and 0xff,
                    ipInt shr 8 and 0xff,
                    ipInt shr 16 and 0xff,
                    ipInt shr 24 and 0xff
                )
            }
        } catch (_: Exception) {
        }
        return try {
            NetworkInterface.getNetworkInterfaces()?.toList()
                ?.flatMap { it.inetAddresses.toList() }
                ?.firstOrNull { !it.isLoopbackAddress && it is Inet4Address }
                ?.hostAddress
        } catch (_: Exception) {
            null
        }
    }

    companion object {
        @Volatile private var instance: AndroidAppHost? = null

        fun get(context: Context): AndroidAppHost {
            return instance ?: synchronized(this) {
                instance ?: AndroidAppHost(context).also { instance = it }
            }
        }

        private fun buildLocalName(context: Context): String {
            val model = Build.MODEL?.replace(Regex("[^A-Za-z0-9\\-]"), "-")?.take(24)
            val id = try {
                Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID)
                    ?.takeLast(4)
            } catch (_: Exception) {
                null
            }
            return listOfNotNull(model, id).joinToString("-").ifBlank { "Android-Phone" }
        }
    }
}
