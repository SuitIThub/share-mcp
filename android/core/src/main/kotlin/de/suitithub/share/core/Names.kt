package de.suitithub.share.core

private val reservedNames =
    setOf("CON", "PRN", "AUX", "NUL") + (1..9).flatMap { listOf("COM$it", "LPT$it") }

private const val BAD_CHARS = "<>:\"|?*"
private const val MAX_NAME_BYTES = 200

/** Reduces a sender-supplied name to a safe base name for Windows and Android (same rules as the Go client). */
fun safeFileName(name: String): String {
    var s = name.substring(name.lastIndexOfAny(charArrayOf('/', '\\')) + 1)
    s = s.map { if (it.code < 32 || it in BAD_CHARS) '_' else it }.joinToString("")
    s = s.trim().trimEnd('.', ' ')
    if (s.isEmpty()) return "file"
    if (s.substringBefore('.').uppercase() in reservedNames) s = "_$s"
    if (s.toByteArray().size > MAX_NAME_BYTES) {
        val ext = s.extension().takeIf { it.toByteArray().size <= 20 } ?: ""
        s = s.truncateUtf8(MAX_NAME_BYTES - ext.toByteArray().size) + ext
    }
    return s
}

/** name, or "stem (n).ext" if it is taken. */
fun uniqueName(name: String, exists: (String) -> Boolean): String {
    if (!exists(name)) return name
    val ext = name.extension()
    val stem = name.removeSuffix(ext)
    var i = 1
    while (true) {
        val candidate = "$stem ($i)$ext"
        if (!exists(candidate)) return candidate
        i++
    }
}

/** Extension including the dot; empty for names without one and for dot files. */
private fun String.extension(): String {
    val i = lastIndexOf('.')
    return if (i <= 0) "" else substring(i)
}

/** Longest prefix with at most max UTF-8 bytes, never splitting a code point. */
internal fun String.truncateUtf8(max: Int): String {
    var bytes = 0
    var i = 0
    while (i < length) {
        val cp = codePointAt(i)
        val n = if (cp < 0x80) 1 else if (cp < 0x800) 2 else if (cp < 0x10000) 3 else 4
        if (bytes + n > max) break
        bytes += n
        i += Character.charCount(cp)
    }
    return substring(0, i)
}
