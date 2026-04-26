# Building MasterRelayVPN

Produces a zero-setup Windows bundle.

## Prerequisites (build machine only)

- Windows 10 or 11, x64
- **Python 3.10–3.13** on PATH
- **.NET 8 SDK** (Desktop)
- **Git**
- **Visual C++ Build Tools** (PyInstaller needs them for `cryptography`)
- *(Optional)* Inno Setup 6 — only if you want a `.exe` installer

End users need none of these.

## One-shot build

From the repo root, in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

Or via batch wrapper:

```bat
build\build.bat
```

The script:
1. Clones `masterking32/MasterHttpRelayVPN` to `.\MasterHttpRelayVPN\` (first run only)
2. Drops the upstream Python files from `src\` and the bridge files from `core\` into it
3. Creates a venv, installs deps from `requirements.txt`, runs PyInstaller
4. Builds the WPF GUI with `dotnet publish` (single-file, self-contained)
5. Stages everything into `dist\MasterRelayVPN\`

### Selective rebuilds

```powershell
# only rebuild the GUI:
build\build.ps1 -SkipCore
# only rebuild the core:
build\build.ps1 -SkipGui
```

## Output layout

```
dist\MasterRelayVPN\
├── MasterRelayVPN.exe          ~34 MB  GUI (.NET 8 self-contained)
├── core\
│   └── MasterRelayCore.exe     ~28 MB  relay engine (Python embedded)
├── data\                       written on first launch
└── README.txt
```

Zip and ship. Runs on any clean Win10/Win11 x64 — no Python, no .NET, no VC++ runtimes.

## Make an installer

After the bundle is built:

```bat
iscc build\installer.iss
```

Output: `dist\MasterRelayVPN-Setup.exe`. Standard installer with desktop-shortcut option, no admin required.

## Single PyInstaller command (reference)

If you ever need to invoke it directly:

```powershell
pyinstaller --noconfirm --onefile --name MasterRelayCore `
    --hidden-import cryptography --hidden-import h2 `
    --hidden-import h2.connection --hidden-import h2.config `
    --hidden-import h2.events --hidden-import h2.settings `
    --collect-submodules cryptography --collect-submodules h2 `
    main_gui.py
```

## Troubleshooting

- **PyInstaller fails on `cryptography`** — install Visual C++ Build Tools, then `python -m pip install --upgrade setuptools wheel`.
- **`dotnet publish` slow on first run** — fine, second build is ~10s.
- **SmartScreen warning on the produced exe** — sign both binaries with `signtool` if you're going to distribute publicly.
- **Python 3.13 + cryptography wheel** — pinned in `requirements.txt` to a range with prebuilt wheels.
