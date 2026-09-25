using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;

namespace RAMMapTray
{
    public struct MemoryStatusInfo
    {
        public ulong TotalMemoryBytes;
        public ulong AvailableMemoryBytes;
        public ulong UsedMemoryBytes;
        public double MemoryLoadPercentage;
        public ulong StandbyMemoryBytes;
        public ulong FreeMemoryBytes;
    }

    public static class NativeMemory
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSetSystemInformation(int SystemInformationClass, ref int SystemInformation, int SystemInformationLength);

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_MEMORY_LIST_INFORMATION
        {
            public ulong ZeroPageCount;
            public ulong FreePageCount;
            public ulong ModifiedPageCount;
            public ulong ModifiedNoWritePageCount;
            public ulong BadPageCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public ulong[] StandbyPageCount;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, int BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x0002;
        private const string SE_PROFILE_SINGLE_PROCESS_NAME = "SeProfileSingleProcessPrivilege";
        private const string SE_INCREASE_QUOTA_NAME = "SeIncreaseQuotaPrivilege";

        private const int SystemMemoryListInformation = 80;
        private const int MemoryEmptyWorkingSets = 2;
        private const int MemoryFlushModifiedList = 3;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 8;

        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_SET_QUOTA = 0x0100;

        private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "dwm", "csrss", "lsass", "services", "smss", 
            "winlogon", "explorer", "audiodg", "svchost", "rammaptray",
            "nvvsvc", "amdrsserv", "taskhostw", "sihost"
        };

        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static MemoryStatusInfo GetMemoryStatus()
        {
            MemoryStatusInfo info = new MemoryStatusInfo();
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();

            if (GlobalMemoryStatusEx(ref memStatus))
            {
                info.TotalMemoryBytes = memStatus.ullTotalPhys;
                info.AvailableMemoryBytes = memStatus.ullAvailPhys;
                info.UsedMemoryBytes = info.TotalMemoryBytes - info.AvailableMemoryBytes;
                info.MemoryLoadPercentage = memStatus.dwMemoryLoad;
            }

            try
            {
                int structSize = Marshal.SizeOf(typeof(SYSTEM_MEMORY_LIST_INFORMATION));
                IntPtr pMem = Marshal.AllocHGlobal(structSize);
                try
                {
                    int status = NtQuerySystemInformation(SystemMemoryListInformation, pMem, structSize, out _);
                    if (status == 0)
                    {
                        SYSTEM_MEMORY_LIST_INFORMATION sysMem = Marshal.PtrToStructure<SYSTEM_MEMORY_LIST_INFORMATION>(pMem);
                        ulong standbyPages = 0;
                        if (sysMem.StandbyPageCount != null)
                        {
                            foreach (var count in sysMem.StandbyPageCount)
                            {
                                standbyPages += count;
                            }
                        }
                        info.StandbyMemoryBytes = standbyPages * 4096;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pMem);
                }
            }
            catch
            {
                info.StandbyMemoryBytes = 0;
            }

            if (info.AvailableMemoryBytes >= info.StandbyMemoryBytes)
            {
                info.FreeMemoryBytes = info.AvailableMemoryBytes - info.StandbyMemoryBytes;
            }
            else
            {
                info.FreeMemoryBytes = info.AvailableMemoryBytes;
            }

            return info;
        }

        public static bool EnablePrivilege(string privilegeName)
        {
            try
            {
                IntPtr hToken;
                if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
                    return false;

                LUID luid;
                if (!LookupPrivilegeValue(null, privilegeName, out luid))
                    return false;

                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                };

                return AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        public static void EmptyWorkingSets()
        {
            EnablePrivilege(SE_PROFILE_SINGLE_PROCESS_NAME);
            EnablePrivilege(SE_INCREASE_QUOTA_NAME);

            int cmd = MemoryEmptyWorkingSets;
            NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));

            TrimUserProcessesWorkingSets();
        }

        public static void EmptyStandbyList()
        {
            EnablePrivilege(SE_PROFILE_SINGLE_PROCESS_NAME);

            int command = MemoryPurgeStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            
            int cmdLow = MemoryPurgeLowPriorityStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, ref cmdLow, sizeof(int));
        }

        public static void PurgeAll()
        {
            EnablePrivilege(SE_PROFILE_SINGLE_PROCESS_NAME);
            EnablePrivilege(SE_INCREASE_QUOTA_NAME);

            int cmdSb = MemoryPurgeStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, ref cmdSb, sizeof(int));
            int cmdSbLow = MemoryPurgeLowPriorityStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, ref cmdSbLow, sizeof(int));

            int cmdMod = MemoryFlushModifiedList;
            NtSetSystemInformation(SystemMemoryListInformation, ref cmdMod, sizeof(int));

            int cmdWs = MemoryEmptyWorkingSets;
            NtSetSystemInformation(SystemMemoryListInformation, ref cmdWs, sizeof(int));
            TrimUserProcessesWorkingSets();
        }

        private static void TrimUserProcessesWorkingSets()
        {
            try
            {
                Process[] processes = Process.GetProcesses();
                IntPtr negOne = new IntPtr(-1);

                foreach (Process proc in processes)
                {
                    try
                    {
                        if (proc.Id <= 4) continue;
                        if (ExcludedProcesses.Contains(proc.ProcessName)) continue;

                        IntPtr hProc = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, false, proc.Id);
                        if (hProc != IntPtr.Zero)
                        {
                            SetProcessWorkingSetSize(hProc, negOne, negOne);
                            CloseHandle(hProc);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch
            {
            }
        }
    }
}
