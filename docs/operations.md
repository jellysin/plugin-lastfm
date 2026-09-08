# Accounts, privacy and recovery

## Account connection

The dashboard lives at `/JellySin/Lastfm/`, prefixed by Jellyfin's configured base
path. Sign in with Jellyfin credentials or Quick Connect. Last.fm connection is a
separate browser authorization: begin, approve on Last.fm, then finish connecting
in the dashboard. A pending attempt belongs to one Jellyfin user and expires after
10 minutes. You never send a Last.fm password to this plugin.

JellySin's project Last.fm application is registered, and its credentials are
configured in the repository secrets for production builds. Browser authorization,
live history/charts/discovery and one explicitly authorized test scrobble were
verified on 2026-09-08. The [API terms review](lastfm-api.md) records the storage,
attribution and display requirements. The [optional clarification enquiry](lastfm-permission-request.md)
is unsent; no separate written approval has been obtained or is implied by these
tests. Account connection uses the registered application's documented API flow.

An administrator can configure an application override in Jellyfin's plugin settings. Changing
applications requires reconnecting user accounts; old sessions cannot be assumed
valid under another application's credentials.

Last.fm session keys are protected on the server and are not returned to the
browser. Jellyfin sign-in remains subject to Jellyfin's own permissions. The
dashboard does not bypass music-library access restrictions.

## Stored data

The plugin stores state under `jellysin-lastfm/state` and its protection key ring
under `jellysin-lastfm/keys` inside Jellyfin's data directory. Back up both together
when moving the server. Losing the key ring requires reconnecting accounts.
Server administrators can access host files and remain trusted.

The state store has an 80 MB aggregate accounting budget, an 8 MB document limit
and bounded document counts. The budget includes conservative reservations for
metadata copies passed to Jellyfin's database/NFO storage. Disposable cache entries
are evicted before durable account or delivery state. Reaching a durable limit
reports a failure rather than silently discarding unsent listens. This accounting
does not measure administrator backups or exports, establish Last.fm's allowance
across independent deployments, or grant additional data rights. These details
are included in the unsent clarification draft.

Disconnecting cancels account work and removes that user's plugin state, including
session data, private caches and pending delivery. It does not delete data already
sent to Last.fm or reverse changes applied to Jellyfin. Last.fm authorization can
be revoked separately in its application settings.

## Scrobbling

Only observed listening counts. Pauses and seeks do not create listening time.
Tracks must exceed 30 seconds and satisfy the documented half-track or four-minute
threshold. Original UTC start times are retained when delivery is delayed.

The outbox survives restarts, submits at most 50 scrobbles per request, and keeps
bounded acknowledgement records. Failed now-playing updates are discarded because
they describe a transient state. Temporary delivery failures receive bounded
backoff; invalid sessions require reconnecting and blocked errors are visible in
the account dashboard.

Daily-limit and rate-limit rejections remain queued. The dashboard distinguishes
durably saved submissions from observations still waiting for an outbox write.
If storage is unavailable, the bounded recovery buffer can retry while the process
is alive; those not-yet-saved observations cannot survive a crash or guarantee
persistence through continued disk failure.

Last.fm has no scrobble idempotency key. If it accepts a batch and the response is
lost, retrying can duplicate a listen. Local occurrence identifiers and receipts
reduce duplicates but cannot provide an exactly-once guarantee across the network.

## Favourites, history and playlists

Favourite synchronization starts with an additive reconciliation. A detected
removal becomes a review item rather than immediately removing the other copy.
Incomplete remote collections pause reconciliation. Before applying a removal,
the service rechecks the current library identity and remote state.

History import starts with a preview. It raises play counts to the larger of
existing local and Last.fm aggregate counts and only moves last-played dates
forward where an observed remote date is available. It does not add Last.fm totals
on top of local totals or fabricate Jellyfin play events. Unmatched or ambiguous
tracks are shown separately. Bounded continuation first gathers counts and then
verified listening dates. Confirmation becomes available when retrieval completes;
each confirmed application handles at most 200 tracks and saves its progress.
Expired previews must be refreshed before applying.

Generated playlists contain accessible local items and belong to the requesting
user. Recipes support loved tracks, top tracks, similarity or discovery, with 1–200
items and optional daily refresh. The service resolves the full desired membership
before updating a playlist and records pending operations for restart recovery.
Stopping recipe management leaves the playlist itself in Jellyfin. An interrupted
update appears in the page for review. Cancelling it pauses daily refresh and keeps
the possibly partial playlist; subsequent cancellation recovery completes that
intent instead of resuming the old update.

## Metadata and clients

Metadata and native similarity use public, user-independent data. Personal history
and recommendations use user-scoped state. The plugin does not place one user's
personal results into Jellyfin's shared similarity cache. Community tags are
Last.fm tags, not a curated genre taxonomy. Artwork is not downloaded by this plugin.

Use the browser dashboard for account management, insights and external discovery.
Jellyfin's plugin administration page is administrator-only. Native client support
for playlists and similar-item rows varies; the plugin cannot add a custom music
home screen to every TV, mobile or third-party client.
