using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MasterRelayVPN.Models;

public class RelayConfigItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public class AppConfig
{
    public static readonly string[] DefaultRelayIds =
    {
        // "AKfycbywYpxZaAU7w9szeU2Z2pT24tiqpac1Y0fXuF1bJzgKpdkT3EPofJIhcS9HapxwxLnu",
        // "AKfycbzwkuJaZx7PGcFuIEUTcs_Ux_wxTEe3rAZpQY0M6hSY31yDSvDKPhVyq6hjY7BoX4cq",
        // "AKfycbwRMoX_2zsIP3QUI8RF1AC2dPf3AgFUNmTKzEARsWrVSPA4tZuB45BEiZkxc0g66EmC",
        // "AKfycbxtSFeu-loescQ5Gu0g4YJh4nOuk94o-wAzS53Bo4GxAlAxWSIz7VrcgTrWUeGh-EOV",
        // "AKfycbxGHR6esfw-ix7vjnE0x1_R-6JJsRvc1a9i78-LEMDQpkN6NyLMCuMz_FJPCoRJNj0XvA",
        // "AKfycbxHK1J6Vx_Qg9if9EhcxPUcvKFbGzy6b2wZjJLwxdaSed2kL7FGkhsmeU51LHVwexEe",
        // "AKfycbymG-6pd795SzZLfbm1sLi8l1Xw_Fx0JqINYC6F_iuJ9eFN8KCcj2QSoPcE97cZoeEM",
        // "AKfycbzycg-5dmL6hwLgRMCXwUimk6YcVEMWidnYCySX5eZ7BO5I0Fc66QOPhYEMFXxMZlK8",
        // "AKfycbyVzgeXOv1fe1w77QTHfVf-eWvTC070jReOhdsTqMmK6StuH-wfYnpAJR196xw3JACUoA",
        // "AKfycbyF9h38JbbKOhPJo98UvJCH6HKglklLbrq7UzPtq5z2OMibOgx4yhSiLIqoF4SQB6QA",
        // "AKfycbxpfRwZW9LuSGVEhiIXDppeYWZkkHkZNwwT6hIIQ2-5y8ogED1jGSlK1uM4_s9PGpVcBg",
        // "AKfycby3DFO5gGfzwZaoqFaVc3o5viZoDLTGhkpihH4_bbbq_I9doLeV-xFMQcWSYWAyttnE",
        // "AKfycbzf5jRWZcKb_jrCB-_S6B-D5GGPV1uMIb5ANJtsxfcX5j7yZDx_5ZBuarrY-WrcmHQF",
        // "AKfycbwlcKFtRmjdD1ZuCuAQcnz4htu_o7ONXMPhd2VwZS8I-_J_d1_r1NTzWS8r2QQeYnb5kA",
        // "AKfycbwTF2qVoBSq8a9BcIz1w243OEDjvKefy3poLqqoYWfcCgjsSi6IZsfsLSEg_4R7EPyF",
        // "AKfycbwQ4iNFunOQmpbIBP0ekXsjqMKbiH2_HWQbsYuo5hrJj_04ghLg38iBzekqbl3TcAW0",
        // "AKfycbyZnyUDB-Lz75o69MAxQvfhrxbSmSNUf-bravwi7XwQSCinCnDFNM8Fd-61jISpYBTt"
        //new version of code.gs
        "AKfycbz_hhjOzk-TA_dk7jOzgOmeHArokMgKRdQxuN1LHJxaYgmZUIEEKFiP-ULpKhnD8ZYfeQ",
        "AKfycbz5dMbDwFE7UfxbZ0VI2oDDR_9Qy7PfPyBn9UHDufpY4u7jt3LWrD5VLnChNAtjiR2L",
        "AKfycbw6GhntNTbXgYpTFCf-HD2uX1ea1tbLWDAWpTyAvYTmnBQrjXR1XAMQ8M0Fvp1NzQSL2A",
        "AKfycbx6YfMLIkNNTeoO4g4h2VkLwGY-e_bS97IyvMWANHa14ccHd8ejBfLLVy7cjS4UnhS9Uw"
    };

    [JsonPropertyName("mode")]            public string Mode { get; set; } = "apps_script";
    [JsonPropertyName("safe_defaults_lock")] public bool SafeDefaultsLock { get; set; } = true;
    [JsonPropertyName("google_ip")]       public string? GoogleIp { get; set; } = "216.239.38.120";
    [JsonPropertyName("front_domain")]    public string? FrontDomain { get; set; } = "www.google.com";
    [JsonPropertyName("custom_sni")]      public string? CustomSni { get; set; } = "";

    // Single-ID stays for backward compat. Multi-ID list is preferred.
    [JsonPropertyName("script_id")]       public string? ScriptId { get; set; } = DefaultRelayIds[0];
    [JsonPropertyName("script_ids")]      public List<string> ScriptIds { get; set; } = new(DefaultRelayIds);
    [JsonPropertyName("relay_items")]     public List<RelayConfigItem> RelayItems { get; set; } = new()
    {
        new RelayConfigItem { Id = DefaultRelayIds[0], Enabled = true },
        new RelayConfigItem { Id = DefaultRelayIds[1], Enabled = true },
        new RelayConfigItem { Id = DefaultRelayIds[2], Enabled = true },
        new RelayConfigItem { Id = DefaultRelayIds[3], Enabled = true },
        // new RelayConfigItem { Id = DefaultRelayIds[4], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[5], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[6], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[7], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[8], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[9], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[10], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[11], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[12], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[13], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[14], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[15], Enabled = false },
        // new RelayConfigItem { Id = DefaultRelayIds[16], Enabled = false },
        
    };

    [JsonPropertyName("worker_host")]     public string? WorkerHost { get; set; }
    [JsonPropertyName("custom_domain")]   public string? CustomDomain { get; set; }
    [JsonPropertyName("auth_key")]        public string AuthKey { get; set; } = "CHANGE_ME_TO_A_STRONG_SECRET";
    [JsonPropertyName("listen_host")]     public string ListenHost { get; set; } = "127.0.0.1";
    [JsonPropertyName("lan_sharing")]     public bool LanSharing { get; set; } = false;
    [JsonPropertyName("listen_port")]     public int ListenPort { get; set; } = 8085;
    [JsonPropertyName("log_level")]       public string LogLevel { get; set; } = "INFO";
    [JsonPropertyName("verify_ssl")]      public bool VerifySsl { get; set; } = true;

    [JsonPropertyName("enable_http2")]    public bool EnableHttp2 { get; set; } = false;
    [JsonPropertyName("enable_chunked")]  public bool EnableChunked { get; set; } = true;
    [JsonPropertyName("chunk_size")]      public int ChunkSize { get; set; } = 131072;
    [JsonPropertyName("max_parallel")]    public int MaxParallel { get; set; } = 3;
    [JsonPropertyName("fragment_size")]   public int FragmentSize { get; set; } = 16384;
    [JsonPropertyName("cache_enabled")]   public bool CacheEnabled { get; set; } = true;
    [JsonPropertyName("cache_max_mb")]    public int CacheMaxMb { get; set; } = 160;
    [JsonPropertyName("cache_default_ttl_s")] public int CacheDefaultTtlS { get; set; } = 900;
    [JsonPropertyName("cache_stale_if_error_s")] public int CacheStaleIfErrorS { get; set; } = 240;
    [JsonPropertyName("relay_cb_threshold")] public int RelayCbThreshold { get; set; } = 3;
    [JsonPropertyName("relay_cb_cooldown")] public int RelayCbCooldown { get; set; } = 20;
    [JsonPropertyName("relay_timeout")]   public int RelayTimeout { get; set; } = 35;
    [JsonPropertyName("script_blacklist_ttl_s")] public int ScriptBlacklistTtlS { get; set; } = 240;
    [JsonPropertyName("retry_safe_attempts")] public int RetrySafeAttempts { get; set; } = 2;
    [JsonPropertyName("retry_backoff_base_ms")] public int RetryBackoffBaseMs { get; set; } = 140;
    [JsonPropertyName("auto_google_ip_refresh")] public bool AutoGoogleIpRefresh { get; set; } = true;
    [JsonPropertyName("google_ip_refresh_interval_s")] public int GoogleIpRefreshIntervalS { get; set; } = 600;
    [JsonPropertyName("google_ip_probe_timeout_s")] public int GoogleIpProbeTimeoutS { get; set; } = 3;
    [JsonPropertyName("google_ip_probe_sample_size")] public int GoogleIpProbeSampleSize { get; set; } = 8;
    [JsonPropertyName("google_ip_switch_min_improvement_ms")] public int GoogleIpSwitchMinImprovementMs { get; set; } = 120;
    [JsonPropertyName("watchdog_enabled")] public bool WatchdogEnabled { get; set; } = true;
    [JsonPropertyName("watchdog_failure_threshold")] public int WatchdogFailureThreshold { get; set; } = 4;
    [JsonPropertyName("watchdog_cooldown_s")] public int WatchdogCooldownS { get; set; } = 45;
    [JsonPropertyName("relay_sign_requests")] public bool RelaySignRequests { get; set; } = false;
    [JsonPropertyName("relay_signing_key")] public string RelaySigningKey { get; set; } = "";
    [JsonPropertyName("relay_sign_version")] public int RelaySignVersion { get; set; } = 1;

    // Multi-ID tuning
    [JsonPropertyName("multi_id_fail_threshold")]   public int MultiIdFailThreshold { get; set; } = 2;
    [JsonPropertyName("multi_id_cooldown_seconds")] public int MultiIdCooldownSeconds { get; set; } = 20;
    [JsonPropertyName("multi_id_strategy")]         public string MultiIdStrategy { get; set; } = "fair_spread";
    [JsonPropertyName("multi_id_max_consecutive")]  public int MultiIdMaxConsecutive { get; set; } = 1;

    // GUI persistence
    [JsonPropertyName("language")]        public string Language { get; set; } = "en";   // "en" | "fa"
    [JsonPropertyName("preset")]          public string Preset { get; set; } = "balanced"; // stealth|balanced|speed|god|auto

    [JsonPropertyName("first_run_done")]  public bool FirstRunDone { get; set; } = false;

    [JsonPropertyName("hosts")]           public Dictionary<string, string> Hosts { get; set; } = new();
    [JsonPropertyName("direct_bypass_domains")] public List<string> DirectBypassDomains { get; set; } = new();
    [JsonPropertyName("bypass_hosts")] public List<string> BypassHosts { get; set; } = new();
    [JsonPropertyName("no_mitm_hosts")] public List<string> NoMitmHosts { get; set; } = new();
    [JsonPropertyName("no_mitm_cidrs")] public List<string> NoMitmCidrs { get; set; } = new();
    [JsonPropertyName("tcp_send_buffer")] public int TcpSendBuffer { get; set; } = 262144;
    [JsonPropertyName("tcp_recv_buffer")] public int TcpRecvBuffer { get; set; } = 262144;
    [JsonPropertyName("half_open_rx_timeout_s")] public int HalfOpenRxTimeoutS { get; set; } = 20;
    [JsonPropertyName("half_open_probe_timeout_s")] public double HalfOpenProbeTimeoutS { get; set; } = 2.0;
    [JsonPropertyName("dc_failover_attempts")] public int DcFailoverAttempts { get; set; } = 2;
    [JsonPropertyName("telegram_optimizations")] public bool TelegramOptimizations { get; set; } = true;
    [JsonPropertyName("telegram_force_direct")] public bool TelegramForceDirect { get; set; } = true;
    [JsonPropertyName("telegram_allow_relay_fallback")] public bool TelegramAllowRelayFallback { get; set; } = true;
    [JsonPropertyName("telegram_disable_batch")] public bool TelegramDisableBatch { get; set; } = true;
    [JsonPropertyName("telegram_disable_fanout")] public bool TelegramDisableFanout { get; set; } = true;
    [JsonPropertyName("telegram_max_inflight_relays")] public int TelegramMaxInflightRelays { get; set; } = 8;
    [JsonPropertyName("multi_id_min_live_endpoints")] public int MultiIdMinLiveEndpoints { get; set; } = 2;
    [JsonPropertyName("telegram_multi_id_fail_threshold")] public int TelegramMultiIdFailThreshold { get; set; } = 5;
    [JsonPropertyName("telegram_multi_id_cooldown_factor")] public double TelegramMultiIdCooldownFactor { get; set; } = 0.65;
    [JsonPropertyName("force_relay_hosts")] public List<string> ForceRelayHosts { get; set; } = new();
    [JsonPropertyName("domain_routing_profiles")] public Dictionary<string, string> DomainRoutingProfiles { get; set; } = new();

    [JsonPropertyName("update_channel")] public string UpdateChannel { get; set; } = "stable"; // stable|beta
    [JsonPropertyName("auto_update_check")] public bool AutoUpdateCheck { get; set; } = true;
    [JsonPropertyName("update_metadata_url")] public string? UpdateMetadataUrl { get; set; } = "";
    [JsonPropertyName("update_public_key_pem")] public string? UpdatePublicKeyPem { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
