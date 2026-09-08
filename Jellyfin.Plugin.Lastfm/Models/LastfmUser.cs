namespace Jellyfin.Plugin.Lastfm.Models
{
    using System;

    public class LastfmUser
    {
        public string Username { get; set; } = string.Empty;

        //We wont store the password, but instead store the session key since its a lifetime key
        public string SessionKey { get; set; } = string.Empty;

        public Guid MediaBrowserUserId { get; set; }

        public LastFmUserOptions Options { get; set; } = new();
    }

    public class LastFmUserOptions
    {
        public bool Scrobble { get; set; }
        public bool SyncFavourites { get; set; }
        public bool AlternativeMode { get; set; }
    }
}
