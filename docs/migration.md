# Moving to JellySin Last.fm

JellySin Last.fm is a new plugin for Jellyfin 12, with a new GUID:
`2034650d-a290-4a16-b195-89fb44cfb932`. It uses the JellySin Plugin Repository.
It does not replace an older plugin automatically and cannot run on Jellyfin 10.11.

The plugin is in development; no published release is available. Once an
installable release is available:

1. Back up the Jellyfin configuration and plugin data directories.
2. Disable the previous Last.fm plugin's scrobbling and favourite synchronization,
   then uninstall that plugin and restart Jellyfin. JellySin suppresses listening
   delivery while it detects the legacy plugin, to prevent duplicate submissions.
3. Add `https://raw.githubusercontent.com/jellysin/repo/main/manifest.json` in
   Jellyfin's plugin repository settings and install JellySin Last.fm. Restart Jellyfin.
4. Open `/JellySin/Lastfm/` under the server's existing base path. Sign in to Jellyfin
   and connect each user's Last.fm account through Last.fm's authorization page.
5. Enable scrobbling and optional music features for that account. Review favourite
   removals and history import previews before applying changes.

Old XML fields, inherited application credentials and user session keys are not
silently copied. The project-owned Last.fm application needs a fresh authorization.
Existing Last.fm history remains on Last.fm; installing this plugin does not erase it.

The previous repositories have been deleted. Existing Jellyfin installations
still require the uninstall and reconnect steps above. A new plugin release uses
SemVer independently of Jellyfin's ABI.

To stop using JellySin, disconnect the account in the dashboard and disable the
plugin. Disconnect cancels account work and removes the plugin's local account data.
It does not undo existing scrobbles, native favourites, imported aggregate counts,
or generated Jellyfin playlists. Revoke the application's permission in Last.fm
settings when you also want to remove its upstream authorization.
