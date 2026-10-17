# JellySin Last.fm

<!-- praetor:readme-governance:start -->
[![HISS Adopted][praetor-hiss-badge]][praetor-hiss-agents]

Praetor manages this repository's declared governance policy. This managed
block records adoption state; it is not a verification certificate.

**Verification**: `make verify-all` runs the repository's configured
verification cascade.

**HISS Audit**: `praetorctl audit` enforces policy, generated-surface
integrity, and the debt ratchet.

**Context Sync**: `praetorctl compile-context --verify` verifies every
generated agent context against `AGENTS.md`.

**Debt Baseline**: `.standards-baseline.json` anchors the debt ratchet at
20 recorded infractions; audit forbids growth.

[praetor-hiss-badge]: https://img.shields.io/badge/Standards-HISS%20Adopted%20(20%20baselined)-yellow
[praetor-hiss-agents]: https://github.com/jellysin/plugin-lastfm/blob/HEAD/AGENTS.md
<!-- praetor:readme-governance:end -->

Last.fm music integration for **Jellyfin 12**: scrobbling, favourites, listening
insights, discovery and playlists. This is a new implementation with its own
plugin identity. The plugin is in development; the first release is [0.1.0](https://github.com/jellysin/plugin-lastfm/releases/tag/v0.1.0).

It is a project of the [JellySin](https://github.com/jellysin) organisation.
JellySin: Jellyfin plus sin, a little temptation for your media server; said JELLY-sin.

## Music features

- Now-playing updates and scrobbles based on observed listening, with a bounded
  durable delivery queue and visible retry/reconnect state.
- Last.fm loved tracks and Jellyfin favourites, with additive initial reconciliation
  and an explicit review before propagating removals.
- Recent history, top tracks/artists/albums and listening statistics; an optional
  previewed import raises local aggregate play counts and last-played dates.
- Discovery of playable music already in your library and off-library tracks,
  artists and albums linked to Last.fm.
- Private Jellyfin playlists from loved tracks, charts, similarity and discovery,
  with saved recipes and optional daily refresh.
- Metadata providers for artist biographies, album wikis and album release dates,
  with HTML stripped to plain text and a link back to the Last.fm page.
- External links to Last.fm artist and album pages built from library names on page
  render without network calls.

The account dashboard is served by the plugin at `/JellySin/Lastfm/` under your
Jellyfin server's base path. Sign in with Jellyfin credentials or Quick Connect,
then select **Connect Last.fm**: the browser opens Last.fm, you allow access there,
and it returns to the dashboard connected. Each Jellyfin user manages their own
account. Last.fm passwords and session keys are never entered into the dashboard.

## Installation after the first release

1. Run Jellyfin 12 and add the **JellySin Plugin Repository** in the administrator dashboard:
   `https://raw.githubusercontent.com/jellysin/repo/main/manifest.json`.
2. Install **JellySin Last.fm** when it appears in the Plugin Repository, then restart Jellyfin.
3. As administrator, open the plugin's settings page and select **Connect Last.fm**
   to connect your own account. Release builds need no application key.
4. Every other user visits `/JellySin/Lastfm/` on the server, signs in, selects
   **Connect Last.fm** and enables the features they want for their account.
   An administrator can add a [link to it in Jellyfin's menu](docs/operations.md#a-link-in-jellyfins-menu).

Jellyfin 10.11 is unsupported. Users of the older Last.fm plugin must remove it,
restart Jellyfin and reconnect their accounts in this plugin. The new GUID is
`2034650d-a290-4a16-b195-89fb44cfb932`; old credentials and XML settings are not
silently copied. See [migration](docs/migration.md).

## Jellyfin versions and clients

The plugin is compiled against Jellyfin 12.0.0 and supports Jellyfin 12 from there
up. Every change is checked on Jellyfin 12.0.0, 12.1.0 and 12.2.0; [release readiness](docs/release-readiness.md) names what each check
covers.

Scrobbling reads the playback reports the server receives, whichever client sends
them; a client that reports no playback to the server produces no listen. Some
clients report only when a track starts and when it stops. For such a session the
server's own once-a-second position updates count as listening time, up to the
position the client's stop reports, and only once that stop arrives. Plays a client
made offline and reports later only as a last-played date are not scrobbled
([#129](https://github.com/jellysin/plugin-lastfm/issues/129)). The checks
send these reports through the server's API. The account dashboard is a web page of
the server and was checked in a Chromium browser and in Jellyfin's web interface;
Jellyfin's TV, mobile and desktop clients were not checked.

Planned work is listed in the repository's
[milestones](https://github.com/jellysin/plugin-lastfm/milestones).

## Removing the plugin

Removing the plugin in Jellyfin removes its program files only. Its data folder,
which holds the account state and the key ring, and its settings file stay on the
server, and Last.fm keeps each authorization until its user revokes it there.
[Stored data](docs/operations.md#stored-data) names the places and what a check showed.

## Boundaries

Generated playlists contain items the user can access in Jellyfin. External
discovery links do not stream or download unavailable music. The plugin does not
use Last.fm's deprecated radio or playlist APIs and does not download artwork.
Its dashboard is a separate authenticated page; custom controls do not
automatically appear in every Jellyfin TV or mobile client.

History import updates Jellyfin's aggregate play count and last-played fields,
without fabricating native chronological play events. It retains the higher count
and later verified date; a later deliberate Jellyfin reset remains possible.
Unmatched and ambiguous tracks remain for review. Last.fm offers no idempotency key for scrobbles, so an accepted request
whose reply is lost cannot be guaranteed exactly once.

See [account privacy and operations](docs/operations.md),
[API research and integration boundaries](docs/lastfm-api.md), and
[architecture](docs/architecture.md). Performance measurements and their limits
are recorded in [the benchmark documentation](benchmarks/README.md).

Build and validation instructions are in CONTRIBUTING.md. New source is licensed
under EUPL-1.2; Last.fm data and third-party dependencies retain their own terms.
