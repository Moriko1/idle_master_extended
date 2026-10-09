using System;
using System.Collections.Generic;
using System.Linq;

namespace IdleMasterExtended
{
    public sealed class IdleSessionSummary
    {
        public bool Completed { get; internal set; }
        public TimeSpan ActiveTime { get; internal set; }
        public long CardsObserved { get; internal set; }
        public long? RemainingCards { get; internal set; }
        public int GamesCompleted { get; internal set; }
        public int RemainingGames { get; internal set; }
        public IdleMode Mode { get; internal set; }
    }

    // Only complete authenticated snapshots update the session. Queue edits and skipped games
    // never become card drops, and an interrupted read cannot become a completion.
    public sealed class IdleSessionTracker
    {
        private readonly Dictionary<int, int> counts = new Dictionary<int, int>();
        private long observed;
        private IdleMode mode;
        public bool Active { get; private set; }
        public long CardsObserved => observed;

        public void Start(IEnumerable<IdleGame> games, IdleMode idleMode)
        {
            counts.Clear(); observed = 0; mode = idleMode;
            foreach (var game in games) counts[game.AppId] = game.RemainingCards;
            Active = true;
        }

        public void Observe(IEnumerable<IdleGame> completeSnapshot)
        {
            if (!Active || mode == IdleMode.Whitelist) return;
            var snapshot = completeSnapshot.GroupBy(game => game.AppId).ToDictionary(group => group.Key, group => group.First().RemainingCards);
            foreach (var appId in counts.Keys.ToArray())
            {
                int latest;
                if (!snapshot.TryGetValue(appId, out latest)) latest = 0;
                if (latest < 0) continue;
                var previous = counts[appId];
                if (previous >= 0 && latest < previous) observed += previous - latest;
                counts[appId] = latest;
            }
        }

        public IdleSessionSummary Finish(bool completed, TimeSpan activeTime)
        {
            if (!Active) return null;
            Active = false;
            return new IdleSessionSummary {
                Completed = completed, ActiveTime = activeTime, CardsObserved = observed,
                RemainingCards = counts.Values.Any(value => value < 0) ? (long?)null : counts.Values.Sum(value => (long)value),
                GamesCompleted = counts.Values.Count(value => value == 0),
                RemainingGames = counts.Values.Count(value => value != 0), Mode = mode
            };
        }

        public void Abandon() { Active = false; counts.Clear(); }
    }
}
