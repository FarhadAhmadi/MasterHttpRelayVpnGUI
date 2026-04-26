# MasterRelayVPN

Windows desktop GUI on top of [masterking32/MasterHttpRelayVPN](https://github.com/masterking32/MasterHttpRelayVPN). Bundles the Python relay engine and a WPF (.NET 8) frontend into a single redistributable folder.

## Build

One command, from the repo root:

```powershell
build\build.ps1
```

Produces:

```
release\
├── MasterRelayVPN\          ready-to-ship folder
│   ├── MasterRelayVPN.exe       (~69 MB, .NET 8 self-contained)
│   ├── core\
│   │   └── MasterRelayCore.exe  (~28 MB, Python embedded via PyInstaller)
│   ├── data\
│   │   ├── cert\
│   │   └── logs\
│   └── README.txt
└── MasterRelayVPN.zip       ~91 MB
```

The script:
1. Verifies `python` and `dotnet` are on PATH.
2. Creates `_pybuild\.venv` if missing, installs pinned deps from `requirements.txt`.
3. Stages `src\*.py` + `core\*.py` into `_pybuild\stage\`, runs PyInstaller against `build\MasterRelayCore.spec`.
4. Runs `dotnet publish` for the WPF GUI (single-file, self-contained, win-x64).
5. Copies both exes into `release\MasterRelayVPN\`, creates `data\cert\` and `data\logs\`.
6. **Self-test**: invokes the bundled core to (1) generate the CA, (2) verify it persists across runs, (3) start the proxy and confirm it accepts TCP within 3 s. Aborts the build if any test fails.
7. Zips the result into `release\MasterRelayVPN.zip`.

### Flags

```powershell
build\build.ps1 -Clean       # wipe _pybuild\ and release\ first
build\build.ps1 -SkipGui     # core only — useful for CI on non-Windows
build\build.ps1 -NoSelfTest  # skip the post-build smoke test
```

### Prerequisites (build host only)

- Windows 10 / 11, x64
- Python 3.10–3.13 on PATH
- .NET 8 SDK Desktop on PATH
- Visual C++ Build Tools (only needed if PyPI doesn't have a `cryptography` wheel for your Python)

End users need none of the above — both binaries in the release folder are self-contained.

## Layout

```
core\        bridge files we maintain (ca_path, stats, log_filter,
             net_patches, gui_bridge, main_gui)
src\         pristine upstream Python files
gui\         C# WPF (.NET 8)
build\       build.ps1, build.bat, clean.ps1, MasterRelayCore.spec, installer.iss
docs\        BUILD.md, ARCHITECTURE.md, QA.md, CHECKLIST.md, RECOVERY.md
requirements.txt
```

## Troubleshooting

See [docs/RECOVERY.md](docs/RECOVERY.md) for fixes to common build failures.

For the architecture diagram and IPC protocol, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
