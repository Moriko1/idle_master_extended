using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IdleMasterExtended
{
    // This job owns only a helper created through this instance and its descendants.
    // Starting suspended prevents orphans between process creation and job assignment.
    internal sealed class WindowsJob : IDisposable
    {
        private readonly SafeJobHandle handle;

        internal WindowsJob()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("Idling helpers require Windows.");
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ExtendedLimitInformation)));
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(handle, 9, buffer, (uint)Marshal.SizeOf(typeof(ExtendedLimitInformation))))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch { handle.Dispose(); throw; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal Process StartProcess(string executablePath, string arguments, string workingDirectory)
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Flags = 1, ShowWindow = 0 };
            ProcessInformation created;
            var commandLine = new StringBuilder("\"" + executablePath + "\" " + arguments);
            if (!CreateProcess(executablePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                0x00000004 | 0x08000000, IntPtr.Zero, workingDirectory, ref startup, out created))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Process process = null;
            try
            {
                if (!AssignProcessToJobObject(handle, created.Process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                process = Process.GetProcessById((int)created.ProcessId);
                var retainedHandle = process.Handle;
                if (ResumeThread(created.Thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return process;
            }
            catch
            {
                TerminateProcess(created.Process, 1);
                if (process != null) process.Dispose();
                throw;
            }
            finally { CloseHandle(created.Thread); CloseHandle(created.Process); }
        }

        public void Dispose() { handle.Dispose(); }

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeJobHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, Reserved2Size;
            public IntPtr Reserved2, StdInput, StdOutput, StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process, Thread;
            public uint ProcessId, ThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, IntPtr information, uint informationLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
            string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
