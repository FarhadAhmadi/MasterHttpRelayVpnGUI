# Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│                      MasterRelayVPN.exe (WPF, 34 MB)               │
│  Views/MainWindow ── ViewModels/MainViewModel ── Services/         │
│       │              (INotifyPropertyChanged)    CoreProcessHost   │
│       │                                          ConfigService     │
│       │                                          CertInstall       │
│       │                                          ProxyToggle       │
│       │                                          FirstRun          │
└───────┼────────────────────────────────────────────────────────────┘
        │  spawn + monitor (stdin/stdout/stderr)
        ▼
┌────────────────────────────────────────────────────────────────────┐
│                MasterRelayCore.exe (PyInstaller, 28 MB)            │
│                                                                    │
│   main_gui.py                                                      │
│       └─ ca_path.install()  ← FIXES PyInstaller temp-dir CA bug    │
│       └─ gui_bridge.install(cfg)                                   │
│              └─ net_patches.apply(cfg)                             │
│                    forces ALPN http/1.1                            │
│                    fragments large writer.write() calls            │
│                    knocks down chunk_size/max_parallel             │
│              └─ patches ProxyServer to count bytes/conns/reqs      │
│              └─ starts ##STATS## emitter on stdout                 │
│       └─ asyncio.run(ProxyServer(cfg).start())                     │
│                                                                    │
│   Upstream code (untouched, lives in src/)                         │
│       proxy_server · domain_fronter · h2_transport · ws · mitm     │
└────────────────────────────────────────────────────────────────────┘
```

## IPC protocol

The C# `CoreProcessHost` reads two streams from the child process:

| Stream | Frame                                          | Purpose         |
|--------|------------------------------------------------|-----------------|
| stdout | `##STATS## { "bytes_up": ..., ... }\n`         | Dashboard feed  |
| stderr | `HH:MM:SS [Module] LEVEL message\n`            | Log panel       |

Both readers run on background threads and marshal to the WPF dispatcher.

## Source layout

```
MasterRelayVPN/
├── core/                   bridge files added on top of upstream
│   ├── ca_path.py          fixes the PyInstaller CA-temp-dir bug
│   ├── stats.py            counters
│   ├── log_filter.py       dedupe + tag
│   ├── net_patches.py      ALPN/HTTP1.1, fragmentation, defaults
│   ├── gui_bridge.py       wires everything together
│   └── main_gui.py         entrypoint
├── src/                    pristine upstream files (drop-in)
│   ├── cert_installer.py
│   ├── domain_fronter.py
│   ├── h2_transport.py
│   ├── mitm.py
│   ├── proxy_server.py
│   └── ws.py
├── gui/                    C# WPF .NET 8 project
│   ├── App.xaml{,.cs}
│   ├── Views/MainWindow.xaml{,.cs}
│   ├── ViewModels/         MainViewModel, RelayCommand, ObservableBase
│   ├── Services/           CoreProcessHost, ConfigService, CertInstall,
│   │                       ProxyToggle, FirstRun, Paths, Converters,
│   │                       ErrorMessages
│   ├── Models/             AppConfig, Stats, LogEntry
│   ├── Themes/Navy.xaml    medium-navy SaaS theme
│   └── Assets/app.ico
├── build/
│   ├── build.ps1           one-shot Windows build
│   ├── build.bat           wrapper
│   └── installer.iss       Inno Setup script
├── docs/                   BUILD.md, README.txt, this file
├── requirements.txt
└── README.md
```

## Why three Python directories?

- `core/` — five small bridge files that we maintain. They don't import each other beyond `stats`/`net_patches`/`log_filter`/`gui_bridge`/`ca_path`.
- `src/` — pristine copy of the upstream relay. Build script copies them into `MasterHttpRelayVPN/` before running PyInstaller.
- `MasterHttpRelayVPN/` — appears at build time only (`build.ps1` clones it). Never edit here; the venv lives inside it for build-time isolation.

Result: at PyInstaller time, the import root is unambiguous — every module is in one place, with no `sys.path` hacks at runtime.
