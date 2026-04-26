using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MasterRelayVPN.Models;

namespace MasterRelayVPN.Services;

public class FirstRunService
{
    public record Report(bool ConfigCreated, bool CertGenerated, bool CertTrusted, string? Message);

    readonly ConfigService _cfg = new();
    readonly CoreProcessHost _core = new();

    public async Task<Report> RunAsync()
    {
        bool createdConfig = false;
        bool certGenerated = false;
        bool certTrusted = false;
        string? msg = null;

        try
        {
            var cfg = _cfg.Load();
            if (!cfg.FirstRunDone)
            {
                ApplyDefaults(cfg);
                cfg.FirstRunDone = true;
                _cfg.Save(cfg);
                createdConfig = true;
            }
        }
        catch (Exception ex)
        {
            msg = "Could not write settings: " + ex.Message;
        }

        try
        {
            if (!CertInstallService.CertExists() && _core.CoreExeExists())
            {
                var ok = await _core.GenerateCaAsync();
                certGenerated = ok && CertInstallService.CertExists();
            }
        }
        catch { }

        try
        {
            if (CertInstallService.CertExists())
            {
                if (CertInstallService.IsTrusted())
                {
                    certTrusted = true;
                }
                else
                {
                    var outcome = CertInstallService.InstallCurrentUser();
                    certTrusted = outcome.Result is CertResult.Installed or CertResult.AlreadyTrusted;
                }
            }
        }
        catch { }

        return new Report(createdConfig, certGenerated, certTrusted, msg);
    }

    static void ApplyDefaults(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Mode)) cfg.Mode = "apps_script";
        if (string.IsNullOrWhiteSpace(cfg.GoogleIp)) cfg.GoogleIp = "216.239.38.120";
        if (string.IsNullOrWhiteSpace(cfg.FrontDomain)) cfg.FrontDomain = "www.google.com";
        if (string.IsNullOrWhiteSpace(cfg.ListenHost)) cfg.ListenHost = "127.0.0.1";
        if (cfg.ListenPort <= 0 || cfg.ListenPort > 65535) cfg.ListenPort = 8085;

        cfg.EnableHttp2 = false;
        if (cfg.ChunkSize <= 0)    cfg.ChunkSize = 131072;
        if (cfg.MaxParallel <= 0)  cfg.MaxParallel = 4;
        if (cfg.FragmentSize <= 0) cfg.FragmentSize = 16384;

        if (string.IsNullOrWhiteSpace(cfg.LogLevel)) cfg.LogLevel = "INFO";
        if (string.IsNullOrWhiteSpace(cfg.AuthKey)) cfg.AuthKey = NewKey();
    }

    static string NewKey()
    {
        var b = new byte[24];
        RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b).Replace("/", "_").Replace("+", "-").TrimEnd('=');
    }
}
