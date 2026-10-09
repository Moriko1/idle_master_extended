# Idle Master Extended

A small Windows desktop app for periodically idling Steam Trading Cards.

This is [Moriko1's fork](https://github.com/Moriko1/idle_master_extended) of
[jonas-med-ett-s/idle_master_extended](https://github.com/jonas-med-ett-s/idle_master_extended),
originally created by [jshackles](https://github.com/jshackles/idle_master).
It keeps the existing WinForms interface, idling modes, filters, themes and translations.

## Download and run

1. Download the **win-x64 ZIP** from [Releases](https://github.com/Moriko1/idle_master_extended/releases).
2. Extract every file to a folder, then open **IdleMasterExtended.exe**.
3. Keep Steam open and signed in. Select **Sign in** and complete Steam's official login page.
4. Wait for the card scan, then press **Start**. Scanning and signing in do not start idling.
5. **Pause** temporarily stops helpers; **Resume** requires a manual click. **Stop** ends the session. Stop and completion show a summary inside Idle Master without activating or restoring its window.

Requirements: Windows 10/11 x64, .NET Framework 4.8, Steam, and Microsoft WebView2 Evergreen Runtime.
The ZIP contains the app and its libraries; it uses the installed browser runtime.
If that runtime is missing, the app links to [Microsoft's official WebView2 installer](https://developer.microsoft.com/microsoft-edge/webview2/).

Steam handles QR sign-in, passwords, Steam Guard and account challenges. The app uses its own
browser profile and does not store passwords or copied cookies in ordinary settings or logs.
Your Community login must match the account in the running Steam client.
**Switch account** and **Sign out** affect this app's session.

## Everyday controls

- **Refresh** checks Steam for available drops; **Retry** repeats a failed check.
- Temporary network errors and unreadable card pages preserve the last successful counts. Running helpers continue while card checks retry automatically. Steam server reconnects also keep the verified account idling.
- Single, One then many, Many then one, Fast, and Whitelist modes remain available in Settings.
- Whitelist mode has no automatic card completion; stop it manually. Its queue ends if all entries become private. At most 30 games run together.
- Games marked Private on Steam are skipped in every mode, including Whitelist, because [Steam does not award them card drops](https://help.steampowered.com/en/faqs/view/1150-C06F-4D62-4966). The app verifies the account's private list before Start/Resume and on periodic queue refreshes. A failed check preserves the previous snapshot and retries; it cannot prove completion.
- Show Steam username defaults to on. Saved preferences survive restarts and portable-folder upgrades.
- **Start** stays disabled when there are no eligible games.
- Settings changes apply only on **Save**. **Cancel** discards edits and leaves idling alone. During a session, appearance and sleep settings apply immediately; mode and filter changes apply to the next session.
- Closing the app stops its helpers. Sleep prevention applies only while idling.
- Shutdown after completion remains an optional one-run setting, disabled by default. Its completion summary offers a shutdown button; completion does not open a system prompt.

The browser session and diagnostic logs are stored under
`%LocalAppData%\IdleMasterExtended\Moriko1`.
Ordinary preferences use `preferences.xml` in that same folder, with atomic saves and a backup; cookies remain in the browser profile. Existing ordinary settings are migrated when available.
Logs contain operation names and exception types/stacks, not cookies, passwords or page contents.
Upgrading from upstream requires one fresh Steam sign-in; copied-cookie login is retired.

## Building and verification

See [build instructions](docs/BUILD.md) and [prerelease validation results](docs/VALIDATION.md).
Run `python scripts/build.py` from a Windows checkout with Visual Studio MSBuild.
The script restores build dependencies, builds both x64 .NET Framework 4.8 applications and runs
the regression suites. It can also produce a portable ZIP, matching source ZIP and SHA-256 checksums.

The first fork download is a **prerelease**. Automated tests cover synthetic Steam responses,
card parsing, failures, cancellation, helper supervision, account checks and Settings Save/Cancel.
Those tests do not prove successful sign-in or real card drops for your account.
Live Steam sign-in, restart persistence and card-drop checks require user-operated validation.

## Credits and license

Idle Master was created by jshackles, based on Stumpokapow's original work.
Idle Master Extended was maintained by Jonas Nilson (jonas-med-ett-s).
This fork preserves their work and the [GNU GPL v2 license](LICENSE).
Dependencies include Steamworks.NET, Html Agility Pack and Microsoft WebView2;
see [third-party notices](docs/THIRD_PARTY.md).

Both source and executable downloads are published together, with checksums.
