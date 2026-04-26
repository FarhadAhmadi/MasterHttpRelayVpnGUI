# Production Checklist

## Build environment

- [ ] Python 3.10–3.13 on PATH (`python --version`)
- [ ] .NET 8 SDK Desktop (`dotnet --list-sdks`)
- [ ] Git (`git --version`)
- [ ] Visual C++ Build Tools 2022 installed
- [ ] Optional: Inno Setup 6 if you want an installer

## Build

- [ ] `build\build.ps1` runs to completion with no red output
- [ ] `dist\MasterRelayVPN\MasterRelayVPN.exe` exists and is ≥ 30 MB
- [ ] `dist\MasterRelayVPN\core\MasterRelayCore.exe` exists and is ≥ 20 MB
- [ ] `dist\MasterRelayVPN\data\` exists

## Smoke run on the dev machine

- [ ] Double-click `MasterRelayVPN.exe`
- [ ] Window appears with navy theme
- [ ] After ~2 s the "Setting things up..." overlay disappears
- [ ] `data\config.json` was written with safe defaults
- [ ] `data\cert\ca.crt` was generated
- [ ] Cert badge near the hero button reads `Trusted` (or `Not Trusted` if you cancelled the OS dialog)
- [ ] Click Start — status flips to Connecting then Running
- [ ] Stats cards start updating
- [ ] Click Stop — status returns to Disconnected; system proxy is removed

## Smoke run on a fresh Windows machine

- [ ] Copy `dist\MasterRelayVPN\` to a clean machine with no Python or .NET installed
- [ ] Run `MasterRelayVPN.exe` — opens with no missing-DLL errors
- [ ] First-run setup completes
- [ ] Start works, stats update
- [ ] Open `chrome://net-internals` in Chrome and confirm requests route through `127.0.0.1:8085`
- [ ] Download a >50 MB file via the proxy — completes without stalling
- [ ] Stop and verify proxy setting is restored

## Settings panel

- [ ] Settings opens, all fields populated from `config.json`
- [ ] Editing a field and clicking Done persists the change to `data\config.json`
- [ ] `EnableHttp2` checkbox is OFF by default
- [ ] Fragment / Chunk / Parallel inputs accept numbers and clamp to safe ranges on Done

## Edge cases

- [ ] Delete `data\config.json` while stopped — next launch recreates it
- [ ] Delete `data\cert\` while stopped — next Start regenerates and re-trusts
- [ ] Edit `data\config.json` to invalid JSON — app starts, falls back to defaults, logs a warning
- [ ] Click Start with port 8085 already in use — UserMessage shows "Listen port is in use" within 1 s
- [ ] Close the window while running — core process exits cleanly (visible in Task Manager)

## Distribution

- [ ] Both EXEs signed with `signtool` (production releases only)
- [ ] Zip of `dist\MasterRelayVPN\` opens cleanly
- [ ] Optional: `iscc build\installer.iss` produces `dist\MasterRelayVPN-Setup.exe`
- [ ] Installer runs without admin rights and creates working app
