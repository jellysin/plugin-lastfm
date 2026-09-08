# Security policy

Report credential exposure, cross-user data access, unsafe account actions or
release-integrity issues with this repository's GitHub **Report a vulnerability**
action. Include versions, a minimal reproduction and impact; omit actual session
keys, passwords and private library contents.

The current stable JellySin Last.fm release on Jellyfin 12 receives fixes.
Published release bytes are immutable. Fixes use new versions and catalog entries.

Each dashboard session has Jellyfin's existing user permissions. Last.fm account
keys stay in protected server-side storage and are never returned to the browser.
Server administrators control the host, files and protection key ring and remain
trusted. A distributed Last.fm application secret is extractable from the plugin;
individual user sessions have separate credentials and must remain private.

Use HTTPS when exposing Jellyfin over a network. Disconnecting removes local
account state; revoke the application in Last.fm settings to remove its upstream
authorization. See docs/operations.md for storage and recovery details.
