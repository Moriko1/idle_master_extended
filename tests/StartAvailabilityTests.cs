using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using IdleMasterExtended.Properties;

namespace IdleMasterExtended.Tests
{
    internal static class StartAvailabilityTests
    {
        public static void RunAll()
        {
            var original = new Dictionary<string, object>();
            foreach (SettingsProperty property in Settings.Default.Properties)
                original[property.Name] = Settings.Default[property.Name];
            var saved = false;
            SettingsSavingEventHandler preventSave = (sender, args) => { saved = true; args.Cancel = true; };
            Settings.Default.SettingsSaving += preventSave;
            try
            {
                Settings.Default.IdlingModeWhitelist = false;
                Settings.Default.fastMode = false;
                Settings.Default.OnlyOneGameIdle = true;
                Settings.Default.OneThenMany = false;
                Settings.Default.IdleOnlyPlayed = false;
                Settings.Default.blacklist = new StringCollection();
                Settings.Default.whitelist = new StringCollection();
                using (var fixture = new Fixture())
                {
                    fixture.Games(new Badge { AppId = 10, Name = "Synthetic game", RemainingCard = 3, HoursPlayed = 2 });
                    fixture.AssertEnabled(true, "A verified card-dropping game enables Start.");
                    fixture.Games();
                    fixture.AssertEnabled(false, "A successfully scanned empty account cannot start.");
                    fixture.Games(new Badge { AppId = 10, Name = "Completed game", RemainingCard = 0, HoursPlayed = 2 });
                    fixture.AssertEnabled(false, "A completed card queue cannot start.");
                    fixture.Games(new Badge { AppId = 10, Name = "Unknown card count", RemainingCard = -1, HoursPlayed = 2 });
                    fixture.AssertEnabled(false, "An unknown card count is ineligible outside whitelist mode.");

                    fixture.Games(new Badge { AppId = 10, Name = "Synthetic game", RemainingCard = 3, HoursPlayed = 2 });
                    Settings.Default.blacklist = Collection("10");
                    fixture.AssertEnabled(false, "A blacklist that excludes the entire queue disables Start.");
                    Settings.Default.blacklist = new StringCollection();
                    Settings.Default.IdleOnlyPlayed = true;
                    fixture.Games(new Badge { AppId = 10, Name = "Unplayed game", RemainingCard = 3, HoursPlayed = 0 });
                    fixture.AssertEnabled(false, "Played-only filtering can leave no eligible games.");
                    fixture.Games(new Badge { AppId = 10, Name = "Played game", RemainingCard = 3, HoursPlayed = 0.1 });
                    fixture.AssertEnabled(true, "A previously played game remains eligible.");

                    Settings.Default.IdlingModeWhitelist = true;
                    Settings.Default.whitelist = Collection("20");
                    fixture.Games(new Badge { AppId = 20, Name = "Whitelisted game", RemainingCard = -1, HoursPlayed = 0 });
                    fixture.AssertEnabled(true, "A nonempty explicit whitelist can idle without card counts or prior playtime.");
                    fixture.Games();
                    fixture.AssertEnabled(false, "An empty scanned whitelist cannot start.");
                    Settings.Default.blacklist = Collection("20");
                    fixture.Games(new Badge { AppId = 20, Name = "Whitelisted game", RemainingCard = -1, HoursPlayed = 0 });
                    fixture.AssertEnabled(false, "A blacklisted whitelist entry cannot start.");

                    Settings.Default.IdlingModeWhitelist = false;
                    Settings.Default.IdleOnlyPlayed = false;
                    Settings.Default.blacklist = new StringCollection();
                    fixture.Games(new Badge { AppId = 10, Name = "Synthetic game", RemainingCard = 3, HoursPlayed = 2 });
                    fixture.Set("ready", false);
                    fixture.AssertEnabled(false, "Start is disabled while the scan has no verified result.");
                    fixture.Set("ready", true);
                    fixture.Set("busy", true);
                    fixture.AssertEnabled(false, "Start is disabled while an action or scan is in progress.");
                    fixture.Set("busy", false);
                    fixture.Set("authenticated", false);
                    fixture.AssertEnabled(false, "An expired login cannot start from preserved counts.");
                    fixture.Set("authenticated", true);
                    fixture.SetCurrent(null);
                    fixture.AssertEnabled(false, "Authentication flags alone cannot start without a verified account.");
                    fixture.SetCurrent(new SteamSession(76561198000000001, "Synthetic account"));
                    fixture.ClientRunning = false;
                    fixture.AssertEnabled(false, "A stopped Steam client disables Start.");
                    fixture.ClientRunning = true;
                    foreach (var state in new[] { IdleRunState.Starting, IdleRunState.Running })
                    {
                        fixture.Set("runStatus", new IdleRunStatus(state, IdleMode.Single,
                            new[] { new IdleGame(10, "Synthetic game", 3, 2) },
                            new[] { new IdleGame(10, "Synthetic game", 3, 2) }, null, null));
                        fixture.AssertEnabled(false, "An existing " + state + " session cannot start twice.");
                    }
                    fixture.Set("runStatus", new IdleRunStatus(IdleRunState.Paused, IdleMode.Single,
                        new IdleGame[0], new[] { new IdleGame(10, "Synthetic game", 3, 2) }, null, null));
                    fixture.AssertEnabled(true, "A manually paused session with eligible games can resume.");
                    fixture.Games();
                    fixture.AssertEnabled(false, "A paused session with no eligible games cannot resume.");
                    Test.Assert(!fixture.LoadFired, "Availability checks must not fire desktop startup.");
                    Test.Assert(!fixture.Session.IsInitialized, "Availability checks must not open a Steam browser.");
                }
                Test.Assert(!saved, "Availability checks must not persist settings.");
            }
            finally
            {
                Settings.Default.SettingsSaving -= preventSave;
                foreach (var entry in original) Settings.Default[entry.Key] = entry.Value;
            }
        }

        private static StringCollection Collection(params string[] values)
        {
            var result = new StringCollection();
            result.AddRange(values);
            return result;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly frmMain form;
            internal bool ClientRunning = true;
            internal bool LoadFired;
            internal SteamSessionService Session => Field<SteamSessionService>("session");

            internal Fixture()
            {
                form = new frmMain(() => ClientRunning);
                form.Load += (sender, args) => LoadFired = true;
                Set("authenticated", true);
                Set("ready", true);
                SetCurrent(new SteamSession(76561198000000001, "Synthetic account"));
            }

            internal void Games(params Badge[] badges)
            {
                form.AllBadges.Clear();
                form.AllBadges.AddRange(badges);
            }

            internal void SetCurrent(SteamSession account)
            {
                typeof(SteamSessionService).GetProperty("Current",
                    BindingFlags.Instance | BindingFlags.Public).SetValue(Session, account);
            }

            internal void Set(string name, object value)
            {
                typeof(frmMain).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, value);
            }

            internal void AssertEnabled(bool expected, string message)
            {
                typeof(frmMain).GetMethod("UpdateButtons", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, null);
                Test.Assert(Field<Button>("btnStart").Enabled == expected, message);
            }

            private T Field<T>(string name)
            {
                return (T)typeof(frmMain).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            }

            public void Dispose()
            {
                // Never Show, Close, or pump messages: those would invoke startup or
                // shutdown paths. Dispose all resources created by the constructor.
                Field<System.Windows.Forms.Timer>("displayTimer").Stop();
                Field<System.Windows.Forms.Timer>("displayTimer").Dispose();
                var cancellation = Field<CancellationTokenSource>("lifetime");
                cancellation.Cancel();
                cancellation.Dispose();
                Field<HttpClient>("artworkClient").Dispose();
                Field<SemaphoreSlim>("scanGate").Dispose();
                Session.Dispose();
                Field<System.Drawing.Image>("darkTrue").Dispose();
                Field<System.Drawing.Image>("darkFalse").Dispose();
                form.Dispose();
            }
        }
    }
}
