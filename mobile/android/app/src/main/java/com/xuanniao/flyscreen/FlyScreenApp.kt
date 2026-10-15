package com.xuanniao.flyscreen

import android.app.Application
import com.xuanniao.flyscreen.host.AndroidAppHost

class FlyScreenApp : Application() {
    override fun onCreate() {
        super.onCreate()
        // 预热宿主（前台服务 / Activity 共用单例）
        AndroidAppHost.get(this)
    }
}
