"""Persistent website verification pauses and one owned hourly retry per site."""
from __future__ import annotations

import asyncio
import copy
import hashlib
import math
import time
from contextvars import ContextVar
from urllib.parse import urlsplit

from .async_utils import run_blocking
from .source_errors import assistance_reason

_PROBE = ContextVar('source_verification_probe', default=None)
RETRY_SECONDS = 3600


class SourceAttention:
    def __init__(self, database=None, clock=time.time):
        self.database = database
        self.clock = clock
        self.records = None
        self.lock = asyncio.Lock()
        self.monitor_task = None
        self.probes = {}
        self.contexts = {}
        self.manga_sources = {}
        self.chapter_sources = {}

    def _db(self):
        if self.database is not None:
            return self.database
        from .db import db
        return db

    async def _load(self):
        if self.records is None:
            self.records = await run_blocking(self._db().setting, 'source_human_attention_v1', {})

    async def _save(self):
        await run_blocking(self._db().set_setting, 'source_human_attention_v1', copy.deepcopy(self.records))

    @staticmethod
    def _remember(mapping, key, value):
        mapping[key] = value
        if len(mapping) > 12000:
            mapping.pop(next(iter(mapping)))

    def learn(self, payload):
        if not isinstance(payload, dict):
            return
        mangas = payload.get('mangas') or ([payload['manga']] if payload.get('manga') else [])
        for manga in mangas:
            if manga.get('id') is not None and manga.get('sourceId') is not None:
                self._remember(self.manga_sources, int(manga['id']), str(manga['sourceId']))
        for chapter in payload.get('chapters') or []:
            sid = self.manga_sources.get(int(chapter.get('mangaId') or 0))
            if sid and chapter.get('id') is not None:
                self._remember(self.chapter_sources, int(chapter['id']), sid)

    def _lookup(self, operation):
        database = self._db()
        sid = operation.get('source_id')
        kind, ident = operation['kind'], operation['id']
        if not sid and kind == 'pages':
            with database.origins() as conn:
                row = conn.execute('SELECT source_id FROM chapter_candidates WHERE source_chapter_id=? LIMIT 1', (ident,)).fetchone()
                sid = row[0] if row else None
        if not sid and kind == 'manga':
            with database.catalog() as conn:
                for table in ('source_matches', 'source_variants'):
                    row = conn.execute(f'SELECT source_id FROM {table} WHERE source_manga_id=? LIMIT 1', (ident,)).fetchone()
                    if row:
                        sid = row[0]
                        break
        with database.catalog() as conn:
            row = conn.execute('SELECT source_id,name,base_url FROM source_config WHERE source_id=?', (sid or '',)).fetchone()
        return dict(row) if row else {'source_id': str(sid or ''), 'name': 'Source', 'base_url': ''}

    @staticmethod
    def _key(engine, scope):
        return hashlib.sha256(f'{engine}|{scope}'.encode()).hexdigest()[:24]

    async def check_website(self, engine, hostname):
        await self.check({'key': self._key(engine, hostname.lower().removeprefix('www.'))})

    async def context(self, engine, operation):
        op = dict(operation)
        kind, ident = op['kind'], op['id']
        sid = op.get('source_id') or (self.chapter_sources if kind == 'pages' else self.manga_sources).get(ident)
        cache_key = str(sid) if sid else f'{kind}:{ident}'
        cached = self.contexts.get(cache_key)
        if not cached or cached[0] < self.clock() - 60:
            if sid:
                op['source_id'] = str(sid)
            context = await run_blocking(self._lookup, op)
            self._remember(self.contexts, cache_key, (self.clock(), context))
        else:
            context = cached[1]
        context = dict(context)
        site = (urlsplit(context.get('base_url') or '').hostname or '').lower().removeprefix('www.')
        scope = site or str(context.get('source_id') or f'{kind}:{ident}')
        context['key'] = self._key(engine, scope)
        context['website'] = site
        # A previously unknown direct ID may now have a source identity. Move its
        # retained pause into the website scope before admitting any new work.
        async with self.lock:
            await self._load()
            for key, row in list(self.records.items()):
                old = row['operation']
                mapped = (self.chapter_sources if old['kind'] == 'pages' else self.manga_sources).get(old['id'])
                related = (old['kind'], old['id']) == (kind, ident) or bool(mapped and mapped == context.get('source_id')) or bool(row.get('source_id') and row['source_id'] == context.get('source_id'))
                if related and not row.get('website') and key != context['key']:
                    if key in self.probes:
                        return dict(row)  # Keep the admitted probe's ownership intact.
                    existing = self.records.get(context['key'])
                    if existing:
                        existing['occurrences'] += row['occurrences']
                        existing['next_retry_at'] = max(existing['next_retry_at'], row['next_retry_at'])
                        existing['generation'] += 1
                    else:
                        self.records[context['key']] = {**row, **context, 'id': context['key'], 'generation': row['generation'] + 1}
                    del self.records[key]
                    await self._save()
        return context

    def _blocked(self, record):
        from .suwayomi import HumanAssistanceRequired
        return HumanAssistanceRequired(record, max(1, math.ceil(record['next_retry_at'] - self.clock())))

    async def check(self, context):
        async with self.lock:
            await self._load()
            record = self.records.get(context['key'])
            if record and _PROBE.get() != (context['key'], record['generation']):
                raise self._blocked(record)

    async def failed(self, context, operation, error):
        reason = assistance_reason(error)
        if not reason:
            return
        async with self.lock:
            await self._load()
            now, key = self.clock(), context['key']
            previous = self.records.get(key) or {}
            # Existing in-flight calls can fail together; group them without
            # extending the hourly deadline or granting additional probes.
            record = {**context, 'id': key, 'reason': reason,
                'first_seen': previous.get('first_seen', now), 'last_seen': now,
                'next_retry_at': previous.get('next_retry_at', now + RETRY_SECONDS),
                'occurrences': previous.get('occurrences', 0) + 1,
                'generation': previous.get('generation', 0) + 1,
                'operation': copy.deepcopy(operation), 'detail': str(error)[:6000],
                'last_retry_error': previous.get('last_retry_error', '')}
            self.records[key] = record
            await self._save()
            raise self._blocked(record) from error

    async def snapshot(self):
        async with self.lock:
            await self._load()
            items = [{k: v for k, v in row.items() if k not in {'operation', 'generation'}} |
                     {'retrying': key in self.probes} for key, row in self.records.items()]
        return {'items': sorted(items, key=lambda row: row['first_seen']), 'count': len(items), 'retry_seconds': RETRY_SECONDS}

    async def retry(self, key, client, *, manual=False):
        async with self.lock:
            await self._load()
            row = self.records.get(key)
            if not row or key in self.probes or (not manual and row['next_retry_at'] > self.clock()):
                return False
            # Reserve and persist before any request. Restart/cancellation keeps
            # this deadline; only one probe can own the website at a time.
            generation = row['generation']
            row['next_retry_at'] = self.clock() + RETRY_SECONDS
            row['last_retry_at'] = self.clock()
            self.probes[key] = asyncio.current_task()
            context, operation = dict(row), copy.deepcopy(row['operation'])
            try:
                await self._save()
            except BaseException:
                self.probes.pop(key, None)
                raise
        token = _PROBE.set((key, generation))
        try:
            from .suwayomi import background_source_work, foreground_source_work
            async with foreground_source_work() if manual else background_source_work():
                async with asyncio.timeout(max(10, min(float(client.timeout) + 5, 120))):
                    await client.retry_verification(operation, context)
            async with self.lock:
                # An older success must never clear a newly raised challenge.
                if self.records.get(key, {}).get('generation') == generation:
                    self.records.pop(key, None)
                    await self._save()
            return True
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            async with self.lock:
                row = self.records.get(key)
                if row:
                    row['last_retry_error'] = str(exc)[:1000]
                    await self._save()
            return False
        finally:
            _PROBE.reset(token)
            self.probes.pop(key, None)

    async def tick(self, client):
        snapshot = await self.snapshot()
        for row in snapshot['items']:
            if row['next_retry_at'] <= self.clock():
                await self.retry(row['id'], client)

    async def monitor(self, client):
        while True:
            try:
                await self.tick(client)
            except asyncio.CancelledError:
                raise
            except Exception:
                # A local DB/runtime outage must not create a tight retry loop.
                pass
            await asyncio.sleep(10)

    def start(self, service, client):
        if self.monitor_task is None or self.monitor_task.done():
            self.monitor_task = service._track_auxiliary(self.monitor(client))


source_attention = SourceAttention()
