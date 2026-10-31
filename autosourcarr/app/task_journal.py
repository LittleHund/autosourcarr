"""Bounded general task history using the existing catalog and one owned writer."""
import asyncio
from dataclasses import asdict
import json
from .async_utils import run_blocking
from .diagnostics import scrub
from .db import now_iso
from .runtime_observability import timed


class TaskJournal:
    def __init__(self,database,registry):
        self.database=database; self.registry=registry
        self.loop=None; self.event=None; self.worker=None; self.stopping=False
        self.error=''
        self.available=False

    def load(self):
        try:
            with self.database.catalog() as c:
                rows=c.execute('SELECT record_json FROM general_task_history ORDER BY updated_at DESC LIMIT 300').fetchall()
                records=[]
                for row in rows:
                    item=json.loads(row[0]); item['cancellable']=False
                    if item['state']=='running':
                        item.update(state='failed',activity='interrupted',interrupted=True,
                                    finished_at=now_iso(),updated_at=now_iso(),
                                    message='Interrupted by restart; review the original operation before retrying',
                                    error='Previous process ended before completion was recorded')
                    records.append(item)
                return records
        finally: self.database.close_current_thread()

    async def start(self):
        if self.worker is not None and not self.worker.done():return
        from .task_registry import TaskRecord
        try:records=await run_blocking(self.load)
        except Exception:
            self.error='Task history could not be loaded; existing rows retained. Check diagnostics and storage.'
            self.registry._journal=self
            return
        with self.registry._lock:
            for item in records:
                if item['id'] not in self.registry._tasks:
                    self.registry._tasks[item['id']]=TaskRecord(**{k:v for k,v in item.items() if k in TaskRecord.__dataclass_fields__})
            self.registry._journal=self
        self.loop=asyncio.get_running_loop(); self.event=asyncio.Event(); self.stopping=False
        self.available=True
        self.worker=asyncio.create_task(self.run()); self.changed()

    def changed(self):
        if self.loop and not self.loop.is_closed() and not self.stopping:
            self.loop.call_soon_threadsafe(self.event.set)

    @timed('journal')
    def write(self,records):
        try:
            with self.database.catalog() as c:
                # Retention/clear operations remove absent records; unchanged
                # completed records do not dirty pages on every active-task tick.
                c.execute('DELETE FROM general_task_history WHERE id NOT IN(SELECT value FROM json_each(?))',
                          (json.dumps([r['id'] for r in records]),))
                c.executemany('''INSERT INTO general_task_history VALUES(?,?,?)
                    ON CONFLICT(id) DO UPDATE SET record_json=excluded.record_json,updated_at=excluded.updated_at
                    WHERE general_task_history.record_json<>excluded.record_json''',
                    [(r['id'],json.dumps(scrub(r),sort_keys=True,separators=(',',':')),r['updated_at']) for r in records])
        finally: self.database.close_current_thread()

    async def flush(self):
        with self.registry._lock:
            records=[asdict(r) for r in self.registry._tasks.values()]
        records.sort(key=lambda r:r['updated_at'],reverse=True)
        running=[r for r in records if r['state']=='running']
        finished=[r for r in records if r['state']!='running'][:300]
        await run_blocking(self.write,running+finished)
        self.error=''

    async def run(self):
        while not self.stopping:
            await self.event.wait(); self.event.clear()
            await asyncio.sleep(.15)
            try: await self.flush()
            except Exception as exc: self.error=type(exc).__name__+': task history could not be saved'

    async def stop(self):
        if self.worker is None:return
        self.stopping=True
        if self.event: self.event.set()
        if self.worker: await self.worker
        await self.flush()
        self.registry._journal=None
        self.worker=None
        self.available=False
