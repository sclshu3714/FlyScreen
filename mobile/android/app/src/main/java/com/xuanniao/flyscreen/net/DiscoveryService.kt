package com.xuanniao.flyscreen.net

import com.xuanniao.flyscreen.protocol.DiscoveryPacket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.ConcurrentHashMap

class DiscoveryService(
    private val localMachine: String,
    private val tcpPort: Int,
    private val udpPort: Int = 15156,
    private val scope: CoroutineScope
) {
    data class Peer(val machineName: String, val ip: String, val tcpPort: Int, val lastSeenMs: Long)

    private val peers = ConcurrentHashMap<String, Peer>()
    private var job: Job? = null
    private var socket: DatagramSocket? = null

    var onPeersUpdated: (() -> Unit)? = null

    fun start(localIp: String) {
        stop()
        job = scope.launch(Dispatchers.IO) {
            val sock = DatagramSocket(udpPort).also {
                it.broadcast = true
                it.reuseAddress = true
                socket = it
            }
            val announce = launch {
                while (isActive) {
                    try {
                        // Discovery 为原始 JSON（无 TCP 帧头），与 Core DiscoveryService 一致
                        val payload = com.google.gson.Gson().toJson(
                            DiscoveryPacket(localMachine, localIp, tcpPort)
                        ).toByteArray(Charsets.UTF_8)
                        val packet = DatagramPacket(
                            payload, payload.size,
                            InetAddress.getByName("255.255.255.255"), udpPort
                        )
                        sock.send(packet)
                    } catch (_: Exception) {
                    }
                    prune()
                    delay(2000)
                }
            }
            val buf = ByteArray(4096)
            try {
                while (isActive) {
                    val packet = DatagramPacket(buf, buf.size)
                    sock.receive(packet)
                    val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                    val pkt = try {
                        com.google.gson.Gson().fromJson(text, DiscoveryPacket::class.java)
                    } catch (_: Exception) {
                        null
                    } ?: continue
                    if (pkt.machineName.isBlank() ||
                        pkt.machineName.equals(localMachine, ignoreCase = true)
                    ) continue
                    val ip = pkt.ip.ifBlank { packet.address.hostAddress ?: continue }
                    peers[pkt.machineName] = Peer(
                        pkt.machineName, ip, if (pkt.tcpPort > 0) pkt.tcpPort else tcpPort,
                        System.currentTimeMillis()
                    )
                    onPeersUpdated?.invoke()
                }
            } catch (_: Exception) {
            } finally {
                announce.cancel()
                try { sock.close() } catch (_: Exception) {}
            }
        }
    }

    fun getPeers(): List<Peer> =
        peers.values.sortedBy { it.machineName }

    fun stop() {
        job?.cancel()
        job = null
        try { socket?.close() } catch (_: Exception) {}
        socket = null
    }

    private fun prune() {
        val cutoff = System.currentTimeMillis() - 10_000
        peers.entries.removeIf { it.value.lastSeenMs < cutoff }
    }
}
