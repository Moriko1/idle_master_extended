# Prerelease validation: 1.12.0-preview.3

Validation performed on the Windows development host for the corrected Moriko1 prerelease.

## Fix in preview.3

The user reported that idling began, then the app entered Paused with all 59 games still queued. The only automatic Paused path was the five-second main-window Steam-client probe. That probe now updates the client indicator without pausing a verified helper run. Backend disconnect callbacks also no longer terminate a helper while the same local Steam account remains verified; explicit logoff, missing client, missing identity and account mismatch still stop safely.

Transient and malformed card reads now retain both the last verified queue and its running helpers, retrying sequentially at 30, 60, 120, 240 and then 300 seconds. Only complete successful reads replace the queue or prove completion. Authentication and helper identity failures remain visible and require a manual recovery.

An explicit Stop button and natural queue completion show a session summary as a child panel inside the main window. Presenting it never shows, restores or activates that window, creates a top-level popup or opens a completion MessageBox. The summary remains available when returning from the tray. Totals come only from authenticated complete snapshots of the initial session games, so skips and filter edits cannot become card drops.

Opening Settings or a filter editor no longer stops idling. Cancel preserves the run; saved mode/filter settings are frozen until the next session. Appearance and sleep preferences can apply immediately.

## Previous fix in preview.2

The initial preview attempted to import every browser cookie. The user's login-window error was traced to CookieException during CookieContainer.Add, before any HTTP request. Browser preferences containing commas reproduce this .NET Framework failure. Preview.2 imports only Community authentication/session/parental cookies without transforming token values, and distinguishes invalid authentication from network failure. New regression cases cover comma/semicolon/overlong preferences, exact domains, preservation of authentication values and malformed authentication.

Blank badge-index counts now use an explicit count from the individual card page when available. If both recognized drop sections are empty, a same-account Steam-owned-library read can exclude only games proven absent; unreadable counts for games in that library still fail the atomic scan. The library request includes free and family licenses and disables the unvetted-app filter. Its result header, identifiers and declared count are validated, including the separately flagged family entries. Session tokens stay in memory and are excluded from reports and logs.

## Passed

- Both executables build as x64 Release for .NET Framework 4.8, with no compiler warnings or errors.
- Six regression suites pass: Community reads and badge parsing; run supervision and cancellation; Settings Save/Cancel; session identity verification; verified helper reconnect decisions; session summaries and nonactivating child controls.
- Synthetic HTTP cases include authenticated empty accounts, expired login, timeouts, HTTP 429, malformed pages, pagination, decimal formats and retention of the previous snapshot. New cases cover verified ownership, shared-license counts, rejected redirects, cancellation of streams that ignore tokens, explicit count phrases, and legitimate scans requiring more than 128 detail reads.
- A main-window regression forces a false client probe while a verified fake helper runs; neither helper nor queue stops. Manual Stop and verified completion present summaries, and repeated Stop keeps the same summary.
- Controller cases cover cancellation during launches and fast-mode delays, 30-helper limits, initialization failure, unexpected exit, account mismatch, skipped games on resume, failed scans and completion cleanup.
- A real Windows Job Object test confirms that disposing the owned job stops its child and leaves an unrelated process running.
- Main-window rendering and control geometry pass at synthetic 100%, 125%, 150% and 200% scales, in light and dark themes. Settings controls, long failure messages, the Stop action and session summaries render visibly. These are rendered forms, not actual monitor DPI changes.
- The desktop application opened and remained running from a portable test folder with C:/Windows as its working directory.
- Git whitespace validation passes.

## Live account evidence

- After the user signed in through the application, the corrected cookie bridge verified the remembered Community account using its authenticated viewer identity. No authentication values or account identifiers were written to the report.
- A fresh opt-in probe verified the remembered account after the previous application and all browser processes for its profile had exited. Opening the probe started no idle helpers.
- A complete authenticated badge scan then succeeded through the production scanner and ownership reader. The atomic snapshot reported eligible games and remaining cards; no helpers were launched.
- The user reported that real idling appeared to start, then unexpectedly paused with 59 games still queued in preview.2. This establishes initial user-observed idling, not sustained idling or card drops. The preview.3 corrections have not yet had a user-operated sustained-run check.

## Pending user-operated checks

- QR/Steam Guard challenge variations and authenticated badge-scan compatibility across other accounts.
- Real card idling and card drops, including pause/resume, account switching and sign-out during a run.
- Layout on actual monitors at the listed DPI settings.
- Startup from an ACL-enforced read-only application folder. The host denied the attempted ACL change, so this check has not passed. Code stores session data/logs in LocalAppData and launches helpers using absolute paths.

The first download is a prerelease. Synthetic tests are separate from the live account evidence above and do not verify real card drops. Consult the release page for the final CI and artifact/checksum verification results.
