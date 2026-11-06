<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo/rcm-logo-dark.svg">
    <img alt="Remote Connection Manager" src="assets/logo/rcm-logo.svg" width="420">
  </picture>
</p>

# RemoteConnectionManager

A Windows remote connection manager for admin work from a privileged access workstation.
Folders of RDP connections and local shells in a plain JSON file, sessions in tabs that resize
with the window, no passwords stored, and an Azure Key Vault browser.

Built with .NET 10 and WPF. The RDP surface is the Microsoft RDP ActiveX hosted through the
[Devolutions MsRdpEx](https://github.com/Devolutions/MsRdpEx) interop package. Terminals use the
Windows Terminal renderer (`Microsoft.Terminal.Wpf`) over ConPTY.

## Status

Phases one to seven. See `docs/PLAN.md` for the plan, decisions and phases.

| Area | State |
|---|---|
| Connections file, folders, inherited defaults | Working |
| Navigation tree, filter, new / edit / duplicate / delete | Working |
| RDP sessions in tabs, live resize, keyboard pass through, clipboard | Working, needs testing against real hosts |
| Quick connect from the status bar | Working |
| Import from mRemoteNG `confCons.xml` (no passwords) | Working, unit tested only |
| Import from Devolutions RDM: `.rdm` XML or RDP `.csv` export (no passwords) | CSV checked against a real export; `.rdm` unit tested only |
| Import from Royal TS `.rtsz` document (no passwords) | Working, checked against a real document |
| Reload when the file changes on disk | Working |
| Terminals (pwsh, Windows PowerShell, cmd) in tabs, start scripts, Ctrl+T quick shell | Working |
| Azure Key Vault: sign in through Windows, browse vaults, pin to tree | Built, needs testing against a real tenant |
| Secrets, keys, certificates: list, expiry, copy with clipboard auto clear, create, edit, delete | Built, needs testing against a real tenant |
| RDP password from a Key Vault secret, read at connect time | Built, needs testing against a real tenant and host |
| RD Gateway, Restricted Admin, Remote Credential Guard | Built, needs testing against real hosts |
| Session in its own window, full screen on a chosen display | Working (moves tested with a disconnected session) |
| Signed builds | `build/publish.ps1` signs when given a certificate |
| Plugins (built in, switch on or off in Settings) | Working |
| RDP password from a 1Password item, unlocked in the 1Password app | Built, unit tested; needs the 1Password CLI to try for real |
| Check services and hardware stats for a host (WinRM, then DCOM), read only | Working, checked against this PC over DCOM |
| SSH to switches, routers and Linux (Windows ssh.exe in a terminal tab), old algorithm switch | Working, checked locally; needs a real switch |
| Telnet (built in client, no Windows feature needed) | Working, checked against a fake device |
| Send password / Send user name from Key Vault or 1Password into SSH and telnet | Built, needs a real vault |
| Websites: default browser or a WebView2 tab, chosen per entry, right click for the other | Working |
| Session logs for PowerShell, cmd, SSH and telnet tabs (plain text, secrets masked, on by default) | Working |

## Build and run

Requires the .NET 10 SDK on Windows, x64.

```
dotnet build RemoteConnectionManager.slnx
dotnet test  RemoteConnectionManager.slnx
dotnet run --project src/RemoteConnectionManager.App
```

The connections file is created on first run at
`%APPDATA%\RemoteConnectionManager\connections.json`. Settings lets you point at another
folder, for example one that OneDrive or git syncs. Logs are under
`%LOCALAPPDATA%\RemoteConnectionManager\logs`.

## Connections file

```json
{
  "version": 1,
  "folders": [
    {
      "name": "Client A",
      "defaults": { "domain": "clienta.local", "keyboardPassThrough": true },
      "connections": [
        { "type": "rdp", "name": "DC01", "host": "dc01.clienta.local", "adminSession": true, "tags": ["dc"] },
        { "type": "terminal", "name": "Az pwsh", "shell": "pwsh", "startScript": "Connect-AzAccount" },
        { "type": "keyvault", "name": "Prod vault", "vaultUri": "https://kv-a.vault.azure.net" }
      ]
    }
  ]
}
```

Any setting left out of an entry is inherited from its folders, then from the document
`defaults`, then from built in defaults. A blank user name signs in as the logged on user
through CredSSP. Set a user name and Windows prompts for that account's password at connect
time. Nothing in the file is ever a secret.

### Sign in from Key Vault, gateway and credential protection

An RDP entry, or a folder for everything under it, can take the password from a Key Vault secret:

```json
"defaults": {
  "credential": { "source": "keyVault", "vaultUri": "https://kv-a.vault.azure.net/", "secretName": "clienta-admin" },
  "credentialProtection": "restrictedAdmin",
  "gatewayHost": "rdgw.clienta.com",
  "gatewayUsage": "bypassLocal"
}
```

The secret's value is the password. A `username` tag on the secret names the account
(`CLIENTA\admin` or `admin@clienta.com`); without one the entry's user name and domain are used.
The value is read each time the session connects, handed to the RDP control and dropped. It is
never written anywhere. An entry can set `"credential": { "source": "windows" }` to opt out of a
folder's vault credential.

`credentialProtection` is `none`, `restrictedAdmin` (nothing delegated, needs admin rights on the
host) or `remoteCredentialGuard` (Kerberos requests come back to this PC; signs in as the logged on
user, so it cannot be combined with a vault credential). `gatewayUsage` is `never`, `always` or
`bypassLocal`; a `gatewayHost` on its own means always. The gateway uses the same account as the
session.

### Sign in from 1Password

Pick "1Password item" as the credential and give the item (name, id, or its `op://vault/item`
reference), and optionally the vault and the account:

```json
"credential": { "source": "onePassword", "onePasswordVault": "Servers", "onePasswordItem": "ClientA admin" }
```

At connect time RCM runs the 1Password CLI (`op item get`), which asks the 1Password app to unlock
if it is locked (Windows Hello or your account password). You approve it in 1Password; RCM never
sees your 1Password password. The item's username and password fields are used, and the password is
dropped once it has been handed to the RDP control. Set up once:

1. `winget install AgileBits.1Password.CLI`
2. In the 1Password app: Settings > Developer > turn on "Integrate with 1Password CLI".

If op.exe is somewhere unusual, set its path in Settings.

### Check services and hardware stats

Select an RDP entry and press "Check services" or "Hardware stats" on its dashboard (or right click
it in the tree). A status tab shows:

- the services on the entry's watch list, then any automatic service that is not running. Services
  known to start on demand (Software Protection, BITS, the browser updaters and so on) are hidden
  unless you tick "Show all services";
- with Hardware stats: Windows version, model, CPU, cores, CPU load, memory, each fixed disk and uptime.

The watch list is inherited like other settings, so set it on a folder of SQL boxes once:
`"watchedServices": ["MSSQL*", "SQLSERVERAGENT"]` (`*` and `?` are wildcards). Everything is read
as your logged on account over WinRM, or DCOM when WinRM is not listening. It is read only: nothing
on the host is started, stopped or changed. You need admin rights on the host (or Remote Management
Users for WinRM).

### Windows and full screen

The session toolbar can move a session into its own window (the tab stays and shows where it
went) or full screen on any display. In full screen, push the pointer to the top edge of that
display for a bar with the display list, Leave full screen and Disconnect. Closing a session
window puts the session back in its tab; it does not disconnect.

Terminal entries take a `shell` (`pwsh`, `windowsPowerShell` or `cmd`), an optional
`workingDirectory` (environment variables allowed, blank means your profile folder), extra
`arguments`, and a `startScript` that runs once the shell is up. PowerShell scripts are passed
as an encoded command, so quotes and newlines need no escaping; cmd runs each line in turn.

### SSH, telnet and websites

SSH entries (`"type": "ssh"`) take a `host`, optional `port`, `userName`, `identityFile`,
`jumpHost`, `arguments` and `legacyAlgorithms` (for older switches that only offer SHA-1, CBC or
ssh-rsa). They run Windows' own `ssh.exe` in a terminal tab, so known_hosts, keys and ssh-agent
work as usual. Set `"protocol": "telnet"` for telnet, which RCM handles itself. A folder can
set `sshUserName` and an `sshCredential` for everything in it. With a credential set, the
terminal's Send password button reads it from Key Vault or 1Password and types it at the
prompt. Nothing is saved.

Website entries (`"type": "web"`) take a `url` and `openIn` (`browser` or `tab`). Right click
an entry to open it the other way.

### Session logs

PowerShell, cmd, SSH and telnet tabs are logged to plain text under
`%LOCALAPPDATA%\RemoteConnectionManager\session-logs\yyyy-MM\`, unless an entry, a folder or
Settings switches it off (`"sessionLogging": false`). Passwords, secrets, SNMP communities and
keys that RCM recognises are masked. A secret printed on its own (for example
`Get-AzKeyVaultSecret -AsPlainText`) cannot be recognised, so switch logging off for that work.
Logs older than 90 days are deleted at start up. Settings changes the folder and how long logs
are kept. The red Log dot on a terminal tab opens its log.

## Azure Key Vault

Open the Key Vault browser from the key icon on the command bar (Ctrl+Shift+K). Sign in uses the
Windows account manager, so the account Windows is signed in with is tried first and there is
usually no prompt. The app keeps only the account name and ids between runs, never a token or
a secret.

Vaults you find can be pinned to the tree as `(KV)` entries. If your account has a role on a
vault but cannot read the subscription, type the vault name into "Open by name".

Azure decides what you can do: the app shows what your role or the vault access policy allows,
and explains a denial in the tab rather than hiding the button. Copied secret values stay out of
Windows clipboard history and are cleared after 30 seconds.

## Layout

```
src/RemoteConnectionManager.Core   models, JSON store, inheritance, search, mRemoteNG, RDM and Royal TS import (no UI)
src/RemoteConnectionManager.Rdp    RdpSessionControl: the RDP ActiveX wrapped for hosting in WPF
src/RemoteConnectionManager.KeyVault  Azure sign in (WAM broker), vault discovery, secrets / keys / certificates (no UI); the Key Vault credential plugin
src/RemoteConnectionManager.Plugins   plugin contracts: IRcmPlugin, ICredentialProvider, IServerInspector, the registry and the service filter
src/RemoteConnectionManager.Plugins.OnePassword   1Password credential plugin (drives op.exe)
src/RemoteConnectionManager.Plugins.ServerChecks  services and hardware over CIM (WinRM, then DCOM)
src/RemoteConnectionManager.App    WPF shell: navigation tree, tabs, dashboard, editors, theme, Terminal/ (ConPTY stack copied from Drover)
tests/                             xunit tests for Core, the Key Vault helpers and the plugins
spike/RdpSpike                     phase 0 throwaway window, delete once the app has been tested on a real host
docs/PLAN.md                       the plan
```

## Running on a locked down machine

Defender's attack surface reduction rule "Block executable files from running unless they meet
a prevalence, age, or trusted list criterion" blocks a fresh unsigned build. Ask for an ASR
exclusion for the install folder, or sign the build. Details in `docs/PLAN.md`, section 5.

`build/publish.ps1` publishes a framework dependent x64 build to `artifacts/publish` and, given
`-CertificateThumbprint` or Azure Trusted Signing settings, signs our own files. It ends by listing
anything still unsigned. Today that is only our five files: every third party DLL, including the
RDP interop ones, already carries its publisher's signature.

## Licence

MIT. See `LICENSE`.
