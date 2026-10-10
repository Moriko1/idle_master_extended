using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace IdleMasterExtended.Tests
{
    /// <summary>Opt-in read-only check of the app-owned remembered browser session; never launches idling.</summary>
    internal static class SessionLiveProbe
    {
        public static int Run(bool identityOnly = false)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int exit = 6;
            using (var context = new ApplicationContext())
            using (var timer = new System.Windows.Forms.Timer { Interval = 1 })
            {
                timer.Tick += async (sender, args) =>
                {
                    timer.Stop();
                    try { exit = await ProbeAsync(identityOnly); }
                    finally { context.ExitThread(); }
                };
                timer.Start();
                Application.Run(context);
            }
            return exit;
        }

        private static async Task<int> ProbeAsync(bool identityOnly)
        {
            SteamReadStatus initialized = SteamReadStatus.TransientFailure;
            SteamReadStatus account = SteamReadStatus.TransientFailure;
            SteamReadStatus badges = SteamReadStatus.TransientFailure;
            bool verified = false;
            int recoveryAttempts = 0;
            int? games = null, eligible = null, cards = null, privateGames = null;
            string scanMessage = null;
            int exit = 6;
            using (var host = new Form { ShowInTaskbar = false, Width = 100, Height = 100 })
            using (var browser = new WebView2 { Dock = DockStyle.Fill })
            using (var session = new SteamSessionService())
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(600)))
            {
                host.Controls.Add(browser);
                var handle = host.Handle;
                try
                {
                    var initialization = session.InitializeAsync(browser);
                    if (await Task.WhenAny(initialization, Task.Delay(TimeSpan.FromSeconds(25))) != initialization)
                    {
                        // Exit 5 reports initialization timeout without printing browser/profile details.
                        Observe(initialization);
                        exit = 5;
                    }
                    else
                    {
                        await initialization;
                        initialized = SteamReadStatus.Success;
                        var validation = await session.ValidateAsync(cancellation.Token);
                        account = validation.Status;
                        verified = validation.IsSuccess;
                        recoveryAttempts = session.RecoveryAttempts;
                        exit = verified && identityOnly ? 0 : 2;
                        if (verified && !identityOnly)
                        {
                            var library = new OwnedGamesReader(session.Client);
                            var scan = await new GameQueueScanner(session.Client, library, library)
                                .ReadAsync(validation.Value.ProfileUrl, null, cancellation.Token);
                            badges = scan.Status;
                            scanMessage = scan.IsSuccess ? null : scan.Message;
                            if (scan.IsSuccess)
                            {
                                games = scan.Value.Badges.Count;
                                privateGames = scan.Value.PrivateAppIds.Count;
                                eligible = scan.Value.Badges.Count(badge => !badge.IsPrivate && badge.RemainingCard > 0);
                                cards = scan.Value.Badges.Where(badge => !badge.IsPrivate).Sum(badge => Math.Max(0, badge.RemainingCard));
                                exit = 0;
                            }
                        }
                    }
                }
                catch (WebView2RuntimeNotFoundException) { exit = 3; }
                catch (COMException error) when (error.HResult == unchecked((int)0x80070020) ||
                                                 error.HResult == unchecked((int)0x800700AA))
                { exit = 4; }
                catch (OperationCanceledException) { exit = 2; }
                catch (Exception) { exit = 6; }
                finally
                {
                    // This disposes the view and HTTP client without signing out or clearing the profile.
                    await session.CloseAsync();
                }
            }
            var report = string.Join(Environment.NewLine, new[] {
                "Initialization status: " + initialized,
                "Identity only: " + identityOnly.ToString().ToLowerInvariant(),
                "Official browser recovery attempts: " + recoveryAttempts,
                "Verified private app count: " + (privateGames?.ToString() ?? "unavailable"),
                "Account status: " + account,
                "Verified account: " + verified.ToString().ToLowerInvariant(),
                "Badge scan status: " + (identityOnly ? "not run" : badges.ToString()),
                "Badge scan message: " + (scanMessage ?? "unavailable"),
                "Scanned games: " + Number(games),
                "Games with drops: " + Number(eligible),
                "Remaining cards: " + Number(cards),
            });
            Console.WriteLine(report);
            var root = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, ".git"))) root = root.Parent;
            if (root != null)
            {
                var artifacts = Path.Combine(root.FullName, "artifacts");
                Directory.CreateDirectory(artifacts);
                File.WriteAllText(Path.Combine(artifacts, identityOnly ? "live-identity-report.txt" : "live-session-report.txt"), report + Environment.NewLine);
            }
            return exit;
        }

        private static string Number(int? value) => value.HasValue ? value.Value.ToString() : "unavailable";

        private static async void Observe(Task task)
        {
            try { await task; }
            catch (Exception) { }
        }
    }
}
