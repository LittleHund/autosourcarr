"""Trusted in-namespace bridge. Exposes no arbitrary host routes or filesystem.

The controller binds only an authenticated-by-filesystem public-egress socket to
/run/repair/egress.sock. Local proxy clients cannot reach outside this namespace
except through that socket, whose host server validates every destination.
"""
from __future__ import annotations

import argparse
import asyncio
import os
from pathlib import Path
import signal
import sys


async def _copy(reader, writer):
    while data := await asyncio.wait_for(reader.read(65536), 90):
        writer.write(data)
        await writer.drain()
    try:
        writer.write_eof()
        await writer.drain()
    except (AttributeError, OSError):
        pass


async def _bridge(reader, writer, connect):
    other = None
    tasks = []
    try:
        incoming, other = await asyncio.wait_for(connect(), 10)
        tasks = [asyncio.create_task(_copy(reader, other)),
                 asyncio.create_task(_copy(incoming, writer))]
        # Close after both half-closes, but bounded waits prohibit hanging idle
        # tunnels. This avoids truncating a response after request EOF.
        await asyncio.gather(*tasks)
    except (OSError, asyncio.TimeoutError, asyncio.IncompleteReadError):
        pass
    finally:
        for task in tasks:
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
        for stream in (other, writer):
            if stream:
                stream.close()
                try:
                    await stream.wait_closed()
                except OSError:
                    pass


async def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument('--proxy-socket', required=True)
    parser.add_argument('--engine-socket')
    parser.add_argument('--ai-socket')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args(argv)
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command:
        raise ValueError('A command is required')
    # Fixed local endpoint values must match controller proxy JVM options.
    capacity = asyncio.Semaphore(32)
    connections = set()
    async def bounded(reader, writer, connect):
        current = asyncio.current_task(); connections.add(current)
        try:
            async with capacity:
                await _bridge(reader, writer, connect)
        finally:
            connections.discard(current)
    outbound = await asyncio.start_server(
        lambda r, w: bounded(r, w, lambda: asyncio.open_unix_connection(args.proxy_socket)),
        '127.0.0.1', 8080, limit=65536)
    reverse = None
    ai = None
    process = None
    try:
        if args.ai_socket:
            ai = await asyncio.start_server(
                lambda r, w: bounded(r, w, lambda: asyncio.open_unix_connection(args.ai_socket)),
                '127.0.0.1', 8081, limit=65536)
        if args.engine_socket:
            # New unique socket directory; refuse replacing anything existing.
            if Path(args.engine_socket).exists() or Path(args.engine_socket).is_symlink():
                raise RuntimeError('Source-test gateway socket already exists')
            reverse = await asyncio.start_unix_server(
                lambda r, w: bounded(r, w, lambda: asyncio.open_connection('127.0.0.1', 4567)),
                args.engine_socket, limit=65536)
            os.chmod(args.engine_socket, 0o600)
        try:
            os.nice(5)  # Allow normal application interaction to take precedence.
        except OSError:
            pass
        process = await asyncio.create_subprocess_exec(*command)
        loop = asyncio.get_running_loop()
        cancelled = asyncio.Event()
        for signum in (signal.SIGTERM, signal.SIGINT):
            loop.add_signal_handler(signum, cancelled.set)
        finished = asyncio.create_task(process.wait())
        stopping = asyncio.create_task(cancelled.wait())
        try:
            await asyncio.wait([finished, stopping], return_when=asyncio.FIRST_COMPLETED)
            if stopping.done() and not finished.done():
                process.terminate()
                try:
                    await asyncio.wait_for(asyncio.shield(finished), 5)
                except asyncio.TimeoutError:
                    process.kill()
            return await finished
        finally:
            stopping.cancel()
            await asyncio.gather(stopping, return_exceptions=True)
    finally:
        if process and process.returncode is None:
            process.kill()
            await process.wait()
        outbound.close(); await outbound.wait_closed()
        if ai:
            ai.close(); await ai.wait_closed()
        if reverse:
            reverse.close(); await reverse.wait_closed()
        for task in list(connections):
            task.cancel()
        await asyncio.gather(*connections, return_exceptions=True)
        if args.engine_socket and reverse:
            try:
                Path(args.engine_socket).unlink()
            except FileNotFoundError:
                pass
        # Exiting the PID-namespace's command causes bubblewrap's PID1 to reap
        # descendants; its outer monitor is also killed on controller cancellation.


if __name__ == '__main__':
    try:
        raise SystemExit(asyncio.run(main()))
    except (OSError, ValueError, RuntimeError):
        print('Internal source-job bridge could not start.', file=sys.stderr)
        raise SystemExit(70)
