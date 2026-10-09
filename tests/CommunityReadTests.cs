using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
            await EmptyAndMalformedPagesAreDifferent();
            await HttpFailuresAndRedirectsAreTyped();
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
            scanner = new BadgeScanner(new FakeCommunity(SteamReadResult<string>.Succeeded(Page().Replace("</body>",
                "<a class='pagelink' href='?p=1001'>1001</a></body>"))));
            read = await scanner.ScanAsync(Profile, CancellationToken.None);
            Require(read.Status == SteamReadStatus.MalformedPage, "The pagination safety bound was not enforced.");
        }

        private static async Task HttpFailuresAndRedirectsAreTyped()
        {
            var handler = new FakeHandler((request, token) => Task.FromResult(Redirect("https://login.steampowered.com/login")));
            using (var client = new SteamHttpClient(handler, maxRetries: 0))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.LoginRequired && handler.Count == 1, "Login redirect classification failed.");
            }
            handler = new FakeHandler((request, token) => Task.FromResult(Redirect("https://example.com/")));
            using (var client = new SteamHttpClient(handler, maxRetries: 0))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.MalformedPage && handler.Count == 1, "An external redirect was followed.");
            }
            handler = new FakeHandler((request, token) => Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/badges/")
                ? Ok("page") : Redirect(Profile + "/badges/")));
            using (var client = new SteamHttpClient(handler, maxRetries: 0))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.IsSuccess && handler.Count == 2, "A same-origin redirect did not resolve.");
            }
            var attempts = 0;
            handler = new FakeHandler((request, token) => Task.FromResult(++attempts == 1
                ? new HttpResponseMessage((HttpStatusCode)429) : Ok("page")));
            using (var client = new SteamHttpClient(handler, maxRetries: 1))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.IsSuccess && handler.Count == 2, "A rate-limit response was not retried within the limit.");
            }
            handler = new FakeHandler((request, token) => { throw new HttpRequestException("Synthetic network failure"); });
            using (var client = new SteamHttpClient(handler, maxRetries: 1))
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
            using (var client = new SteamHttpClient(handler, TimeSpan.FromMilliseconds(30), 0))
            {
                var read = await client.GetAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.TransientFailure && handler.Count == 1, "A timeout was not classified.");
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
            var handler = new FakeHandler(async (request, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Ok("unreachable");
            });
            using (var cancellation = new CancellationTokenSource())
            using (var client = new SteamHttpClient(handler, TimeSpan.FromSeconds(1), 0))
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
            public FakeCommunity(params SteamReadResult<string>[] responses)
            {
                this.responses = new Queue<SteamReadResult<string>>(responses);
            }
            public Task<SteamReadResult<string>> GetAsync(string url, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Urls.Add(url);
                return Task.FromResult(responses.Dequeue());
            }
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
}
