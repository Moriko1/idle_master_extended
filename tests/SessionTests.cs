using System;
namespace IdleMasterExtended.Tests
{
    internal static class SessionTests
    {
        public static void RunAll()
        {
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
    }
}
