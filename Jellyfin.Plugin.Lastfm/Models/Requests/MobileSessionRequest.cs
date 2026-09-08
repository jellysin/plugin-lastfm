namespace Jellyfin.Plugin.Lastfm.Models.Requests
{
    using System.Collections.Generic;

    public class MobileSessionRequest : BaseRequest
    {
        public string Password { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;

        public override Dictionary<string, string> ToDictionary()
        {
            return new Dictionary<string, string>(base.ToDictionary())
            {
                { "password", Password },
                { "username", Username },
            };
        }
    }
}
