"""Cross-install identities use public anchors, never local row numbers/titles."""
import copy
import pytest
from app import portable_identity as identity


def entry(local=1,source='17',url='/manga/example',**extra):
    return {'id':local,'canonical_id':local,'preferred_title':'Example','identities':[],
            'matches':[{'source_id':source,'source_manga_id':local*10,'source_manga_url':url}],**extra}


def test_different_instances_and_titles_have_the_same_source_reference():
    a=entry();b=entry(9001);b['preferred_title']='A manually renamed title'
    assert identity.describe(a)['references']==identity.describe(b)['references']
    reference=identity.describe(a)['references'][0]['id']
    assert identity.parse(reference)==('record','tachiyomi','17','/manga/example')


def test_full_and_relative_source_urls_have_the_same_path_identity():
    assert identity.describe(entry(url='/a?edition=2'))['references']==identity.describe(entry(url='https://site.example/a?edition=2'))['references']
    assert identity.describe(entry(url='/a?edition=2'))['references']!=identity.describe(entry(url='/a?edition=1'))['references']


def test_sources_editions_and_case_sensitive_paths_remain_separate():
    refs=[identity.describe(x)['references'][0]['id'] for x in [entry(),entry(source='18'),entry(url='/manga/Example'),entry(url='/manga/example-color')]]
    assert len(set(refs))==4


def test_signed_64bit_source_ids_and_unicode_paths_remain_exact():
    value=identity.record_id('9223372036854775807','/作品/第1話?edition=2')
    assert identity.parse(value)==('record','tachiyomi','9223372036854775807','/作品/第1話?edition=2')
    assert identity.record_id('17','/a?')!=identity.record_id('17','/a')
    for source in ('0','-1'):
        assert identity.describe(entry(source=source))['references']==[]
    assert identity.describe(entry(url='/'+'作'*4000))['references']==[]


def test_provider_ids_are_namespaced_and_fuzzy_links_stay_unconfirmed():
    value=entry(identities=[{'provider':'anilist','external_id':'0012','identity_scope':'work','confidence':.99,'locked':0},
                          {'provider':'mangadex','external_id':'00000000-0000-4000-8000-000000000001','identity_scope':'edition','locked':1}])
    result=identity.describe(value)
    by_provider={r['provider']:r for r in result['references']}
    assert by_provider['anilist']['id']=='asc1:work:anilist:12'
    assert by_provider['anilist']['association']=='inferred'
    assert by_provider['mangadex']['association']=='locked'
    assert by_provider['mangadex']['scope']=='edition'


def test_empty_records_do_not_mint_title_ids_and_input_is_not_mutated():
    value=entry(matches=[]);before=copy.deepcopy(value)
    assert identity.describe(value)['references']==[]
    assert value==before


@pytest.mark.parametrize('url,expected',[('https://nhentai.net/g/123/','asc1:gallery:nhentai:123'),
    ('https://e-hentai.org/g/123/abcdef/','asc1:gallery:ehentai:123'),('https://exhentai.org/g/123/abcdef/','asc1:gallery:ehentai:123'),
    ('https://hitomi.la/galleries/456.html','asc1:gallery:hitomi:456'),('https://hitomi.la/doujinshi/fixture-456.html','asc1:gallery:hitomi:456')])
def test_gallery_ids_are_shared_across_extensions_and_languages(url,expected):
    for source in ('17','18'):
        refs=identity.describe(entry(source=source,url=url))['references']
        assert len(refs)==1,'Known gallery references do not export access-token paths'
        assert expected in {r['id'] for r in refs}
        assert identity.parse(expected)[0]=='gallery'


def test_gallery_mirrors_and_conflicting_urls_do_not_establish_a_common_id():
    assert identity.gallery_reference({'source_manga_url':'https://nhentai.net.evil.example/g/123/'}) is None
    assert identity.gallery_reference({'source_manga_url':'https://nhentai.net/g/123/','real_url':'https://nhentai.net/g/124/'}) is None


@pytest.mark.parametrize('url',['http://user:password@example.com/a','/a?token=secret','javascript:alert(1)','//evil.example/a','/a#private','/a\n'])
def test_unsafe_or_credential_source_urls_are_not_exported(url):
    assert identity.describe(entry(url=url))['references']==[]


@pytest.mark.parametrize('reference',['asc1:work:anilist:-1','asc1:work:unknown:12','asc1:work:anilist:0012','asc1:record:tachiyomi:17:@@','asc1:record:tachiyomi:17:YQ','asc1:edition:anilist:12'])
def test_invalid_or_noncanonical_ids_are_rejected(reference):
    with pytest.raises(ValueError):identity.parse(reference)
