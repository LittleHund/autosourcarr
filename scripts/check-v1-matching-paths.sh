#!/usr/bin/env bash
# Phase 8: the static UI must call the matching routes under /api/v1. Lists every UI line that still calls one of the
# /api/admin paths of the MatchingEndpoints table in docs/port-map.md and exits 1 when any remain.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

python3 -I - docs/port-map.md autosourcarr/app/static <<'PY'
import re
import sys
from pathlib import Path

port_map, static = Path(sys.argv[1]), Path(sys.argv[2])
paths, inside = [], False
for line in port_map.read_text(encoding='utf-8').splitlines():
    if line.startswith('### '):
        inside = line.startswith('### `MatchingEndpoints`')
        continue
    if inside:
        cells = [cell.strip().strip('`') for cell in line.strip().strip('|').split('|')]
        if len(cells) == 4 and cells[2].startswith('/api/admin/'):
            paths.append(cells[2])
paths = list(dict.fromkeys(paths))
if not paths:
    sys.exit('check-v1-matching-paths: no MatchingEndpoints paths found in ' + str(port_map))

QUOTE = '[\'"`]'
# A path parameter written inline, as a template substitution, or as a string concatenation ('...'+id+'...').
PARAM = r'(?:\$\{[^}]*\}|' + QUOTE + r'\s*\+[^\n]*?\+\s*' + QUOTE + r'|[0-9A-Za-z_.%-]+)'
END_PARAM = r'(?:\$\{[^}]*\}|' + QUOTE + r'\s*\+|[0-9A-Za-z_.%-]+)'


def pattern(path):
    parts = re.split(r'(\{[^}]+\})', path)
    out = ''
    for index, part in enumerate(parts):
        if part.startswith('{'):
            out += END_PARAM if index == len(parts) - 1 or parts[index + 1] == '' else PARAM
        else:
            out += re.escape(part)
    # A fixed path must end there (quote, query string or concatenation), not continue into a longer route.
    if not path.endswith('}'):
        out += r'(?=' + QUOTE + r'|\?)'
    return re.compile(out)


patterns = [(path, pattern(path)) for path in paths]
found = 0
for file in sorted(static.rglob('*')):
    if file.suffix not in ('.js', '.html') or not file.is_file():
        continue
    for number, text in enumerate(file.read_text(encoding='utf-8', errors='replace').splitlines(), 1):
        for path, regex in patterns:
            if regex.search(text):
                print(f'{file}:{number}: {path}')
                found += 1
if found:
    print(f'check-v1-matching-paths: {found} UI call(s) still use /api/admin matching routes ({len(paths)} routes checked)', file=sys.stderr)
    sys.exit(1)
print(f'check-v1-matching-paths: no UI call uses the {len(paths)} /api/admin matching routes')
PY
