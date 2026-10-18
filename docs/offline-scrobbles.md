# Offline plays: design note

The plugin scrobbles only plays that Jellyfin reports through its playback
start, progress and stop events. Plays a client makes offline and reports later
are not scrobbled. This note records what Last.fm and Jellyfin 12 offer, which
of it was measured, and what would have to change upstream before offline
plays could be supported ([#129](https://github.com/jellysin/plugin-lastfm/issues/129)).
It describes no current behaviour beyond what
[`lastfm-api.md`](lastfm-api.md) already states.

## Last.fm limits

| Limit | Value | Source |
| :--- | :--- | :--- |
| Scrobbles per request | at most 50 | [`track.scrobble`](https://www.last.fm/api/show/track.scrobble) and the [scrobbling guide](https://www.last.fm/api/scrobbling), read 2026-10-18; the plugin sends batches of at most 50 (`BatchSize` in `Playback/DeliveryService.cs`) |
| Timestamp | the UTC start of the play, in seconds since the Unix epoch | `track.scrobble`, read 2026-10-18 |
| Order | cached scrobbles go before new ones | scrobbling guide, read 2026-10-18; the outbox sends the oldest pending play first (`Playback/ScrobbleOutbox.cs`) |
| Timestamp window | **not documented, not measured** | Last.fm publishes no age limit. Its documentation only names the ignore codes 3 (timestamp too old) and 4 (timestamp too new) that an accepted request carries for a play outside the window. |

The window is the one number an offline design would depend on: plays older
than it would be ignored with code 3. Measuring it needs signed writes to a
real Last.fm account. Before any implementation, a probe on a designated test
account, which only the operator can provide, would send plays started one
hour ahead and 1, 7, 13, 14, 15 and 30 days back, record the ignore code for
each offset, and remove the probe plays afterwards.
Today the plugin records ignore code 3 as a final result and code 4 as a retry
after a delay (`Playback/ScrobbleOutbox.cs`), so plays outside the window are
reported, not lost silently.

## What Jellyfin 12 reports

Measured by reflection over the pinned `Jellyfin.Controller` and
`Jellyfin.Model` 12.0.0 assemblies on 2026-10-18:

- `ISessionManager` raises `PlaybackStart`, `PlaybackProgress` and
  `PlaybackStopped`. Their `PlaybackProgressEventArgs` and
  `PlaybackStopEventArgs` carry the item, users, position, pause state, device,
  client and play session, but no start time. The plugin therefore takes the
  start time from the server clock when the start report arrives
  (`Playback/PlaybackService.cs`).
- The client's report type `PlaybackProgressInfo` has a
  `PlaybackStartTimeTicks` field. None of the server event arguments or
  `SessionInfo.PlayState` (`PlayerStateInfo`) passes it on, so a plugin cannot
  read it.
- `IUserDataManager.UserDataSaved` raises `UserDataSaveEventArgs` with the
  user, the item, the save reason (`PlaybackStart`, `PlaybackProgress`,
  `PlaybackFinished`, `TogglePlayed`, `UpdateUserRating`, `Import`,
  `UpdateUserData`) and the stored `UserItemData`. That record keeps one
  `LastPlayedDate` and a `PlayCount`, not a list of plays.
- The client's write type `UpdateUserItemDataDto` likewise carries a single
  `LastPlayedDate` and `PlayCount`.

Known clients match this: Streamyfin reports an offline play as an
`UpdateUserData` save with only the latest last-played date, Jellyfin MPV Shim
sends user data without the original play time, and Amperfy uses the Subsonic
API only ([#129](https://github.com/jellysin/plugin-lastfm/issues/129)).

## Why the plugin does not derive plays from user data

A user-data save tells the plugin at most that the play count rose and when the
last play happened. Several offline plays of one track collapse into one
record, and every play but the last has no time at all. A scrobble built from
it would have to invent a start time, which Last.fm requires and which retried
scrobbles must keep unchanged. The plugin does not send invented times.

## What would make offline plays possible

Either of these upstream changes, followed by the timestamp-window probe above:

- a Jellyfin API through which a client reports each offline play with its own
  start time, raised to plugins as an event that carries that time; or
- server-side play history that records each play with its time and raises it
  to plugins.

With such a source, offline plays would enter the existing outbox with their
reported start times. They would go out oldest first in batches of at most 50,
and plays outside the window would end with ignore code 3, as they do today.
