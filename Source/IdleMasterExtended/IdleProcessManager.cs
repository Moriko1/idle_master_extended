using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    public sealed class IdleProcessManager : IIdleHelperFactory, IDisposable
    {
        private readonly string executablePath;
        private readonly TimeSpan readinessTimeout;
        private readonly object sync = new object();
        private readonly HashSet<OwnedIdleHelper> owned = new HashSet<OwnedIdleHelper>();
        private bool disposed;

        public IdleProcessManager(string executablePath = null, TimeSpan? readinessTimeout = null)
        {
            this.executablePath = Path.GetFullPath(executablePath ??
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam-idle.exe"));
            this.readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(10);
            if (this.readinessTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(readinessTimeout));
        }

        public async Task<IIdleHelper> StartAsync(int appId, ulong expectedSteamId, CancellationToken cancellationToken)
        {
            if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
            if (expectedSteamId == 0) throw new ArgumentOutOfRangeException(nameof(expectedSteamId));
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync) if (disposed) throw new ObjectDisposedException(nameof(IdleProcessManager));
            if (!File.Exists(executablePath))
                throw new IdleHelperException("steam-idle.exe is missing. Extract the complete application package.", IdleHelperFailure.InvalidProtocol);

            var pipeName = "SteamIdle_" + Guid.NewGuid().ToString("N");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                PipeAccessRights.FullControl, AccessControlType.Allow));
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            WindowsJob job = null;
            OwnedIdleHelper helper = null;
            try
            {
                job = new WindowsJob();
                var arguments = appId.ToString(CultureInfo.InvariantCulture) +
                    " --parent-pid " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                    " --expected-steamid " + expectedSteamId.ToString(CultureInfo.InvariantCulture) +
                    " --status-pipe " + pipeName;
                var process = job.StartProcess(executablePath, arguments, Path.GetDirectoryName(executablePath));
                helper = new OwnedIdleHelper(appId, process, job, pipe, Remove);
                lock (sync)
                {
                    if (disposed) throw new ObjectDisposedException(nameof(IdleProcessManager));
                    owned.Add(helper);
                }
                await AwaitReadyAsync(helper, expectedSteamId, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                helper.BeginMonitoring();
                return helper;
            }
            catch
            {
                if (helper != null) helper.Dispose();
                else { pipe.Dispose(); if (job != null) job.Dispose(); }
                throw;
            }
        }

        private async Task AwaitReadyAsync(OwnedIdleHelper helper, ulong expectedSteamId, CancellationToken token)
        {
            using (var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var timeout = Task.Delay(readinessTimeout, timeoutCancellation.Token);
                var ready = helper.ReadReadyAsync(expectedSteamId);
                try
                {
                    var completed = await Task.WhenAny(ready, helper.Completion, timeout).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (completed == timeout)
                        throw new IdleHelperException("The idling helper did not become ready. Check Steam is running and signed in.", IdleHelperFailure.InitializationFailed);
                    if (completed == helper.Completion)
                    {
                        await helper.Completion.ConfigureAwait(false);
                        throw new IdleHelperException("The idling helper stopped before becoming ready.", IdleHelperFailure.UnexpectedExit);
                    }
                    await ready.ConfigureAwait(false);
                    if (helper.Completion.IsCompleted) await helper.Completion.ConfigureAwait(false);
                    if (!helper.IsRunning)
                        throw new IdleHelperException("The idling helper stopped before becoming ready.", IdleHelperFailure.UnexpectedExit);
                }
                finally
                {
                    timeoutCancellation.Cancel();
                    _ = ready.ContinueWith(task => { var ignored = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }

        private void Remove(OwnedIdleHelper helper) { lock (sync) owned.Remove(helper); }

        // Read-only process identity for the local activity monitor. These PIDs
        // are captured when this app creates each helper, including startup.
        public IReadOnlyDictionary<int, int> GetOwnedProcessAppIds()
        {
            lock (sync) return new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(
                owned.ToDictionary(helper => helper.ProcessId, helper => helper.AppId));
        }

        public void Dispose()
        {
            List<OwnedIdleHelper> helpers;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                helpers = owned.ToList();
                owned.Clear();
            }
            foreach (var helper in helpers) helper.Dispose();
        }

        private sealed class OwnedIdleHelper : IIdleHelper
        {
            private readonly Process process;
            private readonly WindowsJob job;
            private readonly NamedPipeServerStream pipe;
            private readonly Action<OwnedIdleHelper> removed;
            private readonly TaskCompletionSource<bool> completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object sync = new object();
            private StreamReader reader;
            private bool disposed;
            public int AppId { get; private set; }
            internal int ProcessId { get; private set; }
            public ulong SteamId { get; private set; }
            public Task Completion { get { return completion.Task; } }
            public bool IsRunning
            {
                get
                {
                    lock (sync)
                    {
                        if (disposed || completion.Task.IsCompleted) return false;
                        try { return !process.HasExited; }
                        catch (InvalidOperationException) { return false; }
                    }
                }
            }

            internal OwnedIdleHelper(int appId, Process process, WindowsJob job,
                NamedPipeServerStream pipe, Action<OwnedIdleHelper> removed)
            {
                AppId = appId;
                ProcessId = process.Id;
                this.process = process;
                this.job = job;
                this.pipe = pipe;
                this.removed = removed;
                process.Exited += ProcessExited;
                process.EnableRaisingEvents = true;
                if (process.HasExited) ProcessExited(process, EventArgs.Empty);
            }

            internal async Task ReadReadyAsync(ulong expectedSteamId)
            {
                await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                    throw new IdleHelperException("The idling helper stopped before becoming ready.", IdleHelperFailure.UnexpectedExit);
                if (line != null && line.StartsWith("ERROR ", StringComparison.Ordinal))
                    throw Error(line.Substring(6));
                var fields = (line ?? "").Split(' ');
                int appId;
                ulong steamId;
                if (fields.Length != 3 || fields[0] != "READY" ||
                    !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out appId) ||
                    !ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out steamId) ||
                    appId != AppId || steamId == 0)
                    throw new IdleHelperException("The idling helper reported an invalid game or Steam account.", IdleHelperFailure.InvalidProtocol);
                if (steamId != expectedSteamId)
                    throw new IdleHelperException("The idling helper is using a different Steam account.", IdleHelperFailure.AccountMismatch);
                SteamId = steamId;
            }

            internal void BeginMonitoring()
            {
                MonitorAsync().ContinueWith(task => { var ignored = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            private async Task MonitorAsync()
            {
                try
                {
                    while (true)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        lock (sync) if (disposed) return;
                        if (line == null)
                        {
                            Fail("The idling helper lost its connection. Check Steam and try again.", IdleHelperFailure.UnexpectedExit);
                            return;
                        }
                        if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                        {
                            var error = Error(line.Substring(6));
                            Fail(error.Message, error.Failure);
                            return;
                        }
                        Fail("The idling helper reported an invalid status.", IdleHelperFailure.InvalidProtocol);
                        return;
                    }
                }
                catch (Exception)
                {
                    lock (sync) if (disposed) return;
                    Fail("The idling helper lost its connection. Check Steam and try again.", IdleHelperFailure.UnexpectedExit);
                }
            }

            private async void ProcessExited(object sender, EventArgs args)
            {
                // Give a buffered ERROR line a chance to preserve its actionable reason.
                await Task.Delay(50).ConfigureAwait(false);
                lock (sync) if (disposed) return;
                Fail("The idling helper exited unexpectedly. Check Steam and try again.", IdleHelperFailure.UnexpectedExit);
            }

            private void Fail(string reason, IdleHelperFailure failure)
            {
                lock (sync)
                {
                    if (disposed) return;
                    completion.TrySetException(new IdleHelperException(reason, failure));
                }
                Dispose();
            }

            public void Dispose()
            {
                lock (sync)
                {
                    if (disposed) return;
                    disposed = true;
                    process.Exited -= ProcessExited;
                    completion.TrySetResult(true);
                    // An exit callback runs on a worker thread. Cleanup errors
                    // must neither crash the app nor prevent the remaining
                    // owned handles from being released.
                    DisposeResource(job);
                    DisposeResource(pipe);
                    if (reader != null) DisposeResource(reader);
                    DisposeResource(process);
                }
                removed(this);
            }

            private static void DisposeResource(IDisposable resource)
            {
                try { resource.Dispose(); }
                catch (Exception ex) { Logger.Exception(ex, "Helper cleanup"); }
            }

            private static IdleHelperException Error(string code)
            {
                return DecodeHelperError(code);
            }
        }

        internal static IdleHelperException DecodeHelperError(string code)
        {
            switch (code)
            {
                case "ACCOUNT_MISMATCH": return new IdleHelperException("Steam is signed in to a different account. Switch accounts and try again.", IdleHelperFailure.AccountMismatch);
                case "STEAM_OFFLINE":
                case "STEAM_DISCONNECTED": return new IdleHelperException("Steam disconnected or signed out. Sign in to Steam and try again.", IdleHelperFailure.SteamUnavailable);
                case "INITIALIZATION_FAILED": return new IdleHelperException("Steam could not initialize. Check Steam is running and uses the same Windows user and elevation.", IdleHelperFailure.InitializationFailed);
                case "PARENT_EXITED": return new IdleHelperException("The application that started this helper closed.", IdleHelperFailure.ParentExited);
                case "INVALID_ARGUMENTS": return new IdleHelperException("The idling helper received invalid startup arguments.", IdleHelperFailure.InvalidProtocol);
                default: return new IdleHelperException("The idling helper reported an invalid failure status. Check Steam and try again.", IdleHelperFailure.InvalidProtocol);
            }
        }
    }
}
