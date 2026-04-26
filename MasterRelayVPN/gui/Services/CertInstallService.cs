using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace MasterRelayVPN.Services;

public enum CertResult { Installed, AlreadyTrusted, MissingFile, UserCancelled, Failed }
public record CertOutcome(CertResult Result, string Message);

public static class CertInstallService
{
    public static bool CertExists() => File.Exists(Paths.CaCert);

    public static bool IsTrusted()
    {
        if (!CertExists()) return false;
        try
        {
            using var target = new X509Certificate2(Paths.CaCert);
            foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                try
                {
                    using var store = new X509Store(StoreName.Root, loc);
                    store.Open(OpenFlags.ReadOnly);
                    foreach (var c in store.Certificates)
                        if (string.Equals(c.Thumbprint, target.Thumbprint,
                                          StringComparison.OrdinalIgnoreCase))
                            return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    public static CertOutcome InstallCurrentUser()
    {
        if (!CertExists())
            return new(CertResult.MissingFile, "Certificate not generated yet.");

        try
        {
            using var cert = new X509Certificate2(Paths.CaCert);
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            foreach (var c in store.Certificates)
            {
                if (string.Equals(c.Thumbprint, cert.Thumbprint,
                                  StringComparison.OrdinalIgnoreCase))
                    return new(CertResult.AlreadyTrusted, "Already trusted.");
            }

            store.Add(cert);
            return new(CertResult.Installed, "Certificate installed.");
        }
        catch (System.Security.Cryptography.CryptographicException cex)
            when (cex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                  cex.HResult == unchecked((int)0x800704C7))
        {
            return new(CertResult.UserCancelled, "Cancelled by user.");
        }
        catch (Exception ex)
        {
            return new(CertResult.Failed, ex.Message);
        }
    }

    public static async Task<CertOutcome> InstallMachineAsync()
    {
        if (!CertExists())
            return new(CertResult.MissingFile, "Certificate not generated yet.");

        var path = Paths.CaCert.Replace("'", "''");
        var ps = $@"try {{ Import-Certificate -FilePath '{path}' -CertStoreLocation Cert:\LocalMachine\Root -ErrorAction Stop | Out-Null; exit 0 }} catch {{ exit 1 }}";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps.Replace("\"", "\\\"")}\"",
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return new(CertResult.Failed, "Could not start installer.");
            await Task.Run(() => p.WaitForExit());
            return p.ExitCode == 0
                ? new(CertResult.Installed, "Certificate installed.")
                : new(CertResult.Failed, "Installer reported an error.");
        }
        catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            return new(CertResult.UserCancelled, "Cancelled by user.");
        }
        catch (Exception ex)
        {
            return new(CertResult.Failed, ex.Message);
        }
    }
}
