import asyncio

from app.request_origin import CrossSiteWriteMiddleware, cross_site_write


def headers(**values):
    return [(key.replace('_', '-').encode(), value.encode()) for key, value in values.items()]


def test_cross_site_and_foreign_origin_writes_are_refused():
    assert cross_site_write('POST', '/api/admin/maintenance/reset-database', headers(host='nas:8282', origin='https://evil.example'))
    assert cross_site_write('POST', '/api/admin/maintenance/full-reset', headers(host='nas:8282', sec_fetch_site='cross-site'))
    assert cross_site_write('PUT', '/api/admin/ui-preferences', headers(host='nas:8282', origin='null'))
    assert cross_site_write('DELETE', '/api/admin/x', headers(host='nas:8282', origin='http://nas:30058'))
    assert cross_site_write('POST', '/api/v1/entries/1/refresh', headers(host='nas:8282', origin='https://evil.example'))


def test_same_origin_proxy_and_non_browser_writes_are_allowed():
    assert not cross_site_write('POST', '/api/admin/maintenance/reset-database', headers(host='nas:8282', origin='http://nas:8282', sec_fetch_site='same-origin'))
    assert not cross_site_write('POST', '/api/admin/setup/finish', headers(host='autosourcarr:8282', x_forwarded_host='manga.example.com', origin='https://manga.example.com'))
    assert not cross_site_write('POST', '/api/admin/maintenance/reset-database', headers(host='nas:8282'))
    assert not cross_site_write('GET', '/api/admin/tasks', headers(host='nas:8282', origin='https://evil.example', sec_fetch_site='cross-site'))
    assert not cross_site_write('POST', '/static/app.js', headers(host='nas:8282', origin='https://evil.example'))


def test_middleware_answers_403_without_calling_the_route():
    called = []

    async def route(scope, receive, send):
        called.append(scope['path'])

    sent = []

    async def send(message):
        sent.append(message)

    async def receive():
        return {'type': 'http.request', 'body': b'', 'more_body': False}

    app = CrossSiteWriteMiddleware(route)
    scope = {'type': 'http', 'method': 'POST', 'path': '/api/admin/maintenance/reset-database',
             'headers': headers(host='nas:8282', origin='https://evil.example')}
    asyncio.run(app(scope, receive, send))
    assert called == [] and sent[0]['status'] == 403
    asyncio.run(app(dict(scope, headers=headers(host='nas:8282', origin='http://nas:8282')), receive, send))
    assert called == ['/api/admin/maintenance/reset-database']
