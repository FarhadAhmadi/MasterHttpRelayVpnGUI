"""
Bridge layer between the upstream relay and the WPF GUI.
  - patches ProxyServer to count bytes/requests/connections
  - records request success/failure into health.py
  - emits ##STATS## json on stdout once per second
  - configures stderr logging in the format the GUI's regex expects
  - applies network-stability + multi-ID + health patches
"""

import asyncio
import functools
import json
import logging
import sys
import threading
import time

import stats
import net_patches
import log_filter
import health
import multi_id


def configure_logging(level_name="INFO"):
    level = getattr(logging, level_name.upper(), logging.INFO)
    root = logging.getLogger()
    for h in list(root.handlers):
        root.removeHandler(h)

    h = logging.StreamHandler(sys.stderr)
    h.setFormatter(logging.Formatter(
        fmt="%(asctime)s [%(name)-12s] %(levelname)-7s %(message)s",
        datefmt="%H:%M:%S",
    ))
    root.addHandler(h)
    root.setLevel(level)
    log_filter.install(window=2.0)


def _emit_loop():
    while True:
        try:
            sys.stdout.write("##STATS## " + json.dumps(stats.snapshot()) + "\n")
            sys.stdout.flush()
        except Exception:
            pass
        time.sleep(1.0)


def start_stats_emitter():
    threading.Thread(target=_emit_loop, daemon=True, name="stats-emitter").start()


def _patch_proxy_server():
    import proxy_server
    PS = proxy_server.ProxyServer
    if getattr(PS, "_mrelay_stats_patched", False):
        return
    PS._mrelay_stats_patched = True

    orig_on_client = PS._on_client
    orig_do_connect = PS._do_connect
    orig_do_http = PS._do_http

    async def on_client_wrap(self, reader, writer):
        stats.conn_opened()
        _wrap_writer_for_down(writer)
        _wrap_reader_for_up(reader)
        try:
            return await orig_on_client(self, reader, writer)
        finally:
            stats.conn_closed()

    async def do_connect_wrap(self, target, reader, writer):
        stats.incr_requests()
        return await orig_do_connect(self, target, reader, writer)

    async def do_http_wrap(self, header_block, reader, writer):
        stats.incr_requests()
        return await orig_do_http(self, header_block, reader, writer)

    PS._on_client = on_client_wrap
    PS._do_connect = do_connect_wrap
    PS._do_http = do_http_wrap


def _patch_relay_health():
    """Wrap DomainFronter.relay so we can mark each request ok/err for both
    health.py and the multi_id dispatcher."""
    import domain_fronter
    DF = domain_fronter.DomainFronter
    if getattr(DF, "_mrelay_health_patched", False):
        return
    DF._mrelay_health_patched = True

    orig = DF.relay

    @functools.wraps(orig)
    async def wrapped(self, method, url, headers, body):
        started = time.perf_counter()
        sid = ""
        try:
            resp = await orig(self, method, url, headers, body)
            sid = multi_id.current_request_id() or getattr(self, "script_id", "") or ""
            latency_ms = (time.perf_counter() - started) * 1000.0
            ok = bool(resp) and resp[:12].startswith(b"HTTP/1.1 2")
            if not ok and resp[:12].startswith(b"HTTP/1.1 3"):
                ok = True  # 3xx is fine, treat as success
            health.record(ok)
            if ok:
                multi_id.report_ok(sid, latency_ms)
            else:
                multi_id.report_err(sid)
            return resp
        except Exception as e:
            sid = multi_id.current_request_id() or getattr(self, "script_id", "") or ""
            health.record(False)
            multi_id.report_err(sid, str(e))
            raise

    DF.relay = wrapped


def _wrap_writer_for_down(writer):
    orig = writer.write

    def write_counting(data):
        try: stats.add_down(len(data))
        except Exception: pass
        return orig(data)

    writer.write = write_counting


def _wrap_reader_for_up(reader):
    orig_read = reader.read
    orig_readline = reader.readline
    orig_readexactly = reader.readexactly

    async def read_counting(n=-1):
        d = await orig_read(n)
        if d: stats.add_up(len(d))
        return d

    async def readline_counting():
        d = await orig_readline()
        if d: stats.add_up(len(d))
        return d

    async def readexactly_counting(n):
        d = await orig_readexactly(n)
        if d: stats.add_up(len(d))
        return d

    reader.read = read_counting
    reader.readline = readline_counting
    reader.readexactly = readexactly_counting


def install(cfg):
    """Run BEFORE building ProxyServer."""
    net_patches.apply(cfg)
    multi_id.install(cfg)         # rotates ids, parks bad ones
    _patch_proxy_server()         # byte counters
    _patch_relay_health()         # request-level health probe
    start_stats_emitter()
