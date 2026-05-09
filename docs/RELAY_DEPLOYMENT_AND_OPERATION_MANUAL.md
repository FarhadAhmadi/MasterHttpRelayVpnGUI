# Relay Deployment and Operation Manual

This document is the practical, end-to-end guide for updating and operating the relay stack in this project, including the new `Code.gs` changes.

## 1. Do I need a new Apps Script project?

No. In almost all cases, you should **update your existing Apps Script project** and redeploy a new version.

Use a new project only if your current project is broken, lost, or permanently quota-blocked.

---

## 2. Safe rollout plan (recommended)

Follow this sequence to avoid downtime:

1. Update `Code.gs` in your existing Apps Script project.
2. Deploy a new version with `REQUIRE_SIGNED_REQUESTS = false`.
3. Test relay traffic from the app.
4. Configure signing in app config (`relay_sign_requests=true`, set key).
5. Enable `REQUIRE_SIGNED_REQUESTS = true` in `Code.gs`.
6. Deploy again and re-test.

---

## 3. Files involved in this project

- Apps Script backend source: `src/Code.gs`
- Python relay transport: `src/domain_fronter.py`
- Proxy and routing engine: `src/proxy_server.py`
- GUI config model: `gui/Models/AppConfig.cs`
- GUI settings bindings: `gui/ViewModels/MainViewModel.cs`
- GUI settings view: `gui/Views/MainWindow.xaml`
- Runtime config template: `release/MasterRelayVPN/data/config.json`

---

## 4. Updating Apps Script correctly

### Step-by-step

1. Open your existing Apps Script project.
2. Replace content with `src/Code.gs`.
3. Set secrets:
   - `AUTH_KEY`
   - `SIGNING_KEY`
4. Keep this flag for first rollout:
   - `REQUIRE_SIGNED_REQUESTS = false`
5. Deploy:
   - Deploy -> Manage deployments -> Edit deployment -> New version -> Deploy.

### Deployment ID notes

- If you edited the same deployment, deployment ID usually remains usable.
- If Google gives a different deployment ID, update app config (`script_id` / `script_ids`).

---

## 5. Enabling signed mode (HMAC)

### In GUI / config

Set:

- `relay_sign_requests = true`
- `relay_signing_key = "<same value as SIGNING_KEY in Code.gs>"`
- `relay_sign_version = 1`

### In Apps Script

After client signing is confirmed, set:

- `REQUIRE_SIGNED_REQUESTS = true`

Then redeploy.

### Verification checklist

- Requests succeed with signing on.
- No auth errors in logs.
- Time/date on system is correct (timestamp validation depends on it).

---

## 6. New health endpoint

The relay now exposes a health/status JSON on GET:

- `.../exec?health=1`
- `.../exec?status=1`

Includes:

- total/success/error request counters
- auth/rate-limit/upstream error counters
- average latency
- configured limiter values

Use this to verify deployment is live and to inspect relay behavior quickly.

---

## 7. Domain routing profiles in GUI

You can now define per-domain policy lines:

`host=profile`

Supported profiles:

- `auto` (remove explicit override)
- `direct-only`
- `relay-only`
- `no-mitm`
- `direct-bypass`
- `force-relay`

Examples:

- `web.telegram.org=relay-only`
- `.cloudflare.com=direct-only`
- `.telegram.org=no-mitm`

These map to internal routing lists and help keep difficult domains stable.

---

## 8. Adaptive rate-limit fallback behavior

If relay returns repeated `RATE_LIMIT` for a host:

- proxy tracks per-host streak,
- enters temporary direct-fallback cooldown for that host,
- cooldown grows adaptively (up to a max),
- host exits fallback after cooldown expires.

This reduces repeated failures and improves user experience during burst pressure.

---

## 9. Operational defaults (recommended)

- Keep Cloudflare/challenge-heavy domains on `direct-only`.
- Keep Telegram Desktop/DC traffic on `no-mitm` policy.
- Use relay for blocked web/API traffic where direct fails.
- Start with signing disabled, then enforce after validation.

---

## 10. Troubleshooting

### `PR_END_OF_FILE_ERROR` in Firefox

- Usually a direct TLS path issue under filtering/DPI.
- Try policy override:
  - `web.telegram.org=relay-only`

### Frequent `429` relay errors

- Add more script IDs (`script_ids`).
- Reduce request bursts.
- Rely on adaptive direct fallback.

### Auth/signature failures

- Ensure `relay_signing_key` exactly matches `SIGNING_KEY`.
- Ensure system clock is correct.
- Confirm `REQUIRE_SIGNED_REQUESTS` state matches client rollout phase.

---

## 11. Change management policy (team suggestion)

For every future relay change:

1. Update `src/Code.gs` in repo.
2. Deploy to a staging Apps Script deployment ID.
3. Validate health endpoint + app browsing tests.
4. Promote to production deployment.
5. Export diagnostics bundle and keep with release notes.

---

## 12. Related docs

- Architecture overview: `docs/ARCHITECTURE.md`
- Operations overview: `docs/APP_OPERATIONS_GUIDE.md`

