using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HS_Reconnector.Native
{
    internal static class Win32
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int ASFW_ANY = -1;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_CREATE_THREAD = 0x0002;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint WAIT_OBJECT_0 = 0x00000000;

        public static readonly int CurrentSessionId = GetCurrentSessionId();

        private static int GetCurrentSessionId()
        {
            using (var current = System.Diagnostics.Process.GetCurrentProcess())
                return current.SessionId;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, UIntPtr dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        public static bool TryGetFileIdentity(SafeFileHandle handle, out uint volumeSerial, out ulong fileIndex, out long lastWriteTicks)
        {
            volumeSerial = 0;
            fileIndex = 0;
            lastWriteTicks = 0;

            if (handle == null || handle.IsInvalid || handle.IsClosed)
                return false;

            if (!GetFileInformationByHandle(handle, out var info))
                return false;

            volumeSerial = info.VolumeSerialNumber;
            fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            lastWriteTicks = ((long)(uint)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
            return true;
        }

        public static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static void BringWindowToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);

            var foregroundHwnd = GetForegroundWindow();
            if (foregroundHwnd == hwnd)
                return;

            uint dummy;
            var foregroundThreadId = foregroundHwnd != IntPtr.Zero
                ? GetWindowThreadProcessId(foregroundHwnd, out dummy)
                : 0;
            var currentThreadId = GetCurrentThreadId();

            var attached = false;
            try
            {
                if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                    attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);

                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }

        public static int GetForegroundProcessId()
        {
            var hwnd = GetForegroundWindow();
            return hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out var processId) != 0
                ? (int)processId
                : 0;
        }

        public static string TryGetProcessPath(int processId)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                if (handle == IntPtr.Zero)
                    return null;

                var buffer = new StringBuilder(1024);
                var size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }
        }

        public static bool IsProcess64Bit(int processId)
        {
            if (!Environment.Is64BitOperatingSystem)
                return false;

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                if (handle == IntPtr.Zero)
                    return false;

                return IsWow64Process(handle, out var isWow64) && !isWow64;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }
        }

        public static bool InjectDll(int processId, string dllPath, out string error)
        {
            error = string.Empty;
            if (processId <= 0 || string.IsNullOrWhiteSpace(dllPath))
            {
                error = "Invalid target PID or DLL path.";
                return false;
            }

            IntPtr processHandle = IntPtr.Zero;
            IntPtr remoteMemory = IntPtr.Zero;
            IntPtr remoteThread = IntPtr.Zero;
            var canFreeRemoteMemory = true;

            try
            {
                var access = PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ;
                processHandle = OpenProcess(access, false, processId);
                if (processHandle == IntPtr.Zero)
                {
                    error = "OpenProcess failed (Win32 error " + Marshal.GetLastWin32Error() + ").";
                    return false;
                }

                var pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
                var allocSize = new UIntPtr((uint)pathBytes.Length);
                remoteMemory = VirtualAllocEx(processHandle, IntPtr.Zero, allocSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (remoteMemory == IntPtr.Zero)
                {
                    error = "VirtualAllocEx failed (Win32 error " + Marshal.GetLastWin32Error() + ").";
                    return false;
                }

                if (!WriteProcessMemory(processHandle, remoteMemory, pathBytes, allocSize, out var bytesWritten)
                    || bytesWritten.ToUInt64() != allocSize.ToUInt64())
                {
                    error = "WriteProcessMemory failed (Win32 error " + Marshal.GetLastWin32Error() + ").";
                    return false;
                }

                var kernel32 = GetModuleHandle("kernel32.dll");
                var loadLibraryW = GetProcAddress(kernel32, "LoadLibraryW");
                if (loadLibraryW == IntPtr.Zero)
                {
                    error = "Could not resolve LoadLibraryW.";
                    return false;
                }

                remoteThread = CreateRemoteThread(processHandle, IntPtr.Zero, UIntPtr.Zero, loadLibraryW, remoteMemory, 0, out _);
                if (remoteThread == IntPtr.Zero)
                {
                    error = "CreateRemoteThread failed (Win32 error " + Marshal.GetLastWin32Error() + ").";
                    return false;
                }

                // Remote thread now owns remoteMemory until it completes.
                canFreeRemoteMemory = false;

                var waitResult = WaitForSingleObject(remoteThread, 5000);
                if (waitResult != WAIT_OBJECT_0)
                {
                    error = "Remote LoadLibraryW timed out (wait=0x" + waitResult.ToString("X") + ").";
                    return false;
                }

                canFreeRemoteMemory = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (remoteThread != IntPtr.Zero)
                    CloseHandle(remoteThread);
                if (canFreeRemoteMemory && remoteMemory != IntPtr.Zero && processHandle != IntPtr.Zero)
                    VirtualFreeEx(processHandle, remoteMemory, UIntPtr.Zero, MEM_RELEASE);
                if (processHandle != IntPtr.Zero)
                    CloseHandle(processHandle);
            }
        }
    }
}
