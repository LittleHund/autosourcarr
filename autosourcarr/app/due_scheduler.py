"""Independent complete-observation clocks and restart-safe bounded feed tails."""
from datetime import datetime,timedelta,timezone
import json
from .db import now_iso


def future(seconds):return (datetime.now(timezone.utc)+timedelta(seconds=seconds)).isoformat()


class DueScheduler:
    def __init__(self,database):self.db=database

    def state(self,kind,key):
        with self.db.catalog() as c:
            row=c.execute('SELECT * FROM due_work WHERE kind=? AND work_key=?',(kind,str(key))).fetchone()
        return dict(row) if row else {'kind':kind,'work_key':str(key),'next_due':'','last_attempt':'',
             'last_success':'','last_complete':'','outcome':'unknown','failures':0,'reason':'','config_revision':'','complete_revision':''}

    def attempted(self,kind,key,configuration=''):
        with self.db.catalog() as c:
            c.execute('''INSERT INTO due_work(kind,work_key,last_attempt,outcome,config_revision) VALUES(?,?,?,'running',?)
              ON CONFLICT(kind,work_key) DO UPDATE SET last_attempt=excluded.last_attempt,outcome='running',config_revision=excluded.config_revision''',(kind,str(key),now_iso(),configuration))

    def finished(self,kind,key,*,complete=False,success=False,deferred=False,reason='',interval=3600):
        old=self.state(kind,key);failures=0 if complete else old['failures']+(0 if deferred else 1)
        delay=interval if complete else 60 if deferred else min(3600,60*2**min(failures,6))
        ts=now_iso()
        with self.db.catalog() as c:
            c.execute('''INSERT INTO due_work VALUES(?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(kind,work_key) DO UPDATE SET
              next_due=excluded.next_due,last_success=excluded.last_success,last_complete=excluded.last_complete,
              outcome=excluded.outcome,failures=excluded.failures,reason=excluded.reason,complete_revision=excluded.complete_revision''',
              (kind,str(key),future(delay),old['last_attempt'],ts if success else old['last_success'],
               ts if complete else old['last_complete'],'complete' if complete else 'deferred' if deferred else 'incomplete',failures,str(reason)[:240],old['config_revision'],old['config_revision'] if complete else old['complete_revision']))
        return self.state(kind,key)

    def eligible(self,kind,key,configuration=None):
        value=self.state(kind,key)
        return configuration is not None and configuration!=value['config_revision'] or not value['next_due'] or value['next_due']<=now_iso()

    def snapshot(self,limit=100):
        with self.db.catalog() as c:
            return [dict(r) for r in c.execute('SELECT * FROM due_work ORDER BY next_due,kind,work_key LIMIT ?',(max(1,min(100,limit)),))]


class CrawlPages:
    """A page stays durable until every record has succeeded; never clip its tail.

    Failed records retry independently while successful siblings progress. Full or
    invalid buffers fail before a cursor advances. Ingestion remains idempotent.
    """
    def __init__(self,database):self.db=database

    def load(self,key):
        with self.db.catalog() as c:
            row=c.execute('SELECT page,payload_json FROM crawl_pending WHERE work_key=?',(key,)).fetchone()
        return {'page':row[0],**json.loads(row[1])} if row else None

    def save_page(self,key,page,payload):
        rows=payload.get('mangas') if isinstance(payload,dict) else None
        if not isinstance(rows,list) or type(payload.get('hasNextPage')) is not bool or len(rows)>500:
            raise ValueError('Incomplete or oversized source page; cursor retained')
        fields={'id','sourceId','title','url','thumbnailUrl','description','author','artist','genre','status','inLibrary','initialized','lastFetchedAt'}
        if any(not isinstance(r,dict) or type(r.get('id')) is not int or r['id']<=0 for r in rows):
            raise ValueError('Invalid feed identity; cursor retained')
        state={'has_next':payload['hasNextPage'],'items':[{'manga':{k:v for k,v in r.items() if k in fields},'attempts':0,'retry_at':''} for r in rows]}
        body=json.dumps(state,ensure_ascii=False)
        if len(body.encode())>1024*1024:raise ValueError('Source page exceeds 1 MiB pending limit; cursor retained')
        with self.db.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            if c.execute('SELECT COUNT(*) FROM crawl_pending').fetchone()[0]>=64 and not c.execute('SELECT 1 FROM crawl_pending WHERE work_key=?',(key,)).fetchone():
                raise ValueError('Pending feed capacity deferred; cursor retained')
            c.execute('INSERT INTO crawl_pending VALUES(?,?,?,?) ON CONFLICT(work_key) DO NOTHING',(key,page,body,now_iso()))
        return self.load(key)

    def ready(self,key,budget):
        state=self.load(key)
        if not state:return []
        return [i['manga'] for i in state['items'] if not i['retry_at'] or i['retry_at']<=now_iso()][:max(1,int(budget))]

    def acknowledge(self,key,manga_id,success):
        with self.db.catalog() as c:
            c.execute('BEGIN IMMEDIATE')
            row=c.execute('SELECT payload_json FROM crawl_pending WHERE work_key=?',(key,)).fetchone()
            if not row:return
            state=json.loads(row[0]);remaining=[]
            for item in state['items']:
                if item['manga']['id']==manga_id:
                    if success:continue
                    item['attempts']=min(20,item['attempts']+1)
                    item['retry_at']=future(min(3600,60*2**min(item['attempts'],6)))
                remaining.append(item)
            state['items']=remaining
            c.execute('UPDATE crawl_pending SET payload_json=? WHERE work_key=?',(json.dumps(state),key))

    def finish(self,key):
        state=self.load(key)
        if state is None or state['items']:return None
        with self.db.catalog() as c:c.execute('DELETE FROM crawl_pending WHERE work_key=?',(key,))
        return state['page']+1 if state['has_next'] else 1


def crawl_lane(due,pair_key,configuration):
    clocks={lane:due.state('catalog_'+lane,pair_key) for lane in ('recent','backlog')}
    return min(clocks,key=lambda lane:(clocks[lane]['next_due'] if clocks[lane]['config_revision']==configuration else '',0 if lane=='recent' else 1))
