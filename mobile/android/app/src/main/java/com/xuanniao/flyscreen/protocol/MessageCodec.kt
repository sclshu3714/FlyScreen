package com.xuanniao.flyscreen.protocol

import com.google.gson.Gson
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** 帧格式：1 byte type + 4 byte LE length + payload（与 Core MessageCodec 一致）。 */
object MessageCodec {
    const val HEADER_SIZE = 5
    const val MAX_PAYLOAD = 8 * 1024 * 1024

    private val gson = Gson()

    fun encode(type: Byte, payload: ByteArray = ByteArray(0)): ByteArray {
        require(payload.size <= MAX_PAYLOAD) { "payload too large" }
        val out = ByteArrayOutputStream(HEADER_SIZE + payload.size)
        out.write(type.toInt() and 0xff)
        val len = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(payload.size).array()
        out.write(len)
        out.write(payload)
        return out.toByteArray()
    }

    fun <T> encodeJson(type: Byte, obj: T): ByteArray =
        encode(type, gson.toJson(obj).toByteArray(Charsets.UTF_8))

    fun <T> decodeJson(payload: ByteArray, clazz: Class<T>): T? =
        try {
            gson.fromJson(String(payload, Charsets.UTF_8), clazz)
        } catch (_: Exception) {
            null
        }

    data class Frame(val type: Byte, val payload: ByteArray)
}
