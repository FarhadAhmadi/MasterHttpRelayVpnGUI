# MasterRelayVPN Operations Guide

This guide explains how the app is wired end-to-end and how to operate it safely and effectively.

## 1) Architecture at a glance

- GUI (`gui/`) is a WPF control plane for config, runtime controls, and diagnostics.
- Core (`core/`) starts and supervises the Python relay runtime.
- Relay engine (`src/`) runs:
  - local HTTP/SOCKS proxy,
  - optional MITM for HTTPS relay mode,
  - Apps Script forwarding (`Code.gs`) for censored paths.
- Data/config lives in `data/config.json` at runtime.

## 2) Request routing model

Routing is selected per destination using these policy layers:

1. `block_hosts` -> hard block (403).
2. `bypass_hosts` -> direct tunnel (no relay).
3. `no_mitm_hosts` / `no_mitm_cidrs` -> direct raw CONNECT (no TLS interception).
4. `force_relay_hosts` -> force Apps Script relay path.
5. `domain_routing_profiles` -> per-domain override map:
   - `auto`
   - `direct-only`
   - `relay-only`
   - `no-mitm`

For Telegram:
- desktop/DC traffic generally prefers no-MITM direct tunnel,
- Telegram Web can be routed through relay path if direct TLS is unstable.

## 3) Security model

### Relay authentication

- `AUTH_KEY` is required on every relay request.
- Optional signed mode adds HMAC fields: `ts`, `nonce`, `sig`, `v`.
- Enable signed mode in two phases:
  1. GUI: set `relay_signing_key`, toggle `relay_sign_requests=true`.
  2. `Code.gs`: set `REQUIRE_SIGNED_REQUESTS=true`.

### Replay protection

- Nonce cache via `CacheService`.
- Timestamp skew checks (`MAX_SKEW_SECONDS`).

### SSRF protection

- Relay rejects localhost/private IP targets.
- Relay can deny challenge/protected hosts for direct-only behavior.

## 4) Reliability model

### Adaptive retry + fallback

- Retryable relay errors are propagated as structured protocol errors.
- On repeated `RATE_LIMIT` per host:
  - host enters adaptive cooldown,
  - temporary direct fallback is preferred for tunnel traffic.
- Circuit breaker protects hosts with recurring relay failures.

### Batch handling

- Apps Script batch relay supports safe fallback when fetchAll fails.
- Unsafe methods are not replayed during batch fallback.

## 5) New health/telemetry endpoints

`Code.gs` health/status:

- `GET .../exec?health=1`
- `GET .../exec?status=1`

Returns JSON with:
- request totals,
- error buckets (auth/rate-limit/upstream),
- average latency,
- active limits.

## 6) GUI operations checklist

1. Configure relay IDs and `auth_key`.
2. Set domain profiles for strict domains (Cloudflare/challenge-heavy):
   - use `direct-only` where challenges fail in relay mode.
3. Enable signed relay requests after key rollout.
4. Watch live health card:
   - endpoint health,
   - active relay,
   - success rate / latency.
5. Export diagnostics when debugging.

## 7) Recommended profiles by use case

### A) Maximum compatibility browsing
- `direct-only` for challenge-heavy domains.
- Keep relay for blocked static/API domains only.

### B) Telegram Web in unstable networks
- keep Telegram desktop/DC no-MITM rules,
- allow Web Telegram relay fallback,
- keep retry/cooldown defaults.

### C) Security-first relay
- signed requests enabled and enforced,
- strong `AUTH_KEY` and `SIGNING_KEY`,
- keep private-IP blocking on.

## 8) Troubleshooting quick map

- `PR_END_OF_FILE_ERROR` in Firefox:
  - usually direct TLS path collapse; verify routing policy and fallback.
- Frequent 429 relay errors:
  - reduce burst load, add more script IDs, allow temporary direct fallback.
- Auth failures:
  - confirm `auth_key`, signing key parity, and clock skew.

## 9) Key files

- Relay logic: `src/proxy_server.py`, `src/domain_fronter.py`
- Apps Script backend: `src/Code.gs`
- GUI settings model: `gui/Models/AppConfig.cs`
- GUI bindings: `gui/ViewModels/MainViewModel.cs`
- GUI layout: `gui/Views/MainWindow.xaml`

