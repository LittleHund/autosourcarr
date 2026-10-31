package eu.kanade.tachiyomi.extension.all.autosourcarr

import eu.kanade.tachiyomi.source.model.Filter

data class SourceChoice(
    val label: String,
    val sourceIds: Set<String>,
)

class SourceFilter(private val choices: List<SourceChoice>) :
    Filter.Select<String>("Underlying source", choices.map { it.label }.toTypedArray()) {
    fun sourceIds(): Set<String> = choices.getOrElse(state) { SourceChoice("All enabled sources", emptySet()) }.sourceIds
}

class LanguageCheckBox(
    val code: String,
    val sourceIds: Set<String>,
    label: String,
) : Filter.CheckBox(label, true)

class LanguageFilter(values: List<LanguageCheckBox>) :
    Filter.Group<LanguageCheckBox>("Languages", values) {
    fun sourceIds(): Set<String> = state.filter { it.state }.flatMap { it.sourceIds }.toSet()
}

class MatchCountFilter(maxCount: Int) : Filter.Select<String>(
    "Matched sources",
    (listOf("Any") + (0..maxCount.coerceAtLeast(0)).map { "$it matched" }).toTypedArray(),
) {
    fun exact(): Int? = if (state == 0) null else state - 1
}

class AutoSourcarrSortFilter : Filter.Select<String>(
    "Sort by",
    arrayOf("Relevance", "Most matched sources", "Title"),
) {
    fun value(): String = when (state) {
        1 -> "sources"
        2 -> "title"
        else -> "relevance"
    }
}
