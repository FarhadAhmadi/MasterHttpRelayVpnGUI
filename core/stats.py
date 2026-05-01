"""Runtime stats — collected by gui_bridge, emitted to stdout once a second."""

import threading
import time

_lock = threading.Lock()
_bytes_up = 0
_bytes_down = 0
_requests = 0
_conn_open = 0
_conn_peak = 0
_started = time.time()
_last_t = time.time()
_last_up = 0
_last_down = 0
_last_requests = 0
_cache_hits = 0
_cache_misses = 0
_cache_stale_hits = 0
_cache_entries = 0
_cache_bytes = 0


def add_up(n):
    global _bytes_up
    if n <= 0: return
    with _lock: _bytes_up += n


def add_down(n):
    global _bytes_down
    if n <= 0: return
    with _lock: _bytes_down += n


def incr_requests():
    global _requests
    with _lock: _requests += 1


def conn_opened():
    global _conn_open, _conn_peak
    with _lock:
        _conn_open += 1
        if _conn_open > _conn_peak:
            _conn_peak = _conn_open


def conn_closed():
    global _conn_open
    with _lock:
        if _conn_open > 0:
            _conn_open -= 1


def cache_hit():
    global _cache_hits
    with _lock:
        _cache_hits += 1


def cache_miss():
    global _cache_misses
    with _lock:
        _cache_misses += 1


def cache_stale_hit():
    global _cache_stale_hits
    with _lock:
        _cache_stale_hits += 1


def cache_snapshot(entries, size_bytes):
    global _cache_entries, _cache_bytes
    with _lock:
        _cache_entries = max(int(entries), 0)
        _cache_bytes = max(int(size_bytes), 0)


def snapshot():
    global _last_t, _last_up, _last_down, _last_requests
    now = time.time()
    with _lock:
        up, down = _bytes_up, _bytes_down
        co, peak, reqs = _conn_open, _conn_peak, _requests
        ch, cm, csh = _cache_hits, _cache_misses, _cache_stale_hits
        ce, cb = _cache_entries, _cache_bytes

    dt = max(now - _last_t, 0.001)
    sup = (up - _last_up) / dt
    sdown = (down - _last_down) / dt
    rps = (reqs - _last_requests) / dt
    _last_t, _last_up, _last_down, _last_requests = now, up, down, reqs

    # Surface health + endpoint count to the GUI.
    try:
        import health as _health
        import multi_id as _multi
        h = _health.state()
        ep = _multi.snapshot()
        hx = _health.snapshot()
    except Exception:
        h = "good"
        ep = {
            "endpoints": 0,
            "endpoints_healthy": 0,
            "latency_ms": 0.0,
            "success_rate": 1.0,
            "active_endpoint": "",
        }
        hx = {
            "window_requests": 0,
            "window_errors": 0,
            "window_success_rate": 1.0,
        }

    return {
        "uptime": int(now - _started),
        "bytes_up": up, "bytes_down": down,
        "speed_up": sup, "speed_down": sdown,
        "requests": reqs,
        "connections": co, "peak_connections": peak,
        "cache_hits": ch,
        "cache_misses": cm,
        "cache_stale_hits": csh,
        "cache_hit_rate": (ch / (ch + cm)) if (ch + cm) else 0.0,
        "cache_effective_hit_rate": ((ch + csh) / (ch + cm)) if (ch + cm) else 0.0,
        "cache_entries": ce,
        "cache_bytes": cb,
        "health": h,
        "requests_per_sec": rps,
        **ep,
        **hx,
    }
