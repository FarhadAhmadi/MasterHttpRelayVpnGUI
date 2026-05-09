"""
Local HTTP proxy server.

Intercepts the user's browser traffic and forwards everything through
the Apps Script relay (MITM-decrypts HTTPS locally, forwards requests
as JSON to script.google.com fronted through www.google.com).
"""

import asyncio
import logging
import random
import re
import socket
import ssl
import time
import ipaddress
from collections import OrderedDict
from dataclasses import dataclass
from urllib.parse import urlparse

try:
    import certifi
except Exception:  # optional dependency fallback
    certifi = None

from constants import (
    CACHE_MAX_MB,
    CACHE_TTL_MAX,
    CACHE_TTL_STATIC_LONG,
    CACHE_TTL_STATIC_MED,
    CLIENT_IDLE_TIMEOUT,
    GOOGLE_DIRECT_ALLOW_EXACT,
    GOOGLE_DIRECT_ALLOW_SUFFIXES,
    GOOGLE_DIRECT_EXACT_EXCLUDE,
    GOOGLE_DIRECT_SUFFIX_EXCLUDE,
    GOOGLE_OWNED_EXACT,
    GOOGLE_OWNED_SUFFIXES,
    LARGE_FILE_EXTS,
    MAX_HEADER_BYTES,
    MAX_REQUEST_BODY_BYTES,
    SNI_REWRITE_SUFFIXES,
    STATIC_EXTS,
    TCP_CONNECT_TIMEOUT,
    TRACE_HOST_SUFFIXES,
    UNCACHEABLE_HEADER_NAMES,
)
from domain_fronter import DomainFronter

log = logging.getLogger("Proxy")
try:
    import stats as runtime_stats
except Exception:
    runtime_stats = None


def _is_ip_literal(host: str) -> bool:
    """True for IPv4/IPv6 literals (strips brackets around IPv6)."""
    h = host.strip("[]")
    try:
        ipaddress.ip_address(h)
        return True
    except ValueError:
        return False


def _parse_content_length(header_block: bytes) -> int:
    """Return Content-Length or 0. Matches only the exact header name."""
    for raw_line in header_block.split(b"\r\n"):
        name, sep, value = raw_line.partition(b":")
        if not sep:
            continue
        if name.strip().lower() == b"content-length":
            try:
                return int(value.strip())
            except ValueError:
                return 0
    return 0


def _has_unsupported_transfer_encoding(header_block: bytes) -> bool:
    """True when the request uses Transfer-Encoding, which we don't stream."""
    for raw_line in header_block.split(b"\r\n"):
        name, sep, value = raw_line.partition(b":")
        if not sep:
            continue
        if name.strip().lower() != b"transfer-encoding":
            continue
        encodings = [
            token.strip().lower()
            for token in value.decode(errors="replace").split(",")
            if token.strip()
        ]
        return any(token != "identity" for token in encodings)
    return False


class ResponseCache:
    """Simple LRU response cache — avoids repeated relay calls."""

    @dataclass
    class CacheEntry:
        raw: bytes
        expires_at: float
        stale_until: float
        etag: str
        last_modified: str
        revalidating_until: float = 0.0
        hits: int = 0

    def __init__(self, max_mb: int = 50):
        # key -> CacheEntry
        self._store: OrderedDict[str, ResponseCache.CacheEntry] = OrderedDict()
        self._size = 0
        self._max = max_mb * 1024 * 1024
        self.hits = 0
        self.misses = 0
        self.stale_hits = 0
        self._last_prune = 0.0
        self._revalidations = 0

    @staticmethod
    def _normalize_header_value(headers: dict | None, name: str) -> str:
        if not headers:
            return ""
        for k, v in headers.items():
            if k.lower() == name:
                return str(v).strip().lower()
        return ""

    @classmethod
    def build_key(cls, url: str, headers: dict | None) -> str:
        # Include variant-driving headers so cached objects don't get mixed
        # across encodings/content negotiation (e.g. br vs gzip, avif vs webp).
        accept = cls._normalize_header_value(headers, "accept")
        accept_encoding = cls._normalize_header_value(headers, "accept-encoding")
        accept_language = cls._normalize_header_value(headers, "accept-language")
        return f"{url}\nA:{accept}\nAE:{accept_encoding}\nAL:{accept_language}"

    def get(self, key: str) -> bytes | None:
        entry = self.get_entry(key)
        if not entry:
            self.misses += 1
            return None
        if time.time() > entry.expires_at:
            self.misses += 1
            return None
        # Promote as most-recently used.
        self._store.move_to_end(key)
        entry.hits += 1
        self.hits += 1
        return entry.raw

    def get_stale(self, key: str) -> bytes | None:
        entry = self.get_entry(key)
        if not entry:
            return None
        now = time.time()
        if entry.expires_at < now <= entry.stale_until:
            self._store.move_to_end(key)
            entry.hits += 1
            self.stale_hits += 1
            return entry.raw
        if now > entry.stale_until:
            self._size -= len(entry.raw)
            del self._store[key]
        return None

    def get_entry(self, key: str) -> CacheEntry | None:
        entry = self._store.get(key)
        if not entry:
            return None
        if time.time() > entry.stale_until:
            self._size -= len(entry.raw)
            del self._store[key]
            return None
        return entry

    def can_background_revalidate(self, key: str, cooldown_s: int = 10) -> bool:
        entry = self.get_entry(key)
        if not entry:
            return False
        now = time.time()
        if now <= entry.expires_at:
            return False
        if now > entry.stale_until:
            return False
        if now < entry.revalidating_until:
            return False
        entry.revalidating_until = now + max(1, cooldown_s)
        self._revalidations += 1
        return True

    def conditional_headers(self, key: str) -> dict[str, str]:
        entry = self.get_entry(key)
        if not entry:
            return {}
        hdrs: dict[str, str] = {}
        if entry.etag:
            hdrs["If-None-Match"] = entry.etag
        if entry.last_modified:
            hdrs["If-Modified-Since"] = entry.last_modified
        return hdrs

    def put(self, key: str, raw_response: bytes, ttl: int = 300, stale_if_error: int = 180):
        now = time.time()
        # Opportunistic sweep so expired entries do not occupy RAM forever
        # when their keys are not requested again.
        if now - self._last_prune > 30:
            self._prune_expired(now)
            self._last_prune = now
        size = len(raw_response)
        if size > self._max // 4 or size == 0:
            return
        # Evict oldest to make room
        while self._size + size > self._max and self._store:
            _, evicted_entry = self._store.popitem(last=False)
            self._size -= len(evicted_entry.raw)
        if key in self._store:
            old_entry = self._store[key]
            self._size -= len(old_entry.raw)
            del self._store[key]
        expires_at = now + ttl
        stale_until = expires_at + max(0, int(stale_if_error))
        self._store[key] = self.CacheEntry(
            raw=raw_response,
            expires_at=expires_at,
            stale_until=stale_until,
            etag=self._response_header(raw_response, "etag"),
            last_modified=self._response_header(raw_response, "last-modified"),
        )
        self._size += size

    def refresh_from_not_modified(self, key: str, ttl: int = 300, stale_if_error: int = 180):
        entry = self.get_entry(key)
        if not entry:
            return
        now = time.time()
        entry.expires_at = now + max(1, int(ttl))
        entry.stale_until = entry.expires_at + max(0, int(stale_if_error))
        entry.revalidating_until = 0.0
        self._store.move_to_end(key)

    def _prune_expired(self, now: float | None = None) -> None:
        ts = now if now is not None else time.time()
        expired = [k for k, e in self._store.items() if e.stale_until <= ts]
        for k in expired:
            entry = self._store.pop(k)
            self._size -= len(entry.raw)

    @property
    def entry_count(self) -> int:
        return len(self._store)

    @property
    def size_bytes(self) -> int:
        return self._size

    @property
    def max_bytes(self) -> int:
        return self._max

    @property
    def revalidations(self) -> int:
        return self._revalidations

    @staticmethod
    def _response_header(raw_response: bytes, name: str) -> str:
        hdr_end = raw_response.find(b"\r\n\r\n")
        if hdr_end < 0:
            return ""
        target = name.lower()
        for raw_line in raw_response[:hdr_end].split(b"\r\n")[1:]:
            key, sep, value = raw_line.partition(b":")
            if not sep:
                continue
            if key.decode(errors="replace").strip().lower() == target:
                return value.decode(errors="replace").strip()
        return ""

    @staticmethod
    def parse_ttl(raw_response: bytes, url: str) -> int:
        """Determine cache TTL from response headers and URL."""
        hdr_end = raw_response.find(b"\r\n\r\n")
        if hdr_end < 0:
            return 0
        status_line = raw_response.split(b"\r\n", 1)[0]
        if not status_line.startswith(b"HTTP/"):
            return 0
        try:
            status = int(status_line.split()[1])
        except Exception:
            return 0
        hdr = raw_response[:hdr_end].decode(errors="replace").lower()

        # Don't cache errors or non-200/206
        if status not in (200, 206):
            return 0
        if (
            "no-store" in hdr
            or "private" in hdr
            or "set-cookie:" in hdr
            or "vary: *" in hdr
        ):
            return 0
        if "cache-control:" in hdr and "no-cache" in hdr:
            return 0

        # Explicit max-age (prefer s-maxage when present).
        sm = re.search(r"s-maxage=(\d+)", hdr)
        if sm:
            return min(int(sm.group(1)), CACHE_TTL_MAX)
        m = re.search(r"max-age=(\d+)", hdr)
        if m:
            return min(int(m.group(1)), CACHE_TTL_MAX)

        # immutable usually means a fingerprinted static asset.
        if "cache-control:" in hdr and "immutable" in hdr:
            return CACHE_TTL_STATIC_LONG

        # Heuristic by content type / extension.
        # Be conservative for static assets with querystrings unless they look
        # versioned/fingerprinted (common for CDN assets).
        q = ""
        if "?" in url:
            q = url.split("?", 1)[1].lower()
        versioned_query = any(
            token in q for token in ("v=", "ver=", "version=", "hash=", "id=", "_=")
        )
        path = url.split("?")[0].lower()
        for ext in STATIC_EXTS:
            if path.endswith(ext):
                if q and not versioned_query:
                    return min(300, CACHE_TTL_STATIC_MED)
                return CACHE_TTL_STATIC_LONG

        ct_m = re.search(r"content-type:\s*([^\r\n]+)", hdr)
        ct = ct_m.group(1) if ct_m else ""
        if "image/" in ct or "font/" in ct:
            if q and not versioned_query:
                return min(300, CACHE_TTL_STATIC_MED)
            return CACHE_TTL_STATIC_LONG
        if "text/css" in ct or "javascript" in ct:
            return CACHE_TTL_STATIC_MED
        if "text/html" in ct or "application/json" in ct:
            return 0  # don't cache dynamic content by default

        return 0

    @staticmethod
    def parse_ttl_from_headers(raw_response: bytes, default_ttl: int = 300) -> int:
        hdr_end = raw_response.find(b"\r\n\r\n")
        if hdr_end < 0:
            return max(1, min(int(default_ttl), CACHE_TTL_MAX))
        hdr = raw_response[:hdr_end].decode(errors="replace").lower()
        if "cache-control:" in hdr:
            sm = re.search(r"s-maxage=(\d+)", hdr)
            if sm:
                return max(1, min(int(sm.group(1)), CACHE_TTL_MAX))
            m = re.search(r"max-age=(\d+)", hdr)
            if m:
                return max(1, min(int(m.group(1)), CACHE_TTL_MAX))
        return max(1, min(int(default_ttl), CACHE_TTL_MAX))


class ProxyServer:
    # Pulled from constants.py so users can override any subset via config.
    _GOOGLE_DIRECT_EXACT_EXCLUDE  = GOOGLE_DIRECT_EXACT_EXCLUDE
    _GOOGLE_DIRECT_SUFFIX_EXCLUDE = GOOGLE_DIRECT_SUFFIX_EXCLUDE
    _GOOGLE_DIRECT_ALLOW_EXACT    = GOOGLE_DIRECT_ALLOW_EXACT
    _GOOGLE_DIRECT_ALLOW_SUFFIXES = GOOGLE_DIRECT_ALLOW_SUFFIXES
    _TRACE_HOST_SUFFIXES          = TRACE_HOST_SUFFIXES
    _DOWNLOAD_DEFAULT_EXTS        = tuple(sorted(LARGE_FILE_EXTS))
    _DOWNLOAD_ACCEPT_MARKERS      = (
        "application/octet-stream",
        "application/zip",
        "application/x-bittorrent",
        "video/",
        "audio/",
    )
    # Domains that are very sensitive to stale/static variants or synthetic
    # range probing; keep their traffic on the safest path.
    _SENSITIVE_APP_SUFFIXES       = (
        "instagram.com",
        "cdninstagram.com",
        "fbcdn.net",
        "telegram.org",
        "t.me",
        "telegra.ph",
        "chatgpt.com",
        "openai.com",
        "claude.ai",
        "anthropic.com",
    )
    # Auth/bootstrap script hosts are fragile behind relay rewriting.
    # Prefer end-to-end direct TLS and avoid relay fallback on failure.
    _DIRECT_ONLY_EXACT_HOSTS      = frozenset({
        "accounts.google.com",
        "apis.google.com",
    })

    def __init__(self, config: dict):
        self.host = config.get("listen_host", "127.0.0.1")
        self.port = config.get("listen_port", 8080)
        self.socks_enabled = config.get("socks5_enabled", True)
        self.socks_host = config.get("socks5_host", self.host)
        self.socks_port = config.get("socks5_port", 1080)
        if self.socks_enabled and self.socks_host == self.host \
                and int(self.socks_port) == int(self.port):
            raise ValueError(
                f"listen_port and socks5_port must differ on the same host "
                f"(both set to {self.port} on {self.host}). "
                f"Change one of them in config.json."
            )
        self.fronter = DomainFronter(config)
        self.mitm = None
        self._cache = ResponseCache(max_mb=self._cfg_int(
            config, "cache_max_mb", CACHE_MAX_MB, minimum=16,
        ))
        self._cache_stale_if_error = self._cfg_int(
            config, "cache_stale_if_error_s", 180, minimum=0,
        )
        self._cache_stats_last_log = 0.0
        self._cache_stats_interval = 60.0
        self._cache_stats_last_hits = 0
        self._cache_stats_last_misses = 0
        self._cache_stats_last_stale_hits = 0
        self._cache_stats_last_revalidations = 0
        self._cache_inflight: dict[str, asyncio.Future] = {}
        self._cache_inflight_lock = asyncio.Lock()
        self._client_id_seq = 0
        self._cache_revalidate_timeout = self._cfg_float(
            config, "cache_revalidate_timeout_s", 8.0, minimum=1.0,
        )
        self._cache_revalidate_cooldown = self._cfg_int(
            config, "cache_revalidate_cooldown_s", 10, minimum=1,
        )
        self._direct_fail_until: dict[str, float] = {}
        self._relay_fail_until: dict[str, float] = {}
        self._relay_fail_streak: dict[str, int] = {}
        self._relay_rate_limit_streak: dict[str, int] = {}
        self._temp_direct_until: dict[str, float] = {}
        self._servers: list[asyncio.base_events.Server] = []
        self._client_tasks: set[asyncio.Task] = set()
        self._tcp_connect_timeout = self._cfg_float(
            config, "tcp_connect_timeout", TCP_CONNECT_TIMEOUT, minimum=1.0,
        )
        self._tcp_send_buffer = self._cfg_int(
            config, "tcp_send_buffer", 256 * 1024, minimum=16 * 1024,
        )
        self._tcp_recv_buffer = self._cfg_int(
            config, "tcp_recv_buffer", 256 * 1024, minimum=16 * 1024,
        )
        self._half_open_rx_timeout = self._cfg_float(
            config, "half_open_rx_timeout_s", 15.0, minimum=5.0,
        )
        self._half_open_probe_timeout = self._cfg_float(
            config, "half_open_probe_timeout_s", 2.0, minimum=0.5,
        )
        self._dc_failover_attempts = self._cfg_int(
            config, "dc_failover_attempts", 2, minimum=1,
        )
        self._telegram_force_direct = bool(config.get("telegram_force_direct", False))
        self._telegram_allow_relay_fallback = bool(
            config.get("telegram_allow_relay_fallback", True)
        )
        self._telegram_direct_fail_threshold = self._cfg_int(
            config, "telegram_direct_fail_threshold", 2, minimum=1,
        )
        self._telegram_direct_fail_cooldown = self._cfg_float(
            config, "telegram_direct_fail_cooldown_s", 180.0, minimum=10.0,
        )
        self._telegram_direct_fail_streak: dict[str, int] = {}
        self._download_min_size = self._cfg_int(
            config, "chunked_download_min_size", 5 * 1024 * 1024, minimum=0,
        )
        self._download_chunk_size = self._cfg_int(
            config, "chunked_download_chunk_size", 512 * 1024, minimum=64 * 1024,
        )
        self._download_max_parallel = self._cfg_int(
            config, "chunked_download_max_parallel", 8, minimum=1,
        )
        self._download_max_chunks = self._cfg_int(
            config, "chunked_download_max_chunks", 256, minimum=1,
        )
        self._download_extensions, self._download_any_extension = (
            self._normalize_download_extensions(
                config.get(
                    "chunked_download_extensions",
                    list(self._DOWNLOAD_DEFAULT_EXTS),
                )
            )
        )

        # hosts override — DNS fake-map: domain/suffix → IP
        # Checked before any real DNS lookup; supports exact and suffix matching.
        self._hosts: dict[str, str] = config.get("hosts", {})
        self._relay_cb_threshold = self._cfg_int(
            config, "relay_cb_threshold", 3, minimum=1,
        )
        self._relay_cb_cooldown = self._cfg_float(
            config, "relay_cb_cooldown", 20.0, minimum=1.0,
        )
        self._rate_limit_direct_threshold = self._cfg_int(
            config, "rate_limit_direct_threshold", 3, minimum=1,
        )
        self._rate_limit_direct_base_cooldown = self._cfg_float(
            config, "rate_limit_direct_base_cooldown", 45.0, minimum=5.0,
        )
        self._rate_limit_direct_max_cooldown = self._cfg_float(
            config, "rate_limit_direct_max_cooldown", 300.0, minimum=30.0,
        )
        configured_direct_exclude = config.get("direct_google_exclude", [])
        self._direct_google_exclude = {
            h.lower().rstrip(".")
            for h in (
                list(self._GOOGLE_DIRECT_EXACT_EXCLUDE) +
                list(configured_direct_exclude)
            )
        }
        configured_direct_allow = config.get("direct_google_allow", [])
        self._direct_google_allow = {
            h.lower().rstrip(".")
            for h in (
                list(self._GOOGLE_DIRECT_ALLOW_EXACT) +
                list(configured_direct_allow)
            )
        }

        # ── Per-host policy ────────────────────────────────────────
        # block_hosts  — refuse traffic entirely (close or 403)
        # bypass_hosts — route directly (no MITM, no relay)
        # no_mitm_hosts / no_mitm_cidrs — raw CONNECT tunnel only
        # (no TLS interception), useful for pinned-cert apps.
        # Both accept exact hostnames and leading-dot suffix patterns,
        # e.g. ".local" matches any *.local domain.
        self._block_hosts  = self._load_host_rules(config.get("block_hosts", []))
        self._bypass_hosts = self._load_host_rules(config.get("bypass_hosts", []))
        self._no_mitm_hosts = self._load_host_rules(config.get("no_mitm_hosts", []))
        self._no_mitm_cidrs = self._load_cidr_rules(config.get("no_mitm_cidrs", []))
        self._force_relay_hosts = self._load_host_rules(config.get("force_relay_hosts", []))
        self._filtered_network_mode = bool(config.get("filtered_network_mode", True))
        if self._filtered_network_mode:
            # Keep relay capacity focused on user traffic in heavily filtered
            # networks by avoiding browser/vendor background probe noise.
            self._merge_bypass_hosts([
                "detectportal.firefox.com",
                "incoming.telemetry.mozilla.org",
                "push.services.mozilla.com",
                "ads.mozilla.org",
                "ads-img.mozilla.org",
                "img-getpocket.cdn.mozilla.net",
                "prod-images.merino.prod.webservices.mozgcp.net",
                "firefox.settings.services.mozilla.com",
            ])
        self._telegram_relay_only_mode = bool(config.get("telegram_relay_only_mode", True))
        if self._telegram_relay_only_mode:
            self._merge_force_relay_hosts([
                ".web.telegram.org",
                "web.telegram.org",
                "t.me",
                ".t.me",
                "telegram.me",
                ".telegram.me",
            ])

        # Route YouTube through the relay when requested; the Google frontend
        # IP can enforce SafeSearch on the SNI-rewrite path.
        if config.get("youtube_via_relay", False):
            self._SNI_REWRITE_SUFFIXES = tuple(
                s for s in SNI_REWRITE_SUFFIXES
                if s not in self._YOUTUBE_SNI_SUFFIXES
            )
            log.info("youtube_via_relay enabled — YouTube routed through relay")
        else:
            self._SNI_REWRITE_SUFFIXES = SNI_REWRITE_SUFFIXES

        try:
            from mitm import MITMCertManager
            self.mitm = MITMCertManager()
        except ImportError:
            log.error("Apps Script relay requires the 'cryptography' package.")
            log.error("Run: pip install cryptography")
            raise SystemExit(1)

    # ── Host-policy helpers ───────────────────────────────────────

    @staticmethod
    def _cfg_int(config: dict, key: str, default: int, *, minimum: int = 1) -> int:
        try:
            value = int(config.get(key, default))
        except (TypeError, ValueError):
            value = default
        return max(minimum, value)

    @staticmethod
    def _cfg_float(config: dict, key: str, default: float,
                   *, minimum: float = 0.1) -> float:
        try:
            value = float(config.get(key, default))
        except (TypeError, ValueError):
            value = default
        return max(minimum, value)

    @classmethod
    def _normalize_download_extensions(cls, raw) -> tuple[tuple[str, ...], bool]:
        values = raw if isinstance(raw, (list, tuple)) else cls._DOWNLOAD_DEFAULT_EXTS
        normalized: list[str] = []
        any_extension = False
        seen: set[str] = set()
        for item in values:
            ext = str(item).strip().lower()
            if not ext:
                continue
            if ext in {"*", ".*"}:
                any_extension = True
                continue
            if not ext.startswith("."):
                ext = "." + ext
            if ext not in seen:
                seen.add(ext)
                normalized.append(ext)
        if not normalized and not any_extension:
            normalized = list(cls._DOWNLOAD_DEFAULT_EXTS)
        return tuple(normalized), any_extension

    def _track_current_task(self) -> asyncio.Task | None:
        task = asyncio.current_task()
        if task is not None:
            self._client_tasks.add(task)
        return task

    def _next_client_id(self) -> str:
        self._client_id_seq += 1
        return f"c{self._client_id_seq}"

    @staticmethod
    def _client_ip(addr) -> str:
        try:
            if isinstance(addr, tuple) and len(addr) > 0:
                return str(addr[0])
            return str(addr or "")
        except Exception:
            return ""

    @staticmethod
    def _platform_from_user_agent(user_agent: str) -> str:
        ua = (user_agent or "").lower()
        if "android" in ua:
            return "Android"
        if "iphone" in ua or "ipad" in ua or "ios" in ua:
            return "iOS"
        if "windows" in ua:
            return "Windows"
        if "mac os" in ua or "macintosh" in ua:
            return "macOS"
        if "linux" in ua:
            return "Linux"
        return "Unknown"

    def _untrack_task(self, task: asyncio.Task | None) -> None:
        if task is not None:
            self._client_tasks.discard(task)

    @staticmethod
    def _load_host_rules(raw) -> tuple[set[str], tuple[str, ...]]:
        """Accept a list of host strings; return (exact_set, suffix_tuple).

        A rule starting with '.' (e.g. ".internal") is a suffix rule.
        Everything else is treated as an exact match. Case-insensitive.
        """
        exact: set[str] = set()
        suffixes: list[str] = []
        for item in raw or []:
            h = str(item).strip().lower().rstrip(".")
            if not h:
                continue
            if h.startswith("."):
                suffixes.append(h)
            else:
                exact.add(h)
        return exact, tuple(suffixes)

    @staticmethod
    def _host_matches_rules(host: str,
                            rules: tuple[set[str], tuple[str, ...]]) -> bool:
        exact, suffixes = rules
        h = host.lower().rstrip(".")
        if h in exact:
            return True
        for s in suffixes:
            if h.endswith(s):
                return True
        return False

    def _is_blocked(self, host: str) -> bool:
        return self._host_matches_rules(host, self._block_hosts)

    def _is_bypassed(self, host: str) -> bool:
        return self._host_matches_rules(host, self._bypass_hosts)

    def _is_force_relay_host(self, host: str) -> bool:
        return self._host_matches_rules(host, self._force_relay_hosts)

    def _merge_bypass_hosts(self, hosts: list[str]) -> None:
        exact, suffixes = self._bypass_hosts
        merged_exact = set(exact)
        merged_suffix = list(suffixes)
        seen_suffix = set(suffixes)
        for item in hosts:
            h = str(item).strip().lower().rstrip(".")
            if not h:
                continue
            if h.startswith("."):
                if h not in seen_suffix:
                    seen_suffix.add(h)
                    merged_suffix.append(h)
            else:
                merged_exact.add(h)
        self._bypass_hosts = (merged_exact, tuple(merged_suffix))

    def _merge_force_relay_hosts(self, hosts: list[str]) -> None:
        exact, suffixes = self._force_relay_hosts
        merged_exact = set(exact)
        merged_suffix = list(suffixes)
        seen_suffix = set(suffixes)
        for item in hosts:
            h = str(item).strip().lower().rstrip(".")
            if not h:
                continue
            if h.startswith("."):
                if h not in seen_suffix:
                    seen_suffix.add(h)
                    merged_suffix.append(h)
            else:
                merged_exact.add(h)
        self._force_relay_hosts = (merged_exact, tuple(merged_suffix))

    def _is_temp_direct_host(self, host: str) -> bool:
        if self._is_telegram_web_host(host):
            return False
        h = host.lower().rstrip(".")
        until = self._temp_direct_until.get(h, 0.0)
        now = time.time()
        if until > now:
            return True
        if until:
            self._temp_direct_until.pop(h, None)
        return False

    def _record_rate_limit(self, host: str) -> None:
        h = host.lower().rstrip(".")
        if not h:
            return
        streak = self._relay_rate_limit_streak.get(h, 0) + 1
        self._relay_rate_limit_streak[h] = streak
        if streak < self._rate_limit_direct_threshold:
            return
        step = streak - self._rate_limit_direct_threshold
        ttl = min(
            self._rate_limit_direct_base_cooldown * (2 ** step),
            self._rate_limit_direct_max_cooldown,
        )
        self._temp_direct_until[h] = time.time() + ttl
        log.warning(
            "Adaptive direct fallback for %s: %.0fs (rate-limit streak=%d)",
            h, ttl, streak,
        )

    def _record_relay_host_success(self, host: str) -> None:
        h = host.lower().rstrip(".")
        if not h:
            return
        self._relay_rate_limit_streak.pop(h, None)
        self._telegram_direct_fail_streak.pop(h, None)

    def _record_telegram_direct_failure(self, host: str) -> None:
        h = host.lower().rstrip(".")
        if not h:
            return
        streak = self._telegram_direct_fail_streak.get(h, 0) + 1
        self._telegram_direct_fail_streak[h] = streak
        if streak < self._telegram_direct_fail_threshold:
            return
        self._remember_direct_failure(h, ttl=int(self._telegram_direct_fail_cooldown))
        log.warning(
            "Telegram direct disabled for %.0fs after %d failures → %s",
            self._telegram_direct_fail_cooldown, streak, h,
        )

    def _is_no_mitm_host(self, host: str) -> bool:
        return self._host_matches_rules(host, self._no_mitm_hosts)

    @staticmethod
    def _load_cidr_rules(raw) -> tuple:
        nets: list = []
        for item in raw or []:
            cidr = str(item).strip()
            if not cidr:
                continue
            try:
                nets.append(ipaddress.ip_network(cidr, strict=False))
            except ValueError:
                log.warning("Ignoring invalid CIDR rule: %r", item)
        return tuple(nets)

    def _ip_matches_no_mitm_cidr(self, host: str) -> bool:
        if not self._no_mitm_cidrs or not _is_ip_literal(host):
            return False
        try:
            ip_obj = ipaddress.ip_address(host.strip("[]"))
        except ValueError:
            return False
        for net in self._no_mitm_cidrs:
            if ip_obj.version == net.version and ip_obj in net:
                return True
        return False

    @staticmethod
    def _is_telegram_host(host: str) -> bool:
        h = host.lower().rstrip(".")
        return (
            h == "telegram.org"
            or h.endswith(".telegram.org")
            or h == "t.me"
            or h.endswith(".t.me")
            or h == "telegram.me"
            or h.endswith(".telegram.me")
            or h.endswith(".telegram-cdn.org")
            or h.endswith(".telesco.pe")
            or h.endswith(".tdesktop.com")
        )

    @staticmethod
    def _is_telegram_web_host(host: str) -> bool:
        h = host.lower().rstrip(".")
        return h == "web.telegram.org" or h.endswith(".web.telegram.org")

    def _is_telegram_target(self, host: str) -> bool:
        return self._is_telegram_host(host) or self._ip_matches_no_mitm_cidr(host)

    def _pick_alternate_dc_ips(self, ip_text: str, max_count: int) -> list[str]:
        if max_count <= 0:
            return []
        try:
            ip_obj = ipaddress.ip_address(ip_text.strip("[]"))
        except ValueError:
            return []
        pools = [n for n in self._no_mitm_cidrs if n.version == ip_obj.version]
        if not pools:
            return []
        choices: list[str] = []
        seen = {str(ip_obj)}
        attempts = 0
        while len(choices) < max_count and attempts < (max_count * 12):
            attempts += 1
            net = random.choice(pools)
            if ip_obj in net and net.num_addresses > 4:
                # Prefer a different network when possible.
                continue
            if ip_obj.version == 4 and net.num_addresses > 2:
                off = random.randrange(1, int(net.num_addresses) - 1)
            else:
                off = random.randrange(0, int(net.num_addresses))
            cand = str(net.network_address + off)
            if cand in seen:
                continue
            seen.add(cand)
            choices.append(cand)
        return choices

    def _relay_temporarily_disabled(self, host: str) -> bool:
        h = host.lower().rstrip(".")
        until = self._relay_fail_until.get(h, 0.0)
        now = time.time()
        if until > now:
            return True
        if until:
            self._relay_fail_until.pop(h, None)
        return False

    def _record_relay_result(self, host: str, success: bool) -> None:
        h = host.lower().rstrip(".")
        if not h:
            return
        if success:
            self._relay_fail_streak.pop(h, None)
            self._relay_fail_until.pop(h, None)
            self._record_relay_host_success(h)
            return
        streak = self._relay_fail_streak.get(h, 0) + 1
        self._relay_fail_streak[h] = streak
        if streak >= self._relay_cb_threshold:
            self._relay_fail_until[h] = time.time() + self._relay_cb_cooldown
            self._relay_fail_streak[h] = 0
            log.warning(
                "Relay circuit open for %s: %.0fs cooldown after %d failures",
                h, self._relay_cb_cooldown, self._relay_cb_threshold,
            )

    @staticmethod
    def _header_value(headers: dict | None, name: str) -> str:
        if not headers:
            return ""
        for key, value in headers.items():
            if key.lower() == name:
                return str(value)
        return ""

    def _cache_allowed(self, method: str, url: str,
                       headers: dict | None, body: bytes) -> bool:
        if method.upper() != "GET" or body:
            return False
        if self._is_sensitive_app_url(url):
            return False
        req_cc = self._header_value(headers, "cache-control").lower()
        req_pragma = self._header_value(headers, "pragma").lower()
        if "no-cache" in req_cc or "no-store" in req_cc or "no-cache" in req_pragma:
            return False
        for name in UNCACHEABLE_HEADER_NAMES:
            if self._header_value(headers, name):
                return False
        return self.fronter._is_static_asset_url(url)

    @classmethod
    def _should_trace_host(cls, host: str) -> bool:
        h = host.lower().rstrip(".")
        return any(
            token == h or token in h or h.endswith("." + token)
            for token in cls._TRACE_HOST_SUFFIXES
        )

    def _log_response_summary(self, url: str, response: bytes):
        status, headers, body = self.fronter._split_raw_response(response)
        host = (urlparse(url).hostname or "").lower()

        if status >= 300 or self._should_trace_host(host):
            location = headers.get("location", "") or "-"
            server = headers.get("server", "") or "-"
            cf_ray = headers.get("cf-ray", "") or "-"
            content_type = headers.get("content-type", "") or "-"
            body_len = len(body)

            body_hint = "-"
            rate_limited = False

            # Handle text-like responses (HTML, plain text, JSON…)
            if ("text" in content_type.lower() or "json" in content_type.lower()) and body:
                sample = body[:1200].decode(errors="replace").lower()

                # --- Structured HTML title extraction ---
                if "<title>" in sample and "</title>" in sample:
                    title = sample.split("<title>", 1)[1].split("</title>", 1)[0]
                    body_hint = title.strip()[:120] or "-"

                # --- Known content patterns ---
                elif "captcha" in sample:
                    body_hint = "captcha"
                elif "turnstile" in sample:
                    body_hint = "turnstile"
                elif "loading" in sample:
                    body_hint = "loading"

                # --- Rate-limit / quota markers ---
                rate_limit_markers = (
                    "too many",
                    "rate limit",
                    "quota",
                    "quota exceeded",
                    "request limit",
                    "دفعات زیاد",
                    "بیش از حد",
                    "سرویس در طول یک روز",
                )

                if any(m in sample for m in rate_limit_markers):
                    rate_limited = True
                    body_hint = "quota_exceeded"

            log_msg = (
                "RESP ← %s status=%s type=%s len=%s server=%s location=%s cf-ray=%s hint=%s"
            )
            log_args = (
                host or url[:60],
                status,
                content_type,
                body_len,
                server,
                location,
                cf_ray,
                body_hint,
            )

            if rate_limited:
                log.warning("RATE LIMIT detected! " + log_msg, *log_args)
            else:
                log.info(log_msg, *log_args)

    def _maybe_log_cache_stats(self) -> None:
        now = time.time()
        if (now - self._cache_stats_last_log) < self._cache_stats_interval:
            return
        self._cache_stats_last_log = now

        hits = self._cache.hits
        misses = self._cache.misses
        stale_hits = self._cache.stale_hits
        total = hits + misses
        delta_hits = hits - self._cache_stats_last_hits
        delta_misses = misses - self._cache_stats_last_misses
        delta_stale_hits = stale_hits - self._cache_stats_last_stale_hits
        revalidations = self._cache.revalidations
        delta_revalidations = revalidations - self._cache_stats_last_revalidations
        delta_total = max(0, delta_hits + delta_misses)

        self._cache_stats_last_hits = hits
        self._cache_stats_last_misses = misses
        self._cache_stats_last_stale_hits = stale_hits
        self._cache_stats_last_revalidations = revalidations

        hit_ratio = (hits / total * 100.0) if total else 0.0
        window_hit_ratio = (delta_hits / delta_total * 100.0) if delta_total else 0.0
        window_effective_hit_ratio = (
            ((delta_hits + delta_stale_hits) / delta_total * 100.0)
            if delta_total else 0.0
        )
        fill_ratio = (
            self._cache.size_bytes / self._cache.max_bytes * 100.0
            if self._cache.max_bytes else 0.0
        )

        suggestion = "keep"
        if fill_ratio > 90 and window_hit_ratio > 35:
            suggestion = "increase_cache_mb"
        elif fill_ratio < 35 and window_hit_ratio < 10:
            suggestion = "decrease_cache_mb"

        log.info(
            "CACHE stats: hit=%.1f%% window=%.1f%% effective=%.1f%% reval=%d entries=%d size=%.1fMB/%.1fMB (%.0f%%) suggestion=%s",
            hit_ratio,
            window_hit_ratio,
            window_effective_hit_ratio,
            max(0, delta_revalidations),
            self._cache.entry_count,
            self._cache.size_bytes / (1024 * 1024),
            self._cache.max_bytes / (1024 * 1024),
            fill_ratio,
            suggestion,
        )
        if runtime_stats:
            try:
                runtime_stats.cache_snapshot(self._cache.entry_count, self._cache.size_bytes)
            except Exception:
                pass

    @staticmethod
    def _response_status_code(response: bytes | None) -> int:
        if not response:
            return 0
        line = response.split(b"\r\n", 1)[0]
        m = re.match(rb"HTTP/\d(?:\.\d)?\s+(\d{3})", line)
        if not m:
            return 0
        try:
            return int(m.group(1))
        except Exception:
            return 0

    async def _fetch_with_cache(self, host: str, method: str, url: str,
                                headers: dict | None, body: bytes,
                                cacheable: bool, cache_key: str) -> bytes:
        if not cacheable:
            return await self._fetch_uncached(host, method, url, headers, body)

        response = self._cache.get(cache_key)
        if response is not None:
            if runtime_stats:
                try:
                    runtime_stats.cache_hit()
                except Exception:
                    pass
            return response

        stale = self._cache.get_stale(cache_key)
        if stale is not None:
            if runtime_stats:
                try:
                    runtime_stats.cache_stale_hit()
                except Exception:
                    pass
            await self._maybe_revalidate_in_background(
                host, method, url, headers, body, cache_key
            )
            return stale

        if runtime_stats:
            try:
                runtime_stats.cache_miss()
            except Exception:
                pass

        inflight = None
        owner = False
        async with self._cache_inflight_lock:
            inflight = self._cache_inflight.get(cache_key)
            if inflight is None:
                inflight = asyncio.get_running_loop().create_future()
                self._cache_inflight[cache_key] = inflight
                owner = True

        if not owner:
            try:
                coalesced = await inflight
                if coalesced:
                    return coalesced
            except Exception:
                pass
            response = self._cache.get(cache_key)
            if response is not None:
                return response
            return await self._fetch_uncached(host, method, url, headers, body)

        try:
            response = await self._fetch_uncached(host, method, url, headers, body)
            status_code = self._response_status_code(response)
            if status_code == 304:
                ttl_304 = ResponseCache.parse_ttl_from_headers(response, default_ttl=300)
                self._cache.refresh_from_not_modified(
                    cache_key,
                    ttl=ttl_304,
                    stale_if_error=self._cache_stale_if_error,
                )
                refreshed = self._cache.get(cache_key) or self._cache.get_stale(cache_key)
                if refreshed:
                    if not inflight.done():
                        inflight.set_result(refreshed)
                    return refreshed
            if 500 <= status_code <= 599:
                stale = self._cache.get_stale(cache_key)
                if stale is not None:
                    if runtime_stats:
                        try:
                            runtime_stats.cache_stale_hit()
                        except Exception:
                            pass
                    if not inflight.done():
                        inflight.set_result(stale)
                    return stale
            ttl = ResponseCache.parse_ttl(response, url)
            if ttl > 0:
                self._cache.put(
                    cache_key,
                    response,
                    ttl=ttl,
                    stale_if_error=self._cache_stale_if_error,
                )
                log.debug("Cached (%ds): %s", ttl, url[:60])
            if not inflight.done():
                inflight.set_result(response)
            return response
        except Exception as exc:
            stale = self._cache.get_stale(cache_key)
            if stale is not None:
                if runtime_stats:
                    try:
                        runtime_stats.cache_stale_hit()
                    except Exception:
                        pass
                if not inflight.done():
                    inflight.set_result(stale)
                return stale
            if not inflight.done():
                inflight.set_exception(exc)
            raise
        finally:
            async with self._cache_inflight_lock:
                if self._cache_inflight.get(cache_key) is inflight:
                    self._cache_inflight.pop(cache_key, None)

    async def _fetch_uncached(self, host: str, method: str, url: str,
                              headers: dict | None, body: bytes) -> bytes:
        if self._is_temp_direct_host(host):
            err_body = b"Relay temporarily rate-limited for this host; retry shortly."
            return (
                b"HTTP/1.1 429 Too Many Requests\r\n"
                b"Content-Type: text/plain\r\n"
                b"Retry-After: 30\r\n"
                b"Content-Length: " + str(len(err_body)).encode() + b"\r\n"
                b"\r\n" + err_body
            )
        if self._relay_temporarily_disabled(host):
            err_body = b"Relay temporarily unavailable for this host; retry shortly."
            log.warning("Relay circuit-open fast-fail: %s", host)
            return (
                b"HTTP/1.1 503 Service Unavailable\r\n"
                b"Content-Type: text/plain\r\n"
                b"Retry-After: 20\r\n"
                b"Content-Length: " + str(len(err_body)).encode() + b"\r\n"
                b"\r\n" + err_body
            )
        try:
            response = await self._relay_smart(method, url, headers, body)
            self._record_relay_result(host, success=True)
            return response
        except Exception as e:
            msg = str(e)
            if "RATE_LIMIT" in msg.upper() or "RATE LIMIT" in msg.upper():
                self._record_rate_limit(host)
                self._record_relay_result(host, success=False)
                err_body = b"Relay rate limit reached; temporary adaptive fallback enabled."
                return (
                    b"HTTP/1.1 429 Too Many Requests\r\n"
                    b"Content-Type: text/plain\r\n"
                    b"Retry-After: 30\r\n"
                    b"Content-Length: " + str(len(err_body)).encode() + b"\r\n"
                    b"\r\n" + err_body
                )
            self._record_relay_result(host, success=False)
            log.error("Relay error (%s): %s", url[:60], e)
            err_body = f"Relay error: {e}".encode()
            return (
                b"HTTP/1.1 502 Bad Gateway\r\n"
                b"Content-Type: text/plain\r\n"
                b"Content-Length: " + str(len(err_body)).encode() + b"\r\n"
                b"\r\n" + err_body
            )

    async def _maybe_revalidate_in_background(self, host: str, method: str, url: str,
                                              headers: dict | None, body: bytes,
                                              cache_key: str) -> None:
        if method.upper() != "GET" or body:
            return
        if not self._cache.can_background_revalidate(
            cache_key, cooldown_s=self._cache_revalidate_cooldown
        ):
            return
        try:
            asyncio.create_task(self._background_revalidate(
                host, method, url, headers, body, cache_key
            ))
        except Exception:
            pass

    async def _background_revalidate(self, host: str, method: str, url: str,
                                     headers: dict | None, body: bytes,
                                     cache_key: str) -> None:
        req_headers = dict(headers or {})
        req_headers.update(self._cache.conditional_headers(cache_key))
        try:
            response = await asyncio.wait_for(
                self._fetch_uncached(host, method, url, req_headers, body),
                timeout=self._cache_revalidate_timeout,
            )
        except Exception:
            return
        status = self._response_status_code(response)
        if status == 304:
            ttl_304 = ResponseCache.parse_ttl_from_headers(response, default_ttl=300)
            self._cache.refresh_from_not_modified(
                cache_key,
                ttl=ttl_304,
                stale_if_error=self._cache_stale_if_error,
            )
            return
        ttl = ResponseCache.parse_ttl(response, url)
        if ttl > 0:
            self._cache.put(
                cache_key,
                response,
                ttl=ttl,
                stale_if_error=self._cache_stale_if_error,
            )

    @classmethod
    def _is_sensitive_app_host(cls, host: str) -> bool:
        h = host.lower().rstrip(".")
        return any(h == s or h.endswith("." + s) for s in cls._SENSITIVE_APP_SUFFIXES)

    @classmethod
    def _is_sensitive_app_url(cls, url: str) -> bool:
        host = (urlparse(url).hostname or "").lower()
        return bool(host) and cls._is_sensitive_app_host(host)

    async def start(self):
        http_srv = await asyncio.start_server(self._on_client, self.host, self.port)
        socks_srv = None

        if self.socks_enabled:
            try:
                socks_srv = await asyncio.start_server(
                    self._on_socks_client, self.socks_host, self.socks_port
                )
            except OSError as e:
                log.error("SOCKS5 listener failed on %s:%d: %s",
                          self.socks_host, self.socks_port, e)

        self._servers = [s for s in (http_srv, socks_srv) if s]

        log.info(
            "HTTP proxy listening on %s:%d",
            self.host, self.port,
        )
        if socks_srv:
            log.info(
                "SOCKS5 proxy listening on %s:%d",
                self.socks_host, self.socks_port,
            )

        try:
            async with http_srv:
                if socks_srv:
                    async with socks_srv:
                        await asyncio.gather(
                            http_srv.serve_forever(),
                            socks_srv.serve_forever(),
                        )
                else:
                    await http_srv.serve_forever()
        except asyncio.CancelledError:
            raise

    async def stop(self):
        """Shut down all listeners and release relay resources."""
        for srv in self._servers:
            try:
                srv.close()
            except Exception:
                pass
        for srv in self._servers:
            try:
                await srv.wait_closed()
            except Exception:
                pass
        self._servers = []

        current = asyncio.current_task()
        client_tasks = [task for task in self._client_tasks if task is not current]
        for task in client_tasks:
            task.cancel()
        if client_tasks:
            await asyncio.gather(*client_tasks, return_exceptions=True)
        self._client_tasks.clear()

        try:
            await self.fronter.close()
        except Exception as exc:
            log.debug("fronter.close: %s", exc)

    # ── client handler ────────────────────────────────────────────

    async def _on_client(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        addr = writer.get_extra_info("peername")
        client_id = self._next_client_id()
        client_ip = self._client_ip(addr)
        client_ua = ""
        client_platform = "Unknown"
        client_start = time.time()
        client_req_count = 0
        client_err_count = 0
        task = self._track_current_task()
        if runtime_stats:
            try:
                runtime_stats.client_connected(client_id, client_ip, transport="http")
            except Exception:
                pass
        try:
            first_line = await asyncio.wait_for(reader.readline(), timeout=30)
            if not first_line:
                return

            # Read remaining headers
            header_block = first_line
            while True:
                line = await asyncio.wait_for(reader.readline(), timeout=10)
                header_block += line
                if len(header_block) > MAX_HEADER_BYTES:
                    log.warning("Request header block exceeds cap — closing")
                    return
                if line in (b"\r\n", b"\n", b""):
                    break

            if _has_unsupported_transfer_encoding(header_block):
                log.warning("Unsupported Transfer-Encoding on client request")
                writer.write(
                    b"HTTP/1.1 501 Not Implemented\r\n"
                    b"Connection: close\r\n"
                    b"Content-Length: 0\r\n\r\n"
                )
                await writer.drain()
                return

            request_line = first_line.decode(errors="replace").strip()
            parts = request_line.split(" ", 2)
            if len(parts) < 2:
                return

            method = parts[0].upper()
            header_lines = header_block.split(b"\r\n")[1:]
            for raw_line in header_lines:
                if b":" not in raw_line:
                    continue
                k, v = raw_line.decode(errors="replace").split(":", 1)
                if k.strip().lower() == "user-agent":
                    client_ua = v.strip()
                    client_platform = self._platform_from_user_agent(client_ua)
                    if runtime_stats:
                        try:
                            runtime_stats.client_connected(
                                client_id,
                                client_ip,
                                transport="http",
                                platform_hint=client_platform,
                                user_agent=client_ua,
                            )
                        except Exception:
                            pass
                    break

            if method == "CONNECT":
                client_req_count += 1
                await self._do_connect(parts[1], reader, writer)
            else:
                client_req_count += 1
                await self._do_http(header_block, reader, writer)

        except asyncio.CancelledError:
            pass
        except asyncio.TimeoutError:
            client_err_count += 1
            if runtime_stats:
                try:
                    runtime_stats.client_error(client_id, "timeout")
                except Exception:
                    pass
            log.debug("Timeout: %s", addr)
        except Exception as e:
            client_err_count += 1
            if runtime_stats:
                try:
                    runtime_stats.client_error(client_id, str(e))
                except Exception:
                    pass
            log.error("Error (%s): %s", addr, e)
        finally:
            if runtime_stats:
                try:
                    duration_ms = max((time.time() - client_start) * 1000.0, 0.0)
                    runtime_stats.client_activity(
                        client_id,
                        req_inc=max(client_req_count, 1),
                        latency_ms=duration_ms / max(client_req_count, 1),
                    )
                    runtime_stats.client_disconnected(client_id)
                except Exception:
                    pass
            self._untrack_task(task)
            try:
                writer.close()
                await writer.wait_closed()
            except Exception:
                pass

    async def _on_socks_client(self, reader: asyncio.StreamReader,
                               writer: asyncio.StreamWriter):
        addr = writer.get_extra_info("peername")
        client_id = self._next_client_id()
        client_ip = self._client_ip(addr)
        client_start = time.time()
        client_target = ""
        client_err_count = 0
        task = self._track_current_task()
        if runtime_stats:
            try:
                runtime_stats.client_connected(client_id, client_ip, transport="socks5")
            except Exception:
                pass
        try:
            header = await asyncio.wait_for(reader.readexactly(2), timeout=15)
            ver, nmethods = header[0], header[1]
            if ver != 5:
                return

            methods = await asyncio.wait_for(reader.readexactly(nmethods), timeout=10)
            if 0x00 not in methods:
                writer.write(b"\x05\xff")
                await writer.drain()
                return

            writer.write(b"\x05\x00")
            await writer.drain()

            req = await asyncio.wait_for(reader.readexactly(4), timeout=15)
            ver, cmd, _rsv, atyp = req
            if ver != 5 or cmd != 0x01:
                writer.write(b"\x05\x07\x00\x01\x00\x00\x00\x00\x00\x00")
                await writer.drain()
                return

            if atyp == 0x01:
                raw = await asyncio.wait_for(reader.readexactly(4), timeout=10)
                host = socket.inet_ntoa(raw)
            elif atyp == 0x03:
                ln = (await asyncio.wait_for(reader.readexactly(1), timeout=10))[0]
                host = (await asyncio.wait_for(reader.readexactly(ln), timeout=10)).decode(
                    errors="replace"
                )
            elif atyp == 0x04:
                raw = await asyncio.wait_for(reader.readexactly(16), timeout=10)
                host = socket.inet_ntop(socket.AF_INET6, raw)
            else:
                writer.write(b"\x05\x08\x00\x01\x00\x00\x00\x00\x00\x00")
                await writer.drain()
                return

            port_raw = await asyncio.wait_for(reader.readexactly(2), timeout=10)
            port = int.from_bytes(port_raw, "big")
            client_target = f"{host}:{port}"
            if runtime_stats:
                try:
                    runtime_stats.client_set_target(client_id, client_target)
                except Exception:
                    pass

            log.info("SOCKS5 CONNECT → %s:%d", host, port)

            writer.write(b"\x05\x00\x00\x01\x00\x00\x00\x00\x00\x00")
            await writer.drain()
            await self._handle_target_tunnel(host, port, reader, writer)

        except asyncio.IncompleteReadError:
            pass
        except asyncio.CancelledError:
            pass
        except asyncio.TimeoutError:
            client_err_count += 1
            if runtime_stats:
                try:
                    runtime_stats.client_error(client_id, "socks_timeout")
                except Exception:
                    pass
            log.debug("SOCKS5 timeout: %s", addr)
        except Exception as e:
            client_err_count += 1
            if runtime_stats:
                try:
                    runtime_stats.client_error(client_id, str(e))
                except Exception:
                    pass
            log.error("SOCKS5 error (%s): %s", addr, e)
        finally:
            if runtime_stats:
                try:
                    duration_ms = max((time.time() - client_start) * 1000.0, 0.0)
                    runtime_stats.client_activity(
                        client_id,
                        req_inc=1,
                        latency_ms=duration_ms,
                    )
                    runtime_stats.client_disconnected(client_id)
                except Exception:
                    pass
            self._untrack_task(task)
            try:
                writer.close()
                await writer.wait_closed()
            except Exception:
                pass

    # ── CONNECT (HTTPS tunnelling) ────────────────────────────────

    async def _do_connect(self, target: str, reader, writer):
        host, _, port_str = target.rpartition(":")
        try:
            port = int(port_str) if port_str else 443
        except ValueError:
            log.warning("CONNECT invalid target: %r", target)
            writer.write(b"HTTP/1.1 400 Bad Request\r\n\r\n")
            await writer.drain()
            return
        if not host:
            host, port = target, 443

        log.info("CONNECT → %s:%d", host, port)

        writer.write(b"HTTP/1.1 200 Connection Established\r\n\r\n")
        await writer.drain()

        await self._handle_target_tunnel(host, port, reader, writer)

    async def _handle_target_tunnel(self, host: str, port: int,
                                    reader: asyncio.StreamReader,
                                    writer: asyncio.StreamWriter):
        """Route a target connection through the Apps Script relay."""
        is_telegram_web = self._is_telegram_web_host(host)
        # ── Block / bypass policy ─────────────────────────────────
        if self._is_blocked(host):
            log.warning("BLOCKED → %s:%d (matches block_hosts)", host, port)
            try:
                writer.write(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n")
                await writer.drain()
            except Exception:
                pass
            return

        if self._is_bypassed(host):
            log.info("Bypass tunnel → %s:%d (matches bypass_hosts)", host, port)
            await self._do_direct_tunnel(host, port, reader, writer)
            return
        if self._is_force_relay_host(host):
            log.info("Force-relay route → %s:%d (matches force_relay_hosts)", host, port)
            if port == 443:
                await self._do_mitm_connect(host, port, reader, writer)
            elif port == 80:
                await self._do_plain_http_tunnel(host, port, reader, writer)
            else:
                log.warning("Force-relay host on non-HTTP port (cannot relay): %s:%d", host, port)
            return
        if self._is_temp_direct_host(host):
            log.info("Adaptive direct fallback tunnel → %s:%d (rate-limit cooldown)", host, port)
            await self._do_direct_tunnel(host, port, reader, writer)
            return
        if self._is_no_mitm_host(host):
            if is_telegram_web and port in (80, 443):
                log.info(
                    "Telegram Web host matched no-MITM, but allowing relay path → %s:%d",
                    host, port,
                )
            else:
                log.info("No-MITM tunnel → %s:%d (matches no_mitm_hosts)", host, port)
                if self._is_telegram_target(host):
                    log.info("Telegram no-MITM route selected → %s:%d (host rule)", host, port)
                await self._do_direct_tunnel(host, port, reader, writer)
                return
        if self._ip_matches_no_mitm_cidr(host):
            if is_telegram_web and port in (80, 443):
                log.info(
                    "Telegram Web IP matched no-MITM CIDR, but allowing relay path → %s:%d",
                    host, port,
                )
            else:
                log.info("No-MITM tunnel → %s:%d (matches no_mitm_cidrs)", host, port)
                log.info("Telegram no-MITM route selected → %s:%d (CIDR rule)", host, port)
                await self._do_direct_tunnel(host, port, reader, writer)
                return

        if self._telegram_force_direct and self._is_telegram_host(host) and not is_telegram_web:
            log.info("Telegram direct tunnel policy → %s:%d", host, port)
            if self._direct_temporarily_disabled(host):
                log.info("Telegram relay fallback → %s:%d (direct temporarily disabled)", host, port)
                if port == 443:
                    await self._do_mitm_connect(host, port, reader, writer)
                elif port == 80:
                    await self._do_plain_http_tunnel(host, port, reader, writer)
                return
            ok = await self._do_direct_tunnel(host, port, reader, writer, timeout=4.0)
            if ok:
                self._telegram_direct_fail_streak.pop(host.lower().rstrip("."), None)
                return
            self._record_telegram_direct_failure(host)
            if not self._telegram_allow_relay_fallback:
                log.warning("Telegram direct tunnel failed (relay fallback disabled) → %s:%d", host, port)
                return
            log.warning("Telegram direct tunnel fallback → %s:%d (switching to relay)", host, port)

        # Some cross-site auth/script hosts break when their responses are
        # relayed through Apps Script (MIME/ORB issues). Keep them direct-only.
        if host.lower().rstrip(".") in self._DIRECT_ONLY_EXACT_HOSTS:
            log.info("Direct-only tunnel → %s:%d", host, port)
            ok = await self._do_direct_tunnel(host, port, reader, writer)
            if not ok:
                log.warning("Direct-only host failed (no relay fallback): %s:%d", host, port)
            return

        # ── IP-literal destinations ───────────────────────────────
        # Prefer a direct tunnel first (works for unblocked IPs and keeps
        # TLS end-to-end). If the network blocks the route (common for
        # Telegram data-centers behind DPI), fall back to:
        #   • port 443 → MITM + relay through Apps Script
        #   • port 80  → plain-HTTP relay through Apps Script
        #   • other    → give up (non-HTTP; can't be relayed)
        # We use a shorter connect timeout for IP literals (4 s) because
        # when the route is DPI-dropped, waiting longer doesn't help and
        # clients like Telegram speed up DC-rotation when we fail fast.
        # We remember per-IP failures for a short while so subsequent
        # connects skip the doomed direct attempt.
        if _is_ip_literal(host):
            if not self._direct_temporarily_disabled(host):
                log.info("Direct tunnel → %s:%d (IP literal)", host, port)
                ok = await self._do_direct_tunnel(
                    host, port, reader, writer, timeout=4.0,
                )
                if ok:
                    return
                self._remember_direct_failure(host, ttl=300)
                if port not in (80, 443):
                    log.warning("Direct tunnel failed for %s:%d", host, port)
                    return
                log.warning(
                    "Direct tunnel fallback → %s:%d (switching to relay)",
                    host, port,
                )
            else:
                log.info(
                    "Relay fallback → %s:%d (direct temporarily disabled)",
                    host, port,
                )
            if port == 443:
                await self._do_mitm_connect(host, port, reader, writer)
            elif port == 80:
                await self._do_plain_http_tunnel(host, port, reader, writer)
            return

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
            if self._direct_temporarily_disabled(host):
                log.info("Relay fallback → %s (direct tunnel temporarily disabled)", host)
                if port == 443:
                    await self._do_mitm_connect(host, port, reader, writer)
                else:
                    await self._do_plain_http_tunnel(host, port, reader, writer)
                return

            log.info("Direct tunnel → %s (Google domain, skipping relay)", host)
            ok = await self._do_direct_tunnel(host, port, reader, writer)
            if ok:
                return

            self._remember_direct_failure(host)
            log.warning("Direct tunnel fallback → %s (switching to relay)", host)
            if port == 443:
                await self._do_mitm_connect(host, port, reader, writer)
            else:
                await self._do_plain_http_tunnel(host, port, reader, writer)
        elif port == 443:
            await self._do_mitm_connect(host, port, reader, writer)
        elif port == 80:
            await self._do_plain_http_tunnel(host, port, reader, writer)
        else:
            # Non-HTTP port (e.g. mtalk:5228 XMPP, IMAP, SMTP, SSH) —
            # payload isn't HTTP, so we can't relay or MITM. Tunnel bytes.
            log.info("Direct tunnel → %s:%d (non-HTTP port)", host, port)
            ok = await self._do_direct_tunnel(host, port, reader, writer)
            if not ok:
                log.warning("Direct tunnel failed for %s:%d", host, port)

    # ── Hosts override (fake DNS) ─────────────────────────────────

    # Built-in list of domains that must be reached via Google's frontend IP
    # with SNI rewritten to `front_domain` (default: www.google.com).
    # Source: constants.SNI_REWRITE_SUFFIXES.
    # When youtube_via_relay is enabled the YouTube suffixes are removed so
    # YouTube goes through the Apps Script relay instead.
    _YOUTUBE_SNI_SUFFIXES = frozenset({
        "youtube.com", "youtu.be", "youtube-nocookie.com",
    })
    _SNI_REWRITE_SUFFIXES = SNI_REWRITE_SUFFIXES

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

    # Google-owned domains that may use the raw direct-tunnel shortcut.
    # YouTube/googlevideo SNIs are blocked; they go through
    # _do_sni_rewrite_tunnel via the hosts map instead.
    # Source: constants.GOOGLE_OWNED_SUFFIXES / GOOGLE_OWNED_EXACT.
    _GOOGLE_OWNED_SUFFIXES = GOOGLE_OWNED_SUFFIXES
    _GOOGLE_OWNED_EXACT = GOOGLE_OWNED_EXACT

    def _is_google_domain(self, host: str) -> bool:
        """Return True if host should use the raw direct Google shortcut."""
        h = host.lower().rstrip(".")
        if self._is_direct_google_excluded(h):
            return False
        if not self._is_google_owned_domain(h):
            return False
        return self._is_direct_google_allowed(h)

    def _is_google_owned_domain(self, host: str) -> bool:
        if host in self._GOOGLE_OWNED_EXACT:
            return True
        for suffix in self._GOOGLE_OWNED_SUFFIXES:
            if host.endswith(suffix):
                return True
        return False

    def _is_direct_google_excluded(self, host: str) -> bool:
        if host in self._direct_google_exclude:
            return True
        for suffix in self._GOOGLE_DIRECT_SUFFIX_EXCLUDE:
            if host.endswith(suffix):
                return True
        for token in self._direct_google_exclude:
            if token.startswith(".") and host.endswith(token):
                return True
        return False

    def _is_direct_google_allowed(self, host: str) -> bool:
        if host in self._direct_google_allow:
            return True
        for suffix in self._GOOGLE_DIRECT_ALLOW_SUFFIXES:
            if host.endswith(suffix):
                return True
        for token in self._direct_google_allow:
            if token.startswith(".") and host.endswith(token):
                return True
        return False

    def _direct_temporarily_disabled(self, host: str) -> bool:
        h = host.lower().rstrip(".")
        now = time.time()
        disabled = False
        for key in self._direct_failure_keys(h):
            until = self._direct_fail_until.get(key, 0)
            if until > now:
                disabled = True
            else:
                self._direct_fail_until.pop(key, None)
        return disabled

    def _remember_direct_failure(self, host: str, ttl: int = 600):
        until = time.time() + ttl
        for key in self._direct_failure_keys(host.lower().rstrip(".")):
            self._direct_fail_until[key] = until

    def _direct_failure_keys(self, host: str) -> tuple[str, ...]:
        keys = [host]
        if host.endswith(".google.com") or host == "google.com":
            keys.append("*.google.com")
        if host.endswith(".googleapis.com") or host == "googleapis.com":
            keys.append("*.googleapis.com")
        if host.endswith(".gstatic.com") or host == "gstatic.com":
            keys.append("*.gstatic.com")
        if host.endswith(".googleusercontent.com") or host == "googleusercontent.com":
            keys.append("*.googleusercontent.com")
        return tuple(dict.fromkeys(keys))

    async def _open_tcp_connection(self, target: str, port: int,
                                   timeout: float = 10.0):
        """Connect with IPv4-first resolution and clearer failure reporting."""
        errors: list[str] = []
        loop = asyncio.get_running_loop()

        # Strip IPv6 brackets (CONNECT may deliver "[::1]" as the hostname).
        # ipaddress.ip_address() rejects the bracketed form, which would
        # otherwise force a DNS lookup for an IP literal and fail.
        lookup_target = target.strip()
        if lookup_target.startswith("[") and lookup_target.endswith("]"):
            lookup_target = lookup_target[1:-1]

        try:
            ipaddress.ip_address(lookup_target)
            candidates = [(0, lookup_target)]
        except ValueError:
            try:
                infos = await asyncio.wait_for(
                    loop.getaddrinfo(
                        lookup_target,
                        port,
                        family=socket.AF_UNSPEC,
                        type=socket.SOCK_STREAM,
                    ),
                    timeout=timeout,
                )
            except Exception as exc:
                raise OSError(f"dns lookup failed for {lookup_target}: {exc!r}") from exc

            candidates = []
            seen = set()
            for family, _type, _proto, _canon, sockaddr in infos:
                ip = sockaddr[0]
                key = (family, ip)
                if key in seen:
                    continue
                seen.add(key)
                candidates.append((family, ip))

            candidates.sort(key=lambda item: 0 if item[0] == socket.AF_INET else 1)

        for family, ip in candidates:
            try:
                conn = await asyncio.wait_for(
                    asyncio.open_connection(ip, port, family=family or 0),
                    timeout=timeout,
                )
                _, w = conn
                sock = w.get_extra_info("socket")
                if sock is not None:
                    try:
                        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                    except OSError:
                        pass
                    try:
                        sock.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
                    except OSError:
                        pass
                    try:
                        sock.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, self._tcp_send_buffer)
                        sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, self._tcp_recv_buffer)
                    except OSError:
                        pass
                    log.debug(
                        "Socket tuning applied → %s:%d (nodelay=1 keepalive=1 sndbuf=%d rcvbuf=%d)",
                        ip, port, self._tcp_send_buffer, self._tcp_recv_buffer,
                    )
                return conn
            except Exception as exc:
                fam = "ipv4" if family == socket.AF_INET else (
                    "ipv6" if family == socket.AF_INET6 else "auto"
                )
                errors.append(f"{ip} ({fam}): {exc!r}")

        raise OSError("; ".join(errors) or f"connect failed for {target}:{port}")

    # ── Direct tunnel (no MITM) ───────────────────────────────────

    async def _do_direct_tunnel(self, host: str, port: int,
                                reader: asyncio.StreamReader,
                                writer: asyncio.StreamWriter,
                                connect_ip: str | None = None,
                                timeout: float | None = None):
        """Pipe raw TLS bytes directly to the target server.

        connect_ip overrides DNS: the TCP connection goes to that IP
        while the browser's TLS (SNI=host) is piped through unchanged.
        Without an override we connect to the real hostname so browser-safe
        Google properties (Gemini assets, Play, Accounts, etc.) use their
        normal edge instead of being forced onto the fronting IP.
        """
        target_ip = connect_ip or host
        effective_timeout = (
            self._tcp_connect_timeout if timeout is None else float(timeout)
        )
        connect_candidates = [target_ip]
        if _is_ip_literal(target_ip) and self._ip_matches_no_mitm_cidr(target_ip):
            connect_candidates.extend(
                self._pick_alternate_dc_ips(target_ip, self._dc_failover_attempts)
            )

        r_remote = None
        w_remote = None
        last_err = None
        connected_ip = target_ip
        for idx, candidate_ip in enumerate(connect_candidates):
            try:
                r_remote, w_remote = await self._open_tcp_connection(
                    candidate_ip, port, timeout=effective_timeout,
                )
                connected_ip = candidate_ip
                if idx > 0:
                    log.warning(
                        "DC failover triggered → %s:%d (original=%s, selected=%s)",
                        host, port, target_ip, candidate_ip,
                    )
                    if self._is_telegram_target(host):
                        log.warning(
                            "Telegram DC failover triggered → %s:%d (from %s to %s)",
                            host, port, target_ip, candidate_ip,
                        )
                break
            except Exception as e:
                last_err = e
                if idx + 1 < len(connect_candidates):
                    log.warning(
                        "DC failover triggered → %s:%d (candidate failed: %s)",
                        host, port, candidate_ip,
                    )
                    if self._is_telegram_target(host):
                        log.warning(
                            "Telegram DC candidate failed → %s:%d (candidate=%s)",
                            host, port, candidate_ip,
                        )
                continue
        if r_remote is None or w_remote is None:
            log.error("Direct tunnel connect failed (%s via %s): %s",
                      host, target_ip, last_err)
            return False
        log.info("Direct tunnel established → %s:%d", host, port)
        if self._is_telegram_target(host):
            log.info("Telegram tunnel established → %s:%d (upstream=%s)", host, port, connected_ip)

        tunnel_state = {
            "last_rx": time.time(),
            "last_tx": time.time(),
            "tx_bytes": 0,
            "last_probe_tx_bytes": 0,
            "degraded": False,
        }

        async def pipe(src, dst, label):
            try:
                while True:
                    data = await src.read(65536)
                    if not data:
                        break
                    now = time.time()
                    if src is reader:
                        tunnel_state["tx_bytes"] += len(data)
                        tunnel_state["last_tx"] = now
                    else:
                        tunnel_state["last_rx"] = now
                    dst.write(data)
                    await dst.drain()
            except (ConnectionError, asyncio.CancelledError):
                pass
            except Exception as e:
                log.debug("Pipe %s ended: %s", label, e)
            finally:
                # Half-close rather than hard-close so the other direction
                # can still flush final bytes (important for TLS close_notify).
                try:
                    if not dst.is_closing() and dst.can_write_eof():
                        dst.write_eof()
                except Exception:
                    try:
                        dst.close()
                    except Exception:
                        pass

        async def half_open_watchdog():
            while True:
                await asyncio.sleep(2.0)
                if writer.is_closing() or w_remote.is_closing():
                    return
                no_rx_for = time.time() - tunnel_state["last_rx"]
                tx_active = tunnel_state["tx_bytes"] > tunnel_state["last_probe_tx_bytes"]
                if no_rx_for < self._half_open_rx_timeout or not tx_active:
                    continue
                tunnel_state["last_probe_tx_bytes"] = tunnel_state["tx_bytes"]
                log.warning(
                    "Half-open detection → %s:%d (no_rx_for=%.1fs, tx_bytes=%d)",
                    host, port, no_rx_for, tunnel_state["tx_bytes"],
                )
                if self._is_telegram_target(host):
                    log.warning("Telegram tunnel half-open suspected → %s:%d", host, port)
                try:
                    _, probe_w = await self._open_tcp_connection(
                        connected_ip, port, timeout=self._half_open_probe_timeout,
                    )
                    probe_w.close()
                    await probe_w.wait_closed()
                except Exception:
                    tunnel_state["degraded"] = True
                    log.warning(
                        "Half-open probe failed → %s:%d (mark degraded, reconnect required)",
                        host, port,
                    )
                    if self._is_telegram_target(host):
                        log.warning("Telegram DC failover hint → %s:%d (probe failure)", host, port)
                    try:
                        w_remote.close()
                    except Exception:
                        pass
                    try:
                        writer.close()
                    except Exception:
                        pass
                    return

        await asyncio.gather(
            pipe(reader, w_remote, f"client→{host}"),
            pipe(r_remote, writer, f"{host}→client"),
            half_open_watchdog(),
        )
        return True

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
        loop = asyncio.get_running_loop()
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
        if certifi is not None:
            try:
                ssl_ctx_client.load_verify_locations(cafile=certifi.where())
            except Exception:
                pass
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
                timeout=self._tcp_connect_timeout,
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

    async def _do_plain_http_tunnel(self, host: str, port: int, reader, writer):
        """Handle plain HTTP over SOCKS5 in apps_script mode."""
        log.info("Plain HTTP relay → %s:%d", host, port)
        await self._relay_http_stream(host, port, reader, writer)

    async def _do_mitm_connect(self, host: str, port: int, reader, writer):
        """Intercept TLS, decrypt HTTP, and relay through Apps Script."""
        ssl_ctx = self.mitm.get_server_context(host)

        # Upgrade the existing connection to TLS (we are the server)
        loop = asyncio.get_running_loop()
        transport = writer.transport
        protocol = transport.get_protocol()

        try:
            new_transport = await loop.start_tls(
                transport, protocol, ssl_ctx, server_side=True,
            )
        except Exception as e:
            # TLS handshake failed. Common causes:
            #   • Telegram Desktop / MTProto over port 443 sends obfuscated
            #     non-TLS bytes — we literally cannot decrypt these, and
            #     since the target IP is blocked we can't direct-tunnel
            #     either. Telegram will rotate to another DC on its own;
            #     failing fast here lets that happen sooner.
            #   • Client CONNECTs but never speaks TLS (some probes).
            if _is_ip_literal(host) and port == 443:
                log.info(
                    "Non-TLS traffic on %s:%d (likely Telegram MTProto / "
                    "obfuscated protocol). This DC appears blocked; the "
                    "client should rotate to another endpoint shortly.",
                    host, port,
                )
            elif port != 443:
                log.debug(
                    "TLS handshake skipped for %s:%d (non-HTTPS): %s",
                    host, port, e,
                )
            else:
                log.debug("TLS handshake failed for %s: %s", host, e)
            # Close the client side so it fails fast and can retry, rather
            # than hanging on a half-open connection.
            try:
                if not writer.is_closing():
                    writer.close()
            except Exception:
                pass
            return

        # Update writer to use the new TLS transport
        writer._transport = new_transport

        await self._relay_http_stream(host, port, reader, writer)

    async def _relay_http_stream(self, host: str, port: int, reader, writer):
        """Read decrypted/origin-form HTTP requests and relay them."""
        per_request_timeout = max(20.0, min(120.0, self._tcp_connect_timeout * 3.0))
        # Read and relay HTTP requests from the browser (now decrypted)
        while True:
            try:
                first_line = await asyncio.wait_for(
                    reader.readline(), timeout=CLIENT_IDLE_TIMEOUT
                )
                if not first_line:
                    break

                header_block = first_line
                oversized_headers = False
                while True:
                    line = await asyncio.wait_for(reader.readline(), timeout=10)
                    header_block += line
                    if len(header_block) > MAX_HEADER_BYTES:
                        oversized_headers = True
                        break
                    if line in (b"\r\n", b"\n", b""):
                        break

                # Reject truncated / oversized header blocks cleanly rather
                # than forwarding a half-parsed request to the relay — doing
                # so would send malformed JSON payloads to Apps Script and
                # leave the client hanging until its own timeout fires.
                if oversized_headers:
                    log.warning(
                        "MITM header block exceeds %d bytes — closing (%s)",
                        MAX_HEADER_BYTES, host,
                    )
                    try:
                        writer.write(
                            b"HTTP/1.1 431 Request Header Fields Too Large\r\n"
                            b"Connection: close\r\n"
                            b"Content-Length: 0\r\n\r\n"
                        )
                        await writer.drain()
                    except Exception:
                        pass
                    break

                # Read body
                body = b""
                if _has_unsupported_transfer_encoding(header_block):
                    log.warning("Unsupported Transfer-Encoding → %s:%d", host, port)
                    writer.write(
                        b"HTTP/1.1 501 Not Implemented\r\n"
                        b"Connection: close\r\n"
                        b"Content-Length: 0\r\n\r\n"
                    )
                    await writer.drain()
                    break
                length = _parse_content_length(header_block)
                if length > MAX_REQUEST_BODY_BYTES:
                    raise ValueError(f"Request body too large: {length} bytes")
                if length > 0:
                    body = await reader.readexactly(length)

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

                # Shortening the length of X API URLs to prevent relay errors.
                if (host == "x.com" or host == "twitter.com") and  re.match(r"/i/api/graphql/[^/]+/[^?]+\?variables=", path):
                    path = path.split("&")[0]

                # MITM traffic arrives as origin-form paths; SOCKS/plain HTTP can
                # also send absolute-form requests. Normalize both to full URLs.
                if path.startswith("http://") or path.startswith("https://"):
                    url = path
                elif port == 443:
                    url = f"https://{host}{path}"
                elif port == 80:
                    url = f"http://{host}{path}"
                else:
                    url = f"http://{host}:{port}{path}"

                log.info("MITM → %s %s", method, url)

                # Apps Script relay is not a reliable transport for long-lived
                # Telegram Web websocket channels (/apiws). Try a direct TLS
                # websocket bridge first; if it fails, continue with normal
                # relay handling so clients still get an HTTP-level fallback.
                if (self._is_telegram_web_host(host)
                        and path.startswith("/apiws")
                        and self._is_websocket_upgrade(headers)):
                    if await self._try_direct_ws_bridge(
                        host, port, header_block, body, reader, writer,
                    ):
                        return

                # ── CORS: extract relevant request headers ─────────────
                origin = self._header_value(headers, "origin")
                acr_method = self._header_value(
                    headers, "access-control-request-method",
                )
                acr_headers = self._header_value(
                    headers, "access-control-request-headers",
                )

                # CORS preflight — respond directly. Apps Script's
                # UrlFetchApp does not support the OPTIONS method, so
                # forwarding preflights would always fail and break every
                # cross-origin fetch/XHR the browser runs through us.
                if method.upper() == "OPTIONS" and acr_method:
                    log.debug(
                        "CORS preflight → %s (responding locally)",
                        url[:60],
                    )
                    writer.write(self._cors_preflight_response(
                        origin, acr_method, acr_headers,
                    ))
                    await writer.drain()
                    continue

                if await self._maybe_stream_download(method, url, headers, body, writer):
                    continue

                cacheable = self._cache_allowed(method, url, headers, body)
                cache_key = ResponseCache.build_key(url, headers) if cacheable else ""
                try:
                    response = await asyncio.wait_for(
                        self._fetch_with_cache(
                            host, method, url, headers, body, cacheable, cache_key
                        ),
                        timeout=per_request_timeout,
                    )
                except asyncio.TimeoutError:
                    log.warning("Upstream timeout (%s %s)", method, url[:80])
                    err_body = b"Upstream timeout. Please retry."
                    response = (
                        b"HTTP/1.1 504 Gateway Timeout\r\n"
                        b"Content-Type: text/plain\r\n"
                        b"Connection: keep-alive\r\n"
                        b"Content-Length: " + str(len(err_body)).encode() + b"\r\n"
                        b"\r\n" + err_body
                    )

                # Inject permissive CORS headers whenever the browser sent
                # an Origin (cross-origin XHR / fetch). Without this, the
                # browser blocks the response even though the relay fetched
                # it successfully.
                if origin and response:
                    response = self._inject_cors_headers(response, origin)

                self._log_response_summary(url, response)
                self._maybe_log_cache_stats()

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

    @staticmethod
    def _is_websocket_upgrade(headers: dict) -> bool:
        conn = str(headers.get("Connection", "")).lower()
        upg = str(headers.get("Upgrade", "")).lower()
        return ("upgrade" in conn) and (upg == "websocket")

    async def _try_direct_ws_bridge(self, host: str, port: int,
                                    header_block: bytes, body: bytes,
                                    client_reader, client_writer) -> bool:
        if port != 443:
            return False
        ssl_ctx_client = ssl.create_default_context()
        if certifi is not None:
            try:
                ssl_ctx_client.load_verify_locations(cafile=certifi.where())
            except Exception:
                pass
        if not self.fronter.verify_ssl:
            ssl_ctx_client.check_hostname = False
            ssl_ctx_client.verify_mode = ssl.CERT_NONE
        try:
            up_reader, up_writer = await asyncio.wait_for(
                asyncio.open_connection(
                    host, port, ssl=ssl_ctx_client, server_hostname=host,
                ),
                timeout=4.0,
            )
        except Exception as e:
            log.debug("Telegram WS direct bridge connect failed (%s): %s", host, e)
            return False
        try:
            up_writer.write(header_block + body)
            await up_writer.drain()
        except Exception as e:
            log.debug("Telegram WS direct bridge send failed (%s): %s", host, e)
            try:
                up_writer.close()
            except Exception:
                pass
            return False

        log.info("Telegram WS direct bridge established → %s:%d", host, port)

        async def pipe(src, dst):
            try:
                while True:
                    data = await src.read(65536)
                    if not data:
                        break
                    dst.write(data)
                    await dst.drain()
            except Exception:
                pass
            finally:
                try:
                    dst.close()
                except Exception:
                    pass

        await asyncio.gather(
            pipe(client_reader, up_writer),
            pipe(up_reader, client_writer),
        )
        return True

    # ── CORS helpers ──────────────────────────────────────────────

    @staticmethod
    def _cors_preflight_response(origin: str, acr_method: str,
                                 acr_headers: str) -> bytes:
        """Build a 204 response that satisfies a CORS preflight locally.

        Apps Script's UrlFetchApp does not support OPTIONS, so we have to
        answer preflights here instead of forwarding them.
        """
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
        """Strip existing Access-Control-* headers and add permissive ones.

        Keeps the body untouched; only rewrites the header block. Using
        the exact browser-supplied Origin (rather than "*") is required
        when the request is credentialed (cookies, Authorization).
        """
        sep = b"\r\n\r\n"
        if sep not in response:
            return response
        header_section, body = response.split(sep, 1)
        lines = header_section.decode(errors="replace").split("\r\n")
        lines = [ln for ln in lines
                 if not ln.lower().startswith("access-control-")]
        allow_origin = origin or "*"
        lines += [
            f"Access-Control-Allow-Origin: {allow_origin}",
            "Access-Control-Allow-Credentials: true",
            "Access-Control-Allow-Methods: GET, POST, PUT, DELETE, PATCH, OPTIONS",
            "Access-Control-Allow-Headers: *",
            "Access-Control-Expose-Headers: *",
            "Vary: Origin",
        ]
        return ("\r\n".join(lines) + "\r\n\r\n").encode() + body

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
            # Avoid synthetic range-probe/parallel path for fragile web apps.
            if self._is_sensitive_app_url(url):
                return await self.fronter.relay(method, url, headers, body)
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
                    method,
                    url,
                    headers,
                    body,
                    chunk_size=self._download_chunk_size,
                    max_parallel=self._download_max_parallel,
                    max_chunks=self._download_max_chunks,
                    min_size=self._download_min_size,
                )
        return await self.fronter.relay(method, url, headers, body)

    def _is_likely_download(self, url: str, headers: dict) -> bool:
        """Heuristic: is this URL likely a large file download?"""
        path = url.split("?")[0].lower()
        if self._download_any_extension:
            return True
        for ext in self._download_extensions:
            if path.endswith(ext):
                return True
        accept = self._header_value(headers, "accept").lower()
        if any(marker in accept for marker in self._DOWNLOAD_ACCEPT_MARKERS):
            return True
        return False

    async def _maybe_stream_download(self, method: str, url: str,
                                     headers: dict | None, body: bytes,
                                     writer) -> bool:
        if method.upper() != "GET" or body:
            return False
        if self._is_sensitive_app_url(url):
            return False
        if headers:
            for key in headers:
                if key.lower() == "range":
                    return False
        effective_headers = headers or {}
        if not self._is_likely_download(url, effective_headers):
            return False
        if not self.fronter.stream_download_allowed(url):
            return False
        return await self.fronter.stream_parallel_download(
            url,
            effective_headers,
            writer,
            chunk_size=self._download_chunk_size,
            max_parallel=self._download_max_parallel,
            max_chunks=self._download_max_chunks,
            min_size=self._download_min_size,
        )

    # ── Plain HTTP forwarding ─────────────────────────────────────

    async def _do_http(self, header_block: bytes, reader, writer):
        body = b""
        if _has_unsupported_transfer_encoding(header_block):
            log.warning("Unsupported Transfer-Encoding on plain HTTP request")
            writer.write(
                b"HTTP/1.1 501 Not Implemented\r\n"
                b"Connection: close\r\n"
                b"Content-Length: 0\r\n\r\n"
            )
            await writer.drain()
            return
        length = _parse_content_length(header_block)
        if length > MAX_REQUEST_BODY_BYTES:
            writer.write(b"HTTP/1.1 413 Content Too Large\r\n\r\n")
            await writer.drain()
            return
        if length > 0:
            body = await reader.readexactly(length)

        first_line = header_block.split(b"\r\n")[0].decode(errors="replace")
        log.info("HTTP → %s", first_line)

        # Parse request and relay through Apps Script
        parts = first_line.strip().split(" ", 2)
        method = parts[0] if parts else "GET"
        url = parts[1] if len(parts) > 1 else "/"

        headers = {}
        for raw_line in header_block.split(b"\r\n")[1:]:
            if b":" in raw_line:
                k, v = raw_line.decode(errors="replace").split(":", 1)
                headers[k.strip()] = v.strip()

        # ── CORS preflight over plain HTTP ─────────────────────────────
        origin = self._header_value(headers, "origin")
        acr_method = self._header_value(headers, "access-control-request-method")
        acr_headers = self._header_value(headers, "access-control-request-headers")
        if method.upper() == "OPTIONS" and acr_method:
            log.debug("CORS preflight (HTTP) → %s (responding locally)", url[:60])
            writer.write(self._cors_preflight_response(
                origin, acr_method, acr_headers,
            ))
            await writer.drain()
            return

        if await self._maybe_stream_download(method, url, headers, body, writer):
            return

        cacheable = self._cache_allowed(method, url, headers, body)
        cache_key = ResponseCache.build_key(url, headers) if cacheable else ""
        host = (urlparse(url).hostname or "").lower()
        response = await self._fetch_with_cache(
            host, method, url, headers, body, cacheable, cache_key
        )

        if origin and response:
            response = self._inject_cors_headers(response, origin)

        self._log_response_summary(url, response)
        self._maybe_log_cache_stats()

        writer.write(response)
        await writer.drain()
