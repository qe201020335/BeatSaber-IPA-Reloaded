using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IPA.ConsoleHost
{
    /// <summary>
    /// A console-subsystem process that owns the log console for the game.
    /// </summary>
    /// <remarks>
    /// A GUI process calling <c>AllocConsole</c> always gets a conhost window, because conhost only hands off
    /// to the default terminal (e.g. Windows Terminal) for console applications. Windows creates this process'
    /// console the same way as for any console app, so it follows the default terminal setting. The game then
    /// attaches to it with <c>AttachConsole</c>. This process stays alive until the game exits, so the
    /// terminal keeps the console open.
    /// </remarks>
    internal static class Program
    {
        private delegate bool ConsoleCtrlDelegate(uint ctrlType);

        private const uint CtrlCEvent = 0;
        private const uint CtrlBreakEvent = 1;

        // Kept in a field so the delegate isn't collected while registered.
        private static readonly ConsoleCtrlDelegate CtrlHandler = HandleCtrl;

        private static int Main(string[] args)
        {
            // Ctrl+C and Ctrl+Break are meant for the game. If they killed this process, the terminal would treat
            // the console as finished. Closing the console still terminates us (and the game) as usual.
            _ = SetConsoleCtrlHandler(CtrlHandler, true);

            if (args.Length < 1 || !int.TryParse(args[0], out var processId))
            {
                return 1;
            }

            try
            {
                using var target = Process.GetProcessById(processId);
                Console.Title = target.ProcessName;
                target.WaitForExit();
                return 0;
            }
            catch (ArgumentException)
            {
                // The target already exited.
                return 0;
            }
        }

        private static bool HandleCtrl(uint ctrlType) => ctrlType is CtrlCEvent or CtrlBreakEvent;

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);
    }
}
