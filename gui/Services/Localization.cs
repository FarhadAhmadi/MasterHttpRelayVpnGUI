using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MasterRelayVPN.Services;

/// <summary>
/// Static, INotifyPropertyChanged-backed string table.
/// XAML binds via {Binding Source={x:Static svc:Loc.Instance}, Path=Settings}.
/// Switching language raises a property-changed for every key, so the UI re-reads.
/// </summary>
public class Localization : INotifyPropertyChanged
{
    public static Localization Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    string _lang = "en";
    public string Lang
    {
        get => _lang;
        set
        {
            if (_lang == value) return;
            _lang = value;
            // Notify everything — XAML will refresh all bindings.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            FlowDirection = (value == "fa") ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRtl)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageButtonLabel)));
        }
    }

    public FlowDirection FlowDirection { get; private set; } = FlowDirection.LeftToRight;
    public bool IsRtl => FlowDirection == FlowDirection.RightToLeft;
    public string LanguageButtonLabel => _lang == "fa" ? "EN" : "فا";

    static readonly Dictionary<string, Dictionary<string, string>> _t = new()
    {
        ["en"] = new()
        {
            ["app_subtitle"]      = "Domain-fronted internet relay",
            ["settings"]          = "Settings",
            ["start"]             = "Start",
            ["stop"]              = "Stop",
            ["connecting"]        = "Connecting...",
            ["connected"]         = "Connected",
            ["disconnected"]      = "Disconnected",
            ["connection_failed"] = "Connection failed",
            ["health"]            = "Health",
            ["health_good"]       = "Stable",
            ["health_unstable"]   = "Unstable",
            ["health_down"]       = "Disconnected",
            ["cert"]              = "Cert",
            ["cert_trusted"]      = "Trusted",
            ["cert_not_trusted"]  = "Not Trusted",
            ["cert_missing"]      = "Missing",
            ["proxy"]             = "Proxy",
            ["proxy_on"]          = "on",
            ["proxy_off"]         = "off",
            ["download"]          = "DOWNLOAD",
            ["upload"]            = "UPLOAD",
            ["requests"]          = "REQUESTS",
            ["uptime"]            = "UPTIME",
            ["conns"]             = "CONNS",
            ["since_start"]       = "since start",
            ["activity"]          = "Activity",
            ["copy"]              = "Copy",
            ["export"]            = "Export",
            ["clear"]             = "Clear",
            ["mode"]              = "Mode",
            ["front_domain"]      = "Front domain (SNI)",
            ["custom_sni"]        = "Custom SNI override",
            ["worker_host"]       = "Worker host (CDN modes)",
            ["auth_key"]          = "Auth key",
            ["listen_host"]       = "Listen host",
            ["port"]              = "Port",
            ["network_tuning"]    = "Network tuning",
            ["fragment"]          = "Fragment (B)",
            ["chunk"]             = "Chunk (B)",
            ["parallel"]          = "Parallel",
            ["use_chunked"]       = "Use chunked / parallel downloads",
            ["enable_http2"]      = "Enable HTTP/2 (advanced)",
            ["verify_ssl"]        = "Verify upstream SSL",
            ["install_cert_user"] = "Install Certificate (User)",
            ["install_cert_sys"]  = "Install Certificate (System)",
            ["toggle_proxy"]      = "Toggle Windows Proxy",
            ["done"]              = "Done",
            ["language"]          = "Language",
            ["deployment_ids"]    = "Deployment IDs",
            ["add_deployment"]    = "+ Add Deployment ID",
            ["presets"]           = "Network presets",
            ["preset_stealth"]    = "Stealth (slow internet)",
            ["preset_balanced"]   = "Balanced",
            ["preset_speed"]      = "High Speed",
            ["preset_auto"]       = "Auto Optimize",
            ["preset_active"]     = "active",
            ["please_wait"]       = "Please wait...",
            ["setting_up"]        = "Setting things up...",
            ["preparing_cert"]    = "Preparing certificate...",
        },
        ["fa"] = new()
        {
            ["app_subtitle"]      = "رله اینترنت با Domain Fronting",
            ["settings"]          = "تنظیمات",
            ["start"]             = "شروع",
            ["stop"]              = "توقف",
            ["connecting"]        = "در حال اتصال...",
            ["connected"]         = "متصل",
            ["disconnected"]      = "قطع",
            ["connection_failed"] = "اتصال ناموفق",
            ["health"]            = "وضعیت",
            ["health_good"]       = "پایدار",
            ["health_unstable"]   = "ناپایدار",
            ["health_down"]       = "قطع",
            ["cert"]              = "گواهی",
            ["cert_trusted"]      = "مورد اعتماد",
            ["cert_not_trusted"]  = "غیر مورد اعتماد",
            ["cert_missing"]      = "موجود نیست",
            ["proxy"]             = "پروکسی",
            ["proxy_on"]          = "روشن",
            ["proxy_off"]         = "خاموش",
            ["download"]          = "دانلود",
            ["upload"]            = "آپلود",
            ["requests"]          = "درخواست‌ها",
            ["uptime"]            = "زمان فعالیت",
            ["conns"]             = "اتصالات",
            ["since_start"]       = "از زمان شروع",
            ["activity"]          = "فعالیت",
            ["copy"]              = "کپی",
            ["export"]            = "خروجی",
            ["clear"]             = "پاک‌کردن",
            ["mode"]              = "حالت",
            ["front_domain"]      = "دامنه پیش‌رو (SNI)",
            ["custom_sni"]        = "SNI سفارشی",
            ["worker_host"]       = "میزبان Worker",
            ["auth_key"]          = "کلید احراز هویت",
            ["listen_host"]       = "آدرس گوش‌دادن",
            ["port"]              = "پورت",
            ["network_tuning"]    = "تنظیمات شبکه",
            ["fragment"]          = "اندازه قطعه (B)",
            ["chunk"]             = "اندازه بسته (B)",
            ["parallel"]          = "همزمانی",
            ["use_chunked"]       = "استفاده از دانلود قطعه‌بندی‌شده / موازی",
            ["enable_http2"]      = "فعال‌سازی HTTP/2 (پیشرفته)",
            ["verify_ssl"]        = "بررسی SSL سرور",
            ["install_cert_user"] = "نصب گواهی (کاربر)",
            ["install_cert_sys"]  = "نصب گواهی (سیستم)",
            ["toggle_proxy"]      = "روشن/خاموش پروکسی ویندوز",
            ["done"]              = "پایان",
            ["language"]          = "زبان",
            ["deployment_ids"]    = "شناسه‌های Deployment",
            ["add_deployment"]    = "+ افزودن شناسه",
            ["presets"]           = "پیش‌تنظیمات شبکه",
            ["preset_stealth"]    = "مخفی‌کاری (اینترنت کند)",
            ["preset_balanced"]   = "متعادل",
            ["preset_speed"]      = "حداکثر سرعت",
            ["preset_auto"]       = "بهینه‌سازی خودکار",
            ["preset_active"]     = "فعال",
            ["please_wait"]       = "لطفا صبر کنید...",
            ["setting_up"]        = "در حال راه‌اندازی...",
            ["preparing_cert"]    = "آماده‌سازی گواهی...",
        }
    };

    /// <summary>Indexer used from XAML: {Binding [start], Source=...}.</summary>
    public string this[string key]
    {
        get
        {
            if (_t.TryGetValue(_lang, out var dict) && dict.TryGetValue(key, out var v)) return v;
            if (_t["en"].TryGetValue(key, out var en)) return en;
            return key;
        }
    }

    public void Toggle() => Lang = (_lang == "en") ? "fa" : "en";
}
