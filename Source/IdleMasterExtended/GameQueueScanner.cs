using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    /// <summary>A complete queue and its independently verified per-game privacy snapshot.</summary>
    public sealed class GameQueueSnapshot
    {
        public List<Badge> Badges { get; private set; }
        public HashSet<int> PrivateAppIds { get; private set; }

        internal GameQueueSnapshot(IEnumerable<Badge> badges, IEnumerable<int> privateAppIds)
        {
            Badges = badges.ToList();
            PrivateAppIds = new HashSet<int>(privateAppIds);
        }
    }

    /// <summary>Publishes card or whitelist queues only after the account's private-game list is verified.</summary>
    public sealed class GameQueueScanner
    {
        private readonly ICommunityClient community;
        private readonly IOwnedGamesReader ownedGames;
        private readonly IPrivateGamesReader privateGames;

        public GameQueueScanner(ICommunityClient community, IOwnedGamesReader ownedGames,
            IPrivateGamesReader privateGames)
        {
            this.community = community ?? throw new ArgumentNullException(nameof(community));
            this.ownedGames = ownedGames ?? throw new ArgumentNullException(nameof(ownedGames));
            this.privateGames = privateGames ?? throw new ArgumentNullException(nameof(privateGames));
        }

        public async Task<SteamReadResult<GameQueueSnapshot>> ReadAsync(string profileUrl,
            IEnumerable<string> whitelist, CancellationToken cancellationToken,
            Func<HashSet<int>, Task> privacyVerified = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var whitelistSnapshot = whitelist?.ToList();
            var privacy = await privateGames.ReadPrivateAsync(profileUrl, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!privacy.IsSuccess)
                return SteamReadResult<GameQueueSnapshot>.Failed(privacy.Status, privacy.Message);
            if (privacy.Value == null || privacy.Value.Any(appId => appId <= 0))
                return SteamReadResult<GameQueueSnapshot>.Failed(SteamReadStatus.MalformedPage,
                    "Steam's private game list could not be verified. Your previous game list has been kept.");
            // Copy before further asynchronous reads; never edit the reader's set or
            // allow a later input mutation to change the queue's privacy evidence.
            var privateAppIds = new HashSet<int>(privacy.Value);
            // Positive privacy proof is actionable even if a later badge page fails.
            // Supply a separate copy so the callback cannot edit this scan's evidence.
            if (privacyVerified != null)
            {
                await privacyVerified(new HashSet<int>(privateAppIds)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            List<Badge> badges;
            if (whitelistSnapshot == null)
            {
                var read = await new BadgeScanner(community, ownedGames, privateAppIds)
                    .ScanAsync(profileUrl, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!read.IsSuccess)
                    return SteamReadResult<GameQueueSnapshot>.Failed(read.Status, read.Message);
                badges = read.Value;
            }
            else
            {
                badges = new List<Badge>();
                var added = new HashSet<int>();
                foreach (var value in whitelistSnapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int appId;
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out appId)
                        || appId <= 0 || !added.Add(appId))
                        continue;
                    badges.Add(new Badge
                    {
                        AppId = appId,
                        Name = "App ID: " + appId.ToString(CultureInfo.InvariantCulture),
                        RemainingCard = -1,
                        HoursPlayed = 0,
                        IsPrivate = privateAppIds.Contains(appId)
                    });
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return SteamReadResult<GameQueueSnapshot>.Succeeded(new GameQueueSnapshot(badges, privateAppIds));
        }
    }
}
