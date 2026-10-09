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
        public int PrivateGamesSkipped { get; internal set; }
    }

    // Only complete authenticated snapshots update the session. Queue edits and skipped games
    // never become card drops, and an interrupted read cannot become a completion.
    public sealed class IdleSessionTracker
    {
        private readonly Dictionary<int, int> counts = new Dictionary<int, int>();
        private readonly HashSet<int> privateSkipped = new HashSet<int>();
        private long observed;
        private IdleMode mode;
        public bool Active { get; private set; }
        public long CardsObserved => observed;
        public int PrivateGamesSkipped => privateSkipped.Count;

        // Initial private IDs are queue candidates already filtered out by the caller.
        public void Start(IEnumerable<IdleGame> games, IdleMode idleMode, IEnumerable<int> initiallyPrivateGameIds = null)
        {
            counts.Clear(); privateSkipped.Clear(); observed = 0; mode = idleMode;
            if (initiallyPrivateGameIds != null)
                foreach (var appId in initiallyPrivateGameIds.Where(id => id > 0)) privateSkipped.Add(appId);
            foreach (var game in games)
                if (!privateSkipped.Contains(game.AppId)) counts[game.AppId] = game.RemainingCards;
            Active = true;
        }

        public void Observe(IEnumerable<IdleGame> completeSnapshot, IEnumerable<int> privateGameIds = null)
        {
            if (!Active) return;
            var privateIds = privateGameIds == null ? new HashSet<int>() : new HashSet<int>(privateGameIds);
            ExcludePrivateGames(privateIds);
            var snapshot = completeSnapshot.GroupBy(game => game.AppId).ToDictionary(group => group.Key, group => group.First().RemainingCards);
            if (privateGameIds != null)
            {
                // A complete snapshot plus explicit current privacy proof can restore a
                // formerly excluded candidate. Establish a new baseline; the excluded
                // interval cannot be claimed as observed card drops.
                foreach (var appId in privateSkipped.Where(id => !privateIds.Contains(id) && !counts.ContainsKey(id)))
                {
                    int latest;
                    if (snapshot.TryGetValue(appId, out latest) && (latest >= 0 || mode == IdleMode.Whitelist))
                        counts[appId] = latest;
                }
            }
            if (mode == IdleMode.Whitelist) return;
            foreach (var appId in counts.Keys.ToArray())
            {
                if (privateIds.Contains(appId)) continue;
                int latest;
                if (!snapshot.TryGetValue(appId, out latest)) latest = 0;
                if (latest < 0) continue;
                var previous = counts[appId];
                if (previous >= 0 && latest < previous) observed += previous - latest;
                counts[appId] = latest;
            }
        }

        // Independently verified positive privacy flags take effect even when a later
        // badge read fails. They remove candidates without inventing a card-count snapshot.
        public void ExcludePrivateGames(IEnumerable<int> privateGameIds)
        {
            if (!Active || privateGameIds == null) return;
            var privateIds = new HashSet<int>(privateGameIds);
            foreach (var appId in counts.Keys.Where(id => counts[id] != 0 && privateIds.Contains(id)).ToArray())
            { counts.Remove(appId); privateSkipped.Add(appId); }
        }

        public IdleSessionSummary Finish(bool completed, TimeSpan activeTime)
        {
            if (!Active) return null;
            Active = false;
            return new IdleSessionSummary {
                Completed = completed, ActiveTime = activeTime, CardsObserved = observed,
                RemainingCards = mode == IdleMode.Whitelist || counts.Values.Any(value => value < 0) ? (long?)null : counts.Values.Sum(value => (long)value),
                GamesCompleted = counts.Values.Count(value => value == 0),
                RemainingGames = counts.Values.Count(value => value != 0), Mode = mode, PrivateGamesSkipped = privateSkipped.Count
            };
        }

        public void Abandon() { Active = false; counts.Clear(); privateSkipped.Clear(); }
    }
}
