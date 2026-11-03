# Inside AltSync

This is a partial source snapshot of AltSync 0.2.0. It includes the native code that handles account sessions, network requests, local files, updates and Windows permissions. The files are copied as they are used locally, without removing methods or replacing the implementation with examples.

It is not the complete project and cannot be built on its own. The frontend, artwork, Discord moderation bot and development tools are outside this snapshot. Publishing these files is not an independent security audit and does not prove that a downloaded executable was built from them.

## Where to look

| Area | Files |
| --- | --- |
| Roblox sign-in | [LoginWindow.cs](v0.2.0/LoginWindow.cs), [CookieImportWindow.cs](v0.2.0/CookieImportWindow.cs), [CookieSession.cs](v0.2.0/Core/CookieSession.cs) |
| Session storage | [Vault.cs](v0.2.0/Core/Vault.cs), [Models.cs](v0.2.0/Core/Models.cs) |
| Roblox requests and launch tickets | [RobloxApi.cs](v0.2.0/Core/RobloxApi.cs), [RobloxLaunch.cs](v0.2.0/Core/RobloxLaunch.cs), [LaunchDestination.cs](v0.2.0/Core/LaunchDestination.cs) |
| Games and servers | [GameBrowser.cs](v0.2.0/Core/GameBrowser.cs), [ServerBrowser.cs](v0.2.0/Core/ServerBrowser.cs) |
| Backups and saved destinations | [PortableBackup.cs](v0.2.0/Core/PortableBackup.cs), [BackupWindow.cs](v0.2.0/BackupWindow.cs), [Library.cs](v0.2.0/Core/Library.cs) |
| App-to-interface messages and diagnostics | [MainWindow.xaml.cs](v0.2.0/MainWindow.xaml.cs) |
| Discord presence | [DiscordPresence.cs](v0.2.0/Core/DiscordPresence.cs), [DiscordPipe.cs](v0.2.0/Core/DiscordPipe.cs), [DiscordBranding.cs](v0.2.0/Core/DiscordBranding.cs), [public branding settings](v0.2.0/Branding/discord.json) |
| Windows privileges, processes and windows | [app.manifest](v0.2.0/app.manifest), [MultiInstance.cs](v0.2.0/Core/MultiInstance.cs), [ProcessOwner.cs](v0.2.0/Core/ProcessOwner.cs), [SessionMonitor.cs](v0.2.0/Core/SessionMonitor.cs), [WindowLayouts.cs](v0.2.0/Core/WindowLayouts.cs) |
| Automatic updates and startup | [AutoUpdater.cs](v0.2.0/Core/AutoUpdater.cs), [UpdatePackage.cs](v0.2.0/Core/UpdatePackage.cs), [install.ps1](v0.2.0/Updater/install.ps1), [App.xaml.cs](v0.2.0/App.xaml.cs) |
| Dependencies and release packaging | [AltSync.csproj](v0.2.0/AltSync.csproj), [build.ps1](v0.2.0/scripts/build.ps1), [release privacy check](v0.2.0/scripts/audit-release-privacy.cjs) |

## Account data

Sign-in opens Roblox in a separate WebView2 profile. Once Roblox creates a session, AltSync reads its `.ROBLOSECURITY` cookie from that profile and validates it with Roblox. The sign-in window attempts to clear its browsing data when it closes. Cookie import is a separate, explicit action in a native password field.

Saved cookie values are protected with Windows DPAPI for the current Windows user. Other account fields, including usernames, IDs and avatar URLs, are readable JSON in `%LOCALAPPDATA%\AltSync\accounts.json`. Saved destinations and profiles are stored separately in `library.json`; these can include private server links. Those local files should not be shared.

DPAPI protects saved values at rest. Software running as the same Windows user can potentially decrypt them. During account validation, launch and backup operations, AltSync needs session values in memory. Cookies are excluded from the objects sent to the app's HTML interface.

Portable backups use AES-256-GCM and PBKDF2-SHA256 with 600,000 iterations. A backup that includes accounts contains their sessions inside the encrypted payload. Keep the password and backup private.

## Network and Windows access

The published Roblox client scopes account cookies to `.roblox.com` and disables automatic HTTP redirects. Game discovery uses a separate client without cookies. Account and launch requests go to Roblox; the updater checks the official AltSync GitHub releases and verifies the archive's SHA-256 digest before staging it.

Rich Presence communicates with the local Discord client over IPC. The branding file contains the public Application ID, image and button links, not a bot token. The sign-in page and remote images also use WebView2 and their providers' infrastructure.

AltSync requests administrator privileges for its multi-instance feature. The handle code matches the Roblox singleton event and limits its targets to Roblox processes belonging to the current Windows user and desktop session. Process tracking and window management are included so those operations can be reviewed too.

## Snapshot and download

[manifest.json](v0.2.0/manifest.json) lists the original paths, file sizes and SHA-256 hashes of the copied sources. These hashes let you compare copies of the published files; they do not establish source-to-binary equivalence.

The [0.2.0 release](https://github.com/PingusWasHere/AltSync/releases/tag/v0.2.0) includes the application ZIP and `SHA256SUMS.txt`. If something in the code needs explaining, open an issue or a support ticket. Never include your session cookie or account files.
