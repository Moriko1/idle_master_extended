using System;
using System.Drawing;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IdleMasterExtended.Properties;
using System.Windows.Forms;

namespace IdleMasterExtended.Tests
{
    internal static class SessionSummaryTests
    {
        public static void RunAll()
        {
            VerifiedCounts();
            CompleteSnapshot();
            QueueChanges();
            UnknownCounts();
            SessionLifetime();
            HiddenOwner();
            EditingSettingsKeepsHelpers();
        }

        private static IdleGame Game(int id, int cards) => new IdleGame(id, "Game " + id, cards, 2);

        private static void VerifiedCounts()
        {
            var tracker = new IdleSessionTracker();
            tracker.Start(new[] { Game(1, 5), Game(2, 3) }, IdleMode.Single);
            tracker.Observe(new[] { Game(1, 3), Game(2, 3) });
            // Failed scans never call Observe, so a manual stop preserves this snapshot.
            var summary = tracker.Finish(false, TimeSpan.FromMinutes(21));
            Test.Assert(!summary.Completed && summary.ActiveTime == TimeSpan.FromMinutes(21),
                "Manual Stop preserves the reason and active idling time.");
            Test.Assert(summary.CardsObserved == 2 && summary.RemainingCards == 6 &&
                summary.GamesCompleted == 0 && summary.RemainingGames == 2,
                "Manual Stop reports verified decreases and previous counts after failed reads.");
        }

        private static void CompleteSnapshot()
        {
            var tracker = new IdleSessionTracker();
            tracker.Start(new[] { Game(1, 5), Game(2, 3) }, IdleMode.ManyThenOne);
            tracker.Observe(new[] { Game(1, 2), Game(2, 0) });
            tracker.Observe(new IdleGame[0]);
            var summary = tracker.Finish(true, TimeSpan.FromHours(28));
            Test.Assert(summary.Completed && summary.CardsObserved == 8 && summary.RemainingCards == 0 &&
                summary.GamesCompleted == 2 && summary.RemainingGames == 0,
                "An authenticated empty snapshot finishes tracked games exactly once.");
            Test.Assert(summary.ActiveTime == TimeSpan.FromHours(28) && summary.Mode == IdleMode.ManyThenOne,
                "Durations longer than a day and the selected mode remain available.");
        }

        private static void QueueChanges()
        {
            var tracker = new IdleSessionTracker();
            tracker.Start(new[] { Game(1, 4), Game(2, 2) }, IdleMode.Single);
            // Skip/blacklist changes the queue, never the complete-account snapshot.
            tracker.Observe(new[] { Game(1, 4), Game(2, 2), Game(3, 20) });
            var summary = tracker.Finish(false, TimeSpan.Zero);
            Test.Assert(summary.CardsObserved == 0 && summary.RemainingCards == 6 &&
                summary.GamesCompleted == 0 && summary.RemainingGames == 2,
                "Queue removals are not drops; newly discovered games do not inflate session totals.");
        }

        private static void UnknownCounts()
        {
            var tracker = new IdleSessionTracker();
            tracker.Start(new[] { Game(1, 2), Game(2, 3) }, IdleMode.Fast);
            tracker.Observe(new[] { Game(1, 4), Game(2, -1) });
            var summary = tracker.Finish(false, TimeSpan.Zero);
            Test.Assert(summary.CardsObserved == 0 && summary.RemainingCards == 7,
                "Count increases are not drops; unknown values preserve the last known count.");
            tracker.Start(new[] { Game(1, -1), Game(2, -1) }, IdleMode.Whitelist);
            tracker.Observe(new IdleGame[0]);
            summary = tracker.Finish(false, TimeSpan.FromHours(3));
            Test.Assert(summary.RemainingCards == null && summary.CardsObserved == 0 &&
                summary.GamesCompleted == 0 && summary.RemainingGames == 2,
                "Whitelist sessions retain unknown card totals and never infer badge completion.");
        }

        private static void SessionLifetime()
        {
            var tracker = new IdleSessionTracker();
            Test.Assert(tracker.Finish(false, TimeSpan.Zero) == null,
                "Stop before manual Start cannot create a summary.");
            tracker.Start(new[] { Game(1, 3) }, IdleMode.Single);
            tracker.Observe(new[] { Game(1, 1) });
            Test.Assert(tracker.Active && tracker.CardsObserved == 2,
                "An unfinished session retains observations across pause or retry.");
            tracker.Finish(false, TimeSpan.FromMinutes(5));
            Test.Assert(!tracker.Active && tracker.Finish(true, TimeSpan.Zero) == null,
                "Repeated Stop or late completion cannot create another summary.");
            tracker.Observe(new IdleGame[0]);
            Test.Assert(tracker.CardsObserved == 2, "A late read cannot change a finished session.");
            tracker.Start(new[] { Game(2, 4) }, IdleMode.Fast);
            var summary = tracker.Finish(false, TimeSpan.Zero);
            Test.Assert(summary.CardsObserved == 0 && summary.RemainingCards == 4 && summary.Mode == IdleMode.Fast,
                "A new manual Start resets previous totals.");
            tracker.Start(new[] { Game(3, 4) }, IdleMode.Single);
            tracker.Abandon();
            Test.Assert(!tracker.Active && tracker.Finish(false, TimeSpan.Zero) == null,
                "Closing or signing out discards pending summary work.");
        }

        private static void HiddenOwner()
        {
            var tracker = new IdleSessionTracker();
            tracker.Start(new[] { Game(1, 2) }, IdleMode.Single);
            var summary = tracker.Finish(false, TimeSpan.FromMinutes(2));
            using (var owner = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized })
            using (var panel = new SessionSummaryPanel())
            {
                owner.Controls.Add(panel);
                var sibling = new Button { Bounds = panel.Bounds, Text = "Underlying action" };
                owner.Controls.Add(sibling);
                var handle = owner.Handle;
                var windowState = owner.WindowState;
                var focused = owner.ActiveControl;
                var windows = Application.OpenForms.Count;
                bool activated = false;
                owner.Activated += (sender, args) => activated = true;
                panel.ApplyTheme(Color.FromArgb(38, 38, 38), Color.Gainsboro, true);
                panel.Present(summary, true);
                Test.Assert(owner.Controls.GetChildIndex(panel) == 0,
                    "Presenting must place the summary above sibling controls within its owner.");
                Test.Assert(panel.Parent == owner && ReferenceEquals(panel.Summary, summary),
                    "The summary is retained as a child inside Idle Master.");
                Test.Assert(!owner.Visible && owner.WindowState == windowState && !activated &&
                    owner.ActiveControl == focused && Application.OpenForms.Count == windows,
                    "Presenting cannot show, restore, activate, focus, or create another top-level window.");
                panel.Dismiss();
                Test.Assert(!panel.Visible && !owner.Visible && !activated,
                    "Dismissing cannot activate a hidden owner.");
            }
        }

        private static void EditingSettingsKeepsHelpers()
        {
            var savedSettings = new Dictionary<string, object>();
            foreach (SettingsProperty property in Settings.Default.Properties)
                savedSettings[property.Name] = Settings.Default[property.Name];
            var culture = Thread.CurrentThread.CurrentUICulture;
            var synchronization = SynchronizationContext.Current;
            SettingsSavingEventHandler cancelPersistence = (sender, args) => args.Cancel = true;
            Settings.Default.SettingsSaving += cancelPersistence;
            try
            {
                Settings.Default.language = "English";
                var factory = new FakeFactory();
                var games = new[] { Game(1, 4) };
                using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games)))
                using (var main = new frmMain(() => true))
                {
                    bool loaded = false;
                    main.Load += (sender, args) => loaded = true;
                    run.StartAsync(games, 76561198000000001, IdleMode.Single).GetAwaiter().GetResult();
                    Test.Assert(SpinWait.SpinUntil(() => run.Snapshot.State == IdleRunState.Running, 3000),
                        "A fake helper must be running before editing settings.");
                    SetField(main, "controller", run);
                    SetField(main, "runStatus", run.Snapshot);
                    SetField(main, "runMode", IdleMode.Single);
                    Field<IdleSessionTracker>(main, "sessionTracker").Start(games, IdleMode.Single);
                    main.AllBadges.Add(new Badge { AppId = 1, Name = "Game 1", RemainingCard = 4, HoursPlayed = 2 });
                    main.GetType().GetMethod("UpdateSteamClientStatus", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(main, new object[] { false });
                    Test.Assert(run.Snapshot.State == IdleRunState.Running && factory.Helper.IsRunning,
                        "A false broad Steam-client probe must not automatically pause or stop a verified run.");
                    var edit = main.GetType().GetMethod("EditAndRefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance);
                    var canceled = (Task)edit.Invoke(main, new object[] { new Func<DialogResult>(() => DialogResult.Cancel) });
                    Test.Assert(canceled.IsCompleted, "Cancel must finish without a network read or run transition.");
                    canceled.GetAwaiter().GetResult();
                    Test.Assert(run.Snapshot.State == IdleRunState.Running && factory.Helper.IsRunning,
                        "Opening and canceling Settings must keep the existing helper running.");
                    bool oldFast = Settings.Default.fastMode;
                    var committed = (Task)edit.Invoke(main, new object[] { new Func<DialogResult>(() =>
                    {
                        Settings.Default.fastMode = !oldFast;
                        return DialogResult.OK;
                    }) });
                    Test.Assert(committed.IsCompleted, "Saving active-session settings must not block on another scan.");
                    committed.GetAwaiter().GetResult();
                    Test.Assert(run.Snapshot.State == IdleRunState.Running && factory.Helper.IsRunning &&
                        run.Snapshot.Mode == IdleMode.Single && run.Snapshot.RemainingGames.Count == 1,
                        "Saving a new mode must preserve current helpers, mode and queue until the next session.");
                    var pendingGames = (List<IdleGame>)main.GetType().GetMethod("GamesForRun",
                        BindingFlags.NonPublic | BindingFlags.Instance).Invoke(main, new object[0]);
                    Test.Assert(pendingGames.Count == 1 && pendingGames[0].AppId == 1,
                        "Deferred settings must not change the active queue selection.");
                    SynchronizationContext.SetSynchronizationContext(null);
                    var stop = (Task)main.GetType().GetMethod("StopSessionAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(main, new object[0]);
                    stop.GetAwaiter().GetResult();
                    var popup = Field<SessionSummaryPanel>(main, "summaryPanel");
                    Test.Assert(run.Snapshot.State == IdleRunState.Stopped && !factory.Helper.IsRunning &&
                        popup.Summary != null && !popup.Summary.Completed && popup.Summary.RemainingCards == 4,
                        "Manual Stop must release helpers and retain a last-verified session summary.");
                    var firstSummary = popup.Summary;
                    var again = (Task)main.GetType().GetMethod("StopSessionAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(main, new object[0]);
                    again.GetAwaiter().GetResult();
                    Test.Assert(ReferenceEquals(popup.Summary, firstSummary) && !Field<bool>(main, "ready"),
                        "Repeated Stop must not duplicate summaries; deferred settings need a new scan.");
                    var tracker = Field<IdleSessionTracker>(main, "sessionTracker");
                    tracker.Start(games, IdleMode.Single); tracker.Observe(new IdleGame[0]);
                    run.StartAsync(new IdleGame[0], 76561198000000001, IdleMode.Single).GetAwaiter().GetResult();
                    main.GetType().GetMethod("ControllerChanged", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(main, new object[] { run, run.Snapshot });
                    Test.Assert(popup.Summary.Completed && popup.Summary.RemainingCards == 0 && !tracker.Active,
                        "Verified natural completion must release the session and present its completion summary.");
                    Test.Assert(!loaded && !Field<SteamSessionService>(main, "session").IsInitialized,
                        "A settings interaction probe must never initialize Steam sign-in.");
                    Field<System.Windows.Forms.Timer>(main, "displayTimer").Dispose();
                    Field<System.Windows.Forms.Timer>(main, "tmrCheckSteam").Stop();
                    Field<System.Windows.Forms.Timer>(main, "tmrStatistics").Stop();
                    Field<CancellationTokenSource>(main, "lifetime").Cancel();
                    Field<System.Net.Http.HttpClient>(main, "artworkClient").Dispose();
                }
            }
            finally
            {
                Settings.Default.SettingsSaving -= cancelPersistence;
                SynchronizationContext.SetSynchronizationContext(synchronization);
                Thread.CurrentThread.CurrentUICulture = culture;
                foreach (var pair in savedSettings) Settings.Default[pair.Key] = pair.Value;
            }
        }

        private static T Field<T>(object value, string name) =>
            (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
        private static void SetField(object value, string name, object replacement) =>
            value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(value, replacement);

        private sealed class FakeFactory : IIdleHelperFactory
        {
            public FakeHelper Helper;
            public Task<IIdleHelper> StartAsync(int appId, ulong steamId, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Helper = new FakeHelper(appId, steamId);
                return Task.FromResult<IIdleHelper>(Helper);
            }
        }

        private sealed class FakeHelper : IIdleHelper
        {
            private readonly TaskCompletionSource<bool> ended =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int AppId { get; private set; }
            public ulong SteamId { get; private set; }
            public bool IsRunning => !ended.Task.IsCompleted;
            public Task Completion => ended.Task;
            public FakeHelper(int appId, ulong steamId) { AppId = appId; SteamId = steamId; }
            public void Dispose() { ended.TrySetResult(true); }
        }
    }
}
