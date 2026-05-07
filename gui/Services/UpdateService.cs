using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MasterRelayVPN.Models;

namespace MasterRelayVPN.Services;

public sealed record UpdateCheckResult(
    bool Success,
    bool IsUpdateAvailable,
    string Message,
    string CurrentVersion,
    string LatestVersion,
    string Channel,
    string DownloadUrl);

public sealed class UpdateService
{
    readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    public async Task<UpdateCheckResult> CheckAsync(AppConfig cfg, string currentVersion, CancellationToken ct)
    {
        try
        {
            var channel = NormalizeChannel(cfg.UpdateChannel);
            var metadataUrl = (cfg.UpdateMetadataUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(metadataUrl))
            {
                return new UpdateCheckResult(
                    Success: false,
                    IsUpdateAvailable: false,
                    Message: "No update metadata URL configured.",
                    CurrentVersion: currentVersion,
                    LatestVersion: currentVersion,
                    Channel: channel,
                    DownloadUrl: "");
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
            req.Headers.TryAddWithoutValidation("X-Update-Channel", channel);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    false, false, $"Update server returned {(int)resp.StatusCode}.",
                    currentVersion, currentVersion, channel, "");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var metadata = JsonSerializer.Deserialize<UpdateMetadata>(json);
            if (metadata == null)
            {
                return new UpdateCheckResult(
                    false, false, "Invalid update metadata payload.",
                    currentVersion, currentVersion, channel, "");
            }

            if (!VerifySignature(json, cfg.UpdatePublicKeyPem))
            {
                return new UpdateCheckResult(
                    false, false, "Update signature verification failed.",
                    currentVersion, currentVersion, channel, "");
            }

            var latest = metadata.GetVersionForChannel(channel) ?? currentVersion;
            var download = metadata.GetDownloadForChannel(channel) ?? "";
            var update = IsNewer(latest, currentVersion);
            return new UpdateCheckResult(
                true,
                update,
                update ? "Update available." : "Already up to date.",
                currentVersion,
                latest,
                channel,
                download
            );
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(false, false, "Update check cancelled.", currentVersion, currentVersion, NormalizeChannel(cfg.UpdateChannel), "");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, $"Update check failed: {ex.Message}", currentVersion, currentVersion, NormalizeChannel(cfg.UpdateChannel), "");
        }
    }

    static bool VerifySignature(string metadataJson, string? publicKeyPem)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(publicKeyPem)) return false;
            var metadata = JsonSerializer.Deserialize<UpdateMetadata>(metadataJson);
            if (metadata == null || string.IsNullOrWhiteSpace(metadata.Signature)) return false;

            var payload = metadata.GetCanonicalPayload();
            var data = Encoding.UTF8.GetBytes(payload);
            var sig = Convert.FromBase64String(metadata.Signature);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    static bool IsNewer(string candidate, string current)
    {
        if (Version.TryParse(NormalizeVersion(candidate), out var a) &&
            Version.TryParse(NormalizeVersion(current), out var b))
            return a > b;
        return false;
    }

    static string NormalizeVersion(string value)
        => (value ?? "").Trim().TrimStart('v', 'V');

    static string NormalizeChannel(string? value)
    {
        var c = (value ?? "stable").Trim().ToLowerInvariant();
        return c is "beta" ? "beta" : "stable";
    }

    sealed class UpdateMetadata
    {
        public string? stable_version { get; set; }
        public string? beta_version { get; set; }
        public string? stable_url { get; set; }
        public string? beta_url { get; set; }
        public string Signature { get; set; } = "";

        public string? GetVersionForChannel(string channel)
            => channel == "beta" ? beta_version : stable_version;

        public string? GetDownloadForChannel(string channel)
            => channel == "beta" ? beta_url : stable_url;

        public string GetCanonicalPayload()
        {
            var obj = new
            {
                stable_version = stable_version ?? "",
                beta_version = beta_version ?? "",
                stable_url = stable_url ?? "",
                beta_url = beta_url ?? "",
            };
            return JsonSerializer.Serialize(obj);
        }
    }
}

