using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace IdleMasterExtended
{
    public enum BadgeScanStage { PrivateGames, BadgePages, OwnedGames, GameDetails }

    public sealed class BadgeScanProgress
    {
        public BadgeScanStage Stage { get; }
        public int Completed { get; }
        public int Total { get; }
        public BadgeScanProgress(BadgeScanStage stage, int completed, int total)
        {
            Stage = stage; Completed = completed; Total = total;
        }
    }

    /// <summary>Parses a complete badge snapshot before publishing any changes.</summary>
    public sealed class BadgeScanner
    {
        private const int MaximumBadgePages = 1000;
        private const int MaximumDetailReads = 4096;
        private readonly ICommunityClient community;
        private readonly IOwnedGamesReader ownedGames;
        private readonly int detailBudget;
        private readonly HashSet<int> privateApps;

        public BadgeScanner(ICommunityClient community, IOwnedGamesReader ownedGames = null, IEnumerable<int> privateApps = null)
            : this(community, ownedGames, MaximumDetailReads, privateApps)
        { }

        internal BadgeScanner(ICommunityClient community, IOwnedGamesReader ownedGames, int detailBudget, IEnumerable<int> privateApps = null)
        {
            if (detailBudget < 1 || detailBudget > MaximumDetailReads)
                throw new ArgumentOutOfRangeException(nameof(detailBudget));
            this.community = community ?? throw new ArgumentNullException(nameof(community));
            this.ownedGames = ownedGames;
            this.detailBudget = detailBudget;
            this.privateApps = new HashSet<int>(privateApps ?? Enumerable.Empty<int>());
        }

        public async Task<SteamReadResult<List<Badge>>> ScanAsync(string profileUrl, CancellationToken cancellationToken,
            Action<BadgeScanProgress> progress = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string profile;
            if (!TryProfileUrl(profileUrl, out profile))
                return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "The Steam profile address is invalid.");

            var snapshot = new Dictionary<int, Badge>();
            var pendingDetails = new Dictionary<int, Badge>();
            var pageCount = 1;
            HashSet<int> ownedApps = null;
            ReportProgress(progress, BadgeScanStage.BadgePages, 0, pageCount);
            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await community.GetAsync(profile + "/badges/?p=" + pageNumber.ToString(CultureInfo.InvariantCulture) + "&l=english", cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!read.IsSuccess)
                    return SteamReadResult<List<Badge>>.Failed(read.Status, read.Message);
                var document = ReadDocument(read.Value);
                var problem = PageProblem(document, read.Value);
                if (problem != null)
                    return SteamReadResult<List<Badge>>.Failed(problem.Status, problem.Message);
                if (document.DocumentNode.SelectSingleNode("//*[" + ClassToken("badges_sheet") + "]") == null)
                    return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "Steam's badge page could not be recognized. Your previous game list has been kept.");

                if (pageNumber == 1)
                {
                    var pages = ExtractPageCount(document);
                    if (!pages.IsSuccess)
                        return SteamReadResult<List<Badge>>.Failed(pages.Status, pages.Message);
                    pageCount = pages.Value;
                }

                var rows = document.DocumentNode.SelectNodes("//div[" + ClassToken("badge_row") + "]");
                if (rows == null)
                {
                    ReportProgress(progress, BadgeScanStage.BadgePages, pageNumber, pageCount);
                    continue; // A recognized empty badge sheet is a successful zero-game snapshot.
                }
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var overlay = row.SelectSingleNode(".//a[" + ClassToken("badge_row_overlay") + "]");
                    if (overlay == null)
                    {
                        if (row.SelectSingleNode(".//a[contains(@href,'/gamecards/')]") != null)
                            return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "Steam returned an incomplete game badge.");
                        continue; // Community/event badges have no Steam application ID.
                    }
                    var href = WebUtility.HtmlDecode(overlay.GetAttributeValue("href", string.Empty));
                    var match = Regex.Match(href, @"/gamecards/([0-9]+)(?:/|[?]|$)", RegexOptions.IgnoreCase);
                    if (!match.Success || Regex.IsMatch(href, @"[?&]border=1(?:&|$)"))
                        continue;
                    int appId;
                    if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out appId) || appId <= 0)
                        return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "Steam returned an invalid game ID.");

                    var title = row.SelectSingleNode(".//*[" + ClassToken("badge_title") + "]");
                    if (title == null && !privateApps.Contains(appId))
                        return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "Steam returned a badge without a game name.");
                    var directTitle = title == null ? "" : string.Join(" ", title.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Text).Select(n => n.InnerText));
                    var name = title == null ? "" : CleanText(string.IsNullOrWhiteSpace(directTitle) ? title.InnerText : directTitle);
                    if (privateApps.Contains(appId))
                    {
                        // Steam can omit counts for private games. Keep the exclusion visible,
                        // without inventing zero drops or requesting an ineligible card page.
                        int privateCards; double privateHours;
                        var known = TryStats(row, out privateCards, out privateHours);
                        var badge = new Badge { AppId = appId, Name = string.IsNullOrWhiteSpace(name) ? "App ID: " + appId : name,
                            IsPrivate = true, RemainingCard = known ? privateCards : -1, HoursPlayed = known ? privateHours : 0 };
                        if (!TryAddBadge(snapshot, badge)) return ConflictingBadges();
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(name))
                        return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "Steam returned a badge without a game name.");

                    int cards;
                    double hours;
                    if (!TryStats(row, out cards, out hours))
                    {
                        // Steam can leave this section blank in the badge index even when the
                        // individual card page contains an explicit remaining-drop status.
                        // A missing section or an unfamiliar nonempty status is still invalid.
                        if (!HasEmptyDropsSection(row))
                            return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "Steam's card counts could not be read. Your previous game list has been kept.");
                        // A complete, authenticated ownership set excludes badges left behind
                        // by games this account cannot idle. Obtain it before spending one
                        // detail request per empty row; unknown or nonempty rows are never hidden.
                        if (ownedApps == null && ownedGames != null)
                        {
                            ReportProgress(progress, BadgeScanStage.OwnedGames, 0, 1);
                            var ownership = await ownedGames.ReadAsync(profile, cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!ownership.IsSuccess)
                                return SteamReadResult<List<Badge>>.Failed(ownership.Status, ownership.Message);
                            if (ownership.Value == null || ownership.Value.Any(id => id <= 0))
                                return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "Steam's owned game list could not be verified. Your previous game list has been kept.");
                            ownedApps = new HashSet<int>(ownership.Value);
                            ReportProgress(progress, BadgeScanStage.OwnedGames, 1, 1);
                            ReportProgress(progress, BadgeScanStage.BadgePages, pageNumber - 1, pageCount);
                        }
                        if (ownedApps != null && !ownedApps.Contains(appId))
                            continue;

                        if (!pendingDetails.ContainsKey(appId))
                        {
                            if (pendingDetails.Count >= detailBudget)
                                return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "Steam returned too many badges with missing card counts. Your previous game list has been kept.");
                            pendingDetails.Add(appId, new Badge { AppId = appId, Name = name });
                        }
                        continue;
                    }
                    if (!TryAddBadge(snapshot, new Badge { AppId = appId, Name = name, RemainingCard = cards, HoursPlayed = hours }))
                        return ConflictingBadges();
                }
                ReportProgress(progress, BadgeScanStage.BadgePages, pageNumber, pageCount);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (pendingDetails.Count != 0)
                return await ReadDetailsAsync(pendingDetails.Values.ToList(), snapshot, profile, cancellationToken, progress).ConfigureAwait(false);
            return SteamReadResult<List<Badge>>.Succeeded(snapshot.Values.ToList());
        }

        private async Task<SteamReadResult<List<Badge>>> ReadDetailsAsync(List<Badge> pending,
            Dictionary<int, Badge> snapshot, string profile, CancellationToken token, Action<BadgeScanProgress> progress)
        {
            var sync = new object();
            var progressSync = new object();
            var next = 0;
            var completed = 0;
            var results = new Badge[pending.Count];
            SteamReadResult<Badge> failure = null;
            using (var abort = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                ReportProgress(progress, BadgeScanStage.GameDetails, 0, pending.Count);
                Func<Task> worker = async () =>
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        int index;
                        lock (sync)
                        {
                            if (abort.IsCancellationRequested || next == pending.Count) return;
                            index = next++;
                        }
                        try
                        {
                            var detail = await ReadCardPageAsync(pending[index], profile, abort.Token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (!detail.IsSuccess)
                            {
                                lock (sync) if (failure == null) failure = detail;
                                abort.Cancel();
                                return;
                            }
                            lock (sync)
                            {
                                if (abort.IsCancellationRequested) return;
                                results[index] = detail.Value;
                            }
                            lock (progressSync)
                                ReportProgress(progress, BadgeScanStage.GameDetails, ++completed, pending.Count);
                        }
                        catch (OperationCanceledException)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!abort.IsCancellationRequested) { abort.Cancel(); throw; }
                            return;
                        }
                        catch
                        {
                            abort.Cancel();
                            throw;
                        }
                    }
                };
                // Exactly two async workers; never allocate a task per game or bypass HTTP pacing.
                await Task.WhenAll(worker(), worker()).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (failure != null) return SteamReadResult<List<Badge>>.Failed(failure.Status, failure.Message);
                foreach (var detail in results)
                    if (!TryAddBadge(snapshot, detail)) return ConflictingBadges();
                return SteamReadResult<List<Badge>>.Succeeded(snapshot.Values.ToList());
            }
        }

        internal static void ReportProgress(Action<BadgeScanProgress> progress, BadgeScanStage stage, int completed, int total)
        {
            try { progress?.Invoke(new BadgeScanProgress(stage, completed, total)); }
            catch { /* Display progress is optional and cannot alter the verified snapshot. */ }
        }

        private static bool TryAddBadge(Dictionary<int, Badge> snapshot, Badge badge)
        {
            Badge previous;
            if (snapshot.TryGetValue(badge.AppId, out previous) &&
                (previous.RemainingCard != badge.RemainingCard || previous.HoursPlayed != badge.HoursPlayed || previous.IsPrivate != badge.IsPrivate))
                return false;
            snapshot[badge.AppId] = badge;
            return true;
        }

        private static SteamReadResult<List<Badge>> ConflictingBadges()
        {
            return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage,
                "Steam returned conflicting counts for the same game. Your previous game list has been kept.");
        }

        /// <summary>Returns a new badge; callers apply it only after a successful read.</summary>
        public async Task<SteamReadResult<Badge>> CheckAsync(Badge badge, string profileUrl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string profile;
            if (badge == null || badge.AppId <= 0 || !TryProfileUrl(profileUrl, out profile))
                return SteamReadResult<Badge>.Failed(SteamReadStatus.MalformedPage, "The Steam game or profile address is invalid.");
            return await ReadCardPageAsync(badge, profile, cancellationToken).ConfigureAwait(false);
        }

        private async Task<SteamReadResult<Badge>> ReadCardPageAsync(Badge badge, string profile, CancellationToken cancellationToken)
        {
            var read = await community.GetAsync(profile + "/gamecards/" + badge.StringId + "/?l=english", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!read.IsSuccess)
                return SteamReadResult<Badge>.Failed(read.Status, read.Message);
            var document = ReadDocument(read.Value);
            var problem = PageProblem(document, read.Value);
            if (problem != null)
                return SteamReadResult<Badge>.Failed(problem.Status, problem.Message);
            int cards;
            double hours;
            if (!TryStats(document.DocumentNode, out cards, out hours))
            {
                return SteamReadResult<Badge>.Failed(SteamReadStatus.MalformedPage,
                    "Steam's card counts could not be read. The previous count has been kept.");
            }
            return SteamReadResult<Badge>.Succeeded(new Badge
            {
                AppId = badge.AppId, Name = badge.Name, AveragePrice = badge.AveragePrice,
                RemainingCard = cards, HoursPlayed = hours
            });
        }

        private static HtmlDocument ReadDocument(string html)
        {
            var document = new HtmlDocument();
            document.LoadHtml(html ?? string.Empty);
            return document;
        }

        private static SteamReadResult<string> PageProblem(HtmlDocument document, string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Steam returned an empty response. Try again shortly.");
            if (SteamSessionService.IsChallengePage(document))
                return SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Steam is limiting requests. Wait a little and try again; your sign-in has been kept.");
            if (document.DocumentNode.SelectSingleNode("//*[@id='login_form' or @id='loginForm' or @id='login_container' or " + ClassToken("loginbox") + "]") != null
                || Regex.IsMatch(html, @"g_steamID\s*=\s*(?:false|""0""|'0')", RegexOptions.IgnoreCase)
                || document.DocumentNode.SelectSingleNode("//form[contains(@action,'/login')]") != null)
                return SteamReadResult<string>.Failed(SteamReadStatus.LoginRequired, "Sign in to Steam again to continue.");
            return null;
        }

        private static SteamReadResult<int> ExtractPageCount(HtmlDocument document)
        {
            var maximum = 1;
            var links = document.DocumentNode.SelectNodes("//a[" + ClassToken("pagelink") + "]");
            if (links != null)
            {
                foreach (var link in links)
                {
                    var href = WebUtility.HtmlDecode(link.GetAttributeValue("href", string.Empty));
                    var match = Regex.Match(href, @"(?:[?&])p=([0-9]+)(?:&|$)");
                    int page;
                    if (match.Success)
                    {
                        if (!int.TryParse(match.Groups[1].Value, out page) || page < 1 || page > MaximumBadgePages)
                            return SteamReadResult<int>.Failed(SteamReadStatus.MalformedPage, "Steam returned an invalid badge page count.");
                        maximum = Math.Max(maximum, page);
                    }
                }
            }
            return SteamReadResult<int>.Succeeded(maximum);
        }

        private static bool TryStats(HtmlNode scope, out int cards, out double hours)
        {
            cards = 0;
            hours = 0;
            var drops = scope.SelectSingleNode(".//*[" + ClassToken("badge_title_stats_drops") + "]");
            if (drops == null) return false;
            var cardNode = drops.SelectSingleNode(".//*[" + ClassToken("progress_info_bold") + "]");
            if (cardNode != null)
            {
                var count = Regex.Match(CleanText(cardNode.InnerText), @"\b([0-9]+)\s+(?:card\s+)?drops?\s+remaining\b", RegexOptions.IgnoreCase);
                if (count.Success)
                {
                    if (!int.TryParse(count.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out cards))
                        return false;
                }
                else if (!ExplicitNoDrops(CleanText(cardNode.InnerText)))
                    return false;
            }
            else
                return false;

            var hoursNode = scope.SelectSingleNode(".//*[" + ClassToken("badge_title_stats_playtime") + "]");
            if (hoursNode == null || string.IsNullOrWhiteSpace(CleanText(hoursNode.InnerText)))
                return true;
            var hoursText = CleanText(hoursNode.InnerText);
            var hoursMatch = Regex.Match(hoursText, @"[0-9]+(?:[.,][0-9]+)*");
            if (!hoursMatch.Success)
                return hoursText.IndexOf("no playtime", StringComparison.OrdinalIgnoreCase) >= 0;
            return Badge.TryParseHours(hoursMatch.Value, out hours);
        }

        private static bool HasEmptyDropsSection(HtmlNode row)
        {
            var drops = row.SelectSingleNode(".//*[" + ClassToken("badge_title_stats_drops") + "]");
            return drops != null && !drops.ChildNodes.Any(node => node.NodeType == HtmlNodeType.Element
                || (node.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(CleanText(node.InnerText))));
        }

        private static bool ExplicitNoDrops(string text)
        {
            return Regex.IsMatch(text, @"\b(?:no|0)\s+(?:card\s+)?drops?\s+remaining\b", RegexOptions.IgnoreCase);
        }

        private static string ClassToken(string token)
        {
            return "contains(concat(' ', normalize-space(@class), ' '), ' " + token + " ')";
        }

        private static string CleanText(string text)
        {
            return Regex.Replace(WebUtility.HtmlDecode(text ?? string.Empty), @"\s+", " ").Trim();
        }

        private static bool TryProfileUrl(string url, out string profile)
        {
            profile = null;
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps
                || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.Equals(uri.Host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(uri.AbsolutePath, @"^/(?:profiles/[0-9]{17}|id/[^/?#]+)/?$"))
                return false;
            profile = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return true;
        }
    }
}
