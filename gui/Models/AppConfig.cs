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
    [JsonPropertyName("mode")]            public string Mode { get; set; } = "apps_script";
    [JsonPropertyName("google_ip")]       public string? GoogleIp { get; set; } = "216.239.38.120";
    [JsonPropertyName("front_domain")]    public string? FrontDomain { get; set; } = "www.google.com";
    [JsonPropertyName("custom_sni")]      public string? CustomSni { get; set; } = "";

    // Single-ID stays for backward compat. Multi-ID list is preferred.
    [JsonPropertyName("script_id")]       public string? ScriptId { get; set; }
    [JsonPropertyName("script_ids")]      public List<string> ScriptIds { get; set; } = new();
    [JsonPropertyName("relay_items")]     public List<RelayConfigItem> RelayItems { get; set; } = new();

    [JsonPropertyName("worker_host")]     public string? WorkerHost { get; set; }
    [JsonPropertyName("custom_domain")]   public string? CustomDomain { get; set; }
    [JsonPropertyName("auth_key")]        public string AuthKey { get; set; } = "";
    [JsonPropertyName("listen_host")]     public string ListenHost { get; set; } = "127.0.0.1";
    [JsonPropertyName("listen_port")]     public int ListenPort { get; set; } = 8085;
    [JsonPropertyName("log_level")]       public string LogLevel { get; set; } = "INFO";
    [JsonPropertyName("verify_ssl")]      public bool VerifySsl { get; set; } = true;

    [JsonPropertyName("enable_http2")]    public bool EnableHttp2 { get; set; } = false;
    [JsonPropertyName("enable_chunked")]  public bool EnableChunked { get; set; } = true;
    [JsonPropertyName("chunk_size")]      public int ChunkSize { get; set; } = 131072;
    [JsonPropertyName("max_parallel")]    public int MaxParallel { get; set; } = 4;
    [JsonPropertyName("fragment_size")]   public int FragmentSize { get; set; } = 16384;
    [JsonPropertyName("cache_enabled")]   public bool CacheEnabled { get; set; } = true;
    [JsonPropertyName("cache_max_mb")]    public int CacheMaxMb { get; set; } = 96;
    [JsonPropertyName("cache_default_ttl_s")] public int CacheDefaultTtlS { get; set; } = 600;
    [JsonPropertyName("cache_stale_if_error_s")] public int CacheStaleIfErrorS { get; set; } = 120;

    // Multi-ID tuning
    [JsonPropertyName("multi_id_fail_threshold")]   public int MultiIdFailThreshold { get; set; } = 3;
    [JsonPropertyName("multi_id_cooldown_seconds")] public int MultiIdCooldownSeconds { get; set; } = 30;
    [JsonPropertyName("multi_id_strategy")]         public string MultiIdStrategy { get; set; } = "balanced";
    [JsonPropertyName("multi_id_max_consecutive")]  public int MultiIdMaxConsecutive { get; set; } = 2;

    // GUI persistence
    [JsonPropertyName("language")]        public string Language { get; set; } = "en";   // "en" | "fa"
    [JsonPropertyName("preset")]          public string Preset { get; set; } = "balanced"; // stealth|balanced|speed|auto

    [JsonPropertyName("first_run_done")]  public bool FirstRunDone { get; set; } = false;

    [JsonPropertyName("hosts")]           public Dictionary<string, string> Hosts { get; set; } = new();
    [JsonPropertyName("direct_bypass_domains")] public List<string> DirectBypassDomains { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
