# Native userdata integration fixture

`JellySin.NativeProbe` is a separate test-only plugin, with its own GUID and assembly.
It must never be copied into a production package or a host containing real accounts.
Its endpoints require the host environment opt-in `JELLYSIN_NATIVE_PROBE=1`, a real
administrator session and a generated `JellySin Probe ` user; the playlist endpoints
also require audio fixtures under `/media/Public/`. The probe has no Last.fm client or
account operations.

The disposable pinned Jellyfin 12 host loads the actual production plugin and this
fixture. The probe verifies that native `IUserDataManager` resolves to the production
decorator, then holds a real native snapshot across `IMusicWriter.ApplyHistoryFloor`.
It reports whether a stale write preserves the imported count/date and a fresh native
snapshot can intentionally reset them. The harness also uses Jellyfin's actual
userdata HTTP endpoint for a fresh reset and native favourite remove/re-add routes
before attempting a previously reviewed conditional removal.

Playlist recovery uses a real private native playlist and a journal seeded through
the registered production `IStateStore`. A test-only `IPlaylistManager` proxy delegates
all ordinary calls to the native manager. For one armed, validated fixture playlist,
the production recovery call clears items through that manager and the proxy throws
`IOException` before adding. The harness verifies production's persisted failure
status, the empty native list, both surviving a host restart, and the normal recovery
method restoring the exact intended order. It also checks recipe idempotence,
pending-operation cleanup and denial to a second ordinary user.

This explicitly injects the initial journal and native failure boundary. It does not
simulate Last.fm recipe resolution or claim an unprompted scheduler recovery: after
restart the fixture invokes the actual production `RefreshDueAsync` method. The
recipe has `DailyRefresh=false`, so recovery needs no Last.fm account or traffic.

Library matching runs the production `IMusicLibrary.Match` and
`IDiscoveryLibrary.MatchEntity` against a third scanned library, `/media/Match/`, whose
tagged tracks sit in artist and album folders. `native_library.py` holds the recorded
answers: names with diacritics and a slash, letter case, an album that narrows a
track, two tracks with one title, and queries that must stay unmatched. One table
serves both servers, so a server release that names or finds items differently fails
the run and names the query. The matching needs no Last.fm account or traffic.

The parental-limit check uses the generated private playlist after its recovery. A
limit that allows the tracks changes nothing for the owner. With unrated music also
blocked, the owner can neither read nor match the tracks on any server; Jellyfin 12.1
and later additionally hide the playlist itself, while 12.0 shows it empty. The check
expects each behaviour from the release the server reported. The plugin keeps the
recipe throughout, a second ordinary user never sees playlist or recipe, and lifting
the limit returns the playlist unchanged. The 12.1 boundary comes from the server
source at tag `v12.1`; the pinned 12.0 and 12.2 servers are the ones observed.

Build this project explicitly with the repository SDK and locked NuGet references.
Only `JellySin.NativeProbe.dll` belongs in the fixture mount; the production DLL is
mounted separately. Host assemblies are never copied into either plugin directory.

```sh
dotnet restore tests/integration/JellySin.NativeProbe/JellySin.NativeProbe.csproj --locked-mode
dotnet build tests/integration/JellySin.NativeProbe/JellySin.NativeProbe.csproj -c Release --no-restore
python tests/integration/native_userdata.py \
  --plugin src/JellySin.Plugin.Lastfm/bin/Release/net10.0/JellySin.Plugin.Lastfm.dll \
  --probe tests/integration/JellySin.NativeProbe/bin/Release/net10.0/JellySin.NativeProbe.dll \
  --server floor
```

`--server` takes `floor` (the oldest supported Jellyfin) or `newest`, the two images
pinned in `tools/smoke.py`; without it the newest runs. CI runs both. The report
records the image and the release the server reported.

The host and generated volumes are removed after success or failure. Diagnostic
reports contain only synthetic userdata values, plugin identities and check results.
No existing smoke-state file or real account is read. Production code should be
built before invoking the commands above.

Jellyfin isolates each plugin's assembly context. The fixture therefore references
only the public host contracts at compile time. For the reviewed production
operations, it resolves the already-registered service type from the injected native
userdata decorator and invokes fixed method names. It never creates a replacement
decorator or loads another copy of the production assembly.
