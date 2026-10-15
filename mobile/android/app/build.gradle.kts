plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val flyscreenVersion = extra["flyscreen.version"] as String
val flyscreenVersionCode = extra["flyscreen.versionCode"] as Int

android {
    namespace = "com.xuanniao.flyscreen"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.xuanniao.flyscreen"
        minSdk = 26
        targetSdk = 34
        versionCode = flyscreenVersionCode
        versionName = flyscreenVersion
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        buildConfigField("String", "FLYSREEN_VERSION", "\"$flyscreenVersion\"")
    }

    buildFeatures {
        buildConfig = true
        viewBinding = true
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    // W4 可再加 productFlavors: mobile / tv
    flavorDimensions += "device"
    productFlavors {
        create("phone") {
            dimension = "device"
            applicationIdSuffix = ""
            // 握手 AppVersion 用 BuildConfig.FLYSREEN_VERSION（无后缀），与 Win 对齐
            versionNameSuffix = "-phone"
        }
        create("tablet") {
            dimension = "device"
            versionNameSuffix = "-tablet"
        }
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.12.0")
    implementation("androidx.appcompat:appcompat:1.6.1")
    implementation("com.google.android.material:material:1.11.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.7.0")
    implementation("androidx.activity:activity-ktx:1.8.2")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.7.3")
    implementation("com.google.code.gson:gson:2.10.1")
}
