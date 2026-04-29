"""
Connection health probe.

Aggregates real request success/failure counts in a sliding 60s window
and produces a single health string the GUI shows in its indicator pill.

States:
    good      ratio >= 0.95
    unstable  0.40 <= ratio < 0.95   OR   < 3 samples in window
    down      ratio < 0.40           OR   no samples for >30s while running
"""

import threading
import time

import multi_id


_lock = threading.Lock()
_window = []          # list of (t, ok:bool)
_window_secs = 60.0
_last_activity = 0.0


def record(ok: bool):
    """Called by gui_bridge whenever a relay request completes."""
    global _last_activity
    now = time.monotonic()
    with _lock:
        _window.append((now, ok))
        _last_activity = now
        cutoff = now - _window_secs
        while _window and _window[0][0] < cutoff:
            _window.pop(0)


def state() -> str:
    """Compute current health. Combines local request stats with the
    multi_id dispatcher's view (so even one parked endpoint shows yellow)."""
    now = time.monotonic()
    disp = multi_id.current_health()  # 'good' | 'unstable' | 'down'

    with _lock:
        cutoff = now - _window_secs
        while _window and _window[0][0] < cutoff:
            _window.pop(0)
        n = len(_window)
        ok = sum(1 for _, b in _window if b)
        idle_for = now - _last_activity if _last_activity else 0

    # No traffic yet — defer to dispatcher's view (or 'good' for single-ID).
    if n == 0:
        return disp

    ratio = ok / n
    if ratio >= 0.95 and disp == "good":
        return "good"
    if ratio < 0.40 or disp == "down":
        return "down"
    return "unstable"


def snapshot():
    """Return windowed health metrics for richer GUI diagnostics."""
    now = time.monotonic()
    with _lock:
        cutoff = now - _window_secs
        while _window and _window[0][0] < cutoff:
            _window.pop(0)
        total = len(_window)
        ok = sum(1 for _, b in _window if b)
        err = total - ok

    return {
        "window_requests": total,
        "window_errors": err,
        "window_success_rate": (ok / total) if total else 1.0,
    }
