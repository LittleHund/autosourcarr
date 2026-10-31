#!/usr/bin/env python3
"""Minimal stand-in for the C# EgressProxy, used only by scripts/repair-runner-check.sh.

Listens on 127.0.0.1:<ephemeral>, writes the port to --port-file, allows CONNECT <host>:443
only for hosts given with --allow (exact match) and answers 403 to everything else, including
any plain-HTTP request. It never adds a Via header. EGRESS_STANDIN_UPSTREAM=host:port chains
allowed tunnels through another HTTP proxy (for development containers without direct egress).
"""
import argparse
import os
import socket
import sys
import threading

MAX_HEAD = 16 * 1024


def read_head(conn):
    data = b""
    while b"\r\n\r\n" not in data:
        chunk = conn.recv(4096)
        if not chunk:
            return None, b""
        data += chunk
        if len(data) > MAX_HEAD:
            return None, b""
    head, _, rest = data.partition(b"\r\n\r\n")
    return head.decode("latin-1"), rest


def pipe(source, target):
    try:
        while True:
            chunk = source.recv(65536)
            if not chunk:
                break
            target.sendall(chunk)
    except OSError:
        pass
    finally:
        for sock in (source, target):
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass


def refuse(conn, reason, log):
    body = (reason + "\n").encode()
    conn.sendall(b"HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain\r\nConnection: close\r\nContent-Length: "
                 + str(len(body)).encode() + b"\r\n\r\n" + body)
    log.write("REFUSED " + reason + "\n")
    log.flush()


def open_upstream(host, port):
    upstream = os.environ.get("EGRESS_STANDIN_UPSTREAM")
    if not upstream:
        return socket.create_connection((host, port), timeout=15)
    proxy_host, _, proxy_port = upstream.rpartition(":")
    sock = socket.create_connection((proxy_host, int(proxy_port)), timeout=15)
    sock.sendall(f"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n".encode())
    head, _ = read_head(sock)
    if not head or head.split(" ", 2)[1] != "200":
        sock.close()
        raise OSError("upstream proxy refused the tunnel")
    return sock


def handle(conn, allowed, log):
    with conn:
        conn.settimeout(30)
        head, rest = read_head(conn)
        if head is None:
            return
        parts = head.split("\r\n", 1)[0].split(" ")
        if len(parts) != 3 or parts[0] != "CONNECT":
            refuse(conn, "only CONNECT is supported by the stand-in", log)
            return
        host, _, port = parts[1].rpartition(":")
        host = host.lower().rstrip(".")
        if port != "443" or host not in allowed:
            refuse(conn, f"{host}:{port} is not an allowed egress host", log)
            return
        try:
            upstream = open_upstream(host, 443)
        except OSError as error:
            conn.sendall(b"HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")
            log.write(f"FAILED {host}: {error}\n")
            log.flush()
            return
        log.write(f"ALLOWED {host}:443\n")
        log.flush()
        conn.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
        conn.settimeout(None)
        upstream.settimeout(None)
        if rest:
            upstream.sendall(rest)
        worker = threading.Thread(target=pipe, args=(upstream, conn), daemon=True)
        worker.start()
        pipe(conn, upstream)
        worker.join(timeout=30)
        upstream.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--allow", action="append", default=[])
    parser.add_argument("--port-file", required=True)
    parser.add_argument("--log", required=True)
    args = parser.parse_args()
    allowed = {host.lower() for host in args.allow}
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.bind(("127.0.0.1", 0))
    server.listen(32)
    log = open(args.log, "a", encoding="utf-8")
    with open(args.port_file + ".tmp", "w", encoding="ascii") as handle_file:
        handle_file.write(str(server.getsockname()[1]))
    os.replace(args.port_file + ".tmp", args.port_file)
    while True:
        conn, _ = server.accept()
        threading.Thread(target=handle, args=(conn, allowed, log), daemon=True).start()


if __name__ == "__main__":
    sys.exit(main())
