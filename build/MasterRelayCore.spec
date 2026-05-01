# PyInstaller spec — single source of truth for the Python build.
#
# Produces:  _pybuild/dist/MasterRelayCore.exe (Windows: .exe; on POSIX: no extension)
#
# Why a .spec instead of CLI flags:
#   - cryptography on Python 3.13 ships compiled rust bindings whose dotted
#     names change between releases. collect_all() resolves them at build time
#     instead of relying on a brittle --hidden-import list.
#   - Same for h2 (uses hyperframe + hpack as transitive deps).
#   - Determinism: same spec + same lockfile + same Python = same output.

# -*- mode: python ; coding: utf-8 -*-

from PyInstaller.utils.hooks import collect_all

block_cipher = None

# Resolve every submodule, data file and binary the runtime needs.
crypto_datas, crypto_bins, crypto_hidden = collect_all("cryptography")
h2_datas,     h2_bins,     h2_hidden     = collect_all("h2")
hpack_datas,  hpack_bins,  hpack_hidden  = collect_all("hpack")
hframe_datas, hframe_bins, hframe_hidden = collect_all("hyperframe")

a = Analysis(
    ["main_gui.py"],
    pathex=["."],            # _pybuild/ is the cwd; everything is flat here
    binaries=crypto_bins + h2_bins + hpack_bins + hframe_bins,
    datas=crypto_datas + h2_datas + hpack_datas + hframe_datas,
    hiddenimports=(
        crypto_hidden + h2_hidden + hpack_hidden + hframe_hidden
        + [
            # Local modules — explicit so PyInstaller can't miss them.
            "ca_path", "stats", "log_filter", "net_patches", "gui_bridge",
            "cert_installer", "mitm", "proxy_server", "domain_fronter",
            "h2_transport", "google_ip_scanner", "lan_utils",
        ]
    ),
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=["tkinter", "pytest", "doctest", "unittest"],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name="MasterRelayCore",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,                # leave compression off — easier debugging
    upx_exclude=[],
    runtime_tmpdir=None,
    console=True,             # need stdout/stderr for the GUI's IPC
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
