# Last.fm API research and integration boundaries

Reviewed against the official API documentation for the Jellyfin 12 rewrite.
The public [API index](https://www.last.fm/api) describes 57 methods across artist,
album, track, user, tag, chart, geo, library and authentication families.

## Supported building blocks

| Capability | Official methods and constraints |
| --- | --- |
| Listening | [`track.scrobble`](https://www.last.fm/api/show/track.scrobble), `track.updateNowPlaying`; signed writes, at most 50 scrobbles per batch and individual acknowledgement results |
| Favourites | [`track.love`](https://www.last.fm/api/show/track.love), `track.unlove`, `user.getLovedTracks`; paginated snapshots, no atomic two-way synchronization or deletion feed |
| Metadata | [`artist.getInfo`](https://www.last.fm/api/show/artist.getInfo), [`album.getInfo`](https://www.last.fm/api/show/album.getInfo), [`track.getInfo`](https://www.last.fm/api/show/track.getInfo); optional identifiers, bios/descriptions, track lists, links and popularity |
| Community tags | `artist/album/track.getTopTags`, `tag.getInfo`; tags are community labels rather than a controlled genre classification |
| History | [`user.getRecentTracks`](https://www.last.fm/api/show/user.getRecentTracks); paginated UTC windows, up to 200 entries per page, with a now-playing entry that is not a completed listen |
| Listening charts | [`user.getTopTracks`](https://www.last.fm/api/show/user.getTopTracks), `user.getTopArtists`, `user.getTopAlbums`, weekly-chart families; fixed supported periods, not arbitrary private listening reports |
| Similarity | [`track.getSimilar`](https://www.last.fm/api/show/track.getSimilar), `artist.getSimilar`; similarity seeds can resolve to local accessible items or external catalog links |
| Broader discovery | Artist top tracks/albums, tag charts, global charts and [`geo.getTopTracks`](https://www.last.fm/api/show/geo.getTopTracks); discovery is assembled from documented public methods |

The API also exposes search/corrections, personal tags, user profiles/friends and
library artist listings. Those capabilities are documented future extension points;
this release does not add personal tag editing, friend synchronization or a global
search takeover. No private Last.fm endpoints are scraped.

## Authentication

The [desktop flow](https://www.last.fm/api/desktopauth) obtains an authorization
token, sends the user to Last.fm in a browser and exchanges an approved token for
a session. It supports private/LAN Jellyfin servers without requiring a public
callback. The local pending attempt is bound to the current Jellyfin user and has
a shorter expiry than Last.fm's token lifetime.

[`auth.getMobileSession`](https://www.last.fm/api/show/auth.getMobileSession) is
documented for standalone mobile devices and is not used. This plugin never asks
for a Last.fm password. The canonical username comes from the successful session
exchange. A new project application requires users to reconnect.

Request signing follows the [authentication specification](https://www.last.fm/api/authspec):
ordinal parameter order, concatenated parameter names/values followed by the app
secret, UTF-8 MD5; format/callback/signature parameters are excluded where specified.
Indexed batch names sort as strings, not numerically. All network requests use HTTPS.
The secret shipped in a desktop-style plugin is extractable; user session keys are
separate credentials protected in server-side state.

## Delivery and errors

The [scrobbling guide](https://www.last.fm/api/scrobbling) requires tracks longer
than 30 seconds and listening for half the track or four minutes. Pauses and seeks
must not count as listening. Retried scrobbles retain the original UTC start time.
Now-playing describes the present and is not retried after failure.

JSON API errors must be examined even with HTTP 200. Codes 11 and 16 represent
temporary service failures; invalid session code 9 requires reconnecting. Invalid
or suspended application credentials and signature errors need administrator or
implementation attention. HTTP 429 and API code 29 require throttling, respecting
Retry-After when supplied. The method reference and older guide differ on which
rate-limit responses may safely be replayed; blocked delivery is visible rather
than assuming undocumented guarantees.

No supported fixed requests-per-second allowance, 2,800-scrobble daily quota or
14-day retry window is assumed. Retry queues, pagination, backoff and caches are
bounded by this implementation. Last.fm supplies no request idempotency key, so
local duplicate suppression cannot guarantee exactly-once delivery after an
accepted request loses its response.

## Data and product limits

Last.fm's [playlist](https://www.last.fm/api/playlists) and
[radio](https://www.last.fm/api/radio) APIs are deprecated. Playlists are generated
in Jellyfin from accessible library items; no Last.fm streaming or playlist sync
is promised. The public API has no supported personalized recommendation endpoint,
scrobble editing/deletion, cover upload or Last.fm Pro report API.

MusicBrainz identifiers may be missing and names may be ambiguous. Matching must
not guess between multiple local candidates. Metadata duration units also vary
between methods. Full remote listening history is distinct from Jellyfin's
aggregate count/date fields; an import must not fabricate chronological host events.

The [API terms](https://www.last.fm/api/tos) require attribution and appropriate
links, impose caching/storage conditions including a stated 100 MB cap without
permission, and do not grant artwork or audio rights. The plugin therefore links
back, bounds stored API data, and omits artwork downloads. Its local quota does
not determine how Last.fm interprets storage across deployments or authorize public
data redistribution. Commercial/research usage and public data surfaces may require
contact or approval under the terms. Project application registration and permitted
distribution must be resolved before publication. EUPL-1.2 licenses this code,
not Last.fm's data or third-party artwork.
