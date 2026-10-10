# Prerelease validation: 1.12.0-preview.5

Validation performed on the Windows development host for the corrected Moriko1 prerelease.

## Fix and verification in preview.5

Launching a user Steam game switches every mode to preparation only. Known eligible games below the estimated two-hour threshold warm up, then stop individually; ready games and Whitelist entries with unknown hours wait. A positive queue can remain Running with no helpers while the user plays. Normal mode behavior returns automatically when gameplay ends, without a manual Resume. Local startup/helper elapsed time is monotonic, combines with verified badge hours without double credit, and caps preparation even during a pending request, failed scan, queued initialization or wall-clock change. This is an estimated preparation threshold, not proof that Steam has awarded or will award a card.

The main window no longer calls the native Steam SDK. An off-UI three-second observation uses exact Steam process identity, account-bound registry hints and a bounded incremental Steam tracked-process log. Owned helpers and the app's own process subtree are excluded. Registry app lists are cached; log reads start with at most 256 KiB and subsequently consume only appends. PID creation times reject stale reused IDs. Client/log/registry information is observed Steam client behavior, not a public Steamworks game-enumeration contract: unreadable evidence is Unknown, short uncertainty preserves the current run, and unusual launchers/client schema changes still need user testing. Windows process-reading APIs are documented by [Microsoft](https://learn.microsoft.com/en-us/windows/win32/toolhelp/taking-a-snapshot-and-viewing-processes).

Confirmed Steam process absence gets a short monotonic grace before helpers stop and the queue pauses for manual Resume. Account changes stop immediately. Recoverable helper failures retry with a 5-60 second backoff; six consecutive initialization failures retain the queue in a visible fault requiring recovery instead of silently blocking it forever. Authentication failures retain manual recovery. [Valve documents backend login separately from local client identity](https://partner.steamgames.com/doc/api/ISteamUser#BLoggedOn); backend reconnects alone no longer reject an otherwise verified matching account at helper startup.

Helper failure cleanup waits until native callback dispatch returns, avoiding callback-object disposal during dispatch. The callback timer uses 80 ms and context checks use two seconds, reducing nominal timer wakeups by 37.5 percent while following [Valve's recommended callback cadence](https://partner.steamgames.com/doc/api/steam_api#SteamAPI_RunCallbacks). Helpers run at BelowNormal priority. Actual CPU savings across 30 live helpers have not been measured.

Inactive/minimized windows stop countdown repaint timers, defer list rebuilding, and avoid artwork downloads. Artwork requests are deduplicated, optional failures are not repeatedly retried, and returning to the app applies current state. Closing sign-in unloads Steam's browser page while preserving the dedicated profile cookies. Sleep prevention is released when no helpers are active or the session stops.

Pause/fault transitions and verified zero-card completion request one Windows shell notification per transition. They do not restore or activate Idle Master; only an explicit notification click restores it. Windows notification settings and Focus Assist may suppress delivery. Completion uses the finished session's known remaining count, so skipping the last unfinished game does not produce a false zero-card notification. Stop and completion summaries remain child controls within the app.

Both x64 Release executables build with no compiler warnings/errors and all 11 regression suites pass. New fake-clock/process scenarios cover all five modes during gameplay, automatic return, actual game exclusions, exactly 30 helpers and immediate slot refilling, per-game preparation caps, pending failed scans, delayed/late readiness, fast-mode interruption, manual Pause/Stop, transient recovery, bounded initialization failures and account mismatch. Detector tests cover owned helpers, same-app launches, third-party-launcher tracked PIDs, stale PID reuse, browsers/utility exclusions, partial log writes, rotation, malformed evidence, cached registry reads and failed observations. Desktop policy tests cover client-loss grace, deferred background list/timer work, repeated notifications and Skip-last count proof.

Sixty synthetic form PNGs render and their geometry/text checks pass at 100%, 125%, 150% and 200% scales in light/dark themes, including all new gameplay and client-pause messages. These are invisible-window renders, not actual monitor DPI changes.

The opt-in preview.5 read-only probe verified the remembered account and 10 private app IDs. Its complete production scan returned 949 badges, 41 eligible nonprivate games and 163 remaining cards. No helper was launched, and no account IDs, app IDs, names or authentication values were written to the report. These aggregate counts are current read evidence; their decrease from earlier probes cannot establish that this build caused card drops.

The reported game-launch crash has no matching stack in the app's diagnostic log or Windows Application crash events on this host. The callback-disposal hazard and unsafe failure paths have been corrected and tested; the exact reported crash and real game-open/game-close behavior still need user-operated validation. Windows notification delivery, Steam exit during actual idling, sustained card drops and real foreground/background performance remain separate manual checks.

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

## Passed in preview.4 and earlier

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

## Live account evidence from earlier previews

- The preview.4 opt-in read-only probe verified the remembered account and Steam's authenticated private-app endpoint. It returned 10 private app IDs; the complete production queue scan succeeded with 949 badges, 55 nonprivate games with drops and 221 eligible remaining cards. Reports contain aggregate counts only, without account IDs, app IDs, names or tokens. No idle helpers were launched.
- After the user signed in through the application, the corrected cookie bridge verified the remembered Community account using its authenticated viewer identity. No authentication values or account identifiers were written to the report.
- A fresh opt-in probe verified the remembered account after the previous application and all browser processes for its profile had exited. Opening the probe started no idle helpers.
- A complete authenticated badge scan then succeeded through the production scanner and ownership reader. The atomic snapshot reported eligible games and remaining cards; no helpers were launched.
- The user reported that real idling appeared to start, then unexpectedly paused with 59 games still queued in preview.2. This establishes initial user-observed idling, not sustained idling or card drops. The preview.3/preview.4 corrections have not yet had a user-operated sustained-run check.

## Pending user-operated checks

- Gameplay transitions with ordinary/same-app games and third-party launchers, Steam exit during real idling, Windows notification delivery, and sustained background performance.
- Private-game filtering across other accounts and real in-progress privacy changes.
- QR/Steam Guard challenge variations and authenticated badge-scan compatibility across other accounts.
- Real card idling and card drops, including pause/resume, account switching and sign-out during a run.
- Layout on actual monitors at the listed DPI settings.
- Startup from an ACL-enforced read-only application folder. The host denied the attempted ACL change, so this check has not passed. Code stores session data/logs in LocalAppData and launches helpers using absolute paths.

The first download is a prerelease. Synthetic tests are separate from the live account evidence above and do not verify real card drops. Consult the release page for the final CI and artifact/checksum verification results.
