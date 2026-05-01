#!/usr/bin/env python3
"""GUI-aware entrypoint for MasterRelayCore."""

import argparse
import asyncio
import json
import logging
import os
import signal
import sys

# CA path redirect MUST come before any mitm/cert_installer imports.
import ca_path
ca_path.install()

from cert_installer import install_ca, is_ca_trusted  # noqa: E402
from google_ip_scanner import scan_sync               # noqa: E402
from lan_utils import log_lan_access                  # noqa: E402
from mitm import CA_CERT_FILE, MITMCertManager        # noqa: E402
from proxy_server import ProxyServer                  # noqa: E402

import gui_bridge  # noqa: E402

__version__ = "1.5.0-gui"

_PLACEHOLDER_AUTH_KEYS = {
}


def load_config(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        sys.stderr.write(f"Config not found: {path}\n")
        sys.exit(2)
    except json.JSONDecodeError as e:
        sys.stderr.write(f"Invalid JSON in config: {e}\n")
        sys.exit(2)


def validate(cfg):
    if "auth_key" not in cfg:
        sys.stderr.write("Missing required config key: auth_key\n")
        sys.exit(2)
    if cfg.get("auth_key", "") in _PLACEHOLDER_AUTH_KEYS:
        sys.stderr.write("Refusing to start with a placeholder auth_key\n")
        sys.exit(2)
    if cfg.get("mode") == "apps_script":
        sid = cfg.get("script_ids") or cfg.get("script_id")
        if not sid or sid == "YOUR_APPS_SCRIPT_DEPLOYMENT_ID":
            sys.stderr.write("apps_script mode needs a valid script_id\n")
            sys.exit(2)


def ensure_ca():
    if not os.path.exists(CA_CERT_FILE):
        MITMCertManager()


async def run(cfg):
    server = ProxyServer(cfg)
    try:
        await server.start()
    finally:
        await server.stop()


def _apply_env_overrides(cfg: dict) -> None:
    if os.environ.get("DFT_AUTH_KEY"):
        cfg["auth_key"] = os.environ["DFT_AUTH_KEY"]
    if os.environ.get("DFT_SCRIPT_ID"):
        cfg["script_id"] = os.environ["DFT_SCRIPT_ID"]
    if os.environ.get("DFT_PORT"):
        cfg["listen_port"] = int(os.environ["DFT_PORT"])
    if os.environ.get("DFT_HOST"):
        cfg["listen_host"] = os.environ["DFT_HOST"]
    if os.environ.get("DFT_SOCKS5_PORT"):
        cfg["socks5_port"] = int(os.environ["DFT_SOCKS5_PORT"])
    if os.environ.get("DFT_LOG_LEVEL"):
        cfg["log_level"] = os.environ["DFT_LOG_LEVEL"]


def _make_exception_handler(log):
    def handler(loop, context):
        exc = context.get("exception")
        cb = context.get("handle") or context.get("source_traceback", "")
        if isinstance(exc, ConnectionResetError) and "_call_connection_lost" in str(cb):
            return
        log.error("[asyncio] %s", context.get("message", context))
        if exc:
            loop.default_exception_handler(context)
    return handler


def main():
    ap = argparse.ArgumentParser(prog="mrvpn-core")
    ap.add_argument("-c", "--config", default="config.json")
    ap.add_argument("--gen-ca", action="store_true",
                    help="Generate CA files (if missing) and exit.")
    ap.add_argument("--print-ca-path", action="store_true")
    ap.add_argument("--install-cert", action="store_true")
    ap.add_argument("--no-cert-check", action="store_true")
    ap.add_argument("--scan", action="store_true",
                    help="Scan Google IPs and print the best reachable one.")
    ap.add_argument("--version", action="version", version=__version__)
    args = ap.parse_args()

    if args.print_ca_path:
        print(CA_CERT_FILE); return

    if args.gen_ca:
        ensure_ca()
        print(CA_CERT_FILE); return

    cfg = load_config(args.config)
    _apply_env_overrides(cfg)
    validate(cfg)

    gui_bridge.configure_logging(cfg.get("log_level", "INFO"))
    log = logging.getLogger("Main")

    if cfg.get("mode") == "apps_script":
        ensure_ca()

    if args.install_cert:
        ok = install_ca(CA_CERT_FILE)
        sys.exit(0 if ok else 1)

    if args.scan:
        front_domain = cfg.get("front_domain", "www.google.com")
        log.info("Scanning Google IPs (fronting domain: %s)", front_domain)
        ok = scan_sync(front_domain)
        sys.exit(0 if ok else 1)

    gui_bridge.install(cfg)

    log.info("MasterRelay Core %s starting (mode=%s)", __version__, cfg.get("mode"))
    log.info("listen %s:%d  http2=%s chunked=%s frag=%s",
             cfg.get("listen_host", "127.0.0.1"),
             cfg.get("listen_port", 8085),
             cfg.get("enable_http2", False),
             cfg.get("enable_chunked", True),
             cfg.get("fragment_size", 16384))
    log.info("CA dir: %s", os.path.dirname(CA_CERT_FILE))

    if cfg.get("mode") == "apps_script" and not args.no_cert_check:
        if not is_ca_trusted(CA_CERT_FILE):
            log.warning("MITM CA is not trusted - attempting automatic installation")
            ok = install_ca(CA_CERT_FILE)
            if ok:
                log.info("CA certificate installed")
            else:
                log.warning("Auto-install failed; run with --install-cert")
        else:
            log.info("MITM CA is already trusted")

    lan_sharing = cfg.get("lan_sharing", False)
    listen_host = cfg.get("listen_host", "127.0.0.1")
    if lan_sharing and listen_host == "127.0.0.1":
        cfg["listen_host"] = "0.0.0.0"
        listen_host = "0.0.0.0"
        log.info("LAN sharing enabled - listening on all interfaces")
    if lan_sharing or listen_host in ("0.0.0.0", "::"):
        socks_port = cfg.get("socks5_port", 1080) if cfg.get("socks5_enabled", True) else None
        log_lan_access(cfg.get("listen_port", 8085), socks_port)

    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    loop.set_exception_handler(_make_exception_handler(logging.getLogger("asyncio")))
    stop = asyncio.Event()

    def _stop(*_):
        try: loop.call_soon_threadsafe(stop.set)
        except Exception: pass

    try:
        signal.signal(signal.SIGINT, _stop)
        signal.signal(signal.SIGTERM, _stop)
        if hasattr(signal, "SIGBREAK"):
            signal.signal(signal.SIGBREAK, _stop)
    except Exception:
        pass

    async def runner():
        srv = asyncio.create_task(run(cfg))
        st = asyncio.create_task(stop.wait())
        done, pending = await asyncio.wait({srv, st}, return_when=asyncio.FIRST_COMPLETED)
        for t in pending:
            t.cancel()
            try:
                await t
            except (asyncio.CancelledError, Exception):
                pass
        # Cancel any tasks the proxy spawned (per-client handlers)
        for t in [x for x in asyncio.all_tasks(loop) if x is not asyncio.current_task()]:
            t.cancel()
        await asyncio.sleep(0)
        log.info("Core stopped")

    try:
        loop.run_until_complete(runner())
    except KeyboardInterrupt:
        pass
    finally:
        loop.close()


if __name__ == "__main__":
    main()
