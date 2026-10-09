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

    public sealed class SystemIdleClock : IIdleClock
    {
        public DateTimeOffset UtcNow { get { return DateTimeOffset.UtcNow; } }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    public sealed class IdleHelperException : Exception
    {
        public IdleHelperException(string message) : base(message) { }
    }
}
