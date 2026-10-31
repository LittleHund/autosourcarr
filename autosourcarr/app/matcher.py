import re
import unicodedata
from dataclasses import dataclass
from functools import lru_cache
from typing import Iterable

from rapidfuzz.fuzz import ratio, token_set_ratio
from unidecode import unidecode


PUNCT_RE = re.compile(r"[^\w\s]+", re.UNICODE)
SPACE_RE = re.compile(r"\s+")
URL_RE = re.compile(r"https?://\S+|www\.\S+", re.I)
HTML_TAG_RE = re.compile(r"<[^>]+>")
MARKDOWN_IMAGE_RE = re.compile(r"!\[[^\]]*\]\([^)]*\)")
MARKDOWN_LINK_RE = re.compile(r"\[([^\]]+)\]\([^)]*\)")
MARKDOWN_DECORATION_RE = re.compile(r"(?:\*\*|\*|__|~~|`)")

# Title/alias blocks seen in MangaDex, Comick, MangaUpdates-style descriptions and
# several Mihon extensions.  These are metadata, not synopsis prose.
ALT_HEADER_RE = re.compile(
    r"(?im)^\s*(?:\*{0,2})?(?:"
    r"alternative\s+(?:titles?|names?)|alternate\s+(?:titles?|names?)|alt\.?\s+(?:titles?|names?)|"
    r"associated\s+name(?:\(s\)|s)?|synonyms?|other\s+names?|aliases?"
    r")(?:\*{0,2})?\s*[:\-]?\s*(.*)$"
)

# Sections after the actual synopsis.  Once one of these starts, the remaining text
# is source/publisher metadata or related-work navigation and must not participate in
# summary comparison.
HARD_SUMMARY_SECTION_RE = re.compile(
    r"(?i)(?:\*{0,2})?(?:"
    r"chronology\s+of\s+works(?:\s+in\s+this\s+universe)?|"
    r"official\s+translations?|original\s+(?:webtoon|manga|novel|manhwa|manhua)|"
    r"external\s+links?|related\s+works?|related\s+series|where\s+to\s+read"
    r")(?:\*{0,2})?\s*:"
)

# Alias/metadata headings also occur inline after synopsis prose on some sources.
# Anything from one of these labels onward is not synopsis content.
INLINE_ALIAS_SECTION_RE = re.compile(
    r"(?i)(?:\*{0,2})?(?:"
    r"alternative\s+(?:titles?|names?)|alternate\s+(?:titles?|names?)|alt\.?\s+(?:titles?|names?)|"
    r"associated\s+name(?:\(s\)|s)?|synonyms?|other\s+names?|aliases?"
    r")(?:\*{0,2})?\s*:"
)

# Metadata lines that may appear before/after a synopsis.  Skip the individual line,
# but do not necessarily discard later prose.
LINE_METADATA_RE = re.compile(
    r"(?i)^\s*(?:[-•]\s*)?(?:\*{0,2})?(?:"
    r"authors?|artists?|genres?|status|serialization|publishers?|publication|type|demographic|"
    r"licensed|magazine|original\s+language|translated\s+language|source|sources"
    r")(?:\*{0,2})?\s*:"
)

CREDIT_LINE_RE = re.compile(
    r"(?i)^\s*(?:\(|\[)?\s*(?:from|source|summary\s+from|description\s+from)\s+[\w .&'’\-]+(?:\)|\])?\s*\.?\s*$"
)

STOP_HEADER_RE = re.compile(r"(?m)^\s*[A-Z][A-Za-z ()/&'’\-]{2,40}\s*:\s*")



# Colored/monochrome edition markers.
#
# IMPORTANT: these expressions only recognise *edition qualifiers*.  A real work
# title may legitimately contain words such as "Colored" (for example
# ``Colored Pencil`` or ``The Colored Lands``).  Older v0.2.30-v0.2.35 matching
# treated a standalone ``colored`` token anywhere in a title as an edition marker,
# stripped it from the canonical title and could therefore create false identity
# evidence.  Brackets or an end-of-title qualifier are required for Latin markers.
_COLOR_BODY = r"(?:full[ -]?colou?r|colou?red|colou?r\s+(?:edition|version|ver\.?|ver)|official\s+colou?r|digital\s+colou?r)"
_MONO_BODY = r"(?:monochrome(?:\s+(?:edition|version|ver\.?|ver))?|black\s*(?:and|&)\s*white|b\s*&?\s*w)"
_COLOR_CJK_BODY = r"(?:カラー(?:版)?|フルカラー|彩色(?:版)?|全彩(?:版)?|컬러(?:판)?|채색(?:판)?)"
_MONO_CJK_BODY = r"(?:モノクロ(?:版)?|白黒(?:版)?|흑백(?:판)?)"

# Bracketed markers are unambiguous in either prefix or suffix position.
COLOR_BRACKET_RE = re.compile(rf"(?i)[\[(（【]\s*(?:{_COLOR_BODY}|{_COLOR_CJK_BODY})\s*[\])）】]")
MONO_BRACKET_RE = re.compile(rf"(?i)[\[(（【]\s*(?:{_MONO_BODY}|{_MONO_CJK_BODY})\s*[\])）】]")

# Unbracketed Latin markers must be a suffix introduced by punctuation.  We also
# accept explicit ``Color Edition/Version`` suffixes after whitespace because the
# phrase itself states that it is an edition.  A bare trailing ``Colored`` without
# punctuation is deliberately left unknown rather than risking a false merge.
COLOR_SUFFIX_RE = re.compile(
    rf"(?i)(?:\s*[-:–—]\s*(?:{_COLOR_BODY})|\s+(?:colou?r\s+(?:edition|version|ver\.?|ver)|full[ -]?colou?r|official\s+colou?r|digital\s+colou?r)|\s*(?:{_COLOR_CJK_BODY}))\s*$"
)
MONO_SUFFIX_RE = re.compile(
    rf"(?i)(?:\s*[-:–—]\s*(?:{_MONO_BODY})|\s+(?:monochrome\s+(?:edition|version|ver\.?|ver)|black\s*(?:and|&)\s*white)|\s*(?:{_MONO_CJK_BODY}))\s*$"
)

# Compatibility aliases used by a few internal callers/tests.  They intentionally
# no longer match arbitrary marker words in the middle of a title.
COLOR_TITLE_RE = re.compile(
    rf"(?i)(?:[\[(（【]\s*(?:{_COLOR_BODY}|{_COLOR_CJK_BODY})\s*[\])）】]|(?:\s*[-:–—]\s*(?:{_COLOR_BODY})|\s+(?:colou?r\s+(?:edition|version|ver\.?|ver)|full[ -]?colou?r|official\s+colou?r|digital\s+colou?r)|\s*(?:{_COLOR_CJK_BODY}))\s*$)"
)
MONO_TITLE_RE = re.compile(
    rf"(?i)(?:[\[(（【]\s*(?:{_MONO_BODY}|{_MONO_CJK_BODY})\s*[\])）】]|(?:\s*[-:–—]\s*(?:{_MONO_BODY})|\s+(?:monochrome\s+(?:edition|version|ver\.?|ver)|black\s*(?:and|&)\s*white)|\s*(?:{_MONO_CJK_BODY}))\s*$)"
)

# Manga/webtoon adaptations are separate works in AutoSourcarr.  Only explicit
# edition/adaptation markers count: a normal title containing the word "manga" is
# not enough to classify it.
WEBTOON_TITLE_RE = re.compile(
    r"(?i)(?:[\[(（【]\s*webtoon\s*[\])）】]|\bwebtoon\s+(?:version|edition|adaptation)\b|"
    r"\b(?:version|edition|adaptation)\s*:\s*webtoon\b|웹툰(?:판|버전)?|ウェブトゥーン(?:版)?)"
)
MANGA_TITLE_RE = re.compile(
    r"(?i)(?:[\[(（【]\s*manga\s*[\])）】]|\bmanga\s+(?:version|edition|adaptation)\b|"
    r"\b(?:version|edition|adaptation)\s*:\s*manga\b|漫画版|マンガ版|만화판|망가판)"
)
MEDIA_SUFFIX_RE = re.compile(
    r"(?i)\s*(?:[\[(（【]\s*(?:manga|webtoon)\s*[\])）】]|"
    r"(?:manga|webtoon)\s+(?:version|edition|adaptation))\s*$"
)


def title_media_variant(value: str) -> str:
    """Return ``manga``, ``webtoon`` or ``unknown`` from explicit title text."""
    raw = unicodedata.normalize("NFKC", value or "")
    if not raw.strip():
        return "unknown"
    webtoon = bool(WEBTOON_TITLE_RE.search(raw))
    manga = bool(MANGA_TITLE_RE.search(raw))
    if webtoon == manga:
        return "unknown"
    return "webtoon" if webtoon else "manga"


def media_base_title(value: str) -> str:
    raw = unicodedata.normalize("NFKC", value or "")
    raw = MEDIA_SUFFIX_RE.sub("", raw)
    raw = WEBTOON_TITLE_RE.sub(" ", raw)
    raw = MANGA_TITLE_RE.sub(" ", raw)
    raw = re.sub(r"\s{2,}", " ", raw).strip(" -–—:[]()（）【】")
    return raw.strip()


def normalized_media_base(value: str) -> str:
    return normalize_title(media_base_title(value))


def title_color_variant(value: str) -> str:
    """Return ``colored``, ``monochrome`` or ``unknown`` for strict edition labels.

    Ordinary semantic uses of words such as ``colored`` are intentionally unknown.
    Page sampling can still classify an unlabeled edition later.
    """
    raw = unicodedata.normalize("NFKC", value or "")
    if not raw.strip():
        return "unknown"
    mono = bool(MONO_BRACKET_RE.search(raw) or MONO_SUFFIX_RE.search(raw))
    color = bool(COLOR_BRACKET_RE.search(raw) or COLOR_SUFFIX_RE.search(raw))
    if mono == color:
        return "unknown"
    return "monochrome" if mono else "colored"


def color_base_title(value: str) -> str:
    """Strip only a verified colored/monochrome *edition qualifier*.

    This function must be identity-preserving for ordinary titles.  In particular
    ``Colored Pencil`` remains ``Colored Pencil`` instead of becoming ``Pencil``.
    """
    raw = unicodedata.normalize("NFKC", value or "")
    if title_color_variant(raw) == "unknown":
        return raw.strip()
    raw = COLOR_BRACKET_RE.sub(" ", raw)
    raw = MONO_BRACKET_RE.sub(" ", raw)
    raw = COLOR_SUFFIX_RE.sub("", raw)
    raw = MONO_SUFFIX_RE.sub("", raw)
    raw = re.sub(r"\s{2,}", " ", raw).strip(" -–—:[]()（）【】")
    return raw.strip()


def normalized_color_base(value: str) -> str:
    return normalize_title(color_base_title(value))

PLACEHOLDER_TITLE_RE = re.compile(
    r"^(?:"
    r"n/?a|n\s+a|na|none|null|unknown|untitled|no\s+title|"
    r"no\s+(?:alternative|alternate|associated|other)\s+(?:title|titles|name|names|synonym|synonyms|alias|aliases)|"
    r"no\s+synonyms?|no\s+aliases?|no\s+other\s+stored\s+titles"
    r")$",
    re.I,
)


@lru_cache(maxsize=32768)
def normalize_title(value: str) -> str:
    value = unicodedata.normalize("NFKC", value or "")
    value = unidecode(value).casefold()
    value = value.replace("&", " and ")
    value = PUNCT_RE.sub(" ", value)
    return SPACE_RE.sub(" ", value).strip()


_SOURCE_SUFFIX_SEPARATOR_RE = re.compile(r"\s*(?:[-–—|·•]+)\s*([^|–—]+?)\s*$")
_SOURCE_LANG_SUFFIX_RE = re.compile(r"(?i)\s*[\[(]\s*[a-z]{2,3}\s*[\])]\s*$")


def strip_known_source_suffix(value: str, source_names: Iterable[str] = ()) -> str:
    """Strip a trailing source/site label only when it matches the owning source.

    Upstream extensions occasionally expose browser/page titles such as
    ``Son-in-Law Does Cheap Cultivation – ManhuaUS``.  The site label is not part
    of the work identity and should never become a canonical/alternate title.
    This helper is intentionally provenance-bound: arbitrary dash subtitles are
    preserved unless the suffix normalizes to one of the supplied source names.
    """
    raw = " ".join(unicodedata.normalize("NFKC", value or "").split()).strip()
    if not raw:
        return ""
    wanted: set[str] = set()
    for name in source_names:
        name = " ".join(str(name or "").split()).strip()
        if not name:
            continue
        variants = {name, _SOURCE_LANG_SUFFIX_RE.sub("", name).strip()}
        for variant in variants:
            normalized = normalize_title(variant)
            if normalized:
                wanted.add(normalized)
                if normalized.endswith(" scans"):
                    wanted.add(normalized[:-6].strip())
    if not wanted:
        return raw

    match = _SOURCE_SUFFIX_SEPARATOR_RE.search(raw)
    if not match:
        return raw
    suffix = normalize_title(match.group(1))
    if suffix not in wanted:
        return raw
    cleaned = raw[:match.start()].strip(" \t-–—|·•")
    return cleaned if is_meaningful_title(cleaned) else raw


def is_meaningful_title(value: str) -> bool:
    """Reject scraper/UI placeholders that must never become match evidence."""
    normalized = normalize_title(value)
    if not normalized or len(normalized) < 2:
        return False
    if PLACEHOLDER_TITLE_RE.fullmatch(normalized):
        return False
    return True


_ALIAS_FRAGMENT_RE = re.compile(
    r"(?i)^(?:"
    r"did(?:n['’]?t)?|do(?:n['’]?t)?|does(?:n['’]?t)?|is(?:n['’]?t)?|are(?:n['’]?t)?|"
    r"was(?:n['’]?t)?|were(?:n['’]?t)?|can(?:['’]?t)?|could(?:n['’]?t)?|"
    r"would(?:n['’]?t)?|should(?:n['’]?t)?|won(?:['’]?t)?|right|really|still"
    r")\b"
)


def is_alias_fragment(value: str) -> bool:
    """Reject obvious sentence/subtitle fragments accidentally extracted as aliases.

    Older descriptions sometimes contain comma-bearing titles without perfectly
    balanced wrapper punctuation.  A historical parser could therefore leave rows
    such as ``Didn't I?~`` behind.  These short interrogative continuations are not
    standalone work titles and must never become search/match aliases.
    """
    raw = " ".join((value or "").split()).strip(" \t-•")
    if not raw:
        return True
    normalized = normalize_title(raw)
    tokens = normalized.split()
    if len(tokens) > 5:
        return False
    dangling_wrapper = raw.endswith(("~", "～", "〜")) or raw.startswith(("~", "～", "〜"))
    interrogative = bool(re.search(r"[?!][~～〜]?$", raw))
    if (dangling_wrapper or interrogative) and _ALIAS_FRAGMENT_RE.match(raw):
        return True
    # Very short clause fragments beginning with a continuation pronoun/auxiliary
    # and ending as a sentence are also unsafe. This intentionally does not reject
    # normal short titles such as ``Bleach`` or ``Monster``.
    if len(tokens) <= 4 and interrogative:
        first = tokens[0] if tokens else ""
        if first in {"i","you","he","she","we","they","it","didn","didnt","isn","isnt","aren","arent","wasn","wasnt","right"}:
            return True
    # Line-wrapped Associated Names blocks can split a title after a connective,
    # e.g. ``I Became a Student at the`` + ``Transcension Academy``. A fragment
    # ending in one of these words is not a complete title by itself.
    if 2 <= len(tokens) <= 12 and tokens[-1] in {
        "a", "an", "the", "of", "at", "to", "in", "for", "with", "from", "by",
        "and", "or", "as", "on", "into", "about",
    }:
        return True
    return False


def is_single_word_title(value: str) -> bool:
    """Return whether a normalized title consists of one token.

    Single-word aliases are extremely noisy (for example ``King``, ``Skill`` or
    ``Love``).  They are only safe matching evidence when they are the actual full
    title supplied by a source, not an extracted/generated/manual alias.
    """
    normalized = normalize_title(value)
    return bool(normalized) and len(normalized.split()) == 1


def is_match_evidence_title(value: str, title_type: str = "", *, primary: bool = False) -> bool:
    """Whether a title may be used as automatic matching/duplicate evidence.

    Multi-word meaningful titles are accepted.  A single-word title is accepted
    only when it is the primary title currently returned by a source, or a stored
    source/main-library title whose provenance says it was the complete title.
    """
    if not is_meaningful_title(value):
        return False
    kind = str(title_type or "").strip().lower()
    # Source display titles are persisted as matched aliases so Suwayomi/Migrate can
    # find them exactly. They must not feed back into automatic matching, otherwise
    # one bad automatic source match could teach the entry a bad alias and cascade.
    if kind in {"matched-alias", "cached", "cached-title"} or kind.startswith("translated-"):
        return False
    if not is_single_word_title(value):
        return True
    if primary:
        return True
    return kind in {"source", "source-variant", "main-library"}


def title_similarity(a: str, b: str) -> float:
    if not is_meaningful_title(a) or not is_meaningful_title(b):
        return 0.0
    na, nb = normalize_title(a), normalize_title(b)
    if not na or not nb:
        return 0.0
    if na == nb:
        return 1.0

    # token_set_ratio intentionally ignores extra words. That is useful for loose
    # search ranking, but catastrophic for automatic matching of short titles:
    # ``King`` vs ``Demon King`` and ``Demon King`` vs ``The Demon King Returns``
    # can otherwise score 100%. For one/two-word titles, containment is never
    # equivalent to equality; use ordinary edit similarity instead. Longer titles
    # retain token-set tolerance for punctuation/word-order/localization noise.
    ta, tb = na.split(), nb.split()
    if min(len(ta), len(tb)) <= 2:
        return ratio(na, nb) / 100.0
    return max(ratio(na, nb), token_set_ratio(na, nb)) / 100.0


def automatic_title_similarity(a: str, b: str) -> float:
    """Length-sensitive title similarity used only for automatic attachment.

    Automatic matching deliberately uses the normalized whole-title edit ratio.
    Extra prefixes/suffixes therefore lower the score instead of being ignored by
    token-set or substring logic.  This prevents a short title such as ``God of
    War`` from scoring as an exact match against unrelated longer works merely
    because those words appear somewhere in the title.

    The caller applies ``AUTO_MATCH_THRESHOLD`` (0.94 by default), so a source
    title must be at least 94% similar to the preferred title or a trusted known
    alternate title. Independent summary evidence must not lower that floor.
    """
    if not is_meaningful_title(a) or not is_meaningful_title(b):
        return 0.0
    na, nb = normalize_title(a), normalize_title(b)
    if not na or not nb:
        return 0.0
    if na == nb:
        return 1.0
    return ratio(na, nb) / 100.0


def best_automatic_title_score(left: Iterable[str], right: Iterable[str]) -> tuple[float, str, str]:
    best = 0.0
    best_left = ""
    best_right = ""
    for lval in left:
        if not is_meaningful_title(lval):
            continue
        for rval in right:
            if not is_meaningful_title(rval):
                continue
            score = automatic_title_similarity(lval, rval)
            if score > best:
                best = score
                best_left = lval
                best_right = rval
    return best, best_left, best_right


def name_similarity(a: str, b: str) -> float:
    """Similarity for creator/person names; unlike titles, initials are meaningful."""
    na, nb = normalize_title(a), normalize_title(b)
    if not na or not nb:
        return 0.0
    if na == nb:
        return 1.0
    return max(ratio(na, nb), token_set_ratio(na, nb)) / 100.0


def _strip_markdown_and_links(value: str) -> str:
    value = MARKDOWN_IMAGE_RE.sub(" ", value)
    # Keep link labels when they are part of prose, but remove the target URL.
    value = MARKDOWN_LINK_RE.sub(r"\1", value)
    value = URL_RE.sub(" ", value)
    value = HTML_TAG_RE.sub(" ", value)
    value = MARKDOWN_DECORATION_RE.sub("", value)
    return value


@lru_cache(maxsize=8192)
def clean_summary(value: str) -> str:
    """Return synopsis prose suitable for display and similarity comparisons.

    Source descriptions often append alternate names, related-work chronology,
    official links, scanlator credits and other metadata.  Treating those blocks as
    prose causes false duplicate matches (especially when both pages say things like
    "No alternative titles").  This sanitizer keeps the actual synopsis while
    discarding those non-summary sections and URL noise.
    """
    value = unicodedata.normalize("NFKC", value or "")
    if not value.strip():
        return ""
    value = re.sub(r"(?i)<br\s*/?>", "\n", value)
    value = value.replace("\r", "")

    kept: list[str] = []
    skipping_alias_block = False
    stop_after_line = False

    for raw_line in value.split("\n"):
        line = raw_line.strip()
        if not line:
            # Alias blocks are usually separated from synopsis prose by a blank line.
            if skipping_alias_block:
                skipping_alias_block = False
            continue

        alt = ALT_HEADER_RE.match(line)
        if alt:
            skipping_alias_block = True
            continue
        if skipping_alias_block:
            # If a recognizable metadata header starts, leave alias-skip mode and
            # process it normally. Otherwise this line is another alias and is dropped.
            if STOP_HEADER_RE.match(line):
                skipping_alias_block = False
            else:
                continue

        # Related-work/link sections may be appended to the end of an otherwise good
        # synopsis on the same line. Keep only the prose before the first marker.
        hard = HARD_SUMMARY_SECTION_RE.search(line)
        alias_inline = INLINE_ALIAS_SECTION_RE.search(line)
        markers = [m for m in (hard, alias_inline) if m is not None]
        if markers:
            marker = min(markers, key=lambda m: m.start())
            prefix = _strip_markdown_and_links(line[:marker.start()].strip()).strip()
            prefix = re.sub(
                r"(?i)\s*\(?\s*(?:summary\s+)?from\s+(?:line\s+webtoon|webtoon|mangadex|comick|mangaupdates)\s*\)?\s*\.?\s*$",
                "",
                prefix,
            ).strip()
            if prefix:
                kept.append(prefix)
            stop_after_line = True
            break

        if LINE_METADATA_RE.match(line):
            continue

        simplified = _strip_markdown_and_links(line).strip()
        if not simplified or CREDIT_LINE_RE.match(simplified):
            continue

        # Common trailing credit phrase embedded after synopsis text.
        simplified = re.sub(
            r"(?i)\s*\(?\s*(?:summary\s+)?from\s+(?:line\s+webtoon|webtoon|mangadex|comick|mangaupdates)\s*\)?\s*\.?\s*$",
            "",
            simplified,
        ).strip()
        if simplified:
            kept.append(simplified)
        if stop_after_line:
            break

    text = SPACE_RE.sub(" ", " ".join(kept)).strip(" \t-•")
    if not text:
        return ""

    # Metadata-only placeholders are not summaries.
    if PLACEHOLDER_TITLE_RE.fullmatch(normalize_title(text)):
        return ""

    # Reject obvious repeated-token garbage while leaving normal short summaries and
    # non-Latin prose alone. This is intentionally conservative.
    words = [w for w in re.findall(r"\w+", text, re.UNICODE) if w]
    if len(words) >= 20:
        normalized_words = [normalize_title(w) for w in words if normalize_title(w)]
        if normalized_words:
            unique_ratio = len(set(normalized_words)) / len(normalized_words)
            if unique_ratio < 0.12:
                return ""
    return text


def is_meaningful_summary(value: str) -> bool:
    """Return whether cleaned text looks like synopsis prose rather than metadata/noise."""
    text = clean_summary(value)
    if not text or len(text) < 40:
        return False
    words = re.findall(r"\w+", text, re.UNICODE)
    if len(words) < 8:
        return False
    # Require actual letter content.  Unicode ``isalpha`` keeps CJK/Korean valid.
    letters = sum(1 for ch in text if ch.isalpha())
    visible = sum(1 for ch in text if not ch.isspace())
    if visible and letters / visible < 0.45:
        return False
    normalized_words = [normalize_title(w) for w in words if normalize_title(w)]
    if len(normalized_words) >= 12 and len(set(normalized_words)) / len(normalized_words) < 0.22:
        return False
    return True


@lru_cache(maxsize=8192)
def normalize_description(value: str) -> str:
    text = clean_summary(value)
    if not text:
        return ""
    text = unidecode(text).casefold()
    text = PUNCT_RE.sub(" ", text)
    return SPACE_RE.sub(" ", text).strip()


def description_similarity(a: str, b: str) -> float:
    if not is_meaningful_summary(a) or not is_meaningful_summary(b):
        return 0.0
    na, nb = normalize_description(a), normalize_description(b)
    # Avoid matching generic fragments or publisher boilerplate. The summary cleaner
    # has already removed alias/link/related-work sections before this length gate.
    if len(na) < 40 or len(nb) < 40:
        return 0.0
    if na == nb:
        return 1.0
    direct = ratio(na, nb) / 100.0
    length_ratio = min(len(na), len(nb)) / max(len(na), len(nb))
    token_score = token_set_ratio(na, nb) / 100.0 if length_ratio >= 0.75 else 0.0
    return max(direct, token_score)


def best_description_score(candidate: str, existing: Iterable[str]) -> float:
    return max((description_similarity(candidate, value) for value in existing if value), default=0.0)


def best_title_score(left: Iterable[str], right: Iterable[str]) -> tuple[float, int]:
    lvals = [x for x in left if is_meaningful_title(x)]
    rvals = [x for x in right if is_meaningful_title(x)]
    best = 0.0
    second = 0.0
    strong = 0
    compared = 0
    for l in lvals:
        for r in rvals:
            value = title_similarity(l, r)
            compared += 1
            if value >= 0.97:
                strong += 1
            if value > best:
                second, best = best, value
            elif value > second:
                second = value
    if not compared:
        return 0.0, 0
    score = best
    if strong >= 2:
        score = max(score, 0.995)
    elif second >= 0.92:
        score = min(1.0, score + 0.025)
    return score, strong


def extract_alt_titles(description: str) -> list[str]:
    if not description:
        return []
    out: list[str] = []
    lines = description.replace("\r", "").split("\n")
    active = False
    pending = ""

    def append_alias_text(value: str) -> None:
        nonlocal pending
        parts = _split_aliases(value)
        if not parts:
            return
        if pending:
            parts[0] = f"{pending} {parts[0]}".strip()
            pending = ""
        for part in parts:
            part = part.strip()
            if not part:
                continue
            normalized = normalize_title(part)
            tokens = normalized.split()
            if 2 <= len(tokens) <= 12 and tokens[-1] in {
                "a", "an", "the", "of", "at", "to", "in", "for", "with", "from",
                "by", "and", "or", "as", "on", "into", "about",
            }:
                pending = part
                continue
            out.append(part)

    for line in lines:
        text = line.strip()
        m = ALT_HEADER_RE.match(text)
        if m:
            active = True
            inline = m.group(1).strip()
            if inline:
                append_alias_text(inline)
            continue
        if active:
            if not text:
                if out and not pending:
                    break
                continue
            if STOP_HEADER_RE.match(text):
                # A metadata header cannot continue a wrapped title. Drop any
                # unfinished fragment rather than creating ``... at Status: ...``.
                pending = ""
                break
            append_alias_text(text)
            if len(out) >= 30:
                break
    # Do not persist an unfinished trailing fragment. If the next wrapped line was
    # missing, a value such as ``I Became a Student at the`` is actively harmful
    # matching evidence.
    seen = set()
    result = []
    for title in out:
        n = normalize_title(title)
        if is_meaningful_title(title) and not is_alias_fragment(title) and n not in seen and len(title) <= 250:
            seen.add(n)
            result.append(title.strip())
    return result


_TITLE_PAIR_DELIMITERS = {
    "(": ")", "[": "]", "{": "}",
    "（": "）", "［": "］", "｛": "｝",
    "「": "」", "『": "』", "【": "】", "〈": "〉", "《": "》", "〔": "〕",
    "“": "”", "‘": "’",
}
_TITLE_SAME_DELIMITERS = frozenset({"~", "～", "〜"})


def _split_top_level(value: str, separators: tuple[str, ...]) -> list[str]:
    """Split only outside title punctuation / subtitle wrappers.

    Manga titles commonly embed subtitles or clauses inside paired punctuation, e.g.
    ``Title ～Subtitle, with comma～``.  Treat separators inside those wrappers as
    literal title characters.  This also protects commas inside brackets and quoted
    title segments instead of trying to infer their meaning afterward.
    """
    if not value:
        return []
    same_enabled = {d for d in _TITLE_SAME_DELIMITERS if value.count(d) >= 2}
    stack: list[str] = []
    same_open: str | None = None
    ascii_quote = False
    out: list[str] = []
    buf: list[str] = []
    i = 0
    while i < len(value):
        ch = value[i]
        if ch == '"':
            ascii_quote = not ascii_quote
            buf.append(ch)
            i += 1
            continue
        if not ascii_quote:
            if ch in same_enabled:
                if same_open == ch:
                    same_open = None
                elif same_open is None:
                    same_open = ch
                buf.append(ch)
                i += 1
                continue
            if same_open is None:
                if ch in _TITLE_PAIR_DELIMITERS:
                    stack.append(_TITLE_PAIR_DELIMITERS[ch])
                    buf.append(ch)
                    i += 1
                    continue
                if stack and ch == stack[-1]:
                    stack.pop()
                    buf.append(ch)
                    i += 1
                    continue
                if not stack:
                    matched = next((sep for sep in separators if value.startswith(sep, i)), None)
                    if matched is not None:
                        piece = ''.join(buf).strip(" -•\t")
                        if piece:
                            out.append(piece)
                        buf = []
                        i += len(matched)
                        continue
        buf.append(ch)
        i += 1
    piece = ''.join(buf).strip(" -•\t")
    if piece:
        out.append(piece)
    return out


def _split_aliases(value: str) -> list[str]:
    # Strong separators are only separators at the top title level.  Commas are
    # trickier because they may separate aliases or be grammatical punctuation.
    # Most importantly, commas inside paired subtitle punctuation (～...～, ~...~,
    # brackets or quotes) are *never* split.
    strong_parts = _split_top_level(value, (" / ", "|", ";", "•"))
    out: list[str] = []
    for part in strong_parts:
        part = part.strip(" -•\t")
        if not part:
            continue
        comma_parts = _split_comma_aliases(part)
        out.extend(comma_parts if comma_parts else [part])
    return out


_COMMA_CONTINUATION_WORDS = frozenset({
    "a", "an", "and", "as", "at", "because", "before", "but", "by", "for",
    "from", "how", "if", "in", "is", "of", "on", "or", "that", "the", "then",
    "to", "was", "when", "where", "which", "while", "who", "why", "with", "without",
    # A comma inside a real title very often introduces a second clause beginning
    # with a pronoun/determiner. Treat those as grammatical continuation instead of
    # a new alias. This protects titles such as:
    #   ... Before I Started Lending, I Told You This Loan Charges 10% ...
    "i", "you", "he", "she", "we", "they", "it", "me", "him", "her", "us", "them",
    "my", "your", "his", "our", "their", "this", "these", "those", "there", "here",
})


def _looks_like_comma_alias_item(value: str) -> bool:
    value = value.strip(" \t-•")
    if not is_meaningful_title(value) or len(value) > 250:
        return False
    # A one-character fragment is almost always a grammatical title fragment (for
    # example the ``I`` in ``I, Who...``), not a standalone alias.
    if len(normalize_title(value)) <= 1:
        return False
    first_word = normalize_title(value).split()[0] if normalize_title(value) else ""
    if first_word in _COMMA_CONTINUATION_WORDS:
        return False
    # Lowercase Latin starts normally continue the same title. Native-script, digit
    # and symbol starts are allowed because translated-name lists often contain them.
    first_alpha = next((ch for ch in value if ch.isalpha()), "")
    if first_alpha and "LATIN" in unicodedata.name(first_alpha, "") and first_alpha.islower():
        return False
    return True


def _split_comma_aliases(value: str) -> list[str]:
    if "," not in value:
        return []
    parts = [p.strip(" \t-•") for p in _split_top_level(value, (",",))]
    if len(parts) < 2 or any(not p for p in parts):
        return []
    if not all(_looks_like_comma_alias_item(p) for p in parts):
        return []
    # Two-part comma titles are common, so require stronger list evidence: both
    # sides should contain at least two normalized tokens (or be native-script).
    if len(parts) == 2:
        def strong_item(part: str) -> bool:
            normalized = normalize_title(part)
            tokens = normalized.split()
            if len(tokens) >= 2:
                return True
            letters = [ch for ch in part if ch.isalpha()]
            return bool(letters) and not all("LATIN" in unicodedata.name(ch, "") for ch in letters)
        if not all(strong_item(p) for p in parts):
            return []
    return parts


def looks_english(title: str) -> bool:
    if not title:
        return False
    letters = [c for c in title if c.isalpha()]
    if not letters:
        return False
    latin = sum(1 for c in letters if "LATIN" in unicodedata.name(c, ""))
    return latin / len(letters) >= 0.85


ROMAJI_HINT_TOKENS = frozenset({
    "no", "wo", "o", "ni", "wa", "ga", "kara", "made", "desu", "suki", "ageru",
    "boku", "watashi", "ore", "kanojo", "kare", "sekai", "isekai", "tensei",
    "shoujo", "shojo", "shounen", "shonen", "san", "kun", "chan", "sama",
    "sensei", "senpai", "kouhai",
})


def looks_romanized_japanese(title: str) -> bool:
    tokens = set(normalize_title(title).split())
    return len(tokens & ROMAJI_HINT_TOKENS) >= 2


def looks_preferred_english(title: str) -> bool:
    return looks_english(title) and not looks_romanized_japanese(title)


@dataclass(slots=True)
class MatchDecision:
    score: float
    reason: str
    title_score: float = 0.0
    description_score: float = 0.0


def score_match(
    candidate_titles: Iterable[str],
    entry_titles: Iterable[str],
    candidate_author: str = "",
    entry_author: str = "",
    candidate_description: str = "",
    entry_descriptions: Iterable[str] = (),
) -> MatchDecision:
    title_score, strong = best_title_score(candidate_titles, entry_titles)
    description_score = best_description_score(candidate_description, entry_descriptions)
    score = title_score
    reason = "title"
    if strong >= 2:
        reason = "two-title corroboration"

    # Source matching remains stricter than duplicate auto-merge. A 90% cleaned
    # synopsis match may establish a source match even when translations differ.
    if description_score >= 0.90:
        score = max(score, 0.995)
        reason = "description>=90%" if strong < 2 else reason + "+description>=90%"
    elif description_score >= 0.75:
        score = min(1.0, score + 0.02)
        reason += "+description"

    if candidate_author and entry_author:
        author = name_similarity(candidate_author, entry_author)
        if author >= 0.92:
            score = min(1.0, score + 0.025)
            reason += "+author"
        elif author < 0.35:
            score = max(0.0, score - 0.08)
            reason += "-author"
    return MatchDecision(
        score=score,
        reason=reason,
        title_score=title_score,
        description_score=description_score,
    )
