using System.Collections.Generic;

namespace Jellyfin.Plugin.Lastfm.Providers
{
    #region Result Objects

    public class LastfmStats
    {
        public string listeners { get; set; } = string.Empty;
        public string playcount { get; set; } = string.Empty;
    }

    public class LastfmTag
    {
        public string name { get; set; } = string.Empty;
        public string url { get; set; } = string.Empty;
    }


    public class LastfmTags
    {
        public List<LastfmTag> tag { get; set; } = [];
    }

    public class LastfmFormationInfo
    {
        public string yearfrom { get; set; } = string.Empty;
        public string yearto { get; set; } = string.Empty;
    }

    public class LastFmBio
    {
        public string published { get; set; } = string.Empty;
        public string summary { get; set; } = string.Empty;
        public string content { get; set; } = string.Empty;
        public string placeformed { get; set; } = string.Empty;
        public string yearformed { get; set; } = string.Empty;
        public List<LastfmFormationInfo> formationlist { get; set; } = [];
    }

    public class LastFmImage
    {
        [System.Text.Json.Serialization.JsonPropertyName("#text")]
        public string url { get; set; } = string.Empty;
        public string size { get; set; } = string.Empty;
    }

    public class LastfmArtist : IHasLastFmImages
    {
        public string name { get; set; } = string.Empty;
        public string mbid { get; set; } = string.Empty;
        public string url { get; set; } = string.Empty;
        public string streamable { get; set; } = string.Empty;
        public string ontour { get; set; } = string.Empty;
        public LastfmStats? stats { get; set; }
        public LastfmSimilarArtists? similar { get; set; }
        public LastfmTags? tags { get; set; }
        public LastFmBio? bio { get; set; }
        public List<LastFmImage> image { get; set; } = [];
    }

    public class LastfmSimilarArtists
    {
        public List<LastfmArtist> artist { get; set; } = [];
    }


    public class LastfmAlbum : IHasLastFmImages
    {
        public string name { get; set; } = string.Empty;
        public string artist { get; set; } = string.Empty;
        public string id { get; set; } = string.Empty;
        public string mbid { get; set; } = string.Empty;
        public string releasedate { get; set; } = string.Empty;
        public int listeners { get; set; }
        public int playcount { get; set; }
        public LastfmTags? toptags { get; set; }
        public LastFmBio? wiki { get; set; }
        public List<LastFmImage> image { get; set; } = [];
    }

    public interface IHasLastFmImages
    {
        List<LastFmImage> image { get; set; }
    }

    public class LastfmGetAlbumResult
    {
        public LastfmAlbum? album { get; set; }
    }

    public class LastfmGetArtistResult
    {
        public LastfmArtist? artist { get; set; }
    }

    public class Artistmatches
    {
        public List<LastfmArtist> artist { get; set; } = [];
    }

    public class LastfmArtistSearchResult
    {
        public Artistmatches? artistmatches { get; set; }
    }

    public class LastfmArtistSearchResults
    {
        public LastfmArtistSearchResult? results { get; set; }
    }

    #endregion
}
