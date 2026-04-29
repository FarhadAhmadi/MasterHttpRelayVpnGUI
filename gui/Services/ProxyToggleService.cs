using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MasterRelayVPN.Services;

public static class ProxyToggleService
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public sealed class ProxyState
    {
        public int ProxyEnable { get; init; }
        public string ProxyServer { get; init; } = "";
        public string ProxyOverride { get; init; } = "";
        public string AutoConfigUrl { get; init; } = "";
    }

    public static ProxyState Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false)
            ?? throw new InvalidOperationException("Cannot open Internet Settings.");
        return new ProxyState
        {
            ProxyEnable = key.GetValue("ProxyEnable") is int i ? i : 0,
            ProxyServer = key.GetValue("ProxyServer") as string ?? "",
            ProxyOverride = key.GetValue("ProxyOverride") as string ?? "",
            AutoConfigUrl = key.GetValue("AutoConfigURL") as string ?? "",
        };
    }

    public static void Enable(string host, int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open Internet Settings.");

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
        key.SetValue("ProxyOverride",
            "<local>;localhost;127.0.0.1;10.*;192.168.*;172.16.*;*.local",
            RegistryValueKind.String);

        Refresh();
    }

    public static void Restore(ProxyState state)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key == null) return;

        key.SetValue("ProxyEnable", state.ProxyEnable, RegistryValueKind.DWord);
        if (string.IsNullOrWhiteSpace(state.ProxyServer)) key.DeleteValue("ProxyServer", false);
        else key.SetValue("ProxyServer", state.ProxyServer, RegistryValueKind.String);

        if (string.IsNullOrWhiteSpace(state.ProxyOverride)) key.DeleteValue("ProxyOverride", false);
        else key.SetValue("ProxyOverride", state.ProxyOverride, RegistryValueKind.String);

        if (string.IsNullOrWhiteSpace(state.AutoConfigUrl)) key.DeleteValue("AutoConfigURL", false);
        else key.SetValue("AutoConfigURL", state.AutoConfigUrl, RegistryValueKind.String);

        Refresh();
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key == null) return;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        Refresh();
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue("ProxyEnable") is int i && i == 1;
    }

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool InternetSetOption(IntPtr h, int opt, IntPtr buf, int len);

    static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }
}
