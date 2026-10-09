# Prerelease validation: 1.12.0-preview.4

Validation performed on the Windows development host for the corrected Moriko1 prerelease.

## Fix in preview.4

Steam's authenticated private-app list is now read using the same in-memory Community session token and verified account identity as ownership checks. The request uses Valve's own read-only `IAccountPrivateAppsService/GetPrivateAppList/v1/` contract, not an inferred flag from owned-game metadata or overall profile visibility. [Steam Support confirms that private games receive no trading cards](https://help.steampowered.com/en/faqs/view/1150-C06F-4D62-4966).

A successful Steam result and explicit private-list container are required; an unreadable, expired, redirected or rate-limited response never becomes an empty exclusion set. Card queues publish only after complete success. Independently verified private IDs immediately stop matching helpers even if a later badge page fails; the previous card counts are retained and that partial read cannot complete the run. Every mode skips verified private IDs, including configured whitelist entries. Private badge rows remain visibly marked as skipped, without invented drop counts or extra card-detail requests.

Start and Resume recheck the queue before launching. Periodic refreshes also check privacy in whitelist mode. When a tracked game becomes private, the next verified refresh removes it from the run and from session totals before interpreting missing badge rows; exclusions are reported separately and cannot count as card drops or completed games. Previously observed legitimate drops remain in the summary.

## Fix in preview.3

The user reported that idling began, then the app entered Paused with all 59 games still queued. The only automatic Paused path was the five-second main-window Steam-client probe. That probe now updates the client indicator without pausing a verified helper run. Backend disconnect callbacks also no longer terminate a helper while the same local Steam account remains verified; explicit logoff, missing client, missing identity and account mismatch still stop safely.

Transient and malformed card reads now retain both the last verified queue and its running helpers, retrying sequentially at 30, 60, 120, 240 and then 300 seconds. Only complete successful reads replace the queue or prove completion. Authentication and helper identity failures remain visible and require a manual recovery.

An explicit Stop button and natural queue completion show a session summary as a child panel inside the main window. Presenting it never shows, restores or activates that window, creates a top-level popup or opens a completion MessageBox. The summary remains available when returning from the tray. Totals come only from authenticated complete snapshots of the initial session games, so skips and filter edits cannot become card drops.

Opening Settings or a filter editor no longer stops idling. Cancel preserves the run; saved mode/filter settings are frozen until the next session. Appearance and sleep preferences can apply immediately.

Show Steam username defaults to on in the generated settings, settings definition and executable configuration. A version-independent provider stores ordinary preferences in `%LocalAppData%\IdleMasterExtended\Moriko1\preferences.xml`, migrates known ordinary legacy settings and excludes all cookie/account-token entries. Atomic saves keep a backup; malformed/oversized/DTD files are preserved and recover from a valid backup when available. Explicit saved choices, including hiding the username, survive fresh settings instances. Start remains disabled for empty/finished/filtered queues, unready scans, invalid sessions and active runs.

## Previous fix in preview.2

The initial preview attempted to import every browser cookie. The user's login-window error was traced to CookieException during CookieContainer.Add, before any HTTP request. Browser preferences containing commas reproduce this .NET Framework failure. Preview.2 imports only Community authentication/session/parental cookies without transforming token values, and distinguishes invalid authentication from network failure. New regression cases cover comma/semicolon/overlong preferences, exact domains, preservation of authentication values and malformed authentication.

Blank badge-index counts now use an explicit count from the individual card page when available. If both recognized drop sections are empty, a same-account Steam-owned-library read can exclude only games proven absent; unreadable counts for games in that library still fail the atomic scan. The library request includes free and family licenses and disables the unvetted-app filter. Its result header, identifiers and declared count are validated, including the separately flagged family entries. Session tokens stay in memory and are excluded from reports and logs.

## Passed

- Both executables build as x64 Release for .NET Framework 4.8, with no compiler warnings or errors.
- Nine regression suites pass: authenticated private-app reading and atomic private filtering; Community reads and badge parsing; run supervision and cancellation; Settings Save/Cancel; session identity verification; verified helper reconnect decisions; session summaries and nonactivating child controls; portable settings persistence and migration; Start availability.
- Synthetic HTTP cases include authenticated empty accounts, expired login, timeouts, HTTP 429, malformed pages, pagination, decimal formats and retention of the previous snapshot. New cases cover verified ownership, shared-license counts, rejected redirects, cancellation of streams that ignore tokens, explicit count phrases, and legitimate scans requiring more than 128 detail reads.
- A main-window regression forces a false client probe while a verified fake helper runs; neither helper nor queue stops. Manual Stop and verified completion present summaries, and repeated Stop keeps the same summary.
- Private-game regressions cover explicit empty and populated lists, token/account proof, invalid identifiers/result headers, rate limits, cancellation, copied snapshots, private blank badges without detail requests, Whitelist exclusions, privacy changes during failed scans, selective helper cleanup and pending initialization. Private exclusions preserve legitimate completed-game totals; games later made public establish a fresh count baseline before observing further drops.
- Controller cases cover cancellation during launches and fast-mode delays, 30-helper limits, initialization failure, unexpected exit, account mismatch, skipped games on resume, failed scans and completion cleanup.
- A real Windows Job Object test confirms that disposing the owned job stops its child and leaves an unrelated process running.
- Main-window rendering and control geometry pass at synthetic 100%, 125%, 150% and 200% scales, in light and dark themes. Settings controls, long failure messages, the Stop action and session summaries render visibly. These are rendered forms, not actual monitor DPI changes.
- The desktop application opened and remained running from a portable test folder with C:/Windows as its working directory.
- Fresh settings instances reload saved booleans, modes, language, colors and both filter lists. Tests also cover saved username opt-out, unsaved edits, cookie exclusion, upgrade/reset, XML bounds, corruption preservation and backup recovery.
- Empty and completed queues keep Start disabled, including a connected authenticated synthetic session. Whitelist eligibility, filters, busy/scanning, active runs and Resume are covered separately.
- Git whitespace validation passes.

## Live account evidence

- The preview.4 opt-in read-only probe verified the remembered account and Steam's authenticated private-app endpoint. It returned 10 private app IDs; the complete production queue scan succeeded with 949 badges, 55 nonprivate games with drops and 221 eligible remaining cards. Reports contain aggregate counts only, without account IDs, app IDs, names or tokens. No idle helpers were launched.
- After the user signed in through the application, the corrected cookie bridge verified the remembered Community account using its authenticated viewer identity. No authentication values or account identifiers were written to the report.
- A fresh opt-in probe verified the remembered account after the previous application and all browser processes for its profile had exited. Opening the probe started no idle helpers.
- A complete authenticated badge scan then succeeded through the production scanner and ownership reader. The atomic snapshot reported eligible games and remaining cards; no helpers were launched.
- The user reported that real idling appeared to start, then unexpectedly paused with 59 games still queued in preview.2. This establishes initial user-observed idling, not sustained idling or card drops. The preview.3/preview.4 corrections have not yet had a user-operated sustained-run check.

## Pending user-operated checks

- Private-game filtering across other accounts and real in-progress privacy changes.
- QR/Steam Guard challenge variations and authenticated badge-scan compatibility across other accounts.
- Real card idling and card drops, including pause/resume, account switching and sign-out during a run.
- Layout on actual monitors at the listed DPI settings.
- Startup from an ACL-enforced read-only application folder. The host denied the attempted ACL change, so this check has not passed. Code stores session data/logs in LocalAppData and launches helpers using absolute paths.

The first download is a prerelease. Synthetic tests are separate from the live account evidence above and do not verify real card drops. Consult the release page for the final CI and artifact/checksum verification results.
