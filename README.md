<!-- markdownlint-disable MD013 MD023 MD033 MD036 MD041 -->
<!-- A centred header needs inline HTML, a banner image, indented headings and
     badge lines past 80 columns, so those five rules are off for this file.
     Same directive style as docs/adr/0000-template.md. The prose below still
     wraps at 80. -->
<div align="center">

  <img src="docs/assets/plugin-lastfm-readme-banner.svg" alt="JellySin Last.fm — Last.fm music integration for Jellyfin 12" height="120" />

  <!-- badge block: groups in the fleet README standard's fixed order -->
  <!-- 1 release/version -->
  [![Release](https://img.shields.io/static/v1?label=release&message=v0.5.0&style=for-the-badge&color=3b82f6&labelColor=111a35)](https://github.com/jellysin/plugin-lastfm/releases) <!-- x-release-please-version -->
  <!-- 2 CI/build -->
  [![CI](https://github.com/jellysin/plugin-lastfm/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/jellysin/plugin-lastfm/actions/workflows/ci.yml)
  <!-- 3 security/scorecard -->
  [![CodeQL](https://github.com/jellysin/plugin-lastfm/actions/workflows/codeql.yml/badge.svg?branch=main&event=push)](https://github.com/jellysin/plugin-lastfm/actions/workflows/codeql.yml)
  [![OpenSSF Scorecard](https://img.shields.io/badge/OpenSSF-Scorecard-06b6d4?style=for-the-badge&labelColor=111a35)](https://scorecard.dev/viewer/?uri=github.com/jellysin/plugin-lastfm)
  [![Codacy](https://app.codacy.com/project/badge/Grade/0fb5f6413ce34af6984aecaac7ace613)](https://app.codacy.com/gh/jellysin/plugin-lastfm/dashboard)
  [![CodeRabbit](https://img.shields.io/badge/Reviews-CodeRabbit-8b5cf6?style=for-the-badge&logo=coderabbit&logoColor=white&labelColor=111a35)](https://coderabbit.ai)
  <!-- 4 standards -->
  [![HISS-21](https://img.shields.io/badge/Standards-HISS--21-06b6d4?style=for-the-badge&labelColor=111a35)](AGENTS.md)
  [![Conventional Commits](https://img.shields.io/badge/Commits-Conventional-8b5cf6?style=for-the-badge&logo=conventionalcommits&logoColor=white&labelColor=111a35)](CONTRIBUTING.md)
  <!-- 5 licence -->
  [![License](https://img.shields.io/badge/License-EUPL--1.2_·_CC--BY--SA--4.0-3b82f6?style=for-the-badge&labelColor=111a35)](LICENSE)

  **Last.fm music integration for Jellyfin 12 — scrobbling, favourites, insights, discovery and playlists**

  *A new implementation with its own plugin identity, built for Jellyfin 12*

</div>

Last.fm music integration for **Jellyfin 12**: scrobbling, favourites, listening
insights, discovery and playlists. This is a new implementation with its own
plugin identity. The plugin is in development; the first release is [0.1.0](https://github.com/jellysin/plugin-lastfm/releases/tag/v0.1.0).

It is a project of the [JellySin](https://github.com/jellysin) organisation.
JellySin: Jellyfin plus sin, a little temptation for your media server; said JELLY-sin.

**[📖 Documentation](docs/architecture.md)** · [Integration boundaries](docs/lastfm-api.md) · [Operations](docs/operations.md)

---

## Why JellySin Last.fm

### Music features

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

The user page opens from the Last.fm row of Jellyfin's user settings using the
web client session. Select **Connect Last.fm**: the browser opens Last.fm, you allow access there,
and it returns connected. Each Jellyfin user manages their own
account. Last.fm passwords and session keys are never entered into the dashboard.

## Get started

### Installation after the first release

1. Run Jellyfin 12 and add the **JellySin Plugin Repository** in the administrator dashboard:
   `https://raw.githubusercontent.com/jellysin/repo/main/manifest.json`.
2. Install **JellySin Last.fm** when it appears in the Plugin Repository, then restart Jellyfin.
3. As administrator, open the plugin's settings page and select **Connect Last.fm**
   to connect your own account. Release builds need no application key.
4. Every other user opens the Last.fm row of Jellyfin's user settings, selects
   **Connect Last.fm** and enables the features they want for their account.

Jellyfin 10.11 is unsupported. Users of the older Last.fm plugin must remove it,
restart Jellyfin and reconnect their accounts in this plugin. The new GUID is
`2034650d-a290-4a16-b195-89fb44cfb932`; old credentials and XML settings are not
silently copied. See [migration](docs/migration.md).

### Removing the plugin

Removing the plugin in Jellyfin removes its program files only. Its data folder,
which holds the account state and the key ring, and its settings file stay on the
server, and Last.fm keeps each authorization until its user revokes it there.
[Stored data](docs/operations.md#stored-data) names the places and what a check showed.

## Architecture at a glance

### Jellyfin versions and clients

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

### Boundaries

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

## Guides and reference

Detailed documentation covers architecture, integration points, operations, and benchmarks:

- [Architecture and design](docs/architecture.md)
- [Where each function lives in Jellyfin](docs/jellyfin-integration.md)
- [Last.fm API research and integration boundaries](docs/lastfm-api.md)
- [Account privacy and operations](docs/operations.md)
- [Release readiness and test matrix](docs/release-readiness.md)
- [Migration from older plugins](docs/migration.md)
- [Benchmark documentation](benchmarks/README.md)

## Contribute

Build and validation instructions are in [CONTRIBUTING.md](CONTRIBUTING.md).

## Roadmap and releases

- [Releases and downloads](https://github.com/jellysin/plugin-lastfm/releases)
- [Changelog](CHANGELOG.md)
- Planned work is listed in the repository's [milestones](https://github.com/jellysin/plugin-lastfm/milestones).

## Upstream and licence

Code is licensed under EUPL-1.2 and documentation under CC-BY-SA-4.0, as [REUSE.toml](REUSE.toml) records file by file; Last.fm data and third-party dependencies retain their own terms. Full licence texts live in [`LICENSE`](LICENSE) and under [`LICENSES/`](LICENSES/).

## Standards & Governance

<!-- praetor:readme-governance:start -->
Praetor manages this repository's declared governance policy. This managed
block records adoption state; it is not a verification certificate.

**Verification**: `make verify-all` runs the repository's configured
verification cascade.

**HISS Audit**: `praetorctl audit` enforces policy, generated-surface
integrity, and the debt ratchet.

**Context Sync**: `praetorctl compile-context --verify` verifies every
generated agent context against `AGENTS.md`.

**Debt Baseline**: `.standards-baseline.json` anchors the debt ratchet at
0 recorded infractions; audit forbids growth.
<!-- praetor:readme-governance:end -->
