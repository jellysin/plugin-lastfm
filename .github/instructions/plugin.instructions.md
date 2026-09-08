---
applyTo: "Jellyfin.Plugin.Lastfm/**/*.cs"
---

# Plugin integration

Follow the root AGENTS.md. The plugin is loaded into Jellyfin's process, with
host services injected through its service registrator. Playback and favourite
events belong to a hosted service; metadata providers and scheduled import are
discovered by Jellyfin. Do not add a second server or a shared Emby abstraction.

Use the host's `IUserManager.GetUsers`, user-scoped `InternalItemsQuery`, and
`IUserDataManager` APIs. Set explicit artist filters when importing loved tracks;
passing an audio type filter to `MusicArtist.GetTaggedItems` does not add an
artist restriction. Cancellation or exceptions must release import suppression.

The configuration page and login endpoint are administrator operations. Preserve
the plugin GUID and XML fields so upgrades retain connected Last.fm accounts.
Configuration storage is not encrypted; see SECURITY.md.

Last.fm request signatures use its specified MD5 algorithm and ordinal parameter
ordering. Always use HTTPS. Interpret Last.fm errors even when HTTP returns 200.
Scrobbling requires a track longer than 30 seconds and at least half its duration
or four minutes played. Do not infer missing metadata from filenames. Failed
requests must not be recorded as successful duplicate submissions.

Before changing the host package or ABI, check official Jellyfin release/source
changes, compile against the exact package, and test the actual packaged plugin
in the corresponding server. Passing unit tests alone does not establish loading
or dashboard compatibility.
