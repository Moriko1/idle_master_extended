using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace IdleMasterExtended.Tests
{
    internal static class DesktopRunPolicyTests
    {
        public static void RunAll()
        {
            var policy = new SteamClientLossPolicy();
            var now = TimeSpan.Zero;
            Test.Assert(policy.Observe(SteamClientPresence.Absent, SteamAccountPresence.Unknown, now, true) == null,
                "One missing client observation cannot pause a run.");
            Test.Assert(policy.Observe(SteamClientPresence.Unknown, SteamAccountPresence.Unknown, (now + TimeSpan.FromSeconds(10)), true) == null,
                "Registry or process observation failure must not become a client exit.");
            Test.Assert(policy.Observe(SteamClientPresence.Present, SteamAccountPresence.Matches, (now + TimeSpan.FromSeconds(11)), true) == null,
                "A recovered micro-outage leaves the queue running.");
            Test.Assert(policy.Observe(SteamClientPresence.Absent, SteamAccountPresence.Unknown, (now + TimeSpan.FromSeconds(12)), true) == null,
                "Recovery resets the client-loss grace period.");
            Test.Assert(policy.Observe(SteamClientPresence.Absent, SteamAccountPresence.Unknown, (now + TimeSpan.FromSeconds(15)), true) == "steam_closed_pause",
                "Confirmed Steam exit eventually pauses and retains the queue.");
            Test.Assert(policy.Observe(SteamClientPresence.Present, SteamAccountPresence.Changed, (now + TimeSpan.FromSeconds(16)), true) == "steam_account_changed_pause",
                "A verified different client account stops the run immediately.");
            Test.Assert(policy.Observe(SteamClientPresence.Absent, SteamAccountPresence.Unknown, (now + TimeSpan.FromSeconds(30)), false) == null,
                "An idle app does not emit a run-pause event.");

            var notices = new RunNoticePolicy();
            Test.Assert(notices.Observe(IdleRunState.Completed, true) == RunNotice.None, "Startup empty scans must not notify.");
            notices.Observe(IdleRunState.Starting, false);
            notices.Observe(IdleRunState.Running, false);
            Test.Assert(notices.Observe(IdleRunState.Paused, false) == RunNotice.Paused, "A queue pause emits one Windows notice.");
            Test.Assert(notices.Observe(IdleRunState.Paused, false) == RunNotice.None, "Repeated pause snapshots cannot spam notifications.");
            Test.Assert(notices.Observe(IdleRunState.Faulted, false) == RunNotice.None, "Faulted and paused share a single stopped-run notice.");
            notices.Observe(IdleRunState.Running, false);
            Test.Assert(notices.Observe(IdleRunState.Completed, true) == RunNotice.Completed, "Verified zero remaining cards emits completion.");
            Test.Assert(notices.Observe(IdleRunState.Completed, true) == RunNotice.None, "Completion summaries notify only once.");
            notices.Observe(IdleRunState.Running, false);
            var tracker = new IdleSessionTracker();
            tracker.Start(new[] { new IdleGame(10, "Skipped last game", 3, 2) }, IdleMode.Single);
            var skippedSummary = tracker.Finish(true, TimeSpan.FromMinutes(1));
            Test.Assert(notices.Observe(IdleRunState.Completed, skippedSummary.RemainingCards == 0) == RunNotice.None,
                "An empty queue after Skip cannot notify zero card drops while verified cards remain.");
            notices.Observe(IdleRunState.Running, false);
            Test.Assert(notices.Observe(IdleRunState.Stopped, false) == RunNotice.None, "Manual Stop uses the in-app summary.");

            using (var form = new frmMain(() => true))
            {
                // A synthetic fixture never loads the app, authenticates, launches helpers, or contacts Steam.
                form.VisualWorkProbe = () => false;
                form.AllBadges.Add(new Badge { AppId = 10, Name = "Synthetic game", RemainingCard = 2, HoursPlayed = 1 });
                form.UpdateStateInfo();
                Test.Assert(Field<ListView>(form, "GamesState").Items.Count == 0, "Background snapshots defer expensive list rebuilding.");
                Invoke(form, "UpdateVisualWork");
                Test.Assert(!Field<System.Windows.Forms.Timer>(form, "displayTimer").Enabled, "Inactive windows have no countdown repaint timer.");
                form.VisualWorkProbe = () => true;
                Invoke(form, "UpdateVisualWork");
                Test.Assert(Field<ListView>(form, "GamesState").Items.Count == 1, "Foreground return applies the deferred card list.");
                Test.Assert(!form.Visible && !form.Focused, "Applying background state must not activate or reveal the app.");
                Test.Assert(!Field<SteamSessionService>(form, "session").IsInitialized, "Background UI tests must never open Steam sign-in.");
                Field<SteamActivityMonitor>(form, "steamActivity").Dispose();
                Field<System.Windows.Forms.Timer>(form, "displayTimer").Dispose();
                Field<CancellationTokenSource>(form, "lifetime").Cancel();
                Field<CancellationTokenSource>(form, "lifetime").Dispose();
                Field<HttpClient>(form, "artworkClient").Dispose();
                Field<SemaphoreSlim>(form, "scanGate").Dispose();
                Field<SteamSessionService>(form, "session").Dispose();
                Field<System.Drawing.Image>(form, "darkTrue").Dispose();
                Field<System.Drawing.Image>(form, "darkFalse").Dispose();
            }
        }
        private static T Field<T>(frmMain form, string name) => (T)typeof(frmMain)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
        private static void Invoke(frmMain form, string name) => typeof(frmMain)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
    }
}
