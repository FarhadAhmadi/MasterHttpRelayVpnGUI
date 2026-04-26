"""
Multi-deployment-id dispatcher.

Reads `script_ids: []` from config and rotates through them on every
relay() call. Sticky failure tracking: when one ID returns N consecutive
errors, it's parked for `cooldown` seconds and traffic moves to the
healthy ones. When all IDs are parked, the oldest park expires early so
the relay can still try.

Backwards compatible: if `script_ids` is missing or a single string, we
do nothing — DomainFronter keeps its single-ID behavior.
"""

import logging
import threading
import time

log = logging.getLogger("MultiID")


class _Endpoint:
    __slots__ = ("sid", "ok", "err", "parked_until", "last_err")

    def __init__(self, sid):
        self.sid = sid
        self.ok = 0
        self.err = 0
        self.parked_until = 0.0
        self.last_err = ""


class MultiIdDispatcher:
    def __init__(self, ids, fail_threshold=3, cooldown=30.0):
        self._lock = threading.Lock()
        self._eps = [_Endpoint(s) for s in ids]
        self._idx = 0
        self._fail_threshold = fail_threshold
        self._cooldown = cooldown

    @property
    def count(self):
        return len(self._eps)

    def next_id(self):
        """Round-robin to the next non-parked endpoint. Falls back to the
        least-recently-parked one if everything is parked."""
        now = time.monotonic()
        with self._lock:
            n = len(self._eps)
            if n == 0:
                return None
            for _ in range(n):
                ep = self._eps[self._idx]
                self._idx = (self._idx + 1) % n
                if ep.parked_until <= now:
                    return ep.sid
            # Everyone parked. Wake the one whose park expires soonest.
            ep = min(self._eps, key=lambda e: e.parked_until)
            ep.parked_until = 0.0
            log.warning("all deployment IDs parked; releasing %s early", ep.sid[:12])
            return ep.sid

    def report_ok(self, sid):
        with self._lock:
            for ep in self._eps:
                if ep.sid == sid:
                    ep.ok += 1
                    ep.err = 0
                    return

    def report_err(self, sid, msg=""):
        with self._lock:
            for ep in self._eps:
                if ep.sid == sid:
                    ep.err += 1
                    ep.last_err = msg
                    if ep.err >= self._fail_threshold:
                        ep.parked_until = time.monotonic() + self._cooldown
                        log.warning("parking %s for %ds (%d failures)",
                                    ep.sid[:12], int(self._cooldown), ep.err)
                    return

    def health(self):
        """Aggregate state used by the GUI's health pill.
        Returns one of 'good', 'unstable', 'down'."""
        now = time.monotonic()
        with self._lock:
            if not self._eps:
                return "good"
            live = [e for e in self._eps if e.parked_until <= now]
            if len(live) == len(self._eps):
                return "good"
            if not live:
                return "down"
            return "unstable"


_dispatcher = None


def install(cfg):
    """Patch DomainFronter to use the dispatcher. No-op for single-ID configs."""
    global _dispatcher

    ids = cfg.get("script_ids") or []
    if not ids and cfg.get("script_id"):
        ids = [cfg["script_id"]]
    ids = [s for s in ids if s and s != "YOUR_APPS_SCRIPT_DEPLOYMENT_ID"]

    if len(ids) <= 1:
        return  # nothing to round-robin between

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

    # Override the script_id property used at request-build time.
    # The upstream code reads self.script_id directly when assembling
    # the relay URL. We swap that read with a dynamic call.
    orig_init = DF.__init__

    def init_wrapped(self, config):
        orig_init(self, config)
        # Replace the static attribute with a property that hits the dispatcher.
        try:
            cls = type(self)
            if not hasattr(cls, "_mrelay_dyn_sid"):
                cls._mrelay_dyn_sid = True
                cls.script_id = property(_get_dyn_sid, _set_dyn_sid)
        except Exception as e:
            log.warning("could not install dynamic script_id: %s", e)

    DF.__init__ = init_wrapped
    log.info("multi-ID dispatch enabled (%d endpoints)", len(ids))


def _get_dyn_sid(self):
    if _dispatcher is None:
        return getattr(self, "_static_sid", "")
    sid = _dispatcher.next_id()
    return sid if sid else getattr(self, "_static_sid", "")


def _set_dyn_sid(self, value):
    self._static_sid = value


def report_ok(sid):
    if _dispatcher is not None:
        _dispatcher.report_ok(sid)


def report_err(sid, msg=""):
    if _dispatcher is not None:
        _dispatcher.report_err(sid, msg)


def current_health():
    return _dispatcher.health() if _dispatcher else "good"


def endpoint_count():
    return _dispatcher.count if _dispatcher else 0
