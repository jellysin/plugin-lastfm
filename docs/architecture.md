# Architecture

JellySin Last.fm targets Jellyfin 12 and .NET 10 directly. There is one plugin
assembly and no Emby/shared-core compatibility layer. Shared release tooling is a
build dependency, never a runtime dependency.

## Runtime boundaries

Playback callbacks enqueue bounded observations. A worker tracks actual listening
and persists eligible listens in a durable outbox. The transport serializes and
prioritizes Last.fm work, applies request bounds and interprets API errors even
when the HTTP response succeeds. Acknowledgements are handled per submitted item.

The state store provides bounded atomic document updates. User identity scopes
account data, delivery, previews, favourite reconciliation and playlist recipes.
Disconnect cancels linked operations before deleting user state. Server-side
Data Protection separates secret persistence from ordinary plugin XML settings.

Music services perform conservative local-library matching and check access for
the caller before reading or mutating items. Favourite removals and history imports
are explicit reviewed operations. Playlist updates keep a recovery record because
Jellyfin's clear-and-add update API is not an atomic transaction.

Public metadata and remote similarity are user-independent. Jellyfin's shared
similarity cache does not include user identity; personalized discovery therefore
stays in the user-scoped service and generated playlists.

## HTTP surface

The plugin serves an allowlisted HTML/CSS/JavaScript application and public
bootstrap information under `/JellySin/Lastfm/`. Private routes require a Jellyfin
session and derive identity from its authenticated claims. Account requests do
not accept an arbitrary target user ID. Request origin and input bounds are
checked at the API boundary.

| Route group | Purpose |
| --- | --- |
| `Me/Connection`, `Me/Scrobbling` | User connection lifecycle and listening preference |
| `Me/History`, `Me/Overview`, `Me/Charts` | Private history and listening insights |
| `Me/History/Preview`, `Continue`, `Import` | Resumable preview and explicit aggregate import |
| `Me/Discovery`, `Me/Library/Search` | Accessible local results, external suggestions and seed lookup |
| `Me/Favourites` | Opt-in reconciliation and removal review |
| `Me/Playlists` | User-owned generated playlists and saved recipes |
| `Admin/Application` | Administrator-only application credential override |

Public assets are served from embedded resources with no-store, a restrictive CSP,
no-referrer and nosniff headers. URL composition respects Jellyfin's reverse-proxy
base path. The dashboard uses Jellyfin sign-in or Quick Connect and a separate
Last.fm browser grant; no Last.fm mobile/password authentication is used.

## Delivery

The plugin descriptor contains a new stable GUID and an independent ABI floor of
`12.0.0.0`. SemVer `X.Y.Z` maps to assembly/catalog `X.Y.Z.0`.

Reviewed release-please changes create a draft/tag and explicitly dispatch the
publication workflow on that tag. CI verifies the exact source, then production
builds embed the project application's credentials. Shared tooling produces a
deterministic flat ZIP, metadata, SHA-256/MD5 checksums and an SPDX file inventory.
The workflow attests all four artifacts and verifies existing bytes before
resuming an interrupted draft upload. Published releases must be immutable.

The catalog polls only allowlisted public plugin repositories. It independently
verifies signed workflow/tag/commit provenance and package contents, then opens
its own PR and explicitly dispatches CI. Each repository uses its own built-in
GITHUB_TOKEN; there is no cross-repository write credential.
