# JellySin Last.fm

Last.fm music integration for **Jellyfin 12**: scrobbling, favourites, listening
insights, discovery and playlists. This is a new implementation with its own
plugin identity and release history. The first release is being prepared; no
installable JellySin release is advertised until publication and catalog verification.

## Music features

- Now-playing updates and scrobbles based on observed listening, with a bounded
  durable delivery queue and visible retry/reconnect state.
- Last.fm loved tracks and Jellyfin favourites, with additive initial reconciliation
  and an explicit review before propagating removals.
- Recent history, top tracks/artists/albums and listening statistics; an optional
  previewed import raises local aggregate play counts and last-played dates.
- Discovery of playable music already in your library and off-library tracks,
  artists and albums linked to Last.fm.
- Native artist/track similarity and optional music metadata/community tags.
- Private Jellyfin playlists from loved tracks, charts, similarity and discovery,
  with saved recipes and optional daily refresh.

The account dashboard is served by the plugin at `/JellySin/Lastfm/` under your
Jellyfin server's base path. Sign in with Jellyfin credentials or Quick Connect,
then authorize Last.fm in its own browser page. Each Jellyfin user manages their
own account. Last.fm passwords and session keys are never entered into the dashboard.

## Installation after the first release

1. Run Jellyfin 12 and add this plugin repository in the administrator dashboard:
   `https://raw.githubusercontent.com/jellysin/catalog/main/manifest.json`.
2. Install **JellySin Last.fm** when it appears in the catalog, then restart Jellyfin.
3. Open the plugin's administration page and follow **Open JellySin Last.fm**.
   Users can also visit `/JellySin/Lastfm/` directly on the server.
4. Sign in, connect Last.fm, and enable the features you want for your account.

Jellyfin 10.11 is unsupported. Users of the older Last.fm plugin must disable its
scrobbling before enabling this one and reconnect their accounts. The new GUID is
`2034650d-a290-4a16-b195-89fb44cfb932`; old credentials and XML settings are not
silently copied. See [migration](docs/migration.md).

## Boundaries

Generated playlists contain items the user can access in Jellyfin. External
discovery links do not stream or download unavailable music. The plugin does not
use Last.fm's deprecated radio or playlist APIs and does not download artwork.
Its dashboard is a separate authenticated page; custom controls do not
automatically appear in every Jellyfin TV or mobile client.

History import updates Jellyfin's aggregate play count and last-played fields,
without fabricating native chronological play events. It uses a monotonic floor
to avoid double-adding existing counts; unmatched and ambiguous tracks remain for
review. Last.fm offers no idempotency key for scrobbles, so an accepted request
whose reply is lost cannot be guaranteed exactly once.

See [account privacy and operations](docs/operations.md),
[API research and integration boundaries](docs/lastfm-api.md), and
[architecture](docs/architecture.md). Performance measurements and their limits
are recorded in [the benchmark documentation](benchmarks/README.md).

Build and validation instructions are in CONTRIBUTING.md. New source is licensed
under EUPL-1.2; Last.fm data and third-party dependencies retain their own terms.
