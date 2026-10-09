namespace steam_idle
{
    /// <summary>
    /// Retains a verified local Steam account through server reconnects. Server
    /// connectivity is deliberately absent: BLoggedOn is not a local login check.
    /// https://partner.steamgames.com/doc/api/ISteamUser#BLoggedOn
    /// </summary>
    internal static class HelperConnectionGuard
    {
        internal static string Check(bool parentExited, bool clientRunning,
            ulong? expectedSteamId, ulong currentSteamId, bool confirmedLogoff)
        {
            if (parentExited) return "PARENT_EXITED";
            if (!clientRunning || currentSteamId == 0) return "STEAM_OFFLINE";
            if (expectedSteamId.HasValue && currentSteamId != expectedSteamId.Value)
                return "ACCOUNT_MISMATCH";
            if (confirmedLogoff) return "STEAM_OFFLINE";
            return null;
        }
    }
}
