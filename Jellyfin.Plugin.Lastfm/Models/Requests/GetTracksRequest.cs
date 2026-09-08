namespace Jellyfin.Plugin.Lastfm.Models.Requests
{
    using System.Collections.Generic;

    public class GetTracksRequest : BaseRequest, IPagedRequest
    {
        public string User { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public int Limit { get; set; }
        public int Page { get; set; }

        public override Dictionary<string, string> ToDictionary()
        {
            return new Dictionary<string, string>(base.ToDictionary())
            {
                { "user",   User   },
                { "artist", Artist },
                { "limit" , Limit.ToString() },
                { "page"  , Page.ToString()  }
            };
        }
    }
}
