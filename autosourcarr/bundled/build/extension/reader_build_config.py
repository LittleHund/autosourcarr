#!/usr/bin/env python3
"""Shared, side-effect-free reader address/key checks for the UI and builder."""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from urllib.parse import urlsplit

TOKEN = 'AUTOSOURCARR_READER_API_TOKEN'
INTERNAL = 'AUTOSOURCARR_INTERNAL_URL'
PUBLIC = 'AUTOSOURCARR_PUBLIC_URL'


def evaluate(values, *, allow_insecure=False, allow_docker=False):
    errors, warnings = [], []

    def fail(key, code, message):
        errors.append({'key': key, 'code': code, 'message': message})

    token = values.get(TOKEN, '')
    if not token:
        fail(TOKEN, 12, 'Set a Reader API key before building reader extensions.')
    elif not isinstance(token, str) or not re.fullmatch(r'[A-Za-z0-9._~-]{32,256}', token):
        fail(TOKEN, 13, 'Reader API key must contain 32–256 letters, digits, dots, underscores, tildes or hyphens.')

    internal = values.get(INTERNAL) or 'http://autosourcarr:8282'
    public = values.get(PUBLIC) or internal
    parsed = {}
    for key, value in ((INTERNAL, internal), (PUBLIC, public)):
        valid = isinstance(value, str) and len(value) <= 4096 and value.startswith(('http://', 'https://'))
        valid = valid and not any(c.isspace() or ord(c) < 32 or c in '\\"$`@?#' for c in value)
        if valid:
            try:
                url = urlsplit(value)
                port = url.port
                valid = bool(url.hostname) and not url.username and not url.password and not url.query and not url.fragment
                valid = valid and (port is None or 1 <= port <= 65535)
            except ValueError:
                valid = False
        if not valid:
            fail(key, 14, 'Use a literal HTTP(S) reader base address with a valid host/port and no credentials, interpolation, query, fragment, quotes or whitespace.')
        else:
            parsed[key] = url

    if PUBLIC in parsed:
        if parsed[PUBLIC].scheme != 'https' and not allow_insecure:
            fail(PUBLIC, 14, 'Android reader address must use HTTPS to protect its Reader API key.')
        elif parsed[PUBLIC].hostname.lower() in {'autosourcarr', 'private-autosourcarr'} and not allow_docker:
            fail(PUBLIC, 11, 'Android reader address uses a Docker-only service name. Set the public HTTPS address of this instance.')
    if INTERNAL in parsed:
        url = parsed[INTERNAL]
        if url.hostname.lower() in {'autosourcarr', 'private-autosourcarr'} and url.port is not None and url.port != 8282:
            warnings.append({'key': INTERNAL, 'code': 0, 'message':
                'The Suwayomi reader address uses a Docker service with a port other than 8282. Use the container listener port unless you deliberately changed it, then rebuild and reinstall the JAR.'})
    return {'ready': not errors, 'exit_code': errors[0]['code'] if errors else 0,
            'errors': errors, 'warnings': warnings}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check-env', action='store_true')
    parser.add_argument('--json', action='store_true')
    parser.add_argument('--name', default='AutoSourcarr')
    args = parser.parse_args()
    result = evaluate(os.environ,
                      allow_insecure=os.environ.get('AUTOSOURCARR_ALLOW_INSECURE_READER_API') == '1',
                      allow_docker=os.environ.get('AUTOSOURCARR_ALLOW_DOCKER_ANDROID_URL') == '1')
    if args.json:
        print(json.dumps(result))
    else:
        for item in result['errors']:
            print(f"ERROR: {args.name}: {item['message']}", file=sys.stderr)
        for item in result['warnings']:
            print(f"WARNING: {args.name}: {item['message']}", file=sys.stderr)
    return result['exit_code']


if __name__ == '__main__':
    raise SystemExit(main())
