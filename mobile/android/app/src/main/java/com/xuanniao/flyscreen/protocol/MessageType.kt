package com.xuanniao.flyscreen.protocol

/** 与 Core MessageType 数值保持一致。 */
object MessageType {
    const val Handshake: Byte = 1
    const val HandshakeAck: Byte = 2
    const val Heartbeat: Byte = 3
    const val HeartbeatAck: Byte = 4
    const val MouseMove: Byte = 10
    const val MouseButton: Byte = 11
    const val MouseWheel: Byte = 12
    const val KeyEvent: Byte = 20
    const val ClipboardText: Byte = 30
    const val SwitchFocus: Byte = 50
}
