namespace Jellyfin.Plugin.Lastfm.Models
{
    using System.Text.Json.Serialization;

    public class BaseLastfmTrack
    {
        [JsonPropertyName("artist")]
        public LastfmArtist? Artist { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("mbid")]
        public string MusicBrainzId { get; set; } = string.Empty;
    }

    public class LastfmArtist
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("mbid")]
        public string MusicBrainzId { get; set; } = string.Empty;
    }

    public class LastfmLovedTrack : BaseLastfmTrack
    {
    }


    public class LastfmTrack : BaseLastfmTrack
    {
        [JsonPropertyName("playcount")]
        public int PlayCount { get; set; }
    }
}
