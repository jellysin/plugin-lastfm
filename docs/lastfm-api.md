# Last.fm API research and integration boundaries

Reviewed against the official API documentation for the Jellyfin 12 rewrite.
The public [API index](https://www.last.fm/api) describes 57 methods across artist,
album, track, user, tag, chart, geo, library and authentication families.

## Supported building blocks

| Capability | Official methods and constraints |
| --- | --- |
| Listening | [`track.scrobble`](https://www.last.fm/api/show/track.scrobble), `track.updateNowPlaying`; signed writes, at most 50 scrobbles per batch and individual acknowledgement results; each play carries artist, title and duration, and when known the album, the MusicBrainz track id and the album artist, which Last.fm takes only when it differs from the track artist (read 2026-10-13) |
| Favourites | [`track.love`](https://www.last.fm/api/show/track.love), `track.unlove`, `user.getLovedTracks`; paginated snapshots, no atomic two-way synchronization or deletion feed |
| History | [`user.getRecentTracks`](https://www.last.fm/api/show/user.getRecentTracks); paginated UTC windows, up to 200 entries per page, with a now-playing entry that is not a completed listen |
| Listening charts | [`user.getTopTracks`](https://www.last.fm/api/show/user.getTopTracks), `user.getTopArtists`, `user.getTopAlbums`, weekly-chart families; fixed supported periods, not arbitrary private listening reports |
| Similarity | [`track.getSimilar`](https://www.last.fm/api/show/track.getSimilar), `artist.getSimilar`; similarity seeds can resolve to local accessible items or external catalog links |
| Metadata | [`artist.getInfo`](https://www.last.fm/api/show/artist.getInfo), [`album.getInfo`](https://www.last.fm/api/show/album.getInfo); artist biographies and album wikis mapped to Jellyfin `Overview`, and album release date (`releasedate`) mapped to `PremiereDate` and `ProductionYear` when present; HTML stripped to plain text; trailing CC BY-SA attribution preserved; MusicBrainz identifiers (`MusicBrainzArtist`, `MusicBrainzAlbum`) used for remote query when present (never written back into library items) |
| Images | [`artist.getInfo`](https://www.last.fm/api/show/artist.getInfo), [`album.getInfo`](https://www.last.fm/api/show/album.getInfo); the largest size Last.fm lists for the artist picture or the album cover (`image` entries in the order small, medium, large, extralarge, mega), offered to Jellyfin as the primary image; the grey placeholder images Last.fm lists for items without a picture are skipped |

The API also exposes track descriptions, community tags, search/corrections, personal tags, user profiles/friends
and library artist listings. This release uses none of them: it writes no Last.fm track descriptions or tags
into the Jellyfin library and adds no personal tag editing, friend synchronization or global search takeover.
No private Last.fm endpoints are scraped.

### Metadata providers and stored data

The plugin implements Jellyfin `IRemoteMetadataProvider` for artists (`LastfmArtistProvider`) and
albums (`LastfmAlbumProvider`).

- **Mapped fields**:
  - `artist.getInfo`: `bio.content` (fallback `bio.summary`) mapped to `MusicArtist.Overview`. Last.fm response MBIDs never write or overwrite `MusicBrainzArtist` in Jellyfin to avoid wrong-match risks.
  - `album.getInfo`: `wiki.content` (fallback `wiki.summary`) mapped to `MusicAlbum.Overview`. Album release date (`releasedate`) mapped to `MusicAlbum.PremiereDate` and `ProductionYear`. `wiki.published` is the wiki revision edit timestamp, not the release date, and is never used. Last.fm response MBIDs never write or overwrite `MusicBrainzAlbum` in Jellyfin.
- **Library activation & queries**: whether Jellyfin runs a newly installed metadata provider depends on the library's saved settings. In the Jellyfin 12.0 source, a library whose metadata settings list fetchers for the item type runs only the listed ones, so a newly installed provider stays off until an administrator enables "Last.fm" there ([`BaseItemManager.cs` lines 40-43](https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/MediaBrowser.Controller/BaseItemManager/BaseItemManager.cs#L40-L43)). A library with no saved settings for the type runs every remote provider the server's metadata options do not disable ([`BaseItemManager.cs` lines 45-46](https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/MediaBrowser.Controller/BaseItemManager/BaseItemManager.cs#L45-L46)), and an artist derived from tags without a folder of its own runs every remote provider ([`ProviderManager.cs` lines 607-610](https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/MediaBrowser.Providers/Manager/ProviderManager.cs#L607-L610)). Every refresh that runs the provider sends the artist or album name, or its MusicBrainz identifier (`MusicBrainzArtist` or `MusicBrainzAlbum`), to Last.fm over HTTPS.
- **Attribution & Licensing**: Last.fm artist biographies and album wikis are licensed under Creative Commons Attribution-ShareAlike (CC BY-SA). HTML markup is stripped to plain text, the body is capped to `MaxOverviewLength`, and the trailing attribution sentence (`Read more on Last.fm: <https://www.last.fm/...>`) is preserved intact after the body in `Overview`.
- **Storage & Retention**: Mapped fields land in Jellyfin's library database. The plugin keeps no copy beyond its existing response cache under `jellysin-lastfm/state` inside Jellyfin's data directory.

### Image providers and stored data

The plugin implements Jellyfin `IRemoteImageProvider` (`LastfmImageProvider`) for artists and albums.

- **Which images**: one primary image per artist (`artist.getInfo`) or album (`album.getInfo`), the largest size Last.fm lists, with its address exactly as Last.fm returned it. The query is the same as the metadata providers make: the MusicBrainz identifier when present, otherwise the artist, or the album artist and album name, with `autocorrect=0`.
- **Placeholders**: Last.fm lists a grey star for an artist and a grey disc for an album that have no picture. The plugin recognises them by the 32-character file name in the image address (`2a96cbd8b46e442fc41c2b86b821562f` and `c6f59c1e5e7240a4c0d427abd71f3dbb`, read 2026-10-17, any letter case and extension) and offers no image for them; a placeholder is never fetched and never stored. Several sizes of an item are judged one by one, so a placeholder at the largest size does not hide a real image at a smaller one.
- **Fetching**: Jellyfin downloads the image it chose through the provider. The plugin fetches only `https` addresses on `lastfm.freetls.fastly.net` (no user information, no other port) and refuses anything else with a 404 before any request. The fetch uses the server's default HTTP client with a 30-second deadline, requires an `image/*` content type and stops at 10 MiB, checked by `Content-Length` and by a bounded read. The server's client follows redirects, so an answer that came from any other address is discarded.
- **Library activation**: in the Jellyfin 12.0 source, a library whose saved settings list image fetchers for the item type runs only the listed ones, so a newly installed provider stays off until an administrator enables "Last.fm" under the image fetchers of the album and artist types there ([`BaseItemManager.cs` lines 64-67](https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/MediaBrowser.Controller/BaseItemManager/BaseItemManager.cs#L64-L67)). An item whose library has no saved settings for its type, runs every remote image provider the server's metadata options do not disable ([`BaseItemManager.cs` lines 69-70](https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/MediaBrowser.Controller/BaseItemManager/BaseItemManager.cs#L69-L70), [`ProviderManager.cs` lines 401-404](https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/MediaBrowser.Providers/Manager/ProviderManager.cs#L401-L404)). Every image search that runs the provider sends the artist or album name, or its MusicBrainz identifier, to Last.fm over HTTPS, and Jellyfin then requests the chosen image from Last.fm's image host.
- **Storage & retention**: Jellyfin stores the chosen image in its own library, like any other image. The plugin keeps no image copy and no image address beyond its existing response cache under `jellysin-lastfm/state` inside Jellyfin's data directory.

## Authentication

The [web flow](https://www.last.fm/api/webauth) (read 2026-10-11) sends the user's
browser to Last.fm with the application key and a return address on the Jellyfin
server. After the user allows access, Last.fm sends the browser back to that
address with a token, documented as single use and valid for 60 minutes, and the
server exchanges it for a session through `auth.getSession`.

The return address is opened by the user's own browser, so the server does not
have to be reachable from the internet. Whether Last.fm accepts a private or LAN
address as the return address is not stated in its documentation; it is confirmed
by a live sign-in, recorded in [operations](operations.md#account-connection).

The pending attempt is bound to the Jellyfin user who started it and, through a
cookie that page script cannot read, to the browser that started it. It can be
used once and expires after 10 minutes. The return address accepts the token
without contacting Last.fm and sends the browser on to an address without it; the
exchange happens afterwards, on a request of the signed-in user.

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
must not count as listening. Jellyfin also raises position updates of its own every
second between a client's reports. These count only for a session whose client sent
no progress report since the start other than pausing or resuming, are capped at the distance between the start
and the position the client's stop reports, and create a listen only when that stop
arrives; a session with no stop creates none. Plays a client made offline and later
reports only as a last-played date carry no start time and are not scrobbled.
Retried scrobbles retain the original UTC start time.
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
contact or approval under the terms. The project application is registered and its
documented account flow has been tested. Questions about aggregate storage across
independent installations and attribution in native clients remain recorded in a
clarification draft that the maintainer decided on 2026-10-12 not to send; the terms
are the project's own reading, and [release readiness](release-readiness.md) states
why. API registration is not a blanket permission for other uses. EUPL-1.2 licenses this code, not Last.fm's data or third-party artwork.
