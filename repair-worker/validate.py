"""Functional checks run by the controller, never by the code-writing agent."""
import io
import re
import time

import httpx
from PIL import Image

from contracts import source_identity


class SourceBlocked(RuntimeError):
    pass


def image_check(data):
    if not data or len(data) > 32 * 1024 * 1024:
        raise ValueError('Image is empty or exceeds 32 MiB')
    with Image.open(io.BytesIO(data)) as image:
        if image.width < 2 or image.height < 2:
            raise ValueError('Source returned a placeholder instead of a usable image')
        if image.width*image.height > 32_000_000:
            raise SourceBlocked('Image dimensions exceed the bounded validation budget')
        image.verify()
    with Image.open(io.BytesIO(data)) as image:
        image.load()


class Validator:
    def __init__(self, base_url, *, client=None, pace=3.0, max_requests=160, cancel=None, converted_jar_reader=None):
        self.converted_jar_reader = converted_jar_reader
        self.installed_extension = None
        self.client = client or httpx.Client(base_url=base_url, timeout=45, trust_env=False)
        self.pace, self.max_requests = pace, max_requests
        self.requests = 0
        self.last = 0.0
        self.cancel = cancel or (lambda: None)

    def close(self):
        self.client.close()

    def response(self, method, path, *, paced=True, **kwargs):
        self.cancel()
        if paced:
            self.requests += 1
            if self.requests > self.max_requests:
                raise SourceBlocked('Validation request budget reached; no additional source requests were sent')
            time.sleep(max(0, self.last+self.pace-time.monotonic()))
            self.last = time.monotonic()
        # Streaming bounds also apply to JSON/error responses, not only images.
        with self.client.stream(method, path, **kwargs) as response:
            data = bytearray()
            deadline = time.monotonic()+90
            for chunk in response.iter_bytes():
                self.cancel()
                if time.monotonic() > deadline:
                    raise SourceBlocked('Source response exceeded the overall validation deadline')
                data.extend(chunk)
                if len(data) > 32 * 1024 * 1024:
                    raise ValueError('Source response exceeds 32 MiB')
            result = httpx.Response(response.status_code, headers=response.headers, content=bytes(data), request=response.request)
        if result.status_code == 429:
            raise SourceBlocked('Source rate limit reached; validation stopped without retrying')
        if result.status_code >= 400:
            self.check_error(result.text)
            raise SourceBlocked('Source/runtime HTTP '+str(result.status_code)+'; validation is inconclusive, no code repair attempted')
        result.raise_for_status()
        self.cancel()
        return result

    @staticmethod
    def check_error(message):
        text = str(message).lower()
        if any(word in text for word in ('captcha', 'cloudflare challenge', 'shape-selecting', 'waf challenge', 'human verification')):
            raise SourceBlocked('Source needs human verification. No automatic retry; use its WebView and start a new check.')
        if any(word in text for word in ('429', 'too many requests', 'rate limit')):
            raise SourceBlocked('Source rate limit reached; validation stopped without retrying')
        if re.search(r'http(?: error| status)?\s*[:=]?\s*(?:401|403|5\d\d)', text) or any(word in text for word in ('timed out', 'timeout', 'connection refused', 'unknownhostexception')):
            raise SourceBlocked('Source connection or access failed; validation is inconclusive, no parser repair attempted')

    def graphql(self, query, variables=None, *, paced=True):
        result = self.response('POST', '/api/graphql', json={'query': query, 'variables': variables or {}}, paced=paced).json()
        if result.get('errors'):
            message = '; '.join(str(e.get('message', 'GraphQL error')) for e in result['errors'])
            self.check_error(message)
            raise RuntimeError(message[:2000])
        if not isinstance(result.get('data'), dict):
            raise RuntimeError('Source engine returned no GraphQL data')
        return result['data']

    def install(self, data, pkg, *, apk=False):
        filename, media = ('candidate.apk', 'application/vnd.android.package-archive') if apk else ('candidate.jar', 'application/java-archive')
        self.response('POST', '/api/v1/extension/install', files={'file': (filename, data, media)}, paced=False)
        metadata = 'apkName versionName ' if apk else ''
        result = self.graphql('query { extensions { nodes { pkgName isInstalled '+metadata+'source { nodes { id name lang baseUrl supportsLatest } } } } }', paced=False)
        installed = [e for e in result['extensions']['nodes'] if e.get('isInstalled')]
        if len(installed) != 1 or installed[0].get('pkgName') != pkg:
            raise ValueError('Installed candidate has a different extension package identity')
        self.installed_extension = installed[0]
        sources = installed[0]['source']['nodes']
        if not sources:
            raise ValueError('Extension installed but exposes no sources')
        return sources

    def fetch(self, source_id, kind, query=''):
        data = self.graphql('''mutation($input: FetchSourceMangaInput!) {
          fetchSourceManga(input:$input) { mangas { id title sourceId url } }
        }''', {'input': {'source': str(source_id), 'type': kind, 'page': 1, 'query': query}})
        return data['fetchSourceManga']['mangas']

    def check(self, sources, expected=None):
        if expected is not None and source_identity(sources) != source_identity(expected):
            raise ValueError('Package source IDs, languages or names changed; human identity review required')
        results = []
        for source in sources:
            sid = str(source['id'])
            works = self.fetch(sid, 'POPULAR')
            if not works:
                raise ValueError(source['name']+': popular feed returned no titles')
            query = str(works[0].get('title') or '').strip()
            matches = self.fetch(sid, 'SEARCH', query) if query else []
            if not query or not any(str(x.get('id')) == str(works[0]['id']) for x in matches):
                raise ValueError(source['name']+': search did not retrieve the sampled title')
            if source.get('supportsLatest') and not self.fetch(sid, 'LATEST'):
                raise ValueError(source['name']+': supported latest feed returned no titles')
            # Two independent works, where available. Record exact samples and page
            # indexes; a successful sample never claims every chapter was tested.
            sampled = []
            for work in works[:2]:
                mid = int(work['id'])
                data = self.graphql('''mutation($input: FetchMangaAndChaptersInput!) {
                  fetchMangaAndChapters(input:$input) {
                    manga { id title url description author artist genre thumbnailUrl }
                    chapters { id name url chapterNumber }
                  }
                }''', {'input': {'id': mid, 'fetchManga': True, 'fetchChapters': True}})['fetchMangaAndChapters']
                manga, chapters = data.get('manga') or {}, data.get('chapters') or []
                if not all(manga.get(k) for k in ('title', 'url', 'thumbnailUrl')) or not any(manga.get(k) for k in ('description', 'author', 'artist', 'genre')):
                    raise ValueError(source['name']+': title, URL, cover or descriptive metadata missing')
                image_check(self.response('GET', f'/api/v1/manga/{mid}/thumbnail').content)
                if not chapters or not all(c.get('name') and c.get('url') for c in chapters):
                    raise ValueError(source['name']+': chapter list is empty or incomplete')
                chapter_samples = []
                for position in sorted({0, len(chapters)-1}):
                    chapter = chapters[position]
                    cid = int(chapter['id'])
                    pages = self.graphql('''mutation($input: FetchChapterPagesInput!) {
                      fetchChapterPages(input:$input) { pages }
                    }''', {'input': {'chapterId': cid}})['fetchChapterPages']['pages']
                    if not pages or not all(isinstance(p, str) and p for p in pages):
                        raise ValueError(source['name']+': chapter page list is empty or invalid')
                    indexes = sorted({0, len(pages)//2, len(pages)-1})
                    for page in indexes:
                        route = pages[page]
                        if not re.fullmatch(r'/api/v1/manga/'+str(mid)+r'/chapter/[0-9]+/page/[0-9]+', route):
                            raise ValueError('Page list contains an unexpected reader route')
                        image_check(self.response('GET', route).content)
                    chapter_samples.append({'chapter': chapter['name'], 'page_count': len(pages), 'image_indexes': indexes})
                sampled.append({'title': manga['title'], 'metadata_fields': [k for k,v in manga.items() if v], 'chapter_count': len(chapters), 'chapters': chapter_samples})
            results.append({'source_id': sid, 'source': source['name'], 'search': 'passed', 'works': sampled})
        return results
