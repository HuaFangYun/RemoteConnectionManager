# Changelog

All notable changes to Remote Connection Manager are listed here. Each release on the
[Releases](https://github.com/jamtheradar/RemoteConnectionManager/releases) page has an installer.

## [Unreleased]

## [0.2.0] - 2026-11-06

### Added
- Password generator. Open it from the command bar (key icon) or with Ctrl+Shift+G, and it copies the password to the clipboard.
  - Types: random, memorable (words) and PIN, with a length slider.
  - Random passwords can include numbers and symbols, and always have at least one of each type chosen.
  - Memorable passwords can use a separator, capitals and a number.
  - A bar shows how strong the password is.
  - Passwords come from the Windows crypto random number source.
  - The clipboard copy stays out of clipboard history and cloud clipboard, and is cleared after 30 seconds.
  - Your last choices are saved. Generated passwords are never saved.
- Generate button in the Key Vault item editor. It puts the password straight into the Value field.
- "Ignore certificate errors" RDP option. It connects without the "identity of the remote computer cannot be verified" prompt.
  - Set it on a connection, a folder or as a document default. Connections and subfolders inherit it.
  - It is shown on the dashboard with the other RDP settings.
  - The mRemoteNG import brings it across from `RDPAuthenticationLevel="NoAuth"`.

### Fixed
- Text boxes across the app had double padding, which pushed text off centre in search and filter boxes.
- The Key Vault tab no longer shows "Nothing matches the filter" over the list while items are loading or showing.

## [0.1.0] - 2026-11-06

The first release.

### Added
- **Connections**
  - Folders with settings inherited from parent folders and the document defaults.
  - A navigation tree with a filter, and inherited icon colours.
  - Quick connect from the status bar.
- **Import** from mRemoteNG (`confCons.xml`), Devolutions RDM (`.rdm` or RDP `.csv`) and Royal TS (`.rtsz`). Passwords are not imported.
- **Storage**
  - A local JSON file that reloads when it changes on disk.
  - Or a shared SQL Server or Azure SQL database, signed in with Windows or Microsoft Entra.
  - In the database, permissions are managed in RCM and enforced by the database. Preferences are kept per person.
  - Import and export between the two in JSON.
- **Remote Desktop**
  - Sessions in tabs that resize with the window, with keyboard pass through and clipboard.
  - Passwords from Azure Key Vault or 1Password, read when the session connects. They are never saved.
  - RD Gateway, Restricted Admin and Remote Credential Guard.
  - Move a session to its own window, or to full screen on any display.
- **Terminals**
  - PowerShell 7, Windows PowerShell and cmd tabs, with start scripts. Ctrl+T opens a quick shell.
  - SSH through Windows' `ssh.exe`, with an option for older algorithms.
  - A built in telnet client.
  - Send password and Send user name from Key Vault or 1Password.
- **Websites** open in the default browser or a WebView2 tab.
- **Session logs** for PowerShell, cmd, SSH and telnet tabs.
  - Plain text, on by default and kept for 90 days.
  - Passwords, secrets and keys that RCM recognises are masked.
- **Azure Key Vault browser**
  - Signs in through the Windows account manager.
  - Browse vaults or pin them to the tree.
  - Manage secrets, keys and certificates.
  - Copied values are cleared after 30 seconds.
- **Plugins**
  - Key Vault, 1Password and Server checks, each switched on or off in Settings.
  - Server checks show services and hardware stats for a host. They read only and never change the host.
- **Installer**
  - Installs for the current user or for all users.
  - Needs the .NET 10 Desktop Runtime (x64).

[Unreleased]: https://github.com/jamtheradar/RemoteConnectionManager/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/jamtheradar/RemoteConnectionManager/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/jamtheradar/RemoteConnectionManager/releases/tag/v0.1.0
