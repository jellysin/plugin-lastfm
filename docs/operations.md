# Accounts, privacy and recovery

## Account connection

The dashboard lives at `/JellySin/Lastfm/`, prefixed by Jellyfin's configured base
path. Sign in with Jellyfin credentials or Quick Connect, then select **Connect
Last.fm**. The browser opens Last.fm; after you allow access it returns to the
dashboard and the connection completes without a further step. You never send a
Last.fm password to this plugin. An administrator can connect their own account
the same way from the plugin's settings page in Jellyfin's web interface.

A pending attempt belongs to one Jellyfin user and one browser, can be used once
and expires after 10 minutes. If the return does not connect, start again:

- Finish in the browser that started the attempt, with cookies allowed for the
  Jellyfin server.
- A restart of Jellyfin ends open attempts.
- The return address given to Last.fm starts with the address the server
  publishes for itself: the start option `--published-server-url`, in a
  container the variable `JELLYFIN_PublishedServerUrl`. Only its scheme, host
  and port are used; the path is Jellyfin's base path setting. A request cannot
  change this address, and a proxy that ends TLS and hides the public scheme
  from Jellyfin does not matter. When no address is published, the scheme and
  host of the request are used, so behind a reverse proxy Jellyfin must then
  see the public ones (forwarded headers and Networking, Known proxies).
- Both pages compare the return address with the address the browser itself
  uses before they leave. If the two differ they do not open Last.fm, so
  Last.fm issues no token and none is sent to the other address. A user reads
  "Last.fm sign-in could not start. Ask the administrator of this server to
  check the server address setting." An administrator reads both addresses and
  what to set, on the dashboard and on the settings page. With a published
  address this also refuses a browser that opened the server under another
  address: connect from the published one. The browser tests "the page does not
  leave for ...", "an administrator reads why the page does not leave for ..."
  and "the settings page does not leave for ..." in
  `tests/web/dashboard.spec.mjs` cover the refusal for a return to another
  server, to another page of this server and over another scheme.
- A published address that is not an absolute HTTP or HTTPS address is not
  replaced by the request's address: the sign-in answers HTTP 400 and the
  server log holds one warning from start, without the value. Correct the start
  option and restart Jellyfin.
- The button on the settings page is offered only when Jellyfin's web interface
  is served by the same server address. Otherwise connect from the dashboard.
- Every request that changes something must come from a page of this server:
  its origin is the published server address, or the scheme and host the
  request itself arrived with. Behind a proxy that ends TLS without being a
  known proxy, Jellyfin sees `http` where the browser names `https`; the
  published address then covers the browser's side. A page anywhere else, the
  same host under the other scheme and a request the browser marks as sent by
  another site are answered with HTTP 403. Without a published address and
  without forwarded headers, a server behind such a proxy refuses its own
  pages: publish the address or add the proxy under Networking, Known proxies.

### What the server log says about a sign-in

The plugin writes one line at information level for each step, so that a
sign-in that did not work can be read from the server log alone:

- `Last.fm sign-in begun for user ... from the music dashboard` or `from the
  settings page`, with the scheme of the return address, the scheme the request
  reached the server with, and whether the page is at the published server
  address. `Last.fm sign-in not begun ...` names the reason instead.
- `The page did not open Last.fm for user ...`: the page compared the answer
  with its own address and stayed. The line names which comparison failed.
- `Last.fm return accepted ...` or `Last.fm return refused ...` with the reason,
  for example that the browser sent no cookie of an attempt, that the attempt
  has expired or was returned before, or that the return carries no usable
  token.
- `Last.fm sign-in finished for user ...: connected` or `not connected`.
- A warning `Refused <method> <route>: ...` for every changing request the
  origin rule above turned away, with the same two facts about the address.

A user appears as the first twelve hexadecimal digits of a SHA-256 hash of the
user's id, never by name or id. No line holds an address, a query string, a
token, a cookie or a key. Method and scheme are written as fixed words: `POST`,
`PUT` or `DELETE`, and `http`, `https` or `another scheme`; the text a request
carries in their place never reaches the log. Returns that carry no known
attempt come from anyone who can reach the server, so at most 20 of their lines
are written per minute.
When the server answers a start with an error, Jellyfin's own error line follows
the plugin's.

JellySin's project Last.fm application is registered, and its credentials are
configured in the repository secrets for production builds. Browser authorization,
live history/charts/discovery and one explicitly authorized test scrobble were
verified on 2026-09-08 with the token flow used at that time. The return flow
described above is covered by automated tests, and one live sign-in against Last.fm
is recorded: on 2026-10-11 a canary build of main (commit `5dd33c7`) ran on a
Jellyfin 12.2.0 server whose published `https` address resolves to a private network
address, behind a proxy that ends TLS. The server log showed the three lines above in
order within 15 seconds: begun, with an `https` return address on a request that
reached the server over `http`; return accepted; finished, connected. One account
was stored. Last.fm therefore sent the browser back to an address it cannot reach
itself. That trial delivered no scrobble; no listen was played in it.
The [API terms review](lastfm-api.md) records the storage,
attribution and display requirements. The [optional clarification enquiry](lastfm-permission-request.md)
is not sent, by decision of 2026-10-12; no separate written approval has been obtained or is implied by these
tests. Account connection uses the registered application's documented API flow.

Release builds include the project application, so a server needs no application
key. The **Advanced** section of the plugin's settings page shows which application
is in use and lets an administrator store an override or remove it again. Changing
applications requires reconnecting user accounts; old sessions cannot be assumed
valid under another application's credentials.

Last.fm session keys are protected on the server and are not returned to the
browser. Jellyfin sign-in remains subject to Jellyfin's own permissions. The
dashboard does not bypass music-library access restrictions.

## A link in Jellyfin's menu

Jellyfin's web interface lists a plugin's pages for administrators only. An
administrator can give every user a link to the dashboard through the
`menuLinks` list in the web interface's `config.json`:

```json
"menuLinks": [
  {
    "name": "Last.fm",
    "icon": "music_note",
    "url": "/JellySin/Lastfm/"
  }
]
```

- `url` is the path of the dashboard on the server and starts with Jellyfin's
  base path when one is set, for example `/jellyfin/JellySin/Lastfm/`.
- Packages for Debian, Ubuntu, Fedora and CentOS keep the file at
  `/usr/share/jellyfin/web/config.json`. Jellyfin's documentation states that
  the Debian packages register it as a configuration file, so `apt` handles a
  changed file on upgrade. After an update of the web interface, check that the
  entry is still there.
- The container image holds the file at `/jellyfin/jellyfin-web/config.json`.
  A change made inside the container is gone with the next image. Mount a copy
  instead: `--volume /path/to/web-config.json:/jellyfin/jellyfin-web/config.json`.
  The copy must exist as a file before the first start and replaces the whole
  default file, so start from the file of the image in use and compare it with
  the new default after an update.
- The link opens the dashboard in a new tab. The dashboard keeps its own
  sign-in with Jellyfin credentials or Quick Connect.

Checked on 2026-10-11 with the Jellyfin 12.2.0 container image under the base
path `/jellyfin`, for an administrator and for an ordinary user: the link
appears in the top bar of a wide window and in the side menu of a narrow one,
and opens the dashboard. Package installations and other clients than the web
interface were not checked; the file locations are those of
[Jellyfin's documentation](https://jellyfin.org/docs/general/clients/web-config/),
read on the same day.

## Stored data

The plugin stores state under `jellysin-lastfm/state` and its protection key ring
under `jellysin-lastfm/keys` inside Jellyfin's data directory. Back up both together
when moving the server. Losing the key ring requires reconnecting accounts. An
application override stored under the lost key ring is not used: the administration
page reports it as unreadable, and nothing reaches Last.fm until an administrator
enters the application again or removes the override.
Server administrators can access host files and remain trusted.

The state store has an 80 MB aggregate accounting budget, an 8 MB document limit
and bounded document counts. Disposable cache entries
are evicted before durable account or delivery state. Reaching a durable limit
reports a failure rather than silently discarding unsent listens. This accounting
does not measure administrator backups or exports, establish Last.fm's allowance
across independent deployments, or grant additional data rights. These details
are included in the unsent clarification draft.

Disconnecting cancels account work and removes that user's plugin state, including
session data, private caches and pending delivery. It does not delete data already
sent to Last.fm or reverse changes applied to Jellyfin. Last.fm authorization can
be revoked separately in its application settings.

Removing the plugin in Jellyfin does not remove this data: the plugin has no step
that runs at removal. Checked on 2026-10-11 with the Jellyfin 12.2.0 container image
and a build of `1cd8e91` copied into the plugin folder by hand: after the removal the
server no longer listed the plugin, also after a restart, and the folder with the
plugin file was gone. Still present were `jellysin-lastfm` in the data directory,
with a preference the plugin had stored for a user, and the settings file
`configurations/JellySin.Plugin.Lastfm.xml` in Jellyfin's plugin directory.
No Last.fm account was connected in that run, so the key ring folder was empty, and
an installation from the Plugin Repository was not checked. To remove the data as
well, delete that folder and that file while the server is stopped.

## Scrobbling

Only observed listening counts. Pauses and seeks do not create listening time.
Tracks must exceed 30 seconds and satisfy the documented half-track or four-minute
threshold. Original UTC start times are retained when delivery is delayed.

The plugin settings page chooses which media types count, under "Media types to
scrobble". Music tracks count by default. Audiobook chapters and music videos count
only when an administrator switches them on: a chapter is not a track, and the
artist Jellyfin records for it is the narrator or author. A music video is sent with
its artist and title and no album, because Jellyfin keeps no album for it. Other
videos never count. A configuration saved before these switches existed scrobbles
music only (`ConfigurationTests.SettingsSavedBeforeMediaTypesExistedScrobbleMusicOnly`).

Each user can narrow these rules for their own account on the dashboard, under
"Library scrobbling", "Devices that scrobble" and "Media types that scrobble". A
user can exclude more libraries, devices or media types, but never turn on what an
administrator turned off. A type the administrator turned off is shown disabled and
keeps the user's own choice for when it is turned on again
(`dashboard.spec.mjs`, "saving media types keeps the user exclusion").

The device list holds only devices the user signed in on, newest first, at most 64
(`DeviceAndMediaTypeRuleTests.UserDevicesController_ListsOnlyDevicesTheCallerSignedInOn`);
devices of other users are never listed, even for users allowed on all devices. The
exclusions are stored with the library exclusions in the user's plugin state and
checked from memory while playback is captured.

A play that is running while its user connects again, also to the same Last.fm
account, is not submitted: connecting starts a new capture, and the tracker drops a
play begun under the previous one and ignores its remaining progress. The next
track counts as usual. This follows from the code and matches the 2026-10-12
observation on 12.2.0 in [validation](validation.md#observed-on-2026-10-12-with-jellyfin-1220).

The outbox survives restarts, submits at most 50 scrobbles per request, and keeps
bounded acknowledgement records. A play is sent as soon as it is stored, and a
now-playing update as soon as playback reports it; neither waits for a timer.
Failed now-playing updates are discarded because they describe a transient state. Temporary delivery failures receive bounded
backoff; invalid sessions require reconnecting and blocked errors are visible in
the account dashboard.

Daily-limit and rate-limit rejections remain queued. A play Last.fm ignores with
code 4 or 5 holds that user's delivery, so later plays wait behind it and keep
their order:

- Code 4 (timestamp too new) usually means the server's clock is ahead of
  Last.fm's. The held play is sent again by itself after a delay that grows from
  about 15 seconds to at most an hour, together with the plays behind it. The
  **Retry Last.fm scrobble delivery** scheduled task sends it on its first run
  after that delay.
- Code 5 (daily scrobble limit exceeded) holds until the user chooses **Retry**
  on the Delivery page, once the limit has reset.

The Delivery page counts held plays as awaiting retry and says whether they
retry by themselves (code 4) or wait for the daily limit (code 5).

The Delivery page also reports the age of the oldest play still waiting for
delivery, and warns once that play is 7 days old. The 7 days are the plugin's own
choice: Last.fm documents no fixed age limit, only that it ignores plays it
finds too old (ignore code 3).

The dashboard distinguishes durably saved submissions from observations still
waiting for an outbox write.
If storage is unavailable, the bounded recovery buffer can retry while the process
is alive; those not-yet-saved observations cannot survive a crash or guarantee
persistence through continued disk failure.

Last.fm has no scrobble idempotency key. If it accepts a batch and the response is
lost, retrying can duplicate a listen. Local occurrence identifiers and receipts
reduce duplicates but cannot provide an exactly-once guarantee across the network.

### Scheduled tasks

The plugin adds three tasks under **Dashboard > Scheduled Tasks**, category
**Last.fm**. Each can be started by hand, given other triggers, or stopped there.
A run never overlaps another run of the same task.

| Task | Default trigger | What it does |
| :--- | :--- | :--- |
| Reconcile Last.fm favourites | at server start, then every 5 minutes | One favourite run for each connected account |
| Refresh Last.fm playlists | at server start, then every 5 minutes | Refreshes each account's Last.fm playlists that are due |
| Retry Last.fm scrobble delivery | at server start, then every minute | Sends plays waiting for a backoff and retries plays the server could not store |

The start run finishes work a restart interrupted, such as a playlist write,
without waiting for the first interval. Jellyfin starts the next run no sooner
than one minute after the previous run plus the interval, so on an idle server
the runs come about 6 and 2 minutes apart.
Accounts that need reconnecting are skipped. Removing a task's triggers stops that
work until it is started by hand; fresh plays are still sent when stored.

### What the server log says about delivery

The plugin writes one line for each submitted batch of plays and each playing-now
update, so that scrobble activity and delivery results can be read from the
server log alone:

- `Plays submitted for user ...` at information level, with the number of plays
  sent in the call, the number Last.fm accepted, the number it ignored, and the
  ignore codes it gave, or `none`.
- A warning `Submitting plays for user ... failed with Last.fm error ...` with
  the error code Last.fm returned and the number of plays in the call. They
  remain stored.
- `Playing-now update for user ...: Last.fm accepted it.` at information level.
- `Playing-now update for user ...: Last.fm ignored it (ignore code ...).` at
  information level, with the ignore code.
- `Playing-now update for user ...: Last.fm answered without an error.` at
  information level, when Last.fm answered without an ignore code.
- `Playing-now update for user ...: Last.fm refused it with error ... It is not repeated.`
  at information level, when Last.fm returned an error code.

A line with nothing to report is not written. An update another track overtook leaves
no line. A user appears as in the
[sign-in lines](#what-the-server-log-says-about-a-sign-in), never by name or id.
No line holds an account name or a track.

The ignore codes Last.fm documents: 1 artist ignored, 2 track ignored,
3 timestamp too old, 4 timestamp too new, 5 daily scrobble limit exceeded.

## Favourites, history and playlists

Favourite synchronization starts with an additive reconciliation. A detected
removal becomes a review item rather than immediately removing the other copy.
Incomplete remote collections pause reconciliation. Before applying a removal,
the service rechecks the current library identity and remote state.

**Save preference** and **Sync now** start the reconciliation as a run on the
server and answer within about ten seconds. A longer run continues on the
server and the page says so; **Refresh review** shows its result. Closing the
page does not stop the run. Switching the feature off, a change of the Last.fm
connection and a server stop do; a request that still waits for a stopped run
answers with the stored state. One run per user exists at a time, and the
**Reconcile Last.fm favourites** scheduled task uses the same run.

A run handles at most 200 tracks in id order, and the scheduled task starts one
every five minutes by default, so a large collection takes several runs to reconcile. Once a
first pass has completed, a run also handles up to 200 more tracks whose favourite
or love changed since a run last saw them, so a change does not wait for the run
whose 200 tracks hold it. A love the plugin sent takes none of those places until
a run has seen Last.fm list it with its date; until then, removing it on Last.fm
waits for the run whose 200 tracks hold it. Each run reads page 1 of the loved tracks. When page 1 is
unchanged, the run reuses the collection read by an earlier run. It also reuses a
loved track's library match until the library or the user's access changes. A new love or a removal on Last.fm changes
page 1, so the next run reads the whole collection again. The server keeps this in
memory only, for at most 24 hours, 8 users and 10,000 tracks per user, and a
restart discards it. The [benchmarks](../benchmarks/README.md#favourite-synchronization)
show what it changes.

A run that fails writes one warning that begins `Favourite synchronization for
user`, names the user as in
[the sign-in lines](#what-the-server-log-says-about-a-sign-in) and the type of
the error, never its message, and leaves the status "The last synchronization
did not finish. Synchronize again." A run without a Last.fm connection writes
no warning; the page asks to connect.

A run that pauses because Last.fm's loved tracks did not read back as one
stable, complete collection writes one warning that begins `Favourite
synchronization for user`, names the user the same way and names the check that
failed:

| Check | Meaning |
| --- | --- |
| `PageCountChanged` | The number of pages changed between two pages of the read. |
| `TotalChanged` | The reported track total changed during the read or on the closing re-read of page 1. |
| `ItemCountDiffers` | The tracks read differ in number from the reported total, for example when a loved track has no artist or title. |
| `FirstPageChanged` | Page 1 read at the end differs from page 1 read at the start. |
| `DuplicateTracks` | The same track appears more than once. |
| `EmptyPage` | A page before the last one held no tracks. |
| `PageLimit` | The collection has more than 50 pages of 200 tracks. |

The warning carries counts only: pages read and reported, the total reported
first and last, tracks read and distinct tracks. For `FirstPageChanged` it adds
how many positions of page 1 differ between the two reads, whether both reads
hold the same tracks with the same loved dates, and how many tracks of page 1
share a loved date with another. The same tracks in another order, with tied
dates, would suggest that Last.fm returns tracks loved in the same second in no
fixed order; other tracks or dates would suggest that the collection changed
during the read. It never carries track names.

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

## Library and clients

The plugin writes no Last.fm metadata, tags or artwork into the Jellyfin library and
adds no entry to a library's provider settings. Jellyfin's own similar-item rows are
unchanged. Personal history and recommendations use user-scoped state.

Use the browser dashboard for account management, insights and external discovery.
Jellyfin's plugin administration page is administrator-only. Native client support
for playlists varies; the plugin cannot add a custom music
home screen to every TV, mobile or third-party client.
