# Building and verifying Idle Master Extended

Use Windows 10/11 x64, Visual Studio 2022 or newer with the .NET desktop workload and Python 3. The build restores pinned .NET Framework 4.8 reference assemblies automatically. Steam is unnecessary for the synthetic regression tests.

From the repository root:

    python scripts/build.py
    python scripts/build.py --package --version 1.12.0-preview.3

The script restores pinned NuGet packages, builds the helper before the application, and runs the x64 regression console. Packaging requires a clean Git checkout so the source ZIP corresponds to the binaries. The portable ZIP, source ZIP, and SHA256SUMS.txt are written to dist/.

Run IdleMasterExtended.exe from the extracted portable ZIP. The installed Microsoft Edge WebView2 Evergreen Runtime is required for sign-in; a full browser runtime is intentionally not included. Neither Steam passwords nor Community cookies belong in the ZIP.

## Synthetic layout checks

After building, run Source/IdleMasterExtended.Tests/bin/x64/Release/IdleMasterExtended.Tests.exe --render to create invisible-window PNGs under artifacts/. This mode never shows the app or initializes its Steam browser session. It simulates 100%, 125%, 150%, and 200% scaling in both themes, checks control bounds and long status text, and renders the Settings Save action. These renders are not evidence of actual monitor DPI behavior.

## Optional remembered-session check

The test console accepts --live-session to verify the existing application-owned Steam browser profile and scan badges using authenticated read requests. This is opt-in and is never run by CI. It does not start idle helpers, sign out, or clear the saved profile. The scan has a ten-minute cancellation deadline for large libraries, with shorter per-request limits. Its report under artifacts/live-session-report.txt contains only read statuses, static application failure messages, and aggregate counts; it excludes authentication values, profile addresses, account identifiers, names, and page bodies.

## Manual release checks

Sign in through Steam's official window, including any Steam Guard/QR challenge. Close and reopen the application and confirm the remembered account is verified without starting helpers. Select Start and check that the Steam desktop client has the same account. Pause, resume manually, and sign out while running. Each stop must leave no helpers created by this application.

Check 100%, 125%, 150%, and 200% display scaling; run from an unrelated working directory and a read-only extracted application directory. Settings, diagnostics, and the browser profile should remain under LocalAppData.

A release must distinguish successful automated tests from actual user-operated Steam login, remembered-session, card-idling, and desktop layout checks. CI does not perform those account-dependent checks.
