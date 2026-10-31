import os
import tempfile
from pathlib import Path

# Set before importing app singletons.
_TEST_DATA = tempfile.TemporaryDirectory()
os.environ["DATA_DIR"] = _TEST_DATA.name
os.environ["SOURCE_LANGUAGES"] = "en,all"

from app.matcher import extract_alt_titles, looks_preferred_english, normalize_title, score_match


def test_normalization():
    assert normalize_title("One-Punch Man") == "one punch man"
    assert normalize_title("ＯＮＥ　ＰＩＥＣＥ") == "one piece"


def test_two_title_corroboration_is_near_certain():
    result = score_match(
        ["One-Punch Man", "ワンパンマン"],
        ["One Punch Man", "ワンパンマン"],
    )
    assert result.score >= 0.995
    assert "two-title" in result.reason


def test_alt_title_extraction():
    text = "Description here\n\nAlternative titles: One-Punch Man | Wanpanman\nStatus: Ongoing"
    assert extract_alt_titles(text) == ["One-Punch Man", "Wanpanman"]


def test_description_90_percent_is_match_even_when_titles_differ():
    description = (
        "A veteran adventurer wakes in a city where magic has vanished and must uncover "
        "why the old guilds disappeared before the same disaster reaches his new family."
    )
    result = score_match(
        ["Completely Different Localized Title"],
        ["Another Translation"],
        candidate_description=description,
        entry_descriptions=[description.replace("veteran", "seasoned")],
    )
    assert result.description_score >= 0.90
    assert result.score >= 0.995
    assert "description>=90%" in result.reason


def test_short_description_does_not_auto_match():
    result = score_match(
        ["Unrelated A"],
        ["Unrelated B"],
        candidate_description="A hero fights monsters.",
        entry_descriptions=["A hero fights monsters."],
    )
    assert result.description_score == 0.0
    assert result.score < 0.94


def test_preferred_english_rejects_obvious_romaji_but_accepts_english_title():
    assert looks_preferred_english("+99 Reinforced Wooden Stick") is True
    assert looks_preferred_english("100-mankai no Suki o Ageru") is False


def test_placeholder_aliases_are_never_match_titles():
    from app.matcher import is_meaningful_title, title_similarity

    for value in ("No alternative titles.", "No associated names", "N/A", "Unknown"):
        assert is_meaningful_title(value) is False
        assert title_similarity(value, value) == 0.0
    assert extract_alt_titles("Associated Name(s): No alternative titles.\nStatus: Ongoing") == []


def test_single_word_alias_is_not_match_evidence_but_full_source_title_is():
    from app.matcher import is_match_evidence_title

    assert is_match_evidence_title("King", "manual") is False
    assert is_match_evidence_title("King", "description-alias") is False
    assert is_match_evidence_title("King", "source") is True
    assert is_match_evidence_title("King", "source-variant") is True
    assert is_match_evidence_title("King", primary=True) is True
    assert is_match_evidence_title("The Returned King", "manual") is True


def test_summary_cleaner_removes_links_alias_blocks_and_related_work_chronology():
    from app.matcher import clean_summary, is_meaningful_summary

    raw = (
        "When **civil engineering student Suho Kim** falls asleep reading a *fantasy novel*, "
        "he wakes up as **Lloyd Frontera** and uses his engineering knowledge to save a family in debt. "
        "(From [LINE Webtoon](/group/line-webtoon)) "
        "Chronology of works in this universe: \"[Other Work](/series/other)\" -> \"[Later Work](/series/later)\" "
        "**Original Webtoon:** [Naver Webtoon](https://comic.naver.com/example) "
        "**Official Translations:** [English](https://www.webtoons.com/example)"
    )
    cleaned = clean_summary(raw)
    assert "Suho Kim" in cleaned and "Lloyd Frontera" in cleaned
    assert "Chronology of works" not in cleaned
    assert "Official Translations" not in cleaned
    assert "naver.com" not in cleaned and "webtoons.com" not in cleaned
    assert is_meaningful_summary(cleaned) is True


def test_metadata_only_or_repetitive_gibberish_is_not_comparable_summary():
    from app.matcher import description_similarity, is_meaningful_summary

    assert not is_meaningful_summary("Associated Names: No alternative titles. Official Translations: https://example.invalid")
    gibberish = "spam " * 40
    assert not is_meaningful_summary(gibberish)
    assert description_similarity(gibberish, gibberish) == 0.0


def test_alt_title_comma_lists_split_but_real_comma_titles_stay_intact():
    text = (
        "Alternative titles: +99 Wooden Stick , 99 Hardened Wooden Stick, "
        "99강화나무몽둥이, 99 Reinforced Wood Stick\nStatus: Ongoing"
    )
    assert extract_alt_titles(text) == [
        "+99 Wooden Stick",
        "99 Hardened Wooden Stick",
        "99강화나무몽둥이",
        "99 Reinforced Wood Stick",
    ]

    protected = (
        "Alternative titles: My Wife, the Demon Queen | I, Who Became a Legendary Boss\n"
        "Status: Ongoing"
    )
    assert extract_alt_titles(protected) == [
        "My Wife, the Demon Queen",
        "I, Who Became a Legendary Boss",
    ]

    skill_lender = (
        'Alternative titles: "Skill Lender" Get Back His Pride: Before I Started Lending, '
        'I Told You This Loan Charges 10% Interest Every 10 Days, Right?\nStatus: Ongoing'
    )
    assert extract_alt_titles(skill_lender) == [
        '"Skill Lender" Get Back His Pride: Before I Started Lending, I Told You This Loan Charges 10% Interest Every 10 Days, Right?'
    ]

    # Subtitle wrappers are part of the title. Commas inside paired wave-dash/tilde
    # punctuation must never be treated as alias separators.
    wrapped = (
        "Alternative titles: Skill Lender's Retrieving (Tale) ～I Told You It's 10% "
        "Per 10 Days at First, Didn't I～\nStatus: Ongoing"
    )
    assert extract_alt_titles(wrapped) == [
        "Skill Lender's Retrieving (Tale) ～I Told You It's 10% Per 10 Days at First, Didn't I～"
    ]
    wrapped_ascii = (
        "Alternative titles: Skill Lender's Retrieving Tale ~I Told You It's 10% "
        "Per 10 Days at First, Didn't I?~\nStatus: Ongoing"
    )
    assert extract_alt_titles(wrapped_ascii) == [
        "Skill Lender's Retrieving Tale ~I Told You It's 10% Per 10 Days at First, Didn't I?~"
    ]
    wave_dash = (
        "Alternative titles: Skill Lender's Retrieving Tale 〜I Told You It's 10% "
        "Per 10 Days at First, Didn't I?〜\nStatus: Ongoing"
    )
    assert extract_alt_titles(wave_dash) == [
        "Skill Lender's Retrieving Tale 〜I Told You It's 10% Per 10 Days at First, Didn't I?〜"
    ]

    # Top-level comma lists still split when the commas are actual alias separators.
    wrapped_list = (
        "Alternative titles: +99 Wooden Stick, 99 Hardened Wooden Stick, "
        "Skill Lender's Retrieving (Tale) ～I Told You It's 10% Per 10 Days at First, Didn't I～\n"
        "Status: Ongoing"
    )
    assert extract_alt_titles(wrapped_list) == [
        "+99 Wooden Stick",
        "99 Hardened Wooden Stick",
        "Skill Lender's Retrieving (Tale) ～I Told You It's 10% Per 10 Days at First, Didn't I～",
    ]


def test_one_and_two_word_title_containment_is_not_automatic_equivalence():
    from app.matcher import title_similarity

    assert title_similarity("Bleach", "Bleach") == 1.0
    assert title_similarity("Solo Leveling", "Solo Leveling") == 1.0
    assert title_similarity("King", "Demon King") < 0.94
    assert title_similarity("Demon King", "The Demon King Returns") < 0.94
    assert title_similarity("Solo Leveling", "Solo Leveling Side Story") < 0.94


def test_automatic_title_similarity_is_length_sensitive_for_all_auto_matching():
    from app.matcher import automatic_title_similarity

    assert automatic_title_similarity("Overlord of Sichuan", "The Iron Overlord of Sichuan") < 0.94
    assert automatic_title_similarity("God of War", "The God of War") < 0.94
    assert automatic_title_similarity("God of War", "Bride of the War God") < 0.94
    assert automatic_title_similarity("God of War", "Return of the War God") < 0.94
    assert automatic_title_similarity("Beast Taming Evolution", "Global Beast Tamer: I Can See the Path of Evolution") < 0.94
    assert automatic_title_similarity("Solo Leveling", "Solo Leveling Side Story") < 0.94
    assert automatic_title_similarity("Solo Leveling", "Solo Leveling") == 1.0


def test_dangling_interrogative_subtitle_fragments_are_not_aliases():
    from app.matcher import extract_alt_titles, is_alias_fragment

    for fragment in ("Didn't I?~", "Right?~", "Didn't I?～", "Right?〜"):
        assert is_alias_fragment(fragment) is True
        assert extract_alt_titles(f"Alternative titles: {fragment}") == []


def test_color_edition_markers_and_base_titles():
    from app.matcher import color_base_title, normalized_color_base, title_color_variant

    assert title_color_variant("A Returner's Magic Should Be Special (Colored)") == "colored"
    assert title_color_variant("A Returner's Magic Should Be Special - Full Color") == "colored"
    assert title_color_variant("Some Manga (Monochrome)") == "monochrome"
    assert title_color_variant("Colorful Days") == "unknown"
    assert color_base_title("A Returner's Magic Should Be Special (Colored)") == "A Returner's Magic Should Be Special"
    assert normalized_color_base("A Returner's Magic Should Be Special (Colored)") == normalized_color_base("A Returner's Magic Should Be Special")


def test_color_words_inside_real_titles_are_not_edition_markers():
    from app.matcher import color_base_title, title_color_variant

    for title in ("Colored Pencil", "The Colored Lands", "Work Colored", "Black and White World"):
        assert title_color_variant(title) == "unknown"
        assert color_base_title(title) == title

    assert title_color_variant("Work - Colored") == "colored"
    assert color_base_title("Work - Colored") == "Work"
    assert title_color_variant("[Colored] Work") == "colored"
    assert color_base_title("[Colored] Work") == "Work"


def test_source_suffix_cleanup_is_provenance_bound():
    from app.matcher import strip_known_source_suffix

    assert strip_known_source_suffix(
        "Son-in-Law Does Cheap Cultivation – Manhuaus", ["ManhuaUS"]
    ) == "Son-in-Law Does Cheap Cultivation"
    assert strip_known_source_suffix(
        "Hero Returns | MangaFire", ["MangaFire (EN)"]
    ) == "Hero Returns"
    # A real subtitle is untouched when it is not the owning source name.
    assert strip_known_source_suffix("Love – War", ["ManhuaUS"]) == "Love – War"


def test_line_wrapped_description_alias_is_reassembled_not_cut_in_half():
    text = (
        "Associated Names: I Became a Student at the\n"
        "Transcension Academy | Became a Student at Transcendental Academy\n"
        "Status: Ongoing"
    )
    aliases = extract_alt_titles(text)
    assert "I Became a Student at the Transcension Academy" in aliases
    assert "I Became a Student at the" not in aliases


def test_unfinished_wrapped_alias_is_dropped_before_metadata_header():
    assert extract_alt_titles(
        "Associated Names: I Became a Student at the\nStatus: Ongoing"
    ) == []
