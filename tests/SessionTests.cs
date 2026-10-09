using System;
using System.Net;
namespace IdleMasterExtended.Tests
{
    internal static class SessionTests
    {
        public static void RunAll()
        {
            CookieBridgeIgnoresBrowserPreferences();
            CookieBridgeRejectsInvalidAuthentication();
            const ulong id = 76561198000000001;
            Test.Assert(SteamSessionService.TryReadCookieIdentity(id + "%7C%7Csecret", out var parsed) && parsed == id, "Encoded cookie identity");
            Test.Assert(!SteamSessionService.TryReadCookieIdentity("secret", out parsed), "Malformed cookie identity rejected");
            var authenticated = SteamSessionService.ParseIdentity("<script>g_steamID = '" + id + "';</script><span id='account_pulldown'>A &amp; B</span>", id);
            Test.Assert(authenticated.IsSuccess && authenticated.Value.DisplayName == "A & B", "Authenticated viewer identity");
            Test.Assert(SteamSessionService.ParseIdentity("<script>g_steamID = false;</script>", id).Status == SteamReadStatus.LoginRequired, "Logged out marker");
            Test.Assert(SteamSessionService.ParseIdentity("<script>g_steamID = '" + (id + 1) + "';</script>", id).Status == SteamReadStatus.LoginRequired, "Wrong viewer account");
            Test.Assert(SteamSessionService.ParseIdentity("<html>Steam profile</html>", id).Status == SteamReadStatus.MalformedPage, "Public profile cannot prove login");
            Test.Assert(SteamSessionService.ParseIdentity("<html>Temporary outage</html>", id).Status == SteamReadStatus.MalformedPage, "Error HTML cannot prove login");
            Test.Assert(SteamSessionService.IsTrustedSteamUri("https://login.steampowered.com/"), "Official login origin allowed");
            Test.Assert(!SteamSessionService.IsTrustedSteamUri("https://steamcommunity.com.evil.invalid/"), "Suffix spoof rejected");
            Test.Assert(!SteamSessionService.IsTrustedSteamUri("http://steamcommunity.com/"), "Insecure navigation rejected");
            Test.Assert(!SteamSessionService.IsTrustedSteamUri("https://steamcommunity.com:8443/"), "Nonstandard origin rejected");
            Test.Assert(!SteamSessionService.IsTrustedSteamUri("https://name:password@steamcommunity.com/"), "URL credentials rejected");
        }
        private static void CookieBridgeIgnoresBrowserPreferences()
        {
            var loginValue = "76561198000000001%7C%7Csynthetic-token";
            var preferences = new Cookie("timezoneOffset", "-14400,0", "/", ".steamcommunity.com");
            var oldBridgeRejected = false;
            try { new CookieContainer().Add(preferences); }
            catch (CookieException) { oldBridgeRejected = true; }
            Test.Assert(oldBridgeRejected, "Browser comma value reproduces the original .NET handoff failure");
            var login = new Cookie("steamLoginSecure", loginValue, "/", ".steamcommunity.com") { Secure = true, HttpOnly = true };
            var session = new Cookie("sessionid", "synthetic-session", "/", "steamcommunity.com");
            var parental = new Cookie("steamparental", "synthetic-parental", "/", ".steamcommunity.com") { Secure = true };
            var result = SteamSessionService.CreateCommunityCookieJar(new[] {
                preferences, login, session, parental,
                new Cookie("tracking", new string('x', 5000), "/", ".steamcommunity.com"),
                new Cookie("preferences", "first;second", "/", ".steamcommunity.com"),
                new Cookie("steamLoginSecure", "other-origin", "/", "steamcommunity.com.evil.invalid") });
            Test.Assert(result.IsSuccess, "Unrelated browser preferences cannot block session import");
            var imported = result.Value.GetCookies(new Uri("https://steamcommunity.com/"));
            Test.Assert(imported.Count == 3, "Only exact Community authentication cookies imported");
            Test.Assert(imported["steamLoginSecure"].Value == loginValue && imported["steamLoginSecure"].Secure && imported["steamLoginSecure"].HttpOnly,
                "Authentication token and security attributes remain unchanged");
            Test.Assert(imported["sessionid"].Value == session.Value && imported["steamparental"].Value == parental.Value,
                "Session and parental access retained");
        }
        private static void CookieBridgeRejectsInvalidAuthentication()
        {
            var invalid = SteamSessionService.CreateCommunityCookieJar(new[] {
                new Cookie("steamLoginSecure", "invalid,token", "/", ".steamcommunity.com") });
            Test.Assert(invalid.Status == SteamReadStatus.MalformedPage, "Invalid authentication reported as an import failure, not a network outage");
            var missing = SteamSessionService.CreateCommunityCookieJar(new[] {
                new Cookie("timezoneOffset", "-14400,0", "/", ".steamcommunity.com") });
            Test.Assert(missing.Status == SteamReadStatus.LoginRequired, "Preferences alone cannot establish login");
        }
    }
}
