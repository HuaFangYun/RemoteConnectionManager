# Security policy

Remote Connection Manager handles sign-in details for remote servers, so security reports are
taken seriously and are welcome.

## Supported versions

The project is in early development (0.x). Only the latest code on `main` and the most recent
release get security fixes.

| Version | Supported |
|---|---|
| Latest release / `main` | Yes |
| Anything older | No |

## Reporting a vulnerability

**Please don't open a public issue for a security problem.**

Report it privately through GitHub instead:

1. Go to the [Security tab](https://github.com/jamtheradar/RemoteConnectionManager/security) of this repo.
2. Select **Report a vulnerability**.
3. Include:
   - what the problem is and what it could let someone do
   - steps to reproduce it, or a proof of concept
   - the version or commit you tested
   - your Windows version, if it matters

You should get a reply within 7 days. Once the problem is confirmed, a fix will be worked on and
you'll be kept up to date. You'll be credited in the release notes unless you'd rather not be.

## What's in scope

Things we especially want to hear about:

- passwords or secrets being written to disk, logs, the connections file or the database
- session log masking missing a password prompt or secret
- secrets from Azure Key Vault or 1Password being exposed, cached or sent to the wrong host
- the RDP credential protection settings (Restricted Admin, Remote Credential Guard, RD Gateway)
  not being applied as configured
- a crafted connections file, import file (mRemoteNG, Devolutions RDM, Royal TS) or plugin
  running code or reading files it shouldn't
- SQL injection or permission problems in the SQL Server storage backend

## What's out of scope

- problems in Windows, the Microsoft RDP client, OpenSSH, Azure or 1Password themselves
  (please report those to the vendor)
- attacks that need someone already signed in as you on your PC, or with admin rights on it
- connections files, logs or settings shared on purpose through a synced folder

## How secrets are handled

By design, the app doesn't store passwords. RDP passwords are typed into the Windows prompt,
or read from Key Vault or 1Password when you connect, and are not saved by the app. Session logs
are stored under `%LOCALAPPDATA%\RemoteConnectionManager\logs` and mask secrets on a best-effort
basis. If you find a case where they don't, please report it.
