"""Search is a lead; matching maintainer code and first-party evidence are distinct."""
import importlib
from pathlib import Path
import pytest


@pytest.fixture
def api(monkeypatch):
    root=Path(__file__).resolve().parents[2];monkeypatch.syspath_prepend(str(root));monkeypatch.syspath_prepend(str(root/'repair-worker'))
    return importlib.import_module('address_research')


def inputs(**extra):
    return {'package':'eu.kanade.tachiyomi.extension.en.fixture','raw_meta':{'kind':'upstream','origin':{'repository':'https://github.com/keiyoushi/extensions-source','basis':'known-repository'}},
        'sources':[{'id':'123','baseUrl':'https://old.example/'}],
        'files':[{'path':'src/en/fixture/src/Fixture.kt','addresses':['https://old.example/']}],
        'website_checks':[{'source_ids':['123'],'requested_url':'https://old.example/','final_url':'https://old.example/','state':'inconclusive'}],**extra}


class Probe:
    def __init__(self,code='override val baseUrl = "https://new.example"'):self.code=code;self.calls=[]
    def response(self,url,**options):
        self.calls.append(url)
        if '/commits/HEAD' in url:return 200,'','application/json',b'{"sha":"'+b'a'*40+b'"}',True
        if 'duckduckgo.com' in url:return 200,'','text/html',b'<a class="result__a" href="https://unverified.example/">Search result</a>',True
        return 200,'','text/plain',self.code.encode(),True
    def one(self,url):return {'state':'working','final_url':url}


def test_dead_old_address_gets_original_file_revision_evidence_but_search_does_not_auto_trust_it(api):
    result=api.research(inputs(),Probe())
    assert result['targets'][0]['new_url']=='https://new.example/'
    assert result['targets'][0]['trust']=='review-required'
    assert result['targets'][0]['source_revision']=='a'*40
    assert result['leads']==[{'url':'https://unverified.example/','basis':'public-search-lead','trust':'unverified'}]


def test_original_redirect_and_same_maintainer_file_corroborate_the_same_location(api):
    row={'source_ids':['123'],'requested_url':'https://old.example/','final_url':'https://new.example/',
        'redirects':['https://new.example/'],'state':'working'}
    result=api.research(inputs(website_checks=[row]),Probe())
    assert result['targets'][0]['trust']=='corroborated' and not result['leads']


def test_no_repository_or_ambiguous_multisite_code_is_replaced_by_a_search_hit(api):
    assert not api.research(inputs(raw_meta={'origin':{}}),Probe())['targets']
    assert not api.research(inputs(),Probe('val baseUrl="https://one.example"\nval baseUrl="https://two.example"'))['targets']


def test_private_query_and_non_baseurl_strings_are_not_trusted_targets(api):
    assert api.base_addresses('val apiKey="https://tracking.example"; val baseUrl="http://127.0.0.1/"')==[]
    assert api.base_addresses('val baseUrl="https://example.com/?secret=abc"')==[]


def test_typed_kotlin_and_literal_decompiled_java_getters_are_supported(api):
    assert api.base_addresses('override val baseUrl: String = "https://new.example"')==['https://new.example/']
    assert api.base_addresses('public String getBaseUrl() { return "https://new.example"; }')==['https://new.example/']
