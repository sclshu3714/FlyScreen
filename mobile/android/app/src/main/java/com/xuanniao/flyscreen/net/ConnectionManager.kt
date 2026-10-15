package com.xuanniao.flyscreen.net

import com.xuanniao.flyscreen.protocol.HandshakePayload
import com.xuanniao.flyscreen.protocol.MessageCodec
import com.xuanniao.flyscreen.protocol.MessageType
import com.xuanniao.flyscreen.protocol.PeerCaps
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap

class ConnectionManager(
    private val scope: CoroutineScope,
    var securityKey: String,
    private val localMachine: String,
    private val deviceClass: String,
    private val tcpPort: Int = 15155,
    private val udpPort: Int = 15156
) {
    private val sessions = ConcurrentHashMap<String, PeerSession>()
    private var listenJob: Job? = null
    private var serverSocket: ServerSocket? = null
    val discovery = DiscoveryService(localMachine, tcpPort, udpPort, scope)

    @Volatile var bindIp: String = "0.0.0.0"
    @Volatile var isListening: Boolean = false

    var onStatus: ((String) -> Unit)? = null
    var onPeersUpdated: (() -> Unit)? = null

    fun start(localIp: String) {
        bindIp = localIp
        discovery.onPeersUpdated = { onPeersUpdated?.invoke() }
        discovery.start(localIp)
        startListener()
        onStatus?.invoke("发现已启动，监听 $tcpPort")
    }

    fun stop() {
        listenJob?.cancel()
        try { serverSocket?.close() } catch (_: Exception) {}
        serverSocket = null
        isListening = false
        discovery.stop()
        sessions.values.forEach { it.close() }
        sessions.clear()
    }

    fun connectTo(peerName: String, ip: String, port: Int = tcpPort) {
        scope.launch(Dispatchers.IO) {
            try {
                onStatus?.invoke("正在连接 $peerName ($ip)…")
                val sock = Socket()
                sock.connect(InetSocketAddress(ip, port), 5000)
                val session = PeerSession(sock, scope, peerName)
                wireSession(session, outbound = true, expectedPeer = peerName)
                session.start()
                val hs = HandshakePayload(
                    machineName = localMachine,
                    securityKey = securityKey,
                    localIp = bindIp,
                    deviceClass = deviceClass,
                    caps = PeerCaps.AndroidMobile,
                    appVersion = com.xuanniao.flyscreen.BuildConfig.FLYSREEN_VERSION
                )
                session.send(MessageCodec.encodeJson(MessageType.Handshake, hs))
            } catch (e: Exception) {
                onStatus?.invoke("连接失败: ${e.message}")
            }
        }
    }

    fun sendToActive(frame: ByteArray) {
        sessions.values.firstOrNull { it.authenticated }?.send(frame)
    }

    fun hasAuthenticatedPeer(): Boolean =
        sessions.values.any { it.authenticated }

    private fun startListener() {
        listenJob?.cancel()
        listenJob = scope.launch(Dispatchers.IO) {
            try {
                val ss = ServerSocket(tcpPort)
                serverSocket = ss
                isListening = true
                while (isActive) {
                    val client = ss.accept()
                    val session = PeerSession(client, scope)
                    wireSession(session, outbound = false, expectedPeer = null)
                    session.start()
                }
            } catch (_: Exception) {
                isListening = false
            }
        }
    }

    private fun wireSession(session: PeerSession, outbound: Boolean, expectedPeer: String?) {
        session.onMessage = { type, payload ->
            when (type) {
                MessageType.HandshakeAck -> {
                    val ack = MessageCodec.decodeJson(payload, HandshakePayload::class.java)
                    if (ack != null && ack.securityKey.trim() == securityKey.trim()) {
                        session.authenticated = true
                        session.peerName = expectedPeer ?: ack.machineName
                        sessions[session.peerName] = session
                        onStatus?.invoke("已连接 ${session.peerName}")
                    } else {
                        session.close()
                        onStatus?.invoke("握手密钥不匹配")
                    }
                }
                MessageType.Handshake -> {
                    if (outbound) return@onMessage
                    val hs = MessageCodec.decodeJson(payload, HandshakePayload::class.java)
                    if (hs == null || hs.securityKey.trim() != securityKey.trim()) {
                        session.close()
                        return@onMessage
                    }
                    session.peerName = hs.machineName
                    session.authenticated = true
                    sessions[hs.machineName] = session
                    val ack = HandshakePayload(
                        machineName = localMachine,
                        securityKey = securityKey,
                        localIp = bindIp,
                        isReply = true,
                        deviceClass = deviceClass,
                        caps = PeerCaps.AndroidMobile,
                        appVersion = com.xuanniao.flyscreen.BuildConfig.FLYSREEN_VERSION
                    )
                    session.send(MessageCodec.encodeJson(MessageType.HandshakeAck, ack))
                    onStatus?.invoke("入站已认证 ${hs.machineName}")
                }
                MessageType.Heartbeat -> {
                    session.send(MessageCodec.encode(MessageType.HeartbeatAck))
                }
            }
        }
        session.onDisconnected = {
            val name = session.peerName
            if (name.isNotBlank()) sessions.remove(name, session)
            onStatus?.invoke(if (name.isBlank()) "连接断开" else "$name 已断开")
        }
    }

    /** 周期性重连已配置目标。 */
    fun startReconnectLoop(targetName: String, resolveIp: () -> String?) {
        scope.launch(Dispatchers.IO) {
            while (isActive) {
                if (!hasAuthenticatedPeer()) {
                    val ip = resolveIp()
                    if (!ip.isNullOrBlank()) connectTo(targetName, ip)
                }
                delay(5000)
            }
        }
    }
}
