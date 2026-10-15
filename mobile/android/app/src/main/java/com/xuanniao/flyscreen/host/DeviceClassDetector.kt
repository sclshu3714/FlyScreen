package com.xuanniao.flyscreen.host

import android.content.Context
import android.content.res.Configuration
import com.xuanniao.flyscreen.protocol.PeerDeviceClass

object DeviceClassDetector {
    fun detect(context: Context): String {
        val sw = context.resources.configuration.smallestScreenWidthDp
        val type = context.resources.configuration.screenLayout and Configuration.SCREENLAYOUT_SIZE_MASK
        return if (sw >= 600 || type >= Configuration.SCREENLAYOUT_SIZE_LARGE) {
            PeerDeviceClass.Tablet
        } else {
            PeerDeviceClass.Phone
        }
    }
}
