using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace IdleMasterExtended.Tests
{
    internal static class PrivateGamesTests
    {
        private const string SteamId = "76561198000000001";
        private const string Profile = "https://steamcommunity.com/profiles/" + SteamId;
        private const string Token = "synthetic-private-games-token";
        private static JavaScriptSerializer Json() => new JavaScriptSerializer();

        public static async Task RunAllAsync()
        {
            await AuthenticatedReadUsesAnEmptyRequest();
            await SameAccountProofIsRequired();
            await UnknownAndMalformedListsAreRejected();
            await ErrorsAndRedirectsKeepTheirClassification();
            await TransientFailuresRetryWithoutBecomingEmpty();
            await CancellationPropagates();
        }

        private static async Task AuthenticatedReadUsesAnEmptyRequest()
        {
            var community = new FakeCommunity(Page(Proof()));
            var handler = new ApiHandler((request, token) =>
            {
                Require(request.Method == HttpMethod.Get && request.RequestUri.Scheme == "https"
                    && request.RequestUri.IsDefaultPort && request.RequestUri.Host == "api.steampowered.com"
                    && request.RequestUri.AbsolutePath == "/IAccountPrivateAppsService/GetPrivateAppList/v1/",
                    "Private-game credentials were sent to an unexpected method or origin.");
                Require(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization"),
                    "The privacy API was sent ordinary Community cookies or extra credentials.");
                var input = Json().Deserialize<Dictionary<string, object>>(Query(request.RequestUri, "input_json"));
                Require(input.Count == 0 && Query(request.RequestUri, "steamid") == null
                    && Query(request.RequestUri, "access_token") == Token,
                    "The private-game request did not use the account-scoped empty-request contract.");
                return Task.FromResult(Reply("{\"response\":{\"private_apps\":{\"appids\":[10,20]}}}"));
            });
            var read = await Reader(community, handler).ReadPrivateAsync(Profile, CancellationToken.None);
            Require(read.IsSuccess && read.Value.SetEquals(new[] { 10, 20 }) && handler.Calls == 1,
                "The authenticated private-app list was lost or duplicated.");
            Require(community.RequestedLimit == SteamHttpClient.MaximumResponseLimit,
                "The privacy reader did not retain the bounded large SSR response path.");
            foreach (var body in new[] { "{\"response\":{\"private_apps\":{\"appids\":[]}}}", "{\"response\":{\"private_apps\":{}}}" })
            {
                read = await ReadApi(body);
                Require(read.IsSuccess && read.Value.Count == 0, "A verified empty private-app message was rejected.");
            }
        }

        private static async Task SameAccountProofIsRequired()
        {
            await RejectProof(root => root.Remove("UserConfig"), SteamReadStatus.MalformedPage);
            await RejectProof(root => ((Dictionary<string, object>)root["UserConfig"])["steamid"] = "0", SteamReadStatus.LoginRequired);
            await RejectProof(root => ((Dictionary<string, object>)root["UserConfig"])["steamid"] = "76561198000000002", SteamReadStatus.LoginRequired);
            await RejectProof(root => ChangeLoader(root, 0, loader => loader["steamid"] = "76561198000000002"), SteamReadStatus.LoginRequired);
            await RejectProof(root => ChangeLoader(root, 1, loader => loader["steamid"] = "76561198000000002"), SteamReadStatus.LoginRequired);
            await RejectProof(root => ChangeLoader(root, 0, loader => loader["strWebAPIToken"] = " "), SteamReadStatus.MalformedPage);
            await RejectProof(root => ChangeLoader(root, 1, loader => loader["bOwnProfile"] = false), SteamReadStatus.LoginRequired);
            await RejectProof(root => ((Dictionary<string, object>)root["Config"])["WEBAPI_BASE_URL"] = "https://example.com/", SteamReadStatus.MalformedPage);
            var handler = new ApiHandler((request, token) => Task.FromResult(Reply("{}")));
            var read = await Reader(new FakeCommunity(Page(Proof())), handler)
                .ReadPrivateAsync("https://steamcommunity.com/profiles/00000000000000000", CancellationToken.None);
            Require(read.Status == SteamReadStatus.MalformedPage && handler.Calls == 0,
                "An invalid target profile was used for an account-private read.");
        }

        private static async Task UnknownAndMalformedListsAreRejected()
        {
            foreach (var body in new[]
            {
                "{}", "{\"response\":{}}", "{\"response\":{\"private_apps\":null}}",
                "{\"response\":{\"private_apps\":[]}}", "{\"response\":{\"private_apps\":{\"appids\":null}}}",
                "{\"response\":{\"private_apps\":{\"appids\":{}}}}", "Not JSON",
                "{\"response\":{\"private_apps\":{\"appids\":[10,10]}}}",
                "{\"response\":{\"private_apps\":{\"appids\":[0]}}}",
                "{\"response\":{\"private_apps\":{\"appids\":[-10]}}}",
                "{\"response\":{\"private_apps\":{\"appids\":[2147483648]}}}",
                "{\"response\":{\"private_apps\":{\"appids\":[true]}}}",
                "{\"response\":{\"private_apps\":{\"appids\":[\"10\"]}}}",
                "{\"response\":{\"private_apps\":{\"appids\":[10.0]}}}",
                "{\"response\":{\"private_apps\":{\"appids\":[10.5]}}}",
                "{\"response\":{\"private_apps\":{\"appids\":[1e2]}}}"
            })
            {
                var read = await ReadApi(body);
                Require(read.Status == SteamReadStatus.MalformedPage && read.Value == null,
                    "An unknown or malformed privacy response became an empty private-app list.");
            }
            var oversized = "{\"response\":{\"private_apps\":{\"appids\":[" + string.Join(",", Enumerable.Range(1, 100001)) + "]}}}";
            Require((await ReadApi(oversized)).Status == SteamReadStatus.MalformedPage,
                "The private-app count bound was bypassed.");
        }

        private static async Task ErrorsAndRedirectsKeepTheirClassification()
        {
            foreach (var result in new[] { null, "15", "2" })
                Require((await ReadApi("{\"response\":{\"private_apps\":{\"appids\":[10]}}}", result)).Status == SteamReadStatus.MalformedPage,
                    "HTTP success bypassed Steam's private-list result code.");
            var duplicateResult = new ApiHandler((request, token) =>
            {
                var response = Reply("{\"response\":{\"private_apps\":{}}}");
                response.Headers.TryAddWithoutValidation("x-eresult", "1");
                return Task.FromResult(response);
            });
            Require((await Reader(new FakeCommunity(Page(Proof())), duplicateResult).ReadPrivateAsync(Profile, CancellationToken.None)).Status == SteamReadStatus.MalformedPage,
                "Ambiguous result headers became a private-app list.");
            foreach (var code in new[] { HttpStatusCode.Found, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
            {
                var handler = new ApiHandler((request, token) =>
                {
                    var response = new HttpResponseMessage(code);
                    if (code == HttpStatusCode.Found) response.Headers.Location = new Uri("https://example.com/");
                    return Task.FromResult(response);
                });
                var read = await Reader(new FakeCommunity(Page(Proof())), handler).ReadPrivateAsync(Profile, CancellationToken.None);
                Require(read.Status == (code == HttpStatusCode.Unauthorized ? SteamReadStatus.LoginRequired : SteamReadStatus.MalformedPage)
                    && read.Value == null && handler.Calls == 1, "A redirect or denied privacy read lost its classification or was followed.");
            }
        }

        private static async Task TransientFailuresRetryWithoutBecomingEmpty()
        {
            var attempts = 0;
            var recovering = new ApiHandler((request, token) =>
            {
                attempts++;
                return Task.FromResult(attempts < 3 ? new HttpResponseMessage((HttpStatusCode)429)
                    : Reply("{\"response\":{\"private_apps\":{\"appids\":[10]}}}"));
            });
            var read = await Reader(new FakeCommunity(Page(Proof())), recovering).ReadPrivateAsync(Profile, CancellationToken.None);
            Require(read.IsSuccess && read.Value.SetEquals(new[] { 10 }) && recovering.Calls == 3,
                "A rate-limited private-app read did not retry before applying a verified result.");
            foreach (var fail in new Func<Task<HttpResponseMessage>>[]
            {
                () => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)),
                () => Task.FromException<HttpResponseMessage>(new OperationCanceledException()),
                () => Task.FromException<HttpResponseMessage>(new HttpRequestException())
            })
            {
                var handler = new ApiHandler((request, token) => fail());
                read = await Reader(new FakeCommunity(Page(Proof())), handler).ReadPrivateAsync(Profile, CancellationToken.None);
                Require(read.Status == SteamReadStatus.TransientFailure && read.Value == null && handler.Calls == 3,
                    "A failed private-app read discarded known state or became an empty list.");
            }
        }

        private static async Task CancellationPropagates()
        {
            var handler = new ApiHandler(async (request, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Reply("{}");
            });
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.CancelAfter(30);
                var canceled = false;
                try { await Reader(new FakeCommunity(Page(Proof())), handler).ReadPrivateAsync(Profile, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Require(canceled && handler.Calls == 1, "Private-list cancellation became an empty or failed successful result.");
            }
        }

        private static OwnedGamesReader Reader(FakeCommunity community, ApiHandler handler) =>
            new OwnedGamesReader(community, () => handler);

        private static async Task RejectProof(Action<Dictionary<string, object>> mutate, SteamReadStatus status)
        {
            var proof = Proof();
            mutate(proof);
            var handler = new ApiHandler((request, token) => Task.FromResult(Reply("{}")));
            var read = await Reader(new FakeCommunity(Page(proof)), handler).ReadPrivateAsync(Profile, CancellationToken.None);
            Require(read.Status == status && handler.Calls == 0,
                "Invalid account proof sent a privacy credential or lost its classification.");
        }

        private static Task<SteamReadResult<HashSet<int>>> ReadApi(string body, string result = "1")
        {
            var handler = new ApiHandler((request, token) => Task.FromResult(Reply(body, result)));
            return Reader(new FakeCommunity(Page(Proof())), handler).ReadPrivateAsync(Profile, CancellationToken.None);
        }

        private static Dictionary<string, object> Proof() => new Dictionary<string, object>
        {
            { "UserConfig", new Dictionary<string, object> { { "steamid", SteamId } } },
            { "Config", new Dictionary<string, object> { { "WEBAPI_BASE_URL", "https://api.steampowered.com/" } } },
            { "loaderData", new object[]
                {
                    Json().Serialize(new Dictionary<string, object> { { "steamid", SteamId }, { "strWebAPIToken", Token } }),
                    Json().Serialize(new Dictionary<string, object> { { "steamid", SteamId }, { "bOwnProfile", true } })
                }
            }
        };

        private static void ChangeLoader(Dictionary<string, object> root, int index, Action<Dictionary<string, object>> change)
        {
            var array = (object[])root["loaderData"];
            var loader = Json().Deserialize<Dictionary<string, object>>((string)array[index]);
            change(loader);
            array[index] = Json().Serialize(loader);
        }

        private static string Page(Dictionary<string, object> proof) =>
            "<html><script type='application/json' id='valve-ssr-data'>" + Json().Serialize(proof) + "</script></html>";

        private static HttpResponseMessage Reply(string body, string result = "1")
        {
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            if (result != null) reply.Headers.TryAddWithoutValidation("x-eresult", result);
            return reply;
        }

        private static string Query(Uri uri, string key)
        {
            var match = uri.Query.TrimStart('?').Split('&').Select(part => part.Split(new[] { '=' }, 2))
                .FirstOrDefault(parts => parts[0] == key);
            return match == null || match.Length < 2 ? null : Uri.UnescapeDataString(match[1]);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FakeCommunity : IBoundedCommunityClient
        {
            private readonly string html;
            public int RequestedLimit { get; private set; }
            public FakeCommunity(string html) { this.html = html; }
            public Task<SteamReadResult<string>> GetAsync(string url, CancellationToken token) =>
                GetAsync(url, SteamHttpClient.DefaultResponseLimit, token);
            public Task<SteamReadResult<string>> GetAsync(string url, int maximumResponseBytes, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                RequestedLimit = maximumResponseBytes;
                return Task.FromResult(SteamReadResult<string>.Succeeded(html));
            }
        }

        private sealed class ApiHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
            public int Calls { get; private set; }
            public ApiHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                return send(request, token);
            }
        }
    }
}
