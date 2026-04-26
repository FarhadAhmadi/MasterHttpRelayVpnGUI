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


def snapshot():
    global _last_t, _last_up, _last_down
    now = time.time()
    with _lock:
        up, down = _bytes_up, _bytes_down
        co, peak, reqs = _conn_open, _conn_peak, _requests

    dt = max(now - _last_t, 0.001)
    sup = (up - _last_up) / dt
    sdown = (down - _last_down) / dt
    _last_t, _last_up, _last_down = now, up, down

    # Surface health + endpoint count to the GUI.
    try:
        import health as _health
        import multi_id as _multi
        h = _health.state()
        eps = _multi.endpoint_count()
    except Exception:
        h, eps = "good", 0

    return {
        "uptime": int(now - _started),
        "bytes_up": up, "bytes_down": down,
        "speed_up": sup, "speed_down": sdown,
        "requests": reqs,
        "connections": co, "peak_connections": peak,
        "health": h,
        "endpoints": eps,
    }
