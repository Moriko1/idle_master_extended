using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace IdleMasterExtended.Tests
{
    internal static class SteamActivityTests
    {
        private const ulong Account = 76561198000000001;
        private const string SteamExe = @"C:\Steam\steam.exe";

        public static void RunAll()
        {
            ClientAndAccountFailuresRemainDistinct();
            OwnHelpersAreNotUserGames();
            RealGamesSurviveUtilityParentsAndOwnAppHints();
            UnknownDoesNotMeanNoGame();
            TrackedProcessesIdentifySameAppAndIgnoreLeftoverLaunchers();
            ProcessLogReadsAreBoundedAndIncremental();
            RegistryCacheAvoidsRepeatedLibraryReads();
            MonitorContainsTransientReadFailuresAndDisposes();
        }

        private static void ClientAndAccountFailuresRemainDistinct()
        {
            var backend = new FakeBackend();
            using (var monitor = new SteamActivityMonitor(backend, 99))
            {
                var result = monitor.Read(Account, null, null);
                Test.Assert(result.Client == SteamClientPresence.Present &&
                    result.Account == SteamAccountPresence.Matches && result.Gameplay == SteamGameplayPresence.None,
                    "A clean matching client with no user games is idle.");
                backend.Registry.AccountId++;
                result = monitor.Read(Account, null, null);
                Test.Assert(result.Account == SteamAccountPresence.Changed && result.Gameplay == SteamGameplayPresence.Unknown,
                    "A confirmed account change never permits game activity from the previous account.");
                backend.Registry.AccountId = 0;
                result = monitor.Read(Account, null, null);
                Test.Assert(result.Client == SteamClientPresence.Present && result.Account == SteamAccountPresence.Unknown,
                    "A momentarily unreadable active account remains unknown, not changed.");
                backend.Registry.AccountId = unchecked((uint)Account);
                backend.Images[10] = @"C:\Unrelated\steam.exe";
                Test.Assert(monitor.Read(Account, null, null).Client == SteamClientPresence.Unknown,
                    "A reused or unrelated steam.exe PID does not verify the registry's client.");
                backend.Processes.Clear();
                Test.Assert(monitor.Read(Account, null, null).Client == SteamClientPresence.Absent,
                    "A successful process snapshot without Steam proves the client is closed.");
            }
        }

        private static void OwnHelpersAreNotUserGames()
        {
            var backend = new FakeBackend();
            backend.Registry.RunningAppIds = new[] { 100, 101 };
            backend.Processes.Add(new SteamProcessEntry(99, 10, "IdleMasterExtended.exe"));
            backend.Processes.Add(new SteamProcessEntry(20, 99, "steam-idle.exe"));
            backend.Processes.Add(new SteamProcessEntry(21, 20, "helper-child.exe"));
            backend.Processes.Add(new SteamProcessEntry(22, 10, "steam-idle.exe"));
            backend.Processes.Add(new SteamProcessEntry(23, 22, "helper-child.exe"));
            using (var monitor = new SteamActivityMonitor(backend, 99))
            {
                var result = monitor.Read(Account, new[] { 100, 101 }, new[] { 20, 22 });
                Test.Assert(result.Gameplay == SteamGameplayPresence.None && result.PlayingAppIds.Count == 0,
                    "Exact owned helper PIDs and their descendants, including the app's subtree, never count as gameplay.");
                backend.Registry.RunningAppIds = new[] { 100, 101, 102 };
                result = monitor.Read(Account, new[] { 100, 101 }, new[] { 20, 22 });
                Test.Assert(result.Gameplay == SteamGameplayPresence.Playing && result.PlayingAppIds.SequenceEqual(new[] { 102 }),
                    "A separate running app hint is retained without reporting our helper apps as user games.");
            }
        }

        private static void RealGamesSurviveUtilityParentsAndOwnAppHints()
        {
            var backend = new FakeBackend();
            backend.Registry.RunningAppIds = new[] { 100 };
            backend.Processes.Add(new SteamProcessEntry(20, 99, "steam-idle.exe"));
            backend.Processes.Add(new SteamProcessEntry(30, 10, "steamwebhelper.exe"));
            backend.Images[30] = @"C:\Steam\bin\cef\steamwebhelper.exe";
            backend.Processes.Add(new SteamProcessEntry(31, 30, "game-launcher.exe"));
            backend.Processes.Add(new SteamProcessEntry(32, 31, "actual-game.exe"));
            using (var monitor = new SteamActivityMonitor(backend, 99))
            {
                var result = monitor.Read(Account, new[] { 100 }, new[] { 20 });
                Test.Assert(result.Gameplay == SteamGameplayPresence.Playing,
                    "Launching a game already idled by us is still detected below a Steam utility parent.");
                backend.Processes.RemoveAll(process => process.Id == 31 || process.Id == 32);
                backend.Processes.Add(new SteamProcessEntry(33, 10, "GameOverlayUI.exe"));
                backend.Images[33] = @"C:\Steam\GameOverlayUI.exe";
                result = monitor.Read(Account, new[] { 100 }, new[] { 20 });
                Test.Assert(result.Gameplay == SteamGameplayPresence.None,
                    "Only Steam's verified client utilities do not keep gameplay active.");
                backend.Images[33] = @"C:\Steam\steamapps\common\Some Game\GameOverlayUI.exe";
                Test.Assert(monitor.Read(Account, new[] { 100 }, new[] { 20 }).Gameplay == SteamGameplayPresence.Playing,
                    "A game executable with a utility's filename is not ignored inside a game install directory.");
            }
        }

        private static void UnknownDoesNotMeanNoGame()
        {
            var backend = new FakeBackend();
            backend.Registry.RunningAppsKnown = false;
            using (var monitor = new SteamActivityMonitor(backend, 99))
            {
                Test.Assert(monitor.Read(Account, null, null).Gameplay == SteamGameplayPresence.Unknown,
                    "An incomplete registry read cannot establish that user gameplay ended.");
                backend.Processes.Add(new SteamProcessEntry(31, 10, "actual-game.exe"));
                Test.Assert(monitor.Read(Account, null, null).Gameplay == SteamGameplayPresence.Playing,
                    "Positive process evidence still detects gameplay during a registry outage.");
                backend.Processes.RemoveAll(process => process.Id == 31);
                backend.Registry.RunningAppsKnown = true;
                backend.Processes.Add(new SteamProcessEntry(32, 10, "steamwebhelper.exe"));
                Test.Assert(monitor.Read(Account, null, null).Gameplay == SteamGameplayPresence.Unknown,
                    "An inaccessible utility process image never becomes clean no-game evidence.");
                backend.Registry.ClientKnown = false;
                Test.Assert(monitor.Read(Account, null, null).Client == SteamClientPresence.Unknown,
                    "Micro-outages of client registry metadata are distinct from closing Steam.");
            }
        }

        private static void RegistryCacheAvoidsRepeatedLibraryReads()
        {
            var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var reads = 0;
            var current = (IReadOnlyList<int>)new[] { 100 };
            var fail = false;
            var cache = new RunningAppsCache(() =>
            {
                reads++;
                if (fail) throw new IOException("temporary registry failure");
                return current;
            }, () => instant);
            Test.Assert(cache.Read(false, true).SequenceEqual(new[] { 100 }) && cache.Known && reads == 1,
                "The first local library hint is read once.");
            for (var second = 1; second < 60; second++)
            {
                instant = instant.AddSeconds(1);
                cache.Read(false, true);
            }
            Test.Assert(reads == 1, "An unchanged background client does not repeatedly enumerate the library.");
            current = new[] { 200 };
            Test.Assert(cache.Read(true, true).SequenceEqual(new[] { 200 }) && reads == 2,
                "A registry change invalidates the cached running app list.");
            current = new[] { 300 };
            cache.Read(true, true);
            Test.Assert(reads == 2, "A burst of client registry updates is coalesced.");
            instant = instant.AddSeconds(2);
            Test.Assert(cache.Read(false, true).SequenceEqual(new[] { 300 }) && reads == 3,
                "The next eligible read sees the coalesced change.");
            instant = instant.AddSeconds(2);
            fail = true;
            Test.Assert(cache.Read(true, true).SequenceEqual(new[] { 300 }) && !cache.Known,
                "A failed library hint read preserves previous positive evidence and marks it unknown.");
            fail = false;
            current = new int[0];
            instant = instant.AddSeconds(2);
            Test.Assert(cache.Read(false, true).Count == 0 && cache.Known,
                "Only a successful later read can establish an empty running app list.");
            instant = instant.AddSeconds(5);
            cache.Read(false, false);
            var fallbackReads = reads;
            instant = instant.AddSeconds(5);
            cache.Read(false, false);
            Test.Assert(reads == fallbackReads + 1,
                "When notifications are unavailable a bounded fallback periodically rechecks activity.");
        }

        private static void TrackedProcessesIdentifySameAppAndIgnoreLeftoverLaunchers()
        {
            var backend = new FakeBackend();
            var tracked = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            backend.Registry.RunningAppIds = new[] { 100, 101 };
            backend.Processes.Add(new SteamProcessEntry(20, 99, "steam-idle.exe"));
            backend.Processes.Add(new SteamProcessEntry(21, 99, "steam-idle.exe"));
            // An already running third-party launcher creates the game outside Steam's parent tree.
            backend.Processes.Add(new SteamProcessEntry(30, 1, "third-party-launcher.exe"));
            backend.Processes.Add(new SteamProcessEntry(31, 30, "actual-game.exe"));
            backend.Starts[31] = tracked.AddSeconds(-1);
            backend.Tracked = new SteamTrackedGameSnapshot(true, true, new Dictionary<int, SteamTrackedGame>
            {
                { 20, new SteamTrackedGame(20, 100, tracked) },
                { 21, new SteamTrackedGame(21, 101, tracked) },
                { 31, new SteamTrackedGame(31, 100, tracked) }
            });
            using (var monitor = new SteamActivityMonitor(backend, 99))
            {
                var result = monitor.Read(Account, new[] { 100, 101 }, new[] { 20, 21 });
                Test.Assert(result.Gameplay == SteamGameplayPresence.Playing && result.PlayingAppIds.SequenceEqual(new[] { 100 }),
                    "Steam's tracked nonowned game PID identifies the same app while excluding both owned helpers.");
                backend.Starts[31] = tracked.AddMinutes(1);
                Test.Assert(monitor.Read(Account, new[] { 100, 101 }, new[] { 20, 21 }).Gameplay == SteamGameplayPresence.None,
                    "A reused PID from a later unrelated process does not prolong an old game session.");
                backend.Starts.Remove(31);
                Test.Assert(monitor.Read(Account, new[] { 100, 101 }, new[] { 20, 21 }).Gameplay == SteamGameplayPresence.Unknown,
                    "An inaccessible mapped game PID cannot prove that gameplay ended.");
                backend.Processes.RemoveAll(process => process.Id == 31);
                backend.Processes.Add(new SteamProcessEntry(32, 10, "persistent-launcher.exe"));
                backend.Processes.Add(new SteamProcessEntry(33, 10, "chrome.exe"));
                backend.Starts[32] = tracked.AddSeconds(5);
                backend.Tracked = new SteamTrackedGameSnapshot(true, true, new Dictionary<int, SteamTrackedGame>(),
                    new Dictionary<int, DateTimeOffset> { { 32, tracked.AddSeconds(10) } }, tracked);
                result = monitor.Read(Account, new[] { 100, 101 }, new[] { 20, 21 });
                Test.Assert(result.Gameplay == SteamGameplayPresence.None,
                    "A clean tracking log lets play return after the game ends while a launcher or Steam-opened browser stays open.");
                backend.Processes.Add(new SteamProcessEntry(34, 10, "long-running-game.exe"));
                backend.Starts[34] = tracked.AddDays(-1);
                Test.Assert(monitor.Read(Account, new[] { 100, 101 }, new[] { 20, 21 }).Gameplay == SteamGameplayPresence.Playing,
                    "A live Steam game started before the bounded log tail is still detected through its process graph.");
                backend.Processes.RemoveAll(process => process.Id == 34);
                // The ownership snapshot can predate a queued helper launch, but its parent PID proves ownership.
                backend.Tracked = new SteamTrackedGameSnapshot(true, true,
                    new Dictionary<int, SteamTrackedGame> { { 20, new SteamTrackedGame(20, 100, tracked) },
                        { 21, new SteamTrackedGame(21, 101, tracked) } },
                    new Dictionary<int, DateTimeOffset> { { 32, tracked.AddSeconds(10) } }, tracked);
                Test.Assert(monitor.Read(Account, new int[0], new int[0]).Gameplay == SteamGameplayPresence.None,
                    "A helper registered during an asynchronous activity read remains excluded through its app parent subtree.");
            }
        }

        private static void ProcessLogReadsAreBoundedAndIncremental()
        {
            var directory = Path.Combine(Path.GetTempPath(), "SteamActivityTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "gameprocess_log.txt");
                var reader = new SteamGameProcessLog();
                File.WriteAllText(path, "Steam client diagnostic log\n");
                Test.Assert(!reader.Read(path).Known, "Opening a log with no recognized tracking schema never proves no game.");
                reader = new SteamGameProcessLog();
                File.WriteAllText(path, "[2026-01-01 00:00:01] AppID 100 adding PID 200 as a tracked process \"C:\\Example\\game.exe --opaque-argument fake-secret\"\n");
                var first = reader.Read(path);
                Test.Assert(first.Known && first.Games.Count == 1 && first.Games[200].AppId == 100,
                    "A wellformed client tracking entry identifies its process and app.");
                File.AppendAllText(path, "[2026-01-01 00:00:02] AppID 100 no longer tracking PID 200, exit code 0 (role 1, result 0)\n" +
                    "[2026-01-01 00:00:03] AppID 101 adding PID ");
                var second = reader.Read(path);
                Test.Assert(second.Known && second.Games.Count == 0,
                    "An incomplete writer tail is held until its line completes, without discarding completed removal events.");
                File.AppendAllText(path, "201 as a tracked process\n");
                Test.Assert(reader.Read(path).Games.ContainsKey(201), "Incremental reads join the unfinished line once.");
                File.AppendAllText(path, "[2026-01-01 00:00:04] AppID invalid adding PID 202\n");
                var malformed = reader.Read(path);
                Test.Assert(!malformed.Known && malformed.Games.ContainsKey(201),
                    "A changed or malformed client log schema retains positive evidence and cannot claim no game.");
                File.WriteAllText(path, "[2026-01-01 00:00:05] AppID 102 adding PID 202 as a tracked process\n");
                var rotated = reader.Read(path);
                Test.Assert(rotated.Known && rotated.Games.Count == 1 && rotated.Games.ContainsKey(202),
                    "A truncated or rotated log resets old tracking records.");
                File.WriteAllText(path, new string('x', SteamGameProcessLog.MaximumReadBytes + 100) + "\n" +
                    "[2026-01-01 00:00:06] AppID 103 adding PID 203 as a tracked process\n");
                var bounded = new SteamGameProcessLog().Read(path);
                Test.Assert(bounded.Known && bounded.Games.Count == 1 && bounded.Games.ContainsKey(203),
                    "Initial reads are bounded to the last 256 KiB and safely discard the partial boundary line.");
                Test.Assert(reader.Read(Path.Combine(directory, "missing.txt")).Games.Count == 0,
                    "Changing the client log path never retains tracking records from another client.");
            }
            finally
            {
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var resolved = Path.GetFullPath(directory);
                if (!resolved.StartsWith(temp + "SteamActivityTests-", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected test cleanup path.");
                Directory.Delete(resolved, true);
            }
        }

        private static void MonitorContainsTransientReadFailuresAndDisposes()
        {
            var backend = new FakeBackend { ReadFailure = new Win32Exception(5) };
            var monitor = new SteamActivityMonitor(backend, 99);
            Test.Assert(monitor.Read(Account, null, null).Client == SteamClientPresence.Unknown,
                "Process-access failures are contained as unknown activity.");
            backend.ReadFailure = null;
            Test.Assert(monitor.Read(Account, null, null).Client == SteamClientPresence.Present,
                "A subsequent clean read recovers without requiring a new monitor.");
            var reads = backend.ProcessReads;
            monitor.Dispose();
            monitor.Dispose();
            Test.Assert(backend.DisposeCalls == 1 && monitor.Read(Account, null, null).Client == SteamClientPresence.Unknown &&
                reads == backend.ProcessReads, "Disposal closes backend handles once and prevents later reads.");
        }

        private sealed class FakeBackend : ISteamActivityBackend
        {
            internal readonly SteamRegistryActivity Registry = new SteamRegistryActivity
            {
                ClientKnown = true, ClientProcessId = 10, AccountId = unchecked((uint)Account),
                ExecutablePath = SteamExe, RunningAppsKnown = true, RunningAppIds = new int[0]
            };
            internal readonly List<SteamProcessEntry> Processes = new List<SteamProcessEntry>
            { new SteamProcessEntry(10, 1, "steam.exe") };
            internal readonly Dictionary<int, string> Images = new Dictionary<int, string> { { 10, SteamExe } };
            internal Exception ReadFailure;
            internal SteamTrackedGameSnapshot Tracked;
            internal readonly Dictionary<int, DateTimeOffset> Starts = new Dictionary<int, DateTimeOffset>();
            internal int DisposeCalls, ProcessReads;
            public SteamRegistryActivity ReadRegistry() { return Registry; }
            public IReadOnlyList<SteamProcessEntry> ReadProcesses()
            { ProcessReads++; if (ReadFailure != null) throw ReadFailure; return Processes; }
            public string ReadProcessImagePath(int processId)
            { string result; return Images.TryGetValue(processId, out result) ? result : null; }
            public DateTimeOffset? ReadProcessStartTime(int processId)
            { DateTimeOffset result; return Starts.TryGetValue(processId, out result) ? result : (DateTimeOffset?)null; }
            public SteamTrackedGameSnapshot ReadTrackedGames(string steamExecutable) { return Tracked; }
            public void Dispose() { DisposeCalls++; }
        }
    }
}
