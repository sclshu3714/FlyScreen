/**
 * 与仓库 build/FlyScreen.Version.targets 同一规则。
 * 由 settings.gradle.kts apply；在 beforeProject 写入 :app 的 extra。
 */
import java.io.ByteArrayOutputStream

fun versionCodeOf(version: String): Int {
    val p = version.split('.').map { it.toIntOrNull() ?: 0 }
    val major = p.getOrElse(0) { 0 }
    val minor = p.getOrElse(1) { 0 }
    val patch = p.getOrElse(2) { 0 }
    return major * 10000 + minor * 100 + patch
}

val override = (providers.gradleProperty("flyscreen.version").orNull)?.trim().orEmpty()

val flyscreenVersion: String
val flyscreenVersionCode: Int

if (override.isNotEmpty()) {
    flyscreenVersion = override
    flyscreenVersionCode = versionCodeOf(override)
} else {
    val repoRoot = settings.rootDir.resolve("../..").canonicalFile
    val tagsOut = ByteArrayOutputStream()
    var exit = -1
    try {
        val pb = ProcessBuilder("git", "tag", "-l", "v*.*.*", "--sort=-v:refname")
            .directory(repoRoot)
            .redirectErrorStream(true)
        val proc = pb.start()
        proc.inputStream.copyTo(tagsOut)
        exit = proc.waitFor()
    } catch (_: Exception) {
        exit = -1
    }

    val latest = if (exit == 0) {
        tagsOut.toString(Charsets.UTF_8)
            .lineSequence()
            .map { it.trim() }
            .firstOrNull { it.matches(Regex("""v\d+\.\d+\.\d+""")) }
    } else null

    if (latest != null) {
        val parts = latest.removePrefix("v").split('.').map { it.toInt() }
        flyscreenVersion = "${parts[0]}.${parts[1]}.${parts[2] + 1}"
        flyscreenVersionCode = versionCodeOf(flyscreenVersion)
    } else {
        flyscreenVersion = "1.0.0"
        flyscreenVersionCode = versionCodeOf(flyscreenVersion)
    }
}

gradle.beforeProject {
    if (it.path == ":app") {
        it.extra["flyscreen.version"] = flyscreenVersion
        it.extra["flyscreen.versionCode"] = flyscreenVersionCode
    }
}

println("玄鸟 Android 版本: $flyscreenVersion (code=$flyscreenVersionCode) — 与 Win Git 标签规则一致")
