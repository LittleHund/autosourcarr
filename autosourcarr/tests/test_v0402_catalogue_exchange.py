import json
from contextlib import contextmanager
import pytest
from app.config import settings
from app.db import Database
from app.service import AutoSourcarrService
from app import service as service_module
from app.catalog_exchange import CatalogueExchange
from app import catalog_exchange_format as wire,portable_identity,gallery_policy


@pytest.fixture
def worlds(tmp_path,monkeypatch):
    for name,value in {'metadata_identity_enabled':False,'metadata_identity_on_ingest':False,'automatic_duplicate_matching':False,'color_detection_enabled':False}.items():monkeypatch.setattr(settings,name,value)
    class World:
        def __init__(self,name):
            self.path=tmp_path/name;self.path.mkdir()
            with monkeypatch.context() as local:
                local.setattr(settings,'data_dir',self.path);local.setattr(settings,'database_dir','')
                self.db=Database();self.service=AutoSourcarrService();self.exchange=CatalogueExchange(self.db,self.service)
                self.db.close_current_thread()
        @contextmanager
        def active(self):
            with monkeypatch.context() as local:
                local.setattr(settings,'data_dir',self.path);local.setattr(settings,'database_dir','');local.setattr(service_module,'db',self.db)
                yield self
                self.db.close_current_thread()
        def native(self,title,path,mid=112):
            with self.db.catalog() as c:c.execute("INSERT OR IGNORE INTO source_config(source_id,name,last_seen) VALUES('17','Native source','now')")
            self.service._reload_source_cache()
            payload=dict(id=mid,title=title,url=path,initialized=True,description='A source-native fixture description',author='Fixture Creator',genre=[])
            eid=self.service._create_entry(payload,'17');self.service._upsert_match(eid,'17',payload,1,'known');return eid
        def dump(self):return wire.canonical(self.exchange.export()['catalogue']).encode()
        def add(self,raw):
            stage=self.exchange.preview(raw);return self.exchange.apply(stage['id'],stage['revision'])
        def count(self):
            with self.db.catalog() as c:return c.execute('SELECT COUNT(*) FROM entries').fetchone()[0]
    return World


def test_three_databases_colliding_numbers_add_and_roundtrip_without_duplicates(worlds):
    a,b,c=worlds('a'),worlds('b'),worlds('c')
    with a.active():assert a.native('Alpha Chronicle','/alpha')==1;first=a.dump()
    with b.active():
        assert b.native('Beta Chronicle','/beta',900)==1
        assert b.add(first)['added']==1;second=b.dump()
    with c.active():
        assert c.native('Gamma Chronicle','/gamma',700)==1
        assert c.add(second)['added']==2;third=c.dump()
    with a.active():
        assert a.add(third)['added']==2;assert a.count()==3;fourth=a.dump()
        assert a.service.get_entry(1)['preferred_title']=='Alpha Chronicle'
    with b.active():
        assert b.add(fourth)['added']==1
        assert b.add(fourth)['added']==0 and b.count()==3
        assert b.service.get_entry(1)['preferred_title']=='Beta Chronicle'
        with b.db.catalog() as db:
            assert [r[0] for r in db.execute('SELECT source_manga_id FROM source_matches')]==[900]
        for record in json.loads(first)['entries']:
            result=portable_identity.resolve(b.db,b.service,record['uid'])
            assert len(result['items'])==1 and result['items'][0]['preferred_title']=='Alpha Chronicle'


def test_exact_public_record_reuses_local_id_and_preserves_conflicting_manual_title(worlds):
    a,b=worlds('a'),worlds('b')
    with a.active():
        eid=a.native('Original title','/shared',112);a.service.set_preferred_title(eid,'Shared manual title');raw=a.dump()
        assert b'"source_manga_id"' not in raw and b'"entry_id"' not in raw
    with b.active():
        eid=b.native('Original title','/shared',900);b.service.set_preferred_title(eid,'My local title')
        result=b.add(raw)
        assert result['added']==0 and b.count()==1
        assert b.service.get_entry(eid)['preferred_title']=='My local title'
        choices=b.exchange.decisions()['items'];assert any(r['state']=='conflict' for r in choices)
        assert b.add(raw)['added']==0


def test_shared_work_id_does_not_collapse_editions_or_create_native_bindings(worlds):
    a,b=worlds('a'),worlds('b')
    with a.active():
        eid=a.native('Edition Alpha','/edition-a')
        with a.db.catalog() as db:db.execute("INSERT INTO identity_links(entry_id,provider,external_id,identity_scope,last_checked) VALUES(?,'anilist','123','work','now')",(eid,))
        raw=a.dump()
    with b.active():
        eid=b.native('Edition Beta','/edition-b',900)
        with b.db.catalog() as db:db.execute("INSERT INTO identity_links(entry_id,provider,external_id,identity_scope,last_checked) VALUES(?,'anilist','123','work','now')",(eid,))
        result=b.add(raw);assert result['added']==1
        found=portable_identity.resolve(b.db,b.service,'asc1:work:anilist:123')
        assert found['ambiguous'] and len(found['items'])==2
        other=next(r['id'] for r in found['items'] if r['id']!=eid)
        assert not b.service._auto_merge_evidence(eid,other)['eligible']
        with b.db.catalog() as db:assert db.execute('SELECT COUNT(*) FROM source_matches').fetchone()[0]==1


def test_import_transaction_rolls_back_on_real_sql_failure_and_rejects_stale_preview(worlds):
    a,b=worlds('a'),worlds('b')
    with a.active():a.native('First catalogue work','/one');a.native('Rejected second work','/two',113);raw=a.dump()
    with b.active():
        stage=b.exchange.preview(raw)
        with b.db.catalog() as db:db.execute("CREATE TRIGGER fixture_import_abort BEFORE INSERT ON entries WHEN NEW.preferred_title='Rejected second work' BEGIN SELECT RAISE(ABORT,'fixture failure'); END")
        with pytest.raises(Exception,match='fixture failure'):b.exchange.apply(stage['id'],stage['revision'])
        assert b.count()==0
        with b.db.catalog() as db:
            assert db.execute('SELECT COUNT(*) FROM catalogue_entry_ids').fetchone()[0]==0
            db.execute('DROP TRIGGER fixture_import_abort')
        assert b.exchange.apply(stage['id'],stage['revision'])['added']==2
        stage=b.exchange.preview(raw);b.service.set_preferred_title(1,'A newer local choice')
        with pytest.raises(ValueError,match='changed'):b.exchange.apply(stage['id'],stage['revision'])


def test_strict_bounded_wire_format_rejects_malformed_identity_and_json(worlds):
    a=worlds('a')
    with a.active():a.native('Valid catalogue work','/one');raw=a.dump()
    value=json.loads(raw)
    for damaged in (b'\xff',raw[:-1],b'{"format":1,"format":2}',b'{"format":NaN}'):
        with pytest.raises(ValueError):wire.decode(damaged)
    value['entries'][0]['uid']='1'
    with pytest.raises(ValueError):wire.decode(json.dumps(value).encode())


def test_gallery_source_policy_partitions_shared_groups_and_retains_scoped_lineage(worlds):
    a,b=worlds('a'),worlds('b')
    with a.active():
        with a.db.catalog() as db:db.execute("INSERT INTO source_config(source_id,name,last_seen) VALUES('17','E-Hentai','now')")
        a.service._reload_source_cache()
        for mid,part in ((11,1),(12,2)):
            payload={'id':mid,'title':f'[Fixture Artist] Shared Garden Part {part} [English]','url':f'/gallery/{mid}','genre':['artist:Fixture Artist','fantasy','adventure'],'initialized':True}
            owner=a.service.ingest_manga('17',payload)[0]
        assert a.count()==1
        raw=a.dump();group_uid=json.loads(raw)['entries'][0]['uid']
    with b.active():
        with b.db.catalog() as db:db.execute("INSERT INTO source_config(source_id,name,last_seen) VALUES('17','E-Hentai','now')")
        b.service._reload_source_cache();state=gallery_policy.read(b.db,'17')
        gallery_policy.save(b.db,b.service,'17',{**state['policy'],'enabled':False},state['revision'])
        result=b.add(raw);assert result['added']==2 and b.count()==2
        shared=b.dump();assert len({r['uid'] for r in json.loads(shared)['entries']})==2
        grouped=portable_identity.resolve(b.db,b.service,group_uid)
        assert grouped['ambiguous'] and grouped['total']==2
        assert b.add(raw)['added']==0 and b.count()==2
    with a.active():
        assert a.add(shared)['added']==0 and a.count()==1
        assert portable_identity.resolve(a.db,a.service,group_uid)['items'][0]['id']==owner


def test_native_gallery_split_moves_part_scoped_ids_and_keeps_group_history(worlds):
    a=worlds('a')
    with a.active():
        with a.db.catalog() as db:db.execute("INSERT INTO source_config(source_id,name,last_seen) VALUES('17','E-Hentai','now')")
        a.service._reload_source_cache()
        for mid,part in ((11,1),(12,2)):
            a.service.ingest_manga('17',{'id':mid,'title':f'[Fixture Artist] Native Garden Part {part} [English]','url':f'/gallery/{mid}','genre':['artist:Fixture Artist','fantasy'],'initialized':True})
        first=a.dump();old_uid=json.loads(first)['entries'][0]['uid'];state=gallery_policy.read(a.db,'17')
        gallery_policy.save(a.db,a.service,'17',{**state['policy'],'enabled':False},state['revision'])
        a.service.apply_gallery_layout('17')
        assert a.count()==2
        result=portable_identity.resolve(a.db,a.service,old_uid)
        assert result['total']==2
        later=json.loads(a.dump());assert len({r['uid'] for r in later['entries']})==2
        for record in later['entries']:
            assert portable_identity.resolve(a.db,a.service,record['uid'])['total']==1
