using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CodexUsagePetWatcher
{
    internal static class Program
    {
        private const string MutexName = "Local\\CodexUsagePetWatcher.Singleton.v1";
        private const string StopEventName = "Local\\CodexUsagePetWatcher.Stop.v1";
        private const string CodexPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
        private const int PollMilliseconds = 1500;
        private const int MissingPollsBeforeQuit = 2;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorInsufficientBuffer = 122;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process,
            int flags,
            StringBuilder executableName,
            ref int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackageFamilyName(
            IntPtr process,
            ref uint packageFamilyNameLength,
            StringBuilder packageFamilyName);

        [STAThread]
        public static int Main(string[] args)
        {
            if (HasArgument(args, "--stop"))
            {
                SignalExistingWatcher();
                return 0;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                    return 0;

                using (var stopEvent = new EventWaitHandle(
                    false,
                    EventResetMode.ManualReset,
                    StopEventName))
                {
                    Watch(stopEvent);
                }
            }

            return 0;
        }

        private static bool HasArgument(string[] args, string expected)
        {
            if (args == null)
                return false;

            foreach (string argument in args)
            {
                if (string.Equals(
                    (argument ?? string.Empty).Trim(),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void SignalExistingWatcher()
        {
            try
            {
                using (EventWaitHandle stopEvent =
                    EventWaitHandle.OpenExisting(StopEventName))
                {
                    stopEvent.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Stopping an already stopped watcher is intentionally harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // A per-user autostart normally shares the same access token.
            }
        }

        private static void Watch(EventWaitHandle stopEvent)
        {
            bool codexWasRunning = IsCodexDesktopRunning();
            int missingPolls = 0;

            if (codexWasRunning)
                ControlPet("--show");

            while (!stopEvent.WaitOne(PollMilliseconds))
            {
                bool codexIsRunning = IsCodexDesktopRunning();
                if (codexIsRunning)
                {
                    missingPolls = 0;
                    if (!codexWasRunning)
                    {
                        codexWasRunning = true;
                        ControlPet("--show");
                    }
                }
                else if (codexWasRunning)
                {
                    missingPolls++;
                    if (missingPolls >= MissingPollsBeforeQuit)
                    {
                        missingPolls = 0;
                        codexWasRunning = false;
                        ControlPet("--quit");
                    }
                }
            }
        }

        private static void ControlPet(string command)
        {
            try
            {
                string executable = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "CodexUsagePet.exe");
                if (!File.Exists(executable))
                    return;

                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = command,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };

                Process process = Process.Start(startInfo);
                if (process != null)
                    process.Dispose();
            }
            catch
            {
                // The next state edge or watcher restart can retry safely.
            }
        }

        private static bool IsCodexDesktopRunning()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                return false;
            }

            bool found = false;
            foreach (Process process in processes)
            {
                using (process)
                {
                    if (found || !IsDesktopProcessName(SafeProcessName(process)))
                        continue;

                    IntPtr handle = IntPtr.Zero;
                    try
                    {
                        handle = OpenProcess(
                            ProcessQueryLimitedInformation,
                            false,
                            process.Id);
                        if (handle == IntPtr.Zero)
                            continue;

                        string imagePath = QueryImagePath(handle);
                        if (IsExplicitlyExcludedPath(imagePath))
                            continue;

                        string packageFamily = QueryPackageFamily(handle);
                        bool exactCodexPackage = string.Equals(
                            packageFamily,
                            CodexPackageFamily,
                            StringComparison.OrdinalIgnoreCase);
                        bool installedCodexPath = IsInstalledCodexPath(imagePath);

                        if (exactCodexPackage && installedCodexPath)
                        {
                            found = true;
                            continue;
                        }

                        // Package identity remains authoritative when Windows denies
                        // image-path inspection. ChatGPT.exe is the packaged desktop
                        // shell; Codex CLI/app-server processes use codex.exe instead.
                        if (exactCodexPackage &&
                            string.Equals(
                                SafeProcessName(process),
                                "ChatGPT",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            continue;
                        }

                        // Window metadata is only a fallback for the exact Codex
                        // package. It can never promote an unpackaged local CLI.
                        if (exactCodexPackage && HasCodexMainWindow(process))
                            found = true;
                    }
                    catch
                    {
                        // Processes can exit while being inspected.
                    }
                    finally
                    {
                        if (handle != IntPtr.Zero)
                            CloseHandle(handle);
                    }
                }
            }

            return found;
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsDesktopProcessName(string processName)
        {
            return string.Equals(
                       processName,
                       "ChatGPT",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       processName,
                       "Codex",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string QueryImagePath(IntPtr process)
        {
            try
            {
                int capacity = 32768;
                var path = new StringBuilder(capacity);
                if (QueryFullProcessImageName(process, 0, path, ref capacity))
                    return path.ToString();
            }
            catch (EntryPointNotFoundException)
            {
            }

            return string.Empty;
        }

        private static string QueryPackageFamily(IntPtr process)
        {
            try
            {
                uint length = 0;
                int result = GetPackageFamilyName(process, ref length, null);
                if (result != ErrorInsufficientBuffer || length == 0)
                    return string.Empty;

                var family = new StringBuilder((int)length);
                result = GetPackageFamilyName(process, ref length, family);
                return result == 0 ? family.ToString() : string.Empty;
            }
            catch (EntryPointNotFoundException)
            {
                return string.Empty;
            }
        }

        private static bool IsInstalledCodexPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            string normalized = path.Replace('/', '\\');
            return normalized.IndexOf(
                       "\\Program Files\\WindowsApps\\OpenAI.Codex_",
                       StringComparison.OrdinalIgnoreCase) >= 0 &&
                   normalized.IndexOf(
                       "\\app\\resources\\",
                       StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool IsExplicitlyExcludedPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            string normalized = path.Replace('/', '\\');
            return normalized.IndexOf(
                       "\\app\\resources\\",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf(
                       "\\.codex\\plugins\\.plugin-appserver\\",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.EndsWith(
                       "\\CodexUsagePet.exe",
                       StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(
                       "\\CodexUsagePetWatcher.exe",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasCodexMainWindow(Process process)
        {
            try
            {
                process.Refresh();
                if (process.MainWindowHandle == IntPtr.Zero)
                    return false;

                string title = process.MainWindowTitle ?? string.Empty;
                return title.IndexOf(
                           "Codex",
                           StringComparison.OrdinalIgnoreCase) >= 0 ||
                       title.IndexOf(
                           "ChatGPT",
                           StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
