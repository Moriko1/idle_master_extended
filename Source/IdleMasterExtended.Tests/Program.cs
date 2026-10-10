using System;
using System.Threading.Tasks;

namespace IdleMasterExtended.Tests
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (Array.IndexOf(args, "--live-session") >= 0)
                    return SessionLiveProbe.Run(Array.IndexOf(args, "--identity-only") >= 0);
                if (Array.IndexOf(args, "--render") >= 0)
                {
                    DesktopRenderTests.Run();
                    return 0;
                }
                Test.RunAsync("Community reads and badge parsing", CommunityReadTests.RunAllAsync);
                Test.RunAsync("Private game verification and atomic queue filtering", async () => { await PrivateGamesTests.RunAllAsync(); await PrivateQueueTests.RunAllAsync(); });
                Test.RunAsync("Idle run supervision and cancellation", IdleRunTests.RunAllAsync);
                Test.Run("Settings save and cancellation", SettingsTests.RunAll);
                Test.Run("Steam session identity", () => { SessionTests.RunAll(); SteamSessionNavigationTests.RunAll(); });
                Test.Run("Session summary and nonactivating controls", SessionSummaryTests.RunAll);
                Test.Run("Helper Steam connection resilience", HelperConnectionTests.RunAll);
                Test.Run("Portable preference persistence", SettingsPersistenceTests.RunAll);
                Test.Run("Empty-queue Start availability", StartAvailabilityTests.RunAll);
                Test.Run("Steam game activity detection", SteamActivityTests.RunAll);
                Test.Run("Background work and quiet run notifications", DesktopRunPolicyTests.RunAll);
                Console.WriteLine("PASS: all 11 regression suites.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: " + exception);
                return 1;
            }
        }
    }

    internal static class Test
    {
        public static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void Run(string name, Action test)
        {
            test();
            Console.WriteLine("PASS: " + name);
        }

        public static void RunAsync(string name, Func<Task> test)
        {
            test().GetAwaiter().GetResult();
            Console.WriteLine("PASS: " + name);
        }
    }
}
