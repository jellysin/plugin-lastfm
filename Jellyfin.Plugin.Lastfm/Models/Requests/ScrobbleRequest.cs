namespace Jellyfin.Plugin.Lastfm.Models.Requests
{
    using System.Collections.Generic;
    using System.Runtime.Serialization;

    [DataContract]
    public class ScrobbleRequest : BaseAuthedRequest
    {
        // API docs for scrobbling located at https://www.last.fm/api/show/track.scrobble
        // Track, Artist, and Timestamp are required
        // Album, ArtistAlbum, and MusicBrainzid are optional.
        public string Track { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public int Timestamp { get; set; }
        public string Album { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public string MbId { get; set; } = string.Empty;

        public override Dictionary<string, string> ToDictionary()
        {
            var scrobbleRequest = new Dictionary<string, string>(base.ToDictionary())
            {
                { "track",     Track },
                { "artist",    Artist },
                { "timestamp", Timestamp.ToString() },
            };
            if (!string.IsNullOrWhiteSpace(Album))
            {
                scrobbleRequest.Add("album", Album);
            }
            if (!string.IsNullOrWhiteSpace(MbId))
            {
                scrobbleRequest.Add("mbid", MbId);
            }
            if (!string.IsNullOrWhiteSpace(AlbumArtist))
            {
                scrobbleRequest.Add("albumArtist", AlbumArtist);
            }

            return scrobbleRequest;
        }
    }
}
