using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using MasterRelayVPN.Models;
using MasterRelayVPN.Services;

namespace MasterRelayVPN.ViewModels;

public class MainViewModel : ObservableBase
{
    readonly ConfigService _cfgSvc = new();
    readonly CoreProcessHost _core = new();
    readonly FirstRunService _firstRun = new();
    readonly HealthMonitorService _healthMonitor = new();
    readonly AutoOptimizer _autoTuner;
    readonly Timer _clockTimer;
    readonly DispatcherTimer _logFlushTimer;
    readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    readonly Queue<double> _latencyTrend = new();
    readonly Queue<double> _throughputTrend = new();
    bool _deploymentHandlersAttached;
    bool _suspendDeploymentPersist;
    ProxyToggleService.ProxyState? _previousProxyState;
    bool _proxyManagedByApp;
    int _shutdownStarted;

    const int MaxLogLines = 500;
    public ObservableCollection<LogEntry> Logs { get; } = new();
    public ObservableCollection<RelayEndpointDetail> RelayDetails { get; } = new();
    public ObservableCollection<DeploymentEntry> DeploymentIds { get; } = new();
    public ICollectionView LogsView { get; }
    public MasterRelayVPN.Services.Localization Loc => MasterRelayVPN.Services.Localization.Instance;

    public MainViewModel()
    {
        LogsView = CollectionViewSource.GetDefaultView(Logs);
        LogsView.Filter = LogFilter;

        _autoTuner = new AutoOptimizer(
            getSnap: () => _last,
            apply: choice =>
            {
                FragmentSize = choice.FragmentSize;
                ChunkSize = choice.ChunkSize;
                MaxParallel = choice.MaxParallel;
                _cfgSvc.Save(_cfg);
                AddLog(LogLevel.Info, "auto",
                    $"tuned: fragment={choice.FragmentSize}, chunk={choice.ChunkSize}, parallel={choice.MaxParallel}");
            });

        _core.LogReceived += QueueLog;
        _core.StatsReceived += s => OnUi(() => OnStats(s));
        _core.StatusChanged += s => OnUi(() => Status = s);
        _core.ProcessExited += code => OnUi(() => OnExited(code));

        StartCmd        = new RelayCommand(async () => await StartAsync(), () => !IsRunning && !IsConnecting && !Busy);
        StopCmd         = new RelayCommand(async () => await StopAsync(),  () => IsRunning && !Busy);
        OpenSettingsCmd = new RelayCommand(() =>
        {
            DashboardSection = "settings";
            SettingsOpen = true;
            StatsOpen = false;
        });
        CloseSettingsCmd= new RelayCommand(SaveAndCloseSettings);
        OpenStatsCmd    = new RelayCommand(() =>
        {
            DashboardSection = "analytics";
            StatsOpen = true;
            SettingsOpen = false;
        });
        CloseStatsCmd   = new RelayCommand(() =>
        {
            StatsOpen = false;
            DashboardSection = "home";
        });
        ToggleDensityCmd = new RelayCommand(() => IsCompactDensity = !IsCompactDensity);
        SetStatsSectionCmd = new RelayCommand(p =>
        {
            StatsSection = (p as string) ?? "overview";
            if (StatsSection == "overview") StatsSubsection = "health";
            else if (StatsSection == "relays") StatsSubsection = "relay_health";
            else if (StatsSection == "support") StatsSubsection = "support_paths";
        });
        SetStatsSubsectionCmd = new RelayCommand(p => StatsSubsection = (p as string) ?? "health");
        SetDashboardSectionCmd = new RelayCommand(p =>
        {
            var s = (p as string) ?? "home";
            DashboardSection = s;
            SettingsOpen = s == "settings";
            StatsOpen = s == "analytics";
        });
        InstallCertCmd  = new RelayCommand(async () => await InstallCertAsync());
        InstallCertMachineCmd = new RelayCommand(async () => await InstallCertMachineAsync());
        ToggleSysProxyCmd = new RelayCommand(ToggleSysProxy);
        ClearLogsCmd    = new RelayCommand(() => Logs.Clear());
        CopyLogsCmd     = new RelayCommand(CopyLogs);
        ExportLogsCmd   = new RelayCommand(ExportLogs);
        ExportRelayStatusCmd = new RelayCommand(ExportRelayStatus);
        ExportDiagnosticsBundleCmd = new RelayCommand(ExportDiagnosticsBundle);
        CopySupportSummaryCmd = new RelayCommand(CopySupportSummary);
        CopyRelayStatusCmd = new RelayCommand(CopyRelayStatusToClipboard);
        CopyRuntimeSnapshotCmd = new RelayCommand(CopyRuntimeSnapshotToClipboard);
        AnalyzeAndRecommendCmd = new RelayCommand(AnalyzeAndRecommend);
        ToggleLanguageCmd = new RelayCommand(ToggleLanguage);

        AddDeploymentCmd    = new RelayCommand(AddDeployment);
        RemoveDeploymentCmd = new RelayCommand(p => RemoveDeployment(p as DeploymentEntry));
        ApplyPresetCmd      = new RelayCommand(p => ApplyPreset(p as string));

        Config = _cfgSvc.Load();
        Loc.Lang = string.IsNullOrWhiteSpace(_cfg.Language) ? "en" : _cfg.Language;
        SyncDeploymentList();
        SysProxyOn = ProxyToggleService.IsEnabled();
        Raise(nameof(SysProxyOn));
        Raise(nameof(SysProxyStateLabel));
        Raise(nameof(SysProxyActionLabel));
        RefreshCertStatus();

        _healthMonitor.Checked += r => OnUi(() => OnHealthChecked(r));
        _healthMonitor.Start(
            shouldCheck: () => _core.IsRunning,
            endpoint: () => (ListenHost, ListenPort));
        _clockTimer = new Timer(_ => OnUi(() => Raise(nameof(LastCheckLabel))),
            null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _logFlushTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Background,
            (_, __) => FlushPendingLogs(),
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
        _logFlushTimer.Start();
    }

    public async Task BootAsync()
    {
        Busy = true;
        BootStatus = Loc["setting_up"];
        try
        {
            var report = await _firstRun.RunAsync();
            Config = _cfgSvc.Load();
            Loc.Lang = _cfg.Language ?? "en";
            SyncDeploymentList();
            RefreshCertStatus();

            if (report.CertGenerated || report.CertTrusted)
                AddLog(LogLevel.Info, "setup",
                    report.CertTrusted ? "Certificate trusted." : "Certificate generated.");
            if (!string.IsNullOrEmpty(report.Message))
                AddLog(LogLevel.Warning, "setup", report.Message!);
        }
        catch (Exception ex) { AddLog(LogLevel.Error, "setup", ex.Message); }
        finally { BootStatus = ""; Busy = false; }
    }

    AppConfig _cfg = new();
    public AppConfig Config
    {
        get => _cfg;
        set { _cfg = value; RaiseAllConfigProps(); }
    }

    void RaiseAllConfigProps()
    {
        foreach (var n in new[]
        {
            nameof(Mode), nameof(FrontDomain), nameof(CustomSni), nameof(ScriptId),
            nameof(WorkerHost), nameof(CustomDomain), nameof(AuthKey), nameof(GoogleIp),
            nameof(ListenHost), nameof(ListenPort), nameof(LogLevelText), nameof(VerifySsl),
            nameof(EnableHttp2), nameof(EnableChunked), nameof(ChunkSize),
            nameof(MaxParallel), nameof(FragmentSize), nameof(ActivePreset),
            nameof(CacheEnabled), nameof(CacheMaxMb), nameof(CacheDefaultTtlS), nameof(CacheStaleIfErrorS),
            nameof(MultiIdFailThreshold), nameof(MultiIdCooldownSeconds),
            nameof(MultiIdStrategy), nameof(MultiIdMaxConsecutive),
            nameof(DirectBypassDomainsText),
        }) Raise(n);
    }

    public string Mode          { get => _cfg.Mode;          set { _cfg.Mode = value; Raise(); } }
    public string FrontDomain   { get => _cfg.FrontDomain ?? ""; set { _cfg.FrontDomain = value; Raise(); } }
    public string CustomSni     { get => _cfg.CustomSni ?? "";   set { _cfg.CustomSni = value; Raise(); } }
    public string ScriptId      { get => _cfg.ScriptId ?? "";    set { _cfg.ScriptId = value; Raise(); } }
    public string WorkerHost    { get => _cfg.WorkerHost ?? "";  set { _cfg.WorkerHost = value; Raise(); } }
    public string CustomDomain  { get => _cfg.CustomDomain ?? ""; set { _cfg.CustomDomain = value; Raise(); } }
    public string AuthKey       { get => _cfg.AuthKey;        set { _cfg.AuthKey = value; Raise(); } }
    public string GoogleIp      { get => _cfg.GoogleIp ?? ""; set { _cfg.GoogleIp = value; Raise(); } }
    public string ListenHost    { get => _cfg.ListenHost;     set { _cfg.ListenHost = value; Raise(); } }
    public int    ListenPort    { get => _cfg.ListenPort;     set { _cfg.ListenPort = value; Raise(); } }
    public string LogLevelText  { get => _cfg.LogLevel;       set { _cfg.LogLevel = value; Raise(); } }
    public bool   VerifySsl     { get => _cfg.VerifySsl;      set { _cfg.VerifySsl = value; Raise(); } }
    public bool   EnableHttp2   { get => _cfg.EnableHttp2;    set { _cfg.EnableHttp2 = value; Raise(); } }
    public bool   EnableChunked { get => _cfg.EnableChunked;  set { _cfg.EnableChunked = value; Raise(); } }
    public int    ChunkSize     { get => _cfg.ChunkSize;      set { _cfg.ChunkSize = value; Raise(); } }
    public int    MaxParallel   { get => _cfg.MaxParallel;    set { _cfg.MaxParallel = value; Raise(); } }
    public int    FragmentSize  { get => _cfg.FragmentSize;   set { _cfg.FragmentSize = value; Raise(); } }
    public bool   CacheEnabled  { get => _cfg.CacheEnabled;   set { _cfg.CacheEnabled = value; Raise(); } }
    public int    CacheMaxMb    { get => _cfg.CacheMaxMb;     set { _cfg.CacheMaxMb = value; Raise(); } }
    public int    CacheDefaultTtlS { get => _cfg.CacheDefaultTtlS; set { _cfg.CacheDefaultTtlS = value; Raise(); } }
    public int    CacheStaleIfErrorS { get => _cfg.CacheStaleIfErrorS; set { _cfg.CacheStaleIfErrorS = value; Raise(); } }
    public string ActivePreset  { get => _cfg.Preset; set { _cfg.Preset = value; Raise(); } }
    public int    MultiIdFailThreshold
    {
        get => _cfg.MultiIdFailThreshold;
        set { _cfg.MultiIdFailThreshold = value; Raise(); }
    }
    public int    MultiIdCooldownSeconds
    {
        get => _cfg.MultiIdCooldownSeconds;
        set { _cfg.MultiIdCooldownSeconds = value; Raise(); }
    }
    public string MultiIdStrategy
    {
        get => _cfg.MultiIdStrategy;
        set { _cfg.MultiIdStrategy = value; Raise(); }
    }
    public int    MultiIdMaxConsecutive
    {
        get => _cfg.MultiIdMaxConsecutive;
        set { _cfg.MultiIdMaxConsecutive = value; Raise(); }
    }
    public string DirectBypassDomainsText
    {
        get => string.Join(Environment.NewLine, _cfg.DirectBypassDomains ?? new List<string>());
        set
        {
            var vals = (value ?? "")
                .Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().TrimStart('.').ToLowerInvariant())
                .Where(x => x.Length > 0)
                .Distinct()
                .ToList();
            _cfg.DirectBypassDomains = vals;
            Raise();
        }
    }

    string _status = "Stopped";
    public string Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(StatusBrush)); Raise(nameof(StatusFriendly));
                Raise(nameof(IsRunning)); Raise(nameof(IsConnecting)); Raise(nameof(IsStopped));
                Raise(nameof(HeroLabel));
                RefreshCommands();
            }
        }
    }

    public string StatusFriendly => _status switch
    {
        "Running"    => Loc["connected"],
        "Stopped"    => Loc["disconnected"],
        "Connecting" => Loc["connecting"],
        "Error"      => Loc["connection_failed"],
        _ => _status,
    };

    public Brush StatusBrush => _status switch
    {
        "Running"    => (Brush)Application.Current.Resources["OkBrush"],
        "Stopped"    => (Brush)Application.Current.Resources["FgDimBrush"],
        "Connecting" => (Brush)Application.Current.Resources["WarnBrush"],
        _            => (Brush)Application.Current.Resources["DangerBrush"],
    };

    public bool IsRunning    => _core.IsRunning && _status == "Running";
    public bool IsConnecting => _core.IsRunning && _status == "Connecting";
    public bool IsStopped    => !_core.IsRunning;
    public string HeroLabel  => IsRunning ? Loc["stop"] : (IsConnecting ? "..." : Loc["start"]);

    bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RefreshCommands(); } }
    string _bootStatus = "";
    public string BootStatus { get => _bootStatus; set => Set(ref _bootStatus, value); }
    bool _settingsOpen;
    public bool SettingsOpen { get => _settingsOpen; set => Set(ref _settingsOpen, value); }
    bool _statsOpen;
    public bool StatsOpen { get => _statsOpen; set => Set(ref _statsOpen, value); }
    bool _isCompactDensity;
    public bool IsCompactDensity { get => _isCompactDensity; set => Set(ref _isCompactDensity, value); }
    string _dashboardSection = "home";
    public string DashboardSection
    {
        get => _dashboardSection;
        set
        {
            if (Set(ref _dashboardSection, value))
                Raise(nameof(DashboardBreadcrumb));
        }
    }
    string _statsSection = "overview";
    public string StatsSection { get => _statsSection; set => Set(ref _statsSection, value); }
    string _statsSubsection = "health";
    public string StatsSubsection
    {
        get => _statsSubsection;
        set
        {
            if (Set(ref _statsSubsection, value))
                Raise(nameof(DashboardBreadcrumb));
        }
    }
    public string DashboardBreadcrumb
    {
        get
        {
            if (DashboardSection == "analytics")
                return $"Dashboard / Analytics / {StatsSection} / {StatsSubsection}";
            if (DashboardSection == "settings")
                return "Dashboard / Settings";
            return "Dashboard / Home";
        }
    }
    string _userMessage = "";
    public string UserMessage { get => _userMessage; set => Set(ref _userMessage, value); }

    StatsSnapshot _last = new();
    public string SpeedDown => Human.PerSec(_last.SpeedDown);
    public string SpeedUp   => Human.PerSec(_last.SpeedUp);
    public string TotalDown => Human.Bytes(_last.BytesDown);
    public string TotalUp   => Human.Bytes(_last.BytesUp);
    public long   Requests    => _last.Requests;
    public int    Connections => _last.Connections;
    public string Uptime    => Human.Duration(_last.Uptime);
    public string LatencyLabel => _last.LatencyMs > 0
        ? $"{_last.LatencyMs:0} ms"
        : (_probeLatencyMs > 0 ? $"{_probeLatencyMs:0} ms" : "--");
    public string SuccessRateLabel => $"{Math.Clamp(_last.SuccessRate, 0, 1) * 100:0}%";
    public string WindowSuccessRateLabel => $"{Math.Clamp(_last.WindowSuccessRate, 0, 1) * 100:0}%";
    public string RequestsPerSecLabel => $"{_last.RequestsPerSec:0.0}/s";
    public string WindowErrorsLabel => _last.WindowErrors.ToString();
    public string WindowRequestsLabel => _last.WindowRequests.ToString();
    public string PeakConnectionsLabel => _last.PeakConnections.ToString();
    public string TotalTrafficLabel => Human.Bytes(_last.BytesUp + _last.BytesDown);
    public string CacheHitRateLabel => $"{Math.Clamp(_last.CacheHitRate, 0, 1) * 100:0}%";
    public string CacheEffectiveHitRateLabel => $"{Math.Clamp(_last.CacheEffectiveHitRate, 0, 1) * 100:0}%";
    public string CacheStaleHitsLabel => _last.CacheStaleHits.ToString();
    public string CacheSizeLabel => Human.Bytes(_last.CacheBytes);
    public string EndpointLabel => _last.Endpoints > 0
        ? $"{_last.EndpointsHealthy}/{_last.Endpoints}"
        : "--";
    public string ActiveEndpointLabel => string.IsNullOrWhiteSpace(_last.ActiveEndpoint)
        ? "--"
        : _last.ActiveEndpoint;
    public string RelayRoutingLabel => $"{MultiIdStrategy} (max streak: {MultiIdMaxConsecutive})";
    public string ConfiguredRelaysCountLabel
        => (_cfg.RelayItems?.Count ?? 0) > 0 ? (_cfg.RelayItems?.Count ?? 0).ToString() : "0";
    public string EnabledRelaysCountLabel
        => (_cfg.ScriptIds?.Count ?? 0) > 0 ? (_cfg.ScriptIds?.Count ?? 0).ToString() : "0";
    public string ConfiguredRelaysPreview
    {
        get
        {
            var ids = _cfg.ScriptIds ?? new System.Collections.Generic.List<string>();
            if (ids.Count == 0 && !string.IsNullOrWhiteSpace(_cfg.ScriptId))
                ids = new System.Collections.Generic.List<string> { _cfg.ScriptId };
            if (ids.Count == 0) return "No enabled relay IDs configured";
            return string.Join(Environment.NewLine, ids.Select(ShortRelayId));
        }
    }
    public string AppVersionLabel
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    public string CorePathLabel => Paths.CoreExe;
    public string ConfigPathLabel => Paths.ConfigFile;
    public string DataPathLabel => Paths.DataDir;
    public string ThroughputTrendLabel => BuildSparkline(_throughputTrend, scaleMax: 1024 * 1024);
    public string LatencyTrendLabel => BuildSparkline(_latencyTrend, scaleMax: 1500);

    // Health pill comes straight from the backend snapshot.
    public string Health      => _last.Health ?? "good";
    public string HealthLabel => Health switch
    {
        "good"     => Loc["health_good"],
        "unstable" => Loc["health_unstable"],
        "down"     => Loc["health_down"],
        _          => Loc["health_good"],
    };
    public Brush HealthBrush => Health switch
    {
        "good"     => (Brush)Application.Current.Resources["OkBrush"],
        "unstable" => (Brush)Application.Current.Resources["WarnBrush"],
        "down"     => (Brush)Application.Current.Resources["DangerBrush"],
        _          => (Brush)Application.Current.Resources["FgDimBrush"],
    };

    DateTime? _lastCheckAt;
    double _probeLatencyMs;
    string _diagnostics = "";
    string _lastRelayStatusJson = "";
    string _lastSupportSummary = "";
    DateTime? _sessionStartedAt;
    long _sessionStatsSamples;
    double _sessionLatencySum;
    double _sessionLatencyMax;
    double _sessionRpsSum;
    double _sessionRpsMax;
    double _sessionSuccessSum;
    public string LastCheckLabel
    {
        get
        {
            if (_lastCheckAt is null) return Loc["not_checked"];
            var age = DateTime.Now - _lastCheckAt.Value;
            if (age.TotalSeconds < 2) return Loc["checked_now"];
            if (age.TotalSeconds < 60) return string.Format(Loc["checked_seconds"], (int)age.TotalSeconds);
            return string.Format(Loc["checked_minutes"], (int)age.TotalMinutes);
        }
    }
    public string Diagnostics => string.IsNullOrWhiteSpace(_diagnostics)
        ? Loc["diagnostics_idle"]
        : _diagnostics;

    void OnStats(StatsSnapshot s)
    {
        _last = s;
        UpdateSessionMetrics(s);
        UpdateTrends(s);
        RelayDetails.Clear();
        if (s.EndpointsDetail != null)
        {
            foreach (var ep in s.EndpointsDetail.OrderByDescending(x => x.SuccessRate).ThenBy(x => x.LatencyMs))
                RelayDetails.Add(ep);
        }
        foreach (var n in new[]
        {
            nameof(SpeedDown), nameof(SpeedUp), nameof(TotalDown), nameof(TotalUp),
            nameof(Requests), nameof(Connections), nameof(Uptime),
            nameof(Health), nameof(HealthLabel), nameof(HealthBrush),
            nameof(LatencyLabel), nameof(SuccessRateLabel), nameof(RequestsPerSecLabel),
            nameof(WindowSuccessRateLabel), nameof(WindowErrorsLabel), nameof(WindowRequestsLabel),
            nameof(PeakConnectionsLabel), nameof(TotalTrafficLabel),
            nameof(CacheHitRateLabel), nameof(CacheEffectiveHitRateLabel), nameof(CacheStaleHitsLabel), nameof(CacheSizeLabel),
            nameof(EndpointLabel), nameof(ActiveEndpointLabel),
            nameof(RelayRoutingLabel), nameof(ConfiguredRelaysCountLabel),
            nameof(EnabledRelaysCountLabel),
            nameof(ThroughputTrendLabel), nameof(LatencyTrendLabel),
        }) Raise(n);
    }

    void OnHealthChecked(HealthCheckResult result)
    {
        _lastCheckAt = result.CheckedAt;
        _probeLatencyMs = result.Reachable ? result.LatencyMs : 0;
        _diagnostics = result.Reachable
            ? $"{Loc["diag_proxy_reachable"]} ({result.LatencyMs:0} ms)"
            : $"{Loc["diag_proxy_unreachable"]}: {result.Message}";

        Raise(nameof(LastCheckLabel));
        Raise(nameof(LatencyLabel));
        Raise(nameof(Diagnostics));
    }

    LogLevel _minLevel = LogLevel.Info;
    public LogLevel MinLevel
    {
        get => _minLevel;
        set { if (Set(ref _minLevel, value)) LogsView.Refresh(); }
    }
    string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set { if (Set(ref _filterText, value)) LogsView.Refresh(); }
    }
    bool LogFilter(object o)
    {
        if (o is not LogEntry e) return false;
        if ((int)e.Level < (int)_minLevel) return false;
        if (!string.IsNullOrEmpty(_filterText) &&
            e.Message.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) < 0 &&
            e.Source.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    string _certStatus = "Unknown";
    public string CertStatus
    {
        get => _certStatus;
        set { if (Set(ref _certStatus, value)) Raise(nameof(CertStatusBrush)); }
    }
    public Brush CertStatusBrush => _certStatus switch
    {
        "Trusted"     => (Brush)Application.Current.Resources["OkBrush"],
        "Not Trusted" => (Brush)Application.Current.Resources["WarnBrush"],
        "Missing"     => (Brush)Application.Current.Resources["DangerBrush"],
        _             => (Brush)Application.Current.Resources["FgDimBrush"]
    };

    void RefreshCertStatus()
    {
        try
        {
            if (!CertInstallService.CertExists()) { CertStatus = "Missing"; return; }
            CertStatus = CertInstallService.IsTrusted() ? "Trusted" : "Not Trusted";
        }
        catch { CertStatus = "Unknown"; }
    }

    bool _sysProxyOn;
    public bool SysProxyOn { get => _sysProxyOn; set => Set(ref _sysProxyOn, value); }
    public string SysProxyStateLabel => _sysProxyOn ? "System proxy is ON" : "System proxy is OFF";
    public string SysProxyActionLabel => _sysProxyOn ? "Disable System Proxy" : "Enable System Proxy";

    public RelayCommand StartCmd { get; }
    public RelayCommand StopCmd { get; }
    public RelayCommand OpenSettingsCmd { get; }
    public RelayCommand CloseSettingsCmd { get; }
    public RelayCommand OpenStatsCmd { get; }
    public RelayCommand CloseStatsCmd { get; }
    public RelayCommand ToggleDensityCmd { get; }
    public RelayCommand SetDashboardSectionCmd { get; }
    public RelayCommand SetStatsSectionCmd { get; }
    public RelayCommand SetStatsSubsectionCmd { get; }
    public RelayCommand InstallCertCmd { get; }
    public RelayCommand InstallCertMachineCmd { get; }
    public RelayCommand ToggleSysProxyCmd { get; }
    public RelayCommand ClearLogsCmd { get; }
    public RelayCommand CopyLogsCmd { get; }
    public RelayCommand ExportLogsCmd { get; }
    public RelayCommand ExportRelayStatusCmd { get; }
    public RelayCommand ExportDiagnosticsBundleCmd { get; }
    public RelayCommand CopySupportSummaryCmd { get; }
    public RelayCommand CopyRelayStatusCmd { get; }
    public RelayCommand CopyRuntimeSnapshotCmd { get; }
    public RelayCommand AnalyzeAndRecommendCmd { get; }
    public RelayCommand ToggleLanguageCmd { get; }
    public RelayCommand AddDeploymentCmd { get; }
    public RelayCommand RemoveDeploymentCmd { get; }
    public RelayCommand ApplyPresetCmd { get; }

    void ToggleLanguage()
    {
        Loc.Toggle();
        _cfg.Language = Loc.Lang;
        try { _cfgSvc.Save(_cfg); } catch { }
        // status text + hero label depend on locale
        Raise(nameof(StatusFriendly));
        Raise(nameof(HeroLabel));
        Raise(nameof(HealthLabel));
        Raise(nameof(LastCheckLabel));
        Raise(nameof(Diagnostics));
    }

    // Deployment IDs
    void SyncDeploymentList()
    {
        _suspendDeploymentPersist = true;
        try
        {
            DeploymentIds.Clear();
            var relays = (_cfg.RelayItems ?? new List<RelayConfigItem>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .ToList();
            if (relays.Count == 0)
            {
                var ids = _cfg.ScriptIds ?? new List<string>();
                if (ids.Count == 0 && !string.IsNullOrWhiteSpace(_cfg.ScriptId))
                    ids = new List<string> { _cfg.ScriptId };
                foreach (var sid in ids)
                    relays.Add(new RelayConfigItem { Id = sid, Enabled = true });
            }

            foreach (var relay in relays)
                DeploymentIds.Add(new DeploymentEntry { Value = relay.Id, IsEnabled = relay.Enabled });

            if (DeploymentIds.Count == 0)
                DeploymentIds.Add(new DeploymentEntry { Value = "", IsEnabled = true });
        }
        finally
        {
            _suspendDeploymentPersist = false;
        }

        if (!_deploymentHandlersAttached)
        {
            DeploymentIds.CollectionChanged += (_, __) => PersistDeployments();
            _deploymentHandlersAttached = true;
        }
        foreach (var d in DeploymentIds) d.PropertyChanged += (_, __) => PersistDeployments();
        Raise(nameof(ConfiguredRelaysCountLabel));
        Raise(nameof(EnabledRelaysCountLabel));
        Raise(nameof(ConfiguredRelaysPreview));
    }

    void AddDeployment()
    {
        var entry = new DeploymentEntry { Value = "", IsEnabled = true };
        entry.PropertyChanged += (_, __) => PersistDeployments();
        DeploymentIds.Add(entry);
    }

    void RemoveDeployment(DeploymentEntry? e)
    {
        if (e == null) return;
        DeploymentIds.Remove(e);
        if (DeploymentIds.Count == 0)
        {
            var entry = new DeploymentEntry { Value = "" };
            entry.PropertyChanged += (_, __) => PersistDeployments();
            DeploymentIds.Add(entry);
        }
    }

    void PersistDeployments()
    {
        if (_suspendDeploymentPersist) return;
        var relayItems = DeploymentIds
            .Select(d => new RelayConfigItem
            {
                Id = (d.Value ?? "").Trim(),
                Enabled = d.IsEnabled,
            })
            .Where(x => x.Id.Length > 0 && x.Id != "YOUR_APPS_SCRIPT_DEPLOYMENT_ID")
            .GroupBy(x => x.Id)
            .Select(g => g.First())
            .ToList();

        _cfg.RelayItems = relayItems;
        _cfg.ScriptIds = relayItems.Where(x => x.Enabled).Select(x => x.Id).ToList();
        // Keep `script_id` in sync for back-compat: first non-empty entry.
        _cfg.ScriptId = _cfg.ScriptIds.Count > 0 ? _cfg.ScriptIds[0] : "";
        SaveConfigSafe("deployments");
        Raise(nameof(ConfiguredRelaysCountLabel));
        Raise(nameof(EnabledRelaysCountLabel));
        Raise(nameof(ConfiguredRelaysPreview));
    }

    // Presets
    void ApplyPreset(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var p = Presets.ByKey(key!);
        Presets.ApplyTo(_cfg, p);
        RaiseAllConfigProps();
        AddLog(LogLevel.Info, "preset", $"applied {p.Key}");
    }

    async Task StartAsync()
    {
        try
        {
            ClampNetworkKnobs();
            PersistDeployments();
            SaveConfigSafe("start");

            if (_cfg.Mode == "apps_script" && !CertInstallService.CertExists())
            {
                BootStatus = Loc["preparing_cert"]; Busy = true;
                try { await _core.GenerateCaAsync(); } catch { }
                Busy = false; BootStatus = "";
                RefreshCertStatus();
                if (!CertInstallService.IsTrusted())
                {
                    var outcome = CertInstallService.InstallCurrentUser();
                    if (outcome.Result == CertResult.UserCancelled)
                    {
                        UserMessage = "Certificate is required for HTTPS. Open Settings to install it.";
                        return;
                    }
                    RefreshCertStatus();
                }
            }

            _core.Start();
            ResetRuntimeSession();
            UserMessage = "";
            AddLog(LogLevel.Info, "host", "Engine starting...");
            if (!SysProxyOn)
            {
                ToggleSysProxy();
                AddLog(LogLevel.Info, "sysproxy", "Auto-enabled system proxy because the tunnel started.");
            }

            if (ActivePreset == "auto") _autoTuner.Start();
        }
        catch (Exception ex)
        {
            Status = "Error";
            UserMessage = ErrorMessages.Friendly(ex.Message);
            AddLog(LogLevel.Error, "host", ex.Message);
        }
        finally { RefreshCommands(); }
    }

    async Task StopAsync()
    {
        _autoTuner.Stop();
        try
        {
            await _core.StopAsync(TimeSpan.FromSeconds(4));
            RestorePreviousProxySettings();
        }
        catch (Exception ex) { AddLog(LogLevel.Error, "host", "Stop failed: " + ex.Message); }
        RefreshCommands();
    }

    void OnExited(int code)
    {
        _autoTuner.Stop();
        if (code != 0)
        {
            var lastErr = Logs.LastOrDefault(l => l.Level == LogLevel.Error);
            UserMessage = lastErr != null
                ? ErrorMessages.Friendly(lastErr.Message)
                : "Connection failed. Try a different SNI or check Settings.";
        }
        RestorePreviousProxySettings();
        RefreshCommands();
    }

    void SaveAndCloseSettings()
    {
        try
        {
            ClampNetworkKnobs();
            PersistDeployments();
            SaveConfigSafe("settings");
            AddLog(LogLevel.Info, "config", "Settings saved.");
        }
        catch (Exception ex) { AddLog(LogLevel.Error, "config", ex.Message); }
        SettingsOpen = false;
        DashboardSection = "home";
    }

    void ClampNetworkKnobs()
    {
        if (FragmentSize < 1024)  FragmentSize = 1024;
        if (FragmentSize > 65536) FragmentSize = 65536;
        if (ChunkSize < 16384)    ChunkSize = 16384;
        if (MaxParallel < 1)      MaxParallel = 1;
        if (MaxParallel > 16)     MaxParallel = 16;
        if (CacheMaxMb < 16)      CacheMaxMb = 16;
        if (CacheMaxMb > 1024)    CacheMaxMb = 1024;
        if (CacheDefaultTtlS < 30) CacheDefaultTtlS = 30;
        if (CacheDefaultTtlS > 86400) CacheDefaultTtlS = 86400;
        if (CacheStaleIfErrorS < 0) CacheStaleIfErrorS = 0;
        if (CacheStaleIfErrorS > 3600) CacheStaleIfErrorS = 3600;
        if (MultiIdFailThreshold < 1) MultiIdFailThreshold = 1;
        if (MultiIdFailThreshold > 20) MultiIdFailThreshold = 20;
        if (MultiIdCooldownSeconds < 5) MultiIdCooldownSeconds = 5;
        if (MultiIdCooldownSeconds > 600) MultiIdCooldownSeconds = 600;
        if (MultiIdMaxConsecutive < 1) MultiIdMaxConsecutive = 1;
        if (MultiIdMaxConsecutive > 20) MultiIdMaxConsecutive = 20;
        if (string.IsNullOrWhiteSpace(MultiIdStrategy))
            MultiIdStrategy = "balanced";
        Raise(nameof(RelayRoutingLabel));
    }

    async Task InstallCertAsync()
    {
        if (!CertInstallService.CertExists() && _core.CoreExeExists())
        {
            BootStatus = Loc["preparing_cert"]; Busy = true;
            try { await _core.GenerateCaAsync(); } catch { }
            Busy = false; BootStatus = "";
        }
        var outcome = await Task.Run(() => CertInstallService.InstallCurrentUser());
        AddLog(outcome.Result == CertResult.Failed ? LogLevel.Error : LogLevel.Info,
               "cert", outcome.Message);
        RefreshCertStatus();
        if (outcome.Result == CertResult.Installed)
            MessageBox.Show("Certificate installed.\n\nRestart your browser if needed.",
                "MasterRelayVPN", MessageBoxButton.OK, MessageBoxImage.Information);
        else if (outcome.Result == CertResult.Failed)
            MessageBox.Show(outcome.Message, "Install failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    async Task InstallCertMachineAsync()
    {
        var r = MessageBox.Show(
            "Install certificate system-wide? Administrator rights are required.",
            "MasterRelayVPN", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        var outcome = await CertInstallService.InstallMachineAsync();
        AddLog(outcome.Result == CertResult.Failed ? LogLevel.Error : LogLevel.Info,
               "cert", outcome.Message);
        RefreshCertStatus();
        if (outcome.Result == CertResult.Installed)
            MessageBox.Show("Installed.", "MasterRelayVPN",
                MessageBoxButton.OK, MessageBoxImage.Information);
        else if (outcome.Result == CertResult.Failed)
            MessageBox.Show(outcome.Message, "Install failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    void ToggleSysProxy()
    {
        try
        {
            if (_sysProxyOn)
            {
                RestorePreviousProxySettings();
                AddLog(LogLevel.Info, "sysproxy", "Windows proxy restored");
            }
            else
            {
                _previousProxyState ??= ProxyToggleService.Capture();
                ProxyToggleService.Enable(ListenHost, ListenPort);
                _proxyManagedByApp = true;
                SysProxyOn = true;
                AddLog(LogLevel.Info, "sysproxy", $"Windows proxy -> {ListenHost}:{ListenPort}");
            }
            Raise(nameof(SysProxyStateLabel));
            Raise(nameof(SysProxyActionLabel));
        }
        catch (Exception ex) { AddLog(LogLevel.Error, "sysproxy", ex.Message); }
    }

    void CopyLogs()
    {
        var text = string.Join(Environment.NewLine,
            Logs.Select(e => $"{e.Time:HH:mm:ss} [{e.Source}] {e.LevelShort} {e.Message}"));
        try { Clipboard.SetText(text); } catch { }
    }

    void ExportLogs()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Log file (*.log)|*.log|All files (*.*)|*.*",
                FileName = $"masterrelay-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            };
            if (dlg.ShowDialog() == true)
            {
                File.WriteAllLines(dlg.FileName,
                    Logs.Select(e => $"{e.Time:yyyy-MM-dd HH:mm:ss} [{e.Source}] {e.LevelShort} {e.Message}"));
            }
        }
        catch (Exception ex) { AddLog(LogLevel.Error, "logs", ex.Message); }
    }

    void ExportRelayStatus()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON file (*.json)|*.json|Text file (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"relay-status-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            };
            if (dlg.ShowDialog() != true) return;
            var json = BuildRelayStatusJson();
            File.WriteAllText(dlg.FileName, json);
            AddLog(LogLevel.Info, "relay", $"Relay status exported: {dlg.FileName}");
        }
        catch (Exception ex)
        {
            AddLog(LogLevel.Error, "relay", "Export relay status failed: " + ex.Message);
        }
    }

    void CopyRelayStatusToClipboard()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_lastRelayStatusJson))
                _lastRelayStatusJson = BuildRelayStatusJson();
            Clipboard.SetText(_lastRelayStatusJson);
            AddLog(LogLevel.Info, "relay", "Relay status copied to clipboard.");
        }
        catch (Exception ex)
        {
            AddLog(LogLevel.Error, "relay", "Copy relay status failed: " + ex.Message);
        }
    }

    void ExportDiagnosticsBundle()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON file (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            };
            if (dlg.ShowDialog() != true) return;

            var relayJson = BuildRelayStatusJson();
            var support = BuildSupportSummary();
            var recentLogs = Logs.TakeLast(200).Select(e => new
            {
                t = e.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                lvl = e.Level.ToString(),
                src = e.Source,
                msg = e.Message,
            }).ToList();

            var payload = new
            {
                generated_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                app_version = AppVersionLabel,
                mode = Mode,
                status = Status,
                config_path = ConfigPathLabel,
                data_path = DataPathLabel,
                core_path = CorePathLabel,
                support_summary = support,
                relay_status = System.Text.Json.JsonSerializer.Deserialize<object>(relayJson),
                recent_logs = recentLogs,
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(dlg.FileName, json);
            AddLog(LogLevel.Info, "support", $"Diagnostics exported: {dlg.FileName}");
        }
        catch (Exception ex)
        {
            AddLog(LogLevel.Error, "support", "Diagnostics export failed: " + ex.Message);
        }
    }

    void CopySupportSummary()
    {
        try
        {
            var text = BuildSupportSummary();
            Clipboard.SetText(text);
            AddLog(LogLevel.Info, "support", "Support summary copied to clipboard.");
        }
        catch (Exception ex)
        {
            AddLog(LogLevel.Error, "support", "Copy support summary failed: " + ex.Message);
        }
    }

    void CopyRuntimeSnapshotToClipboard()
    {
        try
        {
            Clipboard.SetText(BuildRuntimeSnapshotJson());
            AddLog(LogLevel.Info, "support", "Runtime snapshot copied to clipboard.");
        }
        catch (Exception ex)
        {
            AddLog(LogLevel.Error, "support", "Copy runtime snapshot failed: " + ex.Message);
        }
    }

    void AnalyzeAndRecommend()
    {
        var notes = new List<string>();
        var apply = new List<Action>();
        var relayNotes = new List<string>();
        var unhealthyRelays = RelayDetails
            .Where(r => r.Uses >= 5 && (r.SuccessRate < 0.70 || r.RecentFailures >= 3))
            .OrderBy(r => r.SuccessRate)
            .ThenByDescending(r => r.RecentFailures)
            .ToList();

        if (!SysProxyOn)
        {
            notes.Add("- System proxy is OFF; apps may bypass the tunnel.");
            apply.Add(ToggleSysProxy);
        }
        if (_last.SuccessRate < 0.80)
        {
            notes.Add("- Success rate is below 80%; reduce fail threshold and increase cooldown.");
            apply.Add(() => MultiIdFailThreshold = Math.Clamp(MultiIdFailThreshold, 1, 3));
            apply.Add(() => MultiIdCooldownSeconds = Math.Max(MultiIdCooldownSeconds, 90));
            apply.Add(() => MultiIdStrategy = "fair_spread");
        }
        if ((_last.LatencyMs > 2200 || _probeLatencyMs > 2200) && MaxParallel > 3)
        {
            notes.Add("- Latency is high; lower parallelism for better stability.");
            apply.Add(() => MaxParallel = 3);
        }
        if (_last.CacheHitRate < 0.35 && CacheEnabled)
        {
            notes.Add("- Cache hit rate is low; increase default TTL and keep relays enabled for reuse.");
            apply.Add(() => CacheDefaultTtlS = Math.Max(CacheDefaultTtlS, 900));
            apply.Add(() => CacheStaleIfErrorS = Math.Max(CacheStaleIfErrorS, 180));
        }
        if (!CacheEnabled)
        {
            notes.Add("- Cache is disabled; enabling it usually improves throughput and latency.");
            apply.Add(() => CacheEnabled = true);
        }
        if ((_cfg.ScriptIds?.Count ?? 0) + (string.IsNullOrWhiteSpace(_cfg.ScriptId) ? 0 : 1) < 3)
            notes.Add("- Fewer than 3 relays are configured; add more deployment IDs for better balancing.");
        if (WindowErrorsLabel != "0")
            notes.Add("- Recent window contains errors; favor stable routing and lower parallelism.");

        foreach (var relay in unhealthyRelays.Take(3))
        {
            relayNotes.Add(
                $"- Relay {ShortRelayId(relay.Id)}: success {(relay.SuccessRate * 100):0.#}% | " +
                $"recent fails {relay.RecentFailures} | latency {(relay.LatencyMs > 0 ? relay.LatencyMs.ToString("0") : "--")} ms");
        }
        if (unhealthyRelays.Count > 0)
        {
            notes.Add("- Some relays are unhealthy. Keep them configured, but expect routing to deprioritize them.");
            apply.Add(() => MultiIdFailThreshold = Math.Clamp(MultiIdFailThreshold, 1, 2));
            apply.Add(() => MultiIdCooldownSeconds = Math.Max(MultiIdCooldownSeconds, 120));
            apply.Add(() => MultiIdStrategy = "fair_spread");
        }

        if (notes.Count == 0)
        {
            MessageBox.Show(
                "Current state looks healthy. No automatic changes are recommended right now.",
                "Analyzer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var msg = "System analysis summary:\n\n"
            + string.Join("\n", notes)
            + (relayNotes.Count > 0 ? "\n\nRelay health details:\n" + string.Join("\n", relayNotes) : "")
            + $"\n\nCache: hit {CacheHitRateLabel}, effective {CacheEffectiveHitRateLabel}, stale hits {CacheStaleHitsLabel}"
            + "\n\nMode: Apply tuning only (no relay removal)."
            + "\n\nApply safe automatic recommendations now?";
        var choice = MessageBox.Show(msg, "Analyzer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;

        foreach (var action in apply) action();
        ClampNetworkKnobs();
        SaveConfigSafe("analyzer");
        RaiseAllConfigProps();
        AddLog(LogLevel.Info, "analyzer", "Recommendations applied.");
    }

    void AddLog(LogLevel lvl, string src, string msg)
        => AddLog(new LogEntry(DateTime.Now, lvl, src, msg));

    void QueueLog(LogEntry e) => _pendingLogs.Enqueue(e);

    void FlushPendingLogs()
    {
        var drained = 0;
        while (drained < 200 && _pendingLogs.TryDequeue(out var e))
        {
            AddLog(e);
            drained++;
        }
    }

    void AddLog(LogEntry e)
    {
        Logs.Add(e);
        while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
    }

    void RefreshCommands()
    {
        Raise(nameof(IsRunning)); Raise(nameof(IsConnecting)); Raise(nameof(IsStopped));
        Raise(nameof(HeroLabel));
        StartCmd.RaiseCanExecuteChanged();
        StopCmd.RaiseCanExecuteChanged();
    }

    static void OnUi(Action a)
    {
        var app = Application.Current;
        if (app == null) { a(); return; }
        app.Dispatcher.BeginInvoke(a);
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 1) return;
        _autoTuner.Stop();
        _healthMonitor.Stop();
        try { _clockTimer.Dispose(); } catch { }
        try { _logFlushTimer.Stop(); } catch { }
        OnUi(FlushPendingLogs);
        try
        {
            ClampNetworkKnobs();
            PersistDeployments();
            SaveConfigSafe("shutdown");
        }
        catch { }
        RestorePreviousProxySettings();
        try { await _core.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
        _core.Dispose();
    }

    void SaveConfigSafe(string source = "config")
    {
        try
        {
            _cfgSvc.Save(_cfg);
            AddLog(LogLevel.Debug, source, $"Config persisted to {_cfgSvc.Path}");
        }
        catch (Exception ex)
        {
            AddLog(LogLevel.Error, source, "Config save failed: " + ex.Message);
        }
    }

    static string ShortRelayId(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return "";
        sid = sid.Trim();
        return sid.Length <= 16 ? sid : $"{sid[..6]}...{sid[^6..]}";
    }

    void RestorePreviousProxySettings()
    {
        if (!_proxyManagedByApp)
        {
            SysProxyOn = ProxyToggleService.IsEnabled();
            return;
        }
        try
        {
            if (_previousProxyState != null) ProxyToggleService.Restore(_previousProxyState);
            else ProxyToggleService.Disable();
        }
        catch { }
        finally
        {
            _proxyManagedByApp = false;
            _previousProxyState = null;
            SysProxyOn = ProxyToggleService.IsEnabled();
            Raise(nameof(SysProxyStateLabel));
            Raise(nameof(SysProxyActionLabel));
        }
    }

    string BuildRelayStatusJson()
    {
        var rows = RelayDetails.Select(r => new
        {
            id = r.Id,
            ok = r.Ok,
            err = r.Err,
            latency_ms = Math.Round(r.LatencyMs, 2),
            uses = r.Uses,
            recent_failures = r.RecentFailures,
            parked = r.Parked,
            parked_for_s = r.ParkedForS,
            success_rate = Math.Round(r.SuccessRate, 4),
            window_1m = new
            {
                total = r.Window1M.Total,
                ok = r.Window1M.Ok,
                success_rate = Math.Round(r.Window1M.SuccessRate, 4),
            },
            window_5m = new
            {
                total = r.Window5M.Total,
                ok = r.Window5M.Ok,
                success_rate = Math.Round(r.Window5M.SuccessRate, 4),
            },
            window_15m = new
            {
                total = r.Window15M.Total,
                ok = r.Window15M.Ok,
                success_rate = Math.Round(r.Window15M.SuccessRate, 4),
            },
        }).ToList();

        var payload = new
        {
            generated_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            strategy = MultiIdStrategy,
            max_consecutive = MultiIdMaxConsecutive,
            total_configured_relays = ConfiguredRelaysCountLabel,
            total_enabled_relays = EnabledRelaysCountLabel,
            endpoint_health = EndpointLabel,
            active_endpoint = ActiveEndpointLabel,
            cache = new
            {
                enabled = CacheEnabled,
                size_mb = CacheMaxMb,
                default_ttl_s = CacheDefaultTtlS,
                stale_if_error_s = CacheStaleIfErrorS,
                hit_rate = CacheHitRateLabel,
                effective_hit_rate = CacheEffectiveHitRateLabel,
                stale_hits = CacheStaleHitsLabel,
                bytes = CacheSizeLabel,
            },
            relay_runtime = new
            {
                requests = Requests,
                requests_per_second = RequestsPerSecLabel,
                window_requests = WindowRequestsLabel,
                window_errors = WindowErrorsLabel,
                window_success_rate = WindowSuccessRateLabel,
            },
            relays = rows,
            recent_relay_logs = Logs
                .Where(x => x.Source.Contains("multi", StringComparison.OrdinalIgnoreCase) || x.Source.Contains("relay", StringComparison.OrdinalIgnoreCase))
                .TakeLast(150)
                .Select(x => new { t = x.Time.ToString("yyyy-MM-dd HH:mm:ss"), lvl = x.Level.ToString(), src = x.Source, msg = x.Message })
                .ToList(),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        _lastRelayStatusJson = json;
        return json;
    }

    string BuildSupportSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"App: {AppVersionLabel}");
        sb.AppendLine($"Mode: {Mode}");
        sb.AppendLine($"Status: {StatusFriendly} ({Status})");
        sb.AppendLine($"Health: {HealthLabel} ({EndpointLabel})");
        sb.AppendLine($"Active relay: {ActiveEndpointLabel}");
        sb.AppendLine($"Requests: {Requests}, RPS: {RequestsPerSecLabel}");
        sb.AppendLine($"Traffic: {TotalTrafficLabel}, Uptime: {Uptime}");
        sb.AppendLine($"Proxy: {(SysProxyOn ? "On" : "Off")}");
        sb.AppendLine($"Strategy: {MultiIdStrategy}, MaxConsecutive: {MultiIdMaxConsecutive}");
        sb.AppendLine($"Relays enabled/configured: {EnabledRelaysCountLabel}/{ConfiguredRelaysCountLabel}");
        sb.AppendLine($"Cache: {(CacheEnabled ? "Enabled" : "Disabled")} | hit {CacheHitRateLabel} | effective {CacheEffectiveHitRateLabel} | stale hits {CacheStaleHitsLabel} | size {CacheSizeLabel}");
        sb.AppendLine($"Bypass domains: {(_cfg.DirectBypassDomains?.Count ?? 0)}");
        if (_sessionStartedAt.HasValue)
        {
            sb.AppendLine($"Session started: {_sessionStartedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Session duration: {Human.Duration((long)(DateTime.Now - _sessionStartedAt.Value).TotalSeconds)}");
            if (_sessionStatsSamples > 0)
            {
                sb.AppendLine($"Session avg latency: {_sessionLatencySum / _sessionStatsSamples:0} ms (max {_sessionLatencyMax:0} ms)");
                sb.AppendLine($"Session avg success: {(_sessionSuccessSum / _sessionStatsSamples) * 100:0}%");
                sb.AppendLine($"Session avg RPS: {_sessionRpsSum / _sessionStatsSamples:0.00}/s (peak {_sessionRpsMax:0.00}/s)");
            }
        }
        _lastSupportSummary = sb.ToString();
        return _lastSupportSummary;
    }

    string BuildRuntimeSnapshotJson()
    {
        var payload = new
        {
            generated_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            session_started_at = _sessionStartedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "n/a",
            session_duration_s = _sessionStartedAt.HasValue ? (int)(DateTime.Now - _sessionStartedAt.Value).TotalSeconds : 0,
            status = Status,
            status_friendly = StatusFriendly,
            proxy = new
            {
                system_proxy_on = SysProxyOn,
                listen_host = ListenHost,
                listen_port = ListenPort,
            },
            traffic = new
            {
                total_down = TotalDown,
                total_up = TotalUp,
                total = TotalTrafficLabel,
                current_down = SpeedDown,
                current_up = SpeedUp,
            },
            health = new
            {
                health = Health,
                health_label = HealthLabel,
                endpoint_health = EndpointLabel,
                active_endpoint = ActiveEndpointLabel,
                latency = LatencyLabel,
                success_rate = SuccessRateLabel,
            },
            session_rollup = new
            {
                samples = _sessionStatsSamples,
                avg_latency_ms = _sessionStatsSamples > 0 ? Math.Round(_sessionLatencySum / _sessionStatsSamples, 2) : 0,
                max_latency_ms = Math.Round(_sessionLatencyMax, 2),
                avg_rps = _sessionStatsSamples > 0 ? Math.Round(_sessionRpsSum / _sessionStatsSamples, 4) : 0,
                peak_rps = Math.Round(_sessionRpsMax, 4),
                avg_success_rate = _sessionStatsSamples > 0 ? Math.Round(_sessionSuccessSum / _sessionStatsSamples, 4) : 0,
            },
            config = new
            {
                mode = Mode,
                strategy = MultiIdStrategy,
                max_consecutive = MultiIdMaxConsecutive,
                fail_threshold = MultiIdFailThreshold,
                cooldown_seconds = MultiIdCooldownSeconds,
                max_parallel = MaxParallel,
                chunk_size = ChunkSize,
                fragment_size = FragmentSize,
                verify_ssl = VerifySsl,
                cache_enabled = CacheEnabled,
                cache_max_mb = CacheMaxMb,
                cache_default_ttl_s = CacheDefaultTtlS,
                cache_stale_if_error_s = CacheStaleIfErrorS,
            },
            relays = RelayDetails.Select(r => new
            {
                id = r.Id,
                ok = r.Ok,
                err = r.Err,
                latency_ms = Math.Round(r.LatencyMs, 2),
                uses = r.Uses,
                recent_failures = r.RecentFailures,
                parked = r.Parked,
                parked_for_s = r.ParkedForS,
                success_rate = Math.Round(r.SuccessRate, 4),
                window_1m = new { total = r.Window1M.Total, ok = r.Window1M.Ok, success_rate = Math.Round(r.Window1M.SuccessRate, 4) },
                window_5m = new { total = r.Window5M.Total, ok = r.Window5M.Ok, success_rate = Math.Round(r.Window5M.SuccessRate, 4) },
                window_15m = new { total = r.Window15M.Total, ok = r.Window15M.Ok, success_rate = Math.Round(r.Window15M.SuccessRate, 4) },
            }).ToList(),
        };
        return System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    void ResetRuntimeSession()
    {
        _sessionStartedAt = DateTime.Now;
        _sessionStatsSamples = 0;
        _sessionLatencySum = 0;
        _sessionLatencyMax = 0;
        _sessionRpsSum = 0;
        _sessionRpsMax = 0;
        _sessionSuccessSum = 0;
    }

    void UpdateSessionMetrics(StatsSnapshot s)
    {
        if (_sessionStartedAt is null || !_core.IsRunning) return;
        _sessionStatsSamples++;
        var latency = s.LatencyMs > 0 ? s.LatencyMs : _probeLatencyMs;
        _sessionLatencySum += Math.Max(0, latency);
        _sessionLatencyMax = Math.Max(_sessionLatencyMax, Math.Max(0, latency));
        _sessionRpsSum += Math.Max(0, s.RequestsPerSec);
        _sessionRpsMax = Math.Max(_sessionRpsMax, Math.Max(0, s.RequestsPerSec));
        _sessionSuccessSum += Math.Clamp(s.SuccessRate, 0, 1);
    }

    void UpdateTrends(StatsSnapshot s)
    {
        PushTrend(_throughputTrend, s.SpeedDown + s.SpeedUp);
        PushTrend(_latencyTrend, s.LatencyMs > 0 ? s.LatencyMs : _probeLatencyMs);
    }

    static void PushTrend(Queue<double> q, double v)
    {
        q.Enqueue(Math.Max(0, v));
        while (q.Count > 24) q.Dequeue();
    }

    static string BuildSparkline(IEnumerable<double> values, double scaleMax)
    {
        const string levels = " .:-=+*#%@";
        var arr = values.ToArray();
        if (arr.Length == 0) return "(waiting for data)";
        var max = Math.Max(scaleMax, arr.Max());
        if (max <= 0) return new string(' ', arr.Length);
        var chars = arr.Select(v =>
        {
            var idx = (int)Math.Round((levels.Length - 1) * Math.Clamp(v / max, 0, 1));
            return levels[idx];
        });
        return new string(chars.ToArray());
    }
}

public class DeploymentEntry : ObservableBase
{
    string _value = "";
    bool _isEnabled = true;
    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }
    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }
}

static class Human
{
    public static string Bytes(double n)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return n < 10 && i > 0 ? $"{n:0.00} {u[i]}" : $"{n:0.#} {u[i]}";
    }
    public static string PerSec(double bps) => Bytes(bps) + "/s";
    public static string Duration(long s)
    {
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s/60}m {s%60:D2}s";
        var h = s / 3600; var m = (s % 3600) / 60;
        return $"{h}h {m:D2}m";
    }
}
