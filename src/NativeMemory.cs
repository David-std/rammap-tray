using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

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

    public static unsafe class NativeMemory
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
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_MEMORY_LIST_INFORMATION
        {
            public ulong ZeroPageCount;
            public ulong FreePageCount;
            public ulong ModifiedPageCount;
            public ulong ModifiedNoWritePageCount;
            public ulong BadPageCount;
            public fixed ulong StandbyPageCount[8];
        }

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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSetSystemInformation(int SystemInformationClass, void* SystemInformation, int SystemInformationLength);

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtQuerySystemInformation(int SystemInformationClass, void* SystemInformation, int SystemInformationLength, out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, int BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x0002;
        private const string SE_PROFILE_SINGLE_PROCESS_NAME = "SeProfileSingleProcessPrivilege";
        private const string SE_INCREASE_QUOTA_NAME = "SeIncreaseQuotaPrivilege";

        private const int SystemMemoryListInformation = 80;
        private const int MemoryEmptyWorkingSets = 2;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 8;

        private static bool _privilegesAcquired = false;

        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static void EnsurePrivileges()
        {
            if (_privilegesAcquired) return;
            EnablePrivilege(SE_PROFILE_SINGLE_PROCESS_NAME);
            EnablePrivilege(SE_INCREASE_QUOTA_NAME);
            _privilegesAcquired = true;
        }

        private static bool EnablePrivilege(string privilegeName)
        {
            try
            {
                if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hToken))
                    return false;

                try
                {
                    if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
                        return false;

                    TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = SE_PRIVILEGE_ENABLED
                    };

                    return AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
                finally
                {
                    CloseHandle(hToken);
                }
            }
            catch
            {
                return false;
            }
        }

        public static MemoryStatusInfo GetMemoryStatus()
        {
            MemoryStatusInfo info = default;
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };

            if (GlobalMemoryStatusEx(ref memStatus))
            {
                info.TotalMemoryBytes = memStatus.ullTotalPhys;
                info.AvailableMemoryBytes = memStatus.ullAvailPhys;
                info.UsedMemoryBytes = info.TotalMemoryBytes - info.AvailableMemoryBytes;
                info.MemoryLoadPercentage = memStatus.dwMemoryLoad;
            }

            SYSTEM_MEMORY_LIST_INFORMATION sysMem;
            if (NtQuerySystemInformation(SystemMemoryListInformation, &sysMem, sizeof(SYSTEM_MEMORY_LIST_INFORMATION), out _) == 0)
            {
                ulong standbyPages = 0;
                for (int i = 0; i < 8; i++)
                {
                    standbyPages += sysMem.StandbyPageCount[i];
                }
                info.StandbyMemoryBytes = standbyPages * 4096;
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

        public static void EmptyWorkingSets()
        {
            EnsurePrivileges();

            int cmd = MemoryEmptyWorkingSets;
            NtSetSystemInformation(SystemMemoryListInformation, &cmd, sizeof(int));
        }

        public static void EmptyStandbyList()
        {
            EnsurePrivileges();

            int command = MemoryPurgeStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, &command, sizeof(int));

            int cmdLow = MemoryPurgeLowPriorityStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, &cmdLow, sizeof(int));
        }

        public static void PurgeAll()
        {
            EnsurePrivileges();

            int cmdSb = MemoryPurgeStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, &cmdSb, sizeof(int));

            int cmdSbLow = MemoryPurgeLowPriorityStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, &cmdSbLow, sizeof(int));

            int cmdWs = MemoryEmptyWorkingSets;
            NtSetSystemInformation(SystemMemoryListInformation, &cmdWs, sizeof(int));
        }
    }
}
