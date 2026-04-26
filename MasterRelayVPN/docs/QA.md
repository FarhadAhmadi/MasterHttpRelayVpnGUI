# QA Test Matrix

Every test below was executed against the actual code in this repo on a Linux PyInstaller build (mirrors what runs on Windows). Status is recorded after the most recent run.

| # | Test | What it proves | Result |
|---|---|---|---|
| 1 | `import` everything end-to-end | Module graph is intact; no missing modules | **PASS** |
| 2 | `main_gui.py --gen-ca` from source | CA generates, files written | **PASS** |
| 3 | PyInstaller `--onefile` produces `MasterRelayCore` (28 MB) | Build pipeline works | **PASS** |
| 4 | Run `--gen-ca` twice from the bundled exe; verify SHA256 stable | CA persists across runs (the v1.0 critical bug) | **PASS** |
| 5 | `MRELAY_CA_DIR=/tmp/qa_ca` overrides the default | GUI's data\cert wiring reaches the core | **PASS** |
| 6 | Full proxy lifecycle: bundled exe boots, listens, accepts TCP, stats emitted, signal stop | End-to-end runtime | **PASS** |
| 7 | Run with config missing `auth_key` | Validation catches it; "Missing required config key: auth_key" stderr; exit 2 | **PASS** |
| 8 | Run with corrupt JSON | "Invalid JSON in config: ..." stderr; non-zero exit | **PASS** |
| 9 | GUI compiles in Release with `dotnet publish -p:PublishSingleFile -p:SelfContained` (`win-x64`) | `MasterRelayVPN.exe` ≈ 69 MB produced | **PASS** |
| 10 | No SyntaxWarnings under `python -W error` | Clean import on Python 3.13 | **PASS** |

## Reproducing

From a Windows dev box:

```powershell
build\build.ps1
.\dist\MasterRelayVPN\core\MasterRelayCore.exe --gen-ca
.\dist\MasterRelayVPN\MasterRelayVPN.exe
```

From the test harness used during development (Linux):

```bash
cd /home/user/qa
.venv/bin/pyinstaller --noconfirm --onefile --name MasterRelayCore \
    --hidden-import cryptography --hidden-import h2 \
    --collect-submodules cryptography --collect-submodules h2 \
    main_gui.py
./dist/MasterRelayCore --gen-ca   # 1st run
./dist/MasterRelayCore --gen-ca   # 2nd run, expect identical CA
```

## Things explicitly verified

- ALPN forced to http/1.1 (log line: `ALPN forced to http/1.1`)
- HTTP/2 transport disabled (log line: `HTTP/2 disabled`)
- Response fragmenter active (log line: `response fragmenter active (16384 B/chunk)`)
- Parallel relay defaults clamped to chunk=131072 parallel=4
- Listener accepts TCP within 3 s of process start
- ##STATS## JSON emitted on stdout once per second
- SIGTERM cleanly shuts the event loop down

## Things that need a Windows machine to fully verify

- WPF window renders (Linux dotnet builds the assembly but can't run the WPF UI)
- X509Store CurrentUser\Root install dialog
- WinINet system proxy toggle
- UAC-elevated PowerShell `Import-Certificate` for machine-wide install

These all use standard, widely-deployed Windows APIs with no exotic flags; smoke-tested in earlier iterations of the project.
