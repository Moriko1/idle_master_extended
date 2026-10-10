using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    public enum IdleMode { Single, OneThenMany, ManyThenOne, Fast, Whitelist }
    public enum IdleRunState { Stopped, Starting, Running, Paused, Completed, Faulted }

    /// <summary>Optional elapsed clock: preparation budgets must not follow wall-clock corrections.</summary>
    public interface IMonotonicIdleClock
    {
        TimeSpan Elapsed { get; }
    }

    /// <summary>Classifies a scan failure without discarding the previous verified game queue.</summary>
    public sealed class IdleRefreshException : Exception
    {
        public SteamReadStatus Status { get; private set; }

        public IdleRefreshException(SteamReadStatus status, string message) : base(message)
        {
            if (status == SteamReadStatus.Success || !Enum.IsDefined(typeof(SteamReadStatus), status))
                throw new ArgumentOutOfRangeException(nameof(status));
            Status = status;
        }
    }

    public sealed class IdleGame
    {
        public int AppId { get; private set; }
        public string Name { get; private set; }
        public int RemainingCards { get; private set; }
        public double HoursPlayed { get; private set; }

        public IdleGame(int appId, string name, int remainingCards, double hoursPlayed)
        {
            if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
            if (remainingCards < -1) throw new ArgumentOutOfRangeException(nameof(remainingCards));
            if (double.IsNaN(hoursPlayed) || double.IsInfinity(hoursPlayed) || hoursPlayed < 0)
                throw new ArgumentOutOfRangeException(nameof(hoursPlayed));
            AppId = appId;
            Name = string.IsNullOrWhiteSpace(name) ? appId.ToString() : name;
            RemainingCards = remainingCards;
            HoursPlayed = hoursPlayed;
        }
    }

    public sealed class IdleRunStatus : EventArgs
    {
        public IdleRunState State { get; private set; }
        public IdleMode Mode { get; private set; }
        public IReadOnlyList<IdleGame> ActiveGames { get; private set; }
        public IReadOnlyList<IdleGame> RemainingGames { get; private set; }
        public DateTimeOffset? NextCheckAt { get; private set; }
        public string Error { get; private set; }
        public SteamReadStatus? ReadFailure { get; private set; }
        public bool GameplayActive { get; private set; }

        internal IdleRunStatus(IdleRunState state, IdleMode mode, IEnumerable<IdleGame> active,
            IEnumerable<IdleGame> remaining, DateTimeOffset? nextCheckAt, string error,
            SteamReadStatus? readFailure = null, bool gameplayActive = false)
        {
            State = state;
            Mode = mode;
            ActiveGames = active.ToList().AsReadOnly();
            RemainingGames = remaining.ToList().AsReadOnly();
            NextCheckAt = nextCheckAt;
            Error = error;
            ReadFailure = readFailure;
            GameplayActive = gameplayActive;
        }
    }

    public sealed class IdleRunController : IDisposable
    {
        public const int MaximumHelpers = 30;
        private readonly IIdleHelperFactory factory;
        private readonly Func<CancellationToken, Task<IReadOnlyList<IdleGame>>> refreshGamesAsync;
        private readonly IIdleClock clock;
        private readonly SemaphoreSlim commands = new SemaphoreSlim(1, 1);
        private readonly object sync = new object();
        private readonly List<IIdleHelper> helpers = new List<IIdleHelper>();
        private readonly HashSet<int> skipped = new HashSet<int>();
        private readonly Dictionary<int, WarmupTime> warmupTimes = new Dictionary<int, WarmupTime>();
        private HashSet<int> playingAppIds = new HashSet<int>();
        private bool gameplayActive;
        private long activityRevision;
        private TaskCompletionSource<bool> activityChanged = NewActivitySignal();
        private HashSet<int> privateGames = new HashSet<int>();
        private readonly Dictionary<int, CancellationTokenSource> pendingLaunches =
            new Dictionary<int, CancellationTokenSource>();
        private bool privateRefreshPending;
        private List<IdleGame> games = new List<IdleGame>();
        private List<IdleGame> active = new List<IdleGame>();
        private CancellationTokenSource runCancellation;
        private Task runTask;
        private ulong expectedSteamId;
        private IdleMode mode = IdleMode.OneThenMany;
        private volatile bool disposed;
        private volatile IdleRunStatus snapshot;

        public event EventHandler<IdleRunStatus> StatusChanged;
        public IdleRunStatus Snapshot { get { return snapshot; } }

        public IdleRunController(IIdleHelperFactory factory,
            Func<CancellationToken, Task<IReadOnlyList<IdleGame>>> refreshGamesAsync, IIdleClock clock = null)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.refreshGamesAsync = refreshGamesAsync ?? throw new ArgumentNullException(nameof(refreshGamesAsync));
            this.clock = clock ?? new SystemIdleClock();
            snapshot = new IdleRunStatus(IdleRunState.Stopped, mode, active, games, null, null);
        }

        public async Task StartAsync(IEnumerable<IdleGame> initialGames, ulong steamId, IdleMode idleMode,
            bool preserveSkipped = false)
        {
            if (initialGames == null) throw new ArgumentNullException(nameof(initialGames));
            if (steamId == 0) throw new ArgumentOutOfRangeException(nameof(steamId));
            if (!Enum.IsDefined(typeof(IdleMode), idleMode)) throw new ArgumentOutOfRangeException(nameof(idleMode));
            var supplied = initialGames.ToList();
            if (supplied.Any(game => game == null))
                throw new ArgumentException("The game list contains a null entry.", nameof(initialGames));
            await commands.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await CancelRunAsync().ConfigureAwait(false);
                lock (sync)
                {
                    var keepSkipped = preserveSkipped && expectedSteamId == steamId && mode == idleMode;
                    mode = idleMode;
                    expectedSteamId = steamId;
                    if (!keepSkipped) skipped.Clear();
                    if (!keepSkipped) warmupTimes.Clear();
                    games = FilterGames(supplied);
                    UpdateWarmupBaselines(games);
                    privateRefreshPending = false;
                }
                BeginRun();
            }
            finally { commands.Release(); }
        }

        /// <summary>Applies independently verified privacy exclusions without treating them as card completion.</summary>
        public void UpdatePrivateGames(IEnumerable<int> appIds)
        {
            if (appIds == null) throw new ArgumentNullException(nameof(appIds));
            var verified = new HashSet<int>(appIds);
            if (verified.Any(appId => appId <= 0)) throw new ArgumentOutOfRangeException(nameof(appIds));
            List<IIdleHelper> removed;
            List<CancellationTokenSource> launches;
            IdleRunStatus updated;
            lock (sync)
            {
                if (disposed) return;
                privateGames = verified;
                var excluded = games.RemoveAll(game => privateGames.Contains(game.AppId));
                active.RemoveAll(game => privateGames.Contains(game.AppId));
                removed = helpers.Where(helper => privateGames.Contains(helper.AppId)).ToList();
                foreach (var helper in removed) { helpers.Remove(helper); FinishWarmupTime(helper.AppId); }
                launches = pendingLaunches.Where(item => privateGames.Contains(item.Key)).Select(item => item.Value).ToList();
                if (excluded > 0 && (snapshot.State == IdleRunState.Starting || snapshot.State == IdleRunState.Running))
                    privateRefreshPending = true;
                updated = new IdleRunStatus(snapshot.State, mode, active, games,
                    snapshot.NextCheckAt, snapshot.Error, snapshot.ReadFailure, gameplayActive);
                snapshot = updated;
            }
            foreach (var launch in launches)
            {
                try { launch.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            foreach (var helper in removed)
            {
                try { helper.Dispose(); }
                catch { /* One helper cannot prevent other private exclusions. */ }
            }
            NotifyChanged(updated);
        }

        /// <summary>Changes preparation policy without pausing or discarding the verified card queue.</summary>
        public void UpdateGameActivity(IEnumerable<int> actualAppIds, bool isGameplayActive)
        {
            if (actualAppIds == null) throw new ArgumentNullException(nameof(actualAppIds));
            var appIds = new HashSet<int>(actualAppIds);
            if (appIds.Any(appId => appId <= 0)) throw new ArgumentOutOfRangeException(nameof(actualAppIds));
            List<IIdleHelper> removed;
            List<CancellationTokenSource> launches;
            IdleRunStatus updated;
            lock (sync)
            {
                if (disposed || (gameplayActive == isGameplayActive && playingAppIds.SetEquals(appIds))) return;
                gameplayActive = isGameplayActive;
                playingAppIds = appIds;
                activityRevision++;
                var previousSignal = activityChanged;
                activityChanged = NewActivitySignal();
                previousSignal.TrySetResult(true);
                removed = gameplayActive ? helpers.Where(helper => !CanPrepareDuringGameplay(
                    games.FirstOrDefault(game => game.AppId == helper.AppId))).ToList() : new List<IIdleHelper>();
                foreach (var helper in removed)
                {
                    helpers.Remove(helper);
                    FinishWarmupTime(helper.AppId);
                }
                active.RemoveAll(game => removed.Any(helper => helper.AppId == game.AppId));
                launches = pendingLaunches.Values.ToList();
                updated = new IdleRunStatus(snapshot.State, mode, active, games,
                    snapshot.NextCheckAt, snapshot.Error, snapshot.ReadFailure, gameplayActive);
                snapshot = updated;
            }
            foreach (var launch in launches)
            {
                try { launch.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            foreach (var helper in removed)
            {
                try { helper.Dispose(); }
                catch { }
            }
            NotifyChanged(updated);
        }

        public async Task PauseAsync(string reason = null)
        {
            await commands.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (Snapshot.State != IdleRunState.Running && Snapshot.State != IdleRunState.Starting) return;
                await CancelRunAsync().ConfigureAwait(false);
                Publish(IdleRunState.Paused, null, reason);
            }
            finally { commands.Release(); }
        }

        public async Task ResumeAsync()
        {
            await commands.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (Snapshot.State != IdleRunState.Paused && Snapshot.State != IdleRunState.Faulted) return;
                await CancelRunAsync().ConfigureAwait(false);
                BeginRun();
            }
            finally { commands.Release(); }
        }

        public async Task StopAsync()
        {
            await commands.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return;
                await CancelRunAsync().ConfigureAwait(false);
                Publish(IdleRunState.Stopped);
            }
            finally { commands.Release(); }
        }

        public async Task SkipAsync()
        {
            await commands.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                var wasRunning = Snapshot.State == IdleRunState.Running || Snapshot.State == IdleRunState.Starting;
                int appId;
                lock (sync) appId = active.Count > 0 ? active[0].AppId : (games.Count > 0 ? games[0].AppId : 0);
                if (appId == 0) return;
                await CancelRunAsync().ConfigureAwait(false);
                lock (sync)
                {
                    skipped.Add(appId);
                    games.RemoveAll(game => game.AppId == appId);
                }
                if (wasRunning) BeginRun();
                else Publish(Snapshot.State);
            }
            finally { commands.Release(); }
        }

        private void BeginRun()
        {
            ThrowIfDisposed();
            lock (sync)
            {
                if (games.Count == 0 && !privateRefreshPending) { Publish(IdleRunState.Completed); return; }
                runCancellation = new CancellationTokenSource();
                var token = runCancellation.Token;
                Publish(IdleRunState.Starting);
                runTask = Task.Run(() => RunAsync(token));
            }
        }

        private async Task CancelRunAsync()
        {
            var cancellation = runCancellation;
            var running = runTask;
            if (cancellation != null) cancellation.Cancel();
            StopHelpers();
            if (running != null) await running.ConfigureAwait(false);
            if (cancellation != null) cancellation.Dispose();
            runCancellation = null;
            runTask = null;
        }

        private async Task RunAsync(CancellationToken token)
        {
            string error = null;
            SteamReadStatus? readFailure = null;
            var finished = false;
            var helperFailures = 0;
            var initializationFailures = 0;
            var scanRetry = new RetryWindow();
            var helperRetry = new RetryWindow();
            try
            {
                while (true)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        List<IdleGame> remaining;
                        bool playing;
                        long revision;
                        lock (sync) { remaining = games.ToList(); playing = gameplayActive; revision = activityRevision; }
                        await WaitForRetryAsync(helperRetry, token, revision).ConfigureAwait(false);
                        if (remaining.Count == 0)
                        {
                            bool needsRefresh;
                            lock (sync) needsRefresh = privateRefreshPending;
                            if (needsRefresh) { await RefreshAsync(token, revision, scanRetry).ConfigureAwait(false); continue; }
                            finished = true;
                            break;
                        }

                        if (playing)
                        {
                            List<IdleGame> preparation;
                            lock (sync) preparation = remaining.Where(CanPrepareDuringGameplay).Take(MaximumHelpers).ToList();
                            await StartHelpersAsync(preparation, token, revision).ConfigureAwait(false);
                            if (HasWaitingPreparation()) continue;
                            // A positive card queue may legitimately have no helpers while
                            // the user plays. Only a complete scan can finish that queue.
                            await WaitAsync(TimeSpan.FromMinutes(6), token, revision).ConfigureAwait(false);
                            await RefreshAsync(token, revision, scanRetry).ConfigureAwait(false);
                        }
                        else if (mode == IdleMode.Fast && remaining.Count > 1)
                        {
                            var batch = remaining.Take(MaximumHelpers).ToList();
                            await StartHelpersAsync(batch, token, revision).ConfigureAwait(false);
                            await WaitAsync(TimeSpan.FromMinutes(5), token, revision).ConfigureAwait(false);
                            await RefreshAsync(token, revision, scanRetry).ConfigureAwait(false);
                            StopHelpers();
                            await WaitAsync(TimeSpan.FromSeconds(5), token, revision).ConfigureAwait(false);
                            foreach (var candidate in batch)
                            {
                                token.ThrowIfCancellationRequested();
                                ThrowIfActivityChanged(revision);
                                IdleGame game;
                                lock (sync) game = games.FirstOrDefault(item => item.AppId == candidate.AppId);
                                if (game == null) continue;
                                await StartHelpersAsync(new[] { game }, token, revision).ConfigureAwait(false);
                                await WaitAsync(TimeSpan.FromSeconds(5), token, revision).ConfigureAwait(false);
                                StopHelpers();
                            }
                        }
                        else
                        {
                            var selected = SelectGames(remaining);
                            await StartHelpersAsync(selected, token, revision).ConfigureAwait(false);
                            var duration = mode == IdleMode.Whitelist || selected.Count > 1
                                ? TimeSpan.FromMinutes(6)
                                : TimeSpan.FromMinutes(selected[0].RemainingCards == 1 ? 5 : 15);
                            await WaitAsync(duration, token, revision).ConfigureAwait(false);
                            await RefreshAsync(token, revision, scanRetry).ConfigureAwait(false);
                        }
                        helperFailures = 0;
                        initializationFailures = 0;
                    }
                    catch (ActivityPolicyChangedException) { token.ThrowIfCancellationRequested(); }
                    catch (IdleHelperException ex) when (IsRecoverable(ex))
                    {
                        StopHelpers();
                        helperFailures = Math.Min(helperFailures + 1, 5);
                        initializationFailures = ex.Failure == IdleHelperFailure.InitializationFailed
                            ? initializationFailures + 1 : 0;
                        if (initializationFailures >= 6)
                            throw new IdleHelperException("Steam could not initialize idling after several retries. Check the queued game and Steam, then retry.");
                        var retry = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, helperFailures - 1)));
                        helperRetry.NextAttemptAt = SteadyTime.Add(retry);
                        helperRetry.Error = "Steam is temporarily unavailable. Retrying idling automatically.";
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                error = ex.Message;
                readFailure = (ex as IdleRefreshException)?.Status;
            }
            finally
            {
                StopHelpers();
                if (!token.IsCancellationRequested && !disposed)
                {
                    if (error != null) Publish(IdleRunState.Faulted, null, error, readFailure);
                    else if (finished) Publish(IdleRunState.Completed);
                }
            }
        }

        private List<IdleGame> SelectGames(List<IdleGame> remaining)
        {
            if (mode == IdleMode.Whitelist) return remaining.Take(MaximumHelpers).ToList();
            if (mode == IdleMode.Single || remaining.Count == 1) return remaining.Take(1).ToList();
            var young = remaining.Where(game => EffectiveHours(game) < 2).Take(MaximumHelpers).ToList();
            var ready = remaining.FirstOrDefault(game => EffectiveHours(game) >= 2);
            if (mode == IdleMode.OneThenMany && ready != null) return new List<IdleGame> { ready };
            if (young.Count > 1) return young;
            return remaining.Take(1).ToList();
        }

        private async Task StartHelpersAsync(IEnumerable<IdleGame> selectedGames, CancellationToken token, long revision)
        {
            List<IdleGame> selected;
            List<IIdleHelper> obsolete;
            lock (sync)
            {
                ThrowIfActivityChanged(revision);
                selected = selectedGames.Where(game => !privateGames.Contains(game.AppId)).Take(MaximumHelpers).ToList();
                var wanted = new HashSet<int>(selected.Select(game => game.AppId));
                obsolete = helpers.Where(helper => !wanted.Contains(helper.AppId)).ToList();
                foreach (var helper in obsolete) { helpers.Remove(helper); FinishWarmupTime(helper.AppId); }
                active = selected.Where(game => helpers.Any(helper => helper.AppId == game.AppId)).ToList();
            }
            foreach (var helper in obsolete) helper.Dispose();
            Publish(IdleRunState.Starting);
            foreach (var game in selected)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfActivityChanged(revision);
                await EnsureHelpersAliveAsync().ConfigureAwait(false);
                CancellationTokenSource launch;
                lock (sync)
                {
                    ThrowIfActivityChanged(revision);
                    if (privateGames.Contains(game.AppId) || helpers.Any(existing => existing.AppId == game.AppId)) continue;
                    launch = CancellationTokenSource.CreateLinkedTokenSource(token);
                    pendingLaunches[game.AppId] = launch;
                    StartWarmupTime(game);
                }
                Task<IIdleHelper> startup = null;
                var returned = false;
                try
                {
                    startup = factory.StartAsync(game.AppId, expectedSteamId, launch.Token);
                    await WaitForPhaseAsync(startup, launch.Token, revision,
                        "An idling helper stopped during preparation startup.").ConfigureAwait(false);
                    var helper = await AwaitCancelableAsync(startup, launch.Token).ConfigureAwait(false);
                    returned = true;
                    if (helper == null) throw new IdleHelperException("The idling helper did not initialize.");
                    var accepted = false;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        lock (sync)
                        {
                            token.ThrowIfCancellationRequested();
                            ThrowIfActivityChanged(revision);
                            if (disposed) throw new OperationCanceledException(token);
                            if (privateGames.Contains(game.AppId)) continue;
                            if (gameplayActive && !CanPrepareDuringGameplay(game)) continue;
                            if (helper.AppId != game.AppId || helper.SteamId != expectedSteamId)
                                throw new IdleHelperException("The idling helper is using a different Steam account or game.");
                            Observe(helper.Completion);
                            helpers.Add(helper);
                            active.Add(game);
                            StartWarmupTime(game);
                            accepted = true;
                        }
                    }
                    finally { if (!accepted) helper.Dispose(); }
                    await EnsureHelpersAliveAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (launch.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    ThrowIfActivityChanged(revision);
                    lock (sync) if (gameplayActive && !CanPrepareDuringGameplay(game)) continue;
                    lock (sync) if (!privateGames.Contains(game.AppId)) throw;
                }
                finally
                {
                    if (!returned && startup != null)
                    {
                        launch.Cancel();
                        ObserveAndDisposeLateHelper(startup);
                    }
                    lock (sync)
                    {
                        pendingLaunches.Remove(game.AppId);
                        if (!helpers.Any(helper => helper.AppId == game.AppId)) FinishWarmupTime(game.AppId);
                    }
                    launch.Dispose();
                }
            }
            ThrowIfActivityChanged(revision);
            Publish(IdleRunState.Running);
        }

        private async Task RefreshAsync(CancellationToken token, long revision, RetryWindow retry)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfActivityChanged(revision);
                await WaitForRetryAsync(retry, token, revision).ConfigureAwait(false);
                try
                {
                    await RefreshOnceAsync(token, revision).ConfigureAwait(false);
                    retry.Failures = 0;
                    retry.Error = null;
                    retry.ReadFailure = null;
                    Publish(IdleRunState.Running);
                    return;
                }
                catch (IdleRefreshException ex) when (ex.Status == SteamReadStatus.TransientFailure ||
                    ex.Status == SteamReadStatus.MalformedPage)
                {
                    // Failed reads never prove that cards have finished. Keep the current
                    // helpers alive and retry one scan at a time, up to a five-minute cadence.
                    // The deadline and failure count belong to the run, rather than
                    // this invocation. Gameplay changes may reselect helpers but may
                    // never shorten a Steam request backoff or reset its escalation.
                    retry.Failures = Math.Min(retry.Failures + 1, 5);
                    var delay = TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, retry.Failures - 1)));
                    retry.NextAttemptAt = SteadyTime.Add(delay);
                    retry.Error = ex.Message;
                    retry.ReadFailure = ex.Status;
                }
            }
        }

        private async Task WaitForRetryAsync(RetryWindow retry, CancellationToken token, long revision)
        {
            if (!retry.NextAttemptAt.HasValue) return;
            var remaining = retry.NextAttemptAt.Value - SteadyTime;
            if (remaining > TimeSpan.Zero)
                await WaitAsync(remaining, token, revision, retry.Error, retry.ReadFailure).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowIfActivityChanged(revision);
            retry.NextAttemptAt = null;
        }

        private async Task RefreshOnceAsync(CancellationToken token, long revision)
        {
            using (var phase = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var refresh = refreshGamesAsync(phase.Token);
                Observe(refresh);
                try
                {
                    await WaitForPhaseAsync(refresh, token, revision,
                        "An idling helper stopped during the badge check. Check Steam and try again.").ConfigureAwait(false);
                    var refreshed = await AwaitCancelableAsync(refresh, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (refreshed == null || refreshed.Any(game => game == null))
                        throw new IdleRefreshException(SteamReadStatus.MalformedPage,
                            "Steam returned an invalid badge scan result.");
                    lock (sync)
                    {
                        token.ThrowIfCancellationRequested();
                        ThrowIfActivityChanged(revision);
                        games = FilterGames(refreshed);
                        UpdateWarmupBaselines(games);
                        privateRefreshPending = false;
                    }
                }
                finally { phase.Cancel(); }
            }
        }

        private List<IdleGame> FilterGames(IEnumerable<IdleGame> supplied)
        {
            return supplied.Where(game => !skipped.Contains(game.AppId) && !privateGames.Contains(game.AppId) &&
                    (mode == IdleMode.Whitelist || game.RemainingCards > 0))
                .GroupBy(game => game.AppId).Select(group => group.First()).ToList();
        }

        private async Task WaitAsync(TimeSpan duration, CancellationToken token, long revision, string error = null,
            SteamReadStatus? readFailure = null)
        {
            token.ThrowIfCancellationRequested();
            Publish(IdleRunState.Running, clock.UtcNow.Add(duration), error, readFailure);
            using (var phase = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var delay = clock.DelayAsync(duration, phase.Token);
                try
                {
                    await WaitForPhaseAsync(delay, token, revision,
                        "An idling helper stopped unexpectedly. Check Steam and sign in again.", true).ConfigureAwait(false);
                    await delay.ConfigureAwait(false);
                }
                finally { phase.Cancel(); }
            }
        }

        private async Task WaitForPhaseAsync(Task phase, CancellationToken token, long revision, string failure,
            bool allowPreparationReselection = false)
        {
            while (true)
            {
                List<Task> waiting;
                Task policyChanged;
                TimeSpan? preparationDeadline;
                lock (sync)
                {
                    ThrowIfActivityChanged(revision);
                    waiting = helpers.Select(helper => helper.Completion).ToList();
                    policyChanged = activityChanged.Task;
                    preparationDeadline = NextPreparationDeadline();
                }
                if (preparationDeadline.HasValue && preparationDeadline.Value <= TimeSpan.Zero)
                {
                    ReleasePreparedHelpers();
                    continue;
                }
                using (var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var deadline = preparationDeadline.HasValue
                        ? clock.DelayAsync(preparationDeadline.Value, deadlineCancellation.Token) : null;
                    waiting.Add(phase);
                    waiting.Add(policyChanged);
                    if (deadline != null) waiting.Add(deadline);
                    try
                    {
                        var completed = await AwaitCancelableAsync(Task.WhenAny(waiting), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        ThrowIfActivityChanged(revision);
                        if (completed == deadline)
                        {
                            await deadline.ConfigureAwait(false);
                            ReleasePreparedHelpers();
                            if (allowPreparationReselection && HasWaitingPreparation())
                                throw new ActivityPolicyChangedException();
                            continue;
                        }
                        if (completed == phase) return;
                        // Deliberate exclusions are removed before disposal, so their
                        // completed tasks cannot be mistaken for helper failures.
                        if (!IsRegisteredCompletion(completed)) continue;
                        try { await completed.ConfigureAwait(false); }
                        catch { if (!IsRegisteredCompletion(completed)) continue; throw; }
                        if (IsRegisteredCompletion(completed)) throw new IdleHelperException(failure,
                            IdleHelperFailure.UnexpectedExit);
                    }
                    finally { deadlineCancellation.Cancel(); }
                }
            }
        }

        private bool CanPrepareDuringGameplay(IdleGame game)
        {
            // Whitelist entries do not carry verified card eligibility or playtime.
            return game != null && mode != IdleMode.Whitelist && game.RemainingCards > 0 &&
                !playingAppIds.Contains(game.AppId) && EffectiveHours(game) < 2 - 1e-9;
        }

        private bool HasWaitingPreparation()
        {
            lock (sync) return gameplayActive && helpers.Count < MaximumHelpers && games.Any(game =>
                CanPrepareDuringGameplay(game) && !helpers.Any(helper => helper.AppId == game.AppId));
        }

        private double EffectiveHours(IdleGame game)
        {
            lock (sync)
            {
                WarmupTime time;
                if (!warmupTimes.TryGetValue(game.AppId, out time)) return game.HoursPlayed;
                var elapsed = time.StartedAt.HasValue
                    ? Math.Max(0, (SteadyTime - time.StartedAt.Value).TotalHours) : 0;
                return Math.Max(game.HoursPlayed, time.Hours + elapsed);
            }
        }

        private void UpdateWarmupBaselines(IEnumerable<IdleGame> supplied)
        {
            foreach (var game in supplied)
            {
                WarmupTime time;
                if (!warmupTimes.TryGetValue(game.AppId, out time))
                    warmupTimes[game.AppId] = new WarmupTime { Hours = game.HoursPlayed };
                else
                {
                    time.Hours = EffectiveHours(game);
                    if (time.StartedAt.HasValue) time.StartedAt = SteadyTime;
                }
            }
        }

        private void StartWarmupTime(IdleGame game)
        {
            lock (sync)
            {
                UpdateWarmupBaselines(new[] { game });
                warmupTimes[game.AppId].StartedAt = SteadyTime;
            }
        }

        private void FinishWarmupTime(int appId)
        {
            lock (sync)
            {
                WarmupTime time;
                if (!warmupTimes.TryGetValue(appId, out time) || !time.StartedAt.HasValue) return;
                time.Hours += Math.Max(0, (SteadyTime - time.StartedAt.Value).TotalHours);
                time.StartedAt = null;
            }
        }

        private TimeSpan? NextPreparationDeadline()
        {
            if (!gameplayActive || mode == IdleMode.Whitelist) return null;
            var preparation = games.Where(game => helpers.Any(helper => helper.AppId == game.AppId) ||
                pendingLaunches.ContainsKey(game.AppId)).ToList();
            if (preparation.Count == 0) return null;
            return TimeSpan.FromMilliseconds(Math.Max(1,
                preparation.Min(game => (2 - EffectiveHours(game)) * 3600000)));
        }

        private void ReleasePreparedHelpers()
        {
            List<IIdleHelper> removed;
            List<CancellationTokenSource> launches;
            lock (sync)
            {
                if (!gameplayActive) return;
                var prepared = new HashSet<int>(games.Where(game => !CanPrepareDuringGameplay(game)).Select(game => game.AppId));
                removed = helpers.Where(helper => prepared.Contains(helper.AppId)).ToList();
                foreach (var helper in removed) { helpers.Remove(helper); FinishWarmupTime(helper.AppId); }
                launches = pendingLaunches.Where(item => prepared.Contains(item.Key)).Select(item => item.Value).ToList();
                foreach (var appId in pendingLaunches.Keys.Where(prepared.Contains).ToList()) FinishWarmupTime(appId);
                active.RemoveAll(game => prepared.Contains(game.AppId));
            }
            foreach (var launch in launches)
            {
                try { launch.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            foreach (var helper in removed)
            {
                try { helper.Dispose(); }
                catch { }
            }
            if (removed.Count > 0 || launches.Count > 0)
                Publish(Snapshot.State, Snapshot.NextCheckAt, Snapshot.Error, Snapshot.ReadFailure);
        }

        private TimeSpan SteadyTime
        {
            get
            {
                var monotonic = clock as IMonotonicIdleClock;
                return monotonic != null ? monotonic.Elapsed : TimeSpan.FromTicks(clock.UtcNow.UtcDateTime.Ticks);
            }
        }

        private void ThrowIfActivityChanged(long revision)
        {
            lock (sync) if (revision != activityRevision) throw new ActivityPolicyChangedException();
        }

        private static bool IsRecoverable(IdleHelperException exception)
        {
            return exception.Failure == IdleHelperFailure.SteamUnavailable ||
                exception.Failure == IdleHelperFailure.InitializationFailed ||
                exception.Failure == IdleHelperFailure.UnexpectedExit;
        }

        private static TaskCompletionSource<bool> NewActivitySignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class ActivityPolicyChangedException : Exception { }
        private sealed class RetryWindow
        {
            public TimeSpan? NextAttemptAt;
            public int Failures;
            public string Error;
            public SteamReadStatus? ReadFailure;
        }
        private sealed class WarmupTime
        {
            public double Hours;
            public TimeSpan? StartedAt;
        }

        private bool IsRegisteredCompletion(Task completion)
        {
            lock (sync) return helpers.Any(helper => helper.Completion == completion);
        }

        private async Task EnsureHelpersAliveAsync()
        {
            List<IIdleHelper> current;
            lock (sync) current = helpers.ToList();
            foreach (var helper in current)
            {
                if (!IsRegisteredCompletion(helper.Completion)) continue;
                if (!helper.IsRunning || helper.Completion.IsCompleted)
                {
                    try
                    {
                        if (helper.Completion.IsCompleted) await helper.Completion.ConfigureAwait(false);
                    }
                    catch { if (!IsRegisteredCompletion(helper.Completion)) continue; throw; }
                    if (IsRegisteredCompletion(helper.Completion))
                        throw new IdleHelperException("An idling helper stopped unexpectedly. Check Steam and try again.",
                            IdleHelperFailure.UnexpectedExit);
                }
            }
        }

        private void StopHelpers()
        {
            List<IIdleHelper> owned;
            lock (sync)
            {
                owned = helpers.ToList();
                foreach (var helper in owned) FinishWarmupTime(helper.AppId);
                helpers.Clear();
                active = new List<IdleGame>();
            }
            foreach (var helper in owned)
            {
                try { helper.Dispose(); }
                catch { /* Continue releasing every other owned helper. */ }
            }
        }

        private void Publish(IdleRunState state, DateTimeOffset? nextCheck = null, string error = null,
            SteamReadStatus? readFailure = null)
        {
            IdleRunStatus status;
            lock (sync)
            {
                status = new IdleRunStatus(state, mode, active, games, nextCheck, error, readFailure, gameplayActive);
                snapshot = status;
            }
            NotifyChanged(status);
        }

        private void NotifyChanged(IdleRunStatus status)
        {
            if (!ReferenceEquals(snapshot, status)) return;
            var handlers = StatusChanged;
            if (handlers == null) return;
            foreach (EventHandler<IdleRunStatus> handler in handlers.GetInvocationList())
            {
                if (!ReferenceEquals(snapshot, status)) return;
                try { handler(this, status); }
                catch { /* UI subscribers cannot prevent helper cleanup. */ }
            }
        }

        private static async Task<T> AwaitCancelableAsync<T>(Task<T> task, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (task.IsCompleted) return await task.ConfigureAwait(false);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetResult(true)))
            {
                var completed = await Task.WhenAny(task, canceled.Task).ConfigureAwait(false);
                if (completed != task) { Observe(task); throw new OperationCanceledException(token); }
            }
            token.ThrowIfCancellationRequested();
            return await task.ConfigureAwait(false);
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(completed => { var ignored = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static void ObserveAndDisposeLateHelper(Task<IIdleHelper> startup)
        {
            startup.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion && completed.Result != null)
                    completed.Result.Dispose();
                else if (completed.IsFaulted) { var ignored = completed.Exception; }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(IdleRunController));
        }

        public void Dispose()
        {
            disposed = true;
            var cancellation = runCancellation;
            if (cancellation != null)
            {
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            StopHelpers();
        }
    }
}
