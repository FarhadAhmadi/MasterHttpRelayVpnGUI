"""
Health-based multi deployment-id router.

Single-ID mode is intentionally a no-op. With multiple Apps Script IDs this
patches DomainFronter._next_script_id(), scores each endpoint, routes more
traffic to healthier endpoints, parks failing endpoints, and probes parked
endpoints again after cooldown.
"""

import logging
import threading
import time
from contextvars import ContextVar

log = logging.getLogger("MultiID")


class _Endpoint:
    __slots__ = (
        "sid", "ok", "err", "recent_failures", "parked_until",
        "last_err", "latency_ms", "last_used", "uses",
    )

    def __init__(self, sid):
        self.sid = sid
        self.ok = 0
        self.err = 0
        self.recent_failures = 0
        self.parked_until = 0.0
        self.last_err = ""
        self.latency_ms = 0.0
        self.last_used = 0.0
        self.uses = 0

    @property
    def total(self):
        return self.ok + self.err

    @property
    def success_rate(self):
        if self.total == 0:
            return 1.0
        return self.ok / max(self.total, 1)

    def score(self, now):
        latency_penalty = min(self.latency_ms, 3000.0) / 100.0 if self.latency_ms else 0.0
        failure_penalty = self.recent_failures * 20.0
        cooldown_penalty = 80.0 if self.parked_until > now else 0.0
        fairness_penalty = min(self.uses, 20) * 0.25
        return (self.success_rate * 50.0) - latency_penalty - failure_penalty - cooldown_penalty - fairness_penalty


class MultiIdDispatcher:
    def __init__(self, ids, fail_threshold=3, cooldown=30.0):
        self._lock = threading.Lock()
        self._eps = [_Endpoint(s) for s in ids]
        self._fail_threshold = max(1, int(fail_threshold))
        self._cooldown = max(5.0, float(cooldown))
        self._active_sid = ""
        self._rr = 0

    @property
    def count(self):
        return len(self._eps)

    def next_id(self):
        now = time.monotonic()
        with self._lock:
            if not self._eps:
                return None

            live = [e for e in self._eps if e.parked_until <= now]
            if not live:
                ep = min(self._eps, key=lambda e: e.parked_until)
                ep.parked_until = 0.0
                ep.recent_failures = max(0, ep.recent_failures - 1)
                live = [ep]
                log.warning("all deployment IDs parked; probing %s", _short(ep.sid))

            ranked = sorted(live, key=lambda e: e.score(now), reverse=True)
            top_score = ranked[0].score(now)
            eligible = [e for e in ranked if top_score - e.score(now) <= 12.0] or ranked[:1]
            ep = eligible[self._rr % len(eligible)]
            self._rr += 1
            ep.last_used = now
            ep.uses += 1
            self._active_sid = ep.sid
            return ep.sid

    def report_ok(self, sid, latency_ms=None):
        if not sid:
            return
        with self._lock:
            ep = self._find(sid)
            if not ep:
                return
            ep.ok += 1
            ep.recent_failures = 0
            ep.parked_until = 0.0
            if latency_ms and latency_ms > 0:
                ep.latency_ms = latency_ms if ep.latency_ms <= 0 else (ep.latency_ms * 0.75 + latency_ms * 0.25)

    def report_err(self, sid, msg=""):
        if not sid:
            return
        with self._lock:
            ep = self._find(sid)
            if not ep:
                return
            ep.err += 1
            ep.recent_failures += 1
            ep.last_err = msg[:180] if msg else ""
            if ep.recent_failures >= self._fail_threshold:
                ep.parked_until = time.monotonic() + self._cooldown
                log.warning("parking %s for %ds (%d failures)",
                            _short(ep.sid), int(self._cooldown), ep.recent_failures)

    def health(self):
        now = time.monotonic()
        with self._lock:
            if not self._eps:
                return "good"
            live = [e for e in self._eps if e.parked_until <= now]
            if not live:
                return "down"
            if len(live) < len(self._eps):
                return "unstable"
            avg_success = sum(e.success_rate for e in self._eps) / len(self._eps)
            return "good" if avg_success >= 0.95 else "unstable"

    def snapshot(self):
        now = time.monotonic()
        with self._lock:
            if not self._eps:
                return {
                    "endpoints": 0,
                    "endpoints_healthy": 0,
                    "latency_ms": 0.0,
                    "success_rate": 1.0,
                    "active_endpoint": "",
                }
            live = [e for e in self._eps if e.parked_until <= now]
            measured = [e.latency_ms for e in self._eps if e.latency_ms > 0]
            total_ok = sum(e.ok for e in self._eps)
            total = sum(e.total for e in self._eps)
            return {
                "endpoints": len(self._eps),
                "endpoints_healthy": len(live),
                "latency_ms": (sum(measured) / len(measured)) if measured else 0.0,
                "success_rate": (total_ok / total) if total else 1.0,
                "active_endpoint": _short(self._active_sid),
            }

    def _find(self, sid):
        for ep in self._eps:
            if ep.sid == sid:
                return ep
        return None


_dispatcher = None
_request_sid = ContextVar("mrelay_request_sid", default="")


def install(cfg):
    global _dispatcher

    ids = cfg.get("script_ids") or []
    if not ids and cfg.get("script_id"):
        ids = [cfg["script_id"]]
    ids = [s.strip() for s in ids if s and s != "YOUR_APPS_SCRIPT_DEPLOYMENT_ID"]

    if len(ids) <= 1:
        _dispatcher = None
        return

    _dispatcher = MultiIdDispatcher(
        ids,
        fail_threshold=int(cfg.get("multi_id_fail_threshold", 3)),
        cooldown=float(cfg.get("multi_id_cooldown_seconds", 30.0)),
    )

    import domain_fronter
    DF = domain_fronter.DomainFronter
    if getattr(DF, "_mrelay_multiid_patched", False):
        return
    DF._mrelay_multiid_patched = True

    orig_next = DF._next_script_id

    def next_script_id_wrapped(self):
        if _dispatcher is None:
            return orig_next(self)
        sid = _dispatcher.next_id()
        _request_sid.set(sid or "")
        return sid if sid else orig_next(self)

    DF._next_script_id = next_script_id_wrapped
    log.info("smart multi-ID routing enabled (%d endpoints)", len(ids))


def report_ok(sid, latency_ms=None):
    if _dispatcher is not None:
        _dispatcher.report_ok(sid, latency_ms)


def report_err(sid, msg=""):
    if _dispatcher is not None:
        _dispatcher.report_err(sid, msg)


def current_health():
    return _dispatcher.health() if _dispatcher else "good"


def endpoint_count():
    return _dispatcher.count if _dispatcher else 0


def snapshot():
    return _dispatcher.snapshot() if _dispatcher else {
        "endpoints": 0,
        "endpoints_healthy": 0,
        "latency_ms": 0.0,
        "success_rate": 1.0,
        "active_endpoint": "",
    }


def current_request_id():
    return _request_sid.get("")


def _short(sid):
    if not sid:
        return ""
    return "..." + sid[-8:] if len(sid) > 12 else sid
