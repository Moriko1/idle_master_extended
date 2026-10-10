using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IdleMasterExtended.Properties;

namespace IdleMasterExtended.Tests
{
    internal static class ScanReuseTests
    {
        private const ulong Account = 76561198000000001;
        private static ScanQueueSource Cards() => new ScanQueueSource(false, new string[0]);

        public static async Task RunAllAsync()
        {
            await RecentScanKeepsBadgeRequestBudgetAsync();
            await ChangedPrivacyRequiresCountsAsync();
            await FailedPrivacyCannotReuseAsync();
            await CanceledPrivacyCannotReuseAsync();
            await SlowPrivacyExpiresSnapshotAsync();
            AccountQueueAndSignOutInvalidate();
            SettingsInputsAreCopiedAndClassified();
            SettingsApplyWithoutAnotherScan();
        }

        private static async Task RecentScanKeepsBadgeRequestBudgetAsync()
        {
            var now = TimeSpan.FromMinutes(10);
            var policy = new ScanReusePolicy(() => now);
            var reader = new FakePrivateReader();
            var source = Cards();
            int fullScans = 0, applied = 0;
            Func<bool, Task> start = async resuming =>
            {
                var read = await policy.VerifyForStartAsync(Account, source, resuming, reader, "synthetic-profile",
                    CancellationToken.None, ids => { applied++; return Task.CompletedTask; });
                Test.Assert(read.IsSuccess, "Synthetic privacy reads must succeed.");
                if (!read.Value) { fullScans++; policy.RecordComplete(Account, source, reader.Ids); }
            };
            await start(false);
            Test.Assert(fullScans == 1 && reader.Reads == 0, "Without a complete scan, Start requires full card and privacy verification.");
            now += TimeSpan.FromSeconds(119);
            await start(false);
            Test.Assert(fullScans == 1 && reader.Reads == 1 && applied == 1,
                "A recent complete scan saves all repeated badge reads, but still verifies privacy before Start.");
            now += TimeSpan.FromSeconds(1);
            await start(false);
            Test.Assert(fullScans == 1 && reader.Reads == 2, "The two-minute boundary is inclusive and reuse cannot extend its age.");
            now += TimeSpan.FromMilliseconds(1);
            await start(false);
            Test.Assert(fullScans == 2 && reader.Reads == 2, "An expired scan requires the normal full scan, without an extra preliminary privacy read.");
            await start(true);
            Test.Assert(fullScans == 3 && reader.Reads == 2, "Manual Resume always performs a full scan, even immediately after success.");
        }

        private static async Task ChangedPrivacyRequiresCountsAsync()
        {
            foreach (var newlyPublic in new[] { false, true })
            {
                var policy = new ScanReusePolicy(() => TimeSpan.Zero);
                var original = newlyPublic ? new HashSet<int> { 20 } : new HashSet<int>();
                policy.RecordComplete(Account, Cards(), original);
                var reader = new FakePrivateReader { Ids = newlyPublic ? new HashSet<int>() : new HashSet<int> { 20 } };
                var badge = new Badge { AppId = 20, RemainingCard = newlyPublic ? -1 : 3, HoursPlayed = 1, IsPrivate = newlyPublic };
                var result = await policy.VerifyForStartAsync(Account, Cards(), false, reader, "synthetic-profile",
                    CancellationToken.None, ids => { badge.IsPrivate = ids.Contains(20); return Task.CompletedTask; });
                Test.Assert(result.IsSuccess && !result.Value && reader.Reads == 1,
                    "Any private-set change requires a complete scan, including formerly private games with unknown counts.");
                Test.Assert(badge.IsPrivate == !newlyPublic && badge.RemainingCard == (newlyPublic ? -1 : 3),
                    "Fresh privacy applies immediately and cannot invent a card count or erase previous cards.");
                Test.Assert(!policy.CanReuse(Account, Cards()), "Privacy changes invalidate the old complete-scan proof.");
            }
        }

        private static async Task FailedPrivacyCannotReuseAsync()
        {
            foreach (var status in new[] { SteamReadStatus.LoginRequired, SteamReadStatus.TransientFailure, SteamReadStatus.MalformedPage })
            {
                var policy = new ScanReusePolicy(() => TimeSpan.Zero);
                policy.RecordComplete(Account, Cards(), new int[0]);
                var reader = new FakePrivateReader { Failure = status };
                bool applied = false;
                var result = await policy.VerifyForStartAsync(Account, Cards(), false, reader, "synthetic-profile",
                    CancellationToken.None, ids => { applied = true; return Task.CompletedTask; });
                Test.Assert(result.Status == status && !applied && !policy.CanReuse(Account, Cards()),
                    "Expired login, network failure and malformed privacy preserve their failure type and invalidate reuse.");
            }
            foreach (var invalid in new[] { null, new HashSet<int> { 0 } })
            {
                var policy = new ScanReusePolicy(() => TimeSpan.Zero);
                policy.RecordComplete(Account, Cards(), new int[0]);
                var result = await policy.VerifyForStartAsync(Account, Cards(), false, new FakePrivateReader { Ids = invalid },
                    "synthetic-profile", CancellationToken.None, ids => Task.CompletedTask);
                Test.Assert(result.Status == SteamReadStatus.MalformedPage && !policy.CanReuse(Account, Cards()),
                    "Invalid successful privacy payloads cannot become an empty authenticated list.");
            }
        }

        private static async Task CanceledPrivacyCannotReuseAsync()
        {
            var policy = new ScanReusePolicy(() => TimeSpan.Zero);
            policy.RecordComplete(Account, Cards(), new int[0]);
            using (var cancellation = new CancellationTokenSource())
            {
                var reader = new FakePrivateReader { BeforeRead = () => cancellation.Cancel() };
                bool canceled = false, applied = false;
                try
                {
                    await policy.VerifyForStartAsync(Account, Cards(), false, reader, "synthetic-profile", cancellation.Token,
                        ids => { applied = true; return Task.CompletedTask; });
                }
                catch (OperationCanceledException) { canceled = true; }
                Test.Assert(canceled && !applied && !policy.CanReuse(Account, Cards()),
                    "Canceling a pending privacy check cannot reuse data or run its UI continuation.");
            }
        }

        private static async Task SlowPrivacyExpiresSnapshotAsync()
        {
            var now = TimeSpan.Zero;
            var policy = new ScanReusePolicy(() => now);
            policy.RecordComplete(Account, Cards(), new int[0]);
            now = TimeSpan.FromSeconds(119);
            var reader = new FakePrivateReader { BeforeRead = () => now += TimeSpan.FromSeconds(2) };
            var result = await policy.VerifyForStartAsync(Account, Cards(), false, reader, "synthetic-profile",
                CancellationToken.None, ids => Task.CompletedTask);
            Test.Assert(result.IsSuccess && !result.Value, "Snapshot freshness is checked again after a slow privacy read.");
            policy.RecordComplete(Account, Cards(), new int[0]);
            now -= TimeSpan.FromSeconds(1);
            Test.Assert(!policy.CanReuse(Account, Cards()), "A broken clock cannot make an invalid age reusable.");
        }

        private static void AccountQueueAndSignOutInvalidate()
        {
            var policy = new ScanReusePolicy(() => TimeSpan.Zero);
            policy.RecordComplete(Account, Cards(), new int[0]);
            Test.Assert(!policy.CanReuse(Account + 1, Cards()) && !policy.CanReuse(Account, Cards()),
                "Account changes invalidate proof even if the previous account is supplied again.");
            policy.RecordComplete(Account, Cards(), new int[0]);
            Test.Assert(!policy.CanReuse(Account, new ScanQueueSource(true, new[] { "20" })),
                "A card snapshot cannot stand in for a whitelist snapshot.");
            policy.RecordComplete(Account, new ScanQueueSource(true, new[] { "20" }), new int[0]);
            Test.Assert(!policy.CanReuse(Account, new ScanQueueSource(true, new[] { "21" })),
                "Changing whitelist IDs requires fresh queue construction.");
            policy.RecordComplete(Account, Cards(), new int[0]);
            policy.Invalidate();
            Test.Assert(!policy.CanReuse(Account, Cards()), "Sign-out and failed scans explicitly invalidate the old proof.");
        }

        private static void SettingsInputsAreCopiedAndClassified()
        {
            var ids = new[] { "20" };
            var blacklist = new[] { "10" };
            var before = new QueueSettingsSnapshot(false, true, false, false, false, "", ids, blacklist);
            ids[0] = "21"; blacklist[0] = "11";
            var original = new QueueSettingsSnapshot(false, true, false, false, false, "", new[] { "20" }, new[] { "10" });
            Test.Assert(!before.HasRunChanges(original), "Settings snapshots copy mutable whitelist and blacklist inputs.");
            var local = new QueueSettingsSnapshot(false, false, true, false, true, "mostcards", new[] { "20" }, new[] { "11" });
            Test.Assert(!before.RequiresFullScan(local) && before.HasRunChanges(local),
                "Card sorting, filtering and scheduling can use the existing complete card snapshot.");
            var whitelist = new QueueSettingsSnapshot(true, true, false, false, false, "", new[] { "20" }, new[] { "10" });
            var changedIds = new QueueSettingsSnapshot(false, true, false, false, false, "", new[] { "21" }, new[] { "10" });
            Test.Assert(before.RequiresFullScan(whitelist) && before.RequiresFullScan(changedIds),
                "Changing queue kind or whitelist IDs requires a new complete scan.");
        }

        private static void SettingsApplyWithoutAnotherScan()
        {
            var original = new Dictionary<string, object>();
            foreach (SettingsProperty property in Settings.Default.Properties) original[property.Name] = Settings.Default[property.Name];
            var culture = Thread.CurrentThread.CurrentUICulture;
            var context = SynchronizationContext.Current;
            SettingsSavingEventHandler preventSave = (sender, args) => args.Cancel = true;
            Settings.Default.SettingsSaving += preventSave;
            try
            {
                Settings.Default.language = "English";
                Settings.Default.IdlingModeWhitelist = false; Settings.Default.fastMode = true;
                Settings.Default.OnlyOneGameIdle = Settings.Default.OneThenMany = Settings.Default.IdleOnlyPlayed = false;
                Settings.Default.sort = ""; Settings.Default.blacklist = new StringCollection(); Settings.Default.whitelist = new StringCollection();
                using (var fixture = new DesktopFixture())
                {
                    fixture.Form.AllBadges.AddRange(new[] {
                        new Badge { AppId = 10, Name = "Unplayed", RemainingCard = 2, HoursPlayed = 0 },
                        new Badge { AppId = 20, Name = "Played", RemainingCard = 4, HoursPlayed = 1 } });
                    fixture.Set("scannedBadges", fixture.Form.AllBadges.ToList());
                    fixture.Set("ready", true); fixture.Set("authenticated", true); fixture.Set("snapshotSteamId", Account);
                    fixture.Policy.RecordComplete(Account, Cards(), new int[0]);
                    fixture.Save(() => { Settings.Default.customTheme = !Settings.Default.customTheme; Settings.Default.showUsername = !Settings.Default.showUsername; Settings.Default.NoSleep = !Settings.Default.NoSleep; });
                    Test.Assert(fixture.Get<bool>("ready") && fixture.Get<SteamReadStatus?>("lastFailure") == null &&
                        fixture.Policy.CanReuse(Account, Cards()) && fixture.Form.AllBadges.Sum(b => b.RemainingCard) == 6,
                        "Appearance and sleep saves preserve the complete queue and do not attempt another scan.");
                    fixture.Save(() => { Settings.Default.sort = "mostcards"; Settings.Default.IdleOnlyPlayed = true; });
                    Test.Assert(fixture.Form.AllBadges[0].AppId == 20 && fixture.Games().Count == 1 && fixture.Get<bool>("ready"),
                        "Sort and played-only changes apply locally without losing Start readiness.");
                    fixture.Save(() => Settings.Default.blacklist.Add("20"));
                    Test.Assert(fixture.Games().Count == 0 && fixture.Get<bool>("ready"), "Blacklisting the remaining game changes eligibility locally.");
                    fixture.Save(() => { Settings.Default.sort = ""; Settings.Default.IdleOnlyPlayed = false; Settings.Default.blacklist.Clear(); });
                    Test.Assert(fixture.Form.AllBadges[0].AppId == 10 && fixture.Games().Count == 2,
                        "Removing local filters and resetting sort restores the complete scan's original order.");
                    fixture.Save(() => Settings.Default.whitelist.Add("30"));
                    Test.Assert(!fixture.Get<bool>("ready") && !fixture.Policy.CanReuse(Account, Cards()),
                        "Changing whitelist IDs invalidates proof and attempts a full scan; an uninitialized synthetic session cannot succeed.");
                    Test.Assert(!fixture.Loaded && !fixture.Get<SteamSessionService>("session").IsInitialized,
                        "Settings probes never show the form, initialize sign-in, contact Steam or launch helpers.");
                }
            }
            finally
            {
                Settings.Default.SettingsSaving -= preventSave;
                foreach (var pair in original) Settings.Default[pair.Key] = pair.Value;
                Thread.CurrentThread.CurrentUICulture = culture;
                SynchronizationContext.SetSynchronizationContext(context);
            }
        }

        private sealed class FakePrivateReader : IPrivateGamesReader
        {
            internal HashSet<int> Ids = new HashSet<int>();
            internal SteamReadStatus? Failure;
            internal Action BeforeRead;
            internal int Reads;
            public Task<SteamReadResult<HashSet<int>>> ReadPrivateAsync(string profileUrl, CancellationToken token)
            {
                Reads++; BeforeRead?.Invoke(); token.ThrowIfCancellationRequested();
                return Task.FromResult(Failure.HasValue ? SteamReadResult<HashSet<int>>.Failed(Failure.Value, "Synthetic failure") :
                    SteamReadResult<HashSet<int>>.Succeeded(Ids));
            }
        }

        private sealed class DesktopFixture : IDisposable
        {
            internal readonly frmMain Form = new frmMain(() => true);
            internal bool Loaded;
            internal ScanReusePolicy Policy => Get<ScanReusePolicy>("scanReuse");
            internal DesktopFixture() { Form.Load += (sender, args) => Loaded = true; }
            internal T Get<T>(string name) => (T)typeof(frmMain).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Form);
            internal void Set(string name, object value) => typeof(frmMain).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(Form, value);
            internal List<IdleGame> Games() => (List<IdleGame>)typeof(frmMain).GetMethod("GamesForRun", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Form, null);
            internal void Save(Action action)
            {
                var edit = new Func<DialogResult>(() => { action(); return DialogResult.OK; });
                var task = (Task)typeof(frmMain).GetMethod("EditAndRefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Form, new object[] { edit });
                Test.Assert(task.IsCompleted, "A synthetic settings save must finish without any pending Steam request.");
                task.GetAwaiter().GetResult();
            }
            public void Dispose()
            {
                Get<System.Windows.Forms.Timer>("tmrCheckSteam").Stop();
                Get<System.Windows.Forms.Timer>("displayTimer").Dispose();
                Get<CancellationTokenSource>("lifetime").Cancel(); Get<CancellationTokenSource>("lifetime").Dispose();
                Get<HttpClient>("artworkClient").Dispose(); Get<SemaphoreSlim>("scanGate").Dispose();
                Get<SteamActivityMonitor>("steamActivity").Dispose(); Get<SteamSessionService>("session").Dispose();
                Get<Image>("darkTrue").Dispose(); Get<Image>("darkFalse").Dispose(); Form.Dispose();
            }
        }
    }
}
