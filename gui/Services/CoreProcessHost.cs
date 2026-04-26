using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MasterRelayVPN.Models;

namespace MasterRelayVPN.Services;

public class CoreProcessHost : IDisposable
{
    Process? _proc;
    CancellationTokenSource? _cts;
    readonly object _lock = new();

    public event Action<LogEntry>? LogReceived;
    public event Action<StatsSnapshot>? StatsReceived;
    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;

    public bool IsRunning
    {
        get { lock (_lock) { return _proc is { HasExited: false }; } }
    }

    public bool CoreExeExists() => File.Exists(Paths.CoreExe);

    public async Task<bool> GenerateCaAsync()
    {
        if (!CoreExeExists()) return false;

        var psi = new ProcessStartInfo
        {
            FileName = Paths.CoreExe,
            Arguments = "--gen-ca",
            WorkingDirectory = Path.GetDirectoryName(Paths.CoreExe)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["MRELAY_CA_DIR"] = Paths.CertDir;

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            await Task.Run(() => p.WaitForExit(15000));
            return File.Exists(Paths.CaCert);
        }
        catch { return false; }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_proc is { HasExited: false })
                throw new InvalidOperationException("Core is already running.");

            if (!CoreExeExists())
                throw new FileNotFoundException(
                    "Engine is missing. Please reinstall the app.", Paths.CoreExe);

            var psi = new ProcessStartInfo
            {
                FileName = Paths.CoreExe,
                Arguments = $"--config \"{Paths.ConfigFile}\"",
                WorkingDirectory = Path.GetDirectoryName(Paths.CoreExe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["MRELAY_CA_DIR"] = Paths.CertDir;

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.Exited += OnExited;

            if (!_proc.Start())
                throw new InvalidOperationException("Failed to start engine.");

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReadStdout(_proc, _cts.Token));
            _ = Task.Run(() => ReadStderr(_proc, _cts.Token));

            StatusChanged?.Invoke("Connecting");
        }
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        Process? p;
        lock (_lock)
        {
            p = _proc;
            if (p == null || p.HasExited) { StatusChanged?.Invoke("Stopped"); return; }
        }

        try
        {
            if (p.StandardInput.BaseStream.CanWrite) p.StandardInput.Close();
        }
        catch { }

        var done = await Task.Run(() => p.WaitForExit((int)timeout.TotalMilliseconds));
        if (!done)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            await Task.Run(() => p.WaitForExit(3000));
        }

        try { _cts?.Cancel(); } catch { }
        StatusChanged?.Invoke("Stopped");
    }

    void OnExited(object? sender, EventArgs e)
    {
        int code = -1;
        try { code = _proc?.ExitCode ?? -1; } catch { }
        ProcessExited?.Invoke(code);
        StatusChanged?.Invoke(code == 0 ? "Stopped" : "Error");
    }

    static readonly Regex LogRe = new(
        @"^(?<ts>\d\d:\d\d:\d\d)\s+\[(?<src>[^\]]+)\]\s+(?<lvl>DEBUG|INFO|WARNING|ERROR)\s+(?<msg>.*)$",
        RegexOptions.Compiled);

    void ReadStdout(Process proc, CancellationToken ct)
    {
        try
        {
            using var r = proc.StandardOutput;
            string? line;
            while (!ct.IsCancellationRequested && (line = r.ReadLine()) != null)
            {
                if (line.StartsWith("##STATS## ", StringComparison.Ordinal))
                {
                    try
                    {
                        var s = JsonSerializer.Deserialize<StatsSnapshot>(line.AsSpan(10).ToString());
                        if (s != null)
                        {
                            StatsReceived?.Invoke(s);
                            StatusChanged?.Invoke("Running");
                        }
                    }
                    catch { }
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    LogReceived?.Invoke(new LogEntry(DateTime.Now, LogLevel.Info, "stdout", line));
                }
            }
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke(new LogEntry(DateTime.Now, LogLevel.Error, "host",
                "stdout reader: " + ex.Message));
        }
    }

    void ReadStderr(Process proc, CancellationToken ct)
    {
        try
        {
            using var r = proc.StandardError;
            string? line;
            while (!ct.IsCancellationRequested && (line = r.ReadLine()) != null)
                LogReceived?.Invoke(ParseLog(line));
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke(new LogEntry(DateTime.Now, LogLevel.Error, "host",
                "stderr reader: " + ex.Message));
        }
    }

    static LogEntry ParseLog(string line)
    {
        var m = LogRe.Match(line);
        if (!m.Success) return new LogEntry(DateTime.Now, LogLevel.Info, "core", line);

        var lvl = m.Groups["lvl"].Value switch
        {
            "DEBUG"   => LogLevel.Debug,
            "WARNING" => LogLevel.Warning,
            "ERROR"   => LogLevel.Error,
            _         => LogLevel.Info
        };
        return new LogEntry(DateTime.Now, lvl,
            m.Groups["src"].Value.Trim(), m.Groups["msg"].Value);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_proc is { HasExited: false })
                _proc.Kill(entireProcessTree: true);
        }
        catch { }
        _proc?.Dispose();
    }
}
