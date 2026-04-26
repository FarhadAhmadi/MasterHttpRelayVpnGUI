"""
Network-stability patches.

Eliminates HTTP/2 flow-control errors and large-write stalls:
  - forces ALPN to http/1.1 on every SSL context
  - turns off the optional h2 transport
  - fragments browser-side writes into small chunks
  - reduces parallel-range chunk_size and max_parallel defaults
  - allows chunked downloads to be turned off entirely
  - exposes custom_sni from config
"""

import functools
import logging
import ssl

log = logging.getLogger("NetPatch")


def apply(cfg):
    _disable_http2(cfg)
    _patch_ssl_alpn()
    _patch_writer_fragmentation(cfg)
    _patch_parallel_defaults(cfg)
    _patch_relay_smart(cfg)
    _apply_custom_sni(cfg)


def _disable_http2(cfg):
    if cfg.get("enable_http2", False):
        log.info("HTTP/2 transport ENABLED via config")
        return
    try:
        import h2_transport
        h2_transport.H2_AVAILABLE = False
        log.info("HTTP/2 disabled")
    except Exception:
        pass


_orig_default_ctx = ssl.create_default_context


def _http1_only_default_context(*args, **kwargs):
    ctx = _orig_default_ctx(*args, **kwargs)
    try:
        ctx.set_alpn_protocols(["http/1.1"])
    except (NotImplementedError, ssl.SSLError):
        pass
    return ctx


def _patch_ssl_alpn():
    if getattr(ssl.create_default_context, "_mrelay_patched", False):
        return
    _http1_only_default_context._mrelay_patched = True
    ssl.create_default_context = _http1_only_default_context
    log.info("ALPN forced to http/1.1")


def _patch_writer_fragmentation(cfg):
    import proxy_server
    PS = proxy_server.ProxyServer
    if getattr(PS, "_mrelay_frag_patched", False):
        return
    PS._mrelay_frag_patched = True

    frag = max(int(cfg.get("fragment_size", 16384)), 1024)
    log.info("response fragmenter active (%d B/chunk)", frag)

    orig_mitm = PS._do_mitm_connect
    orig_http = PS._do_http

    async def mitm_wrapped(self, host, port, reader, writer):
        return await _run_with_fragmenter(orig_mitm, self, frag, host, port, reader, writer)

    async def http_wrapped(self, header_block, reader, writer):
        return await _run_with_fragmenter(orig_http, self, frag, header_block, reader, writer)

    PS._do_mitm_connect = mitm_wrapped
    PS._do_http = http_wrapped


async def _run_with_fragmenter(orig_coro, self, frag, *args):
    writer = args[-1]
    real_write = writer.write

    def fragmenting_write(data):
        if not data:
            return
        if len(data) <= frag:
            real_write(data); return
        view = memoryview(data)
        for off in range(0, len(view), frag):
            real_write(bytes(view[off:off + frag]))

    writer.write = fragmenting_write
    try:
        return await orig_coro(self, *args)
    finally:
        try: writer.write = real_write
        except Exception: pass


def _patch_parallel_defaults(cfg):
    import domain_fronter
    DF = domain_fronter.DomainFronter
    if getattr(DF, "_mrelay_par_patched", False):
        return
    DF._mrelay_par_patched = True

    chunk = int(cfg.get("chunk_size", 128 * 1024))
    par   = int(cfg.get("max_parallel", 4))
    orig = DF.relay_parallel

    @functools.wraps(orig)
    async def wrapped(self, method, url, headers, body, chunk_size=None, max_parallel=None):
        return await orig(self, method, url, headers, body,
                          chunk_size if chunk_size is not None else chunk,
                          max_parallel if max_parallel is not None else par)

    DF.relay_parallel = wrapped
    log.info("parallel relay defaults: chunk=%d parallel=%d", chunk, par)


def _patch_relay_smart(cfg):
    if cfg.get("enable_chunked", True):
        return
    import proxy_server
    PS = proxy_server.ProxyServer
    if getattr(PS, "_mrelay_smart_patched", False):
        return
    PS._mrelay_smart_patched = True

    async def simple(self, method, url, headers, body):
        return await self.fronter.relay(method, url, headers, body)

    PS._relay_smart = simple
    log.info("chunked/parallel relays disabled")


def _apply_custom_sni(cfg):
    sni = (cfg.get("custom_sni") or "").strip()
    if not sni:
        return
    import domain_fronter
    DF = domain_fronter.DomainFronter
    if getattr(DF, "_mrelay_sni_patched", False):
        return
    DF._mrelay_sni_patched = True

    orig = DF.__init__

    @functools.wraps(orig)
    def wrapped(self, config):
        orig(self, config)
        try:
            old = self.sni_host
            self.sni_host = sni
            log.info("custom SNI: %s -> %s", old, sni)
        except Exception as e:
            log.warning("custom_sni not applied: %s", e)

    DF.__init__ = wrapped
