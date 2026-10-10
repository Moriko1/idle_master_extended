using System;
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
            Test.Assert(HelperConnectionGuard.Check(false, true, expected, expected, true) == null,
                "A backend disconnect callback does not invalidate the verified local identity.");
            Test.Assert(HelperConnectionGuard.Check(false, true, null, 0, false) == "STEAM_OFFLINE",
                "Legacy positional launches also require a real local account.");
            Test.Assert(HelperConnectionGuard.Check(false, true, null, expected, false) == null,
                "Legacy launches may first establish the local identity.");
            BriefUnknownContextRecovers(expected);
            IdentityAndParentFailuresAreImmediate(expected);
            CallbackShutdownIsDeferred();
            HelperFailureKindsAreStable();
        }

        private static void BriefUnknownContextRecovers(ulong expected)
        {
            var guard = new HelperConnectionGuard(expected);
            Test.Assert(guard.Observe(false, true, 0, TimeSpan.Zero) == null,
                "A zero identity begins a grace period rather than terminating immediately.");
            Test.Assert(guard.Observe(false, false, 0, TimeSpan.FromSeconds(4.9)) == null,
                "Brief client unavailability preserves the helper through the grace period.");
            Test.Assert(guard.Observe(false, true, expected, TimeSpan.FromSeconds(4.99)) == null,
                "The original identity can recover without stopping the queue.");
            Test.Assert(guard.Observe(false, false, 0, TimeSpan.FromSeconds(8)) == null,
                "A later outage starts a new grace period.");
            Test.Assert(guard.Observe(false, true, 0, TimeSpan.FromSeconds(12.99)) == null,
                "Repeated unavailable samples do not shorten the grace period.");
            Test.Assert(guard.Observe(false, false, 0, TimeSpan.FromSeconds(13)) == "STEAM_OFFLINE",
                "An unavailable identity beyond the grace period stops the helper safely.");
            Test.Assert(HelperConnectionGuard.Check(false, true, expected, 0, false) == "STEAM_OFFLINE",
                "Startup still refuses READY for a zero identity.");
        }

        private static void IdentityAndParentFailuresAreImmediate(ulong expected)
        {
            var guard = new HelperConnectionGuard(expected);
            guard.Observe(false, false, 0, TimeSpan.Zero);
            Test.Assert(guard.Observe(false, true, expected + 1, TimeSpan.FromMilliseconds(1)) == "ACCOUNT_MISMATCH",
                "A nonzero different account is rejected immediately during reconnect grace.");
            guard.Observe(false, false, 0, TimeSpan.FromSeconds(1));
            Test.Assert(guard.Observe(true, false, 0, TimeSpan.FromSeconds(1.01)) == "PARENT_EXITED",
                "The parent exiting bypasses connection grace.");
        }

        private static void CallbackShutdownIsDeferred()
        {
            var latch = new HelperFailureLatch();
            latch.BeginDispatch();
            latch.Request("STEAM_OFFLINE");
            Test.Assert(latch.TakePending() == null,
                "A native callback cannot trigger Close and dispose registrations while dispatching.");
            latch.Request("ACCOUNT_MISMATCH");
            latch.EndDispatch();
            Test.Assert(latch.TakePending() == "ACCOUNT_MISMATCH",
                "A verified identity mismatch takes priority and is handled after callback dispatch returns.");
            Test.Assert(latch.TakePending() == null,
                "A pending callback failure is consumed once.");
        }

        private static void HelperFailureKindsAreStable()
        {
            Test.Assert(IdleProcessManager.DecodeHelperError("STEAM_OFFLINE").Failure == IdleHelperFailure.SteamUnavailable,
                "Offline reports carry a recoverable kind rather than an untyped message.");
            Test.Assert(IdleProcessManager.DecodeHelperError("INITIALIZATION_FAILED").Failure == IdleHelperFailure.InitializationFailed,
                "Initialization reports are classified for controlled retry.");
            Test.Assert(IdleProcessManager.DecodeHelperError("ACCOUNT_MISMATCH").Failure == IdleHelperFailure.AccountMismatch,
                "Account mismatch is a fatal identity error.");
            Test.Assert(IdleProcessManager.DecodeHelperError("PARENT_EXITED").Failure == IdleHelperFailure.ParentExited,
                "Parent exit is not retried.");
            Test.Assert(IdleProcessManager.DecodeHelperError("INVALID_ARGUMENTS").Failure == IdleHelperFailure.InvalidProtocol &&
                IdleProcessManager.DecodeHelperError("unrecognized").Failure == IdleHelperFailure.InvalidProtocol,
                "Invalid and unrecognized helper messages do not become recoverable outages.");
            Test.Assert(new IdleHelperException("Legacy failure").Failure == IdleHelperFailure.Unknown,
                "Unclassified legacy failures retain their safe stopping behavior.");
            using (var manager = new IdleProcessManager())
                Test.Assert(manager.GetOwnedProcessAppIds().Count == 0,
                    "An idle process manager exposes no unrelated processes.");
        }
    }
}
