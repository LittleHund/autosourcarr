"""Development knowledge must retain provenance without touching live state."""
import importlib.util
import sqlite3
from pathlib import Path
from zipfile import ZipFile

import pytest

SPEC = importlib.util.spec_from_file_location('knowledge_db', Path(__file__).resolve().parents[2] / 'scripts' / 'knowledge-db.py')
knowledge = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(knowledge)


def archive(tmp_path, name, contents):
    target = tmp_path / name
    with ZipFile(target, 'w') as out:
        for path, text in {'VERSION': 'test', **contents}.items():
            out.writestr(path, text)
    return target


def record(**changes):
    return {'id': 'FACT-1', 'kind': 'component', 'title': 'Example', 'body': 'Returns one',
            'state': 'confirmed', 'origin': 'code_inspection', 'source_ref': 'fixture',
            'applies_to': 'test', 'evidence': [{'path': 'app/a.py', 'line_start': 1, 'line_end': 2}], **changes}


def test_unrelated_database_is_never_modified(tmp_path):
    target = tmp_path / 'catalog.sqlite'
    with sqlite3.connect(target) as db:
        db.execute('CREATE TABLE live_entries(id INTEGER)')
        db.execute('INSERT INTO live_entries VALUES(17)')
    before = target.read_bytes()
    with pytest.raises(ValueError, match='unrelated database'):
        knowledge.connect(target, create=True)
    assert target.read_bytes() == before


def test_runtime_credentials_journals_and_path_traversal_are_excluded(tmp_path):
    db = knowledge.connect(tmp_path / 'knowledge.sqlite3', create=True)
    paths = {path: 'SYNTHETIC_PRIVATE_VALUE' for path in [
        'config/.setup-transaction.json', 'config/codex-home/auth.json', 'config/autosourcarr.env',
        'config/source-browser.json', '../escaped.py', '/absolute.py', 'data/catalog.sqlite',
        '.extension-signing/reader.jks', 'secrets/auth.json']}
    paths['config/autosourcarr.env.example'] = 'PUBLIC_URL=http://example.invalid'
    result = knowledge.index_archive(db, archive(tmp_path, 'source.zip', paths))
    assert result['indexed'] == 2
    assert len(result['excluded']) == 9
    assert not knowledge.search(db, 'SYNTHETIC_PRIVATE_VALUE')
    assert knowledge.search(db, 'PUBLIC_URL')
    db.close()


def test_incremental_index_and_old_evidence_stay_stale_until_explicit_review(tmp_path):
    db = knowledge.connect(tmp_path / 'knowledge.sqlite3', create=True)
    first = archive(tmp_path, 'one.zip', {'app/a.py': 'def value():\n    return 1\n'})
    knowledge.index_archive(db, first)
    assert knowledge.import_records(db, {'records': [record()]})['changed_records'] == ['FACT-1']
    assert not knowledge.import_records(db, {'records': [record()]})['changed_records']
    assert not knowledge.index_archive(db, first)['changes']
    second = archive(tmp_path, 'two.zip', {'app/a.py': 'def value():\n    return 2\n'})
    knowledge.index_archive(db, second)
    assert db.execute('SELECT count(*) FROM stale_evidence').fetchone()[0] == 1
    knowledge.import_records(db, {'records': [record()]})
    assert db.execute('SELECT count(*) FROM stale_evidence').fetchone()[0] == 1
    result = knowledge.search(db, 'Returns one', kind='component')[0]
    assert result['state'] == 'confirmed' and result['applies_to'] == 'test'
    assert result['stale_evidence_count'] == 1
    current = db.execute("SELECT sha256 FROM files WHERE path='app/a.py'").fetchone()[0]
    revised = record(body='Returns two', evidence=[{'path': 'app/a.py', 'line_start': 1, 'line_end': 2, 'file_sha256': current}])
    knowledge.import_records(db, {'records': [revised]})
    assert db.execute('SELECT count(*) FROM stale_evidence').fetchone()[0] == 0
    assert db.execute('SELECT count(*) FROM record_history').fetchone()[0] == 1
    assert db.execute("SELECT count(*) FROM file_revisions WHERE path='app/a.py'").fetchone()[0] == 2
    assert knowledge.check(db)['search_consistent']
    db.close()


def test_broken_archive_and_bad_record_batch_roll_back(tmp_path):
    db = knowledge.connect(tmp_path / 'knowledge.sqlite3', create=True)
    knowledge.index_archive(db, archive(tmp_path, 'ok.zip', {'app/a.py': 'def value():\n    return 1\n'}))
    before = knowledge.stats(db)
    with pytest.raises(SyntaxError):
        knowledge.index_archive(db, archive(tmp_path, 'bad.zip', {'app/a.py': 'invalid python ?'}))
    assert knowledge.stats(db) == before
    with pytest.raises(ValueError):
        knowledge.import_records(db, {'records': [record(), record(id='BAD', state='invalid')]})
    assert not db.execute('SELECT 1 FROM records').fetchone()
    assert knowledge.check(db)['search_consistent']
    db.close()


def test_removal_is_searchable_only_as_historical_evidence(tmp_path):
    db = knowledge.connect(tmp_path / 'knowledge.sqlite3', create=True)
    knowledge.index_archive(db, archive(tmp_path, 'one.zip', {'app/a.py': 'def unique_fixture():\n    return 1\n'}))
    knowledge.import_records(db, {'records': [record()]})
    knowledge.index_archive(db, archive(tmp_path, 'two.zip', {}))
    assert not knowledge.search(db, 'unique_fixture')
    assert db.execute('SELECT count(*) FROM symbols').fetchone()[0] == 0
    assert db.execute('SELECT count(*) FROM stale_evidence').fetchone()[0] == 1
    assert db.execute("SELECT content FROM file_revisions WHERE path='app/a.py'").fetchone()[0]
    assert knowledge.check(db)['integrity'] == ['ok']
    db.close()


def test_summary_reports_counts_without_returning_historical_ledger(tmp_path):
    db = knowledge.connect(tmp_path / 'knowledge.sqlite3', create=True)
    knowledge.index_archive(db, archive(tmp_path, 'one.zip', {'app/a.py': 'def value():\n    return 1\n'}))
    knowledge.import_records(db, {'records': [record()]})
    full = knowledge.stats(db)
    summary = knowledge.stats(db, summary=True)
    for key in ('files', 'symbols', 'endpoints', 'records_by_kind', 'findings_by_state'):
        assert summary[key] == full[key]
    assert summary['stale_evidence_count'] == 0
    assert summary['sessions_count'] == 0
    assert 'sessions' not in summary and 'stale_records' not in summary
    assert summary['metadata']['latest_version'] == 'test'
    db.close()


def test_python_index_retains_nested_symbols_routes_and_imports():
    source = '''from .helpers import value
class Example:
    async def nested(self):
        """Nested work."""
        return value()
@app.get("/items")
async def items():
    return []
'''
    symbols, routes, imports = knowledge.inspect_python('app/example.py', source)
    assert [(r[2], r[4], r[6]) for r in symbols] == [
        ('Example', 2, 'class Example:'), ('Example.nested', 3, 'async def nested(self):'),
        ('items', 7, 'async def items():'),
    ]
    assert routes == [('app/example.py:6', 'app/example.py', 'GET', '/items', 'items', 6)]
    assert imports == [('app/example.py', '.helpers', 1)]


def test_unresolved_finding_states_stay_visible_without_promoting_their_status(tmp_path):
    db = knowledge.connect(tmp_path / 'knowledge.sqlite3', create=True)
    states = ['open', 'confirmed', 'hypothesis', 'partial', 'reported', 'unverified', 'implemented', 'resolved', 'superseded']
    try:
        knowledge.import_records(db, {'records': [record(id='F-' + state, kind='finding', state=state, evidence=[]) for state in states]})
        visible = {row['state'] for row in db.execute('SELECT state FROM open_findings')}
        assert visible == set(states) - {'resolved', 'superseded'}
        assert db.execute("SELECT state FROM records WHERE id='F-partial'").fetchone()[0] == 'partial'
        assert knowledge.check(db)['search_consistent']
    finally:
        db.close()


def test_legacy_finding_view_migration_preserves_records_history_and_reopen(tmp_path):
    target = tmp_path / 'knowledge.sqlite3'
    db = knowledge.connect(target, create=True)
    knowledge.import_records(db, {'records': [record(id='F-account', kind='finding', state='partial', evidence=[])]})
    knowledge.import_records(db, {'records': [record(id='F-account', kind='finding', state='partial', body='New evidence', evidence=[])]})
    db.execute('DROP VIEW open_findings')
    db.execute("CREATE VIEW open_findings AS SELECT * FROM findings WHERE state IN ('open','confirmed','hypothesis')")
    db.commit()
    before = [tuple(row) for row in db.execute('SELECT * FROM records ORDER BY id')]
    history = [tuple(row) for row in db.execute('SELECT * FROM record_history ORDER BY sequence')]
    assert not db.execute('SELECT 1 FROM open_findings').fetchone()
    db.close()
    for _ in range(2):
        db = knowledge.connect(target)
        try:
            assert db.execute('SELECT id,state FROM open_findings').fetchone()[:] == ('F-account', 'partial')
            assert [tuple(row) for row in db.execute('SELECT * FROM records ORDER BY id')] == before
            assert [tuple(row) for row in db.execute('SELECT * FROM record_history ORDER BY sequence')] == history
            assert db.execute('PRAGMA user_version').fetchone()[0] == knowledge.SCHEMA_VERSION
        finally:
            db.close()


def test_view_migration_failure_rolls_back_its_ddl(tmp_path):
    db = knowledge.connect(tmp_path / 'knowledge.sqlite3', create=True)
    legacy = "CREATE VIEW open_findings AS SELECT * FROM findings WHERE state IN ('open','confirmed','hypothesis')"
    db.execute('DROP VIEW open_findings'); db.execute(legacy); db.commit()
    db.set_authorizer(lambda action, *args: sqlite3.SQLITE_DENY if action == sqlite3.SQLITE_CREATE_VIEW else sqlite3.SQLITE_OK)
    try:
        with pytest.raises(sqlite3.DatabaseError):
            knowledge.ensure_finding_view(db)
    finally:
        db.set_authorizer(None)
    assert db.execute("SELECT sql FROM sqlite_master WHERE name='open_findings'").fetchone()[0] == legacy
    assert not db.in_transaction
    db.close()
