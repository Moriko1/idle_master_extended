using System;
using System.Collections.Generic;

namespace IdleMasterExtended
{
    // HTTP redirects retain a navigation ID; Steam's script handoffs can start another.
    // Completion is only a renewal hint. The caller must still verify the authenticated viewer.
    internal sealed class SteamSessionNavigation
    {
        internal const string InitialUri = "https://steamcommunity.com/my/?l=english";
        private readonly HashSet<ulong> superseded = new HashSet<ulong>();
        private ulong? navigationId;
        private bool completedLogin;

        public SteamReadStatus? Result { get; private set; }

        public void ObserveStarting(ulong id, string uri)
        {
            if (Result.HasValue || superseded.Contains(id) || !SteamSessionService.IsTrustedSteamUri(uri)) return;
            if (!navigationId.HasValue && uri != InitialUri) return;
            if (navigationId.HasValue && navigationId.Value != id) superseded.Add(navigationId.Value);
            navigationId = id;
            completedLogin = false;
        }

        public void ObserveCompleted(ulong id, bool isSuccess, int httpStatus, string uri)
        {
            if (Result.HasValue || navigationId != id || !SteamSessionService.IsTrustedSteamUri(uri)) return;
            if (!isSuccess || httpStatus >= 400)
            {
                Result = SteamReadStatus.TransientFailure;
                return;
            }

            var page = new Uri(uri, UriKind.Absolute);
            completedLogin = IsPath(page.AbsolutePath, "/login");
            if (page.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) &&
                (IsPath(page.AbsolutePath, "/my") ||
                 page.AbsolutePath.StartsWith("/profiles/", StringComparison.OrdinalIgnoreCase) ||
                 page.AbsolutePath.StartsWith("/id/", StringComparison.OrdinalIgnoreCase)))
                Result = SteamReadStatus.Success;
        }

        public SteamReadStatus TimeoutStatus()
        {
            return Result ?? (completedLogin ? SteamReadStatus.LoginRequired : SteamReadStatus.TransientFailure);
        }

        private static bool IsPath(string path, string prefix)
        {
            return path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
