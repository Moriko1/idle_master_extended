using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Forms;
using Steamworks;

namespace steam_idle
{
    internal sealed class HelperArguments
    {
        internal uint AppId { get; private set; }
        internal int? ParentPid { get; private set; }
        internal ulong? ExpectedSteamId { get; private set; }
        internal string StatusPipe { get; private set; }

        internal static bool TryParse(string[] args, out HelperArguments result)
        {
            result = null;
            uint appId;
            if (args == null || args.Length == 0 ||
                !uint.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out appId) || appId == 0)
                return false;
            var parsed = new HelperArguments { AppId = appId };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !seen.Add(args[index])) return false;
                switch (args[index])
                {
                    case "--parent-pid":
                        int pid;
                        if (!int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0)
                            return false;
                        parsed.ParentPid = pid;
                        break;
                    case "--expected-steamid":
                        ulong steamId;
                        if (!ulong.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out steamId) || steamId == 0)
                            return false;
                        parsed.ExpectedSteamId = steamId;
                        break;
                    case "--status-pipe":
                        var pipe = args[index + 1];
                        if (string.IsNullOrEmpty(pipe) || pipe.Length > 128) return false;
                        foreach (var character in pipe)
                            if (!(character >= 'a' && character <= 'z') && !(character >= 'A' && character <= 'Z') &&
                                !(character >= '0' && character <= '9') && character != '_' && character != '-')
                                return false;
                        parsed.StatusPipe = pipe;
                        break;
                    default: return false;
                }
            }
            result = parsed;
            return true;
        }
    }

    internal sealed class HelperStatus : IDisposable
    {
        private NamedPipeClientStream pipe;
        private StreamWriter writer;
        internal bool Connect(string pipeName)
        {
            if (pipeName == null) return true;
            try
            {
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                pipe.Connect(5000);
                writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                return true;
            }
            catch { Dispose(); return false; }
        }

        internal void Send(string message)
        {
            if (writer == null) return;
            try { writer.WriteLine(message); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (writer != null)
            {
                try { writer.Dispose(); } catch (IOException) { }
                writer = null;
            }
            if (pipe != null) { pipe.Dispose(); pipe = null; }
        }
    }

    internal static class Program
    {
        private static void ReportInvalidArguments(string[] args)
        {
            if (args == null) return;
            for (var index = 1; index + 1 < args.Length; index++)
            {
                if (args[index] != "--status-pipe") continue;
                var pipeName = args[index + 1];
                if (string.IsNullOrEmpty(pipeName) || pipeName.Length > 128) return;
                foreach (var character in pipeName)
                    if (!(character >= 'a' && character <= 'z') && !(character >= 'A' && character <= 'Z') &&
                        !(character >= '0' && character <= '9') && character != '_' && character != '-')
                        return;
                using (var status = new HelperStatus())
                {
                    if (status.Connect(pipeName)) status.Send("ERROR INVALID_ARGUMENTS");
                }
                return;
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            HelperArguments options;
            if (!HelperArguments.TryParse(args, out options))
            {
                ReportInvalidArguments(args);
                Environment.ExitCode = 2;
                return;
            }

            var initialized = false;
            Process parent = null;
            using (var status = new HelperStatus())
            {
                if (!status.Connect(options.StatusPipe)) { Environment.ExitCode = 3; return; }
                try
                {
                    if (options.ParentPid.HasValue)
                    {
                        try
                        {
                            parent = Process.GetProcessById(options.ParentPid.Value);
                            var retainedHandle = parent.Handle;
                            if (parent.HasExited || parent.Id == Process.GetCurrentProcess().Id)
                                throw new InvalidOperationException();
                        }
                        catch
                        {
                            status.Send("ERROR PARENT_EXITED");
                            Environment.ExitCode = 4;
                            return;
                        }
                    }

                    Environment.SetEnvironmentVariable("SteamAppId", options.AppId.ToString(CultureInfo.InvariantCulture));
                    if (!SteamAPI.Init())
                    {
                        status.Send("ERROR INITIALIZATION_FAILED");
                        Environment.ExitCode = 5;
                        return;
                    }
                    initialized = true;
                    if (!SteamUser.BLoggedOn())
                    {
                        status.Send("ERROR STEAM_OFFLINE");
                        Environment.ExitCode = 6;
                        return;
                    }
                    var steamId = SteamUser.GetSteamID().m_SteamID;
                    if (options.ExpectedSteamId.HasValue && steamId != options.ExpectedSteamId.Value)
                    {
                        status.Send("ERROR ACCOUNT_MISMATCH");
                        Environment.ExitCode = 7;
                        return;
                    }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new FormSteamIdle(options.AppId, options.ExpectedSteamId, parent, reason =>
                    {
                        status.Send("ERROR " + reason);
                        Environment.ExitCode = 8;
                    }))
                    {
                        status.Send("READY " + options.AppId.ToString(CultureInfo.InvariantCulture) + " " +
                            steamId.ToString(CultureInfo.InvariantCulture));
                        Application.Run(form);
                    }
                }
                catch
                {
                    status.Send("ERROR INITIALIZATION_FAILED");
                    Environment.ExitCode = 5;
                }
                finally
                {
                    if (initialized)
                    {
                        try { SteamAPI.Shutdown(); }
                        catch { }
                    }
                    if (parent != null) parent.Dispose();
                }
            }
        }
    }
}
