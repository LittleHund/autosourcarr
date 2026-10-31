#!/usr/bin/env python3
"""Run the isolated, loopback-only sample UI. No accounts, app DBs or sources are opened.

    python scripts/preview-reimagined.py --port 8383

The real container continues to use the existing FastAPI backend. This optional
sample server serves the same frontend against synthetic in-memory observations.
It never starts helpers, accepts backups or executes repair/library mutations.
"""
from __future__ import annotations

import argparse
import json
import mimetypes
import importlib.util
import re
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from urllib.parse import parse_qs, unquote, urlsplit

from preview_data import PreviewData

STATIC=Path(__file__).resolve().parents[1] / 'autosourcarr/app/static'
_identity_spec=importlib.util.spec_from_file_location('preview_identity',STATIC.parent/'portable_identity.py')
preview_identity=importlib.util.module_from_spec(_identity_spec);_identity_spec.loader.exec_module(preview_identity)


class PreviewHandler(BaseHTTPRequestHandler):
    server_version='AutoSourcarrSample/1'

    def log_message(self, *_args):
        pass

    def reply(self, body, status=200, content_type='application/json; charset=utf-8'):
        if isinstance(body, (dict,list)):
            body=json.dumps(body,ensure_ascii=False).encode('utf-8')
        elif isinstance(body,str):
            body=body.encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type',content_type)
        self.send_header('Content-Length',str(len(body)))
        self.send_header('Cache-Control','no-store')
        self.send_header('X-Content-Type-Options','nosniff')
        self.send_header('X-AutoSourcarr-Version','0.4.5')
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError,ConnectionResetError):
            pass

    def do_GET(self):
        data=self.server.preview_data
        url=urlsplit(self.path);path=unquote(url.path);query=parse_qs(url.query)
        value=lambda key,default='':query.get(key,[default])[0]
        if path=='/':
            self.reply((STATIC/'index.html').read_text(encoding='utf-8').replace('data-ui="reimagined"','data-ui="reimagined" data-preview="sample"'),content_type='text/html; charset=utf-8');return
        if path.startswith('/static/'):
            target=(STATIC/path.removeprefix('/static/')).resolve()
            if not target.is_relative_to(STATIC.resolve()) or not target.is_file():
                self.reply({'detail':'Asset not found'},404);return
            self.reply(target.read_bytes(),content_type=mimetypes.guess_type(target.name)[0] or 'application/octet-stream');return
        cover=re.fullmatch(r'/api/v1/(?:covers|source-covers)/(\d+)',path)
        if cover:
            raw_id=int(cover[1]);entry_id=raw_id//10 if '/source-covers/' in path else raw_id
            self.reply(data.cover(max(1,entry_id)),content_type='image/svg+xml');return
        if path=='/api/v1/health':
            self.reply(data.health());return
        if path=='/api/admin/setup':
            self.reply(dict(completed=True,required=False,integrated=True,fields=[],revision='sample',reader_build=dict(ready=False,exit_code=1,errors=[dict(key='preview',code=1,message='Reader compilation is unavailable in sample preview.')],warnings=[])));return
        if path=='/api/admin/ui-preferences':
            self.reply(dict(user='Sample library',authelia=False,values=data.preferences,identity_header='',identity_diagnostics={}));return
        if path=='/api/admin/sources':
            self.reply(dict(items=data.sources,groups=[]));return
        gallery_policy=re.fullmatch(r'/api/admin/gallery-policies/([0-9]+)',path)
        if gallery_policy:
            source=next((s for s in data.sources if s['source_id']==gallery_policy[1]),None)
            if not source:self.reply({'detail':'Sample source not found'},404);return
            self.reply(dict(source=source,revision=0,policy=dict(profile='auto',enabled=True,criteria=[],title_minimum=.94,tag_similarity=.5,tag_overlap=2),
                detected=False,combined_entries=0,layout_pending=False,layout_result={},note='Synthetic rules. Saving is unavailable in this preview.'));return
        if path=='/api/admin/catalog-sharing/status':
            self.reply(dict(origin=None,entries=len(data.entries),files_added=0,pending_decisions=1,retained_records=1,note='Synthetic catalogue sharing. No real catalogue is opened.'));return
        if path in ('/api/admin/catalog-sharing/decisions','/api/admin/catalog-sharing/records'):
            uid='asc1:entry:autosourcarr:00000000-0000-4000-8000-000000000001'
            fact=dict(reference=preview_identity.record_id('100','/fixture/1'),source_id='100',title='Synthetic publication',raw_title='Synthetic publication',gallery=False,
                metadata=dict(description='Synthetic public metadata for reviewing the sharing interface.',author='Sample author',artist='',genre='',status=1))
            record=dict(uid=uid,aliases=[],lineage=[dict(uid=uid,records=[dict(source_id='100',reference=fact['reference'])])],title='Synthetic incoming publication',titles=[],metadata=fact['metadata'],references=[fact['reference']],sources=[fact])
            if path.endswith('/records'):
                row=dict(uid=uid,digest='0'*64,revision='0'*64,state='review',message='Synthetic reused ID. The existing local publication is retained.',record=record,resolved_uid=uid,entry_ids=[1],can_link=False,local_candidates=[])
            else:
                decision=dict(uid='asc1:decision:autosourcarr:00000000-0000-4000-8000-000000000002',kind='title',entries=[uid],records=[],value=dict(title='Synthetic incoming title'))
                row=dict(uid=decision['uid'],digest='0'*64,revision='0'*64,state='conflict',message='Synthetic conflicting local title. The local choice stays in force.',kind='title',decision=decision,entry_ids=[1],local_records=[],can_apply=False)
            offset=max(0,int(value('offset','0')))
            self.reply(dict(items=[row] if offset==0 else [],total=1,offset=offset,limit=30));return
        if path=='/api/admin/matrix':
            self.reply(data.matrix(value('search'),value('subbed')=='true',value('source_id')));return
        if path=='/api/admin/library':
            self.reply(data.library(query));return
        if re.fullmatch(r'/api/admin/metadata-fields/\d+',path):
            self.reply(dict(revision=0,fields={},history=[]));return
        if re.fullmatch(r'/api/admin/serving-upgrades/\d+',path):
            self.reply(dict(revision=0,policy=dict(enabled=False,cutoff=90,minimum_improvement=10,cooldown_hours=24,sample_max_age_hours=168),retained=[]));return
        if path=='/api/admin/policy-simulator/options':
            self.reply(dict(sources=data.sources,groups=[],rules=[],presets=[dict(name='High title similarity',rule=dict(enabled=True,checks=dict(title=.97)))]));return
        if path=='/api/admin/research-improvements/status':
            self.reply(dict(due=[],pending_feed_pages=0,quality_attempts=[],metrics=dict(phases=[],decode=dict(active=0,peak_active=0,peak_encoded_bytes=0),limits=dict(memory_measure='Synthetic sample; no decoder or source started')),
                            engine=dict(baseline='2.3.2243',target='2.4.2366',native_qualified=False,last_check={})));return
        controls=re.fullmatch(r'/api/admin/library/(\d+)/controls',path)
        if controls:
            eid=int(controls[1]);row=next((e for e in data.library({'scope':['catalog']})['entries'] if e['id']==eid),{})
            self.reply(dict(entry_id=eid,revision=0,monitored=eid==1,in_library=eid in data.library_ids,observation=row.get('observation',{}),observed_at='2026-10-18T12:00:00Z'));return
        if re.fullmatch(r'/api/admin/profiles/\d+',path):
            retained=[dict(id=1,title='Sample merged work',profile=dict(languages=['ja'],sources=['102'],quality='complete'),monitored=False)] if path.endswith('/1') else []
            self.reply(dict(revision=0,profile=dict(languages=[],sources=[],quality='default'),retained=retained));return
        if path=='/api/admin/downloads':
            self.reply(dict(state='STOPPED',revision='sample',queue=[dict(state='QUEUED',progress=0,position=0,tries=0,chapter=dict(id=1001,name='Sample chapter 1'),manga=dict(id=1,title='Sample manga',sourceId='100'))]));return
        if path=='/api/admin/instance/backups':
            self.reply(dict(items=[],pending_restore=False));return
        if path=='/api/admin/review-workbench':
            self.reply(dict(matches=[],decisions=[],bulk_actions=[],automatic_merges=dict(items=[]),entry=data.entry(int(value('entry_id'))) if value('entry_id') else None,note='Synthetic audit preview. Real matching evidence is available in a configured instance.'));return
        if path=='/api/admin/browse':
            entries=data.find(value('query') or value('search'))
            if value('mode')=='LATEST':entries=list(reversed(entries))
            source=value('sources')
            if source:
                ids={m['entry_id'] for m in data.matches if m['source_id']==source}
                entries=[entry for entry in entries if entry['id'] in ids]
            page=max(1,int(value('page','1')));size=min(500,max(1,int(value('limit',value('page_size','50')))))
            self.reply(dict(items=entries[(page-1)*size:page*size],has_next=page*size<len(entries),page=page,cached=True,partial=False));return
        if path=='/api/admin/entries':
            self.reply(dict(items=data.find(value('search'))[:int(value('limit','200'))]));return
        if path=='/api/admin/identity-review':
            rows=data.find(value('search'));offset=max(0,int(value('offset','0')));limit=min(50,max(1,int(value('limit','20'))))
            self.reply(dict(items=[dict(id=r['id'],preferred_title=r['preferred_title'],provider_count=0,saved_decisions=0) for r in rows[offset:offset+limit]],total=len(rows),offset=offset,limit=limit));return
        identity_review=re.fullmatch(r'/api/admin/identity-review/(\d+)',path)
        if identity_review:
            result=data.entry(int(identity_review[1]))
            if not result:self.reply({'detail':'Sample entry not found'},404);return
            for m in result['matches']:m.setdefault('source_manga_url','/fixture/'+str(result['id']))
            refs=[dict(r,decision='inherited',note='',current_evidence=True,manual=False,shared_with=[]) for r in preview_identity.describe(result)['references']]
            self.reply(dict(entry_id=result['id'],title=result['preferred_title'],revision='0'*64,evidence_sha256='0'*64,references=refs,history=[],note='Synthetic local review. The preview refuses identity writes; no original provider evidence, source binding or chapter can be changed.'));return
        if path=='/api/admin/portable-identities/resolve':
            reference=value('reference')
            try:preview_identity.parse(reference)
            except ValueError as exc:self.reply({'detail':str(exc)},409);return
            items=[]
            for saved in data.entries:
                e=data.entry(saved['id'])
                for m in e['matches']:m.setdefault('source_manga_url','/fixture/'+str(e['id']))
                if any(r['id']==reference for r in preview_identity.describe(e)['references']):items.append(saved)
            self.reply(dict(items=items,reference=reference,ambiguous=len(items)>1));return
        entry=re.fullmatch(r'/api/admin/entries/(\d+)',path)
        if entry:
            result=data.entry(int(entry[1]))
            if result:
                for m in result['matches']:m.setdefault('source_manga_url','/fixture/'+str(result['id']))
                result['portable_identity']=preview_identity.describe(result)
            self.reply(result or {'detail':'Manga not found'},200 if result else 404);return
        if re.fullmatch(r'/api/v1/entries/\d+/chapters',path):
            entry_id=int(path.split('/')[4]);total=data.rows[(entry_id-1)%len(data.rows)][4]
            items=[dict(id=entry_id*10000+n,number=n,chapter_number=n,display_name=f'Chapter {n}',name=f'Chapter {n}',title=f'Chapter {n}',candidate_count=2,serving_source_name='MangaDex',serving_source_entry_title=data.rows[entry_id-1][0],scanlator='Sample source',uploaded_at='2026-10-17T12:00:00Z',read=False) for n in range(min(total,12),0,-1)]
            self.reply(dict(items=items));return
        chapter=re.fullmatch(r'/api/admin/chapters/(\d+)',path)
        if chapter:
            chapter_id=int(chapter[1]);entry_id=chapter_id//10000;number=chapter_id%10000
            title=data.rows[(entry_id-1)%len(data.rows)][0]
            candidates=[dict(id=chapter_id*10+i,source_name=name,source_entry_title=title,name=f'Chapter {number}',page_count=24,official=i==0,resolver_score=98-i*5,quality_score=None) for i,name in enumerate(['MangaDex','MANGA Plus'])]
            self.reply(dict(chapter=dict(id=chapter_id,display_name=f'Chapter {number}'),candidates=candidates,rejected_candidates=[],would_serve_candidate_id=candidates[0]['id'],served=dict(candidate_id=candidates[0]['id'])));return
        if re.fullmatch(r'/api/admin/entries/\d+/history',path):
            self.reply(dict(items=[],total=0));return
        if re.fullmatch(r'/api/admin/entries/\d+/tags',path):
            self.reply(dict(tags=[],items=[],metadata=[],identities=[],manual_tags=[],suppressed_tags=[],mapping={},gallery_metadata={}));return
        if path=='/api/admin/activity/history':
            items=[r for r in data.tasks()['items'] if r['kind'] not in ('source-repair','source-raw-input')]
            self.reply(dict(items=items,total=len(items),offset=0,limit=50,note='Synthetic retained Activity. Production history is bounded and durable.'));return
        if path=='/api/admin/tasks':
            self.reply(data.tasks());return
        if path=='/api/admin/needs-attention':
            self.reply(dict(items=[],count=0));return
        if path=='/api/admin/errors':
            self.reply(dict(items=[],groups=[],active_count=0,active_error_count=0,active_warning_count=0));return
        if path=='/api/admin/source-health':
            now=1792238400
            self.reply(dict(items=[dict(source_id=s['source_id'],source_name=s['name'],name=s['name'],lang='en',installed=True,available=True,state='working',status='working',checked_at=now,last_success_at=now,last_repaired_at=None,message='Synthetic successful sample observation.',checked_version='Sample package',checks=[],history=[]) for s in data.sources],daily=dict(enabled=False,next_due_at=None),active={},checking=[],summary=dict(installed=4,healthy=4),history=[]));return
        if path=='/api/admin/duplicates':
            self.reply(dict(groups=[],items=[],subbed_entry_ids=data.library_ids,pending=False,subscription_pending=False));return
        if path.startswith('/api/admin/duplicate') or path.endswith('/mixed-entries'):
            self.reply(dict(items=[],groups=[],duplicates={},total=0,pending=False,offset=0));return
        if path.startswith('/api/admin/imports'):
            self.reply(dict(items=[],runs=[],total=0));return
        if path.startswith('/api/admin/manual'):
            self.reply(dict(items=[],groups=[],matches=[],titles=[],separate=[],rejections=[],duplicates=[],counts={},revision='sample'));return
        if path=='/api/admin/settings/env':
            self.reply(dict(files=[dict(id='autosourcarr',title='AutoSourcarr',path='Sample settings · no file opened',exists=True,writable=False,revision='sample',groups=[dict(name='Library connection',items=[dict(key='MAIN_SUWAYOMI_URL',label='Main Suwayomi',value='Sample library',kind='text',description='Read-only sample. The preview never connects to a real library.'),dict(key='SOURCE_ENGINE_MODE',label='Source engine',value='integrated',kind='text',description='The full application keeps its integrated source engine.')])])],restart_required=False));return
        if path=='/api/admin/storage/database':
            self.reply(dict(active_dir='Sample preview · no database opened',bytes=0,pending_relocation=None));return
        if path.startswith('/api/admin/source-maintenance'):
            raw=re.fullmatch(r'/api/admin/source-maintenance/raws/([^/]+)',path)
            if raw:
                self.reply(dict(package=raw[1],version='1.0.0',revision=0,state='needs-input',message='Synthetic code request. No source is installed or executed by the preview.',origin=dict(repository='',module='.',revision='')));return
            repair=re.fullmatch(r'/api/admin/source-maintenance/jobs/([a-f0-9]{32})',path)
            if repair:
                row=next((r for r in data.tasks()['items'] if r.get('repair_id')==repair[1]),None)
                self.reply(dict(id=repair[1],pkg_name=row.get('context') if row else 'sample',state=row.get('repair_state') if row else 'blocked',message='Synthetic evidence only.',conversation=[],history=[],patch=''));return
            self.reply(dict(enabled=False,configured=False,available=False,ok=False,integrated=True,provider='chatgpt',jobs=[],packages=[],items=[],account=dict(connected=False,status='not-configured',message='Account actions are unavailable in the sample preview.'),settings=dict(enabled=False,provider='chatgpt'),isolation=dict(available=False,reason='preview',message='Source execution is unavailable in the sample preview.'),error='Sample preview: source execution and AI account actions are unavailable.'));return
        if path=='/api/admin/internal-services':
            self.reply(dict(items=[],services=[],mode='sample'));return
        if path=='/api/admin/source-engine':
            self.reply(dict(mode='sample',running=False,available=False,message='The sample preview does not start a source engine.'));return
        if path=='/api/admin/source-extensions':
            self.reply(dict(items=[],extensions=[],stores=[]));return
        if path=='/api/admin/source-repositories':
            self.reply(dict(items=[],repositories=[],revision='sample'));return
        if path in ('/api/admin/orphaned-sources','/api/admin/source-orphans'):
            self.reply(dict(items=[],archives=[],installed=[]));return
        if path.startswith('/api/admin/reader'):
            self.reply(dict(available=False,ready=False,error='Reader builds are unavailable in sample preview.'));return
        self.reply({'detail':'This endpoint is unavailable in the local sample preview.'},404)

    def do_POST(self):
        self.mutation()

    def do_PUT(self):
        self.mutation()

    def do_DELETE(self):
        self.mutation()

    def mutation(self):
        data=self.server.preview_data
        path=urlsplit(self.path).path
        length=int(self.headers.get('Content-Length','0'))
        if length>128*1024:
            self.reply({'detail':'Uploads are unavailable in the sample preview.'},413);return
        try:
            body=json.loads(self.rfile.read(length) or b'{}')
        except (ValueError,UnicodeDecodeError):
            self.reply({'detail':'The sample preview accepts JSON only.'},400);return
        origin=self.headers.get('Origin')
        if origin and origin not in (f'http://127.0.0.1:{self.server.server_port}',f'http://localhost:{self.server.server_port}'):
            self.reply({'detail':'Cross-origin changes are unavailable.'},403);return
        if path=='/api/admin/ui-preferences' and self.command=='PUT':
            allowed={'theme','themeStyle','density','customColor','secondaryColor','tertiaryColor','cardFill','colorRolesVersion','accentStrength','thumbnailScale','matchedThumbnailScale','customizationSections','browsePageSize','sourcesSubtab','sourceSections','settingsSubtab','duplicateScope'}
            values=body.get('values',body)
            if isinstance(values,dict):data.preferences.update({key:value for key,value in values.items() if key in allowed})
            self.reply(dict(values=data.preferences,user='Sample library',authelia=False));return
        if path=='/api/admin/entries/summaries' and self.command=='POST':
            ids=set(body.get('ids',[]));self.reply(dict(items=[entry for entry in data.entries if entry['id'] in ids]));return
        if path=='/api/admin/duplicate-candidates' and self.command=='POST':
            self.reply(dict(duplicates={},pending=False));return
        if path=='/api/admin/matrix/chapter-stats' and self.command=='POST':
            ids=set(body.get('ids',[]));self.reply(dict(entry_stats={str(e['id']):e for e in data.entries if e['id'] in ids},record_stats=[m for m in data.matches if m['entry_id'] in ids]));return
        self.reply({'detail':'Sample preview only. This action needs the full AutoSourcarr instance; no library, source, account or configuration was changed.'},409)


def create_server(port=8383):
    server=HTTPServer(('127.0.0.1',port),PreviewHandler)
    server.preview_data=PreviewData()
    return server


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port',type=int,default=8383)
    args=parser.parse_args()
    if not 1<=args.port<=65535:
        parser.error('Use a port from 1 to 65535.')
    server=create_server(args.port)
    print(f'AutoSourcarr Reimagined sample preview: http://127.0.0.1:{args.port}',flush=True)
    print('Synthetic data; no app database, configuration, source or account is opened. Ctrl+C stops the preview.',flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__=='__main__':
    main()
