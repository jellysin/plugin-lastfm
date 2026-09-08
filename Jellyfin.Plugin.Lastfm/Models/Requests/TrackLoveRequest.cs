namespace Jellyfin.Plugin.Lastfm.Models.Requests
{
    using System.Collections.Generic;

    public class TrackLoveRequest : BaseAuthedRequest
    {
        public string Track { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;

        public override Dictionary<string, string> ToDictionary()
        {
            return new Dictionary<string, string>(base.ToDictionary())
            {
                { "track" , Track  },
                { "artist", Artist }
            };
        }
    }
}
