# Prerelease validation: 1.12.0-preview.2

Validation performed on the Windows development host for the corrected Moriko1 prerelease.

## Fix in preview.2

The initial preview attempted to import every browser cookie. The user's login-window error was traced to CookieException during CookieContainer.Add, before any HTTP request. Browser preferences containing commas reproduce this .NET Framework failure. Preview.2 imports only Community authentication/session/parental cookies without transforming token values, and distinguishes invalid authentication from network failure. New regression cases cover comma/semicolon/overlong preferences, exact domains, preservation of authentication values and malformed authentication.

Blank badge-index counts now use an explicit count from the individual card page when available. If both recognized drop sections are empty, a same-account Steam-owned-library read can exclude only games proven absent; unreadable counts for games in that library still fail the atomic scan. The library request includes free and family licenses and disables the unvetted-app filter. Its result header, identifiers and declared count are validated, including the separately flagged family entries. Session tokens stay in memory and are excluded from reports and logs.

## Passed

- Both executables build as x64 Release for .NET Framework 4.8, with no compiler warnings or errors.
- Four regression suites pass: Community reads and badge parsing; run supervision and cancellation; Settings Save/Cancel; session identity verification.
- Synthetic HTTP cases include authenticated empty accounts, expired login, timeouts, HTTP 429, malformed pages, pagination, decimal formats and retention of the previous snapshot.
- Controller cases cover cancellation during launches and fast-mode delays, 30-helper limits, initialization failure, unexpected exit, account mismatch, skipped games on resume, failed scans and completion cleanup.
- A real Windows Job Object test confirms that disposing the owned job stops its child and leaves an unrelated process running.
- Main-window rendering and control geometry pass at synthetic 100%, 125%, 150% and 200% scales, in light and dark themes. Settings controls and long failure messages render visibly. These are rendered forms, not actual monitor DPI changes.
- The desktop application opened and remained running from a portable test folder with C:/Windows as its working directory.
- Git whitespace validation passes.

## Live account evidence

- After the user signed in through the application, the corrected cookie bridge verified the remembered Community account using its authenticated viewer identity. No authentication values or account identifiers were written to the report.
- A fresh opt-in probe verified the remembered account after the previous application and all browser processes for its profile had exited. Opening the probe started no idle helpers.
- Real idling and card drops have not yet been verified.

## Pending user-operated checks

- QR/Steam Guard challenge variations and authenticated badge-scan compatibility across other accounts.
- Real card idling and card drops, including pause/resume, account switching and sign-out during a run.
- Layout on actual monitors at the listed DPI settings.
- Startup from an ACL-enforced read-only application folder. The host denied the attempted ACL change, so this check has not passed. Code stores session data/logs in LocalAppData and launches helpers using absolute paths.

The first download is a prerelease. Synthetic tests are separate from the live account evidence above and do not verify real card drops. Consult the release page for the final CI and artifact/checksum verification results.
