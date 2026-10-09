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
                if (Array.IndexOf(args, "--render") >= 0)
                {
                    DesktopRenderTests.Run();
                    return 0;
                }
                Test.RunAsync("Community reads and badge parsing", CommunityReadTests.RunAllAsync);
                Test.RunAsync("Idle run supervision and cancellation", IdleRunTests.RunAllAsync);
                Test.Run("Settings save and cancellation", SettingsTests.RunAll);
                Test.Run("Steam session identity", SessionTests.RunAll);
                Console.WriteLine("PASS: all 4 regression suites.");
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
