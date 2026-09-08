# Last.fm permission request — unsent draft

Status: not sent; no Last.fm approval received. The project application is registered
under Lothario87, and live authorization and one approved scrobble passed. This
optional clarification draft remains unsent following maintainer review. Do not put
API keys, shared secrets or user session keys in this message or its attachments.

To: partners@last.fm
Subject: JellySin Last.fm — self-hosted Jellyfin plugin application and display approval

Hello Last.fm partnerships team,

We maintain [JellySin Last.fm](https://github.com/jellysin/plugin-lastfm),
a new EUPL-1.2 plugin for Jellyfin 12. We intend to distribute it free of charge
for noncommercial, self-hosted music libraries, without advertising, a hosted
aggregation service or resale of Last.fm data. Please confirm the following
application and display arrangements before our first production distribution.

The plugin supports scrobbling/now-playing, reviewed favourite synchronization,
listening history/charts, previewed play-count imports, metadata, discovery and
local Jellyfin playlists. Each user authorizes their own Last.fm account through
your documented token flow; session keys stay protected on their Jellyfin server.
Personal pages require that user's Jellyfin sign-in. Shared library metadata is
available to the server's authorized library users. We download no artwork or audio
from Last.fm and include links to relevant Last.fm profiles and catalog entries.

Each server has an 80,000,000-byte aggregate accounting budget for plugin state,
including conservative reservations for metadata copies passed to Jellyfin's
database/NFO storage. Operator-created backups and exports are outside that
measurement. We use HTTP cache directives for response caching; some imported
counts, playlist membership and native metadata remain in Jellyfin afterward.

Could you confirm:

1. May independently operated servers share one project application, using the
   desktop-style token flow for private/LAN installations? The distributed DLL
   contains the application key and shared secret, which server owners can extract;
   individual user session keys are separate. Are different registration or key
   arrangements required?
2. Does the storage allowance apply per installation, per user or across all
   installations using that application? Is the described accounting acceptable,
   and what retention, refresh or deletion requirements apply to native metadata,
   derived counts/playlists and administrator backups?
3. Can you approve authenticated personal pages and shared native-library displays,
   including remote access to a self-hosted server? Which attribution and link
   placements are required in Jellyfin clients whose layouts we cannot control?
4. The resources URL in your terms was unavailable during our review. Please supply
   the currently approved “powered by AudioScrobbler” button, brand guidance and
   required layout, and confirm the permitted project name, README and release
   description. We can provide screenshots or a demo for written display approval.

Please identify any changes or application-wide usage limits needed for this scope.
We will incorporate your response before production distribution.

Thank you,
JellySin maintainers

## Review references

Reviewed 2026-09-08. The official [API terms](https://www.last.fm/api/tos),
especially §§2.7, 3.1, 4.3.4, 7.1 and 8.2, leave these storage, display and
distribution questions to confirm with Last.fm. The [desktop authentication
guide](https://www.last.fm/api/desktopauth) documents the token/session flow;
it does not by itself approve this deployment model. The linked
[brand resources](https://www.last.fm/resources/) could not be retrieved.
This draft records open questions, not a permission grant.
