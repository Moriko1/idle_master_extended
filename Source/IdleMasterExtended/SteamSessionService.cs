using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace IdleMasterExtended
{
    internal sealed class SteamSession
    {
        public ulong SteamId { get; }
        public string DisplayName { get; }
        public string ProfileUrl => "https://steamcommunity.com/profiles/" + SteamId;
        public SteamSession(ulong steamId, string name) { SteamId = steamId; DisplayName = name; }
    }
    internal sealed class SteamSessionService : IDisposable
    {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        private readonly SteamSessionRecovery recovery;
        private TimeSpan? verifiedAt;
        private string verifiedCookie;
        public SteamSessionService() { recovery = new SteamSessionRecovery(() => elapsed.Elapsed); }
        public WebView2 Browser { get; private set; }
        public SteamSession Current { get; private set; }
        public SteamHttpClient Client { get; private set; }
        internal int RecoveryAttempts { get; private set; }
        private bool disposed;
        public bool IsInitialized => !disposed && Browser != null && !Browser.IsDisposed && Browser.CoreWebView2 != null;
        public static bool IsTrustedSteamUri(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
                (uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("login.steampowered.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("help.steampowered.com", StringComparison.OrdinalIgnoreCase));
        }
        public async Task InitializeAsync(WebView2 browser)
        {
            if (IsInitialized) return;
            System.IO.Directory.CreateDirectory(AppPaths.Data);
            Browser = browser;
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.BrowserProfile);
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.Profile.IsPasswordAutosaveEnabled = false;
            browser.CoreWebView2.Profile.IsGeneralAutofillEnabled = false;
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            browser.CoreWebView2.NavigationStarting += (sender, e) =>
            {
                if (e.Uri != "about:blank" && !IsTrustedSteamUri(e.Uri)) e.Cancel = true;
            };
            browser.CoreWebView2.NewWindowRequested += (sender, e) =>
            {
                e.Handled = true;
                if (IsTrustedSteamUri(e.Uri)) browser.CoreWebView2.Navigate(e.Uri);
            };
        }
        internal void InvalidateVerification() { verifiedAt = null; verifiedCookie = null; }
        public async Task<SteamReadResult<SteamSession>> ValidateAsync(CancellationToken token, bool restoreRemembered = true)
        {
            await gate.WaitAsync(token);
            try
            {
                if (!IsInitialized) return SteamReadResult<SteamSession>.Failed(SteamReadStatus.LoginRequired, "Sign in to Steam.");
                var pinnedId = Current?.SteamId ?? 0;
                var accountChanged = false;
                Func<Task<SteamReadResult<SteamSession>>> read = async () =>
                {
                    var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://steamcommunity.com/");
                    token.ThrowIfCancellationRequested();
                    var login = cookies.FirstOrDefault(c => c.Name == "steamLoginSecure" && IsCommunityCookie(c.Name, c.Domain));
                    if (login == null || !TryReadCookieIdentity(login.Value, out var expected))
                        return SteamReadResult<SteamSession>.Failed(SteamReadStatus.LoginRequired, "Sign in to Steam.");
                    if (pinnedId == 0) pinnedId = expected;
                    if (pinnedId != expected)
                    {
                        accountChanged = true;
                        return SteamReadResult<SteamSession>.Failed(SteamReadStatus.LoginRequired, "Steam account changed. Switch account and scan again.");
                    }
                    // Reuse only a recently authenticated, unchanged account. This avoids the
                    // duplicate /my reads in Start followed immediately by a complete scan.
                    if (Current != null && Client != null && verifiedCookie == login.Value &&
                        verifiedAt.HasValue && elapsed.Elapsed - verifiedAt.Value < TimeSpan.FromSeconds(15))
                        return SteamReadResult<SteamSession>.Succeeded(Current);
                    var imported = CreateCommunityCookieJar(cookies.Where(c => IsCommunityCookie(c.Name, c.Domain))
                        .Select(c => new Cookie(c.Name, c.Value, c.Path, c.Domain)
                        { Secure = c.IsSecure, HttpOnly = c.IsHttpOnly }));
                    if (!imported.IsSuccess) return SteamReadResult<SteamSession>.Failed(imported.Status, imported.Message);
                    var candidate = new SteamHttpClient(imported.Value);
                    try
                    {
                        var response = await candidate.GetAsync("https://steamcommunity.com/my/?l=english", token);
                        if (!response.IsSuccess) return SteamReadResult<SteamSession>.Failed(response.Status, response.Message);
                        var identity = ParseIdentity(response.Value, expected);
                        if (!identity.IsSuccess)
                        {
                            accountChanged = HasDifferentViewer(response.Value, expected);
                            return identity;
                        }
                        token.ThrowIfCancellationRequested();
                        Client?.Dispose(); Client = candidate; candidate = null; Current = identity.Value;
                        verifiedAt = elapsed.Elapsed; verifiedCookie = login.Value;
                        return identity;
                    }
                    finally { candidate?.Dispose(); }
                };
                var result = restoreRemembered && !Browser.Visible
                    ? await recovery.ValidateAsync(read, () => RestoreBrowserSessionAsync(token), () => accountChanged, token)
                    : await read();
                if (!result.IsSuccess) { verifiedAt = null; verifiedCookie = null; }
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                verifiedAt = null; verifiedCookie = null;
                Logger.Exception(ex, "Steam session validation");
                return SteamReadResult<SteamSession>.Failed(SteamReadStatus.TransientFailure, "Steam is temporarily unavailable. Retry when connected.");
            }
            finally { gate.Release(); }
        }
        private async Task<SteamReadStatus> RestoreBrowserSessionAsync(CancellationToken token)
        {
            RecoveryAttempts++;
            // Steam itself redeems its remembered profile login. No refresh token is extracted,
            // stored outside WebView2, or exchanged by application code.
            var completed = new TaskCompletionSource<SteamReadStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var navigation = new SteamSessionNavigation();
            EventHandler<CoreWebView2NavigationStartingEventArgs> starting = (sender, args) =>
                navigation.ObserveStarting(args.NavigationId, args.Uri);
            EventHandler<CoreWebView2NavigationCompletedEventArgs> navigated = (sender, args) =>
            {
                navigation.ObserveCompleted(args.NavigationId, args.IsSuccess, args.HttpStatusCode, Browser.CoreWebView2.Source);
                if (navigation.Result.HasValue) completed.TrySetResult(navigation.Result.Value);
            };
            Browser.CoreWebView2.NavigationStarting += starting;
            Browser.CoreWebView2.NavigationCompleted += navigated;
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    deadline.Token.ThrowIfCancellationRequested();
                    Browser.CoreWebView2.Navigate(SteamSessionNavigation.InitialUri);
                    while (!completed.Task.IsCompleted)
                    {
                        await Task.WhenAny(completed.Task, Task.Delay(1000, deadline.Token));
                        deadline.Token.ThrowIfCancellationRequested();
                    }
                    return await completed.Task;
                }
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                return navigation.TimeoutStatus();
            }
            finally
            {
                if (IsInitialized) { Browser.CoreWebView2.NavigationStarting -= starting; Browser.CoreWebView2.NavigationCompleted -= navigated; }
                ReleasePage();
            }
        }
        internal static bool HasDifferentViewer(string html, ulong expected)
        {
            var match = Regex.Match(html ?? "", @"\bg_steamID\s*=\s*['""](?<id>\d{17})['""]\s*;");
            return match.Success && ulong.TryParse(match.Groups["id"].Value, out var id) && id != expected;
        }
        internal static bool IsCommunityCookie(string name, string domain)
        {
            // Browser preference/analytics cookies can contain commas that .NET's CookieContainer rejects.
            // Community reads need only these authentication cookies. Never transform their token values.
            return string.Equals((domain ?? "").TrimStart('.'), "steamcommunity.com", StringComparison.OrdinalIgnoreCase) &&
                (name == "steamLoginSecure" || name == "sessionid" || name == "steamparental");
        }
        internal static SteamReadResult<CookieContainer> CreateCommunityCookieJar(IEnumerable<Cookie> browserCookies)
        {
            var jar = new CookieContainer();
            try
            {
                foreach (var cookie in browserCookies)
                    if (cookie != null && IsCommunityCookie(cookie.Name, cookie.Domain)) jar.Add(cookie);
            }
            catch (CookieException)
            {
                return SteamReadResult<CookieContainer>.Failed(SteamReadStatus.MalformedPage,
                    "The saved Steam sign-in could not be read. Close this window and use Switch account to sign in again.");
            }
            if (jar.GetCookies(new Uri("https://steamcommunity.com/"))["steamLoginSecure"] == null)
                return SteamReadResult<CookieContainer>.Failed(SteamReadStatus.LoginRequired, "Sign in to Steam.");
            return SteamReadResult<CookieContainer>.Succeeded(jar);
        }
        internal static bool TryReadCookieIdentity(string value, out ulong steamId)
        {
            var first = WebUtility.UrlDecode(value ?? "").Split('|')[0];
            return ulong.TryParse(first, out steamId) && first.Length == 17 && steamId != 0;
        }
        internal static SteamReadResult<SteamSession> ParseIdentity(string html, ulong expectedSteamId)
        {
            var document = new HtmlDocument(); document.LoadHtml(html ?? "");
            // This global identifies Steam's authenticated viewer, independently of the profile being viewed.
            var match = Regex.Match(html ?? "", @"\bg_steamID\s*=\s*['""](?<id>\d{17})['""]\s*;");
            if (!match.Success)
            {
                if (IsChallengePage(document))
                    return SteamReadResult<SteamSession>.Failed(SteamReadStatus.TransientFailure,
                        "Steam is limiting requests or checking the connection. Retry shortly; your sign-in has been kept.");
                if (Regex.IsMatch(html ?? "", @"\bg_steamID\s*=\s*(false|['""](?:0)?['""])\s*;") ||
                    document.DocumentNode.SelectSingleNode("//*[@id='login_form' or @id='loginForm' or @id='login_container']") != null)
                    return SteamReadResult<SteamSession>.Failed(SteamReadStatus.LoginRequired, "Steam sign-in expired. Sign in again.");
                return SteamReadResult<SteamSession>.Failed(SteamReadStatus.MalformedPage, "Steam's account page could not be verified. Retry shortly; your sign-in has been kept.");
            }
            if (!ulong.TryParse(match.Groups["id"].Value, out var id) || id != expectedSteamId)
                return SteamReadResult<SteamSession>.Failed(SteamReadStatus.LoginRequired, "Steam account changed. Switch account and scan again.");
            var name = document.DocumentNode.SelectSingleNode("//*[@id='account_pulldown']")?.InnerText;
            return SteamReadResult<SteamSession>.Succeeded(new SteamSession(id,
                string.IsNullOrWhiteSpace(name) ? id.ToString() : WebUtility.HtmlDecode(name).Trim()));
        }
        internal static bool IsChallengePage(HtmlDocument document)
        {
            var text = WebUtility.HtmlDecode(document.DocumentNode.InnerText ?? "");
            return text.IndexOf("too many requests", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("verify you are human", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("access denied", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        public void ReleasePage()
        {
            // Keep the dedicated profile cookies, but release Steam page scripts/rendering after sign-in.
            try { if (IsInitialized && !Browser.Visible) Browser.CoreWebView2.Navigate("about:blank"); }
            catch (InvalidOperationException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        public async Task SignOutAsync()
        {
            await gate.WaitAsync();
            try
            {
                Current = null; Client?.Dispose(); Client = null; recovery.Reset(); verifiedAt = null; verifiedCookie = null;
                if (IsInitialized)
                {
                    Browser.CoreWebView2.CookieManager.DeleteAllCookies();
                    await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                    if (IsInitialized) Browser.CoreWebView2.Navigate("about:blank");
                }
            }
            finally { gate.Release(); }
        }
        public async Task CloseAsync()
        {
            await gate.WaitAsync();
            try { Dispose(); }
            finally { gate.Release(); }
        }
        public void Dispose() { if (disposed) return; disposed = true; Client?.Dispose(); Browser?.Dispose(); }
    }
}
