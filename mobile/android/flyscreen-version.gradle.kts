/**
 * 与仓库 build/FlyScreen.Version.targets / Resolve-FlyScreenVersion.ps1 同一规则。
 * 自动叠加：最新 vX.Y.Z +1；minor/patch 大于 9 向高位进位（1.0.9→1.1.0）。
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

/** minor/patch 大于 9 进位；major 可超过 9。 */
fun normalizeVersionParts(majorIn: Int, minorIn: Int, patchIn: Int): Triple<Int, Int, Int> {
    var major = majorIn.coerceAtLeast(0)
    var minor = minorIn.coerceAtLeast(0)
    var patch = patchIn.coerceAtLeast(0)
    while (patch > 9) {
        patch -= 10
        minor++
    }
    while (minor > 9) {
        minor -= 10
        major++
    }
    return Triple(major, minor, patch)
}

fun bumpVersion(major: Int, minor: Int, patch: Int): Triple<Int, Int, Int> =
    normalizeVersionParts(major, minor, patch + 1)

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
        val bumped = bumpVersion(parts[0], parts[1], parts[2])
        flyscreenVersion = "${bumped.first}.${bumped.second}.${bumped.third}"
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

println("玄鸟 Android 版本: $flyscreenVersion (code=$flyscreenVersionCode) — 与 Win Git 标签规则一致（>9 进位）")
