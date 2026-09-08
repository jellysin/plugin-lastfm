# Native userdata integration fixture

`JellySin.NativeProbe` is a separate test-only plugin, with its own GUID and assembly.
It must never be copied into a production package or a host containing real accounts.
Its endpoints require the host environment opt-in `JELLYSIN_NATIVE_PROBE=1`, a real
administrator session, a generated `JellySin Probe ` user and an audio fixture under
`/media/Public/`. The probe has no Last.fm client or account operations.

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

Build this project explicitly with the repository SDK and locked NuGet references.
Only `JellySin.NativeProbe.dll` belongs in the fixture mount; the production DLL is
mounted separately. Host assemblies are never copied into either plugin directory.

```sh
dotnet restore tests/integration/JellySin.NativeProbe/JellySin.NativeProbe.csproj --locked-mode
dotnet build tests/integration/JellySin.NativeProbe/JellySin.NativeProbe.csproj -c Release --no-restore
python tests/integration/native_userdata.py \
  --plugin src/JellySin.Plugin.Lastfm/bin/Release/net10.0/JellySin.Plugin.Lastfm.dll \
  --probe tests/integration/JellySin.NativeProbe/bin/Release/net10.0/JellySin.NativeProbe.dll
```

The host and generated volumes are removed after success or failure. Diagnostic
reports contain only synthetic userdata values, plugin identities and check results.
No existing smoke-state file or real account is read. Production code should be
built before invoking the commands above.

Jellyfin isolates each plugin's assembly context. The fixture therefore references
only the public host contracts at compile time. For the three reviewed production
operations, it resolves the already-registered service type from the injected native
userdata decorator and invokes fixed method names. It never creates a replacement
decorator or loads another copy of the production assembly.
