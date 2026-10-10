using System;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    public interface IIdleHelper : IDisposable
    {
        int AppId { get; }
        ulong SteamId { get; }
        bool IsRunning { get; }
        Task Completion { get; }
    }

    public interface IIdleHelperFactory
    {
        Task<IIdleHelper> StartAsync(int appId, ulong expectedSteamId, CancellationToken cancellationToken);
    }

    public interface IIdleClock
    {
        DateTimeOffset UtcNow { get; }
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    public sealed class SystemIdleClock : IIdleClock, IMonotonicIdleClock
    {
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        public DateTimeOffset UtcNow { get { return DateTimeOffset.UtcNow; } }
        public TimeSpan Elapsed { get { return clock.Elapsed; } }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    public enum IdleHelperFailure
    {
        Unknown, SteamUnavailable, InitializationFailed, UnexpectedExit,
        AccountMismatch, ParentExited, InvalidProtocol
    }

    public sealed class IdleHelperException : Exception
    {
        public IdleHelperFailure Failure { get; private set; }

        public IdleHelperException(string message, IdleHelperFailure failure = IdleHelperFailure.Unknown)
            : base(message)
        {
            Failure = failure;
        }
    }
}
