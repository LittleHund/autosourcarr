package eu.kanade.tachiyomi.extension.all.autosourcarr

/**
 * Small dependency-free JSON reader used by the legacy libVersion 1.4 build.
 *
 * AutoSourcarr intentionally does not use generated kotlinx.serialization
 * serializers here. Older TachiyomiSY/Mihon forks can ship an older
 * kotlinx.serialization runtime than the current Keiyoushi compiler plugin,
 * which otherwise causes an AbstractMethodError at runtime before a response can
 * be parsed. This parser only implements standard JSON and depends on the Kotlin
 * standard library, so the same extension bytecode works in both Android clients
 * and Suwayomi's JVM extension loader.
 */
internal object MiniJson {
    fun parseObject(text: String): Map<String, Any?> {
        val value = Parser(text).parseDocument()
        return value as? Map<String, Any?>
            ?: throw IllegalArgumentException("Expected JSON object")
    }

    private class Parser(private val text: String) {
        private var index = 0

        fun parseDocument(): Any? {
            skipWhitespace()
            val value = parseValue()
            skipWhitespace()
            if (index != text.length) error("Unexpected trailing JSON data")
            return value
        }

        private fun parseValue(): Any? {
            skipWhitespace()
            if (index >= text.length) error("Unexpected end of JSON")
            return when (text[index]) {
                '{' -> parseObject()
                '[' -> parseArray()
                '"' -> parseString()
                't' -> parseLiteral("true", true)
                'f' -> parseLiteral("false", false)
                'n' -> parseLiteral("null", null)
                '-', in '0'..'9' -> parseNumber()
                else -> error("Unexpected character '${text[index]}'")
            }
        }

        private fun parseObject(): Map<String, Any?> {
            expect('{')
            skipWhitespace()
            val result = LinkedHashMap<String, Any?>()
            if (consume('}')) return result
            while (true) {
                skipWhitespace()
                if (index >= text.length || text[index] != '"') error("Expected object key")
                val key = parseString()
                skipWhitespace()
                expect(':')
                result[key] = parseValue()
                skipWhitespace()
                when {
                    consume('}') -> return result
                    consume(',') -> Unit
                    else -> error("Expected ',' or '}'")
                }
            }
        }

        private fun parseArray(): List<Any?> {
            expect('[')
            skipWhitespace()
            val result = ArrayList<Any?>()
            if (consume(']')) return result
            while (true) {
                result += parseValue()
                skipWhitespace()
                when {
                    consume(']') -> return result
                    consume(',') -> Unit
                    else -> error("Expected ',' or ']' ")
                }
            }
        }

        private fun parseString(): String {
            expect('"')
            val out = StringBuilder()
            while (index < text.length) {
                val ch = text[index++]
                when (ch) {
                    '"' -> return out.toString()
                    '\\' -> {
                        if (index >= text.length) error("Incomplete string escape")
                        when (val escaped = text[index++]) {
                            '"', '\\', '/' -> out.append(escaped)
                            'b' -> out.append('\b')
                            'f' -> out.append('\u000c')
                            'n' -> out.append('\n')
                            'r' -> out.append('\r')
                            't' -> out.append('\t')
                            'u' -> out.append(parseUnicodeEscape())
                            else -> error("Invalid string escape")
                        }
                    }
                    else -> {
                        if (ch.code < 0x20) error("Control character in string")
                        out.append(ch)
                    }
                }
            }
            error("Unterminated string")
        }

        private fun parseUnicodeEscape(): Char {
            if (index + 4 > text.length) error("Incomplete unicode escape")
            val hex = text.substring(index, index + 4)
            index += 4
            return hex.toIntOrNull(16)?.toChar() ?: error("Invalid unicode escape")
        }

        private fun parseNumber(): Number {
            val start = index
            consume('-')
            if (consume('0')) {
                // A single leading zero is valid; the generic digit loop below is
                // intentionally skipped for this branch.
            } else {
                requireDigit()
                while (index < text.length && text[index].isDigit()) index++
            }
            var fractional = false
            if (consume('.')) {
                fractional = true
                requireDigit()
                while (index < text.length && text[index].isDigit()) index++
            }
            if (index < text.length && (text[index] == 'e' || text[index] == 'E')) {
                fractional = true
                index++
                if (index < text.length && (text[index] == '+' || text[index] == '-')) index++
                requireDigit()
                while (index < text.length && text[index].isDigit()) index++
            }
            val raw = text.substring(start, index)
            return if (fractional) raw.toDoubleOrNull() ?: error("Invalid JSON number")
            else raw.toLongOrNull() ?: error("Invalid JSON integer")
        }

        private fun requireDigit() {
            if (index >= text.length || !text[index].isDigit()) error("Expected digit")
        }

        private fun parseLiteral(literal: String, value: Any?): Any? {
            if (!text.regionMatches(index, literal, 0, literal.length)) error("Invalid JSON literal")
            index += literal.length
            return value
        }

        private fun skipWhitespace() {
            while (index < text.length && text[index].isWhitespace()) index++
        }

        private fun expect(ch: Char) {
            if (!consume(ch)) error("Expected '$ch'")
        }

        private fun consume(ch: Char): Boolean {
            if (index < text.length && text[index] == ch) {
                index++
                return true
            }
            return false
        }

        private fun error(message: String): Nothing =
            throw IllegalArgumentException("$message at JSON offset $index")
    }
}

internal fun Any?.jsonObjectOrNull(): Map<String, Any?>? = this as? Map<String, Any?>
internal fun Any?.jsonArrayOrEmpty(): List<Any?> = this as? List<Any?> ?: emptyList()
internal fun Map<String, Any?>.jsonString(key: String, default: String = ""): String = this[key] as? String ?: default
internal fun Map<String, Any?>.jsonBoolean(key: String, default: Boolean = false): Boolean = this[key] as? Boolean ?: default
internal fun Map<String, Any?>.jsonInt(key: String, default: Int = 0): Int = (this[key] as? Number)?.toInt() ?: default
internal fun Map<String, Any?>.jsonLongOrNull(key: String): Long? = (this[key] as? Number)?.toLong()
internal fun Map<String, Any?>.jsonFloat(key: String, default: Float = 0f): Float = (this[key] as? Number)?.toFloat() ?: default
