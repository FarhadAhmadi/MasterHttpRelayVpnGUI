using System.Text.Json.Serialization;

namespace MasterRelayVPN.Models;

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
}
