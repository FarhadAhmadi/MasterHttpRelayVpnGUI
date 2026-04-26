using System;

namespace MasterRelayVPN.Services;

public static class ErrorMessages
{
    public static string Friendly(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Connection failed.";

        var s = raw;
        if (Has(s, "TLS_FAIL", "SSL", "handshake", "certificate"))
            return "Secure connection failed. Try a different SNI.";
        if (Has(s, "TIMEOUT", "timed out"))
            return "Connection timed out. Check your network.";
        if (Has(s, "CONN_RESET", "reset by peer", "broken pipe"))
            return "Connection reset by your network.";
        if (Has(s, "RELAY_FAIL", "502", "Bad Gateway"))
            return "Relay server unreachable. Try again in a moment.";
        if (Has(s, "Permission denied", "AddressInUse", "already in use"))
            return "Listen port is in use. Change it in Settings.";
        if (Has(s, "Missing required config"))
            return "Missing configuration. Open Settings.";

        var clean = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (clean.Length > 120) clean = clean[..120] + "...";
        return clean;
    }

    static bool Has(string s, params string[] needles)
    {
        foreach (var n in needles)
            if (s.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
