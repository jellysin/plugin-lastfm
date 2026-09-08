namespace Jellyfin.Plugin.Lastfm.Models.Requests
{
    using System.Collections.Generic;
    using System.Runtime.Serialization;

    [DataContract]
    public class NowPlayingRequest : BaseAuthedRequest
    {
        public string Track { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public int Duration { get; set; }
        public string MbId { get; set; } = string.Empty;

        public override Dictionary<string, string> ToDictionary()
        {
            var nowPlaying = new Dictionary<string, string>(base.ToDictionary())
            {
                { "track",    Track  },
                { "artist",   Artist },
                { "duration", Duration.ToString() },
            };

            if (!string.IsNullOrWhiteSpace(Album))
            {
                nowPlaying.Add("album", Album);
            }
            if (!string.IsNullOrWhiteSpace(MbId))
            {
                nowPlaying.Add("mbid", MbId);
            }
            if (!string.IsNullOrWhiteSpace(AlbumArtist))
            {
                nowPlaying.Add("albumArtist", AlbumArtist);
            }

            return nowPlaying;
        }
    }
}
