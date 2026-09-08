# Security

Security fixes are maintained for the Jellyfin 12 and 10.11 plugin branches.
Install current Jellyfin server patches as well as plugin updates.

Report vulnerabilities privately through this repository's GitHub security
advisories. Do not post passwords, session keys, server configuration, or signed
Last.fm requests in issues or logs.

Last.fm passwords are exchanged for session keys and are not saved by the plugin.
Session keys are stored in Jellyfin's plugin configuration. That file is not an
encrypted credential store; protect the server configuration directory and its
backups with appropriate filesystem access controls.

The plugin's bundled Last.fm application credentials identify this client and
are present in distributed binaries. They are not user session credentials.
MD5 is required by Last.fm request signing and Jellyfin catalog verification;
it is not used as a password hash or a claim of cryptographic artifact provenance.
