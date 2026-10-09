# Prerelease validation: 1.12.0-preview.1

Validation performed on the Windows development host for the first Moriko1 fork release.

## Passed

- Both executables build as x64 Release for .NET Framework 4.8, with no compiler warnings or errors.
- Four regression suites pass: Community reads and badge parsing; run supervision and cancellation; Settings Save/Cancel; session identity verification.
- Synthetic HTTP cases include authenticated empty accounts, expired login, timeouts, HTTP 429, malformed pages, pagination, decimal formats and retention of the previous snapshot.
- Controller cases cover cancellation during launches and fast-mode delays, 30-helper limits, initialization failure, unexpected exit, account mismatch, skipped games on resume, failed scans and completion cleanup.
- A real Windows Job Object test confirms that disposing the owned job stops its child and leaves an unrelated process running.
- Main-window rendering and control geometry pass at synthetic 100%, 125%, 150% and 200% scales, in light and dark themes. Settings controls and long failure messages render visibly. These are rendered forms, not actual monitor DPI changes.
- The desktop application opened and remained running from a portable test folder with C:/Windows as its working directory.
- Git whitespace validation passes.

## Pending user-operated checks

- Steam sign-in, QR/Steam Guard challenges and authenticated badge scans against a real account.
- Remembered sign-in after closing and reopening the application.
- Real card idling and card drops, including pause/resume, account switching and sign-out during a run.
- Layout on actual monitors at the listed DPI settings.
- Startup from an ACL-enforced read-only application folder. The host denied the attempted ACL change, so this check has not passed. Code stores session data/logs in LocalAppData and launches helpers using absolute paths.

The first download is a prerelease. Passing synthetic tests is not evidence that Steam login or real card drops have succeeded. Consult the release page for the final CI and artifact/checksum verification results.
