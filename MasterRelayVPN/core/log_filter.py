"""Collapse log spam, tag known errors with short prefixes."""

import logging
import re
import threading
import time

_TAGS = (
    ("TLS_FAIL",   re.compile(r"(TLS|SSL|handshake|certificate|ALPN)", re.I)),
    ("CONN_RESET", re.compile(r"(reset by peer|broken pipe|ConnectionResetError|EPIPE|ECONNRESET)", re.I)),
    ("TIMEOUT",    re.compile(r"(timeout|timed out|TimeoutError)", re.I)),
    ("RELAY_FAIL", re.compile(r"(relay|502|bad gateway|UrlFetch)", re.I)),
)


class DedupeFilter(logging.Filter):
    def __init__(self, window=2.0):
        super().__init__()
        self.window = window
        self._key = None
        self._t = 0.0
        self._dupes = 0
        self._lock = threading.Lock()

    def filter(self, record):
        msg = str(record.msg)
        for tag, rx in _TAGS:
            if rx.search(msg):
                if not msg.startswith(f"[{tag}]"):
                    record.msg = f"[{tag}] {msg}"
                break

        key = (record.levelno, record.name, str(record.msg))
        now = time.monotonic()
        with self._lock:
            if key == self._key and (now - self._t) < self.window:
                self._dupes += 1
                self._t = now
                return False

            if self._dupes > 0 and self._key is not None:
                lvl, name, prev_msg = self._key
                summary = logging.LogRecord(
                    name=name, level=lvl, pathname="", lineno=0,
                    msg=f"{prev_msg}  (x{self._dupes + 1})",
                    args=None, exc_info=None,
                )
                for h in logging.getLogger().handlers:
                    if summary.levelno >= h.level:
                        try: h.handle(summary)
                        except Exception: pass
                self._dupes = 0

            self._key = key
            self._t = now
        return True


def install(window=2.0):
    root = logging.getLogger()
    if any(isinstance(f, DedupeFilter) for f in root.filters):
        return
    root.addFilter(DedupeFilter(window=window))
