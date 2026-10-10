using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
namespace IdleMasterExtended.Tests
{
    internal static class SessionTests
    {
        public static void RunAll()
        {
            RememberedSessionRecovery().GetAwaiter().GetResult();
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
            Test.Assert(SteamSessionService.ParseIdentity("<script>g_steamID = false;</script><div id='login_form'></div><h1>Too many requests</h1>", id).Status == SteamReadStatus.TransientFailure,
                "A rate-limit page with logged-out layout cannot expire saved login");
            Test.Assert(SteamSessionService.ParseIdentity("<script>var login_form = true;</script>", id).Status == SteamReadStatus.MalformedPage,
                "A JavaScript name alone cannot prove login expiry");
            Test.Assert(SteamSessionService.HasDifferentViewer("<script>g_steamID = '" + (id+1) + "';</script>", id), "Viewer mismatch has positive identity evidence");
            Test.Assert(SteamSessionService.IsTrustedSteamUri("https://login.steampowered.com/"), "Official login origin allowed");
            Test.Assert(!SteamSessionService.IsTrustedSteamUri("https://steamcommunity.com.evil.invalid/"), "Suffix spoof rejected");
            Test.Assert(!SteamSessionService.IsTrustedSteamUri("http://steamcommunity.com/"), "Insecure navigation rejected");
            Test.Assert(!SteamSessionService.IsTrustedSteamUri("https://steamcommunity.com:8443/"), "Nonstandard origin rejected");
            Test.Assert(!SteamSessionService.IsTrustedSteamUri("https://name:password@steamcommunity.com/"), "URL credentials rejected");
        }
        private static async Task RememberedSessionRecovery()
        {
            const ulong id = 76561198000000001;
            var now = TimeSpan.Zero;
            var policy = new SteamSessionRecovery(() => now);
            var calls = 0; var restores = 0;
            var expired = SteamReadResult<SteamSession>.Failed(SteamReadStatus.LoginRequired, "Synthetic expired access cookie");
            var verified = SteamSessionService.ParseIdentity("<script>g_steamID = '" + id + "';</script>", id);
            var responses = new Queue<SteamReadResult<SteamSession>>(new[] { expired, verified });
            var result = await policy.ValidateAsync(() => { calls++; return Task.FromResult(responses.Dequeue()); },
                () => { restores++; return Task.FromResult(SteamReadStatus.Success); }, () => false, CancellationToken.None);
            Test.Assert(result.IsSuccess && calls == 2 && restores == 1, "Expired access cookie receives one official browser renewal and verified read");
            result = await policy.ValidateAsync(() => Task.FromResult(expired),
                () => { restores++; return Task.FromResult(SteamReadStatus.Success); }, () => false, CancellationToken.None);
            Test.Assert(result.Status == SteamReadStatus.TransientFailure && restores == 1, "Another failure cannot repeat browser renewal inside cooldown");
            policy.Reset(); restores = 0;
            result = await policy.ValidateAsync(() => Task.FromResult(expired),
                () => { restores++; return Task.FromResult(SteamReadStatus.TransientFailure); }, () => false, CancellationToken.None);
            Test.Assert(result.Status == SteamReadStatus.TransientFailure && restores == 1, "Browser outage preserves remembered session");
            result = await policy.ValidateAsync(() => Task.FromResult(verified),
                () => { restores++; return Task.FromResult(SteamReadStatus.Success); }, () => false, CancellationToken.None);
            Test.Assert(result.IsSuccess && restores == 1, "Interactive successful sign-in is visible immediately despite prior recovery cooldown");
            policy.Reset();
            result = await policy.ValidateAsync(() => Task.FromResult(expired),
                () => { restores++; return Task.FromResult(SteamReadStatus.LoginRequired); }, () => false, CancellationToken.None);
            Test.Assert(result.Status == SteamReadStatus.LoginRequired, "Official browser confirmation still requires sign-in for true expiry");
            var count = restores;
            now += TimeSpan.FromMinutes(3);
            responses = new Queue<SteamReadResult<SteamSession>>(new[] { expired, verified });
            result = await policy.ValidateAsync(() => Task.FromResult(responses.Dequeue()),
                () => { restores++; return Task.FromResult(SteamReadStatus.Success); }, () => false, CancellationToken.None);
            Test.Assert(result.IsSuccess && restores == count + 1, "A monotonic cooldown permits a later bounded renewal");
            var before = restores;
            result = await policy.ValidateAsync(() => Task.FromResult(expired),
                () => { restores++; return Task.FromResult(SteamReadStatus.Success); }, () => true, CancellationToken.None);
            Test.Assert(result.Status == SteamReadStatus.LoginRequired && restores == before, "Account mismatch never triggers silent account recovery");
            policy.Reset();
            var malformed = SteamReadResult<SteamSession>.Failed(SteamReadStatus.MalformedPage, "Synthetic malformed page");
            result = await policy.ValidateAsync(() => Task.FromResult(malformed),
                () => { restores++; return Task.FromResult(SteamReadStatus.Success); }, () => false, CancellationToken.None);
            Test.Assert(result.Status == SteamReadStatus.MalformedPage && restores == before, "Malformed pages cannot cause browser reauthentication");
            responses = new Queue<SteamReadResult<SteamSession>>(new[] { expired, malformed });
            result = await policy.ValidateAsync(() => Task.FromResult(responses.Dequeue()),
                () => { restores++; return Task.FromResult(SteamReadStatus.Success); }, () => false, CancellationToken.None);
            Test.Assert(result.Status == SteamReadStatus.MalformedPage, "Browser success or cookie presence without authenticated HTTP proof cannot establish a session");
            policy.Reset();
            using (var cancel = new CancellationTokenSource())
            {
                var cancelled = false;
                try { await policy.ValidateAsync(() => { cancel.Cancel(); return Task.FromResult(verified); },
                    () => Task.FromResult(SteamReadStatus.Success), () => false, cancel.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Test.Assert(cancelled, "Cancellation during the initial verification cannot return success");
            }
            policy.Reset();
            using (var cancel = new CancellationTokenSource())
            {
                var cancelled = false;
                try { await policy.ValidateAsync(() => Task.FromResult(expired), () => { cancel.Cancel(); return Task.FromResult(SteamReadStatus.Success); }, () => false, cancel.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Test.Assert(cancelled, "Closing or stopping cancels session renewal");
            }
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
