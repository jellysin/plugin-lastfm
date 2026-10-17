# Integration checks on real Jellyfin servers

## Native userdata fixture

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
simulate Last.fm recipe resolution: after the first restart the fixture invokes the
actual production `RefreshDueAsync` method. The recipe has `DailyRefresh=false`, so
recovery needs no Last.fm traffic.

A second fixture user then shows the recovery without that call. The plugin's scheduler
refreshes only connected accounts, and connecting a first account clears the user's
stored state, so this user connects first, through the Last.fm stand-in of the page
check with a made-up application. Its write is interrupted at the same native fault and
the host restarts. From then on the harness only reads: the plugin's own scheduler,
which runs at startup and then every five minutes, must restore the exact intended
order and remove the pending-operation record within one interval and a minute.

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
source at tag `v12.1` and is observed on the pinned 12.0, 12.1 and 12.2 servers.

A second user, connected through the Last.fm stand-in, refreshes its generated
playlist while the same limit blocks every track. The refresh has to read the
stand-in's top tracks and answer HTTP 409 without a pending write, and the playlist
and recipe have to be unchanged once the limit is lifted. Without the limit, the same
refresh has to rewrite the playlist, so the refusal is shown to come from the block.

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

`--server` takes `floor` (the oldest supported Jellyfin), `newest` or `12.1`, the
images pinned in `tools/smoke.py`; without it the newest runs. CI runs all three. The
report records the image and the release the server reported.

The host and generated volumes are removed after success or failure. Diagnostic
reports contain only synthetic userdata values, plugin identities and check results.
No existing smoke-state file or real account is read. Production code should be
built before invoking the commands above.

Jellyfin isolates each plugin's assembly context. The fixture therefore references
only the public host contracts at compile time. For the reviewed production
operations, it resolves the already-registered service type from the injected native
userdata decorator and invokes fixed method names. It never creates a replacement
decorator or loads another copy of the production assembly.

## Sign-in behind a proxy that ends TLS

`proxied_sign_in.py` starts the pinned server with a published address
(`https://jellyfin.test`) and no known proxy, and puts a proxy in front of it that
ends TLS and passes every request on as plain HTTP with the host name unchanged.
The server therefore sees `http` where the browser names `https`. A headless
Chromium then uses the plugin the way a person does:

- at the published address: sign in on the dashboard and select **Connect
  Last.fm**, save a preference, ask for a Quick Connect code, and select the
  button on the plugin's settings page in the server's own web interface. The
  browser must arrive at `https://www.last.fm/api/auth/` with a return address
  under the published address and hold the host-only secure cookie of the attempt.
  The dashboard offers its preferences to a connected account only, and the run
  connects none, so the script shows that section itself before it selects
  **Save preference**;
- at a second address, the server's own HTTP port: the dashboard and the settings
  page must stay where they are, name both addresses to an administrator and send
  an ordinary user to the administrator.

Last.fm is never contacted: the browser's request for the approval page is
answered by a stub, and a build without the project application gets a random
application for the run.

The server log is part of the result. `EXPECTED` in the script names the lines
each scenario leaves, and the run fails on a missing line and on a line too many.
After the browser the script sends one write from a foreign origin and one note
with a made-up reason: the first must leave exactly one warning with its reason,
the second none. No line may carry an address, an account id, a password or a
token; users appear as the start of a hash.

```sh
npx --no-install playwright install chromium
python tests/integration/proxied_sign_in.py \
  --plugin src/JellySin.Plugin.Lastfm/bin/Release/net10.0/JellySin.Plugin.Lastfm.dll \
  --server newest --report build/proxied-sign-in
```

It needs Docker, Node with the repository's locked packages and `openssl` for the
certificate of the run. The report directory receives `result.json`, the writes
the proxy passed on with their status, the plugin's log lines and a screenshot
of every failed scenario. `--only <text>` runs the scenarios whose name contains
the text. The first run of this check, against a build that compared the scheme
of the page with the scheme the server saw, failed all scenarios at the
published address with HTTP 403; that defect is what the check guards.

## Plugin pages with a Last.fm stand-in

`page_check.py` opens the plugin's pages in a headless Chromium against the pinned
server, with a stand-in for Last.fm, and keeps a screenshot of each page:

- the administrator saves the plugin settings and a made-up application on the
  settings page in the server's own web interface, and the account section must
  follow the new application;
- an ordinary user signs in on the dashboard and connects; the browser's request
  for Last.fm's approval page is answered with an approval for a fixed token;
- the same user loads charts and recent listens and starts discovery from a
  library track, which must name one similar track in the library and one beyond it.

After the pages pass, the same user runs three flows through the API, without the
browser:

- playback: the user plays the seed track for 36 seconds and reports progress every
  five seconds. The stand-in must record a now-playing call and a scrobble that name
  the track. Delivery must then report nothing pending, rejected, dropped or failed.
- history import: the preview must propose, for the seed and the similar track, the
  larger of the server's play count and the Last.fm play count. After the import,
  both tracks are applied, the server's own play counts equal those numbers, and the
  similar track has a last-played date.
- favourite sync: the user marks the seed track as a favourite on the server and
  turns the sync on. The stand-in must record a love call for the seed track. The
  similar track, which Last.fm lists as loved, must become a favourite on the server.

The server runs on a Docker network made for the run and reaches Last.fm only
through `lastfm_stand_in.py`, a proxy in its own container (a pinned Python image)
on that network. The proxy opens a tunnel to `ws.audioscrobbler.com` alone, ends
its TLS with a certificate from a certificate authority made for the run, and
answers the documented methods with fixture names; Jellyfin's own hosts are
refused and recorded. No port opens on the workstation besides the server's
loopback port, and no real Last.fm account, application or credential is used.
The run fails when a page check fails, when the server never asks the stand-in
for a session, when it rejects the stand-in's certificate, when a listener flow
fails, and when the server log contains the session key the stand-in issued. For
track methods the stand-in records the method with the artist and title, never the
key, session or signature.

```sh
npx --no-install playwright install chromium
python tests/integration/page_check.py \
  --plugin src/JellySin.Plugin.Lastfm/bin/Release/net10.0/JellySin.Plugin.Lastfm.dll \
  --server newest --evidence .workingdir/evidence/page-check
```

It needs Docker, Node with the repository's locked packages and `openssl`. The
evidence directory receives the screenshots, `result.json`, the server log, the
methods the stand-in answered and its tunnel record; keep it out of the
repository. CI runs this check on the newest Jellyfin 12.

## Legacy Last.fm plugin next to JellySin

`tests/integration/legacy_plugin.py` checks that JellySin pauses listening while the
previous Last.fm plugin is installed, so that the two plugins never send the same play
twice. It downloads that plugin's last release (jesseward/jellyfin-plugin-lastfm
10.0.0.0) into a temporary folder, refuses it unless its SHA-256 matches the pinned
value, and mounts it as an installed plugin on a disposable Jellyfin 12 host next to the
production plugin. It fails unless the server lists the legacy plugin as active and an
ordinary user's delivery status reports it:

```sh
python tests/integration/legacy_plugin.py --plugin src/JellySin.Plugin.Lastfm/bin/Release/net10.0/JellySin.Plugin.Lastfm.dll --server newest
```

A running server lists the legacy plugin under the Id its assembly declares,
`de7fe7f0-b048-439e-a431-b1a7e99c930d`, not under the GUID of its catalog entry,
`5e7fe7f0-b048-429e-a431-b1a7e69c930d`; JellySin checks both. The legacy repository is
archived and names no licence, so the release is only downloaded for this check and
never kept, packaged or committed. For the same reason the check stays manual: CI never
downloads or runs the legacy release. In CI, a unit test guards that both Ids are
recognized.

## Account carried across an upgrade and a restore

`state_carryover.py` connects an ordinary user through the Last.fm stand-in of the
page check on the oldest supported Jellyfin 12 and has a library favourite loved.
The same configuration then goes through three steps, and each one must keep what
`docs/operations.md` promises:

- upgrade: the newest Jellyfin 12 starts on the same volume. The user is still
  connected and a second favourite is loved, which needs the stored session key.
- restore: the configuration is copied into a fresh volume and the newest release
  starts on it with an empty cache. The user is still connected and a third
  favourite is loved.
- restore without the key ring: the same copy without `jellysin-lastfm/keys`. The
  server starts, the administration page reports the application override as
  unreadable, the account asks for a new connection, and a synchronization is
  refused without any call to the stand-in.

```sh
python tests/integration/state_carryover.py \
  --plugin src/JellySin.Plugin.Lastfm/bin/Release/net10.0/JellySin.Plugin.Lastfm.dll
```

It needs Docker and `openssl`, and removes its servers, volumes and the stand-in
afterwards. Against a build before the unreadable-override handling, the server
without its key ring did not start: reading the override threw while Jellyfin
loaded the plugin. That defect is what the last step guards. An upgrade from a
published plugin release is not part of the check, since no release has been
published yet. The check stays manual.
