#!/usr/bin/env python3
"""Phase 0 stage 5: export the merge/reject decisions and a 1,000-entry sample from a catalog.sqlite copy.

Standard library only. Opens the database read-only (immutable) and never writes to it.
Writes a folder with:
  schema.sql                 every CREATE statement (tables, indexes, triggers) of the catalog database
  decisions.json             the human decision tables, complete (merge/reject/manual/separate/tag/mixed/import)
  sample-1000.json           1,000 entries (seeded random) with titles, source records, aliases, identity links
  counts.json                row counts of every table, for sanity checks
Nothing from settings, source_config, browse_cache, translation/metadata caches, task journals or
support logs is exported; those tables can hold keys, cookies or private URLs.

Usage:
  python export-conversion-sample.py "F:\\claude projects\\autosourcarr data\\catalog.sqlite" [out-folder] [--entries 1000] [--seed 20261031]
"""
import json, os, random, sqlite3, sys, time

DECISION_TABLES = [
    "duplicate_reviews", "duplicate_review_history", "manual_match_groups", "manual_match_bindings",
    "manual_separate_records", "manual_title_scopes", "mixed_entry_reviews", "import_match_rejections",
    "import_manual_autoresolve", "import_manual_autoresolve_state", "automatic_merge_events",
    "automatic_merge_lineage", "duplicate_split_history", "pending_entry_merges", "pending_entry_splits",
    "preferred_title_overrides", "preferred_title_override_sources", "entry_preferred_titles",
    "entry_aliases", "chapter_candidate_rejections", "tag_reviews", "portable_identity_reviews",
    "portable_identity_review_history", "catalogue_record_resolutions", "catalogue_record_bindings",
    "matching_policies", "matching_presets", "matching_review_actions", "deleted_source_records",
    "deleted_entries_history", "serving_choices", "serving_upgrade_policies", "gallery_policies",
]
# per-entry tables joined on entry_id for the sample
ENTRY_CHILD_TABLES = ["titles", "source_matches", "source_variants", "identity_links", "entry_preferred_titles"]
NEVER = {"settings", "source_config", "browse_cache", "translation_cache", "metadata_query_cache",
         "general_task_history", "due_work", "crawl_pending", "candidate_pages", "served_origin",
         "quality_samples", "quality_attempts", "user_ui_preferences"}
# columns that may carry cached page addresses with tokens; dropped from every exported row
DROP_COLUMNS = {"record_json", "target_snapshot_json", "duplicate_snapshot_json"}


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        print(__doc__); return 2
    db_path = args[0]
    out = args[1] if len(args) > 1 else os.path.join(os.path.dirname(os.path.abspath(db_path)), "conversion-export")
    n_entries = int(_opt("--entries", "1000")); seed = int(_opt("--seed", "20261031"))
    if not os.path.isfile(db_path):
        print("database not found:", db_path); return 2
    os.makedirs(out, exist_ok=True)
    uri = "file:" + os.path.abspath(db_path).replace("\\", "/") + "?mode=ro&immutable=1"
    c = sqlite3.connect(uri, uri=True)
    c.row_factory = sqlite3.Row
    started = time.time()

    tables = [r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")]
    with open(os.path.join(out, "schema.sql"), "w", encoding="utf-8") as f:
        for row in c.execute("SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type, name"):
            f.write(row["sql"].strip() + ";\n\n")
    counts = {}
    for t in tables:
        counts[t] = c.execute(f'SELECT COUNT(*) FROM "{t}"').fetchone()[0]
    counts["_pragma_user_version"] = c.execute("PRAGMA user_version").fetchone()[0]
    counts["_pragma_application_id"] = c.execute("PRAGMA application_id").fetchone()[0]
    json.dump(counts, open(os.path.join(out, "counts.json"), "w"), indent=1)
    print("tables:", len(tables), "entries:", counts.get("entries"))

    decisions = {}
    for t in DECISION_TABLES:
        if t not in tables or t in NEVER: continue
        decisions[t] = [_clean(dict(r)) for r in c.execute(f'SELECT * FROM "{t}"')]
        print(f"  {t}: {len(decisions[t])}")
    json.dump({"exported_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "source": os.path.basename(db_path),
               "tables": decisions}, open(os.path.join(out, "decisions.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)

    ids = [r[0] for r in c.execute("SELECT id FROM entries ORDER BY id")]
    random.Random(seed).shuffle(ids)
    chosen = sorted(ids[:n_entries])
    sample = []
    for eid in chosen:
        entry = _clean(dict(c.execute("SELECT * FROM entries WHERE id=?", (eid,)).fetchone()))
        for t in ENTRY_CHILD_TABLES:
            if t in tables:
                entry[t] = [_clean(dict(r)) for r in c.execute(f'SELECT * FROM "{t}" WHERE entry_id=?', (eid,))]
        entry["entry_aliases"] = [_clean(dict(r)) for r in c.execute("SELECT * FROM entry_aliases WHERE canonical_entry_id=?", (eid,))]
        sample.append(entry)
    json.dump({"seed": seed, "entry_ids": chosen, "entries": sample},
              open(os.path.join(out, f"sample-{n_entries}.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    c.close()
    print(f"written to {out} in {time.time()-started:.0f}s")
    return 0


def _opt(name, default):
    for i, a in enumerate(sys.argv):
        if a == name and i + 1 < len(sys.argv): return sys.argv[i + 1]
        if a.startswith(name + "="): return a.split("=", 1)[1]
    return default


def _clean(row):
    return {k: v for k, v in row.items() if k not in DROP_COLUMNS}


if __name__ == "__main__":
    sys.exit(main())
