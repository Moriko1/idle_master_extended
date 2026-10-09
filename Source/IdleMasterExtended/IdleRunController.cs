using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    public enum IdleMode { Single, OneThenMany, ManyThenOne, Fast, Whitelist }
    public enum IdleRunState { Stopped, Starting, Running, Paused, Completed, Faulted }

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

        internal IdleRunStatus(IdleRunState state, IdleMode mode, IEnumerable<IdleGame> active,
            IEnumerable<IdleGame> remaining, DateTimeOffset? nextCheckAt, string error)
        {
            State = state;
            Mode = mode;
            ActiveGames = active.ToList().AsReadOnly();
            RemainingGames = remaining.ToList().AsReadOnly();
            NextCheckAt = nextCheckAt;
            Error = error;
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
                    games = FilterGames(supplied);
                }
                BeginRun();
            }
            finally { commands.Release(); }
        }

        public async Task PauseAsync()
        {
            await commands.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (Snapshot.State != IdleRunState.Running && Snapshot.State != IdleRunState.Starting) return;
                await CancelRunAsync().ConfigureAwait(false);
                Publish(IdleRunState.Paused);
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
                if (games.Count == 0) { Publish(IdleRunState.Completed); return; }
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
            var finished = false;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    List<IdleGame> remaining;
                    lock (sync) remaining = games.ToList();
                    if (remaining.Count == 0) { finished = true; break; }

                    if (mode == IdleMode.Fast && remaining.Count > 1)
                    {
                        var batch = remaining.Take(MaximumHelpers).ToList();
                        await StartHelpersAsync(batch, token).ConfigureAwait(false);
                        await WaitAsync(TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
                        await RefreshAsync(token).ConfigureAwait(false);
                        StopHelpers();
                        await WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                        foreach (var candidate in batch)
                        {
                            token.ThrowIfCancellationRequested();
                            IdleGame game;
                            lock (sync) game = games.FirstOrDefault(item => item.AppId == candidate.AppId);
                            if (game == null) continue;
                            await StartHelpersAsync(new[] { game }, token).ConfigureAwait(false);
                            await WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                            StopHelpers();
                        }
                    }
                    else
                    {
                        var selected = SelectGames(remaining);
                        await StartHelpersAsync(selected, token).ConfigureAwait(false);
                        var duration = mode == IdleMode.Whitelist || selected.Count > 1
                            ? TimeSpan.FromMinutes(6)
                            : TimeSpan.FromMinutes(selected[0].RemainingCards == 1 ? 5 : 15);
                        await WaitAsync(duration, token).ConfigureAwait(false);
                        if (mode != IdleMode.Whitelist) await RefreshAsync(token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                StopHelpers();
                if (!token.IsCancellationRequested && !disposed)
                {
                    if (error != null) Publish(IdleRunState.Faulted, null, error);
                    else if (finished) Publish(IdleRunState.Completed);
                }
            }
        }

        private List<IdleGame> SelectGames(List<IdleGame> remaining)
        {
            if (mode == IdleMode.Whitelist) return remaining.Take(MaximumHelpers).ToList();
            if (mode == IdleMode.Single || remaining.Count == 1) return remaining.Take(1).ToList();
            var young = remaining.Where(game => game.HoursPlayed < 2).Take(MaximumHelpers).ToList();
            var ready = remaining.FirstOrDefault(game => game.HoursPlayed >= 2);
            if (mode == IdleMode.OneThenMany && ready != null) return new List<IdleGame> { ready };
            if (young.Count > 1) return young;
            return remaining.Take(1).ToList();
        }

        private async Task StartHelpersAsync(IEnumerable<IdleGame> selectedGames, CancellationToken token)
        {
            var selected = selectedGames.Take(MaximumHelpers).ToList();
            var wanted = new HashSet<int>(selected.Select(game => game.AppId));
            List<IIdleHelper> obsolete;
            lock (sync)
            {
                obsolete = helpers.Where(helper => !wanted.Contains(helper.AppId)).ToList();
                foreach (var helper in obsolete) helpers.Remove(helper);
                active = selected.Where(game => helpers.Any(helper => helper.AppId == game.AppId)).ToList();
            }
            foreach (var helper in obsolete) helper.Dispose();
            Publish(IdleRunState.Starting);
            foreach (var game in selected)
            {
                token.ThrowIfCancellationRequested();
                await EnsureHelpersAliveAsync().ConfigureAwait(false);
                lock (sync) if (helpers.Any(existing => existing.AppId == game.AppId)) continue;
                var startup = factory.StartAsync(game.AppId, expectedSteamId, token);
                IIdleHelper helper;
                try { helper = await AwaitCancelableAsync(startup, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    ObserveAndDisposeLateHelper(startup);
                    throw;
                }
                if (helper == null) throw new IdleHelperException("The idling helper did not initialize.");
                var accepted = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (helper.AppId != game.AppId || helper.SteamId != expectedSteamId)
                        throw new IdleHelperException("The idling helper is using a different Steam account or game.");
                    lock (sync)
                    {
                        token.ThrowIfCancellationRequested();
                        if (disposed) throw new OperationCanceledException(token);
                        Observe(helper.Completion);
                        helpers.Add(helper);
                        active.Add(game);
                        accepted = true;
                    }
                }
                finally { if (!accepted) helper.Dispose(); }
                await EnsureHelpersAliveAsync().ConfigureAwait(false);
            }
            Publish(IdleRunState.Running);
        }

        private async Task RefreshAsync(CancellationToken token)
        {
            using (var phase = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var refresh = refreshGamesAsync(phase.Token);
                Observe(refresh);
                try
                {
                    List<Task> waiting;
                    lock (sync) waiting = helpers.Select(helper => helper.Completion).ToList();
                    waiting.Add(refresh);
                    var completed = await AwaitCancelableAsync(Task.WhenAny(waiting), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (completed != refresh)
                    {
                        await completed.ConfigureAwait(false);
                        throw new IdleHelperException("An idling helper stopped during the badge check. Check Steam and try again.");
                    }
                    var refreshed = await AwaitCancelableAsync(refresh, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (refreshed == null || refreshed.Any(game => game == null))
                        throw new InvalidOperationException("Steam returned an invalid badge scan result.");
                    lock (sync)
                    {
                        token.ThrowIfCancellationRequested();
                        games = FilterGames(refreshed);
                    }
                }
                finally { phase.Cancel(); }
            }
        }

        private List<IdleGame> FilterGames(IEnumerable<IdleGame> supplied)
        {
            return supplied.Where(game => !skipped.Contains(game.AppId) &&
                    (mode == IdleMode.Whitelist || game.RemainingCards > 0))
                .GroupBy(game => game.AppId).Select(group => group.First()).ToList();
        }

        private async Task WaitAsync(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Publish(IdleRunState.Running, clock.UtcNow.Add(duration));
            using (var phase = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var delay = clock.DelayAsync(duration, phase.Token);
                try
                {
                    List<Task> waiting;
                    lock (sync) waiting = helpers.Select(helper => helper.Completion).ToList();
                    waiting.Add(delay);
                    var completed = await AwaitCancelableAsync(Task.WhenAny(waiting), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await completed.ConfigureAwait(false);
                    if (completed != delay)
                        throw new IdleHelperException("An idling helper stopped unexpectedly. Check Steam and sign in again.");
                }
                finally { phase.Cancel(); }
            }
        }

        private async Task EnsureHelpersAliveAsync()
        {
            List<IIdleHelper> current;
            lock (sync) current = helpers.ToList();
            foreach (var helper in current)
            {
                if (!helper.IsRunning || helper.Completion.IsCompleted)
                {
                    if (helper.Completion.IsCompleted) await helper.Completion.ConfigureAwait(false);
                    throw new IdleHelperException("An idling helper stopped unexpectedly. Check Steam and sign in again.");
                }
            }
        }

        private void StopHelpers()
        {
            List<IIdleHelper> owned;
            lock (sync)
            {
                owned = helpers.ToList();
                helpers.Clear();
                active = new List<IdleGame>();
            }
            foreach (var helper in owned)
            {
                try { helper.Dispose(); }
                catch { /* Continue releasing every other owned helper. */ }
            }
        }

        private void Publish(IdleRunState state, DateTimeOffset? nextCheck = null, string error = null)
        {
            IdleRunStatus status;
            lock (sync) status = new IdleRunStatus(state, mode, active, games, nextCheck, error);
            snapshot = status;
            var handlers = StatusChanged;
            if (handlers == null) return;
            foreach (EventHandler<IdleRunStatus> handler in handlers.GetInvocationList())
            {
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
