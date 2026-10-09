using System;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    public enum SteamReadStatus
    {
        Success,
        LoginRequired,
        TransientFailure,
        MalformedPage
    }

    /// <summary>A failed read has no value and never changes the previous game snapshot.</summary>
    public sealed class SteamReadResult<T>
    {
        public SteamReadStatus Status { get; private set; }
        public T Value { get; private set; }
        public string Message { get; private set; }
        public bool IsSuccess { get { return Status == SteamReadStatus.Success; } }

        private SteamReadResult(SteamReadStatus status, T value, string message)
        {
            Status = status;
            Value = value;
            Message = message ?? string.Empty;
        }

        public static SteamReadResult<T> Succeeded(T value)
        {
            return new SteamReadResult<T>(SteamReadStatus.Success, value, string.Empty);
        }

        public static SteamReadResult<T> Failed(SteamReadStatus status, string message)
        {
            if (status == SteamReadStatus.Success)
                throw new ArgumentException("A failed read must have a failure status.", nameof(status));
            return new SteamReadResult<T>(status, default(T), message);
        }
    }

    public interface ICommunityClient
    {
        Task<SteamReadResult<string>> GetAsync(string url, CancellationToken cancellationToken);
    }
}
