using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
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
    readonly UpdateService _updateService = new();
    readonly AutoOptimizer _autoTuner;
    readonly Timer _clockTimer;
    readonly DispatcherTimer _logFlushTimer;
    readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    readonly Queue<double> _latencyTrend = new();
    readonly Queue<double> _throughputTrend = new();
    readonly Dictionary<string, int> _hostErrorCounts = new(StringComparer.OrdinalIgnoreCase);
    bool _deploymentHandlersAttached;
    bool _suspendDeploymentPersist;
    ProxyToggleService.ProxyState? _previousProxyState;
    bool _proxyManagedByApp;
    int _shutdownStarted;
    DateTime _lastRuntimeGuardAt = DateTime.MinValue;
    DateTime _lastWatchdogSignalAt = DateTime.MinValue;
    DateTime _lastWatchdogRestartAt = DateTime.MinValue;
    int _watchdogFailureStreak = 0;
    bool _watchdogRestarting;

    sealed record AnalyzerFinding(string Severity, string Area, string Message, int Weight);
    sealed record AnalyzerAction(string Key, string Label, Action Apply);
    sealed class AnalyzerReport
    {
        public List<AnalyzerFinding> Findings { get; } = new();
        public List<string> RelayNotes { get; } = new();
        public List<AnalyzerAction> Actions { get; } = new();
        public List<string> Insights { get; } = new();
        public int RiskScore { get; set; }
        public string Grade { get; set; } = "A";
        public string PrimaryCause { get; set; } = "Stable";
    }

    const int MaxLogLines = 500;
    public ObservableCollection<LogEntry> Logs { get; } = new();
    public ObservableCollection<RelayEndpointDetail> RelayDetails { get; } = new();
    public ObservableCollection<ClientConnectionInfo> ClientConnections { get; } = new();
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
            else if (StatsSection == "clients") StatsSubsection = "client_live";
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
        ResetSystemStateCmd = new RelayCommand(async () => await ResetSystemStateAsync(), () => !Busy);
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
        ApplySafeDefaultsLock();

        _healthMonitor.Checked += r => OnUi(() => OnHealthChecked(r));
        _healthMonitor.Start(
            shouldCheck: () => _core.IsRunning,
            endpoint: () => (ListenHost, ListenPort));
        _clockTimer = new Timer(_ => OnUi(() =>
        {
            Raise(nameof(LastCheckLabel));
            Raise(nameof(SessionDurationLabel));
        }),
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
            ApplySafeDefaultsLock();
            await RunStartupSelfTestAsync();
            if (AutoUpdateCheck)
                await CheckForUpdatesAsync(silent: true);

            if (report.CertGenerated || report.CertTrusted)
                AddLog(LogLevel.Info, "setup",
                    report.CertTrusted ? "Certificate trusted." : "Certificate generated.");
            if (!string.IsNullOrEmpty(report.Message))
                AddLog(LogLevel.Warning, "setup", report.Message!);
        }
        catch (Exception ex) { AddLog(LogLevel.Error, "setup", ex.Message); }
        finally { BootStatus = ""; Busy = false; }
    }

    void ApplySafeDefaultsLock()
    {
        if (!SafeDefaultsLock) return;
        VerifySsl = true;
        EnableHttp2 = false;
        if (MultiIdStrategy != "fair_spread")
            MultiIdStrategy = "fair_spread";
        MultiIdFailThreshold = Math.Clamp(MultiIdFailThreshold, 1, 2);
        MultiIdCooldownSeconds = Math.Clamp(MultiIdCooldownSeconds, 20, 120);
        MultiIdMaxConsecutive = Math.Clamp(MultiIdMaxConsecutive, 1, 2);
        MaxParallel = Math.Clamp(MaxParallel, 1, 5);
        FragmentSize = Math.Clamp(FragmentSize, 8 * 1024, 32 * 1024);
        RelayTimeout = Math.Clamp(RelayTimeout, 20, 45);
        RetrySafeAttempts = Math.Clamp(RetrySafeAttempts, 1, 3);
        RetryBackoffBaseMs = Math.Clamp(RetryBackoffBaseMs, 80, 300);
        CacheEnabled = true;
        CacheMaxMb = Math.Clamp(CacheMaxMb, 96, 512);
        CacheStaleIfErrorS = Math.Clamp(CacheStaleIfErrorS, 180, 900);
        AutoGoogleIpRefresh = true;
    }

    async Task RunStartupSelfTestAsync()
    {
        try
        {
            var issues = new List<string>();
            var fixes = new List<Func<Task>>();

            if (!CertInstallService.CertExists() || !CertInstallService.IsTrusted())
            {
                issues.Add("- CA certificate is missing or untrusted.");
                fixes.Add(async () =>
                {
                    try
                    {
                        await _core.GenerateCaAsync();
                    }
                    catch { }
                    try
                    {
                        var outcome = CertInstallService.InstallCurrentUser();
                        AddLog(outcome.Result == CertResult.Failed ? LogLevel.Warning : LogLevel.Info,
                            "selftest", outcome.Message);
                    }
                    catch { }
                });
            }

            if (ListenPort <= 0 || ListenPort > 65535)
            {
                issues.Add("- Listen port is invalid.");
                fixes.Add(async () =>
                {
                    ListenPort = 8085;
                    await Task.CompletedTask;
                });
            }
            else
            {
                try
                {
                    var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                    if (listeners.Any(ep => ep.Port == ListenPort))
                    {
                        issues.Add($"- Listen port {ListenPort} is already used.");
                        fixes.Add(async () =>
                        {
                            var preferred = new[] { 8085, 10808, 10809, 18080, 28080 };
                            var taken = listeners.Select(x => x.Port).ToHashSet();
                            var next = preferred.FirstOrDefault(p => !taken.Contains(p));
                            ListenPort = next > 0 ? next : (ListenPort + 1);
                            await Task.CompletedTask;
                        });
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(GoogleIp) && Mode == "apps_script")
            {
                issues.Add("- Google front IP is empty.");
                fixes.Add(async () =>
                {
                    GoogleIp = "216.239.38.120";
                    await Task.CompletedTask;
                });
            }

            if (issues.Count == 0)
            {
                AddLog(LogLevel.Info, "selftest", "Startup self-test passed.");
                return;
            }

            var ask = MessageBox.Show(
                "Startup self-test found issues:\n\n"
                + string.Join("\n", issues)
                + "\n\nApply safe automatic fixes now?",
                "Startup Self-Test",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes)
            {
                AddLog(LogLevel.Warning, "selftest", "Self-test issues detected; fixes skipped by user.");
                return;
            }

            foreach (var fix in fixes)
            {
                try { await fix(); } catch { }
            }
            ClampNetworkKnobs();
            PersistDeployments();
            SaveConfigSafe("selftest");
            RaiseAllConfigProps();
            AddLog(LogLevel.Info, "selftest", "Self-test fixes applied.");
        }
        catch (Exception ex)
        {
            AddLog(LogLevel.Warning, "selftest", "Self-test error: " + ex.Message);
        }
    }

    async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var currentVersion = AppVersionLabel;
            var result = await _updateService.CheckAsync(_cfg, currentVersion, CancellationToken.None);
            if (!result.Success)
            {
                if (!silent)
                    MessageBox.Show(result.Message, "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!result.IsUpdateAvailable)
            {
                if (!silent)
                    MessageBox.Show("You are up to date.", "Update Check", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var prompt = MessageBox.Show(
                $"Update available on {result.Channel} channel.\n\nCurrent: {result.CurrentVersion}\nLatest: {result.LatestVersion}\n\nOpen download page now?",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (prompt == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(result.DownloadUrl))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = result.DownloadUrl,
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    AddLog(LogLevel.Warning, "update", "Could not open update URL: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show("Update check failed: " + ex.Message, "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            nameof(ListenHost), nameof(AllowLan), nameof(ListenPort), nameof(LogLevelText), nameof(VerifySsl),
            nameof(EnableHttp2), nameof(EnableChunked), nameof(ChunkSize),
            nameof(MaxParallel), nameof(FragmentSize), nameof(ActivePreset),
            nameof(SafeDefaultsLock),
            nameof(CacheEnabled), nameof(CacheMaxMb), nameof(CacheDefaultTtlS), nameof(CacheStaleIfErrorS),
            nameof(RelayCbThreshold), nameof(RelayCbCooldown),
            nameof(RelayTimeout), nameof(ScriptBlacklistTtlS), nameof(RetrySafeAttempts), nameof(RetryBackoffBaseMs),
            nameof(AutoGoogleIpRefresh), nameof(GoogleIpRefreshIntervalS), nameof(GoogleIpProbeTimeoutS),
            nameof(GoogleIpProbeSampleSize), nameof(GoogleIpSwitchMinImprovementMs),
            nameof(WatchdogEnabled), nameof(WatchdogFailureThreshold), nameof(WatchdogCooldownS),
            nameof(MultiIdFailThreshold), nameof(MultiIdCooldownSeconds),
            nameof(MultiIdStrategy), nameof(MultiIdMaxConsecutive),
            nameof(DirectBypassDomainsText), nameof(NoMitmHostsText), nameof(NoMitmCidrsText), nameof(ForceRelayDomainsText),
            nameof(TcpSendBuffer), nameof(TcpRecvBuffer), nameof(HalfOpenRxTimeoutS), nameof(HalfOpenProbeTimeoutS), nameof(DcFailoverAttempts),
            nameof(UpdateChannel), nameof(AutoUpdateCheck), nameof(UpdateMetadataUrl), nameof(UpdatePublicKeyPem),
        }) Raise(n);
    }

    public string Mode          { get => _cfg.Mode;          set { _cfg.Mode = value; Raise(); } }
    public bool   SafeDefaultsLock { get => _cfg.SafeDefaultsLock; set { _cfg.SafeDefaultsLock = value; Raise(); } }
    public string FrontDomain   { get => _cfg.FrontDomain ?? ""; set { _cfg.FrontDomain = value; Raise(); } }
    public string CustomSni     { get => _cfg.CustomSni ?? "";   set { _cfg.CustomSni = value; Raise(); } }
    public string ScriptId      { get => _cfg.ScriptId ?? "";    set { _cfg.ScriptId = value; Raise(); } }
    public string WorkerHost    { get => _cfg.WorkerHost ?? "";  set { _cfg.WorkerHost = value; Raise(); } }
    public string CustomDomain  { get => _cfg.CustomDomain ?? ""; set { _cfg.CustomDomain = value; Raise(); } }
    public string AuthKey       { get => _cfg.AuthKey;        set { _cfg.AuthKey = value; Raise(); } }
    public string GoogleIp      { get => _cfg.GoogleIp ?? ""; set { _cfg.GoogleIp = value; Raise(); } }
    public string ListenHost    { get => _cfg.ListenHost;     set { _cfg.ListenHost = value; Raise(); } }
    public bool   AllowLan
    {
        get => _cfg.LanSharing;
        set
        {
            if (_cfg.LanSharing == value) return;
            _cfg.LanSharing = value;
            if (value)
            {
                _cfg.ListenHost = "0.0.0.0";
                Raise(nameof(ListenHost));
            }
            else if (string.IsNullOrWhiteSpace(_cfg.ListenHost) ||
                     _cfg.ListenHost == "0.0.0.0" || _cfg.ListenHost == "::")
            {
                _cfg.ListenHost = "127.0.0.1";
                Raise(nameof(ListenHost));
            }
            Raise();
        }
    }
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
    public int    RelayCbThreshold { get => _cfg.RelayCbThreshold; set { _cfg.RelayCbThreshold = value; Raise(); } }
    public int    RelayCbCooldown { get => _cfg.RelayCbCooldown; set { _cfg.RelayCbCooldown = value; Raise(); } }
    public int    RelayTimeout { get => _cfg.RelayTimeout; set { _cfg.RelayTimeout = value; Raise(); } }
    public int    ScriptBlacklistTtlS { get => _cfg.ScriptBlacklistTtlS; set { _cfg.ScriptBlacklistTtlS = value; Raise(); } }
    public int    RetrySafeAttempts { get => _cfg.RetrySafeAttempts; set { _cfg.RetrySafeAttempts = value; Raise(); } }
    public int    RetryBackoffBaseMs { get => _cfg.RetryBackoffBaseMs; set { _cfg.RetryBackoffBaseMs = value; Raise(); } }
    public bool   AutoGoogleIpRefresh { get => _cfg.AutoGoogleIpRefresh; set { _cfg.AutoGoogleIpRefresh = value; Raise(); } }
    public int    GoogleIpRefreshIntervalS { get => _cfg.GoogleIpRefreshIntervalS; set { _cfg.GoogleIpRefreshIntervalS = value; Raise(); } }
    public int    GoogleIpProbeTimeoutS { get => _cfg.GoogleIpProbeTimeoutS; set { _cfg.GoogleIpProbeTimeoutS = value; Raise(); } }
    public int    GoogleIpProbeSampleSize { get => _cfg.GoogleIpProbeSampleSize; set { _cfg.GoogleIpProbeSampleSize = value; Raise(); } }
    public int    GoogleIpSwitchMinImprovementMs { get => _cfg.GoogleIpSwitchMinImprovementMs; set { _cfg.GoogleIpSwitchMinImprovementMs = value; Raise(); } }
    public bool   WatchdogEnabled { get => _cfg.WatchdogEnabled; set { _cfg.WatchdogEnabled = value; Raise(); } }
    public int    WatchdogFailureThreshold { get => _cfg.WatchdogFailureThreshold; set { _cfg.WatchdogFailureThreshold = value; Raise(); } }
    public int    WatchdogCooldownS { get => _cfg.WatchdogCooldownS; set { _cfg.WatchdogCooldownS = value; Raise(); } }
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
    public string ForceRelayDomainsText
    {
        get => string.Join(Environment.NewLine, _cfg.ForceRelayHosts ?? new List<string>());
        set
        {
            var vals = (value ?? "")
                .Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().TrimStart('.').ToLowerInvariant())
                .Where(x => x.Length > 0)
                .Distinct()
                .ToList();
            _cfg.ForceRelayHosts = vals;
            Raise();
        }
    }
    public string NoMitmHostsText
    {
        get => string.Join(Environment.NewLine, _cfg.NoMitmHosts ?? new List<string>());
        set
        {
            var vals = (value ?? "")
                .Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeHostRuleKeepWildcard)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _cfg.NoMitmHosts = vals;
            Raise();
        }
    }
    public string NoMitmCidrsText
    {
        get => string.Join(Environment.NewLine, _cfg.NoMitmCidrs ?? new List<string>());
        set
        {
            var vals = (value ?? "")
                .Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _cfg.NoMitmCidrs = vals;
            Raise();
        }
    }
    public int TcpSendBuffer { get => _cfg.TcpSendBuffer; set { _cfg.TcpSendBuffer = value; Raise(); } }
    public int TcpRecvBuffer { get => _cfg.TcpRecvBuffer; set { _cfg.TcpRecvBuffer = value; Raise(); } }
    public int HalfOpenRxTimeoutS { get => _cfg.HalfOpenRxTimeoutS; set { _cfg.HalfOpenRxTimeoutS = value; Raise(); } }
    public double HalfOpenProbeTimeoutS { get => _cfg.HalfOpenProbeTimeoutS; set { _cfg.HalfOpenProbeTimeoutS = value; Raise(); } }
    public int DcFailoverAttempts { get => _cfg.DcFailoverAttempts; set { _cfg.DcFailoverAttempts = value; Raise(); } }
    public string UpdateChannel
    {
        get => string.IsNullOrWhiteSpace(_cfg.UpdateChannel) ? "stable" : _cfg.UpdateChannel;
        set { _cfg.UpdateChannel = (value ?? "stable").Trim().ToLowerInvariant() == "beta" ? "beta" : "stable"; Raise(); }
    }
    public bool AutoUpdateCheck { get => _cfg.AutoUpdateCheck; set { _cfg.AutoUpdateCheck = value; Raise(); } }
    public string UpdateMetadataUrl { get => _cfg.UpdateMetadataUrl ?? ""; set { _cfg.UpdateMetadataUrl = value; Raise(); } }
    public string UpdatePublicKeyPem { get => _cfg.UpdatePublicKeyPem ?? ""; set { _cfg.UpdatePublicKeyPem = value; Raise(); } }

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
    public string LiveBandwidthLabel => Human.PerSec(_last.SpeedDown + _last.SpeedUp);
    public string SessionDurationLabel => _sessionStartedAt.HasValue
        ? Human.Duration((long)Math.Max(0, (DateTime.Now - _sessionStartedAt.Value).TotalSeconds))
        : "--";
    public string SessionAvgLatencyLabel => _sessionStatsSamples > 0
        ? $"{_sessionLatencySum / _sessionStatsSamples:0} ms"
        : "--";
    public string SessionAvgRpsLabel => _sessionStatsSamples > 0
        ? $"{_sessionRpsSum / _sessionStatsSamples:0.00}/s"
        : "--";
    public string SessionAvgSuccessLabel => _sessionStatsSamples > 0
        ? $"{(_sessionSuccessSum / _sessionStatsSamples) * 100:0}%"
        : "--";
    public string CacheHitRateLabel => $"{Math.Clamp(_last.CacheHitRate, 0, 1) * 100:0}%";
    public string CacheEffectiveHitRateLabel => $"{Math.Clamp(_last.CacheEffectiveHitRate, 0, 1) * 100:0}%";
    public string CacheStaleHitsLabel => _last.CacheStaleHits.ToString();
    public string CacheSizeLabel => Human.Bytes(_last.CacheBytes);
    public string ClientsActiveLabel => _last.ClientsActive.ToString();
    public string ClientsSeenLabel => _last.ClientsTotalSeen.ToString();
    public string EndpointLabel => _last.Endpoints > 0
        ? $"{_last.EndpointsHealthy}/{_last.Endpoints}"
        : "--";
    public string QuickHealthGradeLabel => _last.SuccessRate >= 0.95
        ? "A"
        : (_last.SuccessRate >= 0.85 ? "B" : (_last.SuccessRate >= 0.70 ? "C" : "D"));
    public string TopFailingHostLabel
    {
        get
        {
            if (_hostErrorCounts.Count == 0) return "--";
            var top = _hostErrorCounts.OrderByDescending(x => x.Value).First();
            return $"{top.Key} ({top.Value})";
        }
    }
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
        RuntimeGuardTune(s);
        RelayDetails.Clear();
        if (s.EndpointsDetail != null)
        {
            foreach (var ep in s.EndpointsDetail.OrderByDescending(x => x.SuccessRate).ThenBy(x => x.LatencyMs))
                RelayDetails.Add(ep);
        }
        ClientConnections.Clear();
        if (s.ClientsDetail != null)
        {
            foreach (var c in s.ClientsDetail
                .OrderByDescending(x => x.Active)
                .ThenByDescending(x => x.LastSeen))
            {
                ClientConnections.Add(c);
            }
        }
        foreach (var n in new[]
        {
            nameof(SpeedDown), nameof(SpeedUp), nameof(TotalDown), nameof(TotalUp),
            nameof(Requests), nameof(Connections), nameof(Uptime),
            nameof(Health), nameof(HealthLabel), nameof(HealthBrush),
            nameof(LatencyLabel), nameof(SuccessRateLabel), nameof(RequestsPerSecLabel),
            nameof(WindowSuccessRateLabel), nameof(WindowErrorsLabel), nameof(WindowRequestsLabel),
            nameof(PeakConnectionsLabel), nameof(TotalTrafficLabel),
            nameof(LiveBandwidthLabel), nameof(SessionDurationLabel),
            nameof(SessionAvgLatencyLabel), nameof(SessionAvgRpsLabel),
            nameof(SessionAvgSuccessLabel),
            nameof(CacheHitRateLabel), nameof(CacheEffectiveHitRateLabel), nameof(CacheStaleHitsLabel), nameof(CacheSizeLabel),
            nameof(ClientsActiveLabel), nameof(ClientsSeenLabel),
            nameof(EndpointLabel), nameof(ActiveEndpointLabel),
            nameof(QuickHealthGradeLabel), nameof(TopFailingHostLabel),
            nameof(RelayRoutingLabel), nameof(ConfiguredRelaysCountLabel),
            nameof(EnabledRelaysCountLabel),
            nameof(ThroughputTrendLabel), nameof(LatencyTrendLabel),
        }) Raise(n);
    }

    void RuntimeGuardTune(StatsSnapshot s)
    {
        if (!IsRunning) return;
        var now = DateTime.UtcNow;
        if ((now - _lastRuntimeGuardAt).TotalSeconds < 14) return;
        _lastRuntimeGuardAt = now;

            var changed = false;
            var success = Math.Min(Math.Clamp(s.SuccessRate, 0, 1), Math.Clamp(s.WindowSuccessRate, 0, 1));
            var highErrorPressure = s.WindowRequests >= 10 && s.WindowErrors >= 4;
            var highLatency = s.LatencyMs > 2500;

        if (success < 0.92 || highErrorPressure || highLatency)
        {
            if (MultiIdStrategy != "fair_spread") { MultiIdStrategy = "fair_spread"; changed = true; }
            if (MultiIdFailThreshold > 2) { MultiIdFailThreshold = 2; changed = true; }
            if (MultiIdCooldownSeconds > 20) { MultiIdCooldownSeconds = 20; changed = true; }
            if (MultiIdMaxConsecutive > 1) { MultiIdMaxConsecutive = 1; changed = true; }
            var maxParallelCap = ActivePreset == "god" ? 4 : 3;
            if (MaxParallel > maxParallelCap) { MaxParallel = maxParallelCap; changed = true; }
            if (FragmentSize > 16384) { FragmentSize = 16384; changed = true; }
            if (!CacheEnabled) { CacheEnabled = true; changed = true; }
            if (CacheStaleIfErrorS < 240) { CacheStaleIfErrorS = 240; changed = true; }
            var cacheFloor = ActivePreset == "god" ? 224 : 160;
            if (CacheMaxMb < cacheFloor) { CacheMaxMb = cacheFloor; changed = true; }
            if (!AutoGoogleIpRefresh) { AutoGoogleIpRefresh = true; changed = true; }
        }
        else if (success >= 0.985 && s.LatencyMs > 0 && s.LatencyMs < 1200 && s.RequestsPerSec > 0.4)
        {
            var targetParallel = ActivePreset == "god" ? 5 : 4;
            var targetChunk = ActivePreset == "god" ? 224 * 1024 : 192 * 1024;
            var targetCache = ActivePreset == "god" ? 224 : 160;
            if (MaxParallel < targetParallel) { MaxParallel = targetParallel; changed = true; }
            if (ChunkSize < targetChunk) { ChunkSize = targetChunk; changed = true; }
            if (CacheMaxMb < targetCache) { CacheMaxMb = targetCache; changed = true; }
        }

        if (!changed) return;
        ClampNetworkKnobs();
        SaveConfigSafe("runtime_guard");
        AddLog(LogLevel.Info, "runtime_guard",
            $"adaptive tuning applied: ok={success * 100:0}% lat={s.LatencyMs:0}ms err={s.WindowErrors}/{s.WindowRequests}");
    }

    void OnHealthChecked(HealthCheckResult result)
    {
        _lastCheckAt = result.CheckedAt;
        _probeLatencyMs = result.Reachable ? result.LatencyMs : 0;
        _diagnostics = result.Reachable
            ? $"{Loc["diag_proxy_reachable"]} ({result.LatencyMs:0} ms)"
            : $"{Loc["diag_proxy_unreachable"]}: {result.Message}";

        _ = WatchdogTickAsync(result);

        Raise(nameof(LastCheckLabel));
        Raise(nameof(LatencyLabel));
        Raise(nameof(Diagnostics));
    }

    async Task WatchdogTickAsync(HealthCheckResult health)
    {
        if (!WatchdogEnabled || !IsRunning) return;
        if (_watchdogRestarting) return;

        var now = DateTime.UtcNow;
        if (health.Reachable && _last.Health != "down")
        {
            _lastWatchdogSignalAt = now;
            _watchdogFailureStreak = 0;
            return;
        }

        _watchdogFailureStreak++;
        var sinceGood = _lastWatchdogSignalAt == DateTime.MinValue
            ? TimeSpan.FromSeconds(999)
            : (now - _lastWatchdogSignalAt);
        if (_watchdogFailureStreak < Math.Max(1, WatchdogFailureThreshold) && sinceGood.TotalSeconds < 45)
            return;
        if ((now - _lastWatchdogRestartAt).TotalSeconds < Math.Max(10, WatchdogCooldownS))
            return;

        _watchdogRestarting = true;
        _lastWatchdogRestartAt = now;
        try
        {
            AddLog(LogLevel.Warning, "watchdog",
                $"Health watchdog restarting core (streak={_watchdogFailureStreak}, health={_last.Health}, reachable={health.Reachable})");
            _autoTuner.Stop();
            await _core.StopAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(400);
            _core.Start();
            if (ActivePreset == "auto") _autoTuner.Start();
            _watchdogFailureStreak = 0;
        }
        catch (Exception ex)
        {
            AddLog(LogLevel.Error, "watchdog", "Watchdog restart failed: " + ex.Message);
        }
        finally
        {
            _watchdogRestarting = false;
        }
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
    public RelayCommand ResetSystemStateCmd { get; }
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

    async Task ResetSystemStateAsync()
    {
        var ask = MessageBox.Show(
            "Reset runtime system state and restart engine?\n\nThis clears runtime cache, relay health windows, counters, and session stats.",
            "Reset System State",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        try
        {
            Busy = true;
            var wasRunning = IsRunning || IsConnecting;
            _autoTuner.Stop();
            _hostErrorCounts.Clear();
            Logs.Clear();
            RelayDetails.Clear();
            _last = new StatsSnapshot();
            ResetRuntimeSession();
            Raise(nameof(TopFailingHostLabel));
            Raise(nameof(QuickHealthGradeLabel));
            Raise(nameof(CacheHitRateLabel));
            Raise(nameof(CacheEffectiveHitRateLabel));
            Raise(nameof(CacheStaleHitsLabel));
            Raise(nameof(CacheSizeLabel));
            Raise(nameof(SuccessRateLabel));
            Raise(nameof(WindowSuccessRateLabel));
            Raise(nameof(WindowErrorsLabel));
            Raise(nameof(WindowRequestsLabel));
            Raise(nameof(RequestsPerSecLabel));
            Raise(nameof(LatencyLabel));

            if (wasRunning)
            {
                await _core.StopAsync(TimeSpan.FromSeconds(4));
                await Task.Delay(250);
                _core.Start();
                if (ActivePreset == "auto") _autoTuner.Start();
            }

            AddLog(LogLevel.Info, "reset", "System state reset completed.");
        }
        catch (Exception ex)
        {
            AddLog(LogLevel.Error, "reset", "Reset failed: " + ex.Message);
        }
        finally
        {
            Busy = false;
            RefreshCommands();
        }
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
        var bypass = (_cfg.DirectBypassDomains ?? new List<string>())
            .Select(NormalizeHostRule)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _cfg.BypassHosts = bypass;
        _cfg.NoMitmHosts = (_cfg.NoMitmHosts ?? new List<string>())
            .Select(NormalizeHostRuleKeepWildcard)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _cfg.NoMitmCidrs = (_cfg.NoMitmCidrs ?? new List<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _cfg.ForceRelayHosts = (_cfg.ForceRelayHosts ?? new List<string>())
            .Select(NormalizeHostRule)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var profileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in _cfg.BypassHosts) profileMap[h] = "direct-bypass";
        foreach (var h in _cfg.NoMitmHosts) profileMap[h] = "no-mitm";
        foreach (var h in _cfg.ForceRelayHosts) profileMap[h] = "force-relay";
        _cfg.DomainRoutingProfiles = profileMap;
        SaveConfigSafe("deployments");
        Raise(nameof(ConfiguredRelaysCountLabel));
        Raise(nameof(EnabledRelaysCountLabel));
        Raise(nameof(ConfiguredRelaysPreview));
    }

    static string NormalizeHostRule(string raw)
    {
        var host = (raw ?? "").Trim().ToLowerInvariant();
        if (host.StartsWith("http://")) host = host.Substring("http://".Length);
        if (host.StartsWith("https://")) host = host.Substring("https://".Length);
        var slash = host.IndexOf('/');
        if (slash >= 0) host = host.Substring(0, slash);
        return host.Trim('.').Trim();
    }

    static string NormalizeHostRuleKeepWildcard(string raw)
    {
        var host = (raw ?? "").Trim().ToLowerInvariant();
        var keepSuffixRule = host.StartsWith(".");
        if (host.StartsWith("http://")) host = host.Substring("http://".Length);
        if (host.StartsWith("https://")) host = host.Substring("https://".Length);
        var slash = host.IndexOf('/');
        if (slash >= 0) host = host.Substring(0, slash);
        host = keepSuffixRule ? "." + host.Trim('.').Trim() : host.Trim('.').Trim();
        return host == "." ? "" : host;
    }

    // Presets
    void ApplyPreset(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var p = Presets.ByKey(key!);
        Presets.ApplyTo(_cfg, p);
        if (string.Equals(p.Key, "god", StringComparison.OrdinalIgnoreCase))
            ApplyGodModeKnobs();
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
            if (SafeDefaultsLock)
                ApplySafeDefaultsLock();
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
        NormalizeListenHostAndLanSharing();
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
        if (RelayCbThreshold < 1) RelayCbThreshold = 1;
        if (RelayCbThreshold > 10) RelayCbThreshold = 10;
        if (RelayCbCooldown < 5) RelayCbCooldown = 5;
        if (RelayCbCooldown > 180) RelayCbCooldown = 180;
        if (RelayTimeout < 8) RelayTimeout = 8;
        if (RelayTimeout > 120) RelayTimeout = 120;
        if (ScriptBlacklistTtlS < 30) ScriptBlacklistTtlS = 30;
        if (ScriptBlacklistTtlS > 1800) ScriptBlacklistTtlS = 1800;
        if (RetrySafeAttempts < 1) RetrySafeAttempts = 1;
        if (RetrySafeAttempts > 4) RetrySafeAttempts = 4;
        if (RetryBackoffBaseMs < 20) RetryBackoffBaseMs = 20;
        if (RetryBackoffBaseMs > 2500) RetryBackoffBaseMs = 2500;
        if (GoogleIpRefreshIntervalS < 60) GoogleIpRefreshIntervalS = 60;
        if (GoogleIpRefreshIntervalS > 3600) GoogleIpRefreshIntervalS = 3600;
        if (GoogleIpProbeTimeoutS < 1) GoogleIpProbeTimeoutS = 1;
        if (GoogleIpProbeTimeoutS > 10) GoogleIpProbeTimeoutS = 10;
        if (GoogleIpProbeSampleSize < 3) GoogleIpProbeSampleSize = 3;
        if (GoogleIpProbeSampleSize > 30) GoogleIpProbeSampleSize = 30;
        if (GoogleIpSwitchMinImprovementMs < 0) GoogleIpSwitchMinImprovementMs = 0;
        if (GoogleIpSwitchMinImprovementMs > 2000) GoogleIpSwitchMinImprovementMs = 2000;
        if (MultiIdFailThreshold < 1) MultiIdFailThreshold = 1;
        if (MultiIdFailThreshold > 20) MultiIdFailThreshold = 20;
        if (MultiIdCooldownSeconds < 5) MultiIdCooldownSeconds = 5;
        if (MultiIdCooldownSeconds > 600) MultiIdCooldownSeconds = 600;
        if (MultiIdMaxConsecutive < 1) MultiIdMaxConsecutive = 1;
        if (MultiIdMaxConsecutive > 20) MultiIdMaxConsecutive = 20;
        if (TcpSendBuffer < 16384) TcpSendBuffer = 16384;
        if (TcpSendBuffer > 4 * 1024 * 1024) TcpSendBuffer = 4 * 1024 * 1024;
        if (TcpRecvBuffer < 16384) TcpRecvBuffer = 16384;
        if (TcpRecvBuffer > 4 * 1024 * 1024) TcpRecvBuffer = 4 * 1024 * 1024;
        if (HalfOpenRxTimeoutS < 5) HalfOpenRxTimeoutS = 5;
        if (HalfOpenRxTimeoutS > 180) HalfOpenRxTimeoutS = 180;
        if (HalfOpenProbeTimeoutS < 0.5) HalfOpenProbeTimeoutS = 0.5;
        if (HalfOpenProbeTimeoutS > 10.0) HalfOpenProbeTimeoutS = 10.0;
        if (DcFailoverAttempts < 1) DcFailoverAttempts = 1;
        if (DcFailoverAttempts > 6) DcFailoverAttempts = 6;
        if (string.IsNullOrWhiteSpace(MultiIdStrategy))
            MultiIdStrategy = "balanced";
        Raise(nameof(RelayRoutingLabel));
    }

    void NormalizeListenHostAndLanSharing()
    {
        var host = (ListenHost ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            if (AllowLan) host = "0.0.0.0";
            else host = "127.0.0.1";
        }
        else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            host = "127.0.0.1";
        }

        if (AllowLan)
        {
            if (host == "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                host = "0.0.0.0";
        }
        else
        {
            if (host == "0.0.0.0" || host == "::")
                host = "127.0.0.1";
        }

        _cfg.ListenHost = host;
        _cfg.LanSharing = host == "0.0.0.0" || host == "::";
        Raise(nameof(ListenHost));
        Raise(nameof(AllowLan));
    }

    void ApplyGodModeKnobs()
    {
        EnableHttp2 = false;
        EnableChunked = true;
        VerifySsl = true;

        FragmentSize = 12 * 1024;
        ChunkSize = 224 * 1024;
        MaxParallel = 5;

        CacheEnabled = true;
        CacheMaxMb = Math.Max(CacheMaxMb, 224);
        CacheDefaultTtlS = Math.Max(CacheDefaultTtlS, 1200);
        CacheStaleIfErrorS = Math.Max(CacheStaleIfErrorS, 300);

        MultiIdStrategy = "fair_spread";
        MultiIdFailThreshold = 2;
        MultiIdCooldownSeconds = 20;
        MultiIdMaxConsecutive = 1;

        RelayCbThreshold = 3;
        RelayCbCooldown = 24;
        RelayTimeout = Math.Max(RelayTimeout, 35);
        ScriptBlacklistTtlS = 240;
        RetrySafeAttempts = Math.Max(RetrySafeAttempts, 2);
        RetryBackoffBaseMs = 140;

        AutoGoogleIpRefresh = true;
        GoogleIpRefreshIntervalS = 420;
        GoogleIpProbeTimeoutS = 3;
        GoogleIpProbeSampleSize = 10;
        GoogleIpSwitchMinImprovementMs = 90;

        if (!string.IsNullOrWhiteSpace(CustomSni) &&
            !CustomSni.Equals("www.google.com", StringComparison.OrdinalIgnoreCase))
        {
            CustomSni = "";
        }
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
                analyzer = BuildAnalyzerReport(includeActions: false),
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
        var report = BuildAnalyzerReport(includeActions: true);
        if (report.Findings.Count == 0 && report.Actions.Count == 0)
        {
            MessageBox.Show(
                "Current state looks healthy. No automatic changes are recommended right now.",
                "Analyzer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var findingsText = report.Findings
            .OrderByDescending(f => f.Weight)
            .ThenBy(f => f.Area)
            .Select(f => $"- [{f.Severity}] {f.Area}: {f.Message}");
        var actionText = report.Actions.Take(10).Select(a => $"- {a.Label}");

        var msg = "System analysis summary:\n\n"
            + $"Risk grade: {report.Grade} ({report.RiskScore}/100)\n"
            + $"Primary cause: {report.PrimaryCause}\n\n"
            + string.Join("\n", findingsText)
            + (report.RelayNotes.Count > 0 ? "\n\nRelay risk details:\n" + string.Join("\n", report.RelayNotes) : "")
            + (report.Actions.Count > 0 ? "\n\nPlanned safe auto-fixes:\n" + string.Join("\n", actionText) : "")
            + "\n\nObserved metrics:"
            + $"\n- Success rate: {SuccessRateLabel}"
            + $"\n- Window success: {WindowSuccessRateLabel} ({WindowRequestsLabel} req, {WindowErrorsLabel} errors)"
            + $"\n- Latency: {LatencyLabel}"
            + $"\n- Endpoint health: {EndpointLabel}"
            + $"\n- Cache: hit {CacheHitRateLabel}, effective {CacheEffectiveHitRateLabel}, stale {CacheStaleHitsLabel}"
            + "\n\nMode: Apply safe tuning only (no relay removal)."
            + "\n\nApply safe automatic recommendations now?";
        var choice = MessageBox.Show(msg, "Analyzer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;

        foreach (var action in report.Actions) action.Apply();
        ClampNetworkKnobs();
        SaveConfigSafe("analyzer");
        RaiseAllConfigProps();
        AddLog(LogLevel.Info, "analyzer",
            $"Applied {report.Actions.Count} recommendations | risk {report.RiskScore}/100 ({report.Grade}) | cause={report.PrimaryCause}");
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
        TrackFailingHostFromLog(e);
        Raise(nameof(TopFailingHostLabel));
    }

    AnalyzerReport BuildAnalyzerReport(bool includeActions)
    {
        var report = new AnalyzerReport();
        var appliedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFinding(string severity, string area, string message, int weight)
            => report.Findings.Add(new AnalyzerFinding(severity, area, message, weight));

        void AddAction(string key, string label, Action apply)
        {
            if (!includeActions) return;
            if (!appliedKeys.Add(key)) return;
            report.Actions.Add(new AnalyzerAction(key, label, apply));
        }

        var enabledRelayCount = _cfg.ScriptIds?.Count ?? 0;
        var windowErrorRate = _last.WindowRequests > 0
            ? (double)_last.WindowErrors / _last.WindowRequests
            : 0.0;
        var unhealthyRelays = RelayDetails
            .Where(r => (r.Window1M.Total >= 10 && r.Window1M.SuccessRate < 0.80) || r.RecentFailures >= 3 || r.Parked)
            .OrderBy(r => r.Window1M.SuccessRate)
            .ThenByDescending(r => r.RecentFailures)
            .ToList();
        var totalUses = RelayDetails.Sum(r => Math.Max(0, r.Uses));
        var dominantRelay = totalUses > 0
            ? RelayDetails.OrderByDescending(r => r.Uses).FirstOrDefault()
            : null;
        var dominanceRatio = (dominantRelay != null && totalUses > 0)
            ? (double)dominantRelay.Uses / totalUses
            : 0.0;
        var parked = RelayDetails.Count(r => r.Parked);
        var latency = _last.LatencyMs > 0 ? _last.LatencyMs : _probeLatencyMs;
        var hasStrongSignal = _last.WindowRequests >= 12;

        if (!CertInstallService.CertExists() || !CertInstallService.IsTrusted())
            AddFinding("HIGH", "TLS", "CA certificate is missing/untrusted; HTTPS interception may fail.", 18);

        if (!VerifySsl)
        {
            AddFinding("HIGH", "Security", "verify_ssl is OFF; upstream TLS downgrade risk is higher.", 16);
            AddAction("verify_ssl_on", "Enable SSL verification", () => VerifySsl = true);
        }

        if (!string.IsNullOrWhiteSpace(CustomSni) &&
            !CustomSni.Equals("www.google.com", StringComparison.OrdinalIgnoreCase))
        {
            AddFinding("MED", "SNI", $"Custom SNI is '{CustomSni}', which can break some CDNs/apps.", 9);
            AddAction("custom_sni_clear", "Clear custom SNI", () => CustomSni = "");
        }

        if (enabledRelayCount < 3)
            AddFinding("MED", "Capacity", "Fewer than 3 enabled relays; balancing headroom is limited.", 7);

        if (_last.SuccessRate < 0.92 || windowErrorRate > 0.12 || (hasStrongSignal && _last.WindowErrors >= 4))
        {
            AddFinding(
                "HIGH",
                "Reliability",
                $"Success {_last.SuccessRate * 100:0.#}% and window error rate {windowErrorRate * 100:0.#}% indicate instability.",
                22
            );
            AddAction("fail_threshold", "Set relay fail threshold to 2", () => MultiIdFailThreshold = Math.Clamp(MultiIdFailThreshold, 1, 2));
            AddAction("cooldown_to_20", "Set relay cooldown to 20s", () => MultiIdCooldownSeconds = 20);
            AddAction("max_consecutive_1", "Limit max consecutive relay use to 1", () => MultiIdMaxConsecutive = 1);
            AddAction("strategy_spread", "Use fair_spread relay strategy", () => MultiIdStrategy = "fair_spread");
            AddAction("retry_safe_2", "Use 2 safe retries (GET/HEAD)", () => RetrySafeAttempts = Math.Max(2, RetrySafeAttempts));
            AddAction("retry_backoff_140", "Set retry backoff base to 140ms", () => RetryBackoffBaseMs = 140);
            AddAction("blacklist_240", "Set script blacklist TTL to 240s", () => ScriptBlacklistTtlS = 240);
            AddAction("relay_timeout_35", "Set relay timeout to 35s", () => RelayTimeout = Math.Max(RelayTimeout, 35));
            AddAction("preset_god_reliability", "Switch to God Mode profile", () =>
            {
                ActivePreset = "god";
                ApplyGodModeKnobs();
            });
        }

        if (latency > 2200)
        {
            AddFinding("MED", "Latency", $"Latency is high ({latency:0} ms).", 11);
            AddAction("max_parallel_2", "Reduce max parallel to 2", () => MaxParallel = Math.Min(MaxParallel, 2));
            AddAction("fragment_8k", "Use smaller fragment size (8KB)", () => FragmentSize = Math.Min(FragmentSize, 8192));
            AddAction("auto_ip_refresh_on", "Enable auto Google IP refresh", () => AutoGoogleIpRefresh = true);
            AddAction("ip_refresh_600", "Refresh Google IP every 600s", () => GoogleIpRefreshIntervalS = 600);
            AddAction("ip_probe_timeout_3", "Set Google IP probe timeout to 3s", () => GoogleIpProbeTimeoutS = 3);
            if (!string.Equals(ActivePreset, "god", StringComparison.OrdinalIgnoreCase))
            {
                AddAction("preset_god_latency", "Switch to God Mode profile", () =>
                {
                    ActivePreset = "god";
                    ApplyGodModeKnobs();
                });
            }
        }
        else if (latency > 0 && latency < 1200 && _last.SuccessRate > 0.97 && _last.RequestsPerSec > 0.4)
        {
            AddFinding("LOW", "Throughput", "Network is healthy; can increase parallelism a bit.", 4);
            AddAction("max_parallel_4", "Increase max parallel to 4", () => MaxParallel = Math.Max(MaxParallel, 4));
        }

        if (!CacheEnabled)
        {
            AddFinding("MED", "Cache", "Cache is disabled; repeated content cannot be reused.", 8);
            AddAction("cache_on", "Enable cache", () => CacheEnabled = true);
        }
        else
        {
            if (_last.CacheHitRate < 0.30 && _last.Requests > 60)
            {
                AddFinding("LOW", "Cache", $"Cache hit rate is low ({CacheHitRateLabel}); TTL/capacity tuning may help.", 5);
                AddAction("cache_ttl_900", "Set cache default TTL to 900s", () => CacheDefaultTtlS = Math.Max(CacheDefaultTtlS, 900));
                AddAction("cache_stale_240", "Set stale-on-error to 240s", () => CacheStaleIfErrorS = Math.Max(CacheStaleIfErrorS, 240));
            }
            if (CacheMaxMb < 160)
            {
                AddFinding("LOW", "Cache", "Cache size is small for modern browsing sessions.", 4);
                AddAction("cache_size_160", "Set cache size to 160MB", () => CacheMaxMb = Math.Max(CacheMaxMb, 160));
            }
        }

        if (RelayCbThreshold > 3)
        {
            AddFinding("LOW", "Circuit-breaker", "Per-host relay breaker reacts slowly.", 4);
            AddAction("relay_cb_threshold", "Set relay circuit threshold to 3", () => RelayCbThreshold = 3);
        }
        if (RelayCbCooldown < 20)
        {
            AddFinding("LOW", "Circuit-breaker", "Breaker cooldown is short; quick re-fail loops may happen.", 3);
            AddAction("relay_cb_cooldown", "Set relay circuit cooldown to 20s", () => RelayCbCooldown = 20);
        }

        if (dominanceRatio > 0.70 && RelayDetails.Count >= 3)
        {
            AddFinding(
                "MED",
                "Load-balance",
                $"Relay {ShortRelayId(dominantRelay?.Id ?? "")} handles {dominanceRatio * 100:0.#}% of uses.",
                9
            );
            AddAction("strategy_fair", "Use fair_spread strategy", () => MultiIdStrategy = "fair_spread");
            AddAction("max_consecutive_one", "Set max consecutive relay use to 1", () => MultiIdMaxConsecutive = 1);
        }

        if (parked > 0 && RelayDetails.Count > 0)
            AddFinding("MED", "Relays", $"{parked}/{RelayDetails.Count} relays are currently parked.", 7);

        if (!SysProxyOn)
            AddFinding("INFO", "Routing", "System proxy is OFF (manual/browser-only mode expected).", 1);

        var telegramHosts = (_cfg.NoMitmHosts ?? new List<string>())
            .Select(x => x?.ToLowerInvariant() ?? "")
            .Where(x => x.Length > 0)
            .ToList();
        var hasTelegramNoMitmHostRules = telegramHosts.Any(h =>
            h == "telegram.org" || h == "t.me" ||
            h.EndsWith(".telegram.org") || h.EndsWith(".t.me") ||
            h.EndsWith(".telegram-cdn.org") || h.EndsWith(".telesco.pe") || h.EndsWith(".tdesktop.com"));
        var hasTelegramNoMitmCidrs = (_cfg.NoMitmCidrs?.Count ?? 0) > 0;
        if (!hasTelegramNoMitmHostRules || !hasTelegramNoMitmCidrs)
        {
            AddFinding("MED", "Telegram", "Telegram no-MITM host/CIDR coverage looks incomplete.", 9);
            AddAction("telegram_no_mitm_seed", "Seed Telegram no-MITM host/CIDR defaults", () =>
            {
                _cfg.NoMitmHosts = new List<string>
                {
                    "telegram.org", ".telegram.org", "t.me", ".t.me",
                    ".telegram-cdn.org", ".telesco.pe", ".tdesktop.com"
                };
                _cfg.NoMitmCidrs = new List<string>
                {
                    "149.154.160.0/20", "91.108.4.0/22", "91.108.8.0/22",
                    "91.108.12.0/22", "91.108.16.0/22", "91.108.56.0/22"
                };
                Raise(nameof(NoMitmHostsText));
                Raise(nameof(NoMitmCidrsText));
            });
        }
        if (DcFailoverAttempts < 2)
        {
            AddFinding("LOW", "Telegram", "Telegram DC failover retries are conservative.", 4);
            AddAction("telegram_failover_attempts", "Set Telegram DC failover attempts to 2", () => DcFailoverAttempts = 2);
        }
        if (HalfOpenRxTimeoutS > 40)
        {
            AddFinding("LOW", "Telegram", "Half-open timeout is long; Telegram recovery may be slow on bad links.", 3);
            AddAction("telegram_half_open_rx", "Set half-open RX timeout to 20s", () => HalfOpenRxTimeoutS = 20);
        }

        if (_last.SuccessRate < 0.88 || windowErrorRate > 0.15 || (hasStrongSignal && _last.WindowErrors >= 5))
            report.PrimaryCause = "Relay quality / endpoint instability";
        else if (latency > 2200)
            report.PrimaryCause = "Network path latency / front IP quality";
        else if (dominanceRatio > 0.70)
            report.PrimaryCause = "Relay load imbalance";
        else if (!CacheEnabled || (_last.CacheHitRate < 0.25 && _last.Requests > 80))
            report.PrimaryCause = "Low cache efficiency";
        else
            report.PrimaryCause = "Stable";

        foreach (var relay in unhealthyRelays.Take(5))
        {
            report.RelayNotes.Add(
                $"- Relay {ShortRelayId(relay.Id)} | uses {relay.Uses} | " +
                $"1m success {(relay.Window1M.SuccessRate * 100):0.#}% ({relay.Window1M.Ok}/{relay.Window1M.Total}) | " +
                $"fails {relay.RecentFailures} | parked {(relay.Parked ? $"yes {relay.ParkedForS}s" : "no")} | " +
                $"latency {(relay.LatencyMs > 0 ? relay.LatencyMs.ToString("0") : "--")} ms");
        }

        report.RiskScore = Math.Clamp(report.Findings.Sum(f => f.Weight), 0, 100);
        report.Grade = report.RiskScore switch
        {
            <= 10 => "A",
            <= 22 => "B",
            <= 40 => "C",
            <= 60 => "D",
            _ => "E",
        };

        report.Insights.Clear();
        report.Insights.AddRange(BuildQuickInsights(report));
        return report;
    }

    static readonly Regex _urlInParensRx = new(@"\((https?://[^)\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex _respHostRx = new(@"RESP\s+[^\s]*\s+([a-z0-9.-]+\.[a-z]{2,})\s+status=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    void TrackFailingHostFromLog(LogEntry e)
    {
        if (e.Level != LogLevel.Error && e.Level != LogLevel.Warning) return;
        var msg = e.Message ?? "";

        string? host = null;

        var m = _urlInParensRx.Match(msg);
        if (m.Success && Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var uri))
            host = uri.Host;

        if (string.IsNullOrWhiteSpace(host))
        {
            var rm = _respHostRx.Match(msg);
            if (rm.Success) host = rm.Groups[1].Value;
        }

        if (string.IsNullOrWhiteSpace(host)) return;
        host = host.Trim().ToLowerInvariant();
        _hostErrorCounts[host] = _hostErrorCounts.TryGetValue(host, out var n) ? n + 1 : 1;
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
        var analyzer = BuildAnalyzerReport(includeActions: false);
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

        var totalRelayOk = RelayDetails.Sum(r => r.Ok);
        var totalRelayErr = RelayDetails.Sum(r => r.Err);
        var totalRelayReq = totalRelayOk + totalRelayErr;
        var totalUses = RelayDetails.Sum(r => Math.Max(0, r.Uses));
        var parkedCount = RelayDetails.Count(r => r.Parked);
        var dominant = totalUses > 0
            ? RelayDetails.OrderByDescending(r => r.Uses).FirstOrDefault()
            : null;
        var dominanceRatio = dominant != null && totalUses > 0 ? (double)dominant.Uses / totalUses : 0.0;
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
            analysis = new
            {
                relay_total_ok = totalRelayOk,
                relay_total_err = totalRelayErr,
                relay_total_requests = totalRelayReq,
                relay_error_rate = totalRelayReq > 0 ? Math.Round((double)totalRelayErr / totalRelayReq, 4) : 0.0,
                parked_relays = parkedCount,
                parked_ratio = RelayDetails.Count > 0 ? Math.Round((double)parkedCount / RelayDetails.Count, 4) : 0.0,
                dominant_relay = dominant != null ? ShortRelayId(dominant.Id) : "--",
                dominant_use_ratio = Math.Round(dominanceRatio, 4),
                routing_balance = dominanceRatio > 0.70 ? "imbalanced" : (dominanceRatio > 0.50 ? "mixed" : "balanced"),
                health_grade = _last.SuccessRate >= 0.95 ? "A" : (_last.SuccessRate >= 0.85 ? "B" : (_last.SuccessRate >= 0.70 ? "C" : "D")),
                analyzer_risk_score = analyzer.RiskScore,
                analyzer_grade = analyzer.Grade,
                analyzer_primary_cause = analyzer.PrimaryCause,
            },
            insights = BuildQuickInsights(analyzer),
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
        var analyzer = BuildAnalyzerReport(includeActions: false);
        var topFail = RelayDetails
            .OrderByDescending(r => r.RecentFailures)
            .ThenBy(r => r.Window1M.SuccessRate)
            .Take(3)
            .ToList();
        var topHot = RelayDetails
            .OrderByDescending(r => r.Uses)
            .Take(2)
            .ToList();
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
        if (!SysProxyOn && _core.IsRunning && _last.WindowRequests > 0)
            sb.AppendLine("Proxy mode: manual/browser-scoped (system-wide proxy intentionally disabled).");
        sb.AppendLine($"Strategy: {MultiIdStrategy}, MaxConsecutive: {MultiIdMaxConsecutive}");
        sb.AppendLine($"Relays enabled/configured: {EnabledRelaysCountLabel}/{ConfiguredRelaysCountLabel}");
        sb.AppendLine($"Cache: {(CacheEnabled ? "Enabled" : "Disabled")} | hit {CacheHitRateLabel} | effective {CacheEffectiveHitRateLabel} | stale hits {CacheStaleHitsLabel} | size {CacheSizeLabel}");
        sb.AppendLine($"Bypass domains: {(_cfg.DirectBypassDomains?.Count ?? 0)}");
        sb.AppendLine($"Telegram no-MITM hosts/CIDRs: {(_cfg.NoMitmHosts?.Count ?? 0)}/{(_cfg.NoMitmCidrs?.Count ?? 0)}");
        sb.AppendLine($"Runtime health grade: {(_last.SuccessRate >= 0.95 ? "A" : (_last.SuccessRate >= 0.85 ? "B" : (_last.SuccessRate >= 0.70 ? "C" : "D")))}");
        sb.AppendLine($"Analyzer grade: {analyzer.Grade} ({analyzer.RiskScore}/100)");
        sb.AppendLine($"Primary cause: {analyzer.PrimaryCause}");
        sb.AppendLine($"Window: success {WindowSuccessRateLabel}, requests {WindowRequestsLabel}, errors {WindowErrorsLabel}");
        sb.AppendLine($"Trend throughput: {ThroughputTrendLabel}");
        sb.AppendLine($"Trend latency: {LatencyTrendLabel}");
        if (topHot.Count > 0)
            sb.AppendLine("Top used relays: " + string.Join(", ", topHot.Select(r => $"{ShortRelayId(r.Id)} ({r.Uses} uses)")));
        if (topFail.Count > 0)
            sb.AppendLine("Relays to watch: " + string.Join(", ", topFail.Select(r =>
                $"{ShortRelayId(r.Id)} ({r.RecentFailures} fails, 1m {(r.Window1M.SuccessRate * 100):0.#}%)")));
        var insights = BuildQuickInsights(analyzer);
        if (insights.Count > 0)
        {
            sb.AppendLine("Insights:");
            foreach (var i in insights) sb.AppendLine($"  - {i}");
        }
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
        var analyzer = BuildAnalyzerReport(includeActions: false);
        var relaySpread = RelayDetails
            .OrderByDescending(r => r.Uses)
            .Select(r => new { id = ShortRelayId(r.Id), uses = r.Uses })
            .ToList();
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
                window_success_rate = WindowSuccessRateLabel,
                window_requests = WindowRequestsLabel,
                window_errors = WindowErrorsLabel,
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
                relay_cb_threshold = RelayCbThreshold,
                relay_cb_cooldown = RelayCbCooldown,
                relay_timeout = RelayTimeout,
                script_blacklist_ttl_s = ScriptBlacklistTtlS,
                retry_safe_attempts = RetrySafeAttempts,
                retry_backoff_base_ms = RetryBackoffBaseMs,
                auto_google_ip_refresh = AutoGoogleIpRefresh,
                google_ip_refresh_interval_s = GoogleIpRefreshIntervalS,
                google_ip_probe_timeout_s = GoogleIpProbeTimeoutS,
                google_ip_probe_sample_size = GoogleIpProbeSampleSize,
                google_ip_switch_min_improvement_ms = GoogleIpSwitchMinImprovementMs,
            },
            analysis = new
            {
                analyzer_risk_score = analyzer.RiskScore,
                analyzer_grade = analyzer.Grade,
                analyzer_primary_cause = analyzer.PrimaryCause,
                insights = BuildQuickInsights(analyzer),
                throughput_trend = ThroughputTrendLabel,
                latency_trend = LatencyTrendLabel,
                relay_spread = relaySpread,
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

    List<string> BuildQuickInsights(AnalyzerReport? report = null)
    {
        var insights = new List<string>();
        if (report != null)
        {
            insights.Add($"Analyzer risk: {report.RiskScore}/100 (grade {report.Grade})");
            if (string.Equals(ActivePreset, "god", StringComparison.OrdinalIgnoreCase))
                insights.Add("God Mode is active: aggressive-safe tuning profile is enabled.");
            if (!string.IsNullOrWhiteSpace(report.PrimaryCause) &&
                !report.PrimaryCause.Equals("Stable", StringComparison.OrdinalIgnoreCase))
            {
                insights.Add($"Primary bottleneck: {report.PrimaryCause}");
            }
            if (report.Actions.Count > 0)
                insights.Add($"{report.Actions.Count} safe analyzer actions are ready to apply.");
        }
        if (!SysProxyOn)
        {
            if (_core.IsRunning && _last.WindowRequests > 0)
                insights.Add("Manual/browser-scoped proxy mode is active (system-wide proxy remains off).");
            else
                insights.Add("System proxy is disabled; apps outside browser/manual config may bypass tunnel.");
        }
        if (_last.SuccessRate < 0.85) insights.Add($"Global success rate is low ({SuccessRateLabel}); prioritize reliability tuning.");
        if (_last.WindowRequests > 0 && _last.WindowErrors > 0)
        {
            var errRate = (double)_last.WindowErrors / _last.WindowRequests;
            if (errRate > 0.15) insights.Add($"Recent error pressure is elevated ({errRate * 100:0.#}% errors in window).");
        }
        if (_last.LatencyMs > 2200 || _probeLatencyMs > 2200)
            insights.Add($"Latency is high ({LatencyLabel}); reduce parallel load and verify front IP quality.");
        if (_last.LatencyMs > 2500)
            insights.Add("For best results, run Google IP scan and update `google_ip` to the fastest reachable candidate.");
        if ((_cfg.NoMitmHosts?.Count ?? 0) == 0 || (_cfg.NoMitmCidrs?.Count ?? 0) == 0)
            insights.Add("Telegram no-MITM coverage appears incomplete; add both Telegram hosts and CIDR ranges.");
        else
            insights.Add($"Telegram no-MITM profile is active ({_cfg.NoMitmHosts!.Count} hosts, {_cfg.NoMitmCidrs!.Count} CIDRs).");
        if (DcFailoverAttempts < 2)
            insights.Add("Telegram DC failover attempts are low; use at least 2 for unstable links.");
        if (HalfOpenRxTimeoutS > 40)
            insights.Add("Half-open timeout is high; Telegram reconnect can be delayed on congested networks.");
        if (!CacheEnabled) insights.Add("Cache is disabled; repeated content cannot be reused.");
        else if (_last.CacheHitRate < 0.35) insights.Add($"Cache effectiveness is low (hit {CacheHitRateLabel}); consider longer TTL.");
        var parked = RelayDetails.Count(r => r.Parked);
        if (RelayDetails.Count > 0 && parked > 0)
            insights.Add($"{parked}/{RelayDetails.Count} relays are currently parked.");
        var totalUses = RelayDetails.Sum(r => Math.Max(0, r.Uses));
        if (totalUses > 0)
        {
            var top = RelayDetails.OrderByDescending(r => r.Uses).First();
            var dominance = (double)top.Uses / totalUses;
            if (dominance > 0.70)
                insights.Add($"Relay distribution is imbalanced; {ShortRelayId(top.Id)} handles {dominance * 100:0.#}% of uses.");
        }
        return insights;
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
        _hostErrorCounts.Clear();
        Raise(nameof(SessionDurationLabel));
        Raise(nameof(SessionAvgLatencyLabel));
        Raise(nameof(SessionAvgRpsLabel));
        Raise(nameof(SessionAvgSuccessLabel));
        Raise(nameof(TopFailingHostLabel));
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
