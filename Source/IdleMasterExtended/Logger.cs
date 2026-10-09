using System;
using System.IO;
using System.Text;
namespace IdleMasterExtended
{
    public static class Logger
    {
        private static readonly object Gate = new object();
        public static void Exception(Exception ex, params string[] context)
        {
            // Do not log exception messages, response bodies or request headers: they can contain credentials.
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.Logs);
                    var path = Path.Combine(AppPaths.Logs, "error.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
                    File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + string.Join(" / ", context) +
                        Environment.NewLine + ex.GetType().FullName + Environment.NewLine + ex.StackTrace + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* A logging failure must not turn a recoverable error into a crash. */ }
        }
    }
}
