using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace MasterRelayVPN.Models;

public class RelayEndpointDetail
{
    [JsonPropertyName("id")]              public string Id { get; set; } = "";
    [JsonPropertyName("ok")]              public int Ok { get; set; }
    [JsonPropertyName("err")]             public int Err { get; set; }
    [JsonPropertyName("recent_failures")] public int RecentFailures { get; set; }
    [JsonPropertyName("latency_ms")]      public double LatencyMs { get; set; }
    [JsonPropertyName("parked")]          public bool Parked { get; set; }
    [JsonPropertyName("parked_for_s")]    public int ParkedForS { get; set; }
    [JsonPropertyName("uses")]            public int Uses { get; set; }
    [JsonPropertyName("success_rate")]    public double SuccessRate { get; set; }
}

public class StatsSnapshot
{
    [JsonPropertyName("uptime")]           public long Uptime { get; set; }
    [JsonPropertyName("bytes_up")]         public long BytesUp { get; set; }
    [JsonPropertyName("bytes_down")]       public long BytesDown { get; set; }
    [JsonPropertyName("speed_up")]         public double SpeedUp { get; set; }
    [JsonPropertyName("speed_down")]       public double SpeedDown { get; set; }
    [JsonPropertyName("requests")]         public long Requests { get; set; }
    [JsonPropertyName("connections")]      public int Connections { get; set; }
    [JsonPropertyName("peak_connections")] public int PeakConnections { get; set; }

    // New: emitted by core/stats.py
    [JsonPropertyName("health")]           public string Health { get; set; } = "good";
    [JsonPropertyName("endpoints")]        public int Endpoints { get; set; } = 0;
    [JsonPropertyName("endpoints_healthy")] public int EndpointsHealthy { get; set; } = 0;
    [JsonPropertyName("latency_ms")]        public double LatencyMs { get; set; } = 0;
    [JsonPropertyName("success_rate")]      public double SuccessRate { get; set; } = 1;
    [JsonPropertyName("requests_per_sec")]  public double RequestsPerSec { get; set; } = 0;
    [JsonPropertyName("active_endpoint")]   public string ActiveEndpoint { get; set; } = "";
    [JsonPropertyName("window_requests")]   public int WindowRequests { get; set; } = 0;
    [JsonPropertyName("window_errors")]     public int WindowErrors { get; set; } = 0;
    [JsonPropertyName("window_success_rate")] public double WindowSuccessRate { get; set; } = 1;
    [JsonPropertyName("cache_hits")]        public int CacheHits { get; set; } = 0;
    [JsonPropertyName("cache_misses")]      public int CacheMisses { get; set; } = 0;
    [JsonPropertyName("cache_hit_rate")]    public double CacheHitRate { get; set; } = 0;
    [JsonPropertyName("cache_entries")]     public int CacheEntries { get; set; } = 0;
    [JsonPropertyName("cache_bytes")]       public long CacheBytes { get; set; } = 0;
    [JsonPropertyName("endpoints_detail")]  public List<RelayEndpointDetail> EndpointsDetail { get; set; } = new();
}
