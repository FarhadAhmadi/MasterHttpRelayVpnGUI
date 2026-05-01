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

    public static string DataDir { get; } = ResolveDataDir();
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

    static string ResolveDataDir()
    {
        // Prefer alongside EXE when writable; otherwise fall back to user profile.
        var local = Path.Combine(AppDir, "data");
        if (CanWriteDirectory(local)) return EnsureDir(local);

        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MasterRelayVPN",
            "data");
        return EnsureDir(userData);
    }

    static bool CanWriteDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
