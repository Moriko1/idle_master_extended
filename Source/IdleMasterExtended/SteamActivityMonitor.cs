using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace IdleMasterExtended
{
    public enum SteamClientPresence { Present, Absent, Unknown }
    public enum SteamAccountPresence { Matches, Changed, Unknown }
    public enum SteamGameplayPresence { None, Playing, Unknown }

    public sealed class SteamActivitySnapshot
    {
        public SteamClientPresence Client { get; private set; }
        public SteamAccountPresence Account { get; private set; }
        public SteamGameplayPresence Gameplay { get; private set; }
        public IReadOnlyList<int> PlayingAppIds { get; private set; }

        internal SteamActivitySnapshot(SteamClientPresence client, SteamAccountPresence account,
            SteamGameplayPresence gameplay, IEnumerable<int> appIds = null)
        {
            Client = client;
            Account = account;
            Gameplay = gameplay;
            PlayingAppIds = Array.AsReadOnly((appIds ?? Enumerable.Empty<int>()).Distinct().OrderBy(id => id).ToArray());
        }

        internal static SteamActivitySnapshot Unknown()
        {
            return new SteamActivitySnapshot(SteamClientPresence.Unknown, SteamAccountPresence.Unknown,
                SteamGameplayPresence.Unknown);
        }
    }

    /// <summary>
    /// Reads local client activity without loading Steam's native SDK into the main UI process.
    /// Steam's client registry is a best-effort hint, not a public Steamworks contract. Missing
    /// or unreadable information is Unknown, never evidence that a game/account disappeared.
    /// </summary>
    public sealed class SteamActivityMonitor : IDisposable
    {
        private readonly ISteamActivityBackend backend;
        private readonly int applicationProcessId;
        private readonly object sync = new object();
        private readonly Dictionary<int, TimeSpan> recentOwnedApps = new Dictionary<int, TimeSpan>();
        private readonly Stopwatch recentOwnershipClock = Stopwatch.StartNew();
        private bool disposed;

        public SteamActivityMonitor() : this(new WindowsSteamActivityBackend(), Process.GetCurrentProcess().Id) { }

        internal SteamActivityMonitor(ISteamActivityBackend backend, int applicationProcessId)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.applicationProcessId = applicationProcessId;
        }

        public SteamActivitySnapshot Read(ulong expectedSteamId, IEnumerable<int> ownedHelperAppIds,
            IEnumerable<int> ownedHelperProcessIds)
        {
            lock (sync)
            {
                if (disposed) return SteamActivitySnapshot.Unknown();
                try
                {
                    // Both reads are bounded, local, and performed by the caller's background task.
                    var processes = backend.ReadProcesses();
                    var registry = backend.ReadRegistry();
                    var trackedGames = registry != null && registry.ClientKnown
                        ? backend.ReadTrackedGames(registry.ExecutablePath) : null;
                    var instant = recentOwnershipClock.Elapsed;
                    foreach (var appId in ownedHelperAppIds ?? Enumerable.Empty<int>())
                        if (appId > 0) recentOwnedApps[appId] = instant;
                    foreach (var appId in recentOwnedApps.Where(pair => instant - pair.Value > TimeSpan.FromSeconds(65))
                        .Select(pair => pair.Key).ToArray()) recentOwnedApps.Remove(appId);
                    return Classify(expectedSteamId, registry, processes, backend.ReadProcessImagePath,
                        applicationProcessId, recentOwnedApps.Keys, ownedHelperProcessIds, trackedGames,
                        backend.ReadProcessStartTime);
                }
                catch (Exception exception) when (IsReadFailure(exception))
                {
                    return SteamActivitySnapshot.Unknown();
                }
            }
        }

        internal static SteamActivitySnapshot Classify(ulong expectedSteamId, SteamRegistryActivity registry,
            IReadOnlyList<SteamProcessEntry> processes, Func<int, string> imagePath, int applicationProcessId,
            IEnumerable<int> helperAppIds, IEnumerable<int> helperProcessIds, SteamTrackedGameSnapshot trackedGames = null,
            Func<int, DateTimeOffset?> processStarted = null)
        {
            if (processes == null || processes.Count > 32768) return SteamActivitySnapshot.Unknown();
            var clients = processes.Where(process => string.Equals(process.Name, "steam.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (clients.Length == 0)
                return new SteamActivitySnapshot(SteamClientPresence.Absent, SteamAccountPresence.Unknown,
                    SteamGameplayPresence.Unknown);
            if (registry == null || !registry.ClientKnown || registry.ClientProcessId <= 0)
                return SteamActivitySnapshot.Unknown();
            var client = clients.SingleOrDefault(process => process.Id == registry.ClientProcessId);
            if (client == null || !SamePath(registry.ExecutablePath, imagePath(client.Id)))
                return SteamActivitySnapshot.Unknown();

            var account = expectedSteamId == 0 || registry.AccountId == 0
                ? SteamAccountPresence.Unknown
                : registry.AccountId == unchecked((uint)expectedSteamId)
                    ? SteamAccountPresence.Matches : SteamAccountPresence.Changed;
            if (account != SteamAccountPresence.Matches)
                return new SteamActivitySnapshot(SteamClientPresence.Present, account, SteamGameplayPresence.Unknown);

            var excluded = new HashSet<int>((helperProcessIds ?? Enumerable.Empty<int>()).Where(id => id > 0));
            if (applicationProcessId > 0) excluded.Add(applicationProcessId);
            var ownedApps = new HashSet<int>(helperAppIds ?? Enumerable.Empty<int>());
            var children = processes.GroupBy(process => process.ParentId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var excludedQueue = new Queue<int>(excluded);
            while (excludedQueue.Count > 0)
            {
                SteamProcessEntry[] descendants;
                if (!children.TryGetValue(excludedQueue.Dequeue(), out descendants)) continue;
                foreach (var child in descendants) if (excluded.Add(child.Id)) excludedQueue.Enqueue(child.Id);
            }
            var queue = new Queue<int>();
            var visited = new HashSet<int> { client.Id };
            queue.Enqueue(client.Id);
            var playing = false;
            var uncertainProcess = false;
            var steamDirectory = Path.GetDirectoryName(Path.GetFullPath(registry.ExecutablePath));
            while (queue.Count > 0)
            {
                SteamProcessEntry[] descendants;
                if (!children.TryGetValue(queue.Dequeue(), out descendants)) continue;
                foreach (var child in descendants)
                {
                    if (!visited.Add(child.Id) || excluded.Contains(child.Id)) continue;
                    // Utility parents remain in the graph: a launcher can create the actual game.
                    queue.Enqueue(child.Id);
                    if (IsExternalUtilityName(child.Name)) continue;
                    if (!IsSteamUtilityName(child.Name))
                    {
                        // A clean Steam tracking log distinguishes a finished game from a
                        // third-party launcher/browser process that remains open afterwards.
                        if (trackedGames == null || !trackedGames.Known) playing = true;
                        else if (!trackedGames.Games.ContainsKey(child.Id))
                        {
                            var started = processStarted == null ? null : processStarted(child.Id);
                            DateTimeOffset removedAt;
                            if (!started.HasValue) uncertainProcess = true;
                            else if (trackedGames.Removed.TryGetValue(child.Id, out removedAt) &&
                                started.Value <= removedAt.AddSeconds(5)) { }
                            else if (trackedGames.CoverageStart.HasValue && started.Value < trackedGames.CoverageStart.Value)
                                playing = true;
                        }
                        continue;
                    }
                    var utilityPath = imagePath(child.Id);
                    if (utilityPath == null) { uncertainProcess = true; continue; }
                    if (!IsClientUtilityPath(steamDirectory, utilityPath) &&
                        (trackedGames == null || !trackedGames.Known)) playing = true;
                }
            }

            var identifiedGames = new HashSet<int>();
            if (trackedGames != null)
            {
                var livePids = new HashSet<int>(processes.Select(process => process.Id));
                foreach (var game in trackedGames.Games.Values)
                {
                    if (!livePids.Contains(game.ProcessId)) continue;
                    if (excluded.Contains(game.ProcessId)) { ownedApps.Add(game.AppId); continue; }
                    var started = processStarted == null ? null : processStarted(game.ProcessId);
                    if (!started.HasValue) { uncertainProcess = true; continue; }
                    // A reused PID cannot keep an old game's tracking record alive indefinitely.
                    if (started.Value > game.TrackedAt.AddSeconds(5)) continue;
                    identifiedGames.Add(game.AppId);
                }
                if (identifiedGames.Count > 0) playing = true;
                if (!trackedGames.Known && trackedGames.WasAvailable) uncertainProcess = true;
            }
            var running = registry.RunningAppIds ?? new int[0];
            var foreignApps = registry.RunningAppsKnown
                ? running.Where(id => id > 0 && !ownedApps.Contains(id)).Distinct().ToArray() : new int[0];
            if (foreignApps.Length > 0) playing = true;
            var appIds = foreignApps.Concat(identifiedGames);
            if (playing)
                return new SteamActivitySnapshot(SteamClientPresence.Present, account,
                    SteamGameplayPresence.Playing, appIds);
            return new SteamActivitySnapshot(SteamClientPresence.Present, account,
                registry.RunningAppsKnown && !uncertainProcess ? SteamGameplayPresence.None : SteamGameplayPresence.Unknown);
        }

        private static bool IsSteamUtilityName(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "steam.exe":
                case "steamwebhelper.exe":
                case "steamservice.exe":
                case "gameoverlayui.exe":
                case "steamerrorreporter.exe":
                case "steamerrorreporter64.exe":
                case "steam_monitor.exe":
                case "steam_monitor64.exe":
                case "crashhandler.exe":
                case "crashhandler64.exe":
                case "steamxboxutil.exe": return true;
                default: return false;
            }
        }

        private static bool IsExternalUtilityName(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "chrome.exe": case "msedge.exe": case "firefox.exe": case "brave.exe":
                case "opera.exe": case "iexplore.exe": case "msiexec.exe": case "werfault.exe":
                case "werfaultsecure.exe": return true;
                default: return false;
            }
        }

        private static bool IsClientUtilityPath(string steamDirectory, string executable)
        {
            var full = Path.GetFullPath(executable);
            var prefix = steamDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                full.IndexOf(Path.DirectorySeparatorChar + "steamapps" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool SamePath(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second) ||
                !Path.IsPathRooted(first) || !Path.IsPathRooted(second)) return false;
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReadFailure(Exception exception)
        {
            return exception is IOException || exception is Win32Exception || exception is SecurityException ||
                exception is UnauthorizedAccessException || exception is ArgumentException ||
                exception is InvalidOperationException || exception is NotSupportedException;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                backend.Dispose();
            }
        }
    }

    internal sealed class SteamRegistryActivity
    {
        internal bool ClientKnown;
        internal int ClientProcessId;
        internal uint AccountId;
        internal string ExecutablePath;
        internal bool RunningAppsKnown;
        internal IReadOnlyList<int> RunningAppIds;
    }

    internal sealed class SteamProcessEntry
    {
        internal int Id;
        internal int ParentId;
        internal string Name;
        internal SteamProcessEntry(int id, int parentId, string name) { Id = id; ParentId = parentId; Name = name; }
    }

    internal interface ISteamActivityBackend : IDisposable
    {
        SteamRegistryActivity ReadRegistry();
        IReadOnlyList<SteamProcessEntry> ReadProcesses();
        string ReadProcessImagePath(int processId);
        DateTimeOffset? ReadProcessStartTime(int processId);
        SteamTrackedGameSnapshot ReadTrackedGames(string steamExecutable);
    }

    internal sealed class SteamTrackedGame
    {
        internal readonly int ProcessId, AppId;
        internal readonly DateTimeOffset TrackedAt;
        internal SteamTrackedGame(int processId, int appId, DateTimeOffset trackedAt)
        { ProcessId = processId; AppId = appId; TrackedAt = trackedAt; }
    }

    internal sealed class SteamTrackedGameSnapshot
    {
        internal readonly bool Known, WasAvailable;
        internal readonly IReadOnlyDictionary<int, SteamTrackedGame> Games;
        internal readonly IReadOnlyDictionary<int, DateTimeOffset> Removed;
        internal readonly DateTimeOffset? CoverageStart;
        internal SteamTrackedGameSnapshot(bool known, bool wasAvailable, IDictionary<int, SteamTrackedGame> games,
            IDictionary<int, DateTimeOffset> removed = null, DateTimeOffset? coverageStart = null)
        {
            Known = known;
            WasAvailable = wasAvailable;
            Games = new System.Collections.ObjectModel.ReadOnlyDictionary<int, SteamTrackedGame>(
                new Dictionary<int, SteamTrackedGame>(games));
            Removed = new System.Collections.ObjectModel.ReadOnlyDictionary<int, DateTimeOffset>(
                new Dictionary<int, DateTimeOffset>(removed ?? new Dictionary<int, DateTimeOffset>()));
            CoverageStart = coverageStart;
        }
    }

    /// <summary>
    /// Incremental, bounded parser for Steam's own tracked-process diagnostic log. Its schema
    /// is observed client data, so failures are unknown and no log text is copied to our logs.
    /// </summary>
    internal sealed class SteamGameProcessLog
    {
        internal const int MaximumReadBytes = 256 * 1024;
        private static readonly Regex Event = new Regex(
            @"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s+AppID (?<app>\d+) (?<action>adding PID|no longer tracking PID) (?<pid>\d+)(?:[\s,].*)?$",
            RegexOptions.CultureInvariant);
        private readonly Dictionary<int, SteamTrackedGame> games = new Dictionary<int, SteamTrackedGame>();
        private readonly Dictionary<int, DateTimeOffset> removed = new Dictionary<int, DateTimeOffset>();
        private string previousPath;
        private long position;
        private DateTime writeTime;
        private string pending = "";
        private bool wasAvailable;
        private bool known;
        private bool hasSchemaProof;
        private DateTimeOffset? coverageStart;

        internal SteamTrackedGameSnapshot Read(string path)
        {
            try
            {
                if (!string.Equals(previousPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    previousPath = path;
                    position = 0;
                    pending = "";
                    games.Clear();
                    removed.Clear();
                    hasSchemaProof = false;
                    coverageStart = null;
                    wasAvailable = false;
                    known = false;
                }
                var info = new FileInfo(path);
                if (!info.Exists) return Snapshot(false);
                if (position == info.Length && writeTime == info.LastWriteTimeUtc) return Snapshot(known);
                var reset = info.Length < position || (position == info.Length && writeTime != info.LastWriteTimeUtc);
                if (reset)
                { position = 0; pending = ""; games.Clear(); removed.Clear(); hasSchemaProof = false; coverageStart = null; }
                var initial = position == 0;
                var start = Math.Max(position, info.Length - MaximumReadBytes);
                var droppedBytes = start > position;
                var count = checked((int)(info.Length - start));
                var bytes = new byte[count];
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
                {
                    file.Position = start;
                    var read = 0;
                    while (read < count)
                    {
                        var size = file.Read(bytes, read, count - read);
                        if (size == 0) throw new IOException("Steam process log changed during reading.");
                        read += size;
                    }
                }
                position = start + count;
                writeTime = info.LastWriteTimeUtc;
                if (droppedBytes)
                {
                    pending = "";
                    var newline = Array.IndexOf(bytes, (byte)'\n');
                    var skip = newline < 0 ? bytes.Length : newline + 1;
                    bytes = bytes.Skip(skip).ToArray();
                }
                var text = pending + Encoding.UTF8.GetString(bytes);
                var lines = text.Split('\n');
                pending = lines[lines.Length - 1];
                if (pending.Length > 4096) { pending = ""; known = false; return Snapshot(false); }
                var clean = true;
                for (var index = 0; index < lines.Length - 1; index++)
                    if (!ApplyLine(lines[index], games, removed, timestamp =>
                    { hasSchemaProof = true; if (!coverageStart.HasValue) coverageStart = timestamp; })) clean = false;
                wasAvailable = true;
                // Missing incremental events cannot establish that an already tracked game ended.
                known = hasSchemaProof && clean && (initial || !droppedBytes);
                return Snapshot(known);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                exception is SecurityException || exception is ArgumentException || exception is NotSupportedException)
            {
                return Snapshot(false);
            }
        }

        private SteamTrackedGameSnapshot Snapshot(bool clean)
        { return new SteamTrackedGameSnapshot(clean, wasAvailable, games, removed, coverageStart); }

        internal static bool ApplyLine(string line, IDictionary<int, SteamTrackedGame> current,
            IDictionary<int, DateTimeOffset> removed = null, Action<DateTimeOffset> recognized = null)
        {
            if (line.IndexOf("AppID", StringComparison.Ordinal) < 0) return true;
            // Steam appends the process command line and role details. Parse only the fixed
            // event prefix; never retain that payload (which can contain account information).
            var match = Event.Match(line.TrimEnd('\r'));
            int appId, processId;
            DateTime timestamp;
            if (!match.Success || !int.TryParse(match.Groups["app"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out appId) || appId <= 0 ||
                !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out processId) || processId <= 0 ||
                !DateTime.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out timestamp)) return false;
            var instant = new DateTimeOffset(timestamp).ToUniversalTime();
            if (recognized != null) recognized(instant);
            if (match.Groups["action"].Value == "adding PID")
            {
                if (current.Count >= 32768 && !current.ContainsKey(processId)) return false;
                current[processId] = new SteamTrackedGame(processId, appId, instant);
                if (removed != null) removed.Remove(processId);
            }
            else
            {
                SteamTrackedGame previous;
                if (current.TryGetValue(processId, out previous) && previous.AppId == appId && instant >= previous.TrackedAt)
                    current.Remove(processId);
                if (removed != null)
                {
                    if (removed.Count >= 32768 && !removed.ContainsKey(processId))
                        foreach (var oldest in removed.OrderBy(pair => pair.Value).Take(1024).Select(pair => pair.Key).ToArray())
                            removed.Remove(oldest);
                    removed[processId] = instant;
                }
            }
            return true;
        }
    }

    internal sealed class RunningAppsCache
    {
        private readonly Func<IReadOnlyList<int>> read;
        private readonly Func<DateTimeOffset> now;
        private DateTimeOffset nextRead = DateTimeOffset.MinValue;
        private DateTimeOffset expires = DateTimeOffset.MinValue;
        private DateTimeOffset lastRead = DateTimeOffset.MinValue;
        private IReadOnlyList<int> apps = new int[0];
        private bool dirty = true;
        internal bool Known { get; private set; }

        internal RunningAppsCache(Func<IReadOnlyList<int>> read, Func<DateTimeOffset> now)
        { this.read = read; this.now = now; }

        internal IReadOnlyList<int> Read(bool changed, bool watcherAvailable)
        {
            dirty |= changed;
            var instant = now();
            if (!watcherAvailable && lastRead != DateTimeOffset.MinValue && expires > lastRead.AddSeconds(5))
                expires = lastRead.AddSeconds(5);
            if ((!dirty && instant < expires) || instant < nextRead) return apps;
            try
            {
                var result = read();
                if (result == null) throw new IOException("Steam app activity is unavailable.");
                apps = Array.AsReadOnly(result.Distinct().ToArray());
                Known = true;
                dirty = false;
            }
            catch (Exception exception) when (exception is IOException || exception is SecurityException ||
                exception is UnauthorizedAccessException || exception is ArgumentException)
            {
                // Retain the last hint for safe positive detection, but never claim an empty read.
                Known = false;
                dirty = true;
            }
            nextRead = instant.AddSeconds(2);
            lastRead = instant;
            expires = instant.AddSeconds(watcherAvailable ? 60 : 5);
            return apps;
        }
    }

    internal sealed class WindowsSteamActivityBackend : ISteamActivityBackend
    {
        private const string SteamKey = @"Software\Valve\Steam";
        private readonly RunningAppsCache runningApps;
        private readonly SteamGameProcessLog processLog = new SteamGameProcessLog();
        private RegistryKey watchedApps;
        private EventWaitHandle changed;
        private bool armed;

        internal WindowsSteamActivityBackend()
        {
            var cacheStart = DateTimeOffset.UtcNow;
            var cacheClock = Stopwatch.StartNew();
            runningApps = new RunningAppsCache(ReadRunningApps, () => cacheStart + cacheClock.Elapsed);
        }

        public SteamRegistryActivity ReadRegistry()
        {
            using (var steam = Registry.CurrentUser.OpenSubKey(SteamKey, false))
            using (var active = Registry.CurrentUser.OpenSubKey(SteamKey + @"\ActiveProcess", false))
            {
                if (steam == null || active == null) return new SteamRegistryActivity();
                var result = new SteamRegistryActivity();
                uint pid;
                uint account;
                if (!TryDword(active.GetValue("pid"), out pid) || pid > int.MaxValue ||
                    !TryDword(active.GetValue("ActiveUser"), out account)) return result;
                result.ClientKnown = true;
                result.ClientProcessId = (int)pid;
                result.AccountId = account;
                result.ExecutablePath = steam.GetValue("SteamExe") as string;
                var watchReady = EnsureWatch();
                var hasChanged = changed != null && changed.WaitOne(0);
                if (hasChanged) { armed = false; watchReady = ArmWatch(); }
                result.RunningAppIds = runningApps.Read(hasChanged, watchReady);
                result.RunningAppsKnown = runningApps.Known;
                return result;
            }
        }

        private bool EnsureWatch()
        {
            if (watchedApps == null)
            {
                watchedApps = Registry.CurrentUser.OpenSubKey(SteamKey + @"\Apps", false);
                if (watchedApps == null) return false;
                changed = new EventWaitHandle(false, EventResetMode.AutoReset);
            }
            return armed || ArmWatch();
        }

        private bool ArmWatch()
        {
            if (watchedApps == null || changed == null) return false;
            // Async, thread-agnostic notification. Re-arm only after the previous event is signaled.
            // https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regnotifychangekeyvalue
            armed = RegNotifyChangeKeyValue(watchedApps.Handle, true, 1u | 4u | 0x10000000u,
                changed.SafeWaitHandle, true) == 0;
            if (!armed) { watchedApps.Dispose(); watchedApps = null; changed.Dispose(); changed = null; }
            return armed;
        }

        private IReadOnlyList<int> ReadRunningApps()
        {
            using (var apps = Registry.CurrentUser.OpenSubKey(SteamKey + @"\Apps", false))
            {
                if (apps == null) return null;
                var names = apps.GetSubKeyNames();
                if (names.Length > 32768) return null;
                var running = new HashSet<int>();
                foreach (var name in names)
                {
                    int appId;
                    if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out appId) || appId <= 0) continue;
                    using (var app = apps.OpenSubKey(name, false))
                    {
                        if (app == null) return null;
                        var raw = app.GetValue("Running");
                        if (raw == null) continue;
                        uint value;
                        if (!TryDword(raw, out value) || value > 1) return null;
                        if (value == 1) running.Add(appId);
                    }
                }
                return running.ToArray();
            }
        }

        private static bool TryDword(object value, out uint result)
        {
            if (value is int) { result = unchecked((uint)(int)value); return true; }
            result = 0;
            return false;
        }

        public IReadOnlyList<SteamProcessEntry> ReadProcesses()
        {
            // Read only process IDs/parents/names; never load every process's modules or command line.
            // https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/nf-tlhelp32-createtoolhelp32snapshot
            using (var snapshot = CreateToolhelp32Snapshot(2, 0))
            {
                if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32)) };
                if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var result = new List<SteamProcessEntry>();
                do
                {
                    if (entry.ProcessId <= int.MaxValue && entry.ParentProcessId <= int.MaxValue)
                        result.Add(new SteamProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.Executable));
                    if (result.Count > 32768) throw new IOException("Too many local processes.");
                } while (Process32Next(snapshot, ref entry));
                if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
                return result;
            }
        }

        public string ReadProcessImagePath(int processId)
        {
            using (var process = OpenProcess(0x1000, false, (uint)processId))
            {
                if (process.IsInvalid) return null;
                var path = new StringBuilder(32768);
                uint size = (uint)path.Capacity;
                return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
            }
        }

        public DateTimeOffset? ReadProcessStartTime(int processId)
        {
            using (var process = OpenProcess(0x1000, false, (uint)processId))
            {
                if (process.IsInvalid) return null;
                long created, exited, kernel, user;
                if (!GetProcessTimes(process, out created, out exited, out kernel, out user)) return null;
                return new DateTimeOffset(DateTime.FromFileTimeUtc(created));
            }
        }

        public SteamTrackedGameSnapshot ReadTrackedGames(string steamExecutable)
        {
            if (string.IsNullOrWhiteSpace(steamExecutable) || !Path.IsPathRooted(steamExecutable)) return null;
            return processLog.Read(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(steamExecutable)), "logs", "gameprocess_log.txt"));
        }

        public void Dispose()
        {
            if (watchedApps != null) { watchedApps.Dispose(); watchedApps = null; }
            if (changed != null) { changed.Dispose(); changed = null; }
            armed = false;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size, Usage, ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
        }

        private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeSnapshotHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process,
            out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool subtree,
            uint filter, SafeWaitHandle changed, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
    }
}
