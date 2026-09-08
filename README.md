# Jellyfin Last.fm plugin

[![CI](https://github.com/lusoris/jellyfin-plugin-lastfm/actions/workflows/ci.yml/badge.svg)](https://github.com/lusoris/jellyfin-plugin-lastfm/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/lusoris/jellyfin-plugin-lastfm)](https://github.com/lusoris/jellyfin-plugin-lastfm/releases)

Scrobble music to Last.fm, send now-playing updates, sync favourites, and use
Last.fm artist, album, and image metadata in Jellyfin.

This is the maintained fork of [jesseward/jellyfin-plugin-lastfm](https://github.com/jesseward/jellyfin-plugin-lastfm).

## Supported servers

| Jellyfin server | Plugin release | Maintenance branch | Runtime |
| --- | --- | --- | --- |
| 12.0 | 12.x | `main` | .NET 10 |
| 10.11.11 | 10.11.x | `release/10.11` | .NET 9 |

Plugin versions advance independently of server patches. The catalog's `targetAbi`
selects the appropriate build; a .NET 10 plugin cannot run inside Jellyfin 10.11.
Historical releases remain available for older servers.

When upgrading the server from 10.11 to 12, follow the
[Jellyfin 12 migration instructions](https://github.com/jellyfin/jellyfin/releases/tag/v12.0):
back up server data, remove the old plugin before upgrading, install the compatible
12.x plugin afterward, and run the full library rescan required by Jellyfin.

## Install

Add this URL under **Dashboard → Plugins → Repositories**, then install **Last.fm**
from the catalog and restart Jellyfin:

```text
https://raw.githubusercontent.com/lusoris/jellyfin-plugin-lastfm/main/manifest.json
```

For a manual installation, download the ZIP for your server from
[Releases](https://github.com/lusoris/jellyfin-plugin-lastfm/releases), extract
`Jellyfin.Plugin.Lastfm.dll` into its own directory under Jellyfin's plugins
folder, and restart the server. The plugin directory must be writable by Jellyfin
so it can manage plugin metadata. Remove a previous manually installed copy before
installing another build of this plugin.

## Configure

Open the Last.fm plugin configuration as a Jellyfin administrator. Select a
Jellyfin user, enter that user's Last.fm username and password, choose the desired
options, and save. Later option changes can leave the password blank to retain
the connected account. Passwords are exchanged for session keys and are not saved.

Scrobbling requires a track longer than 30 seconds and at least half its duration
or four minutes of observed playback. Seeking, initial resume position, and paused
time do not count as listening. Both scrobbling modes use these rules. The
alternative mode also waits for Jellyfin's playback-finished user-data event.

Enabling favourite sync sends subsequent Jellyfin favourite changes to Last.fm.
Run **Import Last.fm Loved Tracks** under Scheduled Tasks to import existing
Last.fm favourites. Imports respect user library access and match by MusicBrainz
artist ID and track title; tracks without an artist MusicBrainz ID are skipped.

Last.fm metadata providers can be selected in the music library's metadata
settings. Scrobbling does not require enabling those providers.

## Development and releases

See [CONTRIBUTING.md](CONTRIBUTING.md) for the pinned SDK, validation commands,
backports, and release-please workflow. Package generation and catalog updates live
in this repository; no checkout of `jellyfin-utilities` is needed.

## Security and licensing

See [SECURITY.md](SECURITY.md) for private reporting and configuration storage.

This plugin has no explicit open-source license. It was originally adapted from
Emby code and cannot be distributed with Jellyfin due to licensing incompatibilities.
This maintenance work does not change that licensing status.

Original plugin by [jesseward](https://github.com/jesseward), adapted from the
Emby Last.fm plugin.
