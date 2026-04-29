# MasterRelayVPN 1.5.0 Release

Release date: 2026-04-29

## Why this release exists

This release focuses on four user-visible goals:

1. reliability (no exit freezes),
2. performance (lower UI and background overhead),
3. robust multi-relay routing (better rate-limit avoidance),
4. richer UX (new operational tabs and telemetry).

The changes were intentionally scoped to improve runtime behavior without
breaking existing config compatibility (`script_id` is still supported).

## What changed

### 1) Exit and shutdown reliability

- Switched GUI shutdown to async flow to avoid UI-thread blocking.
- Removed synchronous waits (`.Wait()`) from close path.
- Hardened core process stop sequence:
  - signal/close stdin,
  - early cancel stream readers,
  - graceful wait,
  - soft close attempt,
  - forced tree kill fallback.

Result: far fewer "app freezes on exit" scenarios.

### 1.1) System proxy safety on close

- Added full proxy state capture/restore for the current Windows user profile.
- When app-managed proxy is enabled, previous values are restored on:
  - normal stop,
  - unexpected process exit handling,
  - window/application shutdown.
- Restored values include `ProxyEnable`, `ProxyServer`, `ProxyOverride`,
  and `AutoConfigURL`.

Result: users return to their previous network proxy state after closing the
app, avoiding accidental connectivity issues.

### 2) Performance upgrades

- Added batched log ingestion in GUI:
  - logs are queued from process reader threads,
  - flushed on dispatcher timer in bounded chunks.
- Added adaptive stats emitter in core:
  - faster cadence when active,
  - lower cadence when idle.

Result: lower UI jitter under heavy logging and reduced idle CPU wakeups.

### 3) Multi-relay settings + smarter relay balancing

- Fixed relay ID persistence regression:
  - relay IDs are now persisted immediately and on shutdown.
- Added advanced multi-relay settings:
  - `multi_id_strategy`: `balanced | round_robin | least_used`
  - `multi_id_fail_threshold`
  - `multi_id_cooldown_seconds`
  - `multi_id_max_consecutive`
- Extended dispatcher logic:
  - strategy-based endpoint selection,
  - consecutive-hit cap to force spread,
  - park/cooldown for failing relays,
  - per-relay details in runtime snapshot.

Result: better request distribution across relay IDs and improved behavior
when individual relays are throttled or unstable.

### 4) UI/UX modernization

- Replaced old diagnostics/log split with tabbed operational workspace:
  - `Overview`
  - `Relays`
  - `Software`
  - `Activity`
- Added overview mini-trends (ASCII sparkline style):
  - throughput trend,
  - latency trend.
- Added relay telemetry table:
  - relay ID, ok/err, latency, uses, recent failures, parked time.
- Added software metadata panel:
  - app version, config path, core path, data path.

Result: faster operator understanding and better troubleshooting visibility.

## Compatibility and migration notes

- Existing configs remain valid.
- `script_id` is still read for backward compatibility.
- `script_ids` remains the preferred multi-relay input.
- New multi-relay fields have safe defaults and clamping.

## Release structure (expected after build)

```
release/
  MasterRelayVPN/
    MasterRelayVPN.exe
    core/
      MasterRelayCore.exe
    data/
      cert/
      logs/
    README.txt
    exe-details.txt
    exe-details.json
    how-to-run.txt
  MasterRelayVPN.zip
```

## Build and validation command

Use:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-build-exes.ps1 -Clean
```

This command validates prerequisites, builds both executables, runs self-tests
(unless disabled), and writes full executable details (paths, size, hash, etc.).

## Key files touched in this release

- `gui/Views/MainWindow.xaml`
- `gui/Views/MainWindow.xaml.cs`
- `gui/ViewModels/MainViewModel.cs`
- `gui/Services/CoreProcessHost.cs`
- `gui/Models/AppConfig.cs`
- `gui/Models/Stats.cs`
- `gui/MasterRelayVPN.csproj`
- `core/main_gui.py`
- `core/gui_bridge.py`
- `core/multi_id.py`
- `scripts/validate-build-exes.ps1`
