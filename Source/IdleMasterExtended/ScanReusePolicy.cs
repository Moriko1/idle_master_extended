using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    /// <summary>The inputs which change the data read from Steam, rather than its local presentation.</summary>
    internal sealed class ScanQueueSource
    {
        private readonly bool whitelist;
        private readonly int[] whitelistAppIds;

        internal ScanQueueSource(bool whitelist, IEnumerable<string> whitelistIds)
        {
            this.whitelist = whitelist;
            var ids = new List<int>();
            var seen = new HashSet<int>();
            foreach (var value in whitelistIds ?? Enumerable.Empty<string>())
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && seen.Add(id))
                    ids.Add(id);
            whitelistAppIds = ids.ToArray();
        }

        internal bool Matches(ScanQueueSource other) => other != null && whitelist == other.whitelist &&
            whitelistAppIds.SequenceEqual(other.whitelistAppIds);
    }

    internal sealed class QueueSettingsSnapshot
    {
        internal ScanQueueSource Source { get; }
        private readonly bool fast, single, oneThenMany, onlyPlayed;
        private readonly string sort;
        private readonly HashSet<string> blacklist;

        internal QueueSettingsSnapshot(bool whitelist, bool fast, bool single, bool oneThenMany, bool onlyPlayed,
            string sort, IEnumerable<string> whitelistIds, IEnumerable<string> blacklistIds)
        {
            Source = new ScanQueueSource(whitelist, whitelistIds);
            this.fast = fast; this.single = single; this.oneThenMany = oneThenMany; this.onlyPlayed = onlyPlayed;
            this.sort = sort;
            blacklist = new HashSet<string>(blacklistIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        }

        internal bool RequiresFullScan(QueueSettingsSnapshot other) => !Source.Matches(other?.Source);
        internal bool HasRunChanges(QueueSettingsSnapshot other) => other == null || RequiresFullScan(other) ||
            fast != other.fast || single != other.single || oneThenMany != other.oneThenMany ||
            onlyPlayed != other.onlyPlayed || sort != other.sort || !blacklist.SetEquals(other.blacklist);
    }

    /// <summary>Reuses a complete short-lived scan only after fresh, matching privacy evidence.</summary>
    internal sealed class ScanReusePolicy
    {
        private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);
        private readonly Func<TimeSpan> elapsed;
        private TimeSpan completedAt;
        private ulong steamId;
        private ScanQueueSource source;
        private HashSet<int> privateAppIds;

        internal ScanReusePolicy(Func<TimeSpan> elapsed = null)
        {
            var stopwatch = Stopwatch.StartNew();
            this.elapsed = elapsed ?? (() => stopwatch.Elapsed);
        }

        internal void RecordComplete(ulong accountId, ScanQueueSource queueSource, IEnumerable<int> privateIds)
        {
            if (accountId == 0 || queueSource == null || privateIds == null) throw new ArgumentException("A complete verified scan is required.");
            var ids = new HashSet<int>(privateIds);
            if (ids.Any(id => id <= 0)) throw new ArgumentException("Private game IDs must be positive.");
            steamId = accountId; source = queueSource; privateAppIds = ids; completedAt = elapsed();
        }

        internal void Invalidate() { source = null; privateAppIds = null; steamId = 0; }

        internal bool CanReuse(ulong accountId, ScanQueueSource queueSource, bool resuming = false)
        {
            if (source == null) return false;
            var age = elapsed() - completedAt;
            if (resuming || steamId != accountId || !source.Matches(queueSource) || age < TimeSpan.Zero || age > MaximumAge)
            {
                Invalidate();
                return false;
            }
            return true;
        }

        // A successful false result requires a full scan. A failed result cannot start a run.
        internal async Task<SteamReadResult<bool>> VerifyForStartAsync(ulong accountId, ScanQueueSource queueSource,
            bool resuming, IPrivateGamesReader reader, string profileUrl, CancellationToken token,
            Func<HashSet<int>, Task> privacyVerified)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!CanReuse(accountId, queueSource, resuming)) return SteamReadResult<bool>.Succeeded(false);
                var privacy = await reader.ReadPrivateAsync(profileUrl, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!privacy.IsSuccess)
                {
                    Invalidate();
                    return SteamReadResult<bool>.Failed(privacy.Status, privacy.Message);
                }
                if (privacy.Value == null || privacy.Value.Any(id => id <= 0))
                {
                    Invalidate();
                    return SteamReadResult<bool>.Failed(SteamReadStatus.MalformedPage,
                        "Steam's private game list could not be verified. Your previous game list has been kept.");
                }
                var fresh = new HashSet<int>(privacy.Value);
                var unchanged = privateAppIds != null && privateAppIds.SetEquals(fresh);
                // Newly private games must be excluded immediately, even if the ensuing full
                // scan fails. Newly public games need that scan to restore their missing counts.
                if (privacyVerified != null) await privacyVerified(new HashSet<int>(fresh)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!unchanged) { Invalidate(); return SteamReadResult<bool>.Succeeded(false); }
                return SteamReadResult<bool>.Succeeded(CanReuse(accountId, queueSource));
            }
            catch { Invalidate(); throw; }
        }
    }
}
