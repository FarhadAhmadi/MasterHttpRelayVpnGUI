"""
Local HTTP proxy server.

Intercepts the user's browser traffic and forwards everything through
a domain-fronted connection to a CDN worker or Apps Script relay.

Supports:
  - CONNECT method  → WebSocket tunnel (modes 1-3) or MITM relay (apps_script)
  - GET / POST etc. → HTTP forwarding  (modes 1-3) or JSON relay (apps_script)
"""

import asyncio
from collections import OrderedDict
import logging
import re
import ssl
import time

from domain_fronter import DomainFronter

log = logging.getLogger("Proxy")


class ResponseCache:
    """LRU response cache with TTL and lightweight observability."""

    def __init__(self, max_mb: int = 50):
        self._store: OrderedDict[str, tuple[bytes, float]] = OrderedDict()
        self._size = 0
        self._max = max_mb * 1024 * 1024

    def get(self, key: str) -> bytes | None:
        entry = self._store.get(key)
        if not entry:
            return None
        raw, expires = entry
        if time.time() > expires:
            self._size -= len(raw)
            del self._store[key]
            return None
        self._store.move_to_end(key)
        return raw

    def put(self, key: str, raw_response: bytes, ttl: int = 300):
        size = len(raw_response)
        if size > self._max // 4 or size == 0:
            return
        # Evict oldest to make room
        while self._size + size > self._max and self._store:
            _, (old_raw, _) = self._store.popitem(last=False)
            self._size -= len(old_raw)
        if key in self._store:
            self._size -= len(self._store[key][0])
        self._store[key] = (raw_response, time.time() + ttl)
        self._store.move_to_end(key)
        self._size += size

    @property
    def count(self) -> int:
        return len(self._store)

    @property
    def bytes_size(self) -> int:
        return self._size

    @staticmethod
    def parse_ttl(raw_response: bytes, url: str) -> int:
        """Determine cache TTL from response headers and URL."""
        hdr_end = raw_response.find(b"\r\n\r\n")
        if hdr_end < 0:
            return 0
        hdr = raw_response[:hdr_end].decode(errors="replace").lower()

        # Don't cache errors or non-200
        if b"HTTP/1.1 200" not in raw_response[:20]:
            return 0
        if "no-store" in hdr or "private" in hdr or "no-cache" in hdr:
            return 0
        if "set-cookie:" in hdr:
            return 0

        # Explicit max-age
        m = re.search(r"max-age=(\d+)", hdr)
        if m:
            return min(int(m.group(1)), 86400)

        # Heuristic by content type / extension
        path = url.split("?")[0].lower()
        static_exts = (
            ".css", ".js", ".woff", ".woff2", ".ttf", ".eot",
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico",
            ".mp3", ".mp4", ".wasm",
        )
        for ext in static_exts:
            if path.endswith(ext):
                return 3600  # 1 hour for static assets

        ct_m = re.search(r"content-type:\s*([^\r\n]+)", hdr)
        ct = ct_m.group(1) if ct_m else ""
        if "image/" in ct or "font/" in ct:
            return 3600
        if "text/css" in ct or "javascript" in ct:
            return 1800
        if "text/html" in ct or "application/json" in ct:
            return 0  # don't cache dynamic content by default

        return 0


class ProxyServer:
    def __init__(self, config: dict):
        self.host = config.get("listen_host", "127.0.0.1")
        self.port = config.get("listen_port", 8080)
        self.mode = config.get("mode", "domain_fronting")
        self.fronter = DomainFronter(config)
        self.mitm = None
        self._cache = ResponseCache(max_mb=50)
        self._cache_inflight: dict[str, asyncio.Future] = {}
        self._cache_inflight_lock = asyncio.Lock()

        # Persistent HTTP tunnel cache for google_fronting mode
        # Key: "host:port" → (tunnel_reader, tunnel_writer, lock)
        self._http_tunnels: dict = {}
        self._tunnel_lock = asyncio.Lock()

        # hosts override — DNS fake-map: domain/suffix → IP
        # Checked before any real DNS lookup; supports exact and suffix matching.
        self._hosts: dict[str, str] = config.get("hosts", {})

        if self.mode == "apps_script":
            try:
                from mitm import MITMCertManager
                self.mitm = MITMCertManager()
            except ImportError:
                log.error("apps_script mode requires 'cryptography' package.")
                log.error("Run: pip install cryptography")
                raise SystemExit(1)

    def _cache_key(self, method: str, url: str, headers: dict) -> str:
        """Build a conservative key so cached responses are safe to reuse."""
        vary = (
            headers.get("Accept", ""),
            headers.get("Accept-Encoding", ""),
            headers.get("Accept-Language", ""),
        )
        return "|".join([method.upper(), url, *vary])

    @staticmethod
    def _cacheable_request(method: str, body: bytes, headers: dict) -> bool:
        if method.upper() != "GET" or body:
            return False
        cc = headers.get("Cache-Control", "")
        pragma = headers.get("Pragma", "")
        return "no-cache" not in cc.lower() and "no-cache" not in pragma.lower()

    def _emit_cache_stats(self):
        try:
            import stats
            stats.cache_snapshot(self._cache.count, self._cache.bytes_size)
        except Exception:
            pass

    async def _fetch_with_cache(self, method: str, url: str, headers: dict, body: bytes):
        if not self._cacheable_request(method, body, headers):
            return await self._relay_smart(method, url, headers, body)

        key = self._cache_key(method, url, headers)
        cached = self._cache.get(key)
        if cached is not None:
            try:
                import stats
                stats.cache_hit()
            except Exception:
                pass
            return cached

        leader = False
        async with self._cache_inflight_lock:
            fut = self._cache_inflight.get(key)
            if fut is None:
                fut = asyncio.get_running_loop().create_future()
                self._cache_inflight[key] = fut
                leader = True

        if not leader:
            try:
                return await asyncio.wait_for(fut, timeout=20)
            except Exception:
                pass

        try:
            try:
                import stats
                stats.cache_miss()
            except Exception:
                pass
            response = await self._relay_smart(method, url, headers, body)
            ttl = ResponseCache.parse_ttl(response, url)
            if ttl > 0:
                self._cache.put(key, response, ttl)
                self._emit_cache_stats()
            if not fut.done():
                fut.set_result(response)
            return response
        except Exception as e:
            if not fut.done():
                fut.set_exception(e)
            raise
        finally:
            async with self._cache_inflight_lock:
                self._cache_inflight.pop(key, None)

    async def start(self):
        srv = await asyncio.start_server(self._on_client, self.host, self.port)
        log.info(
            "Listening on %s:%d — configure your browser HTTP proxy to this address",
            self.host, self.port,
        )
        async with srv:
            await srv.serve_forever()

    # ── client handler ────────────────────────────────────────────

    async def _on_client(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        addr = writer.get_extra_info("peername")
        try:
            first_line = await asyncio.wait_for(reader.readline(), timeout=30)
            if not first_line:
                return

            # Read remaining headers
            header_block = first_line
            while True:
                line = await asyncio.wait_for(reader.readline(), timeout=10)
                header_block += line
                if line in (b"\r\n", b"\n", b""):
                    break

            request_line = first_line.decode(errors="replace").strip()
            parts = request_line.split(" ", 2)
            if len(parts) < 2:
                return

            method = parts[0].upper()

            if method == "CONNECT":
                await self._do_connect(parts[1], reader, writer)
            else:
                await self._do_http(header_block, reader, writer)

        except asyncio.TimeoutError:
            log.debug("Timeout: %s", addr)
        except Exception as e:
            log.error("Error (%s): %s", addr, e)
        finally:
            try:
                writer.close()
                await writer.wait_closed()
            except Exception:
                pass

    # ── CONNECT (HTTPS tunnelling) ────────────────────────────────

    async def _do_connect(self, target: str, reader, writer):
        host, _, port = target.rpartition(":")
        port = int(port) if port else 443
        if not host:
            host, port = target, 443

        log.info("CONNECT → %s:%d", host, port)

        writer.write(b"HTTP/1.1 200 Connection Established\r\n\r\n")
        await writer.drain()

        if self.mode == "apps_script":
            override_ip = self._sni_rewrite_ip(host)
            if override_ip:
                # SNI-blocked domain: MITM-decrypt from browser, then
                # re-connect to the override IP with SNI=front_domain so
                # the ISP never sees the blocked hostname in the TLS handshake.
                log.info("SNI-rewrite tunnel → %s via %s (SNI: %s)",
                         host, override_ip, self.fronter.sni_host)
                await self._do_sni_rewrite_tunnel(host, port, reader, writer,
                                                  connect_ip=override_ip)
            elif self._is_google_domain(host):
                log.info("Direct tunnel → %s (Google domain, skipping relay)", host)
                await self._do_direct_tunnel(host, port, reader, writer)
            else:
                await self._do_mitm_connect(host, port, reader, writer)
        else:
            await self.fronter.tunnel(host, port, reader, writer)

    # ── Hosts override (fake DNS) ─────────────────────────────────

    # Built-in list of domains that must be reached via Google's frontend IP
    # with SNI rewritten to `front_domain` (default: www.google.com).
    # These are Google-owned services whose real SNI is DPI-blocked in some
    # countries, but that Google serves from the same edge IP as www.google.com.
    # Users don't need to configure anything — any host matching one of these
    # suffixes is transparently SNI-rewritten to the configured `google_ip`.
    # Config's "hosts" map still takes precedence (for custom overrides).
    _SNI_REWRITE_SUFFIXES = (
        "youtube.com",
        "youtu.be",
        "youtube-nocookie.com",
        "ytimg.com",
        "ggpht.com",
        "gvt1.com",
        "gvt2.com",
        "doubleclick.net",
        "googlesyndication.com",
        "googleadservices.com",
        "google-analytics.com",
        "googletagmanager.com",
        "googletagservices.com",
        "fonts.googleapis.com",
    )

    def _sni_rewrite_ip(self, host: str) -> str | None:
        """Return the IP to SNI-rewrite `host` through, or None.

        Order of precedence:
          1. Explicit entry in config `hosts` map (exact or suffix match).
          2. Built-in `_SNI_REWRITE_SUFFIXES` → mapped to config `google_ip`.
        """
        ip = self._hosts_ip(host)
        if ip:
            return ip
        h = host.lower().rstrip(".")
        for suffix in self._SNI_REWRITE_SUFFIXES:
            if h == suffix or h.endswith("." + suffix):
                return self.fronter.connect_host  # configured google_ip
        return None

    def _hosts_ip(self, host: str) -> str | None:
        """Return override IP for host if defined in config 'hosts', else None.

        Supports exact match and suffix match (e.g. 'youtube.com' matches
        'www.youtube.com', 'm.youtube.com', etc.).
        """
        h = host.lower().rstrip(".")
        if h in self._hosts:
            return self._hosts[h]
        # suffix match: check every parent label
        parts = h.split(".")
        for i in range(1, len(parts)):
            parent = ".".join(parts[i:])
            if parent in self._hosts:
                return self._hosts[parent]
        return None

    # ── Google domain detection ───────────────────────────────────

    # Only domains whose SNI the ISP does NOT block — direct tunnel is safe.
    # YouTube/googlevideo SNIs are blocked; they go through _do_sni_rewrite_tunnel
    # via the hosts map instead.
    _GOOGLE_SUFFIXES = (
        ".google.com", ".google.co",
        ".googleapis.com", ".gstatic.com",
        ".googleusercontent.com",
    )
    _GOOGLE_EXACT = {
        "google.com", "gstatic.com", "googleapis.com",
    }

    def _is_google_domain(self, host: str) -> bool:
        """Return True if host is a Google-owned domain."""
        h = host.lower().rstrip(".")
        if h in self._GOOGLE_EXACT:
            return True
        for suffix in self._GOOGLE_SUFFIXES:
            if h.endswith(suffix):
                return True
        return False

    # ── Direct tunnel (no MITM) ───────────────────────────────────

    async def _do_direct_tunnel(self, host: str, port: int,
                                reader: asyncio.StreamReader,
                                writer: asyncio.StreamWriter,
                                connect_ip: str | None = None):
        """Pipe raw TLS bytes directly to the target server.

        connect_ip overrides DNS: the TCP connection goes to that IP
        while the browser's TLS (SNI=host) is piped through unchanged.
        Defaults to the configured google_ip for Google-category domains.
        """
        target_ip = connect_ip or self.fronter.connect_host
        try:
            r_remote, w_remote = await asyncio.wait_for(
                asyncio.open_connection(target_ip, port), timeout=10
            )
        except Exception as e:
            log.error("Direct tunnel connect failed (%s via %s): %s",
                      host, target_ip, e)
            return

        async def pipe(src, dst, label):
            try:
                while True:
                    data = await src.read(65536)
                    if not data:
                        break
                    dst.write(data)
                    await dst.drain()
            except (ConnectionError, asyncio.CancelledError):
                pass
            except Exception as e:
                log.debug("Pipe %s ended: %s", label, e)
            finally:
                try:
                    dst.close()
                except Exception:
                    pass

        await asyncio.gather(
            pipe(reader, w_remote, f"client→{host}"),
            pipe(r_remote, writer, f"{host}→client"),
        )

    # ── SNI-rewrite tunnel ────────────────────────────────────────

    async def _do_sni_rewrite_tunnel(self, host: str, port: int, reader, writer,
                                     connect_ip: str | None = None):
        """MITM-decrypt TLS from browser, then re-encrypt toward connect_ip
        using SNI=front_domain (e.g. www.google.com).

        The ISP only ever sees SNI=www.google.com in the outgoing handshake,
        hiding the blocked hostname (e.g. www.youtube.com).
        """
        target_ip = connect_ip or self.fronter.connect_host
        sni_out   = self.fronter.sni_host  # e.g. "www.google.com"

        # Step 1: MITM — accept TLS from the browser
        ssl_ctx_server = self.mitm.get_server_context(host)
        loop = asyncio.get_event_loop()
        transport = writer.transport
        protocol  = transport.get_protocol()
        try:
            new_transport = await loop.start_tls(
                transport, protocol, ssl_ctx_server, server_side=True,
            )
        except Exception as e:
            log.debug("SNI-rewrite TLS accept failed (%s): %s", host, e)
            return
        writer._transport = new_transport

        # Step 2: open outgoing TLS to target IP with the safe SNI
        ssl_ctx_client = ssl.create_default_context()
        if not self.fronter.verify_ssl:
            ssl_ctx_client.check_hostname = False
            ssl_ctx_client.verify_mode = ssl.CERT_NONE
        try:
            r_out, w_out = await asyncio.wait_for(
                asyncio.open_connection(
                    target_ip, port,
                    ssl=ssl_ctx_client,
                    server_hostname=sni_out,
                ),
                timeout=10,
            )
        except Exception as e:
            log.error("SNI-rewrite outbound connect failed (%s via %s): %s",
                      host, target_ip, e)
            return

        # Step 3: pipe application-layer bytes between the two TLS sessions
        async def pipe(src, dst, label):
            try:
                while True:
                    data = await src.read(65536)
                    if not data:
                        break
                    dst.write(data)
                    await dst.drain()
            except (ConnectionError, asyncio.CancelledError):
                pass
            except Exception as exc:
                log.debug("Pipe %s ended: %s", label, exc)
            finally:
                try:
                    dst.close()
                except Exception:
                    pass

        await asyncio.gather(
            pipe(reader, w_out, f"client→{host}"),
            pipe(r_out,  writer, f"{host}→client"),
        )

    # ── MITM CONNECT (apps_script mode) ───────────────────────────

    async def _do_mitm_connect(self, host: str, port: int, reader, writer):
        """Intercept TLS, decrypt HTTP, and relay through Apps Script."""
        ssl_ctx = self.mitm.get_server_context(host)

        # Upgrade the existing connection to TLS (we are the server)
        loop = asyncio.get_event_loop()
        transport = writer.transport
        protocol = transport.get_protocol()

        try:
            new_transport = await loop.start_tls(
                transport, protocol, ssl_ctx, server_side=True,
            )
        except Exception as e:
            # Non-HTTPS traffic (e.g. MTProto, plain HTTP on port 80/443)
            # routed through the proxy will always fail TLS — log at DEBUG
            # to avoid alarming noise.
            if port != 443:
                log.debug("TLS handshake skipped for %s:%d (non-HTTPS): %s", host, port, e)
            else:
                log.debug("TLS handshake failed for %s: %s", host, e)
            return

        # Update writer to use the new TLS transport
        writer._transport = new_transport

        # Read and relay HTTP requests from the browser (now decrypted)
        while True:
            try:
                first_line = await asyncio.wait_for(reader.readline(), timeout=120)
                if not first_line:
                    break

                header_block = first_line
                while True:
                    line = await asyncio.wait_for(reader.readline(), timeout=10)
                    header_block += line
                    if line in (b"\r\n", b"\n", b""):
                        break

                # Read body
                body = b""
                for raw_line in header_block.split(b"\r\n"):
                    if raw_line.lower().startswith(b"content-length:"):
                        length = int(raw_line.split(b":", 1)[1].strip())
                        body = await reader.readexactly(length)
                        break

                # Parse the request
                request_line = first_line.decode(errors="replace").strip()
                parts = request_line.split(" ", 2)
                if len(parts) < 2:
                    break

                method = parts[0]
                path = parts[1]

                # Parse headers
                headers = {}
                for raw_line in header_block.split(b"\r\n")[1:]:
                    if b":" in raw_line:
                        k, v = raw_line.decode(errors="replace").split(":", 1)
                        headers[k.strip()] = v.strip()

                # Build full URL (browser sends just the path in CONNECT)
                if port == 443:
                    url = f"https://{host}{path}"
                else:
                    url = f"https://{host}:{port}{path}"

                log.info("MITM → %s %s", method, url)

                # ── CORS: extract relevant request headers ────────────────────
                origin = next(
                    (v for k, v in headers.items() if k.lower() == "origin"), ""
                )
                acr_method = next(
                    (v for k, v in headers.items()
                     if k.lower() == "access-control-request-method"), ""
                )
                acr_headers = next(
                    (v for k, v in headers.items()
                     if k.lower() == "access-control-request-headers"), ""
                )

                # CORS preflight — respond directly; UrlFetchApp doesn't
                # support OPTIONS so forwarding it would always fail.
                if method.upper() == "OPTIONS" and acr_method:
                    log.debug("CORS preflight → %s (responding locally)", url[:60])
                    writer.write(self._cors_preflight_response(origin, acr_method, acr_headers))
                    await writer.drain()
                    continue

                try:
                    response = await self._fetch_with_cache(method, url, headers, body)
                except Exception as e:
                    log.error("Relay error (%s): %s", url[:60], e)
                    err_body = f"Relay error: {e}".encode()
                    response = (
                        b"HTTP/1.1 502 Bad Gateway\r\n"
                        b"Content-Type: text/plain\r\n"
                        b"Content-Length: " + str(len(err_body)).encode() + b"\r\n"
                        b"\r\n" + err_body
                    )

                # Inject permissive CORS headers whenever the browser
                # sent an Origin (cross-origin XHR / fetch).
                if origin and response:
                    response = self._inject_cors_headers(response, origin)

                writer.write(response)
                await writer.drain()

            except asyncio.TimeoutError:
                break
            except asyncio.IncompleteReadError:
                break
            except ConnectionError:
                break
            except Exception as e:
                log.error("MITM handler error (%s): %s", host, e)
                break

    # ── CORS helpers ──────────────────────────────────────────────────────────

    @staticmethod
    def _cors_preflight_response(origin: str, acr_method: str, acr_headers: str) -> bytes:
        """Return a 204 No Content response that satisfies a CORS preflight."""
        allow_origin = origin or "*"
        allow_methods = (
            f"{acr_method}, GET, POST, PUT, DELETE, PATCH, OPTIONS"
            if acr_method else
            "GET, POST, PUT, DELETE, PATCH, OPTIONS"
        )
        allow_headers = acr_headers or "*"
        return (
            "HTTP/1.1 204 No Content\r\n"
            f"Access-Control-Allow-Origin: {allow_origin}\r\n"
            f"Access-Control-Allow-Methods: {allow_methods}\r\n"
            f"Access-Control-Allow-Headers: {allow_headers}\r\n"
            "Access-Control-Allow-Credentials: true\r\n"
            "Access-Control-Max-Age: 86400\r\n"
            "Vary: Origin\r\n"
            "Content-Length: 0\r\n"
            "\r\n"
        ).encode()

    @staticmethod
    def _inject_cors_headers(response: bytes, origin: str) -> bytes:
        """Inject CORS headers only if the upstream response lacks them.

        We must NOT overwrite the origin server's CORS headers: sites like
        x.com return carefully-scoped Access-Control-Allow-Headers that list
        specific custom headers (e.g. x-csrf-token). Replacing them with
        wildcards together with Allow-Credentials: true makes browsers
        reject the response (per the Fetch spec, "*" is literal when
        credentials are included), which the site then blames on privacy
        extensions. So we only fill in what the server omitted.
        """
        sep = b"\r\n\r\n"
        if sep not in response:
            return response
        header_section, body = response.split(sep, 1)
        lines = header_section.decode(errors="replace").split("\r\n")

        existing = {ln.split(":", 1)[0].strip().lower()
                    for ln in lines if ":" in ln}

        # If the upstream already handled CORS, leave it completely alone.
        if "access-control-allow-origin" in existing:
            return response

        # Otherwise inject a minimal, credential-safe set (no wildcards,
        # since wildcards combined with credentials are invalid).
        allow_origin = origin or "*"
        additions = [f"Access-Control-Allow-Origin: {allow_origin}"]
        if allow_origin != "*":
            additions.append("Access-Control-Allow-Credentials: true")
            additions.append("Vary: Origin")
        return ("\r\n".join(lines + additions) + "\r\n\r\n").encode() + body

    async def _relay_smart(self, method, url, headers, body):
        """Choose optimal relay strategy based on request type.

        - GET requests for likely-large downloads use parallel-range.
        - All other requests (API calls, HTML, JSON, XHR) go through the
          single-request relay. This avoids injecting a synthetic Range
          header on normal traffic, which some origins honor by returning
          206 — breaking fetch()/XHR on sites like x.com or Cloudflare
          challenge pages.
        """
        if method == "GET" and not body:
            # Respect client's own Range header verbatim.
            if headers:
                for k in headers:
                    if k.lower() == "range":
                        return await self.fronter.relay(
                            method, url, headers, body
                        )
            # Only probe with Range when the URL looks like a big file.
            if self._is_likely_download(url, headers):
                return await self.fronter.relay_parallel(
                    method, url, headers, body
                )
        return await self.fronter.relay(method, url, headers, body)

    def _is_likely_download(self, url: str, headers: dict) -> bool:
        """Heuristic: is this URL likely a large file download?"""
        # Check file extension
        path = url.split("?")[0].lower()
        large_exts = {
            ".zip", ".tar", ".gz", ".bz2", ".xz", ".7z", ".rar",
            ".exe", ".msi", ".dmg", ".deb", ".rpm", ".apk",
            ".iso", ".img",
            ".mp4", ".mkv", ".avi", ".mov", ".webm",
            ".mp3", ".flac", ".wav", ".aac",
            ".pdf", ".doc", ".docx", ".ppt", ".pptx",
            ".wasm",
        }
        for ext in large_exts:
            if path.endswith(ext):
                return True
        return False

    # ── Plain HTTP forwarding ─────────────────────────────────────

    async def _do_http(self, header_block: bytes, reader, writer):
        body = b""
        for raw_line in header_block.split(b"\r\n"):
            if raw_line.lower().startswith(b"content-length:"):
                length = int(raw_line.split(b":", 1)[1].strip())
                body = await reader.readexactly(length)
                break

        first_line = header_block.split(b"\r\n")[0].decode(errors="replace")
        log.info("HTTP → %s", first_line)

        if self.mode == "apps_script":
            # Parse request and relay through Apps Script
            parts = first_line.strip().split(" ", 2)
            method = parts[0] if parts else "GET"
            url = parts[1] if len(parts) > 1 else "/"

            headers = {}
            for raw_line in header_block.split(b"\r\n")[1:]:
                if b":" in raw_line:
                    k, v = raw_line.decode(errors="replace").split(":", 1)
                    headers[k.strip()] = v.strip()

            # ── CORS preflight over plain HTTP ────────────────────────────
            origin = next(
                (v for k, v in headers.items() if k.lower() == "origin"), ""
            )
            acr_method = next(
                (v for k, v in headers.items()
                 if k.lower() == "access-control-request-method"), ""
            )
            acr_headers_val = next(
                (v for k, v in headers.items()
                 if k.lower() == "access-control-request-headers"), ""
            )
            if method.upper() == "OPTIONS" and acr_method:
                log.debug("CORS preflight (HTTP) → %s (responding locally)", url[:60])
                writer.write(self._cors_preflight_response(origin, acr_method, acr_headers_val))
                await writer.drain()
                return

            response = await self._fetch_with_cache(method, url, headers, body)

            # Inject CORS headers for cross-origin requests
            if origin and response:
                response = self._inject_cors_headers(response, origin)
        elif self.mode in ("google_fronting", "custom_domain", "domain_fronting"):
            # Use WebSocket tunnel for ALL traffic (much faster than forward())
            response = await self._tunnel_http(header_block, body)
        else:
            response = await self.fronter.forward(header_block + body)

        writer.write(response)
        await writer.drain()

    async def _tunnel_http(self, header_block: bytes, body: bytes) -> bytes:
        """Forward plain HTTP via a persistent WebSocket tunnel.

        Instead of opening a new TLS+HTTP connection for each request
        (the old forward() path), this keeps a WebSocket tunnel open
        to the target host and pipes raw HTTP through it.
        Much faster for rapid-fire requests (e.g., Telegram API).
        """
        import re as _re

        # Parse target host:port from the raw HTTP request
        host = ""
        port = 80
        for line in header_block.split(b"\r\n")[1:]:
            if not line:
                break
            if line.lower().startswith(b"host:"):
                host_val = line.split(b":", 1)[1].strip().decode(errors="replace")
                if ":" in host_val:
                    h, p = host_val.rsplit(":", 1)
                    try:
                        host, port = h, int(p)
                    except ValueError:
                        host = host_val
                else:
                    host = host_val
                break

        if not host:
            return b"HTTP/1.1 400 Bad Request\r\n\r\nNo Host header\r\n"

        # Rewrite the request line: browser sends absolute URL
        # (e.g., "GET http://host/path HTTP/1.1") but the target
        # server expects a relative path ("GET /path HTTP/1.1")
        first_line = header_block.split(b"\r\n")[0]
        first_str = first_line.decode(errors="replace")
        parts = first_str.split(" ", 2)
        if len(parts) >= 2 and parts[1].startswith("http://"):
            from urllib.parse import urlparse
            parsed = urlparse(parts[1])
            rel_path = parsed.path or "/"
            if parsed.query:
                rel_path += "?" + parsed.query
            new_first = f"{parts[0]} {rel_path}"
            if len(parts) == 3:
                new_first += f" {parts[2]}"
            header_block = new_first.encode() + b"\r\n" + b"\r\n".join(header_block.split(b"\r\n")[1:])

        raw_request = header_block + body

        # Send through tunnel
        try:
            return await asyncio.wait_for(
                self.fronter.forward(raw_request), timeout=30
            )
        except Exception as e:
            log.error("Tunnel HTTP failed (%s:%d): %s", host, port, e)
            return b"HTTP/1.1 502 Bad Gateway\r\n\r\nTunnel forward failed\r\n"
