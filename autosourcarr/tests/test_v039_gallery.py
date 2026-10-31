from __future__ import annotations

import uuid

import pytest

from app import tag_store
from app.db import db
from app.gallery_metadata import enrich_gallery_payload, gallery_pair_evidence, is_gallery_source
from app.service import AutoSourcarrService


def parsed(title, tags=None, **fields):
    return enrich_gallery_payload({"id": 1, "title": title, "genre": tags or [], **fields}, {"name": "E-Hentai"})


def evidence(left, right):
    return gallery_pair_evidence(left.get("_gallery_metadata"), right.get("_gallery_metadata"), tag_store.canonical_keys)


def test_decorations_become_provenance_bound_metadata_without_synthetic_summary():
    title = '(C105) [Silver Circle (Mira Vale)] The Glass Lantern (Sky Kingdom) [English] [Digital]'
    source = {"title": title, "genre": ["artist:Mira Vale", "parody:Sky Kingdom", "fantasy"]}
    result = enrich_gallery_payload(source, {"name": "ExHentai"})
    assert source["title"] == title
    assert result["title"] == "The Glass Lantern"
    data = result["_gallery_metadata"]
    assert data["artist"] == "Mira Vale" and data["circle"] == "Silver Circle"
    assert data["parody"] == "Sky Kingdom" and data["language"] == "English"
    assert data["raw_title"] == title
    assert data["provenance"]["artist"] == "source field"
    assert "description" not in result
    assert enrich_gallery_payload(result, {"name": "ExHentai"}) == result


def test_normal_manga_source_is_not_bracket_stripped():
    data = {"title": "[A Tale] Of Hidden Doors (Finale)", "genre": ["fantasy"]}
    assert enrich_gallery_payload(data, {"name": "Ordinary Manga"}) == data
    assert not is_gallery_source({"base_url": "https://e-hentai.org.fake.invalid"})
    assert is_gallery_source({"base_url": "https://e-hentai.org"})


@pytest.mark.parametrize('title', ['Artist ::: Example Studio', 'Artist - Mira Vale', '[Mira Vale] Updated 2026-09-20', '[Mira Vale] Complete Works'])
def test_artist_collection_does_not_auto_merge(title):
    item = parsed(title, ["artist:Mira Vale", "fantasy", "adventure"])
    assert item["_gallery_metadata"]["kind"] == "collection"
    assert not evidence(item, item)["eligible"]


def test_gallery_without_provider_matches_by_native_title_creator_and_tags():
    a = parsed('[Mira Vale] The Glass Lantern [English]', ['fantasy', 'science fiction'])
    b = parsed('[Mira Vale] The Glass Lantern [Japanese]', ['fantasy', 'sci-fi'])
    assert evidence(a, b)["eligible"]
    assert len(evidence(a, b)["tag_overlap"]) == 3


@pytest.mark.parametrize('left,right,ltags,rtags', [
    ('[Mira Vale] The Glass Lantern','[Another Artist] The Glass Lantern',['fantasy'],['fantasy']),
    ('The Glass Lantern','The Glass Lantern',['English','Manga'],['English','Manga']),
    ('[Mira Vale] The Glass Lantern','[Mira Vale] Castle Under The Moon',['fantasy','adventure'],['fantasy','adventure']),
    ('[Mira Vale] The Glass Lantern Part 1','[Mira Vale] The Glass Lantern Part 2',['fantasy'],['fantasy']),
    ('[Mira Vale] The Glass Lantern (Colored)','[Mira Vale] The Glass Lantern (Monochrome)',['fantasy'],['fantasy']),
    ('[Mira Vale] The Glass Lantern (Manga)','[Mira Vale] The Glass Lantern (Webtoon)',['fantasy'],['fantasy']),
])
def test_identity_conflicts_are_not_rescued_by_exact_titles_or_tags(left,right,ltags,rtags):
    assert not evidence(parsed(left,ltags),parsed(right,rtags))["eligible"]


def source(sid, name):
    with db.catalog() as c:
        c.execute('''INSERT INTO source_config(source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available)
        VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)''', (sid,name,name,'en','https://example.invalid','',1,1,1,1,50,'now',1))


def setup_pair():
    suffix = uuid.uuid4().hex[:8]
    a,b = 'gallery-a-'+suffix, 'gallery-b-'+suffix
    source(a,'E-Hentai');source(b,'Luscious')
    return AutoSourcarrService(),a,b,'The Glass Lantern '+suffix


def manga(mid,title,sid,tags=None):
    return {"id":mid,"sourceId":sid,"title":title,"genre":tags or [],"initialized":True,"description":"","url":f"/gallery/{mid}"}


def test_ingest_gallery_fallback_and_rich_matching_preserve_manual_title():
    s,a,b,title=setup_pair()
    left=manga(91,'[Mira Vale] '+title+' [English]',a,['fantasy','sci-fi'])
    eid,_,_=s.ingest_manga(a,left)
    s.set_preferred_title(eid,'My Personal Label')
    right=manga(92,'[Mira Vale] '+title+' [Japanese]',b,['fantasy','science fiction'])
    other,_,_=s.ingest_manga(b,right)
    assert other==eid
    with db.catalog() as c:
        row=c.execute('SELECT preferred_title,artist FROM entries WHERE id=?',(eid,)).fetchone()
        assert row['preferred_title']=='My Personal Label'
        assert row['artist']=='Mira Vale'
    meta=tag_store.entry_metadata(eid)
    assert len(meta['gallery'])==2
    assert meta['metadata_basis']=='source'


def test_sparse_gallery_exact_title_stays_separate_until_source_detail_arrives():
    s,a,b,title=setup_pair()
    eid,_,_=s.ingest_manga(a,manga(101,title,a))
    other,_,_=s.ingest_manga(b,manga(102,title,b))
    assert other!=eid
    assert not s._auto_merge_evidence(eid,other)['eligible']
    s._upsert_match(eid,a,manga(101,title,a,['artist:Mira Vale','fantasy']),1)
    s._upsert_match(other,b,manga(102,title,b,['artist:Mira Vale','fantasy']),1)
    assert s._auto_merge_evidence(eid,other)['eligible']
    s.review_duplicate(eid,other,'not_duplicate')
    assert not s._auto_merge_evidence(eid,other)['eligible']


def test_creator_conflict_blocks_local_discovery_variants_and_duplicate_exact_shortcuts():
    s,a,b,title=setup_pair()
    eid,_,_=s.ingest_manga(a,manga(111,'[Mira Vale] '+title,a,['fantasy','adventure']))
    candidate=manga(112,'[Other Artist] '+title,b,['fantasy','adventure'])
    assert not s._local_match_candidates(candidate,b)
    trusted=s._trusted_automatic_entry_evidence(eid)
    assert not s._source_match_evidence({'id':eid},candidate)['eligible']
    score,_=s._variant_candidate_score(candidate,entry_titles=trusted['titles'],entry_descriptions=[],entry_author='',real_normalized_titles=set(),trusted_payloads=trusted['gallery_payloads'])
    assert score==0
    other,_,_=s.ingest_manga(b,candidate)
    assert eid!=other
    # Even two matching source aliases, a fabricated shared provider identity and
    # duplicate summaries cannot override source creator conflict.
    x=s._entry_duplicate_evidence_snapshot(eid); y=s._entry_duplicate_evidence_snapshot(other)
    for z in (x,y):
        z['normalized_titles']=['the glass lantern','a second exact title']
        z['identities']=[['provider','same']]
    assert not s._auto_merge_snapshot_evidence(x,y)['eligible']


def test_source_tags_follow_active_owner_on_manual_move_and_split():
    s,a,b,title=setup_pair()
    eid,_,_=s.ingest_manga(a,manga(121,title,a,['artist:Mira Vale','fantasy']))
    other,_,_=s.ingest_manga(b,manga(122,'Another Unique Work '+uuid.uuid4().hex,b,['artist:Other Artist','space']))
    s._upsert_source_variant(other,a,manga(121,title,a,['artist:Mira Vale','fantasy']),1,'manual',True)
    assert not tag_store.entry_metadata(eid)['tags']
    assert any(t['name']=='fantasy' for t in tag_store.entry_metadata(other)['tags'])


def test_provider_title_only_does_not_replace_source_native_gallery_metadata():
    s,a,b,title=setup_pair()
    eid,_,_=s.ingest_manga(a,manga(131,'[Mira Vale] '+title,a,['fantasy','adventure']))
    score,_=s._identity_candidate_score(eid,{'title':title,'description':'A long but unrelated provider synopsis.','genre':['fantasy']})
    assert score==0
    score,_=s._identity_candidate_score(eid,{'title':title,'artist':'Mira Vale','genre':['fantasy','adventure']})
    assert score>=.94


def test_import_exact_title_requires_gallery_tags_but_manual_source_choice_survives():
    s,a,b,title=setup_pair()
    eid,_,_=s.ingest_manga(a,manga(141,'[Mira Vale] '+title,a,['fantasy','adventure']))
    item={'title':title,'source':b,'sourceName':'Luscious'}
    assert not s._import_candidate_evidence(item,eid)['eligible']
    item.update({'artist':'Mira Vale','genre':['fantasy','adventure']})
    assert s._import_candidate_evidence(item,eid)['eligible']
    s._upsert_source_variant(eid,b,manga(142,'Uncertain Work',b),1,'manual',True)
    with db.catalog() as c:
        assert c.execute('SELECT locked FROM source_variants WHERE entry_id=? AND source_id=?',(eid,b)).fetchone()['locked']


def test_collection_creator_label_and_namespace_aliases_are_clean():
    assert parsed('Artist ::: Example Studio')['_gallery_metadata']['artist']=='Example Studio'
    gallery=parsed('The Glass Lantern',{'artists':['Mira Vale'],'groups':['Silver Circle'],'parodies':['Sky Kingdom']})['_gallery_metadata']
    assert gallery['artist']=='Mira Vale'
    assert gallery['circle']=='Silver Circle'
    assert gallery['parody']=='Sky Kingdom'


def test_unknown_edition_markers_are_retained_in_title():
    item=parsed('[Mira Vale] The Glass Lantern (Special) [English]')
    assert item['title']=='The Glass Lantern (Special)'
    assert not item['_gallery_metadata']['parody']


@pytest.mark.parametrize('header,expected', [('Artist','artist'),('Circle','circle'),('Collection',None)])
def test_bracketed_collection_labels_are_not_creator_names(header,expected):
    title=f'[{header}] Example Creator "UPDATE" 09/08/2020'
    item=parsed(title)
    meta=item['_gallery_metadata']
    assert meta['kind']=='collection'
    assert meta['raw_title']==title
    assert meta['collection_name']=='Example Creator'
    assert meta['annotations']==['"UPDATE" 09/08/2020']
    assert not any(t['name'] in {'Artist','Circle','Collection'} for t in meta['tags'])
    if expected:
        assert meta[expected]=='Example Creator'
        assert meta['provenance'][expected]=='title label'
    else:
        assert not meta['artist'] and not meta['circle']
    assert not evidence(item,item)['eligible']


def test_bracketed_collection_label_preserves_creator_named_update_without_date():
    meta=parsed('[Artist] Update Studio')['_gallery_metadata']
    assert meta['artist']=='Update Studio'
    assert not meta['annotations']


@pytest.mark.parametrize('title', ['[Artist] Example Creator "UPDATE" 09/08/2020','[Example Creator] The Glass Lantern'])
def test_cached_parsed_creator_does_not_become_claimed_source_field(title):
    item=parsed(title)
    again=enrich_gallery_payload(item,{'name':'E-Hentai'})
    assert again['_gallery_metadata']['provenance']['artist']==item['_gallery_metadata']['provenance']['artist']
    assert again['_gallery_metadata']['raw_title']==title


@pytest.mark.parametrize('genre', ['["Action", "artist:Mira Vale"]','{"tag":["Action"],"artist":["Mira Vale"]}'])
def test_legacy_json_genres_decode_as_real_tags(genre):
    meta=parsed('The Glass Lantern',genre)['_gallery_metadata']
    assert {'namespace':'tag','name':'Action'} in meta['tags']
    assert {'namespace':'artist','name':'Mira Vale'} in meta['tags']
    assert not any('[' in t['name'] or '"' in t['name'] for t in meta['tags'])


def test_malformed_json_genre_stays_readable_text():
    meta=parsed('The Glass Lantern','[Sample tag]')['_gallery_metadata']
    assert {'namespace':'tag','name':'[Sample tag]'} in meta['tags']
