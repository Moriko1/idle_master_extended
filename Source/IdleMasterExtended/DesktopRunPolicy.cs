using System;
namespace IdleMasterExtended
{
    internal sealed class SteamClientLossPolicy
    {
        private TimeSpan? absentSince;
        public string Observe(SteamClientPresence client, SteamAccountPresence account, TimeSpan now, bool running)
        {
            if (account == SteamAccountPresence.Changed && running) return "steam_account_changed_pause";
            if (client == SteamClientPresence.Present) { absentSince = null; return null; }
            // Unknown observations do not pause or erase evidence. Confirm absence again before acting.
            if (client != SteamClientPresence.Absent) return null;
            if (!absentSince.HasValue) absentSince = now;
            return running && now - absentSince.Value >= TimeSpan.FromSeconds(3) ? "steam_closed_pause" : null;
        }
    }
    internal enum RunNotice { None, Paused, Completed }
    internal sealed class RunNoticePolicy
    {
        private IdleRunState previous = IdleRunState.Stopped;
        public RunNotice Observe(IdleRunState state, bool noRemainingCards)
        {
            var wasPaused = previous == IdleRunState.Paused || previous == IdleRunState.Faulted;
            var wasActive = previous == IdleRunState.Starting || previous == IdleRunState.Running || wasPaused;
            var notice = (state == IdleRunState.Paused || state == IdleRunState.Faulted) && wasActive && !wasPaused
                ? RunNotice.Paused : state == IdleRunState.Completed && wasActive && noRemainingCards
                    ? RunNotice.Completed : RunNotice.None;
            previous = state;
            return notice;
        }
    }
}
