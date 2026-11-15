using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IPA.Logging
{
    // https://stackoverflow.com/a/48864902/3117125
    internal static class WinConsole
    {
        internal static TextWriter ConOut;
        internal static TextReader ConIn;

        private static SafeFileHandle outHandle;
        private static SafeFileHandle inHandle;

        public static bool UseVTEscapes { get; private set; } = true;

        internal static IntPtr OutHandle => outHandle.DangerousGetHandle();
        internal static IntPtr InHandle => inHandle.DangerousGetHandle();

        internal static bool IsInitialized;

        public static void Initialize(int processId, bool alwaysCreateNewConsole = false)
        {
            bool consoleAttached;
            if (alwaysCreateNewConsole || !(consoleAttached = AttachConsole(processId)))
            {
                consoleAttached = TryAttachToConsoleHost() || AllocConsole();
            }

            if (consoleAttached)
            {
                InitializeStreams();
                IsInitialized = true;
            }
        }

        /// <summary>
        /// Creates the console through a console-subsystem helper process and attaches to it.
        /// </summary>
        /// <remarks>
        /// conhost only hands a new console off to the default terminal (e.g. Windows Terminal) for console
        /// applications, so <see cref="AllocConsole"/> from the game always opens a conhost window. The helper's
        /// console is created like any console app's, so it respects the default terminal setting.
        /// The helper waits for this process to exit, which keeps the console open until then.
        /// </remarks>
        private static bool TryAttachToConsoleHost()
        {
            if (IsRunningOnWine())
            {
                return false;
            }

            try
            {
                var hostPath = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(typeof(WinConsole).Assembly.Location), "..", "..", "Libs", ConsoleHostFileName));
                if (!File.Exists(hostPath))
                {
                    return false;
                }

                var startupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
                var commandLine = new StringBuilder($"\"{hostPath}\" {GetCurrentProcessId()}");
                if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateNewConsole,
                        IntPtr.Zero, null, ref startupInfo, out var processInfo))
                {
                    return false;
                }

                try
                {
                    // The helper's console is set up (and handed off to the terminal) during its startup, so keep
                    // trying until attaching succeeds or the helper exits.
                    var stopwatch = Stopwatch.StartNew();
                    while (stopwatch.ElapsedMilliseconds < ConsoleHostTimeoutMs)
                    {
                        if (AttachConsole((int)processInfo.dwProcessId))
                        {
                            return true;
                        }

                        if (WaitForSingleObject(processInfo.hProcess, 20) == WaitObject0)
                        {
                            return false;
                        }
                    }

                    _ = TerminateProcess(processInfo.hProcess, 1);
                    return false;
                }
                finally
                {
                    _ = CloseHandle(processInfo.hThread);
                    _ = CloseHandle(processInfo.hProcess);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRunningOnWine()
        {
            var ntdll = GetModuleHandle("ntdll.dll");
            return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
        }

        private static void InitializeStreams()
        {
            InitializeOutStream();
            InitializeInStream();
        }

        private static void InitializeOutStream()
        {
            // Get/SetConsoleMode and GetConsoleScreenBufferInfo need read access, writing needs write access.
            var file = new SafeFileHandle(CreateFile("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero), true);
            if (!file.IsInvalid)
            {
                outHandle = file;
                var writer = new ConsoleWriter(OutHandle);
                ConOut = writer;
                Console.SetOut(writer);
                Console.SetError(writer);

                // Use our own CONOUT$ handle: AttachConsole keeps the process' existing std handles, so the
                // stdout handle isn't necessarily the console.
                if (GetConsoleMode(OutHandle, out var mode))
                {
                    mode |= EnableVTProcessing;
                    if (!SetConsoleMode(OutHandle, mode))
                    {
                        UseVTEscapes = false;
                        Console.Error.WriteLine("Could not enable VT100 escape code processing (maybe you're running an old Windows?): " +
                            new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    }
                }
                else
                {
                    UseVTEscapes = false;
                    Console.Error.WriteLine("Could not enable VT100 escape code processing (maybe you're running an old Windows?): " +
                        new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
            }
        }

        /// <summary>
        /// Writes UTF-16 text with <c>WriteConsoleW</c>, so the output doesn't depend on the console's code page.
        /// </summary>
        private sealed class ConsoleWriter : TextWriter
        {
            private readonly IntPtr handle;

            public ConsoleWriter(IntPtr handle) => this.handle = handle;

            public override Encoding Encoding => Encoding.Unicode;

            public override void Write(char value) => Write(value.ToString());

            public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }

                while (value.Length > 0)
                {
                    if (!WriteConsole(handle, value, (uint)value.Length, out var written, IntPtr.Zero) || written == 0)
                    {
                        return;
                    }

                    value = value.Substring((int)written);
                }
            }
        }

        private static void InitializeInStream()
        {
            var fs = CreateFileStream("CONIN$", GenericRead, FileShareRead, FileAccess.Read, out inHandle);
            if (fs != null)
            {
                Console.SetIn(ConIn = new StreamReader(fs));
            }
        }

        private static FileStream CreateFileStream(string name, uint win32DesiredAccess, uint win32ShareMode,
                                FileAccess dotNetFileAccess, out SafeFileHandle handle)
        {
            var file = new SafeFileHandle(CreateFile(name, win32DesiredAccess, win32ShareMode, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero), true);
            if (!file.IsInvalid)
            {
                handle = file;
                var fs = new FileStream(file, dotNetFileAccess);
                return fs;
            }

            handle = null;
            return null;
        }

        #region Win API Functions and Constants

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleW", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool WriteConsole(IntPtr hConsoleOutput, string lpBuffer, uint nNumberOfCharsToWrite,
            out uint lpNumberOfCharsWritten, IntPtr lpReserved);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CreateProcess(string lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory, ref StartupInfo lpStartupInfo, out ProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        private const string ConsoleHostFileName = "IPA.ConsoleHost.exe";
        private const int ConsoleHostTimeoutMs = 5000;
        private const uint CreateNewConsole = 0x00000010;
        private const uint WaitObject0 = 0x00000000;

        private const uint EnableVTProcessing = 0x0004;

        private const uint GenericWrite = 0x40000000;
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 0x00000003;
        private const uint FileAttributeNormal = 0x80;

        internal const int AttachParent = -1;

        #endregion
    }
}