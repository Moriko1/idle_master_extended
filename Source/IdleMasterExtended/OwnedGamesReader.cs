using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using HtmlAgilityPack;

namespace IdleMasterExtended
{
    public interface IOwnedGamesReader
    {
        Task<SteamReadResult<HashSet<int>>> ReadAsync(string profileUrl, CancellationToken cancellationToken);
    }

    /// <summary>Reads complete ownership with the current browser session; never persists its API token.</summary>
    public sealed class OwnedGamesReader : IOwnedGamesReader
    {
        private const string ApiEndpoint = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/";
        private const int MaximumGames = 100000;
        private const string InvalidLibrary = "Steam's owned game list could not be verified. Your previous game list has been kept.";
        private readonly ICommunityClient community;
        private readonly Func<HttpMessageHandler> transportFactory;

        public OwnedGamesReader(ICommunityClient community, Func<HttpMessageHandler> transportFactory = null)
        {
            this.community = community ?? throw new ArgumentNullException(nameof(community));
            this.transportFactory = transportFactory ?? (() => new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });
        }

        public async Task<SteamReadResult<HashSet<int>>> ReadAsync(string profileUrl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri profile;
            if (!Uri.TryCreate(profileUrl, UriKind.Absolute, out profile) || profile.Scheme != Uri.UriSchemeHttps
                || !profile.IsDefaultPort || !string.IsNullOrEmpty(profile.UserInfo)
                || !string.Equals(profile.Host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase))
                return Failed();
            var account = Regex.Match(profile.AbsolutePath, @"^/profiles/([0-9]{17})/?$");
            if (!account.Success || account.Groups[1].Value.All(ch => ch == '0')) return Failed();
            var steamId = account.Groups[1].Value;
            var url = profile.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/games/?tab=all&l=english";
            var bounded = community as IBoundedCommunityClient;
            var page = bounded == null ? await community.GetAsync(url, cancellationToken).ConfigureAwait(false)
                : await bounded.GetAsync(url, SteamHttpClient.MaximumResponseLimit, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!page.IsSuccess)
                return SteamReadResult<HashSet<int>>.Failed(page.Status, page.Message);
            string token;
            var proof = TrySessionProof(page.Value, steamId, out token);
            if (proof != SteamReadStatus.Success)
                return SteamReadResult<HashSet<int>>.Failed(proof,
                    proof == SteamReadStatus.LoginRequired ? "Sign in to the same Steam account again to continue." : InvalidLibrary);

            // Current official Community transport/token contract:
            // https://cdn.fastly.steamstatic.com/steamcommunity/public/ssr/CFcRBfQR.js
            // https://cdn.fastly.steamstatic.com/steamcommunity/public/ssr/BKJ3hLh4.js
            // Explicit inclusion avoids the profile page's default omission of free subscriptions.
            var input = new Dictionary<string, object>
            {
                { "steamid", steamId }, { "include_appinfo", true },
                { "include_played_free_games", true }, { "include_free_sub", true },
                { "skip_unvetted_apps", false }, { "include_family_licenses", true }
            };
            var requestUrl = ApiEndpoint + "?access_token=" + Uri.EscapeDataString(token)
                + "&input_json=" + Uri.EscapeDataString(Serializer().Serialize(input)) + "&format=json";
            // The origin is fixed; redirects are rejected before another request can forward the token.
            using (var client = new HttpClient(transportFactory(), true))
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("IdleMasterExtended/1.12");
                SteamReadResult<HashSet<int>> result = null;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(15));
                        try
                        {
                            using (var request = new HttpRequestMessage(HttpMethod.Get, requestUrl))
                            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                            using (var cancelResponse = deadline.Token.Register(() => response.Dispose()))
                            {
                                deadline.Token.ThrowIfCancellationRequested();
                                var status = (int)response.StatusCode;
                                if (status == 401)
                                    result = SteamReadResult<HashSet<int>>.Failed(SteamReadStatus.LoginRequired, "Sign in to Steam again to continue.");
                                else if (status == 429 || status == 408 || status >= 500)
                                    result = SteamReadResult<HashSet<int>>.Failed(SteamReadStatus.TransientFailure, "Steam's owned game list is temporarily unavailable. Try again shortly.");
                                else if (!response.IsSuccessStatusCode)
                                    result = Failed();
                                else
                                {
                                    IEnumerable<string> results;
                                    if (!response.Headers.TryGetValues("x-eresult", out results)
                                        || results.Count() != 1 || results.Single().Trim() != "1")
                                        result = Failed();
                                    else
                                    {
                                        var content = await SteamHttpClient.ReadContentAsync(response.Content, SteamHttpClient.DefaultResponseLimit, deadline.Token).ConfigureAwait(false);
                                        result = content.IsSuccess ? ParseGames(content.Value)
                                            : SteamReadResult<HashSet<int>>.Failed(content.Status, content.Message);
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            result = SteamReadResult<HashSet<int>>.Failed(SteamReadStatus.TransientFailure, "Steam's owned game list took too long to respond. Try again shortly.");
                        }
                        catch (HttpRequestException)
                        {
                            result = SteamReadResult<HashSet<int>>.Failed(SteamReadStatus.TransientFailure, "Steam's owned game list could not be reached. Try again shortly.");
                        }
                        catch (IOException)
                        {
                            result = SteamReadResult<HashSet<int>>.Failed(SteamReadStatus.TransientFailure, "Steam's owned game list was interrupted. Try again shortly.");
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result.Status != SteamReadStatus.TransientFailure || attempt == 2) return result;
                    await Task.Delay(500 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                }
                return result;
            }
        }

        private static SteamReadStatus TrySessionProof(string html, string steamId, out string token)
        {
            token = null;
            try
            {
                var document = new HtmlDocument();
                document.LoadHtml(html ?? string.Empty);
                var node = document.DocumentNode.SelectSingleNode("//script[@id='valve-ssr-data' and @type='application/json']");
                if (node == null) return SteamReadStatus.MalformedPage;
                var root = Object(Serializer().DeserializeObject(node.InnerText));
                object value;
                var user = root != null && root.TryGetValue("UserConfig", out value) ? Object(value) : null;
                if (user == null) return SteamReadStatus.MalformedPage;
                var identity = AccountStatus(user, steamId);
                if (identity != SteamReadStatus.Success) return identity;
                var loaders = root.TryGetValue("loaderData", out value) ? value as object[] : null;
                if (loaders == null || loaders.Length < 2) return SteamReadStatus.MalformedPage;
                var template = DecodedObject(loaders[0]);
                var ownProfile = DecodedObject(loaders[1]);
                if (template == null || ownProfile == null) return SteamReadStatus.MalformedPage;
                identity = AccountStatus(template, steamId);
                if (identity != SteamReadStatus.Success) return identity;
                identity = AccountStatus(ownProfile, steamId);
                if (identity != SteamReadStatus.Success) return identity;
                if (!ownProfile.TryGetValue("bOwnProfile", out value) || !(value is bool))
                    return SteamReadStatus.MalformedPage;
                if (!(bool)value) return SteamReadStatus.LoginRequired;
                var config = root.TryGetValue("Config", out value) ? Object(value) : null;
                if (config != null && config.TryGetValue("WEBAPI_BASE_URL", out value))
                {
                    Uri configured;
                    if (!(value is string) || !Uri.TryCreate((string)value, UriKind.Absolute, out configured)
                        || configured.Scheme != Uri.UriSchemeHttps || !configured.IsDefaultPort
                        || !string.Equals(configured.Host, "api.steampowered.com", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrEmpty(configured.UserInfo) || configured.AbsolutePath != "/"
                        || !string.IsNullOrEmpty(configured.Query) || !string.IsNullOrEmpty(configured.Fragment))
                        return SteamReadStatus.MalformedPage;
                }
                if (!template.TryGetValue("strWebAPIToken", out value) || !(value is string)) return SteamReadStatus.MalformedPage;
                token = (string)value;
                if (token.Length == 0 || token.Length > 16384 || token.Any(char.IsWhiteSpace))
                { token = null; return SteamReadStatus.MalformedPage; }
                return SteamReadStatus.Success;
            }
            catch (ArgumentException) { return SteamReadStatus.MalformedPage; }
            catch (InvalidOperationException) { return SteamReadStatus.MalformedPage; }
        }

        private static SteamReadResult<HashSet<int>> ParseGames(string json)
        {
            try
            {
                var root = Object(Serializer().DeserializeObject(json));
                object value;
                var response = root != null && root.TryGetValue("response", out value) ? Object(value) : null;
                int count;
                if (response == null || !response.TryGetValue("game_count", out value) || !Integer(value, out count)
                    || count < 0 || count > MaximumGames) return Failed();
                var games = response.TryGetValue("games", out value) ? value as object[] : null;
                if (games == null || games.Length > MaximumGames) return Failed();
                var apps = new HashSet<int>();
                var shared = 0;
                foreach (var item in games)
                {
                    var game = Object(item);
                    int appId;
                    if (game == null || !game.TryGetValue("appid", out value) || !Integer(value, out appId)
                        || appId <= 0 || !apps.Add(appId)) return Failed();
                    if (game.TryGetValue("family_shared", out value))
                    {
                        if (!(value is bool)) return Failed();
                        if ((bool)value) shared++;
                    }
                }
                // Valve's inclusive response counts permanent games separately from appended
                // family licenses. Keep shared apps in the set so they are never falsely excluded.
                return count == games.Length - shared ? SteamReadResult<HashSet<int>>.Succeeded(apps) : Failed();
            }
            catch (ArgumentException) { return Failed(); }
            catch (InvalidOperationException) { return Failed(); }
        }

        private static SteamReadStatus AccountStatus(Dictionary<string, object> data, string expected)
        {
            object value;
            if (!data.TryGetValue("steamid", out value)) return SteamReadStatus.MalformedPage;
            var actual = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (actual == "0" || actual == "00000000000000000") return SteamReadStatus.LoginRequired;
            if (!Regex.IsMatch(actual ?? string.Empty, @"^[0-9]{17}$")) return SteamReadStatus.MalformedPage;
            return string.Equals(actual, expected, StringComparison.Ordinal)
                ? SteamReadStatus.Success : SteamReadStatus.LoginRequired;
        }

        private static bool Integer(object value, out int number)
        {
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }

        private static Dictionary<string, object> Object(object value) => value as Dictionary<string, object>;

        private static Dictionary<string, object> DecodedObject(object value)
        {
            return value is string ? Object(Serializer().DeserializeObject((string)value)) : Object(value);
        }

        private static JavaScriptSerializer Serializer() => new JavaScriptSerializer
        {
            MaxJsonLength = SteamHttpClient.MaximumResponseLimit, RecursionLimit = 100
        };

        private static SteamReadResult<HashSet<int>> Failed() => SteamReadResult<HashSet<int>>.Failed(SteamReadStatus.MalformedPage, InvalidLibrary);
    }
}
