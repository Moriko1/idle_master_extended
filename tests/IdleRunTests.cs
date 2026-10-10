using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended.Tests
{
    internal static class IdleRunTests
    {
        private const ulong SteamId = 76561198000000001;

        public static async Task RunAllAsync()
        {
            await PrioritizesReadyThenManyAsync();
            await CapsWhitelistWithoutCardCompletionAsync();
            await WhitelistRefreshRemovesExcludedGamesAsync();
            await PrivateExclusionsSurviveFailedScansAsync();
            await AllPrivateFailedScanWaitsForSuccessAsync();
            await PrivatePendingLaunchIsCanceledAsync();
            await ConcurrentStopAndPrivacyUpdateStayStoppedAsync();
            await PauseCancelsFastTransitionsAsync();
            await SkipSurvivesRefreshAsync();
            await ResumeRescanPreservesSkippedAsync();
            await HelperFailureReleasesPeersAsync();
            await ScanFailurePreservesQueueAsync();
            await RecoverableScansKeepHelpersAndRetryAsync();
            await InvalidScanRetriesWithoutCompletingAsync();
            await PauseAndStopCancelScanRetriesAsync();
            await ExpiredLoginStopsSafelyAsync();
            await EmptyScanCompletesAsync();
            await HelperFailureDuringScanAsync();
            await PauseCancelsPendingScanAsync();
            await LateHelperIsReleasedAsync();
            await WrongAccountNeverRunsAsync();
            await RapidRepeatedControlsSerializeAsync();
            await MissingHelperIsReportedAsync();
            await GameplayPreparesAcrossModesAsync();
            await PreparationStopsAtLocalDeadlinesAsync();
            await PreparationDeadlineSurvivesPendingFailedScanAsync();
            await PreparationDeadlinesWatchPendingStartupAsync();
            await GameplayCapsPreparationAndKeepsQueueAsync();
            await GameplayInterruptsFastGapAsync();
            await GameplayCancelsPendingLaunchAsync();
            await GameplayChangesNeverResumeManualPauseAsync();
            await RecoverableHelpersRestartWithoutPausingAsync();
            await RecoverableStartupRetriesAndStopCancelsAsync();
            await PersistentInitializationFailureNeedsRecoveryAsync();
            await TypedAccountMismatchStopsSafelyAsync();
            OwnedJobClosesOnlyItsChild();
        }

        private static async Task GameplayPreparesAcrossModesAsync()
        {
            foreach (IdleMode mode in Enum.GetValues(typeof(IdleMode)))
            {
                var factory = new FakeFactory();
                var clock = new AdvancingClock();
                var games = mode == IdleMode.Whitelist
                    ? new[] { Game(1, -1), Game(2, -1), Game(3, -1) }
                    : new[] { Game(1, 3, 2), Game(2, 3, 0.5), Game(3, 3, 0.5) };
                using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
                {
                    run.UpdateGameActivity(new[] { 3 }, true);
                    await run.StartAsync(games, SteamId, mode);
                    await Until(() => run.Snapshot.State == IdleRunState.Running && clock.PendingCount > 0,
                        "gameplay preparation for " + mode);
                    Assert(run.Snapshot.GameplayActive && run.Snapshot.RemainingGames.Count == 3,
                        "Gameplay must retain the verified card queue in every mode.");
                    Assert(mode == IdleMode.Whitelist ? factory.ActiveCount == 0 :
                        factory.ActiveCount == 1 && factory.IsActive(2),
                        "Only young card games may prepare during gameplay; the actual game and unknown whitelist hours wait.");
                    run.UpdateGameActivity(new int[0], false);
                    await Until(() => !run.Snapshot.GameplayActive && (mode == IdleMode.ManyThenOne
                        ? factory.IsActive(2) && factory.IsActive(3) : factory.IsActive(1)),
                        "automatic normal policy after gameplay for " + mode);
                    Assert(run.Snapshot.State != IdleRunState.Paused && factory.MaximumActive <= 30,
                        "Ending gameplay resumes the chosen mode automatically without a manual control.");
                    await run.StopAsync();
                    Assert(factory.ActiveCount == 0 && clock.PendingCount == 0,
                        "Stop must cancel both preparation deadlines and ordinary waits.");
                }
            }
        }

        private static async Task PreparationStopsAtLocalDeadlinesAsync()
        {
            var factory = new FakeFactory();
            var clock = new AdvancingClock();
            var games = new[] { Game(1, 3, 1.99), Game(2, 4, 1.98) };
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
            {
                run.UpdateGameActivity(new int[0], true);
                await run.StartAsync(games, SteamId, IdleMode.Fast);
                await Until(() => factory.ActiveCount == 2 && clock.PendingCount == 2, "two preparation deadlines");
                clock.ShiftWallClock(TimeSpan.FromDays(-1));
                clock.AdvanceNext();
                await Until(() => !factory.IsActive(1) && factory.IsActive(2), "first game reaches two hours");
                Assert(run.Snapshot.RemainingGames.Count == 2 && run.Snapshot.RemainingGames.Sum(game => game.RemainingCards) == 7,
                    "A preparation threshold cannot invent card drops or remove a queued game.");
                clock.ShiftWallClock(TimeSpan.FromDays(2));
                run.UpdateGameActivity(new[] { 999 }, true);
                await Until(() => factory.IsActive(2) && clock.PendingCount == 2, "monotonic budget after clock correction");
                Assert(factory.StartCount == 2,
                    "Wall-clock changes must not extend, shorten, or restart a preparation budget.");
                clock.AdvanceNext();
                await Until(() => factory.ActiveCount == 0 && clock.PendingCount == 1, "second preparation threshold");
                clock.AdvanceNext();
                await Until(() => clock.PendingCount == 1 && run.Snapshot.State == IdleRunState.Running,
                    "stale badge hours after preparation");
                Assert(factory.StartCount == 2 && run.Snapshot.GameplayActive && run.Snapshot.ActiveGames.Count == 0,
                    "Stale scan hours must not restart warmed games, cycle solos, or claim completion while the user plays.");
                run.UpdateGameActivity(new int[0], false);
                await Until(() => factory.ActiveCount == 2, "normal fast mode after all games warm");
                await run.StopAsync();
            }
        }

        private static async Task PreparationDeadlineSurvivesPendingFailedScanAsync()
        {
            var factory = new FakeFactory();
            var clock = new AdvancingClock();
            var games = new[] { Game(1, 3, 1.8) };
            var scan = new TaskCompletionSource<IReadOnlyList<IdleGame>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = false;
            using (var run = new IdleRunController(factory, token => { entered = true; return scan.Task; }, clock))
            {
                run.UpdateGameActivity(new int[0], true);
                await run.StartAsync(games, SteamId, IdleMode.Single);
                await Until(() => factory.ActiveCount == 1 && clock.PendingCount == 2, "pending scan preparation setup");
                clock.AdvanceNext();
                await Until(() => entered && clock.PendingCount == 1, "HTTP scan pending before warmup deadline");
                clock.AdvanceNext();
                await Until(() => factory.ActiveCount == 0, "warmup ends while HTTP scan is pending");
                Assert(!scan.Task.IsCompleted && run.Snapshot.RemainingGames.Single().RemainingCards == 3,
                    "An unresponsive scan cannot leave a game idling beyond the preparation budget.");
                scan.TrySetException(new IdleRefreshException(SteamReadStatus.TransientFailure, "Synthetic timeout"));
                await Until(() => clock.PendingCount == 1 && run.Snapshot.ReadFailure == SteamReadStatus.TransientFailure,
                    "failed scan after preparation deadline");
                Assert(factory.StartCount == 1 && run.Snapshot.State == IdleRunState.Running,
                    "A failed read after preparation must retain the queue and keep warmed helpers stopped.");
                await run.StopAsync();
            }
        }

        private static async Task GameplayCapsPreparationAndKeepsQueueAsync()
        {
            var factory = new FakeFactory();
            var clock = new AdvancingClock();
            var games = Enumerable.Range(1, 31).Select(id => Game(id, 2, 1.999)).ToArray();
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
            {
                run.UpdateGameActivity(new int[0], true);
                await run.StartAsync(games, SteamId, IdleMode.Single);
                await Until(() => factory.ActiveCount == 30 && clock.PendingCount == 2, "thirty-game preparation cap");
                Assert(factory.MaximumActive == 30 && run.Snapshot.RemainingGames.Count == 31,
                    "All modes use at most thirty preparation helpers while additional games remain queued.");
                clock.AdvanceNext();
                await Until(() => factory.IsActive(31) && clock.PendingCount == 2, "remaining preparation game");
                Assert(factory.StartCount == 31 && factory.MaximumActive == 30,
                    "Stale badge data cannot restart the first thirty games instead of preparing the remaining game.");
                clock.AdvanceNext();
                await Until(() => factory.ActiveCount == 0, "last preparation game stops");
                Assert(run.Snapshot.State == IdleRunState.Running && run.Snapshot.RemainingGames.Count == 31,
                    "Completing preparation cannot complete a positive card queue.");
                await run.StopAsync();
            }
        }

        private static async Task PreparationDeadlinesWatchPendingStartupAsync()
        {
            var factory = new FakeFactory();
            var clock = new AdvancingClock();
            var first = new FakeHelper(1, SteamId, null);
            var late = new TaskCompletionSource<IIdleHelper>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingToken = default(CancellationToken);
            var released = 0;
            factory.StartOverride = (app, steam, token) =>
            {
                if (app == 1) return Task.FromResult<IIdleHelper>(first);
                pendingToken = token;
                return late.Task;
            };
            var games = new[] { Game(1, 3, 1.999), Game(2, 4, 1.998) };
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
            {
                run.UpdateGameActivity(new int[0], true);
                await run.StartAsync(games, SteamId, IdleMode.ManyThenOne);
                await Until(() => factory.Attempts == 2 && clock.PendingCount == 1 && first.IsRunning,
                    "second helper startup pending");
                clock.AdvanceNext();
                await Until(() => !first.IsRunning && clock.PendingCount == 1, "first warmup cap during later startup");
                Assert(!late.Task.IsCompleted && !pendingToken.IsCancellationRequested,
                    "An accepted helper must stop at its own cap while a later helper is still initializing.");
                clock.AdvanceNext();
                await Until(() => pendingToken.IsCancellationRequested && run.Snapshot.State == IdleRunState.Running &&
                    clock.PendingCount == 1, "pending helper reaches its conservative warmup cap");
                Assert(run.Snapshot.ActiveGames.Count == 0 && run.Snapshot.RemainingGames.Count == 2 && factory.Attempts == 2,
                    "Startup time consumes the preparation budget and cannot become card completion or repeated warmup launches.");
                late.TrySetResult(new FakeHelper(2, SteamId, () => Interlocked.Increment(ref released)));
                await Until(() => released == 1, "late readiness after preparation startup deadline");
                await run.StopAsync();
            }
        }

        private static async Task GameplayInterruptsFastGapAsync()
        {
            var factory = new FakeFactory();
            var clock = new AdvancingClock();
            var games = new[] { Game(1, 3, 2), Game(2, 3, 0.5) };
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
            {
                await run.StartAsync(games, SteamId, IdleMode.Fast);
                await Until(() => factory.ActiveCount == 2 && clock.PendingCount == 1, "fast pre-game batch");
                clock.AdvanceNext();
                await Until(() => factory.ActiveCount == 0 && clock.PendingCount == 1, "fast pre-game solo gap");
                run.UpdateGameActivity(new[] { 1 }, true);
                await Until(() => factory.IsActive(2) && clock.PendingCount == 2, "gameplay interrupts fast solo gap");
                Assert(!factory.IsActive(1) && run.Snapshot.GameplayActive && run.Snapshot.State == IdleRunState.Running,
                    "Gameplay must cancel a queued fast solo transition and prepare only eligible games immediately.");
                await run.StopAsync();
            }
        }

        private static async Task GameplayCancelsPendingLaunchAsync()
        {
            var factory = new FakeFactory();
            var clock = new AdvancingClock();
            var pending = new TaskCompletionSource<IIdleHelper>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingToken = default(CancellationToken);
            var released = 0;
            var retained = new FakeHelper(2, SteamId, null);
            factory.StartOverride = (app, steam, token) =>
            {
                if (app == 1) { pendingToken = token; return pending.Task; }
                return Task.FromResult<IIdleHelper>(retained);
            };
            var games = new[] { Game(1), Game(2) };
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
            {
                await run.StartAsync(games, SteamId, IdleMode.ManyThenOne);
                await Until(() => factory.Attempts == 1, "launch pending before actual game");
                run.UpdateGameActivity(new[] { 1 }, true);
                await Until(() => retained.IsRunning && factory.Attempts == 2 && clock.PendingCount == 2,
                    "preparation continues after canceled game launch");
                Assert(pendingToken.IsCancellationRequested && run.Snapshot.ActiveGames.Single().AppId == 2,
                    "Launching the same game must cancel the pending helper and retain unrelated preparation.");
                pending.TrySetResult(new FakeHelper(1, SteamId, () => Interlocked.Increment(ref released)));
                await Until(() => released == 1, "late gameplay helper disposal");
                Assert(run.Snapshot.State == IdleRunState.Running && run.Snapshot.RemainingGames.Count == 2,
                    "Late readiness cannot crash or overwrite gameplay policy.");
                await run.StopAsync();
                Assert(!retained.IsRunning && clock.PendingCount == 0, "Stop cancels remaining preparation and waits.");
            }
        }

        private static async Task GameplayChangesNeverResumeManualPauseAsync()
        {
            var factory = new FakeFactory();
            var clock = new AdvancingClock();
            var games = new[] { Game(1) };
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
            {
                await run.StartAsync(games, SteamId, IdleMode.Single);
                await Until(() => factory.ActiveCount == 1 && clock.PendingCount == 1, "manual pause before game changes");
                await run.PauseAsync("Steam was closed.");
                var starts = factory.StartCount;
                for (var index = 0; index < 16; index++)
                    run.UpdateGameActivity(index % 2 == 0 ? new[] { 1 } : new int[0], index % 2 == 0);
                await Task.Delay(30);
                Assert(run.Snapshot.State == IdleRunState.Paused && run.Snapshot.Error == "Steam was closed." &&
                    factory.ActiveCount == 0 && factory.StartCount == starts && clock.PendingCount == 0,
                    "Gameplay changes cannot undo a manual or confirmed-client-exit pause.");
                await run.StopAsync();
                run.UpdateGameActivity(new int[0], true);
                run.UpdateGameActivity(new int[0], false);
                Assert(run.Snapshot.State == IdleRunState.Stopped && factory.ActiveCount == 0,
                    "Ending gameplay cannot restart a stopped session.");
            }
        }

        private static async Task RecoverableHelpersRestartWithoutPausingAsync()
        {
            foreach (var failure in new[] { IdleHelperFailure.SteamUnavailable, IdleHelperFailure.UnexpectedExit })
            {
                var factory = new FakeFactory();
                var clock = new FakeClock();
                var games = new[] { Game(1, 3), Game(2, 4) };
                using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
                {
                    await run.StartAsync(games, SteamId, IdleMode.ManyThenOne);
                    await Until(() => factory.ActiveCount == 2 && clock.PendingCount == 1, "recoverable helper setup");
                    factory.Fail(1, failure);
                    await Until(() => factory.ActiveCount == 0 && clock.PendingCount == 1 && run.Snapshot.Error != null,
                        "recoverable helper retry " + failure);
                    Assert(run.Snapshot.State == IdleRunState.Running && run.Snapshot.RemainingGames.Sum(game => game.RemainingCards) == 7 &&
                        clock.NextDelay == TimeSpan.FromSeconds(5),
                        "Brief helper failures retry automatically, retaining the previous verified queue.");
                    clock.ReleaseNext();
                    await Until(() => factory.ActiveCount == 2 && clock.PendingCount == 1 && factory.StartCount == 4,
                        "automatic helper recovery " + failure);
                    Assert(run.Snapshot.Error == null && !factory.DuplicateActive,
                        "Successful recovery clears the transient status without overlapping helpers.");
                    await run.StopAsync();
                }
            }
        }

        private static async Task RecoverableStartupRetriesAndStopCancelsAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            factory.StartOverride = (app, steam, token) => Task.FromException<IIdleHelper>(
                new IdleHelperException("Synthetic initialization failure", IdleHelperFailure.InitializationFailed));
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(new[] { Game(1) }), clock))
            {
                await run.StartAsync(new[] { Game(1) }, SteamId, IdleMode.Single);
                await Until(() => factory.Attempts == 1 && clock.PendingCount == 1, "initialization retry");
                Assert(run.Snapshot.State == IdleRunState.Running && clock.NextDelay == TimeSpan.FromSeconds(5),
                    "Transient Steam initialization failures must not demand a manual restart.");
                clock.ReleaseNext();
                await Until(() => factory.Attempts == 2 && clock.PendingCount == 1, "second initialization retry");
                Assert(clock.NextDelay == TimeSpan.FromSeconds(10), "Repeated initialization failures use a bounded backoff.");
                await run.StopAsync();
                var attempts = factory.Attempts;
                await Task.Delay(30);
                Assert(run.Snapshot.State == IdleRunState.Stopped && clock.PendingCount == 0 && factory.Attempts == attempts,
                    "Manual Stop must cancel retry delays and prevent late relaunches.");
            }
        }

        private static async Task TypedAccountMismatchStopsSafelyAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(new[] { Game(1) }), clock))
            {
                await run.StartAsync(new[] { Game(1) }, SteamId, IdleMode.Single);
                await Until(() => factory.ActiveCount == 1 && clock.PendingCount == 1, "typed mismatch setup");
                factory.Fail(1, IdleHelperFailure.AccountMismatch);
                await Until(() => run.Snapshot.State == IdleRunState.Faulted, "typed account mismatch");
                Assert(factory.ActiveCount == 0 && clock.PendingCount == 0 && factory.StartCount == 1 &&
                    run.Snapshot.RemainingGames.Single().RemainingCards == 2,
                    "An account mismatch cannot auto-retry or discard the retained card counts.");
            }
        }

        private static async Task PersistentInitializationFailureNeedsRecoveryAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            factory.StartOverride = (app, steam, token) => Task.FromException<IIdleHelper>(
                new IdleHelperException("Synthetic unavailable game", IdleHelperFailure.InitializationFailed));
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(
                new[] { Game(1, 3), Game(2, 4) }), clock))
            {
                await run.StartAsync(new[] { Game(1, 3), Game(2, 4) }, SteamId, IdleMode.ManyThenOne);
                for (var attempt = 1; attempt <= 5; attempt++)
                {
                    var expected = attempt;
                    await Until(() => factory.Attempts == expected && clock.PendingCount == 1,
                        "bounded unavailable-game retry " + attempt);
                    Assert(run.Snapshot.State == IdleRunState.Running, "Brief initialization failures retry automatically.");
                    clock.ReleaseNext();
                }
                await Until(() => factory.Attempts == 6 && run.Snapshot.State == IdleRunState.Faulted,
                    "persistent unavailable game recovery state");
                Assert(clock.PendingCount == 0 && factory.ActiveCount == 0 &&
                    run.Snapshot.RemainingGames.Sum(game => game.RemainingCards) == 7 &&
                    run.Snapshot.Error.Contains("several retries"),
                    "A persistently unavailable game must retain counts and request recovery instead of retrying forever or completing.");
            }
        }

        private static IdleGame Game(int id, int cards = 2, double hours = 0)
        {
            return new IdleGame(id, "Game " + id, cards, hours);
        }

        private static async Task PrioritizesReadyThenManyAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var young = new[] { Game(1), Game(2) };
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(young), clock))
            {
                await run.StartAsync(young.Concat(new[] { Game(3, 2, 2) }), SteamId, IdleMode.OneThenMany);
                await Until(() => clock.PendingCount == 1, "initial solo phase");
                Assert(run.Snapshot.ActiveGames.Single().AppId == 3, "One-then-many must first select a game with two hours.");
                Assert(clock.NextDelay == TimeSpan.FromMinutes(15), "A solo with multiple drops checks after fifteen minutes.");
                clock.ReleaseNext();
                await Until(() => clock.PendingCount == 1 && factory.StartCount == 3, "young-game batch");
                Assert(run.Snapshot.ActiveGames.Count == 2, "After ready games complete, young games idle together.");
                Assert(clock.NextDelay == TimeSpan.FromMinutes(6), "A normal batch checks after six minutes.");
                await run.StopAsync();
                Assert(factory.ActiveCount == 0 && clock.PendingCount == 0, "Stop releases helpers and cancels the delay.");
            }
        }

        private static async Task CapsWhitelistWithoutCardCompletionAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var scans = 0;
            var whitelist = Enumerable.Range(1, 31).Select(id => Game(id, -1)).ToArray();
            using (var run = new IdleRunController(factory, token =>
            {
                Interlocked.Increment(ref scans);
                return Task.FromResult<IReadOnlyList<IdleGame>>(whitelist);
            }, clock))
            {
                await run.StartAsync(whitelist, SteamId, IdleMode.Whitelist);
                await Until(() => clock.PendingCount == 1, "whitelist batch");
                Assert(factory.ActiveCount == 30 && factory.MaximumActive == 30, "The helper limit is thirty, not thirty-one.");
                Assert(run.Snapshot.RemainingGames.Count == 31, "Games beyond the concurrency limit remain queued.");
                clock.ReleaseNext();
                await Until(() => clock.PendingCount == 1 && factory.ActiveCount == 30 && scans == 1, "next whitelist phase");
                Assert(factory.StartCount == 30, "An unchanged whitelist keeps its existing helpers.");
                Assert(scans == 1 && run.Snapshot.State == IdleRunState.Running,
                    "Whitelist idling does not auto-complete from card counts.");
                await run.StopAsync();
                Assert(factory.ActiveCount == 0, "All whitelist helpers stop.");
            }
        }

        private static async Task WhitelistRefreshRemovesExcludedGamesAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var initial = new[] { Game(1, -1), Game(2, -1) };
            var scans = 0;
            using (var run = new IdleRunController(factory, token =>
            {
                token.ThrowIfCancellationRequested();
                var scan = Interlocked.Increment(ref scans);
                return Task.FromResult<IReadOnlyList<IdleGame>>(scan == 1
                    ? new[] { Game(2, -1) } : new IdleGame[0]);
            }, clock))
            {
                await run.StartAsync(initial, SteamId, IdleMode.Whitelist);
                await Until(() => clock.PendingCount == 1 && factory.ActiveCount == 2, "initial privacy-checked whitelist");
                Assert(clock.NextDelay == TimeSpan.FromMinutes(6), "Whitelist privacy checks use the normal six-minute interval.");
                clock.ReleaseNext();
                await Until(() => clock.PendingCount == 1 && scans == 1 && factory.ActiveCount == 1,
                    "private game removed from active whitelist");
                Assert(!factory.IsActive(1) && factory.IsActive(2),
                    "Removing a newly private game must dispose only its helper.");
                Assert(factory.StartCount == 2 && run.Snapshot.State == IdleRunState.Running
                    && run.Snapshot.ActiveGames.Single().AppId == 2 && run.Snapshot.RemainingGames.Single().AppId == 2,
                    "A remaining whitelist helper must continue without relaunching or card completion.");
                clock.ReleaseNext();
                await Until(() => run.Snapshot.State == IdleRunState.Completed && scans == 2,
                    "entire whitelist excluded by verified private-game policy");
                Assert(factory.ActiveCount == 0 && clock.PendingCount == 0 && factory.StartCount == 2,
                    "An all-excluded whitelist must complete and release every helper and delay.");
            }
        }

        private static async Task PrivateExclusionsSurviveFailedScansAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var initial = new[] { Game(1, 3), Game(2, 3) };
            var firstScan = new TaskCompletionSource<IReadOnlyList<IdleGame>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scans = 0;
            using (var run = new IdleRunController(factory, token =>
            {
                var scan = Interlocked.Increment(ref scans);
                return scan == 1 ? firstScan.Task : Task.FromResult<IReadOnlyList<IdleGame>>(initial);
            }, clock))
            {
                await run.StartAsync(initial, SteamId, IdleMode.ManyThenOne);
                await Until(() => clock.PendingCount == 1 && factory.ActiveCount == 2, "private exclusion initial batch");
                clock.ReleaseNext();
                await Until(() => scans == 1, "delayed card scan after privacy request");
                var privateIds = new HashSet<int> { 1 };
                run.UpdatePrivateGames(privateIds);
                privateIds.Clear();
                await Until(() => factory.ActiveCount == 1 && !factory.IsActive(1), "newly private helper released during scan");
                Assert(factory.IsActive(2) && run.Snapshot.State == IdleRunState.Running && !firstScan.Task.IsCompleted,
                    "Independent privacy proof must stop only its helper while other games continue during a delayed card scan.");
                firstScan.TrySetException(new IdleRefreshException(SteamReadStatus.TransientFailure, "Card page timeout after verified privacy"));
                await Until(() => clock.PendingCount == 1 && run.Snapshot.ReadFailure == SteamReadStatus.TransientFailure,
                    "typed retry after private helper removal");
                Assert(run.Snapshot.State == IdleRunState.Running && factory.IsActive(2) && factory.StartCount == 2
                    && run.Snapshot.RemainingGames.Single().AppId == 2 && run.Snapshot.RemainingGames.Single().RemainingCards == 3,
                    "A failed card page must preserve remaining counts and keep the unaffected helper idling.");
                clock.ReleaseNext();
                await Until(() => scans == 2 && clock.PendingCount == 1 && run.Snapshot.ReadFailure == null,
                    "successful scan following private exclusion");
                Assert(!factory.IsActive(1) && factory.IsActive(2) && factory.StartCount == 2
                    && run.Snapshot.RemainingGames.Single().AppId == 2,
                    "The copied private policy must survive retries and exclude stale returned games without relaunching peers.");
                await run.StopAsync();
            }
        }

        private static async Task AllPrivateFailedScanWaitsForSuccessAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var initial = new[] { Game(1, 3, 2) };
            var firstScan = new TaskCompletionSource<IReadOnlyList<IdleGame>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scans = 0;
            using (var run = new IdleRunController(factory, token =>
            {
                var scan = Interlocked.Increment(ref scans);
                return scan == 1 ? firstScan.Task : Task.FromResult<IReadOnlyList<IdleGame>>(initial);
            }, clock))
            {
                await run.StartAsync(initial, SteamId, IdleMode.Single);
                await Until(() => clock.PendingCount == 1 && factory.ActiveCount == 1, "all-private initial helper");
                clock.ReleaseNext();
                await Until(() => scans == 1, "all-private pending card scan");
                run.UpdatePrivateGames(new[] { 1 });
                Assert(factory.ActiveCount == 0 && run.Snapshot.State == IdleRunState.Running,
                    "A verified private game must stop immediately without prematurely declaring card completion.");
                firstScan.TrySetException(new IdleRefreshException(SteamReadStatus.MalformedPage, "Card page failed after all-private proof"));
                await Until(() => clock.PendingCount == 1 && run.Snapshot.ReadFailure == SteamReadStatus.MalformedPage,
                    "all-private failed scan retry");
                Assert(run.Snapshot.State == IdleRunState.Running && run.Snapshot.RemainingGames.Count == 0
                    && factory.ActiveCount == 0 && initial[0].RemainingCards == 3,
                    "All-private exclusions plus an incomplete card scan must retain the session and honest card counts.");
                clock.ReleaseNext();
                await Until(() => scans == 2 && run.Snapshot.State == IdleRunState.Completed,
                    "all-private successful full refresh");
                Assert(factory.ActiveCount == 0 && factory.StartCount == 1 && clock.PendingCount == 0,
                    "Only a successful full refresh may complete the all-private run, without restarting excluded helpers.");
            }
        }

        private static async Task PrivatePendingLaunchIsCanceledAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var late = new TaskCompletionSource<IIdleHelper>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken pendingToken = default(CancellationToken);
            var released = 0;
            var retained = new FakeHelper(2, SteamId, null);
            factory.StartOverride = (appId, steamId, token) =>
            {
                if (appId == 1) { pendingToken = token; return late.Task; }
                return Task.FromResult<IIdleHelper>(retained);
            };
            var initial = new[] { Game(1), Game(2) };
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(initial), clock))
            {
                await run.StartAsync(initial, SteamId, IdleMode.ManyThenOne);
                await Until(() => factory.Attempts == 1, "pending helper before private proof");
                run.UpdatePrivateGames(new[] { 1 });
                await Until(() => factory.Attempts == 2 && clock.PendingCount == 1, "retained game after private pending launch");
                Assert(pendingToken.IsCancellationRequested && retained.IsRunning
                    && run.Snapshot.ActiveGames.Single().AppId == 2,
                    "A verified private pending launch must cancel without blocking or stopping other queued games.");
                late.TrySetResult(new FakeHelper(1, SteamId, () => Interlocked.Increment(ref released)));
                await Until(() => released == 1, "late private helper cleanup");
                Assert(run.Snapshot.State == IdleRunState.Running && run.Snapshot.RemainingGames.Single().AppId == 2,
                    "A late private helper must never become active or rejoin the queue.");
                await run.StopAsync();
                Assert(!retained.IsRunning, "Manual Stop must still release the retained helper.");
            }
        }

        private static async Task ConcurrentStopAndPrivacyUpdateStayStoppedAsync()
        {
            // Exercise both lock acquisition orders without real helpers or timers.
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var factory = new FakeFactory();
                var clock = new FakeClock();
                var games = new[] { Game(1, 3, 2) };
                using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
                using (var startTogether = new Barrier(2))
                {
                    await run.StartAsync(games, SteamId, IdleMode.Single);
                    await Until(() => clock.PendingCount == 1 && factory.ActiveCount == 1, "concurrent privacy and Stop setup");
                    var stop = Task.Run(async () =>
                    {
                        startTogether.SignalAndWait();
                        await run.StopAsync();
                    });
                    var privacy = Task.Run(() =>
                    {
                        startTogether.SignalAndWait();
                        run.UpdatePrivateGames(new[] { 1 });
                    });
                    await Task.WhenAll(stop, privacy);
                    Assert(run.Snapshot.State == IdleRunState.Stopped && factory.ActiveCount == 0 && clock.PendingCount == 0,
                        "Concurrent verified privacy updates must never overwrite manual Stop with a stale running state.");
                }
            }

            // A disposal callback may finish after Stop publishes its terminal state.
            // Its obsolete privacy event must not resurrect the stopped UI either.
            var blockedClock = new FakeClock();
            var blockingFactory = new FakeFactory();
            var disposeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var releaseDispose = new ManualResetEventSlim())
            {
                var helper = new FakeHelper(1, SteamId, () =>
                {
                    disposeStarted.TrySetResult(true);
                    releaseDispose.Wait(TimeSpan.FromSeconds(3));
                });
                blockingFactory.StartOverride = (appId, steamId, token) => Task.FromResult<IIdleHelper>(helper);
                var games = new[] { Game(1, 3, 2) };
                using (var run = new IdleRunController(blockingFactory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), blockedClock))
                {
                    var stoppedSeen = false;
                    var staleAfterStop = false;
                    run.StatusChanged += (sender, status) =>
                    {
                        if (status.State == IdleRunState.Stopped) stoppedSeen = true;
                        else if (stoppedSeen) staleAfterStop = true;
                    };
                    await run.StartAsync(games, SteamId, IdleMode.Single);
                    await Until(() => blockedClock.PendingCount == 1, "blocked privacy disposal setup");
                    var privacy = Task.Run(() => run.UpdatePrivateGames(new[] { 1 }));
                    await Until(() => disposeStarted.Task.IsCompleted, "private helper disposal blocked");
                    try { await run.StopAsync(); }
                    finally { releaseDispose.Set(); }
                    await privacy;
                    Assert(run.Snapshot.State == IdleRunState.Stopped && !helper.IsRunning && blockedClock.PendingCount == 0
                        && stoppedSeen && !staleAfterStop,
                        "Late private cleanup must not publish a stale running event after manual Stop.");
                }
            }
        }

        private static async Task PauseCancelsFastTransitionsAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var games = new[] { Game(1), Game(2) };
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
            {
                await run.StartAsync(games, SteamId, IdleMode.Fast);
                await Until(() => clock.PendingCount == 1, "fast batch");
                Assert(clock.NextDelay == TimeSpan.FromMinutes(5), "Fast batches last five minutes.");
                clock.ReleaseNext();
                await Until(() => clock.PendingCount == 1 && factory.ActiveCount == 0, "fast transition gap");
                Assert(clock.NextDelay == TimeSpan.FromSeconds(5), "Fast mode waits five seconds before solos.");
                await run.PauseAsync();
                var starts = factory.StartCount;
                await Task.Delay(30);
                Assert(run.Snapshot.State == IdleRunState.Paused && factory.ActiveCount == 0 &&
                    clock.PendingCount == 0 && factory.StartCount == starts, "Pause cancels a pending fast-mode transition.");

                await run.ResumeAsync();
                await Until(() => clock.PendingCount == 1 && factory.ActiveCount == 2, "resumed fast batch");
                clock.ReleaseNext();
                await Until(() => clock.PendingCount == 1 && factory.ActiveCount == 0, "resumed gap");
                clock.ReleaseNext();
                await Until(() => clock.PendingCount == 1 && factory.ActiveCount == 1, "fast solo");
                Assert(clock.NextDelay == TimeSpan.FromSeconds(5), "Each fast solo lasts five seconds.");
                await run.PauseAsync();
                starts = factory.StartCount;
                await Task.Delay(30);
                Assert(factory.StartCount == starts && factory.ActiveCount == 0, "Pause never starts the next fast solo.");
            }
        }

        private static async Task SkipSurvivesRefreshAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var games = new[] { Game(1, 1), Game(2, 1) };
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
            {
                await run.StartAsync(games, SteamId, IdleMode.Single);
                await Until(() => clock.PendingCount == 1, "single phase");
                Assert(clock.NextDelay == TimeSpan.FromMinutes(5), "A last card checks after five minutes.");
                await run.SkipAsync();
                await Until(() => clock.PendingCount == 1 && run.Snapshot.ActiveGames.Single().AppId == 2, "skipped phase");
                clock.ReleaseNext();
                await Until(() => clock.PendingCount == 1, "refreshed phase");
                Assert(run.Snapshot.RemainingGames.Count == 1 && run.Snapshot.RemainingGames[0].AppId == 2,
                    "Refresh cannot reintroduce a game skipped during this run.");
                await run.StopAsync();
            }
        }

        private static async Task ResumeRescanPreservesSkippedAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var games = new[] { Game(1), Game(2), Game(3) };
            using (var run = new IdleRunController(factory, token =>
                Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
            {
                await run.StartAsync(games, SteamId, IdleMode.Single);
                await Until(() => clock.PendingCount == 1, "resume skip setup");
                await run.SkipAsync();
                await Until(() => clock.PendingCount == 1 && run.Snapshot.ActiveGames[0].AppId == 2, "skip before pause");
                await run.PauseAsync();
                await run.StartAsync(games, SteamId, IdleMode.Single, preserveSkipped: true);
                await Until(() => clock.PendingCount == 1, "resume with refreshed counts");
                Assert(run.Snapshot.RemainingGames.All(game => game.AppId != 1),
                    "A fresh scan while resuming the same account must retain skipped games.");
                clock.ReleaseNext();
                await Until(() => clock.PendingCount == 1, "resume refresh");
                Assert(run.Snapshot.RemainingGames.All(game => game.AppId != 1),
                    "Later scans must also keep the resumed skip set.");
                await run.StopAsync();
                await run.StartAsync(games, SteamId + 1, IdleMode.Single, preserveSkipped: true);
                await Until(() => clock.PendingCount == 1, "new account");
                Assert(run.Snapshot.RemainingGames.Any(game => game.AppId == 1),
                    "A different account cannot inherit the previous account's skip set.");
                await run.StopAsync();
            }
        }

        private static async Task HelperFailureReleasesPeersAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(new IdleGame[0]), clock))
            {
                await run.StartAsync(new[] { Game(1, -1), Game(2, -1) }, SteamId, IdleMode.Whitelist);
                await Until(() => clock.PendingCount == 1, "helper failure setup");
                factory.Fail(1);
                await Until(() => run.Snapshot.State == IdleRunState.Faulted, "unexpected helper exit");
                Assert(factory.ActiveCount == 0 && clock.PendingCount == 0, "A helper failure immediately releases its peers.");
                Assert(run.Snapshot.RemainingGames.Count == 2 && !string.IsNullOrEmpty(run.Snapshot.Error),
                    "A helper failure retains the queue and an actionable error.");
            }
        }

        private static async Task ScanFailurePreservesQueueAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            using (var run = new IdleRunController(factory, token =>
                Task.FromException<IReadOnlyList<IdleGame>>(new InvalidOperationException("Steam is temporarily unavailable.")), clock))
            {
                await run.StartAsync(new[] { Game(1), Game(2) }, SteamId, IdleMode.ManyThenOne);
                await Until(() => clock.PendingCount == 1, "scan failure setup");
                clock.ReleaseNext();
                await Until(() => run.Snapshot.State == IdleRunState.Faulted, "scan failure");
                Assert(factory.ActiveCount == 0 && run.Snapshot.RemainingGames.Count == 2,
                    "An unsuccessful scan must not become an empty completed queue.");
                Assert(run.Snapshot.Error == "Steam is temporarily unavailable.", "The scan error reaches the UI.");
            }
        }

        private static async Task RecoverableScansKeepHelpersAndRetryAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var scans = 0;
            var activeScans = 0;
            var maximumScans = 0;
            using (var run = new IdleRunController(factory, async token =>
            {
                var active = Interlocked.Increment(ref activeScans);
                maximumScans = Math.Max(maximumScans, active);
                var attempt = Interlocked.Increment(ref scans);
                try
                {
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    if (attempt <= 5)
                        throw new IdleRefreshException(
                            attempt % 2 == 0 ? SteamReadStatus.MalformedPage : SteamReadStatus.TransientFailure,
                            "Steam could not be read.");
                    if (attempt == 6)
                        return new[] { Game(1, 1, 2), Game(2, 0, 2) };
                    return new IdleGame[0];
                }
                finally { Interlocked.Decrement(ref activeScans); }
            }, clock))
            {
                await run.StartAsync(new[] { Game(1), Game(2) }, SteamId, IdleMode.ManyThenOne);
                await Until(() => clock.PendingCount == 1, "recoverable scan setup");
                clock.ReleaseNext();
                var retrySeconds = new[] { 30, 60, 120, 240, 300 };
                for (var attempt = 1; attempt <= retrySeconds.Length; attempt++)
                {
                    var expectedAttempts = attempt;
                    await Until(() => scans == expectedAttempts && clock.PendingCount == 1,
                        "recoverable scan retry");
                    Assert(run.Snapshot.State == IdleRunState.Running && factory.ActiveCount == 2 &&
                        factory.StartCount == 2, "A read failure keeps the existing helpers idling.");
                    Assert(run.Snapshot.RemainingGames.Count == 2 &&
                        run.Snapshot.RemainingGames.Sum(game => game.RemainingCards) == 4,
                        "Transient and malformed reads keep every previously verified count.");
                    Assert(run.Snapshot.ReadFailure.HasValue && run.Snapshot.Error == "Steam could not be read.",
                        "The running status exposes a recoverable read failure.");
                    Assert(clock.NextDelay == TimeSpan.FromSeconds(retrySeconds[attempt - 1]),
                        "Retry backoff is bounded at five minutes.");
                    clock.ReleaseNext();
                }
                await Until(() => scans == 6 && clock.PendingCount == 1 && run.Snapshot.ActiveGames.Count == 1,
                    "successful scan after outages");
                Assert(run.Snapshot.State == IdleRunState.Running && run.Snapshot.ReadFailure == null &&
                    run.Snapshot.Error == null && run.Snapshot.RemainingGames.Single().RemainingCards == 1,
                    "Recovery clears the warning and applies the complete verified queue.");
                Assert(factory.StartCount == 2 && factory.ActiveCount == 1 && maximumScans == 1,
                    "Recovery reuses the surviving helper and never overlaps badge reads.");
                clock.ReleaseNext();
                await Until(() => run.Snapshot.State == IdleRunState.Completed, "verified empty scan after recovery");
                Assert(scans == 7 && factory.ActiveCount == 0,
                    "Only a successful empty scan completes the recovered run and stops helpers.");
            }
        }

        private static async Task InvalidScanRetriesWithoutCompletingAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            using (var run = new IdleRunController(factory,
                token => Task.FromResult<IReadOnlyList<IdleGame>>(null), clock))
            {
                await run.StartAsync(new[] { Game(1) }, SteamId, IdleMode.Single);
                await Until(() => clock.PendingCount == 1, "invalid scan setup");
                clock.ReleaseNext();
                await Until(() => clock.PendingCount == 1 &&
                    run.Snapshot.ReadFailure == SteamReadStatus.MalformedPage, "invalid scan retry");
                Assert(run.Snapshot.State == IdleRunState.Running && run.Snapshot.RemainingGames.Count == 1 &&
                    factory.ActiveCount == 1, "An invalid null scan cannot become zero cards or completion.");
                await run.StopAsync();
            }
        }

        private static async Task PauseAndStopCancelScanRetriesAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var scans = 0;
            using (var run = new IdleRunController(factory, token =>
            {
                Interlocked.Increment(ref scans);
                return Task.FromException<IReadOnlyList<IdleGame>>(new IdleRefreshException(
                    SteamReadStatus.TransientFailure, "Steam is temporarily unavailable."));
            }, clock))
            {
                var games = new[] { Game(1), Game(2) };
                await run.StartAsync(games, SteamId, IdleMode.ManyThenOne);
                await Until(() => clock.PendingCount == 1, "pause retry setup");
                clock.ReleaseNext();
                await Until(() => scans == 1 && clock.PendingCount == 1, "retry before pause");
                await run.PauseAsync();
                var starts = factory.StartCount;
                await Task.Delay(30);
                Assert(run.Snapshot.State == IdleRunState.Paused && factory.ActiveCount == 0 &&
                    clock.PendingCount == 0 && scans == 1 && factory.StartCount == starts,
                    "Pause cancels the retry delay and leaves no late scan or helper launch.");
                await run.ResumeAsync();
                await Until(() => clock.PendingCount == 1, "resumed retry setup");
                clock.ReleaseNext();
                await Until(() => scans == 2 && clock.PendingCount == 1, "retry before stop");
                await run.StopAsync();
                starts = factory.StartCount;
                await Task.Delay(30);
                Assert(run.Snapshot.State == IdleRunState.Stopped && factory.ActiveCount == 0 &&
                    clock.PendingCount == 0 && scans == 2 && factory.StartCount == starts,
                    "Stop cancels the retry and preserves manual restart control.");
            }
        }

        private static async Task ExpiredLoginStopsSafelyAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            using (var run = new IdleRunController(factory, token =>
                Task.FromException<IReadOnlyList<IdleGame>>(new IdleRefreshException(
                    SteamReadStatus.LoginRequired, "Steam sign-in expired.")), clock))
            {
                await run.StartAsync(new[] { Game(1), Game(2) }, SteamId, IdleMode.ManyThenOne);
                await Until(() => clock.PendingCount == 1, "expired login setup");
                clock.ReleaseNext();
                await Until(() => run.Snapshot.State == IdleRunState.Faulted, "expired login stop");
                Assert(factory.ActiveCount == 0 && clock.PendingCount == 0 &&
                    run.Snapshot.RemainingGames.Count == 2 &&
                    run.Snapshot.ReadFailure == SteamReadStatus.LoginRequired,
                    "Lost account authentication stops helpers safely while retaining the queue and reason.");
            }
        }

        private static async Task EmptyScanCompletesAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            using (var run = new IdleRunController(factory, token =>
                Task.FromResult<IReadOnlyList<IdleGame>>(new IdleGame[0]), clock))
            {
                await run.StartAsync(new[] { Game(1) }, SteamId, IdleMode.Single);
                await Until(() => clock.PendingCount == 1, "completion setup");
                clock.ReleaseNext();
                await Until(() => run.Snapshot.State == IdleRunState.Completed, "empty validated scan");
                Assert(run.Snapshot.RemainingGames.Count == 0 && factory.ActiveCount == 0,
                    "A successful empty scan completes and releases every helper.");
            }
        }

        private static async Task HelperFailureDuringScanAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var pending = new TaskCompletionSource<IReadOnlyList<IdleGame>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = false;
            var callbackCanceled = false;
            using (var run = new IdleRunController(factory, token => { entered = true; token.Register(() => callbackCanceled = true); return pending.Task; }, clock))
            {
                await run.StartAsync(new[] { Game(1), Game(2) }, SteamId, IdleMode.ManyThenOne);
                await Until(() => clock.PendingCount == 1, "scan helper monitoring setup");
                clock.ReleaseNext();
                await Until(() => entered, "scan helper monitoring");
                factory.Fail(1);
                await Until(() => run.Snapshot.State == IdleRunState.Faulted, "helper failure during scan");
                Assert(callbackCanceled, "Helper failure must cancel the abandoned HTTP scan token.");
                Assert(factory.ActiveCount == 0 && run.Snapshot.RemainingGames.Count == 2,
                    "A pending scan cannot delay peer cleanup after a helper fails.");
                pending.SetResult(new IdleGame[0]);
                await Task.Delay(30);
                Assert(run.Snapshot.State == IdleRunState.Faulted && run.Snapshot.RemainingGames.Count == 2,
                    "A late scan result cannot replace a failed run.");
            }
        }

        private static async Task PauseCancelsPendingScanAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var pending = new TaskCompletionSource<IReadOnlyList<IdleGame>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = false;
            using (var run = new IdleRunController(factory, token => { entered = true; return pending.Task; }, clock))
            {
                await run.StartAsync(new[] { Game(1) }, SteamId, IdleMode.Single);
                await Until(() => clock.PendingCount == 1, "pending scan setup");
                clock.ReleaseNext();
                await Until(() => entered, "pending scan");
                var pause = run.PauseAsync();
                Assert(await Task.WhenAny(pause, Task.Delay(1000)) == pause, "Pause cannot wait on an uncooperative network request.");
                await pause;
                pending.SetResult(new IdleGame[0]);
                await Task.Delay(30);
                Assert(run.Snapshot.State == IdleRunState.Paused && run.Snapshot.RemainingGames.Count == 1 &&
                    factory.ActiveCount == 0, "A stale scan result cannot replace a paused run.");
            }
        }

        private static async Task LateHelperIsReleasedAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var pending = new TaskCompletionSource<IIdleHelper>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.StartOverride = (app, steam, token) => pending.Task;
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(new IdleGame[0]), clock))
            {
                await run.StartAsync(new[] { Game(1) }, SteamId, IdleMode.Single);
                await Until(() => factory.Attempts == 1, "pending helper startup");
                await run.PauseAsync();
                var late = new FakeHelper(1, SteamId, null);
                pending.SetResult(late);
                await Until(() => !late.IsRunning, "late helper cleanup");
                Assert(run.Snapshot.State == IdleRunState.Paused, "Late readiness cannot resume a paused run.");
            }
        }

        private static async Task WrongAccountNeverRunsAsync()
        {
            var factory = new FakeFactory();
            FakeHelper rejected = null;
            factory.StartOverride = (app, steam, token) =>
            {
                rejected = new FakeHelper(app, steam + 1, null);
                return Task.FromResult<IIdleHelper>(rejected);
            };
            using (var run = new IdleRunController(factory, token => Task.FromResult<IReadOnlyList<IdleGame>>(new IdleGame[0]), new FakeClock()))
            {
                await run.StartAsync(new[] { Game(1) }, SteamId, IdleMode.Single);
                await Until(() => run.Snapshot.State == IdleRunState.Faulted, "account mismatch");
                Assert(rejected != null && !rejected.IsRunning && run.Snapshot.ActiveGames.Count == 0,
                    "A helper reporting a different account must be disposed before running.");
            }
        }

        private static async Task RapidRepeatedControlsSerializeAsync()
        {
            var factory = new FakeFactory();
            var clock = new FakeClock();
            var games = Enumerable.Range(1, 31).Select(id => Game(id)).ToArray();
            using (var run = new IdleRunController(factory, token =>
                Task.FromResult<IReadOnlyList<IdleGame>>(games), clock))
            {
                for (var round = 0; round < 3; round++)
                {
                    await run.StartAsync(games, SteamId, IdleMode.ManyThenOne);
                    await Until(() => clock.PendingCount == 1 && factory.ActiveCount == 30,
                        "rapid controls setup");
                    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var operations = Enumerable.Range(0, 25).Select(index => Task.Run(async () =>
                    {
                        await release.Task;
                        switch (index % 5)
                        {
                            case 0: await run.StartAsync(games, SteamId, IdleMode.ManyThenOne); break;
                            case 1: await run.PauseAsync(); break;
                            case 2: await run.ResumeAsync(); break;
                            case 3: await run.SkipAsync(); break;
                            default: await run.StopAsync(); break;
                        }
                    })).ToArray();
                    var all = Task.WhenAll(operations);
                    release.SetResult(true);
                    Assert(await Task.WhenAny(all, Task.Delay(5000)) == all,
                        "Rapid repeated controls must finish without deadlocking a transition.");
                    await all;
                    await run.StopAsync();
                    Assert(run.Snapshot.State == IdleRunState.Stopped &&
                        run.Snapshot.ActiveGames.Count == 0 && factory.ActiveCount == 0 && clock.PendingCount == 0,
                        "The final stop must cancel every pending transition and release every helper.");
                    Assert(factory.MaximumActive <= IdleRunController.MaximumHelpers && !factory.DuplicateActive,
                        "Overlapping controls cannot create duplicate helpers or exceed the concurrency limit.");
                }
            }
        }

        private static async Task MissingHelperIsReportedAsync()
        {
            using (var factory = new IdleProcessManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")))
            {
                try { await factory.StartAsync(1, SteamId, CancellationToken.None); }
                catch (IdleHelperException ex)
                {
                    Assert(ex.Message.Contains("complete application package"), "A missing helper explains how to recover.");
                    return;
                }
                throw new InvalidOperationException("A missing helper executable was accepted.");
            }
        }

        private static void OwnedJobClosesOnlyItsChild()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
            Process unrelated = null;
            Process child = null;
            WindowsJob job = null;
            try
            {
                unrelated = Process.Start(new ProcessStartInfo(executable, "127.0.0.1 -n 60")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                job = new WindowsJob();
                child = job.StartProcess(executable, "127.0.0.1 -n 60", Path.GetDirectoryName(executable));
                Assert(!child.HasExited && !unrelated.HasExited, "Lifecycle probes should be running.");
                job.Dispose();
                Assert(child.WaitForExit(3000), "Closing an owned job must terminate its helper.");
                Assert(!unrelated.HasExited, "Closing the job must not terminate an unrelated process.");
            }
            finally
            {
                if (job != null) job.Dispose();
                if (child != null) child.Dispose();
                if (unrelated != null)
                {
                    if (!unrelated.HasExited) unrelated.Kill();
                    unrelated.WaitForExit(3000);
                    unrelated.Dispose();
                }
            }
        }

        private static async Task Until(Func<bool> condition, string description)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition())
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidOperationException("Timed out waiting for " + description + ".");
                await Task.Delay(5);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FakeClock : IIdleClock
        {
            private readonly object sync = new object();
            private readonly List<Delay> delays = new List<Delay>();
            public DateTimeOffset UtcNow { get { return new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero); } }
            public int PendingCount { get { lock (sync) return delays.Count(delay => !delay.Source.Task.IsCompleted); } }
            public TimeSpan NextDelay { get { lock (sync) return delays.First(delay => !delay.Source.Task.IsCompleted).Duration; } }

            public Task DelayAsync(TimeSpan duration, CancellationToken token)
            {
                var delay = new Delay { Duration = duration,
                    Source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
                lock (sync) delays.Add(delay);
                token.Register(() => delay.Source.TrySetCanceled());
                return delay.Source.Task;
            }

            public void ReleaseNext()
            {
                lock (sync) delays.First(delay => !delay.Source.Task.IsCompleted).Source.TrySetResult(true);
            }

            private sealed class Delay
            {
                public TimeSpan Duration;
                public TaskCompletionSource<bool> Source;
            }
        }

        private sealed class AdvancingClock : IIdleClock, IMonotonicIdleClock
        {
            private readonly object sync = new object();
            private readonly List<Delay> delays = new List<Delay>();
            private DateTimeOffset now = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
            private TimeSpan wallOffset;
            public DateTimeOffset UtcNow { get { lock (sync) return now.Add(wallOffset); } }
            public TimeSpan Elapsed
            { get { lock (sync) return now - new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero); } }
            public int PendingCount { get { lock (sync) return delays.Count(delay => !delay.Source.Task.IsCompleted); } }

            public Task DelayAsync(TimeSpan duration, CancellationToken token)
            {
                Delay delay;
                lock (sync)
                {
                    delay = new Delay { Due = now.Add(duration),
                        Source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
                    delays.Add(delay);
                }
                token.Register(() => delay.Source.TrySetCanceled());
                return delay.Source.Task;
            }

            public void AdvanceNext()
            {
                lock (sync)
                {
                    now = delays.Where(delay => !delay.Source.Task.IsCompleted).Min(delay => delay.Due);
                    foreach (var delay in delays.Where(delay => !delay.Source.Task.IsCompleted && delay.Due <= now))
                        delay.Source.TrySetResult(true);
                }
            }

            public void ShiftWallClock(TimeSpan adjustment) { lock (sync) wallOffset += adjustment; }

            private sealed class Delay
            {
                public DateTimeOffset Due;
                public TaskCompletionSource<bool> Source;
            }
        }

        private sealed class FakeFactory : IIdleHelperFactory
        {
            private readonly object sync = new object();
            private readonly List<FakeHelper> helpers = new List<FakeHelper>();
            public Func<int, ulong, CancellationToken, Task<IIdleHelper>> StartOverride;
            public int Attempts;
            public int MaximumActive { get; private set; }
            public bool DuplicateActive { get; private set; }
            public int StartCount { get { lock (sync) return helpers.Count; } }
            public int ActiveCount { get { lock (sync) return helpers.Count(helper => helper.IsRunning); } }

            public Task<IIdleHelper> StartAsync(int appId, ulong steamId, CancellationToken token)
            {
                Interlocked.Increment(ref Attempts);
                if (StartOverride != null) return StartOverride(appId, steamId, token);
                token.ThrowIfCancellationRequested();
                lock (sync)
                {
                    if (helpers.Any(existing => existing.AppId == appId && existing.IsRunning))
                        DuplicateActive = true;
                    var helper = new FakeHelper(appId, steamId, null);
                    helpers.Add(helper);
                    MaximumActive = Math.Max(MaximumActive, helpers.Count(item => item.IsRunning));
                    return Task.FromResult<IIdleHelper>(helper);
                }
            }

            public bool IsActive(int appId)
            {
                lock (sync) return helpers.Any(helper => helper.AppId == appId && helper.IsRunning);
            }

            public void Fail(int appId, IdleHelperFailure failure = IdleHelperFailure.Unknown)
            {
                lock (sync) helpers.Last(helper => helper.AppId == appId && helper.IsRunning).Fail(failure);
            }
        }

        private sealed class FakeHelper : IIdleHelper
        {
            private readonly TaskCompletionSource<bool> completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Action disposed;
            public int AppId { get; private set; }
            public ulong SteamId { get; private set; }
            public bool IsRunning { get { return !completion.Task.IsCompleted; } }
            public Task Completion { get { return completion.Task; } }
            public FakeHelper(int appId, ulong steamId, Action disposed)
            {
                AppId = appId;
                SteamId = steamId;
                this.disposed = disposed;
            }
            public void Fail(IdleHelperFailure failure = IdleHelperFailure.Unknown)
            { completion.TrySetException(new IdleHelperException("Steam disconnected.", failure)); }
            public void Dispose()
            {
                completion.TrySetResult(true);
                if (disposed != null) disposed();
            }
        }
    }
}
