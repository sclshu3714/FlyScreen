package com.xuanniao.flyscreen.host

import android.content.Context

class AppPreferences(context: Context) {
    private val sp = context.getSharedPreferences("flyscreen", Context.MODE_PRIVATE)

    var securityKey: String
        get() = sp.getString("security_key", "") ?: ""
        set(value) = sp.edit().putString("security_key", value).apply()

    var targetPeer: String
        get() = sp.getString("target_peer", "") ?: ""
        set(value) = sp.edit().putString("target_peer", value).apply()
}
