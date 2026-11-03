#!/usr/bin/env python3
"""Regenerate tests/AutoSourcarr.Tests/Fixtures/legacy/instance-0.4.5.ascbackup with the REAL 0.4.5 code.

The fixture is made by the Python InstanceBackups.create of autosourcarr/app/instance_backup.py from the 0.4.4 legacy
fixture SQL plus a few synthetic manual decisions, so the C# reader (Core/Cutover/LegacyBackupReader) is tested against
real Python output. No personal data: every value is synthetic and the passphrase below is public test data.

Needs the pinned `cryptography` (autosourcarr/requirements.txt), e.g. in a scratch venv:
    python -m venv .venv && .venv/bin/pip install cryptography
    .venv/bin/python scripts/make-legacy-backup-fixture.py
"""
import io
import json
import sqlite3
import zipfile
import sys
import tempfile
from pathlib import Path

PASSPHRASE = "cutover fixture passphrase 0451"  # public test passphrase (CutoverTests.FixturePassphrase)
MANUAL = """
INSERT INTO source_config(source_id,name,lang,last_seen) VALUES('src-a','Source A','en','2026-01-01');
UPDATE entries SET preferred_title='Alpha Manual',manual_preferred_title=1 WHERE id=1;
INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,match_type,locked,active,metadata_json,last_checked) VALUES
  (1,'src-a',101,'Alpha','manual',1,1,'{}','2026-01-01'),(2,'src-a',102,'Beta','auto',0,1,'{}','2026-01-01');
INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,separate_entry_id_hint,source_title,created_at,updated_at)
  VALUES(1,'src-a',102,2,'Beta','2026-01-01','2026-01-01');
INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES(1,2,'not_duplicate','2026-01-01');
INSERT INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES(1,'Alpha Alias','alpha alias','','manual','',0);
INSERT INTO library_tracking(entry_id,monitored,revision,main_manga_id,observation_json,observed_at,updated_at) VALUES(1,1,1,77,'{}','','');
INSERT INTO settings(key,value) VALUES('startup_catalog_maintenance_version','"0.2.69"'),('automatic_merge_safety_repair_version','"0.2.69"'),
  ('bound_title_consensus_version','"0.2.78"'),
  ('subbed_display_snapshot_v1','{"origin":"http://suwayomi:30058","observed_at":"2026-01-01","payload":{"entry_ids":[1,2],"stats_by_entry":{}}}');
"""


def main() -> int:
    repo = Path(__file__).resolve().parent.parent
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else repo / "tests/AutoSourcarr.Tests/Fixtures/legacy/instance-0.4.5.ascbackup"
    sys.path.insert(0, str(repo / "autosourcarr"))
    from app.instance_backup import InstanceBackups, protect  # noqa: E402  (0.4.5 code, no app imports at module scope)

    fixtures = repo / "tests/AutoSourcarr.Tests/Fixtures/legacy"
    with tempfile.TemporaryDirectory(prefix="asc-fixture-") as raw:
        work = Path(raw)
        data, config = work / "data", work / "config"
        data.mkdir(); config.mkdir()
        for name, sql, extra in (("catalog.sqlite", "catalog-0.4.4.sql", MANUAL), ("chapter-origins.sqlite", "origins-0.4.4.sql", "")):
            connection = sqlite3.connect(data / name)
            connection.executescript((fixtures / sql).read_text(encoding="utf-8") + extra)
            connection.execute("PRAGMA application_id=1095975729")
            connection.execute("PRAGMA user_version=1")
            connection.commit(); connection.close()
        (config / "autosourcarr.env").write_text(
            "DATA_DIR=/data\nDATABASE_DIR=/data\nTZ=Europe/Amsterdam\nADMIN_TOKEN=fixture-not-a-secret-0451\n"
            "MAIN_SUWAYOMI_URL=http://suwayomi:30058\nKOMF_BROWSER_URL=http://komf:8085\n", encoding="utf-8", newline="\n")
        summary = InstanceBackups({"data": data, "config": config, "db": data}, "0.4.5").create(PASSPHRASE)
        made = data / "instance-backups" / (summary["id"] + ".ascbackup")
        # The manifest records the absolute roots of this machine (a temp folder with the user name); write the
        # container paths a real 0.4.5 records instead, through the same protect() container.
        plain = protect(made.read_bytes(), PASSPHRASE, decrypt=True)
        out = io.BytesIO()
        with zipfile.ZipFile(io.BytesIO(plain)) as source, zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=3) as copy:
            for info in source.infolist():
                content = source.read(info.filename)
                if info.filename == "instance.json":
                    manifest = json.loads(content)
                    manifest["paths"] = {"config": "/config", "data": "/data", "db": "/data"}
                    content = json.dumps(manifest).encode()
                copy.writestr(info, content)
        target.write_bytes(protect(out.getvalue(), PASSPHRASE))
    print(target)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
