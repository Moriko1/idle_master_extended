using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    public interface ISteamRequestClock
    {
        TimeSpan Elapsed { get; }
        DateTimeOffset UtcNow { get; }
        Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
    }

    public enum SteamRequestSurface { Community, LibraryApi }
    public enum SteamRequestFailure { RateLimited, AccessLimited, TokenRejected, Unauthorized, ServerUnavailable, TimedOut, NetworkUnavailable, Interrupted }

    public sealed class SteamRequestDiagnostic
    {
        public SteamRequestSurface Surface { get; }
        public SteamRequestFailure Failure { get; }
        public int? HttpStatus { get; }
        public SteamRequestDiagnostic(SteamRequestSurface surface, SteamRequestFailure failure, int? httpStatus = null)
        {
            Surface = surface;
            Failure = failure;
            HttpStatus = httpStatus;
        }
    }

    /// <summary>One process-wide send budget; replacing a session cannot erase a server cooldown.</summary>
    public sealed class SteamRequestBudget
    {
        private static readonly object LogGate = new object();
        private readonly object gate = new object();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly ISteamRequestClock clock;
        private readonly Action<SteamRequestDiagnostic> diagnostic;
        private TimeSpan nextSend;
        private TimeSpan cooldown;

        public static SteamRequestBudget Shared { get; } = new SteamRequestBudget(new SystemClock(), RecordDiagnostic);

        // Isolated clocks/budgets make tests deterministic without contacting Steam or sleeping.
        public SteamRequestBudget(ISteamRequestClock clock, Action<SteamRequestDiagnostic> diagnostic = null)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.diagnostic = diagnostic;
        }

        public async Task WaitForTurnAsync(CancellationToken cancellationToken)
        {
            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TimeSpan delay;
                    lock (gate)
                    {
                        var now = clock.Elapsed;
                        var due = nextSend > cooldown ? nextSend : cooldown;
                        if (due <= now)
                        {
                            nextSend = Add(now, TimeSpan.FromSeconds(1));
                            return;
                        }
                        delay = due - now;
                    }
                    // Recheck after every delay: another in-flight response may extend the deadline.
                    await clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally { sendGate.Release(); }
        }

        public void ObserveResponse(SteamRequestSurface surface, HttpResponseMessage response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            var status = (int)response.StatusCode;
            if (status == 429 || status == 403)
            {
                var delay = TimeSpan.FromSeconds(status == 429 ? 60 : 30);
                var retry = response.Headers.RetryAfter;
                if (retry != null)
                {
                    var requested = retry.Delta ?? (retry.Date.HasValue ? retry.Date.Value - clock.UtcNow : TimeSpan.Zero);
                    if (requested > delay) delay = requested;
                }
                lock (gate)
                {
                    var deadline = Add(clock.Elapsed, delay);
                    if (deadline > cooldown) cooldown = deadline;
                }
                Report(surface, status == 429 ? SteamRequestFailure.RateLimited : SteamRequestFailure.AccessLimited, status);
            }
            else if (status == 401)
                Report(surface, surface == SteamRequestSurface.LibraryApi ? SteamRequestFailure.TokenRejected : SteamRequestFailure.Unauthorized, status);
            else if (status == 408 || status >= 500)
                Report(surface, SteamRequestFailure.ServerUnavailable, status);
        }

        internal void Report(SteamRequestSurface surface, SteamRequestFailure failure, int? status = null)
        {
            try { diagnostic?.Invoke(new SteamRequestDiagnostic(surface, failure, status)); }
            catch { /* Diagnostics cannot turn a recoverable read into a failure. */ }
        }

        private static TimeSpan Add(TimeSpan value, TimeSpan increment)
        {
            return increment.Ticks > TimeSpan.MaxValue.Ticks - value.Ticks ? TimeSpan.MaxValue : value + increment;
        }

        private static void RecordDiagnostic(SteamRequestDiagnostic item)
        {
            // These fixed categories are the entire payload: no URI, token, cookie, account or body.
            try
            {
                lock (LogGate)
                {
                    Directory.CreateDirectory(AppPaths.Logs);
                    var path = Path.Combine(AppPaths.Logs, "steam-requests.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
                    File.AppendAllText(path, DateTimeOffset.UtcNow.ToString("o") + " " + item.Surface + " " + item.Failure
                        + (item.HttpStatus.HasValue ? " HTTP" + item.HttpStatus.Value : string.Empty) + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* A read-only or unavailable log folder must not affect the run. */ }
        }

        private sealed class SystemClock : ISteamRequestClock
        {
            private readonly Stopwatch stopwatch = Stopwatch.StartNew();
            public TimeSpan Elapsed => stopwatch.Elapsed;
            public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
            public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
                Task.Delay(duration > TimeSpan.FromHours(12) ? TimeSpan.FromHours(12) : duration, cancellationToken);
        }
    }
}
