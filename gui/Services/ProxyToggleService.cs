using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MasterRelayVPN.Services;

public static class ProxyToggleService
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

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
