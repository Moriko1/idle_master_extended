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
    /// <summary>Parses a complete badge snapshot before publishing any changes.</summary>
    public sealed class BadgeScanner
    {
        private const int MaximumBadgePages = 1000;
        private const int MaximumDetailReads = 4096;
        private readonly ICommunityClient community;
        private readonly IOwnedGamesReader ownedGames;
        private readonly int detailBudget;

        public BadgeScanner(ICommunityClient community, IOwnedGamesReader ownedGames = null)
            : this(community, ownedGames, MaximumDetailReads)
        { }

        internal BadgeScanner(ICommunityClient community, IOwnedGamesReader ownedGames, int detailBudget)
        {
            if (detailBudget < 1 || detailBudget > MaximumDetailReads)
                throw new ArgumentOutOfRangeException(nameof(detailBudget));
            this.community = community ?? throw new ArgumentNullException(nameof(community));
            this.ownedGames = ownedGames;
            this.detailBudget = detailBudget;
        }

        public async Task<SteamReadResult<List<Badge>>> ScanAsync(string profileUrl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string profile;
            if (!TryProfileUrl(profileUrl, out profile))
                return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "The Steam profile address is invalid.");

            var snapshot = new Dictionary<int, Badge>();
            var pageCount = 1;
            var detailReads = 0;
            HashSet<int> ownedApps = null;
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
                    continue; // A recognized empty badge sheet is a successful zero-game snapshot.
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
                    if (title == null)
                        return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "Steam returned a badge without a game name.");
                    var directTitle = string.Join(" ", title.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Text).Select(n => n.InnerText));
                    var name = CleanText(string.IsNullOrWhiteSpace(directTitle) ? title.InnerText : directTitle);
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
                        // Once this scan has a complete verified ownership set, absent apps
                        // cannot be idled by this account. Skip only genuinely empty index rows.
                        if (ownedApps != null && !ownedApps.Contains(appId))
                            continue;
                        if (++detailReads > detailBudget)
                            return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.MalformedPage, "Steam returned too many badges with missing card counts. Your previous game list has been kept.");
                        var detail = await ReadCardPageAsync(new Badge { AppId = appId, Name = name }, profile, cancellationToken).ConfigureAwait(false);
                        if (detail.Result.IsSuccess)
                        {
                            snapshot[appId] = detail.Result.Value;
                            continue;
                        }
                        if (detail.Result.Status != SteamReadStatus.MalformedPage || !detail.EmptyDrops || ownedGames == null)
                            return SteamReadResult<List<Badge>>.Failed(detail.Result.Status, detail.Result.Message);
                        if (ownedApps == null)
                        {
                            var ownership = await ownedGames.ReadAsync(profile, cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!ownership.IsSuccess)
                                return SteamReadResult<List<Badge>>.Failed(ownership.Status, ownership.Message);
                            ownedApps = ownership.Value;
                        }
                        if (!ownedApps.Contains(appId))
                            continue;
                        return SteamReadResult<List<Badge>>.Failed(detail.Result.Status, detail.Result.Message);
                    }
                    snapshot[appId] = new Badge { AppId = appId, Name = name, RemainingCard = cards, HoursPlayed = hours };
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return SteamReadResult<List<Badge>>.Succeeded(snapshot.Values.ToList());
        }

        /// <summary>Returns a new badge; callers apply it only after a successful read.</summary>
        public async Task<SteamReadResult<Badge>> CheckAsync(Badge badge, string profileUrl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string profile;
            if (badge == null || badge.AppId <= 0 || !TryProfileUrl(profileUrl, out profile))
                return SteamReadResult<Badge>.Failed(SteamReadStatus.MalformedPage, "The Steam game or profile address is invalid.");
            return (await ReadCardPageAsync(badge, profile, cancellationToken).ConfigureAwait(false)).Result;
        }

        private async Task<CardPageRead> ReadCardPageAsync(Badge badge, string profile, CancellationToken cancellationToken)
        {
            var read = await community.GetAsync(profile + "/gamecards/" + badge.StringId + "/?l=english", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!read.IsSuccess)
                return new CardPageRead(SteamReadResult<Badge>.Failed(read.Status, read.Message), false);
            var document = ReadDocument(read.Value);
            var problem = PageProblem(document, read.Value);
            if (problem != null)
                return new CardPageRead(SteamReadResult<Badge>.Failed(problem.Status, problem.Message), false);
            int cards;
            double hours;
            if (!TryStats(document.DocumentNode, out cards, out hours))
            {
                var empty = document.DocumentNode.SelectSingleNode("//*[" + ClassToken("badge_gamecard_page") + "]") != null
                    && document.DocumentNode.SelectSingleNode("//*[" + ClassToken("badge_title_stats_playtime") + "]") != null
                    && HasEmptyDropsSection(document.DocumentNode);
                return new CardPageRead(SteamReadResult<Badge>.Failed(SteamReadStatus.MalformedPage,
                    "Steam's card counts could not be read. The previous count has been kept."), empty);
            }
            return new CardPageRead(SteamReadResult<Badge>.Succeeded(new Badge
            {
                AppId = badge.AppId, Name = badge.Name, AveragePrice = badge.AveragePrice,
                RemainingCard = cards, HoursPlayed = hours
            }), false);
        }

        private sealed class CardPageRead
        {
            public SteamReadResult<Badge> Result { get; }
            public bool EmptyDrops { get; }
            public CardPageRead(SteamReadResult<Badge> result, bool emptyDrops)
            {
                Result = result;
                EmptyDrops = emptyDrops;
            }
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
            if (document.DocumentNode.SelectSingleNode("//*[@id='login_form' or @id='loginForm' or @id='login_container' or " + ClassToken("loginbox") + "]") != null
                || Regex.IsMatch(html, @"g_steamID\s*=\s*(?:false|""0""|'0')", RegexOptions.IgnoreCase)
                || document.DocumentNode.SelectSingleNode("//form[contains(@action,'/login')]") != null)
                return SteamReadResult<string>.Failed(SteamReadStatus.LoginRequired, "Sign in to Steam again to continue.");
            var text = CleanText(document.DocumentNode.InnerText);
            if (text.IndexOf("too many requests", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("verify you are human", StringComparison.OrdinalIgnoreCase) >= 0)
                return SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Steam is limiting requests. Wait a little and try again.");
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
