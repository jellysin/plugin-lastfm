# ADR 0001: coordinate music imports with Jellyfin userdata writes

Status: implemented; the pinned Jellyfin 12 integration probe is a required check.

An import must raise existing play counts and verified dates without overwriting a
concurrent native play. A later deliberate native reset must still work. Jellyfin
12 returns detached userdata snapshots, while a retained `BaseItem` can contain
an older userdata row. Re-reading that same item immediately before a write is
therefore insufficient. The native probe reproduced a stored count falling from
10 to 6 when a playback snapshot predating an import was subsequently saved.

The plugin decorates only the singleton concrete
`Emby.Server.Implementations.Library.UserDataManager` from
`Emby.Server.Implementations`. That verified implementation has no disposable
resources requiring ownership transfer. Unknown implementations, factory
registrations, instances, or service lifetimes remain untouched; history imports
and conditional favourite writes then fail closed. Plugin load order and another
plugin replacing this registration are compatibility boundaries, not assumed
guarantees.

Only audio userdata receives coordination. Native video/resume behavior delegates
directly. A user gate serializes audio saves with imports. Weak snapshot tracking
distinguishes a snapshot read before an import from a fresh native edit. An old
snapshot merges the latest persisted count/date floor; a fresh reset may lower
them. Batch snapshots and saved values are copied so callers cannot mutate the
host's shared userdata cache before saving.

Mutation boundaries obtain current userdata through an explicit one-item library
query with user data enabled. This public API reads the repository rather than
trusting the supplied item's snapshot. The library service is resolved lazily to
avoid its dependency cycle through the userdata manager. Writes still use the
native save API; the plugin never writes Jellyfin database tables directly. Native
DTO patches are applied to that fresh snapshot before saving. The additional
query occurs during a native audio save, import, or conditional favourite write;
plugin playback event callbacks and ordinary userdata reads add no library query.

History snapshots and favourite revision counters are bounded. Snapshot capacity
exhaustion stops new imports visibly while native reads and writes remain
available. Favourite tracking exhaustion requires a restart before further
reviews. A process epoch invalidates pending removal reviews across a restart.
The native fixture must verify the actual DI registration, detached stale
snapshot, persisted floor and date, deliberate native reset, and favourite
remove/re-add rejection before a supported Jellyfin update ships.

References: [Jellyfin 12 userdata implementation](https://github.com/jellyfin/jellyfin/blob/v12.0/Emby.Server.Implementations/Library/UserDataManager.cs),
[Jellyfin 12 library queries](https://github.com/jellyfin/jellyfin/blob/v12.0/Emby.Server.Implementations/Library/LibraryManager.cs).

Last.fm has no conditional love/unlove write or revision token. Reviews compare
the current remote loved timestamp and the complete collection with their saved
observation, and refuse remote removal when its timestamp is absent. Changes
after that read, or remove/re-add within the same timestamp second, cannot be
made atomic across services. Local conditional removal is atomic at Jellyfin's
coordinated write boundary; the UI must not imply cross-service atomic delivery.
