using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended.Tests
{
    internal static class CommunityReadTests
    {
        private const string Profile = "https://steamcommunity.com/profiles/76561198000000001";

        public static async Task RunAllAsync()
        {
            await SnapshotIsCompleteAndPaginationIsNumeric();
            await FailedReadsKeepPriorCounts();
            await EmptyIndexDropsRequireDetailEvidence();
            await VerifiedOwnershipSkipsEmptyUnownedRows();
            await DetailsAreDeduplicatedWithinOneAtomicScan();
            await ParallelDetailsAreBoundedAndCancelable();
            await LargeScanRequestBudgetProof();
            await OwnedGamesTests.RunAllAsync();
            await EmptyAndMalformedPagesAreDifferent();
            await HttpFailuresAndRedirectsAreTyped();
            await RequestBudgetsSurviveClientReplacement();
            await ProductionSpacingRemainsBounded();
            await RetryAfterUsesMonotonicCooldowns();
            await CanceledWaitDoesNotEraseTheCooldown();
            await ConcurrentWaitersRespectSpacingAndExtendedCooldowns();
            await ResponseLimitsAndBodyCancellationAreEnforced();
            await CancellationPropagates();
        }

        private static async Task SnapshotIsCompleteAndPaginationIsNumeric()
        {
            var first = Page(Row(10, "A &amp; B", "3 card drops remaining", "1,5 hrs on record"))
                .Replace("</body>", "<a class='selected pagelink' href='?p=3'>3</a><a class='pagelink' href='?p=2'>2</a></body>");
            var client = new FakeCommunity(SteamReadResult<string>.Succeeded(first),
                SteamReadResult<string>.Succeeded(Page(Row(20, "Second", "No card drops remaining", "1,234.5 hrs on record"))),
                SteamReadResult<string>.Succeeded(Page(Row(30, "Third", "1 card drop remaining", "0.5 hrs on record"),
                    Row(40, "Foil", "8 card drops remaining", "1 hrs on record").Replace("/gamecards/40/", "/gamecards/40/?border=1"))));
            var result = await new BadgeScanner(client).ScanAsync(Profile, CancellationToken.None);
            Require(result.IsSuccess && result.Value.Count == 3, "The complete normal-card snapshot was not returned.");
            Require(result.Value.Single(b => b.AppId == 10).Name == "A & B", "Game title decoding failed.");
            Require(result.Value.Single(b => b.AppId == 10).HoursPlayed == 1.5, "Comma decimal playtime was misread.");
            Require(result.Value.Single(b => b.AppId == 20).HoursPlayed == 1234.5, "Thousands separators were misread.");
            Require(result.Value.Single(b => b.AppId == 20).RemainingCard == 0, "Explicit no-drops text was misread.");
            Require(client.Urls.Count == 3 && client.Urls[0].Contains("?p=1&l=english")
                && client.Urls[1].Contains("?p=2&l=english") && client.Urls[2].Contains("?p=3&l=english"),
                "Badge pages were skipped, repeated or fetched without the requested language.");
        }

        private static async Task FailedReadsKeepPriorCounts()
        {
            var old = new Badge { AppId = 10, Name = "Game", RemainingCard = 5, HoursPlayed = 1 };
            var client = new FakeCommunity(SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Try later"));
            var check = await new BadgeScanner(client).CheckAsync(old, Profile, CancellationToken.None);
            Require(check.Status == SteamReadStatus.TransientFailure && check.Value == null
                && old.RemainingCard == 5 && old.HoursPlayed == 1, "A failed card read changed previous counts.");

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(Row(10, "Game", "No card drops remaining", "2 hrs on record"))));
            check = await new BadgeScanner(client).CheckAsync(old, Profile, CancellationToken.None);
            Require(check.IsSuccess && check.Value.RemainingCard == 0 && old.RemainingCard == 5,
                "A successful card read should return a separate snapshot.");

            var first = Page(Row(10, "Game", "3 card drops remaining", "1 hrs on record"))
                .Replace("</body>", "<a class='pagelink' href='?p=2'>2</a></body>");
            client = new FakeCommunity(SteamReadResult<string>.Succeeded(first),
                SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Try later"));
            var scan = await new BadgeScanner(client).ScanAsync(Profile, CancellationToken.None);
            Require(scan.Status == SteamReadStatus.TransientFailure && scan.Value == null,
                "An incomplete multipage snapshot was published.");
        }

        private static async Task EmptyAndMalformedPagesAreDifferent()
        {
            var scanner = new BadgeScanner(new FakeCommunity(SteamReadResult<string>.Succeeded(Page())));
            var read = await scanner.ScanAsync(Profile, CancellationToken.None);
            Require(read.IsSuccess && read.Value.Count == 0, "A recognized empty badge sheet should succeed.");
            scanner = new BadgeScanner(new FakeCommunity(SteamReadResult<string>.Succeeded("<html><body>Unexpected response</body></html>")));
            read = await scanner.ScanAsync(Profile, CancellationToken.None);
            Require(read.Status == SteamReadStatus.MalformedPage, "An unrecognized page was treated as no games.");
            scanner = new BadgeScanner(new FakeCommunity(SteamReadResult<string>.Succeeded("<html><div id='login_container'></div></html>")));
            read = await scanner.ScanAsync(Profile, CancellationToken.None);
            Require(read.Status == SteamReadStatus.LoginRequired, "A login page was not classified.");
            scanner = new BadgeScanner(new FakeCommunity(SteamReadResult<string>.Succeeded(Page(Row(10, "Game", "unrecognized card information", "2 hrs on record")))));
            read = await scanner.ScanAsync(Profile, CancellationToken.None);
            Require(read.Status == SteamReadStatus.MalformedPage, "Unknown drop text was silently converted to zero.");
            foreach (var status in new[] { "99 cards collected", "Level 3" })
            {
                var page = Page(Row(10, "Game", status, "2 hrs on record"));
                scanner = new BadgeScanner(new FakeCommunity(SteamReadResult<string>.Succeeded(page)));
                read = await scanner.ScanAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.MalformedPage, "Unrelated numeric status became a remaining-drop count.");
                var previous = new Badge { AppId = 10, Name = "Game", RemainingCard = 5, HoursPlayed = 1 };
                scanner = new BadgeScanner(new FakeCommunity(SteamReadResult<string>.Succeeded(page)));
                var check = await scanner.CheckAsync(previous, Profile, CancellationToken.None);
                Require(check.Status == SteamReadStatus.MalformedPage && previous.RemainingCard == 5,
                    "Unrelated detail-page digits replaced a previous card count.");
                var community = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(EmptyDropRow(10))),
                    SteamReadResult<string>.Succeeded(page.Replace("badge_row is_link", "badge_row badge_gamecard_page")));
                var ownership = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int> { 10 }));
                var fallback = await new BadgeScanner(community, ownership).ScanAsync(Profile, CancellationToken.None);
                Require(fallback.Status == SteamReadStatus.MalformedPage && ownership.Calls == 1,
                    "Ownership proof hid a nonempty unrecognized detail status.");
            }
            var popupOnly = EmptyDropRow(10).Replace("<div class='badge_title_stats_drops'>  </div>",
                "<div class='badge_title_stats_drops'><div class='how_to_dropcards'>No card drops remaining</div></div>");
            scanner = new BadgeScanner(new FakeCommunity(SteamReadResult<string>.Succeeded(Page(popupOnly))));
            read = await scanner.ScanAsync(Profile, CancellationToken.None);
            Require(read.Status == SteamReadStatus.MalformedPage, "Help-popup text supplied a zero count without a status node.");
            scanner = new BadgeScanner(new FakeCommunity(SteamReadResult<string>.Succeeded(Page().Replace("</body>",
                "<a class='pagelink' href='?p=1001'>1001</a></body>"))));
            read = await scanner.ScanAsync(Profile, CancellationToken.None);
            Require(read.Status == SteamReadStatus.MalformedPage, "The pagination safety bound was not enforced.");
        }

        private static async Task EmptyIndexDropsRequireDetailEvidence()
        {
            var index = Page(Row(10, "Known", "3 card drops remaining", "1 hr on record"), EmptyDropRow(20));
            var client = new FakeCommunity(SteamReadResult<string>.Succeeded(index),
                SteamReadResult<string>.Succeeded(Page(Row(20, "Detail", "No card drops remaining", "2.2 hrs on record"))));
            var scan = await new BadgeScanner(client).ScanAsync(Profile, CancellationToken.None);
            Require(scan.IsSuccess && scan.Value.Count == 2 && scan.Value.Single(b => b.AppId == 20).RemainingCard == 0
                && scan.Value.Single(b => b.AppId == 20).HoursPlayed == 2.2,
                "An empty index status was not resolved using explicit detail-page evidence.");
            Require(client.Urls.Count == 2 && client.Urls[1].EndsWith("/gamecards/20/?l=english"),
                "The fallback did not read the individual Steam card page.");

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(EmptyDropRow(20))),
                SteamReadResult<string>.Succeeded(Page(Row(20, "Detail", "4 card drops remaining", "2.2 hrs on record"))));
            scan = await new BadgeScanner(client).ScanAsync(Profile, CancellationToken.None);
            Require(scan.IsSuccess && scan.Value.Single().RemainingCard == 4,
                "A detail-page count was silently replaced by zero.");

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(index),
                SteamReadResult<string>.Succeeded(Page(EmptyDropRow(20))));
            scan = await new BadgeScanner(client).ScanAsync(Profile, CancellationToken.None);
            Require(scan.Status == SteamReadStatus.MalformedPage && scan.Value == null,
                "An empty detail page was treated as zero or published a partial snapshot.");

            foreach (var status in new[] { SteamReadStatus.TransientFailure, SteamReadStatus.LoginRequired })
            {
                client = new FakeCommunity(SteamReadResult<string>.Succeeded(index),
                    SteamReadResult<string>.Failed(status, "Synthetic detail failure"));
                scan = await new BadgeScanner(client).ScanAsync(Profile, CancellationToken.None);
                Require(scan.Status == status && scan.Value == null,
                    "A failed detail read lost its typed failure or published a partial snapshot.");
            }

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(
                EmptyDropRow(20).Replace("badge_title_stats_drops", "unknown_status"))));
            scan = await new BadgeScanner(client).ScanAsync(Profile, CancellationToken.None);
            Require(scan.Status == SteamReadStatus.MalformedPage && client.Urls.Count == 1,
                "A missing status section triggered unsupported fallback requests.");

            var responses = new List<SteamReadResult<string>>
            {
                SteamReadResult<string>.Succeeded(Page(Enumerable.Range(1, 4).Select(EmptyDropRow).ToArray()))
            };
            responses.AddRange(Enumerable.Range(1, 3).Select(id => SteamReadResult<string>.Succeeded(
                Page(Row(id, "Detail", "No card drops remaining", "")))));
            client = new FakeCommunity(responses.ToArray());
            scan = await new BadgeScanner(client, null, 3).ScanAsync(Profile, CancellationToken.None);
            Require(scan.Status == SteamReadStatus.MalformedPage && scan.Value == null && client.Urls.Count == 1,
                "Detail-page fallback requests exceeded their finite per-scan limit.");

            responses = new List<SteamReadResult<string>>
            {
                SteamReadResult<string>.Succeeded(Page(Enumerable.Range(1, 130).Select(EmptyDropRow).ToArray()))
            };
            responses.AddRange(Enumerable.Range(1, 130).Select(id => SteamReadResult<string>.Succeeded(
                Page(Row(id, "Detail", "No card drops remaining", "")))));
            client = new FakeCommunity(responses.ToArray());
            scan = await new BadgeScanner(client).ScanAsync(Profile, CancellationToken.None);
            Require(scan.IsSuccess && scan.Value.Count == 130 && scan.Value.All(badge => badge.RemainingCard == 0)
                && client.Urls.Count == 131, "A legitimate large scan exceeded an arbitrary small-account detail limit.");
        }

        private static string EmptyDropRow(int appId)
        {
            return Row(appId, "Game", "", "2.2 hrs on record")
                .Replace("<span class='extra progress_info_bold'></span>", "  ");
        }


        private static async Task VerifiedOwnershipSkipsEmptyUnownedRows()
        {
            var index = Page(Row(10, "Known", "3 card drops remaining", "1 hr on record"), EmptyDropRow(20), EmptyDropRow(30));
            var detail = Page(EmptyDropRow(20).Replace("badge_row is_link", "badge_row badge_gamecard_page"));
            var client = new FakeCommunity(SteamReadResult<string>.Succeeded(index),
                SteamReadResult<string>.Succeeded(detail), SteamReadResult<string>.Succeeded(detail));
            var owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int> { 10 }));
            var scan = await new BadgeScanner(client, owned).ScanAsync(Profile, CancellationToken.None);
            Require(scan.IsSuccess && scan.Value.Count == 1 && scan.Value[0].RemainingCard == 3
                && owned.Calls == 1 && client.Urls.Count == 1,
                "Proven unowned empty index rows caused unnecessary detail requests or invented card counts.");

            var manyEmpty = new List<string> { Row(10, "Known", "3 card drops remaining", "1 hr on record") };
            manyEmpty.AddRange(Enumerable.Range(20, 1000).Select(EmptyDropRow));
            client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(manyEmpty.ToArray())),
                SteamReadResult<string>.Succeeded(detail));
            owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int> { 10 }));
            scan = await new BadgeScanner(client, owned).ScanAsync(Profile, CancellationToken.None);
            Require(scan.IsSuccess && scan.Value.Count == 1 && owned.Calls == 1 && client.Urls.Count == 1,
                "A thousand verified unowned empty badges still caused detail-page traffic.");

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(EmptyDropRow(20), EmptyDropRow(30))));
            owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int>()));
            scan = await new BadgeScanner(client, owned).ScanAsync(Profile, CancellationToken.None);
            Require(scan.IsSuccess && scan.Value.Count == 0 && owned.Calls == 1 && client.Urls.Count == 1,
                "A first scan with a verified empty library did not produce a valid empty queue.");

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(index), SteamReadResult<string>.Succeeded(detail));
            owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int> { 10, 20 }));
            scan = await new BadgeScanner(client, owned).ScanAsync(Profile, CancellationToken.None);
            Require(scan.Status == SteamReadStatus.MalformedPage && scan.Value == null,
                "An owned game's unknown count was silently treated as zero.");

            foreach (var status in new[] { SteamReadStatus.TransientFailure, SteamReadStatus.LoginRequired, SteamReadStatus.MalformedPage })
            {
                client = new FakeCommunity(SteamReadResult<string>.Succeeded(index), SteamReadResult<string>.Succeeded(detail));
                owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Failed(status, "Synthetic library failure"));
                scan = await new BadgeScanner(client, owned).ScanAsync(Profile, CancellationToken.None);
                Require(scan.Status == status && scan.Value == null, "An unverified library supplied ownership proof.");
            }

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(index), SteamReadResult<string>.Succeeded(Page(EmptyDropRow(20))));
            owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int> { 10 }));
            scan = await new BadgeScanner(client, owned).ScanAsync(Profile, CancellationToken.None);
            Require(scan.IsSuccess && scan.Value.Count == 1 && owned.Calls == 1 && client.Urls.Count == 1,
                "An unowned empty index row was fetched even after complete ownership proof.");

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(index), SteamReadResult<string>.Succeeded(Page(EmptyDropRow(20))));
            owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int> { 10, 20 }));
            scan = await new BadgeScanner(client, owned).ScanAsync(Profile, CancellationToken.None);
            Require(scan.Status == SteamReadStatus.MalformedPage && scan.Value == null && client.Urls.Count == 2,
                "Verified ownership hid an owned game's malformed detail count.");

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(
                Row(20, "Game", "99 cards collected", "1 hr on record").Replace("badge_title_stats_drops", "unrelated_status"))));
            owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int>()));
            scan = await new BadgeScanner(client, owned).ScanAsync(Profile, CancellationToken.None);
            Require(scan.Status == SteamReadStatus.MalformedPage && client.Urls.Count == 1 && owned.Calls == 0,
                "Unrelated progress digits were mistaken for a remaining-drop count.");
        }

        private static async Task DetailsAreDeduplicatedWithinOneAtomicScan()
        {
            var first = Page(EmptyDropRow(10), Row(20, "Known", "3 card drops remaining", "1 hr on record"))
                .Replace("</body>", "<a class='pagelink' href='?p=2'>2</a></body>");
            var second = Page(EmptyDropRow(10), EmptyDropRow(10), Row(20, "Known", "3 card drops remaining", "1 hr on record"));
            var client = new FakeCommunity(SteamReadResult<string>.Succeeded(first), SteamReadResult<string>.Succeeded(second),
                SteamReadResult<string>.Succeeded(Page(Row(10, "Detail", "No card drops remaining", "2.2 hrs on record"))));
            var owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int> { 10, 20 }));
            var progress = new List<BadgeScanProgress>();
            var scan = await new BadgeScanner(client, owned, 1).ScanAsync(Profile, CancellationToken.None, progress.Add);
            Require(scan.IsSuccess && scan.Value.Count == 2 && client.Urls.Count == 3 && owned.Calls == 1,
                "Duplicate empty badges across pages consumed more than one fresh detail read.");
            Require(client.Urls[1].Contains("?p=2&l=english") && client.Urls[2].Contains("/gamecards/10/"),
                "Detail traffic began before the complete badge index was collected.");
            var detailStart = progress.FindIndex(item => item.Stage == BadgeScanStage.GameDetails && item.Completed == 0);
            Require(detailStart > 0 && progress.Take(detailStart).Any(item => item.Stage == BadgeScanStage.BadgePages && item.Completed == 2)
                && progress.Last().Stage == BadgeScanStage.GameDetails && progress.Last().Completed == 1 && progress.Last().Total == 1,
                "Progress did not distinguish complete badge pages from unique detail checks.");

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(first),
                SteamReadResult<string>.Failed(SteamReadStatus.TransientFailure, "Synthetic later-page outage"));
            scan = await new BadgeScanner(client, owned).ScanAsync(Profile, CancellationToken.None);
            Require(scan.Status == SteamReadStatus.TransientFailure && scan.Value == null && client.Urls.Count == 2,
                "A later badge-page failure spent detail traffic or published a partial zero-card queue.");

            foreach (var duplicate in new[] { Row(10, "Same game", "4 card drops remaining", "1 hr on record"),
                Row(10, "Same game", "3 card drops remaining", "1.5 hrs on record"),
                Row(10, "Same game", "unrecognized status", "1 hr on record") })
            {
                client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(
                    Row(10, "Same game", "3 card drops remaining", "1 hr on record"), duplicate)));
                scan = await new BadgeScanner(client).ScanAsync(Profile, CancellationToken.None);
                Require(scan.Status == SteamReadStatus.MalformedPage && scan.Value == null,
                    "A conflicting or malformed duplicate silently replaced verified counts.");
            }

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(EmptyDropRow(10),
                Row(10, "Same game", "4 card drops remaining", "2.2 hrs on record"))),
                SteamReadResult<string>.Succeeded(Page(Row(10, "Detail", "No card drops remaining", "2.2 hrs on record"))));
            scan = await new BadgeScanner(client, owned).ScanAsync(Profile, CancellationToken.None);
            Require(scan.Status == SteamReadStatus.MalformedPage && scan.Value == null,
                "A conflicting detail and explicit index count produced a partial snapshot.");

            client = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(EmptyDropRow(10))),
                SteamReadResult<string>.Succeeded(Page(Row(10, "Detail", "No card drops remaining", "2.2 hrs on record"))),
                SteamReadResult<string>.Succeeded(Page(EmptyDropRow(10))),
                SteamReadResult<string>.Succeeded(Page(Row(10, "Detail", "4 card drops remaining", "2.2 hrs on record"))));
            var scanner = new BadgeScanner(client, owned);
            var zero = await scanner.ScanAsync(Profile, CancellationToken.None);
            var renewed = await scanner.ScanAsync(Profile, CancellationToken.None);
            Require(zero.IsSuccess && zero.Value.Single().RemainingCard == 0 && renewed.IsSuccess && renewed.Value.Single().RemainingCard == 4
                && client.Urls.Count == 4,
                "A prior zero-count cache hid newly available drops on a later scan.");
        }

        private static async Task ParallelDetailsAreBoundedAndCancelable()
        {
            var owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int> { 1, 2, 3 }));
            var community = new PendingDetailsCommunity();
            var scan = new BadgeScanner(community, owned).ScanAsync(Profile, CancellationToken.None);
            await community.TwoStarted.Task;
            Require(community.Started == 2 && community.Peak == 2 && !scan.IsCompleted,
                "Detail checks were serialized or started more than two concurrent requests.");
            community.Complete(1, SteamReadStatus.Success);
            await community.ThreeStarted.Task;
            Require(community.Peak == 2, "The next detail bypassed the two-worker limit.");
            community.Complete(2, SteamReadStatus.Success); community.Complete(3, SteamReadStatus.Success);
            var result = await scan;
            Require(result.IsSuccess && result.Value.Count == 3 && community.InFlight == 0,
                "Successful parallel details did not yield a complete snapshot.");

            foreach (var failure in new[] { SteamReadStatus.TransientFailure, SteamReadStatus.LoginRequired, SteamReadStatus.MalformedPage })
            {
                community = new PendingDetailsCommunity();
                scan = new BadgeScanner(community, owned).ScanAsync(Profile, CancellationToken.None);
                await community.TwoStarted.Task;
                community.Complete(1, failure);
                result = await scan;
                Require(result.Status == failure && result.Value == null && community.Started == 2
                    && community.Cancelled == 1 && community.InFlight == 0,
                    "A failed detail lost its typed failure, published a partial queue, or left its peer running.");
            }

            using (var cancel = new CancellationTokenSource())
            {
                community = new PendingDetailsCommunity();
                scan = new BadgeScanner(community, owned).ScanAsync(Profile, cancel.Token);
                await community.TwoStarted.Task; cancel.Cancel();
                var cancelled = false;
                try { await scan; } catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && community.Started == 2 && community.Cancelled == 2 && community.InFlight == 0,
                    "User cancellation was masked as a read failure or left queued detail requests alive.");
            }
        }

        private static async Task LargeScanRequestBudgetProof()
        {
            var pages = new[] { Page(Enumerable.Range(20, 400).Select(EmptyDropRow).Concat(new[] { EmptyDropRow(10) }).ToArray())
                .Replace("</body>", "<a class='pagelink' href='?p=3'>3</a></body>"),
                Page(Enumerable.Range(420, 400).Select(EmptyDropRow).ToArray()),
                Page(Enumerable.Range(820, 200).Select(EmptyDropRow).ToArray()) };
            var clock = new FakeSteamRequestClock();
            var sends = new List<TimeSpan>();
            var handler = new FakeHandler((request, token) =>
            {
                sends.Add(clock.Elapsed);
                if (request.RequestUri.AbsolutePath.Contains("/gamecards/"))
                    return Task.FromResult(Ok(Page(Row(10, "Detail", "No card drops remaining", "2.2 hrs on record"))));
                var page = int.Parse(request.RequestUri.Query.Split('=')[1].Split('&')[0]);
                return Task.FromResult(Ok(pages[page - 1]));
            });
            using (var community = new SteamHttpClient(handler, requestBudget: new SteamRequestBudget(clock)))
            {
                var owned = new FakeOwnedGames(SteamReadResult<HashSet<int>>.Succeeded(new HashSet<int> { 10 }));
                var result = await new BadgeScanner(community, owned).ScanAsync(Profile, CancellationToken.None);
                Require(result.IsSuccess && result.Value.Count == 1 && result.Value[0].RemainingCard == 0
                    && handler.Count == 4 && owned.Calls == 1,
                    "A thousand empty unowned index rows expanded beyond three pages and one owned detail request.");
                var spacing = sends[1] - sends[0];
                Require(spacing > TimeSpan.Zero && sends.Skip(1).Select((at, index) => at - sends[index]).All(gap => gap == spacing)
                    && clock.Elapsed.Ticks == spacing.Ticks * 3,
                    "Scanner optimization bypassed shared request pacing or introduced extra waits.");
            }
        }

        private sealed class PendingDetailsCommunity : ICommunityClient
        {
            private readonly object sync = new object();
            private readonly Dictionary<int, TaskCompletionSource<SteamReadResult<string>>> pending = new Dictionary<int, TaskCompletionSource<SteamReadResult<string>>>();
            public readonly TaskCompletionSource<bool> TwoStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ThreeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Started { get; private set; }
            public int InFlight { get; private set; }
            public int Peak { get; private set; }
            public int Cancelled { get; private set; }
            public async Task<SteamReadResult<string>> GetAsync(string url, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (url.Contains("/badges/"))
                    return SteamReadResult<string>.Succeeded(Page(Enumerable.Range(1, 3).Select(EmptyDropRow).ToArray()));
                var id = int.Parse(url.Split(new[] { "/gamecards/" }, StringSplitOptions.None)[1].Split('/')[0]);
                var response = new TaskCompletionSource<SteamReadResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (sync)
                {
                    pending.Add(id, response); Started++; InFlight++; Peak = Math.Max(Peak, InFlight);
                    if (Started == 2) TwoStarted.TrySetResult(true);
                    if (Started == 3) ThreeStarted.TrySetResult(true);
                }
                using (token.Register(() => { lock (sync) { Cancelled++; response.TrySetCanceled(); } }))
                {
                    try { return await response.Task.ConfigureAwait(false); }
                    finally { lock (sync) InFlight--; }
                }
            }
            public void Complete(int id, SteamReadStatus status)
            {
                lock (sync) pending[id].TrySetResult(status == SteamReadStatus.Success
                    ? SteamReadResult<string>.Succeeded(Page(Row(id, "Detail", "No card drops remaining", "2.2 hrs on record")))
                    : SteamReadResult<string>.Failed(status, "Synthetic detail failure"));
            }
        }

        private sealed class FakeOwnedGames : IOwnedGamesReader
        {
            private readonly SteamReadResult<HashSet<int>> result;
            public int Calls { get; private set; }
            public FakeOwnedGames(SteamReadResult<HashSet<int>> result) { this.result = result; }
            public Task<SteamReadResult<HashSet<int>>> ReadAsync(string profileUrl, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Calls++;
                return Task.FromResult(result);
            }
        }

        private static async Task HttpFailuresAndRedirectsAreTyped()
        {
            var handler = new FakeHandler((request, token) => Task.FromResult(Redirect("https://login.steampowered.com/login")));
            using (var client = new SteamHttpClient(handler, maxRetries: 0, requestBudget: NewBudget()))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.LoginRequired && handler.Count == 1, "Login redirect classification failed.");
            }
            foreach (var address in new[] { "http://login.steampowered.com/login", "https://login.steampowered.com:444/login",
                "https://user@login.steampowered.com/login", "http://steamcommunity.com/login" })
            {
                handler = new FakeHandler((request, token) => Task.FromResult(Redirect(address)));
                using (var client = new SteamHttpClient(handler, requestBudget: NewBudget()))
                {
                    var read = await client.GetAsync(Profile, CancellationToken.None);
                    Require(read.Status == SteamReadStatus.MalformedPage && handler.Count == 1,
                        "An unsafe login redirect was treated as evidence of expired login.");
                }
            }
            handler = new FakeHandler((request, token) => Task.FromResult(Redirect("https://example.com/")));
            using (var client = new SteamHttpClient(handler, maxRetries: 0, requestBudget: NewBudget()))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.MalformedPage && handler.Count == 1, "An external redirect was followed.");
            }
            handler = new FakeHandler((request, token) => Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/badges/")
                ? Ok("page") : Redirect(Profile + "/badges/")));
            using (var client = new SteamHttpClient(handler, maxRetries: 0, requestBudget: NewBudget()))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.IsSuccess && handler.Count == 2, "A same-origin redirect did not resolve.");
            }
            var attempts = 0;
            handler = new FakeHandler((request, token) => Task.FromResult(++attempts == 1
                ? new HttpResponseMessage((HttpStatusCode)429) : Ok("page")));
            using (var client = new SteamHttpClient(handler, maxRetries: 1, requestBudget: NewBudget()))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.TransientFailure && read.Value == null && handler.Count == 1, "A rate-limit response was retried immediately or treated as lost login.");
            }
            handler = new FakeHandler((request, token) => { throw new HttpRequestException("Synthetic network failure"); });
            using (var client = new SteamHttpClient(handler, maxRetries: 1, requestBudget: NewBudget()))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.TransientFailure && handler.Count == 2, "Network retries were not bounded.");
                var invalid = await client.GetAsync("https://example.com/profile", CancellationToken.None);
                Require(invalid.Status == SteamReadStatus.MalformedPage && handler.Count == 2, "An unofficial host was requested.");
            }
            handler = new FakeHandler(async (request, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Ok("unreachable");
            });
            using (var client = new SteamHttpClient(handler, TimeSpan.FromMilliseconds(30), 0, NewBudget()))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.TransientFailure && handler.Count == 1, "A timeout was not classified.");
            }
        }

        internal static SteamRequestBudget NewBudget() => new SteamRequestBudget(new FakeSteamRequestClock());

        private static async Task RequestBudgetsSurviveClientReplacement()
        {
            var clock = new FakeSteamRequestClock();
            var records = new List<SteamRequestDiagnostic>();
            var budget = new SteamRequestBudget(clock, records.Add);
            var sends = new List<TimeSpan>();
            var handler = new FakeHandler((request, token) =>
            {
                sends.Add(clock.Elapsed);
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429));
            });
            using (var first = new SteamHttpClient(handler, maxRetries: 3, requestBudget: budget))
            {
                var read = await first.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.TransientFailure && sends.Count == 1,
                    "Throttling triggered an internal retry despite an explicit retry allowance.");
            }
            handler = new FakeHandler((request, token) => { sends.Add(clock.Elapsed); return Task.FromResult(Ok("page")); });
            using (var replacement = new SteamHttpClient(handler, requestBudget: budget))
            {
                Require((await replacement.GetAsync(Profile, CancellationToken.None)).IsSuccess, "The replacement transport could not recover.");
                Require((await replacement.GetAsync(Profile, CancellationToken.None)).IsSuccess, "A successive read could not recover.");
            }
            Require(sends.SequenceEqual(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(61) }),
                "Replacing a client erased its cooldown or bypassed one-second send spacing.");
            Require(records.Count == 1 && records[0].Surface == SteamRequestSurface.Community
                && records[0].Failure == SteamRequestFailure.RateLimited && records[0].HttpStatus == 429,
                "HTTP diagnostics did not keep a categorical, credential-free rate-limit record.");

            foreach (var code in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized })
            {
                handler = new FakeHandler((request, token) => Task.FromResult(new HttpResponseMessage(code)));
                using (var client = new SteamHttpClient(handler, requestBudget: NewBudget()))
                {
                    var read = await client.GetAsync(Profile, CancellationToken.None);
                    Require(read.Status == (code == HttpStatusCode.Unauthorized ? SteamReadStatus.LoginRequired : SteamReadStatus.TransientFailure)
                        && handler.Count == 1 && read.Value == null, "A default transport retried or lost its typed HTTP failure.");
                }
            }
            using (var client = new SteamHttpClient(new FakeHandler((request, token) => Task.FromResult(Ok("page")))))
                Require(ReferenceEquals(client.RequestBudget, SteamRequestBudget.Shared), "Production clients did not use the process-wide budget.");
        }

        private static async Task ProductionSpacingRemainsBounded()
        {
            var clock = new FakeSteamRequestClock();
            var budget = new SteamRequestBudget(clock, minimumSpacing: SteamRequestBudget.Shared.MinimumSpacing);
            Require(budget.MinimumSpacing == TimeSpan.FromMilliseconds(500), "Production reads must remain paced at two per second.");
            for (var i = 0; i < 100; i++) await budget.WaitForTurnAsync(CancellationToken.None);
            Require(clock.Elapsed == TimeSpan.FromMilliseconds(500 * 99), "The production scan budget did not enforce spacing.");
        }

        private static async Task RetryAfterUsesMonotonicCooldowns()
        {
            foreach (var kind in new[] { "delta", "date", "missing", "short", "malformed", "forbidden" })
            {
                var clock = new FakeSteamRequestClock();
                var budget = new SteamRequestBudget(clock);
                await budget.WaitForTurnAsync(CancellationToken.None);
                using (var response = new HttpResponseMessage(kind == "forbidden" ? HttpStatusCode.Forbidden : (HttpStatusCode)429))
                {
                    if (kind == "delta") response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(125));
                    if (kind == "date") response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.UtcNow.AddSeconds(125));
                    if (kind == "short") response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                    if (kind == "malformed") response.Headers.TryAddWithoutValidation("Retry-After", "not-a-delay");
                    budget.ObserveResponse(SteamRequestSurface.Community, response);
                }
                // Advancing the wall clock cannot prematurely release a monotonic cooldown.
                clock.UtcNow = clock.UtcNow.AddDays(7);
                await budget.WaitForTurnAsync(CancellationToken.None);
                var expected = kind == "delta" || kind == "date" ? 125 : kind == "forbidden" ? 30 : 60;
                Require(clock.Elapsed == TimeSpan.FromSeconds(expected), "A Retry-After or minimum cooldown was shortened or affected by wall-clock changes.");
            }
        }

        private static async Task CanceledWaitDoesNotEraseTheCooldown()
        {
            var clock = new FakeSteamRequestClock();
            var budget = new SteamRequestBudget(clock);
            await budget.WaitForTurnAsync(CancellationToken.None);
            using (var response = new HttpResponseMessage((HttpStatusCode)429))
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                budget.ObserveResponse(SteamRequestSurface.Community, response);
            }
            using (var cancellation = new CancellationTokenSource())
            {
                clock.BeforeDelay = duration => cancellation.Cancel();
                var canceled = false;
                try { await budget.WaitForTurnAsync(cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Require(canceled && clock.Elapsed == TimeSpan.Zero, "Cancellation failed to interrupt cooldown spacing.");
            }
            clock.BeforeDelay = null;
            await budget.WaitForTurnAsync(CancellationToken.None);
            Require(clock.Elapsed == TimeSpan.FromSeconds(120), "Cancellation consumed a future send slot or cleared a server cooldown.");

            var handler = new FakeHandler((request, token) => Task.FromResult(Ok("page")));
            using (var client = new SteamHttpClient(handler, TimeSpan.FromMilliseconds(30), requestBudget: budget))
            {
                using (var response = new HttpResponseMessage((HttpStatusCode)429)) budget.ObserveResponse(SteamRequestSurface.Community, response);
                Require((await client.GetAsync(Profile, CancellationToken.None)).IsSuccess && handler.Count == 1,
                    "Waiting for a server cooldown consumed the request timeout before a send.");
            }
        }

        private static async Task ConcurrentWaitersRespectSpacingAndExtendedCooldowns()
        {
            var clock = new FakeSteamRequestClock { YieldDelays = true };
            var budget = new SteamRequestBudget(clock);
            await budget.WaitForTurnAsync(CancellationToken.None);
            var extended = false;
            clock.BeforeDelay = duration =>
            {
                if (extended) return;
                extended = true;
                using (var response = new HttpResponseMessage((HttpStatusCode)429))
                    budget.ObserveResponse(SteamRequestSurface.Community, response);
            };
            await Task.WhenAll(Enumerable.Range(0, 3).Select(index => budget.WaitForTurnAsync(CancellationToken.None)));
            Require(clock.Elapsed == TimeSpan.FromSeconds(62) && clock.Delays[0] == TimeSpan.FromSeconds(1),
                "Concurrent sends bypassed an in-flight cooldown extension or shared spacing.");
        }


        private static async Task ResponseLimitsAndBodyCancellationAreEnforced()
        {
            const int size = 9 * 1024 * 1024;
            var handler = new FakeHandler((request, token) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TestReadStream(size)) }));
            using (var client = new SteamHttpClient(handler, maxRetries: 0, requestBudget: NewBudget()))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.MalformedPage,
                    "An unknown-length response exceeded the ordinary eight-MiB limit.");
                read = await client.GetAsync(Profile, SteamHttpClient.MaximumResponseLimit, CancellationToken.None);
                Require(read.IsSuccess && read.Value.Length == size,
                    "An explicit bounded SSR read could not exceed the ordinary response limit.");
            }
            handler = new FakeHandler((request, token) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TestReadStream(0, true)) }));
            using (var client = new SteamHttpClient(handler, TimeSpan.FromMilliseconds(30), 0, NewBudget()))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.TransientFailure, "The attempt timeout did not cancel a response body read.");
            }
            handler = new FakeHandler((request, token) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TestReadStream(0, true)) }));
            using (var cancellation = new CancellationTokenSource())
            using (var client = new SteamHttpClient(handler, TimeSpan.FromSeconds(1), 0, NewBudget()))
            {
                cancellation.CancelAfter(30);
                var canceled = false;
                try { await client.GetAsync(Profile, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Require(canceled, "User cancellation did not propagate while reading the response body.");
            }
        }

        private static async Task CancellationPropagates()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var canceled = false;
                try { await new BadgeScanner(new FakeCommunity()).ScanAsync(Profile, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Require(canceled, "Scanner cancellation did not propagate.");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                var community = new FakeCommunity(SteamReadResult<string>.Succeeded(Page(EmptyDropRow(20))),
                    SteamReadResult<string>.Succeeded(Page(Row(20, "Detail", "No card drops remaining", ""))));
                community.BeforeRead = count => { if (count == 2) cancellation.Cancel(); };
                var canceled = false;
                try { await new BadgeScanner(community).ScanAsync(Profile, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Require(canceled && community.Urls.Count == 2,
                    "Cancellation during a detail fallback became a failed or partial snapshot.");
            }
            var handler = new FakeHandler(async (request, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Ok("unreachable");
            });
            using (var cancellation = new CancellationTokenSource())
            using (var client = new SteamHttpClient(handler, TimeSpan.FromSeconds(1), 0, NewBudget()))
            {
                cancellation.CancelAfter(30);
                var canceled = false;
                try { await client.GetAsync(Profile, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Require(canceled, "User cancellation became a transient failure.");
            }
        }

        private static string Page(params string[] rows)
        {
            return "<html><body><div class='badges_sheet extra'>" + string.Join("", rows) + "</div></body></html>";
        }
        private static string Row(int appId, string title, string cards, string hours)
        {
            return "<div class='extra badge_row is_link'><a class='selected badge_row_overlay' href='/gamecards/" + appId + "/'></a>"
                + "<div class='badge_title extra'>" + title + "<div>Level 1</div></div>"
                + "<div class='badge_title_stats'><div class='badge_title_stats_drops'><span class='extra progress_info_bold'>"
                + cards + "</span></div><div class='extra badge_title_stats_playtime'>" + hours + "</div></div></div>";
        }
        private static HttpResponseMessage Ok(string content)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        }
        private static HttpResponseMessage Redirect(string url)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(url);
            return response;
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FakeCommunity : ICommunityClient
        {
            private readonly Queue<SteamReadResult<string>> responses;
            public List<string> Urls { get; } = new List<string>();
            public Action<int> BeforeRead { get; set; }
            public FakeCommunity(params SteamReadResult<string>[] responses)
            {
                this.responses = new Queue<SteamReadResult<string>>(responses);
            }
            public Task<SteamReadResult<string>> GetAsync(string url, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Urls.Add(url);
                BeforeRead?.Invoke(Urls.Count);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(responses.Dequeue());
            }
        }
        private sealed class TestReadStream : Stream
        {
            private int remaining;
            private readonly bool waitForDispose;
            private readonly TaskCompletionSource<int> stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TestReadStream(int length, bool waitForDispose = false)
            {
                remaining = length;
                this.waitForDispose = waitForDispose;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = Math.Min(count, remaining);
                for (var index = 0; index < read; index++) buffer[offset + index] = (byte)'x';
                remaining -= read;
                return read;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                // Models .NET Framework network streams that ignore a mid-flight read token.
                if (waitForDispose) return stopped.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing && waitForDispose) stopped.TrySetException(new ObjectDisposedException(nameof(TestReadStream)));
                base.Dispose(disposing);
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
            public int Count { get; private set; }
            public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            {
                this.respond = respond;
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Count++;
                return respond(request, cancellationToken);
            }
        }
    }

    internal sealed class FakeSteamRequestClock : ISteamRequestClock
    {
        public TimeSpan Elapsed { get; private set; }
        public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = new List<TimeSpan>();
        public Action<TimeSpan> BeforeDelay { get; set; }
        public bool YieldDelays { get; set; }
        public async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(duration);
            BeforeDelay?.Invoke(duration);
            if (YieldDelays) await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Elapsed += duration;
            UtcNow += duration;
        }
    }
}
