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
    internal static class OwnedGamesTests
    {
        private const string SteamId = "76561198000000001";
        private const string Profile = "https://steamcommunity.com/profiles/" + SteamId;
        private const string Token = "synthetic-token";
        private static JavaScriptSerializer Json() => new JavaScriptSerializer();

        public static async Task RunAllAsync()
        {
            await InclusiveRequestAndFamilyCountsAreVerified();
            await IdentityAndTokenProofIsRequired();
            await IncompleteAndErrorRepliesAreRejected();
            await ApiCancellationPropagates();
        }

        private static async Task InclusiveRequestAndFamilyCountsAreVerified()
        {
            var community = new FakeCommunity(Page(Proof()));
            var handler = new ApiHandler((request, token) =>
            {
                Require(request.Method == HttpMethod.Get && request.RequestUri.Scheme == "https"
                    && request.RequestUri.Host == "api.steampowered.com"
                    && request.RequestUri.AbsolutePath == "/IPlayerService/GetOwnedGames/v1/",
                    "Ownership credentials were directed to an unexpected origin or method.");
                var input = Json().Deserialize<Dictionary<string, object>>(Query(request.RequestUri, "input_json"));
                Require((string)input["steamid"] == SteamId && Query(request.RequestUri, "access_token") == Token,
                    "The request did not use the verified profile and template token.");
                foreach (var flag in new[] { "include_appinfo", "include_free_sub", "include_played_free_games", "include_family_licenses" })
                    Require(input.ContainsKey(flag) && input[flag] is bool && (bool)input[flag], "An ownership inclusion flag was omitted.");
                Require(input.ContainsKey("skip_unvetted_apps") && !(bool)input["skip_unvetted_apps"]
                    && !input.ContainsKey("appids_filter"), "The owned library was filtered.");
                return Task.FromResult(Reply("{\"response\":{\"game_count\":2,\"games\":[{\"appid\":10},{\"appid\":20,\"family_shared\":false},{\"appid\":30,\"family_shared\":true}]}}"));
            });
            var read = await new OwnedGamesReader(community, () => handler).ReadAsync(Profile, CancellationToken.None);
            Require(read.IsSuccess && read.Value.SetEquals(new[] { 10, 20, 30 }),
                "The inclusive library or conservative family membership was lost.");
            Require(community.RequestedLimit == SteamHttpClient.MaximumResponseLimit && handler.Calls == 1,
                "The SSR response limit was unbounded or the API request was repeated.");
            var empty = await ReadApi("{\"response\":{\"game_count\":0,\"games\":[]}}");
            Require(empty.IsSuccess && empty.Value.Count == 0, "An explicit successful empty library was not recognized.");
        }

        private static async Task IdentityAndTokenProofIsRequired()
        {
            await RejectProof(root => root.Remove("UserConfig"), SteamReadStatus.MalformedPage);
            await RejectProof(root => ((Dictionary<string, object>)root["UserConfig"]).Remove("steamid"), SteamReadStatus.MalformedPage);
            await RejectProof(root => ((Dictionary<string, object>)root["UserConfig"])["steamid"] = "invalid", SteamReadStatus.MalformedPage);
            await RejectProof(root => ((Dictionary<string, object>)root["UserConfig"])["steamid"] = "0", SteamReadStatus.LoginRequired);
            await RejectProof(root => ((Dictionary<string, object>)root["UserConfig"])["steamid"] = "76561198000000002", SteamReadStatus.LoginRequired);
            await RejectProof(root => ChangeLoader(root, 0, loader => loader.Remove("steamid")), SteamReadStatus.MalformedPage);
            await RejectProof(root => ChangeLoader(root, 0, loader => loader["strWebAPIToken"] = ""), SteamReadStatus.MalformedPage);
            await RejectProof(root => ChangeLoader(root, 1, loader => loader.Remove("bOwnProfile")), SteamReadStatus.MalformedPage);
            await RejectProof(root => ChangeLoader(root, 1, loader => loader["bOwnProfile"] = "true"), SteamReadStatus.MalformedPage);
            await RejectProof(root => ChangeLoader(root, 1, loader => loader["bOwnProfile"] = false), SteamReadStatus.LoginRequired);
            await RejectProof(root => ((Dictionary<string, object>)root["Config"])["WEBAPI_BASE_URL"] = "https://example.com/", SteamReadStatus.MalformedPage);
            var handler = new ApiHandler((request, token) => Task.FromResult(Reply("{}")));
            var read = await new OwnedGamesReader(new FakeCommunity("<html>Unexpected format</html>"), () => handler)
                .ReadAsync(Profile, CancellationToken.None);
            Require(read.Status == SteamReadStatus.MalformedPage && handler.Calls == 0, "Unknown HTML was treated as an expired or empty library.");
            read = await new OwnedGamesReader(new FakeCommunity(Page(Proof())), () => handler)
                .ReadAsync("https://steamcommunity.com/profiles/00000000000000000", CancellationToken.None);
            Require(read.Status == SteamReadStatus.MalformedPage && handler.Calls == 0, "An all-zero profile ID was accepted.");
        }

        private static async Task IncompleteAndErrorRepliesAreRejected()
        {
            foreach (var body in new[]
            {
                "{}",
                "{\"response\":{}}",
                "{\"response\":{\"game_count\":0}}",
                "{\"response\":{\"game_count\":2,\"games\":[{\"appid\":10}]}}",
                "{\"response\":{\"game_count\":2,\"games\":[{\"appid\":10},{\"appid\":10}]}}",
                "{\"response\":{\"game_count\":1,\"games\":[{\"appid\":0}]}}",
                "{\"response\":{\"game_count\":1,\"games\":[{\"appid\":10,\"family_shared\":\"true\"}]}}",
                "Not JSON"
            })
            {
                var read = await ReadApi(body);
                Require(read.Status == SteamReadStatus.MalformedPage && read.Value == null, "An incomplete library supplied absence proof.");
            }
            foreach (var result in new[] { null, "15", "2" })
            {
                var read = await ReadApi("{\"response\":{\"game_count\":1,\"games\":[{\"appid\":10}]}}", result);
                Require(read.Status == SteamReadStatus.MalformedPage, "HTTP success bypassed Steam's result-code check.");
            }
            var redirect = new ApiHandler((request, token) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://example.com/");
                return Task.FromResult(response);
            });
            var failed = await new OwnedGamesReader(new FakeCommunity(Page(Proof())), () => redirect)
                .ReadAsync(Profile, CancellationToken.None);
            Require(failed.Status == SteamReadStatus.MalformedPage && redirect.Calls == 1,
                "An API redirect forwarded the ownership token.");
        }

        private static async Task ApiCancellationPropagates()
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
                try { await new OwnedGamesReader(new FakeCommunity(Page(Proof())), () => handler).ReadAsync(Profile, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Require(canceled && handler.Calls == 1, "Ownership cancellation became an empty or failed library.");
            }
        }

        private static async Task RejectProof(Action<Dictionary<string, object>> mutate, SteamReadStatus status)
        {
            var proof = Proof();
            mutate(proof);
            var handler = new ApiHandler((request, token) => Task.FromResult(Reply("{}")));
            var read = await new OwnedGamesReader(new FakeCommunity(Page(proof)), () => handler).ReadAsync(Profile, CancellationToken.None);
            Require(read.Status == status && handler.Calls == 0, "Invalid session proof sent an API credential or lost its failure classification.");
        }

        private static Task<SteamReadResult<HashSet<int>>> ReadApi(string body, string result = "1")
        {
            var handler = new ApiHandler((request, token) => Task.FromResult(Reply(body, result)));
            return new OwnedGamesReader(new FakeCommunity(Page(Proof())), () => handler).ReadAsync(Profile, CancellationToken.None);
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
