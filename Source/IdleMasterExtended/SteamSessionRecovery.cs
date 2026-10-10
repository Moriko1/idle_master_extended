using System;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    // A Community access-cookie failure is not proof that Steam's remembered browser login expired.
    internal sealed class SteamSessionRecovery
    {
        private readonly Func<TimeSpan> elapsed;
        private TimeSpan retryAt;
        private SteamReadResult<SteamSession> deferredFailure;
        public SteamSessionRecovery(Func<TimeSpan> elapsed) { this.elapsed = elapsed; }
        public void Reset() { retryAt = TimeSpan.Zero; deferredFailure = null; }
        public async Task<SteamReadResult<SteamSession>> ValidateAsync(
            Func<Task<SteamReadResult<SteamSession>>> read,
            Func<Task<SteamReadStatus>> restore, Func<bool> accountChanged, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = await read();
            token.ThrowIfCancellationRequested();
            if (result.IsSuccess) { deferredFailure = null; return result; }
            if (result.Status != SteamReadStatus.LoginRequired || accountChanged()) return result;
            if (deferredFailure != null && elapsed() < retryAt) return deferredFailure;
            if (elapsed() < retryAt)
                return SteamReadResult<SteamSession>.Failed(SteamReadStatus.TransientFailure,
                    "Steam's saved sign-in is being checked. Retry shortly; your session has been kept.");
            retryAt = elapsed() + TimeSpan.FromMinutes(2);
            var restored = await restore();
            token.ThrowIfCancellationRequested();
            result = restored == SteamReadStatus.Success ? await read()
                : SteamReadResult<SteamSession>.Failed(restored, restored == SteamReadStatus.LoginRequired
                    ? "Steam's remembered sign-in expired. Sign in again to continue."
                    : "Steam's saved sign-in could not be checked. Retry when connected; your session has been kept.");
            token.ThrowIfCancellationRequested();
            deferredFailure = result.IsSuccess ? null : result;
            return result;
        }
    }
}
