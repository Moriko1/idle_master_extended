using System;

namespace steam_idle
{
    /// <summary>
    /// Retains a verified local Steam account through server reconnects. Server
    /// connectivity is deliberately absent: BLoggedOn is not a local login check.
    /// https://partner.steamgames.com/doc/api/ISteamUser#BLoggedOn
    /// </summary>
    internal sealed class HelperConnectionGuard
    {
        private readonly ulong? expectedSteamId;
        private readonly TimeSpan unavailableGrace;
        private TimeSpan? unavailableSince;

        internal HelperConnectionGuard(ulong? expectedSteamId, TimeSpan? unavailableGrace = null)
        {
            this.expectedSteamId = expectedSteamId;
            this.unavailableGrace = unavailableGrace ?? TimeSpan.FromSeconds(5);
            if (this.unavailableGrace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(unavailableGrace));
        }

        // The monotonic clock is supplied by the caller so reconnect behavior can
        // be verified without sleeping or interacting with the Steam client.
        internal string Observe(bool parentExited, bool clientRunning, ulong currentSteamId, TimeSpan now)
        {
            var failure = Check(parentExited, clientRunning, expectedSteamId, currentSteamId, false);
            if (failure != "STEAM_OFFLINE")
            {
                unavailableSince = null;
                return failure;
            }
            if (!unavailableSince.HasValue) unavailableSince = now;
            return now - unavailableSince.Value >= unavailableGrace ? failure : null;
        }

        // Startup remains strict: a helper never reports READY for an unknown or
        // different account. Server disconnect callbacks alone do not prove that
        // the locally signed-in account changed.
        internal static string Check(bool parentExited, bool clientRunning,
            ulong? expectedSteamId, ulong currentSteamId, bool confirmedLogoff)
        {
            if (parentExited) return "PARENT_EXITED";
            if (currentSteamId != 0 && expectedSteamId.HasValue && currentSteamId != expectedSteamId.Value)
                return "ACCOUNT_MISMATCH";
            if (!clientRunning || currentSteamId == 0) return "STEAM_OFFLINE";
            return null;
        }
    }

    // Steam invokes callbacks inside RunCallbacks. Closing the form there would
    // dispose native callback registrations while Steam is still dispatching.
    internal sealed class HelperFailureLatch
    {
        private bool dispatching;
        private string pending;

        internal void BeginDispatch() { dispatching = true; }
        internal void EndDispatch() { dispatching = false; }
        internal void Request(string reason)
        {
            if (pending == null || reason == "ACCOUNT_MISMATCH" || reason == "PARENT_EXITED") pending = reason;
        }
        internal string TakePending()
        {
            if (dispatching) return null;
            var reason = pending;
            pending = null;
            return reason;
        }
    }
}
