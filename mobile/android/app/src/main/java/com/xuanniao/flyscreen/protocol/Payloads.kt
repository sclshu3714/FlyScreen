package com.xuanniao.flyscreen.protocol

import com.google.gson.annotations.SerializedName

data class HandshakePayload(
    @SerializedName("MachineName") val machineName: String = "",
    @SerializedName("SecurityKey") val securityKey: String = "",
    @SerializedName("LocalIp") val localIp: String = "",
    @SerializedName("ProtocolVersion") val protocolVersion: Int = 2,
    @SerializedName("IsReply") val isReply: Boolean = false,
    @SerializedName("Platform") val platform: String? = "Android",
    @SerializedName("DeviceClass") val deviceClass: String? = "Phone",
    @SerializedName("Caps") val caps: Array<String>? = PeerCaps.AndroidMobile,
    @SerializedName("AppVersion") val appVersion: String? = "1.4.0"
)

data class MouseMovePayload(
    @SerializedName("Nx") val nx: Double = 0.0,
    @SerializedName("Ny") val ny: Double = 0.0,
    @SerializedName("Relative") val relative: Boolean = false,
    @SerializedName("Dx") val dx: Int = 0,
    @SerializedName("Dy") val dy: Int = 0
)

data class MouseButtonPayload(
    @SerializedName("Button") val button: Int = 0,
    @SerializedName("Down") val down: Boolean = false
)

data class MouseWheelPayload(
    @SerializedName("Delta") val delta: Int = 0,
    @SerializedName("Horizontal") val horizontal: Boolean = false
)

data class DiscoveryPacket(
    @SerializedName("MachineName") val machineName: String = "",
    @SerializedName("Ip") val ip: String = "",
    @SerializedName("TcpPort") val tcpPort: Int = 15155
)

object PeerCaps {
    const val AbsPointer = "AbsPointer"
    const val ClipboardText = "ClipboardText"
    const val DirectTransfer = "DirectTransfer"
    val AndroidMobile = arrayOf(AbsPointer, ClipboardText, DirectTransfer)
}

object PeerDeviceClass {
    const val Phone = "Phone"
    const val Tablet = "Tablet"
}
