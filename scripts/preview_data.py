"""Synthetic, in-memory fixtures for the optional Reimagined preview only."""
from __future__ import annotations

import copy
import html
import math
from datetime import datetime, timezone


class PreviewData:
    def __init__(self):
        rows = [
            ('Witch Hat Atelier', 'Kamome Shirahama', 'Ongoing', 'A young apprentice discovers a world of carefully drawn magic, and the cost of learning its secrets.', 85, 62, '#dfc9a5', '#364b43', 'witch'),
            ('Sakamoto Days', 'Yuto Suzuki', 'Ongoing', 'A quiet shopkeeper’s extraordinary past catches up with his ordinary family life.', 186, 186, '#e9ce48', '#1e3536', 'action'),
            ('Frieren: Beyond Journey’s End', 'Kanehito Yamada', 'Ongoing', 'Long after the great adventure ends, an elven mage sets out to understand the people she once travelled with.', 136, 111, '#c6d3b8', '#4d6971', 'landscape'),
            ('One Piece', 'Eiichiro Oda', 'Ongoing', 'An unlikely crew follows the horizon in search of freedom, friendship, and a legendary treasure.', 1130, 1082, '#ba5941', '#efc36c', 'sea'),
            ('Chainsaw Man', 'Tatsuki Fujimoto', 'Ongoing', 'A desperate young man finds a new life among hunters, monsters, and the promises that bind them.', 181, 181, '#e3a45a', '#4c343b', 'action'),
            ('The Apothecary Diaries', 'Natsu Hyuuga', 'Ongoing', 'A sharp-eyed apothecary follows the smallest clues through a palace full of secrets.', 78, 41, '#b8c9c0', '#3e5c60', 'flower'),
            ('Dandadan', 'Yukinobu Tatsu', 'Ongoing', 'A wager about ghosts and aliens sends two classmates into an increasingly strange world.', 172, 143, '#c7574e', '#292e48', 'orbit'),
            ('Blue Period', 'Tsubasa Yamaguchi', 'Ongoing', 'A student discovers painting and begins the difficult work of finding something of his own to say.', 72, 51, '#c6cdd4', '#355d8b', 'art'),
            ('Vinland Saga', 'Makoto Yukimura', 'Finished', 'A journey through a violent age becomes a search for a life beyond revenge.', 220, 220, '#b3bab3', '#263c48', 'sea'),
            ('Kingdom', 'Yasuhisa Hara', 'Ongoing', 'An ambitious young soldier moves through the rival states of an ancient land.', 815, 691, '#a65b48', '#2e2c3b', 'landscape'),
            ('Dungeon Meshi', 'Ryoko Kui', 'Finished', 'A small party ventures back into a dungeon, learning to survive on the creatures it holds.', 97, 97, '#d2bd83', '#4d6150', 'flower'),
            ('Vagabond', 'Takehiko Inoue', 'Hiatus', 'A wandering swordsman follows an uncertain road toward mastery and understanding.', 327, 284, '#d3c2a2', '#333e3b', 'ink'),
            ('The Summer Hikaru Died', 'Mokumokuren', 'Ongoing', 'In a quiet mountain town, a familiar face returns with something unfamiliar behind it.', 31, 0, '#bfccc4', '#3f6269', 'orbit'),
            ('A Bride’s Story', 'Kaoru Mori', 'Ongoing', 'Lives and traditions meet along the routes of nineteenth-century Central Asia.', 112, 0, '#c1ac7e', '#534648', 'flower'),
            ('20th Century Boys', 'Naoki Urasawa', 'Finished', 'Childhood memories become clues to a conspiracy that reaches far beyond a group of old friends.', 249, 0, '#c8b381', '#a55545', 'orbit'),
            ('The Climber', 'Shinichi Sakamoto', 'Finished', 'A solitary climber confronts the mountain, and the distance between himself and other people.', 170, 0, '#c6d3d3', '#3c5869', 'landscape'),
        ]
        self.rows = rows
        self.entries = []
        self.matches = []
        self.sources = [dict(source_id=str(100+i), id=str(100+i), name=name, display_name=name, lang='en', base_url='https://preview.invalid', priority=i+1, enabled_language=True, enabled_search=True, enabled_chapters=True, available=True, installed=True, is_installed=True, extension_id='preview.'+str(i), group_id=None, custom_group_id=None) for i, name in enumerate(['MangaDex', 'MANGA Plus', 'MangaSee', 'Asura Scans'])]
        for index, (title, author, status, description, total, downloaded, *_art) in enumerate(rows):
            entry_id=index+1
            source_count=0 if entry_id==8 else 1+index%3
            self.entries.append(dict(id=entry_id, preferred_title=title, author=author, artist=author, description=description, status={'Ongoing':1,'Finished':2,'Hiatus':6}[status], status_label=status, match_count=source_count, entry_match_count=source_count, manual_preferred_title=None, thumbnail_url=f'/api/v1/entries/{entry_id}/thumbnail', sources=[s['name'] for s in self.sources[:source_count]], served_latest_chapter=total, served_chapter_count=total, chapter_records_checked=source_count>0, chapter_stats_pending=False, aliases=[title], updated_at='2026-10-17T12:00:00Z'))
            for source_index in range(source_count):
                self.matches.append(dict(entry_id=entry_id, source_id=str(100+source_index), source_manga_id=entry_id*10+source_index, source_title=title, source_name=self.sources[source_index]['name'], source_lang='en', source_url=f'https://preview.invalid/title/{entry_id}', description=description, confidence=.99 if source_index==0 else .96, locked=entry_id in (1,5), primary=True, is_variant=False, chapter_count=total-(8 if source_index and index%4==0 else 0), latest_chapter=total, chapters_checked=True, missing_chapter_count=8 if source_index and index%4==0 else 0, chapter_stats_pending=False))
        self.library_ids=list(range(1,13))
        self.preferences={}

    def subscription(self):
        return dict(ok=True, entry_ids=self.library_ids, stats_by_entry={str(entry_id):dict(entry_id=entry_id, chapter_count=self.rows[entry_id-1][4], downloaded_chapter_count=self.rows[entry_id-1][5]) for entry_id in self.library_ids}, refreshing=False, cache_age_seconds=0)

    def find(self, query=''):
        terms=query.casefold().split()
        return [copy.deepcopy(entry) for entry in self.entries if all(term in (entry['preferred_title']+' '+entry['author']+' '+str(entry['id'])).casefold() for term in terms)]

    def entry(self, entry_id):
        found=next((entry for entry in self.entries if entry['id']==entry_id), None)
        if not found:
            return None
        result=copy.deepcopy(found)
        result.update(matches=[copy.deepcopy(m) for m in self.matches if m['entry_id']==entry_id], source_variants=[], titles=[dict(title=found['preferred_title'], title_type='source')], metadata_identities=[], variants=[])
        return result

    def matrix(self, query='', subbed=False, source_id=''):
        entries=self.find(query)
        if subbed:
            entries=[entry for entry in entries if entry['id'] in self.library_ids]
        if source_id:
            allowed={m['entry_id'] for m in self.matches if m['source_id']==source_id}
            entries=[entry for entry in entries if entry['id'] in allowed]
        selected={entry['id'] for entry in entries}
        return dict(entries=entries,sources=self.sources,matches=[m for m in self.matches if m['entry_id'] in selected],variants=[],subscription=self.subscription(),duplicates={},duplicates_pending=False,chapter_stats_pending=False,discovery_outcomes=[])

    def library(self,query):
        value=lambda key,default='':query.get(key,[default])[0]
        scope=value('scope','all');entries=self.find(value('search'))
        if scope!='catalog':entries=[e for e in entries if e['id'] in self.library_ids]
        stats=self.subscription()['stats_by_entry']
        for e in entries:
            s=stats.get(str(e['id']),{});checked=e['id'] not in (4,8)
            e.update(source_count=e['match_count'],monitored=e['id']==1,revision=0,library_stats=s,
                observation=dict(download_state='checked' if checked else 'unknown',chapter_count=s.get('chapter_count'),downloaded=s.get('downloaded_chapter_count'),
                    missing_downloads=max(0,int(s.get('chapter_count') or 0)-int(s.get('downloaded_chapter_count') or 0)) if checked else None,
                    source_gaps=8 if e['id'] in (5,9) else 0,coverage_state='checked' if checked else 'verification'))
        if value('source'):
            allowed={r['entry_id'] for r in self.matches if r['source_id']==value('source')};entries=[e for e in entries if e['id'] in allowed]
        if scope=='unmatched':entries=[e for e in entries if not e['match_count']]
        if scope=='monitored':entries=[e for e in entries if e['monitored']]
        if scope=='missing_downloads':entries=[e for e in entries if e['observation']['download_state']=='checked' and e['observation']['missing_downloads']>0]
        if scope=='source_gaps':entries=[e for e in entries if e['observation']['source_gaps']>0]
        if scope=='verification':entries=[e for e in entries if e['observation']['download_state']!='checked']
        if value('publication') not in ('','any'):entries=[e for e in entries if e['status_label'].casefold().startswith(value('publication')[:5])]
        if value('title_state')=='manual':entries=[e for e in entries if e['manual_preferred_title']]
        if value('title_state')=='automatic':entries=[e for e in entries if not e['manual_preferred_title']]
        if value('source_count'):entries=[e for e in entries if e['source_count']==int(value('source_count'))]
        if value('record_count'):entries=[e for e in entries if e['entry_match_count']==int(value('record_count'))]
        if value('sort')=='title':entries.sort(key=lambda e:(e['preferred_title'].lower(),e['id']))
        elif value('sort')=='matches':entries.sort(key=lambda e:(-e['match_count'],-e['id']))
        else:entries.sort(key=lambda e:-e['id'])
        total=len(entries);offset=int(value('cursor','0'));limit=min(120,max(1,int(value('limit','60'))));selected=entries[offset:offset+limit]
        return dict(entries=selected,total=total,next_cursor=str(offset+limit) if offset+limit<total else '',limit=limit,sources=self.sources,
            matches=[r for r in self.matches if r['entry_id'] in {e['id'] for e in selected}],variants=[],subscription={'ok':True,'refreshing':False},scope=scope)

    def health(self):
        matched=sum(entry['match_count']>0 for entry in self.entries)
        return dict(ok=True, version='0.4.1', reimagined_preview=True, source_host=dict(ok=True,configured=True,url='Sample source engine'), main_suwayomi=dict(ok=True,configured=True,url='Sample library'),translation=dict(configured=False,ok=False), errors=dict(active=0,active_errors=0,active_warnings=0,items=[],groups=[]),library=dict(ok=True,subbed_entries=len(self.library_ids),downloaded_chapters=sum(row[5] for row in self.rows[:12]),download_size=dict(configured=False,available=False)),reader_build=dict(ready=False,errors=[],warnings=[]),stats=dict(catalog=dict(entries=len(self.entries),matched_entries=matched,unmatched_entries=len(self.entries)-matched,possible_duplicates=0,duplicate_groups=0,duplicate_snapshot_ready=True,publication_statuses={status:sum(row[2]==status for row in self.rows) for status in ['Ongoing','Finished','Hiatus']}),sources=dict(families=4,enabled_families=4,variants=4,enabled_variants=4,search_variants=4,chapter_variants=4,unavailable_variants=0),chapters=dict(virtual_chapters=sum(row[4] for row in self.rows),chapter_candidates=sum(m['chapter_count'] for m in self.matches),served_history=1248),cache=dict(persistent_browse_feeds=8,cover_files=16),storage=dict(total_bytes=0),performance=dict(continuous_catalog_discovery=False)))

    def tasks(self):
        now=datetime.now(timezone.utc).isoformat()
        items=[dict(id='preview-metadata',kind='metadata',label='Refresh library metadata',title='Refresh library metadata',state='completed',origin='manual',activity='finished',progress=12,total=12,done=12,current=12,percent=100,message='Sample result · metadata refreshed for 12 manga',started_at='2026-10-17T12:00:00+00:00',finished_at='2026-10-17T12:00:08+00:00',updated_at=now,cancellable=False),dict(id='preview-discovery',kind='source-discovery',label='Discover additional source records',title='Discover additional source records',state='completed',origin='automatic',activity='finished',progress=16,total=16,done=16,current=16,percent=100,message='Sample result · source matching completed',started_at='2026-10-17T11:30:00+00:00',finished_at='2026-10-17T11:30:20+00:00',updated_at=now,cancellable=False)]
        for item in items:
            item['name']=item['title']
        for n,(name,state,repair_state,kind) in enumerate([
            ('Sample code request','running','awaiting-raws','source-raw-input'),
            ('Sample isolated checks','running','checking-backup','source-repair'),
            ('Sample candidate for review','completed','ready','source-repair'),
            ('Sample blocked repair','failed','blocked','source-repair')],1):
            items.append(dict(id='source-repair:'+str(n)*32,repair_id=str(n)*32,kind=kind,name=name,state=state,repair_state=repair_state,
                context='eu.kanade.tachiyomi.extension.en.sample'+str(n),raw_package='eu.kanade.tachiyomi.extension.en.sample'+str(n),
                origin='manual',updated_at=now,created_at=now,cancellable=False,message='Synthetic sample only; no code, model or source execution is running.'))
        return dict(items=items,running=2,completed=3,failed=1,source_repairs=dict(jobs=[],stale=False,isolation=dict(available=False,reason='preview',message='Source repair execution is unavailable in the sample preview. The full application retains its isolated repair workflow.')))

    def cover(self, entry_id):
        title,author,_status,_description,_total,_downloaded,paper,ink,style=self.rows[(entry_id-1)%len(self.rows)]
        seed=entry_id*13
        details=[]
        if style in ('landscape','sea','ink'):
            for layer in range(5):
                y=230+layer*45
                points=' '.join(f'{x},{y+math.sin((x+seed)/70)*42+math.cos(x/90+layer)*35}' for x in range(-20,441,20))
                details.append(f'<polygon points="-20,600 {points} 440,600" fill="{ink}" opacity="{.14+layer*.13}"/>')
            details.append(f'<circle cx="280" cy="180" r="75" fill="{paper}" opacity=".7"/>')
        elif style in ('flower','witch'):
            for i in range(16):
                angle=i*137.5+seed
                x=200+math.cos(math.radians(angle))*min(120,35+i*7)
                y=285+math.sin(math.radians(angle))*min(145,35+i*8)
                details.append(f'<ellipse cx="{x:.1f}" cy="{y:.1f}" rx="21" ry="65" transform="rotate({angle:.1f} {x:.1f} {y:.1f})" fill="{ink}" opacity=".36"/>')
            details.append(f'<circle cx="200" cy="285" r="38" fill="{paper}"/><circle cx="200" cy="285" r="28" fill="{ink}" opacity=".85"/>')
        elif style=='action':
            for i in range(9):
                x=i*65-70
                details.append(f'<path d="M{x} 110 {x+130} 520" stroke="{ink}" stroke-width="{12+i%3*15}" opacity="{.1+i*.065}"/>')
            details.append(f'<path d="m100 470 120-300 85 250-130-110Z" fill="{paper}" opacity=".8"/>')
        else:
            for i in range(5):
                details.append(f'<circle cx="200" cy="280" r="{42+i*34}" fill="none" stroke="{ink}" stroke-width="{2+i}" opacity=".4"/>')
            details.append(f'<circle cx="260" cy="260" r="70" fill="{ink}" opacity=".7"/>')
        words=title.replace(':','').split()
        lines=[]
        line=''
        for word in words:
            if len(line+' '+word)>17 and line:
                lines.append(line);line=word
            else:
                line=(line+' '+word).strip()
        if line:
            lines.append(line)
        heading=''.join(f'<text x="28" y="{58+i*35}" font-family="Georgia,serif" font-size="31" font-weight="bold" fill="{ink}">{html.escape(line)}</text>' for i,line in enumerate(lines[:3]))
        return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 600"><defs><filter id="grain"><feTurbulence type="fractalNoise" baseFrequency=".7" numOctaves="3" stitchTiles="stitch"/><feColorMatrix type="saturate" values="0"/><feComponentTransfer><feFuncA type="linear" slope=".10"/></feComponentTransfer></filter></defs><rect width="400" height="600" fill="{paper}"/><rect x="14" y="14" width="372" height="572" rx="2" stroke="{ink}" stroke-width="1" fill="none" opacity=".3"/>{"".join(details)}<rect width="400" height="600" filter="url(#grain)" opacity=".7"/>{heading}<rect y="530" width="400" height="70" fill="{paper}" opacity=".94"/><text x="28" y="559" font-family="Arial,sans-serif" font-size="13" letter-spacing="1" fill="{ink}">{html.escape(author.upper())}</text><text x="28" y="581" font-family="Arial,sans-serif" font-size="9" letter-spacing="2" fill="{ink}" opacity=".65">SAMPLE ARTWORK · REIMAGINED</text><text x="364" y="565" text-anchor="end" font-family="Georgia,serif" font-size="26" fill="{ink}">{entry_id:02}</text></svg>'
