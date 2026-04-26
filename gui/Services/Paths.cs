using System;
using System.IO;

namespace MasterRelayVPN.Services;

/// <summary>
/// Layout next to MasterRelayVPN.exe:
///   data\config.json
///   data\cert\ca.crt + ca.key
///   data\logs\
///   core\MasterRelayCore.exe
/// </summary>
public static class Paths
{
    public static string AppDir => AppContext.BaseDirectory;

    public static string DataDir { get; } = EnsureDir(Path.Combine(AppContext.BaseDirectory, "data"));
    public static string CertDir { get; } = EnsureDir(Path.Combine(DataDir, "cert"));
    public static string LogsDir { get; } = EnsureDir(Path.Combine(DataDir, "logs"));

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string CaCert     => Path.Combine(CertDir, "ca.crt");
    public static string CaKey      => Path.Combine(CertDir, "ca.key");

    public static string CoreExe
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CORE_PATH");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

            string[] candidates =
            {
                Path.Combine(AppDir, "core", "MasterRelayCore.exe"),
                Path.Combine(AppDir, "MasterRelayCore.exe"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            return candidates[0];
        }
    }

    static string EnsureDir(string p)
    {
        try { Directory.CreateDirectory(p); } catch { }
        return p;
    }
}
