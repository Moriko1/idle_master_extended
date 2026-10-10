namespace IdleMasterExtended.Tests
{
    internal static class SteamSessionNavigationTests
    {
        public static void RunAll()
        {
            OldNavigationCannotCompleteRenewal();
            HttpRedirectRetainsNavigation();
            ScriptHandoffTracksLatestNavigation();
            NetworkFailureCannotExpireLogin();
            LoginExpiryRequiresCompletedCurrentPage();
            TrustedOriginAndPathRequired();
        }

        private static void OldNavigationCannotCompleteRenewal()
        {
            var observer = new SteamSessionNavigation();
            observer.ObserveStarting(1, "about:blank");
            observer.ObserveCompleted(1, false, 0, "about:blank");
            observer.ObserveStarting(2, "https://steamcommunity.com/login/home/");
            observer.ObserveCompleted(2, true, 200, "https://steamcommunity.com/login/home/");
            Test.Assert(!observer.Result.HasValue && observer.TimeoutStatus() == SteamReadStatus.TransientFailure,
                "Earlier blank or login navigation cannot settle a renewal that has not started");
            observer.ObserveStarting(3, SteamSessionNavigation.InitialUri);
            observer.ObserveCompleted(1, false, 0, "about:blank");
            observer.ObserveCompleted(2, false, 0, "https://steamcommunity.com/login/home/");
            Test.Assert(!observer.Result.HasValue, "Cancelled earlier navigation cannot fail the current renewal");
            observer.ObserveCompleted(3, true, 200, "https://steamcommunity.com/profiles/76561198000000001/");
            Test.Assert(observer.Result == SteamReadStatus.Success, "Current official profile page allows a verified cookie re-read");
        }

        private static void HttpRedirectRetainsNavigation()
        {
            var observer = new SteamSessionNavigation();
            observer.ObserveStarting(1, SteamSessionNavigation.InitialUri);
            observer.ObserveStarting(1, "https://login.steampowered.com/jwt/refresh");
            observer.ObserveStarting(1, "https://steamcommunity.com/id/synthetic-test/");
            observer.ObserveCompleted(1, true, 200, "https://steamcommunity.com/id/synthetic-test/");
            Test.Assert(observer.Result == SteamReadStatus.Success, "HTTP redirects with the same ID can complete remembered-login renewal");
        }

        private static void ScriptHandoffTracksLatestNavigation()
        {
            var observer = new SteamSessionNavigation();
            observer.ObserveStarting(1, SteamSessionNavigation.InitialUri);
            observer.ObserveStarting(1, "https://steamcommunity.com/login/home/");
            observer.ObserveCompleted(1, true, 200, "https://steamcommunity.com/login/home/");
            observer.ObserveStarting(2, "https://login.steampowered.com/jwt/refresh");
            observer.ObserveCompleted(1, false, 0, "https://steamcommunity.com/login/home/");
            observer.ObserveCompleted(2, true, 200, "https://login.steampowered.com/jwt/refresh");
            observer.ObserveStarting(3, "https://steamcommunity.com/my/");
            observer.ObserveStarting(1, "https://steamcommunity.com/login/home/");
            observer.ObserveCompleted(1, false, 0, "https://steamcommunity.com/login/home/");
            observer.ObserveCompleted(2, false, 0, "https://login.steampowered.com/jwt/refresh");
            observer.ObserveCompleted(3, true, 200, "https://steamcommunity.com/my/");
            Test.Assert(observer.Result == SteamReadStatus.Success,
                "Official script handoffs with new IDs complete without requiring a changed access cookie");
        }

        private static void NetworkFailureCannotExpireLogin()
        {
            foreach (var status in new[] { 403, 429, 500, 503 })
            {
                var observer = new SteamSessionNavigation();
                observer.ObserveStarting(1, SteamSessionNavigation.InitialUri);
                observer.ObserveStarting(1, "https://steamcommunity.com/login/home/");
                observer.ObserveCompleted(1, true, status, "https://steamcommunity.com/login/home/");
                Test.Assert(observer.Result == SteamReadStatus.TransientFailure &&
                    observer.TimeoutStatus() == SteamReadStatus.TransientFailure,
                    "An HTTP error page cannot prove remembered login expired");
            }
            var failed = new SteamSessionNavigation();
            failed.ObserveStarting(1, SteamSessionNavigation.InitialUri);
            failed.ObserveCompleted(1, false, 0, "https://steamcommunity.com/my/");
            Test.Assert(failed.Result == SteamReadStatus.TransientFailure, "Failed current navigation is a connection failure");
        }

        private static void LoginExpiryRequiresCompletedCurrentPage()
        {
            var observer = new SteamSessionNavigation();
            observer.ObserveStarting(1, SteamSessionNavigation.InitialUri);
            observer.ObserveStarting(1, "https://steamcommunity.com/login/home/");
            Test.Assert(observer.TimeoutStatus() == SteamReadStatus.TransientFailure,
                "An unfinished login navigation cannot prove expiry");
            observer.ObserveCompleted(1, true, 200, "https://steamcommunity.com/login/home/");
            Test.Assert(!observer.Result.HasValue && observer.TimeoutStatus() == SteamReadStatus.LoginRequired,
                "A completed official login page at the renewal deadline requires user sign-in");
            observer.ObserveStarting(2, "https://steamcommunity.com/login/home/");
            observer.ObserveCompleted(1, true, 200, "https://steamcommunity.com/login/home/");
            Test.Assert(observer.TimeoutStatus() == SteamReadStatus.TransientFailure,
                "Previous login completion cannot classify a new unfinished navigation as expired");
        }

        private static void TrustedOriginAndPathRequired()
        {
            foreach (var address in new[] { "http://steamcommunity.com/my/", "https://steamcommunity.com.evil.invalid/my/",
                "https://steamcommunity.com:8443/my/", "https://name:password@steamcommunity.com/my/", "about:blank" })
            {
                var observer = new SteamSessionNavigation();
                observer.ObserveStarting(1, SteamSessionNavigation.InitialUri);
                observer.ObserveStarting(2, address);
                observer.ObserveCompleted(2, true, 200, address);
                Test.Assert(!observer.Result.HasValue && observer.TimeoutStatus() == SteamReadStatus.TransientFailure,
                    "A blank or untrusted origin cannot complete official renewal");
            }
            var wrongPath = new SteamSessionNavigation();
            wrongPath.ObserveStarting(1, SteamSessionNavigation.InitialUri);
            wrongPath.ObserveCompleted(1, true, 200, "https://steamcommunity.com/myth/");
            Test.Assert(!wrongPath.Result.HasValue, "A path sharing the my prefix is not an identity route");
            wrongPath.ObserveStarting(2, "https://steamcommunity.com/login_fake/");
            wrongPath.ObserveCompleted(2, true, 200, "https://steamcommunity.com/login_fake/");
            Test.Assert(wrongPath.TimeoutStatus() == SteamReadStatus.TransientFailure,
                "A path sharing the login prefix cannot prove expiry");
        }
    }
}
