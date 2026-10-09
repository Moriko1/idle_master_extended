using System;
using System.Linq;
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
        public WebView2 Browser { get; private set; }
        public SteamSession Current { get; private set; }
        public SteamHttpClient Client { get; private set; }
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
        public async Task<SteamReadResult<SteamSession>> ValidateAsync(CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                if (!IsInitialized) return SteamReadResult<SteamSession>.Failed(SteamReadStatus.LoginRequired, "Sign in to Steam.");
                var browserCookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://steamcommunity.com/");
                token.ThrowIfCancellationRequested();
                var login = browserCookies.FirstOrDefault(c => c.Name == "steamLoginSecure");
                if (login == null || !TryReadCookieIdentity(login.Value, out var expected))
                    return SteamReadResult<SteamSession>.Failed(SteamReadStatus.LoginRequired, "Sign in to Steam.");
                var jar = new CookieContainer();
                foreach (var cookie in browserCookies)
                {
                    if (!cookie.Domain.TrimStart('.').Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)) continue;
                    jar.Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                    { Secure = cookie.IsSecure, HttpOnly = cookie.IsHttpOnly });
                }
                var candidate = new SteamHttpClient(jar);
                try
                {
                    var response = await candidate.GetAsync("https://steamcommunity.com/my/?l=english", token);
                    if (!response.IsSuccess) return SteamReadResult<SteamSession>.Failed(response.Status, response.Message);
                    var identity = ParseIdentity(response.Value, expected);
                    if (!identity.IsSuccess) return identity;
                    token.ThrowIfCancellationRequested();
                    Client?.Dispose(); Client = candidate; candidate = null; Current = identity.Value;
                    return identity;
                }
                finally { candidate?.Dispose(); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Exception(ex, "Steam session validation");
                return SteamReadResult<SteamSession>.Failed(SteamReadStatus.TransientFailure, "Steam is temporarily unavailable. Retry when connected.");
            }
            finally { gate.Release(); }
        }
        internal static bool TryReadCookieIdentity(string value, out ulong steamId)
        {
            var first = WebUtility.UrlDecode(value ?? "").Split('|')[0];
            return ulong.TryParse(first, out steamId) && first.Length == 17 && steamId != 0;
        }
        internal static SteamReadResult<SteamSession> ParseIdentity(string html, ulong expectedSteamId)
        {
            // This global identifies Steam's authenticated viewer, independently of the profile being viewed.
            var match = Regex.Match(html ?? "", @"\bg_steamID\s*=\s*['""](?<id>\d{17})['""]\s*;");
            if (!match.Success)
            {
                if (Regex.IsMatch(html ?? "", @"\bg_steamID\s*=\s*(false|['""](?:0)?['""])\s*;") ||
                    (html ?? "").IndexOf("login_form", StringComparison.OrdinalIgnoreCase) >= 0)
                    return SteamReadResult<SteamSession>.Failed(SteamReadStatus.LoginRequired, "Steam sign-in expired. Sign in again.");
                return SteamReadResult<SteamSession>.Failed(SteamReadStatus.MalformedPage, "Steam's account page could not be verified. Retry or sign in again.");
            }
            if (!ulong.TryParse(match.Groups["id"].Value, out var id) || id != expectedSteamId)
                return SteamReadResult<SteamSession>.Failed(SteamReadStatus.LoginRequired, "Steam account changed. Switch account and scan again.");
            var document = new HtmlDocument(); document.LoadHtml(html);
            var name = document.DocumentNode.SelectSingleNode("//*[@id='account_pulldown']")?.InnerText;
            return SteamReadResult<SteamSession>.Succeeded(new SteamSession(id,
                string.IsNullOrWhiteSpace(name) ? id.ToString() : WebUtility.HtmlDecode(name).Trim()));
        }
        public async Task SignOutAsync()
        {
            await gate.WaitAsync();
            try
            {
                Current = null; Client?.Dispose(); Client = null;
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
