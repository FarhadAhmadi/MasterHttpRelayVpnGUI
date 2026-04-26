# Failure recovery

If `build\build.ps1` fails, find the matching scenario below and follow the fix.

## "python not found on PATH" / "dotnet not found on PATH"
Prerequisite missing.
- Python 3.10–3.13 from <https://www.python.org/downloads/>. **Tick "Add to PATH"** during install.
- .NET 8 Desktop SDK from <https://dotnet.microsoft.com/download/dotnet/8.0>.

Then open a new PowerShell window so PATH is reloaded, and re-run `build\build.ps1`.

## "venv creation failed" / "pip install failed"
- Open `_pybuild\.venv` and check whether it's a partial install. If yes, run `build\clean.ps1` and retry.
- Some corporate networks block PyPI. Test with `python -m pip install --upgrade pip`. If that fails too, fix proxy settings (`HTTP_PROXY`, `HTTPS_PROXY`).
- On Python 3.13, `cryptography` needs Visual C++ Build Tools 2022 if no wheel is available. Install from <https://visualstudio.microsoft.com/visual-cpp-build-tools/> and retry.

## "PyInstaller failed"
- Look in `_pybuild\stage\build\MasterRelayCore\warn-MasterRelayCore.txt` for missing modules.
- The most common Python 3.13 case is `cryptography.hazmat.bindings._rust` — already covered by `collect_all("cryptography")` in `MasterRelayCore.spec`. If a different transitive dep is missing, add it to `MasterRelayCore.spec`'s `hiddenimports` list.
- Try a non-onefile build to debug: edit the spec, swap `EXE(...)` for the standard onedir form, rerun.

## "MasterRelayCore.exe not produced"
PyInstaller silently aborted. Re-run with `build\build.ps1 -Clean` to wipe `_pybuild\` and start fresh.

## "dotnet publish failed"
- Confirm the SDK version is 8.0+: `dotnet --version` should print `8.0.x`.
- If the error is `NETSDK1100`, you're not on Windows. The script requires a real Windows host; cross-compile only works with `-p:EnableWindowsTargeting=true` which is fine for compile but won't run the WPF parts.

## "MasterRelayVPN.exe not produced"
The publish probably failed with a missing icon or asset. Confirm `gui\Assets\app.ico` exists. If it doesn't, `git restore gui/Assets/app.ico` (or rebuild from `build\build.ps1 -Clean`).

## Self-test failures

### "[1/3] --gen-ca produces ca.crt + ca.key" failed
The bundled exe couldn't write to its own folder. This means antivirus or Defender quarantined `MasterRelayCore.exe`. Check Windows Security → Protection History.

### "[2/3] CA persists across runs" failed
The `core/ca_path.py` redirect didn't take effect. Confirm:
1. `core\ca_path.py` exists and ends with the `def install():` block.
2. `core\main_gui.py` calls `ca_path.install()` **before** the line that imports from `mitm`.

### "[3/3] core boots and accepts TCP" failed
Look at `_pybuild\selftest_err.log` — the script prints it on failure. Common causes:
- Port 18099 already in use → another instance is still running. Run `Stop-Process -Name MasterRelayCore -Force` and retry.
- A `ModuleNotFoundError` for one of the bridge files → confirm `core\*.py` was staged into `_pybuild\stage\` (the script lists what it staged).

## "zip not produced"
Most likely a long-path issue on Windows. The release folder contains nothing > 100 chars deep; if you renamed or relocated the repo into a deeply nested path, move it closer to the drive root.

## "No newer changes than last build, and the build still fails"
Run `build\build.ps1 -Clean` once. The `-Clean` flag wipes `_pybuild\` and `release\` so the next build starts fresh.

## Last-resort rebuild

```powershell
build\clean.ps1
build\build.ps1 -Clean
```
