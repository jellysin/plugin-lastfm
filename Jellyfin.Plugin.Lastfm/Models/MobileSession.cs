namespace Jellyfin.Plugin.Lastfm.Models
{
    using System.Text.Json.Serialization;

    public class MobileSession
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("subscriber")]
        public int Subscriber { get; set; }
    }
}
