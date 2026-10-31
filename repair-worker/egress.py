"""Minimal public-Internet-only forward proxy for the internal job network.

Resolve before connecting and pin the socket to that IP: private/reserved/mixed
DNS answers are refused. No request/URL/header logging or TLS interception.
"""
import asyncio
import ipaddress
import socket
from urllib.parse import urlsplit


def destination(target, connect=False):
    url = urlsplit(('https://' if connect else '')+target)
    if url.scheme not in {'http', 'https'} or url.username or url.password or not url.hostname:
        raise ValueError('Invalid proxy destination')
    port = url.port or (443 if url.scheme == 'https' else 80)
    if port not in {80, 443}:
        raise ValueError('Only public HTTP/HTTPS destinations are allowed')
    return url.hostname, port, (url.path or '/')+('?' + url.query if url.query else '')


def public_address(host, port):
    addresses = socket.getaddrinfo(host, port, type=socket.SOCK_STREAM)
    if not addresses:
        raise ValueError('Destination has no addresses')
    for row in addresses:
        ip = ipaddress.ip_address(row[4][0])
        if not ip.is_global or ip.is_multicast or ip.is_reserved:
            raise ValueError('Private/reserved destinations are forbidden')
        if isinstance(ip, ipaddress.IPv6Address) and ip.ipv4_mapped and not ip.ipv4_mapped.is_global:
            raise ValueError('Private IPv4-mapped destination is forbidden')
    return addresses[0][4][0]


async def pipe(reader, writer):
    while data := await asyncio.wait_for(reader.read(65536), 90):
        writer.write(data)
        await writer.drain()
    try:
        writer.write_eof(); await writer.drain()
    except (AttributeError,OSError): pass


capacity = asyncio.Semaphore(32)


async def handle(reader, writer):
    upstream = None
    accepted = False
    try:
        async with capacity:
            header = await asyncio.wait_for(reader.readuntil(b'\r\n\r\n'), 10)
            if len(header) > 16384: raise ValueError('Header limit')
            lines = header.decode('iso-8859-1').split('\r\n')
            method, target, version = lines[0].split(' ', 2)
            if version not in {'HTTP/1.0', 'HTTP/1.1'}: raise ValueError('HTTP version')
            connect = method == 'CONNECT'
            host, port, path = destination(target, connect)
            address = await asyncio.wait_for(asyncio.to_thread(public_address, host, port), 10)
            incoming, upstream = await asyncio.wait_for(asyncio.open_connection(address, port), 10)
            if connect:
                writer.write(b'HTTP/1.1 200 Connection Established\r\n\r\n')
                await writer.drain()
            else:
                forwarded = [line for line in lines[1:] if line and not line.lower().startswith(('proxy-', 'connection:'))]
                upstream.write((f'{method} {path} {version}\r\n'+'\r\n'.join(forwarded)+'\r\nConnection: close\r\n\r\n').encode('iso-8859-1'))
                await upstream.drain()
            accepted = True
            tasks = [asyncio.create_task(pipe(reader, upstream)), asyncio.create_task(pipe(incoming, writer))]
            try:
                await asyncio.gather(*tasks)
            finally:
                for task in tasks: task.cancel()
                await asyncio.gather(*tasks, return_exceptions=True)
    except Exception:
        if not accepted:
            writer.write(b'HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n')
    finally:
        if upstream:
            upstream.close()
            try: await upstream.wait_closed()
            except OSError: pass
        writer.close()
        try: await writer.wait_closed()
        except OSError: pass


async def main():
    server = await asyncio.start_server(handle, '0.0.0.0', 8080, limit=16384)
    async with server:
        await server.serve_forever()


if __name__ == '__main__':
    asyncio.run(main())
