package com.xuanniao.flyscreen.net

import com.xuanniao.flyscreen.protocol.MessageCodec
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.DataInputStream
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

class PeerSession(
    private val socket: Socket,
    private val scope: CoroutineScope,
    val peerNameHint: String = ""
) {
    private val sendChannel = Channel<ByteArray>(Channel.UNLIMITED)
    private var sendJob: Job? = null
    private var recvJob: Job? = null
    @Volatile var peerName: String = peerNameHint
    @Volatile var authenticated: Boolean = false
    @Volatile private var disposed = false

    var onMessage: ((Byte, ByteArray) -> Unit)? = null
    var onDisconnected: (() -> Unit)? = null

    fun start() {
        socket.tcpNoDelay = true
        sendJob = scope.launch(Dispatchers.IO) {
            val out = socket.getOutputStream()
            try {
                for (frame in sendChannel) {
                    if (disposed) break
                    out.write(frame)
                    out.flush()
                }
            } catch (_: Exception) {
            } finally {
                raiseDisconnected()
            }
        }
        recvJob = scope.launch(Dispatchers.IO) {
            try {
                val input = DataInputStream(socket.getInputStream())
                val header = ByteArray(MessageCodec.HEADER_SIZE)
                while (isActive && !disposed) {
                    input.readFully(header)
                    val type = header[0]
                    val len = ByteBuffer.wrap(header, 1, 4).order(ByteOrder.LITTLE_ENDIAN).int
                    if (len < 0 || len > MessageCodec.MAX_PAYLOAD) break
                    val payload = if (len == 0) ByteArray(0) else ByteArray(len)
                    if (len > 0) input.readFully(payload)
                    onMessage?.invoke(type, payload)
                }
            } catch (_: Exception) {
            } finally {
                raiseDisconnected()
            }
        }
    }

    fun send(frame: ByteArray) {
        if (disposed) return
        sendChannel.trySend(frame)
    }

    fun close() {
        disposed = true
        sendChannel.close()
        sendJob?.cancel()
        recvJob?.cancel()
        try { socket.close() } catch (_: Exception) {}
    }

    private fun raiseDisconnected() {
        if (disposed) {
            onDisconnected?.invoke()
            return
        }
        disposed = true
        onDisconnected?.invoke()
    }
}
