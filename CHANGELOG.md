# Changelog

## [0.2.0](https://github.com/jellysin/plugin-lastfm/compare/v0.1.0...v0.2.0) (2026-10-17)


### Features

* **favourites:** log one line per finished favourites run ([#136](https://github.com/jellysin/plugin-lastfm/issues/136)) ([4d3f110](https://github.com/jellysin/plugin-lastfm/commit/4d3f11032387b6dadcd641294ffd71a1839e3dde))
* **favourites:** match pending loved tracks after a library scan ([#124](https://github.com/jellysin/plugin-lastfm/issues/124)) ([55252e6](https://github.com/jellysin/plugin-lastfm/commit/55252e6fb6d247aaf433998c73da0532c4139808))
* **favourites:** reuse loved-track matches by MusicBrainz id ([#117](https://github.com/jellysin/plugin-lastfm/issues/117)) ([78d836e](https://github.com/jellysin/plugin-lastfm/commit/78d836e4b538d3158c06b156105a79d0e00fb125))
* let administrators choose the media types to scrobble ([#119](https://github.com/jellysin/plugin-lastfm/issues/119)) ([28c1ce9](https://github.com/jellysin/plugin-lastfm/commit/28c1ce9a7bbd24674a3d3c934414359fa8659f03))
* let users choose which devices and media types scrobble ([#123](https://github.com/jellysin/plugin-lastfm/issues/123)) ([d0cbb55](https://github.com/jellysin/plugin-lastfm/commit/d0cbb55be88c0a221ae7dd05ae0729bbe4f2fb35))
* **metadata:** add Last.fm artist and album metadata providers ([#130](https://github.com/jellysin/plugin-lastfm/issues/130)) ([f1b681e](https://github.com/jellysin/plugin-lastfm/commit/f1b681e231bb62653782ce8febf9c46c13a0043c))
* **metadata:** link artists and albums to their Last.fm pages ([#133](https://github.com/jellysin/plugin-lastfm/issues/133)) ([9ba4e88](https://github.com/jellysin/plugin-lastfm/commit/9ba4e8875174248a5edcde5f1e772509c619a425))
* **playback:** scrobble plays of clients that report only start and stop ([#131](https://github.com/jellysin/plugin-lastfm/issues/131)) ([9634c8d](https://github.com/jellysin/plugin-lastfm/commit/9634c8dad9b5eaf562b4d6a9ad1ccd6588442305))
* run favourites, playlists and delivery retry as scheduled tasks ([#118](https://github.com/jellysin/plugin-lastfm/issues/118)) ([8ad6b46](https://github.com/jellysin/plugin-lastfm/commit/8ad6b46e38f350062223195be1cafb225842b5c9))
* **scrobble:** let admins and users choose which libraries scrobble ([#116](https://github.com/jellysin/plugin-lastfm/issues/116)) ([fa8c591](https://github.com/jellysin/plugin-lastfm/commit/fa8c591bd2f4a249cf787682c7b4bac377dd0bfd))


### Bug Fixes

* **agents:** give the audit and gate personas their model tiers ([#111](https://github.com/jellysin/plugin-lastfm/issues/111)) ([21f2ac7](https://github.com/jellysin/plugin-lastfm/commit/21f2ac7edf00b03ec132a0130caee0f5c1fb8bc3))
* **api:** answer a missing Last.fm application with its own status ([#137](https://github.com/jellysin/plugin-lastfm/issues/137)) ([dfc3c66](https://github.com/jellysin/plugin-lastfm/commit/dfc3c66be8418619b478070ce64647723421041b)), closes [#99](https://github.com/jellysin/plugin-lastfm/issues/99)
* **playback:** never scrobble audiobook chapters ([#114](https://github.com/jellysin/plugin-lastfm/issues/114)) ([8a88694](https://github.com/jellysin/plugin-lastfm/commit/8a88694468ee23a21e70d4da7bcd7742cbf1b886))
* **release:** tag the release commit when the draft is created ([#113](https://github.com/jellysin/plugin-lastfm/issues/113)) ([33443a9](https://github.com/jellysin/plugin-lastfm/commit/33443a9f0bd5262e4e6e509b598a2acfcb56a55b))
* **tasks:** re-arm the interval after a task's first run ([#126](https://github.com/jellysin/plugin-lastfm/issues/126)) ([3e9b3d6](https://github.com/jellysin/plugin-lastfm/commit/3e9b3d66f43be708b5353346c55009508045f77b))

## 0.1.0 (2026-10-16)


### Features

* connect Last.fm with one button and a return to the server ([#52](https://github.com/jellysin/plugin-lastfm/issues/52)) ([c2ac064](https://github.com/jellysin/plugin-lastfm/commit/c2ac06463c6dc0c1cc98cbdc936f10c8a5194946))
* count how page 1 differs when the favourite synchronization pauses on it ([#77](https://github.com/jellysin/plugin-lastfm/issues/77)) ([f387249](https://github.com/jellysin/plugin-lastfm/commit/f387249f81d4f602518c131dd5ddd7f8481b5782))
* introduce JellySin Last.fm for Jellyfin 12 ([#2](https://github.com/jellysin/plugin-lastfm/issues/2)) ([0ab3b9e](https://github.com/jellysin/plugin-lastfm/commit/0ab3b9e19ff9fe09f12195c4aaa918377ff36ab4))
* log one line per submitted batch of plays and per playing-now update ([#71](https://github.com/jellysin/plugin-lastfm/issues/71)) ([22a7a2e](https://github.com/jellysin/plugin-lastfm/commit/22a7a2e18852f2d0c7947454d2adafbba07bfff6)), closes [#67](https://github.com/jellysin/plugin-lastfm/issues/67)
* log which check paused the favourite synchronization ([#73](https://github.com/jellysin/plugin-lastfm/issues/73)) ([d8e7364](https://github.com/jellysin/plugin-lastfm/commit/d8e7364f6a03dabb8b935dfdf3eb11fed9fa975d))
* remove the native metadata and similarity providers ([#60](https://github.com/jellysin/plugin-lastfm/issues/60)) ([7ca8ea6](https://github.com/jellysin/plugin-lastfm/commit/7ca8ea6c81f67065211d467effe9b161ad7839eb))


### Bug Fixes

* accept writes from the published address behind a TLS proxy ([#56](https://github.com/jellysin/plugin-lastfm/issues/56)) ([5dd33c7](https://github.com/jellysin/plugin-lastfm/commit/5dd33c705d12a44103a944061bf78e9f4ff4e901))
* build the sign-in return from the published server address ([#53](https://github.com/jellysin/plugin-lastfm/issues/53)) ([aede7e8](https://github.com/jellysin/plugin-lastfm/commit/aede7e8a782386c25290f7898b7f9693e4eeead9))
* **ci:** fail the secret-scan self-test clearly when the scanner misses the planted key ([#81](https://github.com/jellysin/plugin-lastfm/issues/81)) ([b2e289d](https://github.com/jellysin/plugin-lastfm/commit/b2e289d160c99cd8013c51ac5c8cc84573eed408))
* name users by their log pseudonym in feature refresh warnings ([#74](https://github.com/jellysin/plugin-lastfm/issues/74)) ([044ff89](https://github.com/jellysin/plugin-lastfm/commit/044ff89a4b1a0760fb4f4ad9824d432459569561))
* offer only Connect until a Last.fm account is connected ([#61](https://github.com/jellysin/plugin-lastfm/issues/61)) ([77870ea](https://github.com/jellysin/plugin-lastfm/commit/77870ea537ca7bf4b6ec348413e011faf91d97ae))
* pause listening when the legacy Last.fm plugin runs under the Id its assembly declares ([#95](https://github.com/jellysin/plugin-lastfm/issues/95)) ([59c6eeb](https://github.com/jellysin/plugin-lastfm/commit/59c6eeb35002abb07ff152401f9495252df9af12))
* reconcile a changed favourite before the run whose window holds it ([#107](https://github.com/jellysin/plugin-lastfm/issues/107)) ([099e44f](https://github.com/jellysin/plugin-lastfm/commit/099e44fae20ed4a9da6354949079079d01e366e3))
* retry a play Last.fm found ahead of its clock by itself ([#100](https://github.com/jellysin/plugin-lastfm/issues/100)) ([13c4cd2](https://github.com/jellysin/plugin-lastfm/commit/13c4cd2eb13d0200b8875ac4e83cd29a24439ead))
* run the favourite reconciliation outside the web request ([#70](https://github.com/jellysin/plugin-lastfm/issues/70)) ([f909c9d](https://github.com/jellysin/plugin-lastfm/commit/f909c9d24fce2d3bdbdd6d98ec3d0d275c8322d7)), closes [#69](https://github.com/jellysin/plugin-lastfm/issues/69)
* send the album artist with scrobbles and now playing ([#101](https://github.com/jellysin/plugin-lastfm/issues/101)) ([2979a9d](https://github.com/jellysin/plugin-lastfm/commit/2979a9d359f19e2c4cf443c0af17494a003d1200))
* start and report an application override the restored server cannot read ([#98](https://github.com/jellysin/plugin-lastfm/issues/98)) ([f7c0bc0](https://github.com/jellysin/plugin-lastfm/commit/f7c0bc044569c9478ce57b69d1be884b663c38e8))
* warn when the oldest held play is seven days old ([#102](https://github.com/jellysin/plugin-lastfm/issues/102)) ([09423ea](https://github.com/jellysin/plugin-lastfm/commit/09423ea46d3ba7b09c4b3a482ff922373cf02fbb))


### Performance Improvements

* reuse the loved collection and library matches between favourite runs ([#105](https://github.com/jellysin/plugin-lastfm/issues/105)) ([b07a4c6](https://github.com/jellysin/plugin-lastfm/commit/b07a4c60dd8d30b429257da7a33554c02661ae2a))
