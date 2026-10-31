#!/usr/bin/env python3
"""Portable development knowledge index. Never open the application's live databases.

Usage: knowledge-db.py --db AutoSourcarr-knowledge.sqlite3 <command>
  index release.zip        Index a reviewed release archive incrementally.
  import records.json      Upsert evidence-backed records; retain change history.
  search 'import retry'    Search records and indexed source, literal tokens.
  show REQ-001             Show a record, provenance, and stale evidence.
  stats --summary         Bounded current coverage without the historical ledger.
  stats | check           Inspect coverage / integrity and evidence freshness.

No network, accounts or third-party packages. The database is a development
artifact; it is not a runtime catalogue, service, or autonomous research worker.
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import re
import sqlite3
import stat
import sys
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from zipfile import ZipFile

SCHEMA_VERSION = 1
APPLICATION_ID = 0x4153434B  # ASCK: distinct from the application's live SQLite files.
DB_KIND = 'autosourcarr.development.knowledge'
MAX_FILE_BYTES = 4 * 1024 * 1024
MAX_ARCHIVE_BYTES = 128 * 1024 * 1024
KINDS = {'requirement', 'decision', 'component', 'finding', 'verification',
         'limitation', 'workflow', 'research', 'dependency', 'release', 'source'}
STATES = {'requested', 'implemented', 'verified', 'reported', 'open', 'hypothesis',
          'unverified', 'superseded', 'confirmed', 'resolved', 'partial'}
FINDING_VIEW_SQL = "CREATE VIEW open_findings AS SELECT * FROM findings WHERE state NOT IN ('resolved','superseded')"
LEGACY_FINDING_VIEW_SQL = "CREATE VIEW open_findings AS SELECT * FROM findings WHERE state IN ('open','confirmed','hypothesis')"

SCHEMA = """
CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS sessions(
 id TEXT PRIMARY KEY, started_at TEXT NOT NULL, finished_at TEXT,
 requested_seconds INTEGER NOT NULL, elapsed_seconds REAL,
 scope TEXT NOT NULL, status TEXT NOT NULL, limitations TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS artifacts(
 sha256 TEXT PRIMARY KEY, filename TEXT NOT NULL, version TEXT NOT NULL,
 indexed_at TEXT NOT NULL, file_count INTEGER NOT NULL, total_bytes INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS file_revisions(
 path TEXT NOT NULL, sha256 TEXT NOT NULL, byte_count INTEGER NOT NULL,
 content TEXT, first_seen TEXT NOT NULL, PRIMARY KEY(path,sha256));
CREATE TABLE IF NOT EXISTS files(
 path TEXT PRIMARY KEY, sha256 TEXT NOT NULL, byte_count INTEGER NOT NULL,
 language TEXT NOT NULL, role TEXT NOT NULL, artifact_sha256 TEXT NOT NULL,
 present INTEGER NOT NULL DEFAULT 1, observed_at TEXT NOT NULL,
 FOREIGN KEY(path,sha256) REFERENCES file_revisions(path,sha256),
 FOREIGN KEY(artifact_sha256) REFERENCES artifacts(sha256));
CREATE TABLE IF NOT EXISTS symbols(
 id TEXT PRIMARY KEY, path TEXT NOT NULL REFERENCES files(path), name TEXT NOT NULL,
 kind TEXT NOT NULL, line_start INTEGER NOT NULL, line_end INTEGER NOT NULL,
 signature TEXT NOT NULL, summary TEXT NOT NULL DEFAULT '');
CREATE INDEX IF NOT EXISTS symbols_path ON symbols(path,line_start);
CREATE TABLE IF NOT EXISTS endpoints(
 id TEXT PRIMARY KEY, path TEXT NOT NULL REFERENCES files(path), method TEXT NOT NULL,
 route TEXT NOT NULL, handler TEXT NOT NULL, line_start INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS code_imports(
 path TEXT NOT NULL REFERENCES files(path), module TEXT NOT NULL,
 line_start INTEGER NOT NULL, PRIMARY KEY(path,module,line_start));
CREATE TABLE IF NOT EXISTS records(
 id TEXT PRIMARY KEY, kind TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL,
 state TEXT NOT NULL, confidence TEXT NOT NULL, priority TEXT NOT NULL,
 origin TEXT NOT NULL, source_ref TEXT NOT NULL, applies_to TEXT NOT NULL,
 created_at TEXT NOT NULL, updated_at TEXT NOT NULL, session_id TEXT REFERENCES sessions(id));
CREATE TABLE IF NOT EXISTS record_history(
 sequence INTEGER PRIMARY KEY AUTOINCREMENT, record_id TEXT NOT NULL,
 changed_at TEXT NOT NULL, previous_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS evidence(
 id INTEGER PRIMARY KEY AUTOINCREMENT, record_id TEXT NOT NULL REFERENCES records(id),
 path TEXT, file_sha256 TEXT, line_start INTEGER, line_end INTEGER,
 symbol TEXT NOT NULL DEFAULT '', url TEXT NOT NULL DEFAULT '',
 evidence_type TEXT NOT NULL, note TEXT NOT NULL,
 FOREIGN KEY(path,file_sha256) REFERENCES file_revisions(path,sha256));
CREATE INDEX IF NOT EXISTS evidence_record ON evidence(record_id);
CREATE TABLE IF NOT EXISTS relations(
 source_id TEXT NOT NULL REFERENCES records(id), relation TEXT NOT NULL,
 target_id TEXT NOT NULL REFERENCES records(id),
 PRIMARY KEY(source_id,relation,target_id));
CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts5(
 key UNINDEXED, kind UNINDEXED, title, body, tokenize='unicode61');
CREATE VIEW IF NOT EXISTS stale_evidence AS
 SELECT e.*, f.sha256 current_sha256, f.present
 FROM evidence e LEFT JOIN files f ON f.path=e.path
 WHERE e.path IS NOT NULL AND (f.path IS NULL OR f.present=0 OR f.sha256!=e.file_sha256);
CREATE VIEW IF NOT EXISTS requirements AS SELECT * FROM records WHERE kind='requirement';
CREATE VIEW IF NOT EXISTS findings AS SELECT * FROM records WHERE kind='finding';
CREATE VIEW IF NOT EXISTS open_findings AS SELECT * FROM findings WHERE state NOT IN ('resolved','superseded');
CREATE VIEW IF NOT EXISTS verification AS SELECT * FROM records WHERE kind='verification';
CREATE VIEW IF NOT EXISTS current_source AS
 SELECT f.*, r.content FROM files f JOIN file_revisions r USING(path,sha256) WHERE f.present=1;
"""


def now():
    return datetime.now(timezone.utc).isoformat(timespec='seconds')


def ensure_finding_view(connection):
    """Compatible view-only migration; never reclassify or rewrite a finding.

    Keep schema1 readable by older archive tools. Unknown manual view definitions
    are retained rather than replaced with an inferred contract.
    """
    row = connection.execute("SELECT sql FROM sqlite_master WHERE type='view' AND name='open_findings'").fetchone()
    normalize = lambda sql: ' '.join(sql.strip().rstrip(';').split()).casefold()
    current = normalize(row[0]) if row else None
    if current == normalize(FINDING_VIEW_SQL):
        return
    if current is not None and current != normalize(LEGACY_FINDING_VIEW_SQL):
        raise ValueError('Unrecognized open_findings view; its definition and records were retained.')
    connection.execute('BEGIN IMMEDIATE')
    try:
        if current is not None:
            connection.execute('DROP VIEW open_findings')
        connection.execute(FINDING_VIEW_SQL)
        connection.commit()
    except BaseException:
        connection.rollback()
        raise


def connect(path, *, create=False):
    path = Path(path)
    if not create and not path.is_file():
        raise ValueError('Knowledge database does not exist; index a release first.')
    connection = sqlite3.connect(path, timeout=15)
    connection.row_factory = sqlite3.Row
    connection.execute('PRAGMA foreign_keys=ON')
    tables = {r[0] for r in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    application_id = connection.execute('PRAGMA application_id').fetchone()[0]
    if tables:
        marker = connection.execute("SELECT value FROM metadata WHERE key='db_kind'").fetchone() if 'metadata' in tables and application_id == APPLICATION_ID else None
        if not marker or marker[0] != DB_KIND:
            connection.close()
            raise ValueError('Refusing to open an unrelated database. Use the dedicated knowledge artifact.')
    elif application_id not in (0, APPLICATION_ID):
        connection.close()
        raise ValueError('Refusing to initialize a database owned by another application.')
    if create:
        version = connection.execute('PRAGMA user_version').fetchone()[0]
        if version not in (0, SCHEMA_VERSION):
            connection.close()
            raise ValueError(f'Unsupported knowledge schema {version}')
        connection.executescript(SCHEMA)
        connection.execute(f'PRAGMA user_version={SCHEMA_VERSION}')
        connection.execute(f'PRAGMA application_id={APPLICATION_ID}')
        connection.execute('INSERT OR IGNORE INTO metadata VALUES (?,?)', ('db_kind', DB_KIND))
        connection.execute('INSERT OR IGNORE INTO metadata VALUES (?,?)', ('scope', 'AutoSourcarr development knowledge for the current chat/project; not a live app database'))
        connection.commit()
    elif connection.execute('PRAGMA user_version').fetchone()[0] != SCHEMA_VERSION:
        connection.close()
        raise ValueError('Unsupported knowledge database schema')
    try:
        ensure_finding_view(connection)
    except BaseException:
        connection.close()
        raise
    return connection


def safe_path(name):
    p = PurePosixPath(name)
    if p.is_absolute() or '..' in p.parts or '\\' in name or not p.parts:
        return False
    lowered = [part.casefold() for part in p.parts]
    if any(part in {'__pycache__', '.git', '.pytest_cache', 'node_modules',
                     'data', 'logs', 'audit', '.extension-signing', 'source-host',
                     'codex-home', '.codex', 'profiles', 'sessions', 'repairs'} for part in lowered):
        return False
    basename = p.name.casefold()
    # Retained config contains setup journals/account JSON as well as .env files.
    # Only shipped templates and prose documentation are source material here.
    if 'config' in lowered and not (basename.endswith('.example') or basename.endswith('.md')):
        return False
    if basename in {'auth.json', 'credentials.json', 'cookies.json', '.setup-transaction.json', 'source-browser.json', 'setup-state.json'}:
        return False
    if (basename == '.env' or basename.endswith(('.env', '.sqlite', '.sqlite3', '.db',
            '.pem', '.key', '.keystore', '.jks', '.log')) or 'cookie_store' in basename):
        return False
    return True


def role(path):
    if '/bundled/' in path:
        return 'generated-copy'
    if '/tests/' in path:
        return 'test'
    if path.endswith(('.md', '.txt')) and 'requirements' not in path:
        return 'documentation'
    if path.startswith('repair-worker/'):
        return 'repair'
    if path.startswith('extension/'):
        return 'reader-build'
    if '/static/' in path:
        return 'frontend'
    if '/app/' in path:
        return 'backend'
    return 'deployment-tooling'


def inspect_python(path, content):
    tree = ast.parse(content, filename=path)
    lines = content.splitlines()
    symbols, endpoints, imports = [], [], []

    def walk(node, prefix=''):
        for child in ast.iter_child_nodes(node):
            if isinstance(child, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
                name = prefix + child.name
                kind = 'class' if isinstance(child, ast.ClassDef) else ('async-function' if isinstance(child, ast.AsyncFunctionDef) else 'function')
                signature = lines[child.lineno - 1].strip()
                symbols.append((f'{path}:{name}:{child.lineno}', path, name, kind, child.lineno,
                                child.end_lineno, signature, ast.get_docstring(child) or ''))
                for dec in child.decorator_list:
                    if isinstance(dec, ast.Call) and isinstance(dec.func, ast.Attribute) and dec.func.attr in {'get', 'post', 'put', 'patch', 'delete', 'websocket', 'head', 'options'}:
                        if dec.args and isinstance(dec.args[0], ast.Constant) and isinstance(dec.args[0].value, str):
                            route = dec.args[0].value
                            endpoints.append((f'{path}:{dec.lineno}', path, dec.func.attr.upper(), route, name, dec.lineno))
                walk(child, name + '.')
            else:
                walk(child, prefix)
    walk(tree)
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            imports.extend((path, item.name, node.lineno) for item in node.names)
        elif isinstance(node, ast.ImportFrom):
            imports.append((path, '.' * node.level + (node.module or ''), node.lineno))
    return symbols, endpoints, list(dict.fromkeys(imports))


def index_archive(db, archive):
    archive = Path(archive)
    archive_sha = hashlib.sha256(archive.read_bytes()).hexdigest()
    stamp = now()
    loaded, rejected = [], []
    with ZipFile(archive) as z:
        infos = [i for i in z.infolist() if not i.is_dir()]
        if sum(i.file_size for i in infos) > MAX_ARCHIVE_BYTES:
            raise ValueError('Archive is too large for this source-only index.')
        names = [i.filename for i in infos]
        if len(names) != len(set(names)):
            raise ValueError('Duplicate archive paths are not supported.')
        for item in infos:
            if not safe_path(item.filename) or item.file_size > MAX_FILE_BYTES or stat.S_ISLNK(item.external_attr >> 16):
                rejected.append(item.filename)
                continue
            raw = z.read(item)
            try:
                content = raw.decode('utf-8') if b'\0' not in raw else None
            except UnicodeDecodeError:
                content = None
            # Avoid indexing actual private keys accidentally included in an archive.
            if content and re.search(r'-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----', content):
                rejected.append(item.filename)
                continue
            loaded.append((item.filename, hashlib.sha256(raw).hexdigest(), len(raw), content))
        version = next((text.strip() for name, _, _, text in loaded if name == 'VERSION' and text), 'unknown')
    changes = []
    with db:
        db.execute('INSERT OR IGNORE INTO artifacts VALUES (?,?,?,?,?,?)',
                   (archive_sha, archive.name, version, stamp, len(loaded), sum(x[2] for x in loaded)))
        previous = {r['path']: (r['sha256'], r['present']) for r in db.execute('SELECT path,sha256,present FROM files')}
        present_paths = {row[0] for row in loaded}
        for path in previous.keys() - present_paths:
            if previous[path][1]:
                db.execute('UPDATE files SET present=0,observed_at=? WHERE path=?', (stamp, path))
                db.execute('DELETE FROM search WHERE key=?', ('file:' + path,))
                db.execute('DELETE FROM symbols WHERE path=?', (path,))
                db.execute('DELETE FROM endpoints WHERE path=?', (path,))
                db.execute('DELETE FROM code_imports WHERE path=?', (path,))
                changes.append({'path': path, 'change': 'removed'})
        for path, digest, size, content in loaded:
            db.execute('INSERT OR IGNORE INTO file_revisions VALUES (?,?,?,?,?)', (path, digest, size, content, stamp))
            db.execute('''INSERT INTO files VALUES (?,?,?,?,?,?,1,?) ON CONFLICT(path) DO UPDATE SET
                          sha256=excluded.sha256,byte_count=excluded.byte_count,language=excluded.language,
                          role=excluded.role,artifact_sha256=excluded.artifact_sha256,present=1,observed_at=excluded.observed_at''',
                       (path, digest, size, PurePosixPath(path).suffix.lstrip('.') or 'text', role(path), archive_sha, stamp))
            if previous.get(path) == (digest, 1):
                continue
            changes.append({'path': path, 'change': 'changed' if path in previous else 'added'})
            db.execute('DELETE FROM search WHERE key=?', ('file:' + path,))
            for table in ('symbols', 'endpoints', 'code_imports'):
                db.execute(f'DELETE FROM {table} WHERE path=?', (path,))
            if content is not None:
                db.execute('INSERT INTO search VALUES (?,?,?,?)', ('file:' + path, 'code', path, content))
                if path.endswith('.py'):
                    syms, routes, imports = inspect_python(path, content)
                    db.executemany('INSERT INTO symbols VALUES (?,?,?,?,?,?,?,?)', syms)
                    db.executemany('INSERT INTO endpoints VALUES (?,?,?,?,?,?)', routes)
                    db.executemany('INSERT INTO code_imports VALUES (?,?,?)', imports)
                elif path.endswith(('.js', '.kt', '.sh')):
                    for lineno, line in enumerate(content.splitlines(), 1):
                        match = re.search(r'^\s*(?:(?:async|export|private|public|override|suspend)\s+)*(?:function|fun|class)\s+([\w$]+)', line)
                        if match:
                            name = match.group(1)
                            db.execute('INSERT INTO symbols VALUES (?,?,?,?,?,?,?,?)',
                                       (f'{path}:{name}:{lineno}', path, name, 'lexical-symbol', lineno, lineno, line.strip(), 'Lexical index; not a complete AST'))
        db.execute('INSERT OR REPLACE INTO metadata VALUES (?,?)', ('latest_artifact_sha256', archive_sha))
        db.execute('INSERT OR REPLACE INTO metadata VALUES (?,?)', ('latest_version', version))
        db.execute('INSERT OR REPLACE INTO metadata VALUES (?,?)', ('last_index_at', stamp))
    return {'version': version, 'artifact_sha256': archive_sha, 'indexed': len(loaded),
            'changes': changes, 'excluded': rejected,
            'stale_records': [r[0] for r in db.execute('SELECT DISTINCT record_id FROM stale_evidence')]}


def import_records(db, payload):
    rows = payload.get('records', [])
    if not isinstance(rows, list):
        raise ValueError('records must be a list')
    changed = []
    with db:
        for session in payload.get('sessions', []):
            db.execute('''INSERT INTO sessions VALUES (:id,:started_at,:finished_at,:requested_seconds,:elapsed_seconds,:scope,:status,:limitations)
                          ON CONFLICT(id) DO UPDATE SET finished_at=excluded.finished_at,elapsed_seconds=excluded.elapsed_seconds,
                          status=excluded.status,limitations=excluded.limitations''', session)
        for record in rows:
            if not re.fullmatch(r'[A-Za-z0-9_.:-]{1,100}', record['id']):
                raise ValueError('Record id must be a stable short identifier')
            if record['kind'] not in KINDS or record['state'] not in STATES:
                raise ValueError('Unknown record kind or state')
            if record.get('confidence', 'high') not in {'high', 'medium', 'low'}:
                raise ValueError('Unknown confidence')
            required = ('title', 'body', 'origin', 'source_ref', 'applies_to')
            if any(not isinstance(record.get(k), str) or not record[k].strip() for k in required):
                raise ValueError(f'{record["id"]}: content and provenance are required')
            stamp = now()
            previous = db.execute('SELECT * FROM records WHERE id=?', (record['id'],)).fetchone()
            old_evidence = [dict(r) for r in db.execute('SELECT * FROM evidence WHERE record_id=? ORDER BY id', (record['id'],))]
            evidence = []
            for raw in record.get('evidence', []):
                item = {'path': None, 'file_sha256': None, 'line_start': None, 'line_end': None,
                        'symbol': '', 'url': '', 'evidence_type': 'code_inspection', 'note': '', **raw}
                if item['path']:
                    file = db.execute('SELECT * FROM files WHERE path=?', (item['path'],)).fetchone()
                    if not file:
                        raise ValueError(f'Unindexed evidence file: {item["path"]}')
                    # Reimporting an old note is not evidence of a new review.
                    # Preserve its old pinned revision unless the caller explicitly
                    # supplies a new hash after checking the changed source.
                    old_pin = next((e['file_sha256'] for e in old_evidence
                                    if e['path'] == item['path'] and e['symbol'] == item['symbol']
                                    and e['line_start'] == item['line_start']
                                    and e['line_end'] == (item['line_end'] or item['line_start'])
                                    and e['evidence_type'] == item['evidence_type']), None)
                    item['file_sha256'] = item['file_sha256'] or old_pin or file['sha256']
                    revision = db.execute('SELECT content FROM file_revisions WHERE path=? AND sha256=?', (item['path'], item['file_sha256'])).fetchone()
                    if not revision:
                        raise ValueError('Unknown evidence file revision')
                    start, end = item['line_start'], item['line_end']
                    if start is not None:
                        item['line_end'] = end = end or start
                        if type(start) is not int or type(end) is not int or not 1 <= start <= end <= len((revision[0] or '').splitlines()):
                            raise ValueError('Evidence line range outside source revision')
                evidence.append(item)
            fields = {k: record.get(k, default) for k, default in [('confidence','high'),('priority','normal'),('session_id',None)]}
            values = {k: record[k] for k in ('id','kind','title','body','state','origin','source_ref','applies_to')}
            values.update(fields)
            normalized_old = {k: previous[k] for k in values} if previous else None
            normalized_evidence = [{k: e[k] for k in evidence[0]} for e in old_evidence] if evidence else []
            if previous and normalized_old == values and normalized_evidence == evidence and len(old_evidence) == len(evidence):
                continue
            if previous:
                db.execute('INSERT INTO record_history(record_id,changed_at,previous_json) VALUES (?,?,?)',
                           (record['id'], stamp, json.dumps({'record': dict(previous), 'evidence': old_evidence}, ensure_ascii=False)))
            values.update(created_at=previous['created_at'] if previous else stamp, updated_at=stamp)
            db.execute('''INSERT INTO records VALUES (:id,:kind,:title,:body,:state,:confidence,:priority,:origin,:source_ref,:applies_to,:created_at,:updated_at,:session_id)
                          ON CONFLICT(id) DO UPDATE SET kind=excluded.kind,title=excluded.title,body=excluded.body,state=excluded.state,
                          confidence=excluded.confidence,priority=excluded.priority,origin=excluded.origin,source_ref=excluded.source_ref,
                          applies_to=excluded.applies_to,updated_at=excluded.updated_at,session_id=excluded.session_id''', values)
            db.execute('DELETE FROM evidence WHERE record_id=?', (record['id'],))
            for item in evidence:
                db.execute('''INSERT INTO evidence(record_id,path,file_sha256,line_start,line_end,symbol,url,evidence_type,note)
                              VALUES (:record_id,:path,:file_sha256,:line_start,:line_end,:symbol,:url,:evidence_type,:note)''',
                           {'record_id': record['id'], **item})
            db.execute('DELETE FROM search WHERE key=?', ('record:' + record['id'],))
            db.execute('INSERT INTO search VALUES (?,?,?,?)', ('record:' + record['id'], record['kind'], record['title'], record['body']))
            changed.append(record['id'])
        for relation in payload.get('relations', []):
            db.execute('INSERT OR IGNORE INTO relations VALUES (?,?,?)', (relation['source_id'], relation['relation'], relation['target_id']))
    return {'changed_records': changed}


def search(db, query, limit=20, kind=None):
    tokens = re.findall(r'\w+', query, re.UNICODE)
    if not tokens:
        return []
    expression = ' AND '.join('"' + word.replace('"', '""') + '"' for word in tokens)
    sql = '''SELECT search.key,search.kind,search.title,
             snippet(search,3,'[',']',' … ',28) excerpt,
             r.state,r.applies_to,
             (SELECT COUNT(*) FROM stale_evidence e WHERE e.record_id=r.id) stale_evidence_count
             FROM search LEFT JOIN records r ON search.key='record:'||r.id
             WHERE search MATCH ?'''
    params = [expression]
    if kind:
        sql += ' AND search.kind=?'
        params.append(kind)
    sql += ' ORDER BY bm25(search,0,0,6,1) LIMIT ?'
    params.append(min(100, max(1, limit)))
    return [dict(r) for r in db.execute(sql, params)]


def stats(db, *, summary=False):
    if summary:
        return {
            'metadata': dict(db.execute("SELECT key,value FROM metadata WHERE key IN ('db_kind','latest_version','last_index_at','current_review_entrypoint','current_verification_entrypoint')")),
            'files': db.execute('SELECT COUNT(*) FROM files WHERE present=1').fetchone()[0],
            'symbols': db.execute('SELECT COUNT(*) FROM symbols').fetchone()[0],
            'endpoints': db.execute('SELECT COUNT(*) FROM endpoints').fetchone()[0],
            'records_by_kind': dict(db.execute('SELECT kind,COUNT(*) FROM records GROUP BY kind')),
            'findings_by_state': dict(db.execute('SELECT state,COUNT(*) FROM findings GROUP BY state')),
            'stale_evidence_count': db.execute('SELECT COUNT(*) FROM stale_evidence').fetchone()[0],
            'sessions_count': db.execute('SELECT COUNT(*) FROM sessions').fetchone()[0],
        }
    return {'metadata': dict(db.execute('SELECT key,value FROM metadata')),
            'files': db.execute('SELECT COUNT(*) FROM files WHERE present=1').fetchone()[0],
            'file_revisions': db.execute('SELECT COUNT(*) FROM file_revisions').fetchone()[0],
            'symbols': db.execute('SELECT COUNT(*) FROM symbols').fetchone()[0],
            'endpoints': db.execute('SELECT COUNT(*) FROM endpoints').fetchone()[0],
            'records_by_kind': dict(db.execute('SELECT kind,COUNT(*) FROM records GROUP BY kind')),
            'findings_by_state': dict(db.execute('SELECT state,COUNT(*) FROM findings GROUP BY state')),
            'stale_records': [r[0] for r in db.execute('SELECT DISTINCT record_id FROM stale_evidence')],
            'sessions': [dict(r) for r in db.execute('SELECT * FROM sessions')]}


def check(db):
    integrity = [r[0] for r in db.execute('PRAGMA integrity_check')]
    foreign_keys = [tuple(r) for r in db.execute('PRAGMA foreign_key_check')]
    db.execute("INSERT INTO search(search) VALUES ('integrity-check')")
    db.commit()
    actual = [r[0] for r in db.execute('SELECT key FROM search')]
    expected = {'record:' + r[0] for r in db.execute('SELECT id FROM records')}
    expected.update('file:' + r[0] for r in db.execute('SELECT path FROM current_source WHERE content IS NOT NULL'))
    return {'integrity': integrity, 'foreign_key_errors': foreign_keys,
            'search_documents': len(actual), 'search_consistent': set(actual) == expected and len(actual) == len(expected),
            'stale_evidence_count': db.execute('SELECT COUNT(*) FROM stale_evidence').fetchone()[0]}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--db', type=Path, required=True)
    sub = parser.add_subparsers(dest='command', required=True)
    sub.add_parser('index').add_argument('archive', type=Path)
    sub.add_parser('import').add_argument('json_file', type=Path)
    find = sub.add_parser('search'); find.add_argument('query'); find.add_argument('--kind'); find.add_argument('--limit', type=int, default=20)
    sub.add_parser('show').add_argument('record_id')
    sub.add_parser('stats').add_argument('--summary', action='store_true')
    sub.add_parser('check')
    args = parser.parse_args()
    db = connect(args.db, create=args.command == 'index')
    try:
        if args.command == 'index':
            result = index_archive(db, args.archive)
        elif args.command == 'import':
            result = import_records(db, json.loads(args.json_file.read_text(encoding='utf-8')))
        elif args.command == 'search':
            result = search(db, args.query, args.limit, args.kind)
        elif args.command == 'stats':
            result = stats(db, summary=args.summary)
        elif args.command == 'check':
            result = check(db)
        else:
            row = db.execute('SELECT * FROM records WHERE id=?', (args.record_id,)).fetchone()
            result = {'record': dict(row) if row else None,
                      'evidence': [dict(r) for r in db.execute('SELECT * FROM evidence WHERE record_id=?', (args.record_id,))],
                      'stale_evidence': [dict(r) for r in db.execute('SELECT * FROM stale_evidence WHERE record_id=?', (args.record_id,))],
                      'relations': [dict(r) for r in db.execute('SELECT * FROM relations WHERE source_id=? OR target_id=?', (args.record_id,args.record_id))]}
        print(json.dumps(result, ensure_ascii=False, indent=2))
        if args.command == 'check' and (result['integrity'] != ['ok'] or result['foreign_key_errors'] or not result['search_consistent']):
            return 1
    finally:
        db.close()
    return 0


if __name__ == '__main__':
    sys.exit(main())
