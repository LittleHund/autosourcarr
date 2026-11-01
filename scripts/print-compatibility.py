#!/usr/bin/env python3
"""PIPE-4: print the compatibility list of this source tree as JSON (one file per release).

Reads src/AutoSourcarr.Core/Compatibility/compatibility.json and adds what the build derives itself:
the database schema (latest FluentMigrator migration per database file, as SchemaMigrator.LatestMigration does).
It also checks that the listed extension API equals libVersion in extension/module/build.gradle.kts, so a release
cannot publish a stale list.

Usage (CI, per release):
    python scripts/print-compatibility.py > compatibility-<version>.json      # attach to the release
    python scripts/print-compatibility.py --check                             # fail the build on drift
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LIST = ROOT / 'src/AutoSourcarr.Core/Compatibility/compatibility.json'
GRADLE = ROOT / 'extension/module/build.gradle.kts'
MIGRATION = re.compile(r'\[Migration\((\d+)[^\]]*\]\s*\[Tags\(SchemaMigrator\.(CatalogTag|OriginsTag)\)\]')


def schema_versions():
    latest = {'catalog': 0, 'origins': 0}
    for path in (ROOT / 'src').rglob('*.cs'):
        for version, tag in MIGRATION.findall(path.read_text(encoding='utf-8')):
            key = 'catalog' if tag == 'CatalogTag' else 'origins'
            latest[key] = max(latest[key], int(version))
    if not all(latest.values()):
        raise SystemExit('No catalog/origins migrations found under src/')
    return latest


def lib_version():
    match = re.search(r'libVersion\s*=\s*"([^"]+)"', GRADLE.read_text(encoding='utf-8'))
    if not match:
        raise SystemExit('libVersion not found in ' + str(GRADLE))
    return match.group(1)


def build():
    data = json.loads(LIST.read_text(encoding='utf-8'))
    problems = []
    if data['extension_api']['version'] != lib_version():
        problems.append(f"extension_api.version {data['extension_api']['version']} != libVersion {lib_version()}")
    for name in ('share_format', 'profile_format'):
        section = data[name]
        if section['version'] not in {row['version'] for row in section['readers']}:
            problems.append(f'{name}.version has no reader row')
    share = data['share_format']
    if not 1 <= share['min_reader_version'] <= share['version']:
        problems.append('share_format.min_reader_version must be between 1 and version')
    result = {
        'list_version': data['list_version'],
        'app_version': data['app_version'],
        'database_schema': schema_versions(),
        'share_format': {'version': share['version'], 'min_reader_version': share['min_reader_version'],
                         'readers': [{'version': r['version'], 'app_version': r['app_version']} for r in share['readers']]},
        'extension_api': {'version': data['extension_api']['version']},
        'profile_format': {'version': data['profile_format']['version'],
                           'readers': [{'version': r['version'], 'app_version': r['app_version']} for r in data['profile_format']['readers']]},
    }
    return result, problems


def main(argv):
    result, problems = build()
    if problems:
        for problem in problems:
            print('compatibility.json: ' + problem, file=sys.stderr)
        return 1
    if '--check' not in argv:
        print(json.dumps(result, indent=2))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
