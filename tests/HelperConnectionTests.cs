using steam_idle;

namespace IdleMasterExtended.Tests
{
    internal static class HelperConnectionTests
    {
        public static void RunAll()
        {
            const ulong expected = 76561198000000001;
            // Back-end disconnection never invalidates an otherwise verified client.
            // Repeated disconnected heartbeats and the eventual reconnection use the
            // same decision, without a timeout that could terminate a long outage.
            for (var heartbeat = 0; heartbeat < 3600; heartbeat++)
                Test.Assert(HelperConnectionGuard.Check(false, true, expected, expected, false) == null,
                    "A server reconnect preserves the same local Steam account.");
            Test.Assert(HelperConnectionGuard.Check(false, true, expected, expected, false) == null,
                "Reconnection leaves the original helper running.");
            Test.Assert(HelperConnectionGuard.Check(true, true, expected, expected, false) == "PARENT_EXITED",
                "The helper exits when its parent exits even during a reconnect.");
            Test.Assert(HelperConnectionGuard.Check(false, false, expected, expected, false) == "STEAM_OFFLINE",
                "Closing the Steam client remains a failure.");
            Test.Assert(HelperConnectionGuard.Check(false, true, expected, 0, false) == "STEAM_OFFLINE",
                "An unavailable local account cannot be assumed to be the remembered account.");
            Test.Assert(HelperConnectionGuard.Check(false, true, expected, expected + 1, false) == "ACCOUNT_MISMATCH",
                "A different local Steam account always fails, including while reconnecting.");
            Test.Assert(HelperConnectionGuard.Check(false, true, expected, expected, true) == "STEAM_OFFLINE",
                "Explicit logoff invalidates a cached matching Steam ID.");
            Test.Assert(HelperConnectionGuard.Check(false, true, null, 0, false) == "STEAM_OFFLINE",
                "Legacy positional launches also require a real local account.");
            Test.Assert(HelperConnectionGuard.Check(false, true, null, expected, false) == null,
                "Legacy launches may first establish the local identity.");
        }
    }
}
