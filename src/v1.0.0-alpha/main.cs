using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;
using System.Drawing;

namespace ProcRipper
{
    class Program
    {
        private const string GAME_CONFIG_FILE = "GAME_PRIORITY.GCFG";
        private const string SYSTEM_CONFIG_FILE = "PROC_PRIORITY.GCFG";
        private const int CHECK_INTERVAL = 2000;
        private static readonly HashSet<int> _processedProcesses = new HashSet<int>();
        private static bool _gameOptimized = false;
        private static int _gameProcessId = -1;

        private static readonly Dictionary<int, string> PriorityMap = 
            new Dictionary<int, string>
            {
                { 15, "TIME_CRITICAL" },
                { 2, "HIGHEST" },
                { 1, "ABOVE_NORMAL" },
                { 0, "NORMAL" },
                { -1, "BELOW_NORMAL" },
                { -2, "LOWEST" },
                { -15, "IDLE" }
            };

        private static readonly Dictionary<string, Dictionary<string, ThreadConfig>> _gameConfigs = 
            new Dictionary<string, Dictionary<string, ThreadConfig>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Dictionary<string, ThreadConfig>> _systemConfigs = 
            new Dictionary<string, Dictionary<string, ThreadConfig>>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> CriticalProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csrss", "winlogon", "smss", "wininit", "services", "lsass"
        };

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessPriorityBoost(IntPtr hProcess, bool bDisablePriorityBoost);
        
        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
        private const uint THREAD_SET_INFORMATION = 0x0020;
        private const uint THREAD_QUERY_INFORMATION = 0x0040;
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int GetThreadDescription(IntPtr hThread, out IntPtr ppszThreadDescription);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);

        private const uint PROCESS_SET_INFORMATION = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public LUID_AND_ATTRIBUTES[] Privileges;
        }

        const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hWnd);

        class ThreadConfig
        {
            public int Priority { get; set; }
            public string Affinity { get; set; } = "ALL";
            public bool DisableBoost { get; set; } = false;
        }

        private static NotifyIcon _notifyIcon;

        [STAThread]
        static void Main(string[] args)
        {
            ShowConsole();
            ShowBanner();
            SetupTrayIcon();
            LoadConfigurations();

            if (_gameConfigs.Count == 0 && _systemConfigs.Count == 0)
            {
                WriteColored("No processes configured. Exiting.", ConsoleColor.Red);
                return;
            }

            WriteColored("PROC RIPPER Started", ConsoleColor.Green);
            WriteColored("===================", ConsoleColor.Green);
            WriteColored($"Monitoring game processes: {string.Join(", ", _gameConfigs.Keys)}", ConsoleColor.Cyan);
            WriteColored($"System processes will activate after first game launch.", ConsoleColor.Cyan);
            WriteColored("Hiding console to system tray in 3 seconds...", ConsoleColor.Yellow);
            
            for (int i = 3; i > 0; i--)
            {
                WriteColored($"{i}...", ConsoleColor.Yellow);
                Thread.Sleep(1000);
            }
            
            MinimizeToTray();

            Thread monitoringThread = new Thread(MonitoringLoop);
            monitoringThread.IsBackground = true;
            monitoringThread.Start();

            Application.Run();
        }

        static void ShowConsole()
        {
            var consoleHandle = GetConsoleWindow();
            ShowWindow(consoleHandle, SW_SHOW);
        }

        static void SetupTrayIcon()
        {
            Application.EnableVisualStyles();
            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Visible = true,
                Text = "ProcRipper - Running"
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            
            ToolStripMenuItem showHideItem = new ToolStripMenuItem("Show/Hide Console");
            showHideItem.Click += (s, e) => ToggleConsole();
            menu.Items.Add(showHideItem);
            
            menu.Items.Add(new ToolStripSeparator());
            
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => 
            {
                _notifyIcon.Visible = false;
                Application.Exit();
                Environment.Exit(0);
            };
            menu.Items.Add(exitItem);
            
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (s, e) => ToggleConsole();
            _notifyIcon.ShowBalloonTip(3000, "ProcRipper", "Monitoring started successfully", ToolTipIcon.Info);
        }

        static void ToggleConsole()
        {
            IntPtr handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
            {
                if (IsWindowVisible(handle))
                {
                    ShowWindow(handle, SW_HIDE);
                    _notifyIcon.Text = "ProcRipper - Hidden";
                }
                else
                {
                    ShowWindow(handle, SW_SHOW);
                    BringWindowToTop(handle);
                    _notifyIcon.Text = "ProcRipper - Running";
                }
            }
        }

        static void ShowBanner()
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine(@"
██████╗ ██████╗  ██████╗ ██████╗██████╗ ██╗██████╗ ██████╗ ███████╗██████╗ 
██╔══██╗██╔══██╗██╔═══██╗██╔════╝██╔══██╗██║██╔══██╗██╔══██╗██╔════╝██╔══██╗
██████╔╝██████╔╝██║   ██║██║     ██████╔╝██║██████╔╝██████╔╝█████╗  ██████╔╝
██╔═══╝ ██╔══██╗██║   ██║██║     ██╔═══╝ ██║██╔═══╝ ██╔══██╗██╔══╝  ██╔══██╗
██║     ██║  ██║╚██████╔╝╚██████╗██║     ██║██║     ██║  ██║███████╗██║  ██║
╚═╝     ╚═╝  ╚═╝ ╚═════╝  ╚═════╝╚═╝     ╚═╝╚═╝     ╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝
");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("               Process Thread Optimizer v2.0");
            Console.ResetColor();
            Console.WriteLine();
        }

        private static void MonitoringLoop()
        {
            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (!isAdmin)
            {
                WriteColored("WARNING: Not running as administrator. Access to system processes may be limited.", ConsoleColor.Yellow);
            }
            else
            {
                if (EnablePrivilege("SeDebugPrivilege"))
                {
                    WriteColored("SeDebugPrivilege enabled successfully.", ConsoleColor.Green);
                }
                else
                {
                    WriteColored($"WARNING: Failed to enable SeDebugPrivilege. Error: {Marshal.GetLastWin32Error()}", ConsoleColor.Yellow);
                }
            }

            while (true)
            {
                try
                {
                    CheckForConfiguredProcesses();
                    Thread.Sleep(CHECK_INTERVAL);
                }
                catch (Exception ex)
                {
                    WriteColored($"Error in monitoring loop: {ex.Message}", ConsoleColor.Red);
                }
            }
        }

        private static bool EnablePrivilege(string privilegeName)
        {
            IntPtr token = IntPtr.Zero;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                return false;

            LUID luid;
            if (!LookupPrivilegeValue(null, privilegeName, out luid))
            {
                CloseHandle(token);
                return false;
            }

            TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
            tp.PrivilegeCount = 1;
            tp.Privileges = new LUID_AND_ATTRIBUTES[1];
            tp.Privileges[0].Luid = luid;
            tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;

            bool result = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            CloseHandle(token);
            return result;
        }

        static void WriteColored(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        static void LoadConfigurations()
        {
            LoadConfigFile(GAME_CONFIG_FILE, _gameConfigs, true);
            LoadConfigFile(SYSTEM_CONFIG_FILE, _systemConfigs, false);
        }

        static void LoadConfigFile(string fileName, Dictionary<string, Dictionary<string, ThreadConfig>> configs, bool isGame)
        {
            try
            {
                if (!File.Exists(fileName))
                {
                    WriteColored($"WARNING: Configuration file {fileName} not found.", ConsoleColor.Yellow);
                    WriteColored("Creating default configuration file...", ConsoleColor.Cyan);
                    CreateDefaultConfigFile(fileName, isGame);
                    return;
                }

                string[] lines = File.ReadAllLines(fileName);
                string currentProcess = null;
                Dictionary<string, ThreadConfig> currentThreads = null;

                foreach (string line in lines)
                {
                    string trimmed = line.Trim();

                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                        continue;

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentProcess = trimmed.Substring(1, trimmed.Length - 2).Trim().Replace(".exe", string.Empty);
                        currentThreads = new Dictionary<string, ThreadConfig>(StringComparer.OrdinalIgnoreCase);
                        configs[currentProcess] = currentThreads;
                        continue;
                    }

                    if (currentProcess == null)
                        continue;

                    string[] parts = trimmed.Split('=');
                    if (parts.Length == 2)
                    {
                        string threadName = parts[0].Trim();
                        string configStr = parts[1].Trim();
                        string[] values = configStr.Split(',');
                        
                        if (values.Length >= 1 && int.TryParse(values[0].Trim(), out int priority))
                        {
                            var config = new ThreadConfig { Priority = priority };

                            if (values.Length >= 2)
                            {
                                List<string> affinityParts = new List<string>();
                                for (int i = 1; i < values.Length; i++)
                                {
                                    string val = values[i].Trim();
                                    if (val.Equals("true", StringComparison.OrdinalIgnoreCase) || 
                                        val.Equals("false", StringComparison.OrdinalIgnoreCase))
                                    {
                                        break;
                                    }
                                    affinityParts.Add(val);
                                }
                                config.Affinity = string.Join(",", affinityParts).ToUpper();
                            }

                            for (int i = 1; i < values.Length; i++)
                            {
                                string val = values[i].Trim();
                                if (bool.TryParse(val, out bool disableBoost))
                                {
                                    config.DisableBoost = disableBoost;
                                    break;
                                }
                            }

                            if (PriorityMap.ContainsKey(priority))
                            {
                                currentThreads[threadName] = config;
                                WriteColored($"Loaded: [{currentProcess}] {threadName} -> {PriorityMap[priority]} (Affinity: {config.Affinity}, Boost: {config.DisableBoost})", ConsoleColor.DarkCyan);
                            }
                            else
                            {
                                WriteColored($"[WARNING] Invalid priority value {priority} for thread {threadName}", ConsoleColor.Yellow);
                            }
                        }
                        else
                        {
                            WriteColored($"[WARNING] Cannot parse priority value '{parts[1]}' for thread {threadName}", ConsoleColor.Yellow);
                        }
                    }
                    else
                    {
                        WriteColored($"[WARNING] Invalid line format: {line}", ConsoleColor.Yellow);
                    }
                }

                WriteColored($"\n✓ LOADED {configs.Count} PROCESS CONFIGURATIONS FROM {fileName}", ConsoleColor.Green);
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                WriteColored($"[ERROR] Failed to load configuration file: {ex.Message}", ConsoleColor.Red);
            }
        }

        static void CreateDefaultConfigFile(string fileName, bool isGame)
        {
            try
            {
             var defaultConfig = new List<string>
            {
                "# Fortnite Thread Priority Configuration",
                "# Format: THREAD_NAME=PRIORITY,AFFINITY,DISABLE_BOOST",
                "# Available priorities:",
                "# 15=TIME_CRITICAL, 2=HIGHEST, 1=ABOVE_NORMAL, 0=NORMAL, -1=BELOW_NORMAL, -2=LOWEST, -15=IDLE",
                "#",
                "# Affinity options:",
                "# ALL = Use all CPU cores",
                "# 0,2,4,6 = Use only even cores (for 8-core CPU)",
                "# 0-3 = Use first 4 cores",
                "# 0,1 = Use first 2 cores only",
                "#",
                "# DisableBoost: true/false - Disables priority boosting for more consistent performance",
                "# true = NO boosting (recommended for gaming)",
                "# false = YES boosting (Windows default)",
                "",
                "#MODIFIED Beyond Fortnite Thread Template For Controller Player",
                "[FortniteClient-Win64-Shipping.exe]",
                "# CRITICAL PATH - Maximum priority",
                "RenderThread 0=15,0,2,4,6,true",
                "RHIThread=15,0,2,4,6,true",
                "GameThread=15,1,3,5,7,true",
                "",
                "# HIGH PRIORITY - Prevents issues",
                "AudioMixerRenderThread(2)=2,1,3,5,7,true",
                "",
                "# NORMAL PRIORITY - Important but not critical",  
                "RtcNetworkThread=0,1,3,5,7,true",
                "RtcWorkerThread=0,1,3,5,7,true",
                "",
                "# LOW PRIORITY - Background tasks",
                "Background Worker #2=-1,1,3,5,7,true",
                "Background Worker #1=-1,1,3,5,7,true",
                "Background Worker #3=-1,1,3,5,7,true",
                "Background Worker #7=-1,1,3,5,7,true",
                "Background Worker #4=-1,1,3,5,7,true",
                "Background Worker #5=-1,1,3,5,7,true",
                "Background Worker #6=-1,1,3,5,7,true",
                "Background Worker #0=-1,1,3,5,7,true",
                "FAsyncLoadingThread=-15,1,3,5,7,true",
                "WindowsRawInputThread=-2,1,3,5,7,true", 
                "# Why Input Thread is lowest? https://x.com/BEYONDPERF_LLG/status/1993263329993724067"
            };

                File.WriteAllLines(fileName, defaultConfig);
                WriteColored($"✓ Created default configuration file: {fileName}", ConsoleColor.Green);
                WriteColored("⚠ Please edit this file with actual thread names and restart the application.", ConsoleColor.Cyan);
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                WriteColored($"[ERROR] Failed to create default config file: {ex.Message}", ConsoleColor.Red);
            }
        }

        static void CheckForConfiguredProcesses()
        {
            bool processedGameThisLoop = CheckProcesses(_gameConfigs, true);

            if (processedGameThisLoop && !_gameOptimized)
            {
                WriteColored("Game optimization complete! Hiding console in 3 seconds...", ConsoleColor.Yellow);
                
                for (int i = 3; i > 0; i--)
                {
                    WriteColored($"{i}...", ConsoleColor.Yellow);
                    Thread.Sleep(1000);
                }
                
                _gameOptimized = true;
                MinimizeToTray();
            }

            if (_gameOptimized)
            {
                CheckProcesses(_systemConfigs, false);
            }
        }

        static void MinimizeToTray()
        {
            IntPtr handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_HIDE);
                _notifyIcon.Text = "ProcRipper - Running in Background";
                _notifyIcon.ShowBalloonTip(2000, "ProcRipper", "Now running in background. Double-click tray icon to show console.", ToolTipIcon.Info);
            }
        }

        static bool CheckProcesses(Dictionary<string, Dictionary<string, ThreadConfig>> configs, bool isGame)
        {
            bool anyProcessed = false;

            foreach (string processName in configs.Keys)
            {
                Process[] processes = Process.GetProcessesByName(processName);
                
                if (processes.Length == 0)
                {
                    continue;
                }

                foreach (Process process in processes)
                {
                    try
                    {
                        if (_processedProcesses.Contains(process.Id))
                            continue;

                        string type = isGame ? "Game" : "System";
                        WriteColored($"\n{type} process detected: {processName} (PID {process.Id}) at {DateTime.Now:HH:mm:ss}", ConsoleColor.Magenta);
                        
                        if (!isGame && CriticalProcesses.Contains(processName))
                        {
                            WriteColored($"WARNING: Modifying critical system process '{processName}'. This may cause instability. Proceed with caution.", ConsoleColor.Yellow);
                        }

                        if (isGame)
                        {
                            _gameProcessId = process.Id;
                            
                            if (!WaitForGameThreadInitialization(process))
                            {
                                WriteColored("Game process exited during initialization wait. Skipping optimization.", ConsoleColor.Red);
                                continue;
                            }
                        }
                        else
                        {
                            WriteColored("Applying system process optimization...", ConsoleColor.Cyan);
                        }

                        ApplyProcessSettings(process, processName, configs);
                        EnumerateProcessThreads(process, processName, configs);

                        WriteColored("Thread optimization applied.", ConsoleColor.Green);
                        _processedProcesses.Add(process.Id);
                        anyProcessed = true;
                        
                        process.EnableRaisingEvents = true;
                        process.Exited += (sender, e) =>
                        {
                            WriteColored($"\n{type} process {processName} (PID {process.Id}) exited at {DateTime.Now:HH:mm:ss}", ConsoleColor.DarkYellow);
                            _processedProcesses.Remove(process.Id);
                            process.Dispose();
                            if (isGame)
                            {
                                _gameOptimized = false;
                                _gameProcessId = -1;
                            }
                        };
                    }
                    catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
                    {
                        WriteColored($"ERROR: Cannot access {processName} (PID {process.Id}): {ex.Message}", ConsoleColor.Red);
                    }
                }
            }

            return anyProcessed;
        }

        static bool WaitForGameThreadInitialization(Process process)
        {
            const int initialWaitSeconds = 10;
            const int threadCountThreshold = 20;
            const int maxWaitCycles = 12;
            
            WriteColored($"Waiting {initialWaitSeconds} seconds for initial thread initialization...", ConsoleColor.Cyan);
            Thread.Sleep(initialWaitSeconds * 1000);

            int waitCycle = 1;
            
            while (waitCycle <= maxWaitCycles)
            {
                try
                {
                    process.Refresh();
                    int threadCount = process.Threads.Count;
                    
                    WriteColored($"Thread count: {threadCount} (cycle {waitCycle}/{maxWaitCycles})", 
                        threadCount >= threadCountThreshold ? ConsoleColor.Green : ConsoleColor.Yellow);

                    if (threadCount >= threadCountThreshold)
                    {
                        WriteColored($"Thread count reached {threadCount} (threshold: {threadCountThreshold}). Proceeding with optimization.", ConsoleColor.Green);
                        return true;
                    }
                    else
                    {
                        WriteColored($"Thread count {threadCount} is below threshold {threadCountThreshold}. Waiting another 10 seconds...", ConsoleColor.Yellow);
                        Thread.Sleep(10000);
                        waitCycle++;
                    }

                    if (process.HasExited)
                    {
                        WriteColored("Game process exited during thread initialization wait.", ConsoleColor.Red);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    WriteColored($"Error checking thread count: {ex.Message}", ConsoleColor.Red);
                    if (waitCycle >= 3)
                    {
                        WriteColored("Proceeding with optimization despite thread count check issues.", ConsoleColor.Yellow);
                        return true;
                    }
                    Thread.Sleep(10000);
                    waitCycle++;
                }
            }

            WriteColored($"Maximum wait time reached ({maxWaitCycles * 10} seconds). Proceeding with current thread count.", ConsoleColor.Yellow);
            return true;
        }

        static void ApplyProcessSettings(Process process, string processName, Dictionary<string, Dictionary<string, ThreadConfig>> configs)
        {
            try
            {
                if (!configs.TryGetValue(processName, out var threadConfigs))
                    return;

                bool disableBoost = threadConfigs.Values.Any(c => c.DisableBoost);
                
                if (disableBoost)
                {
                    IntPtr processHandle = OpenProcess(PROCESS_SET_INFORMATION, false, (uint)process.Id);
                    if (processHandle == IntPtr.Zero)
                    {
                        int error = Marshal.GetLastWin32Error();
                        WriteColored($"WARNING: Failed to open process handle for boost disable. Error: {error}", ConsoleColor.Yellow);
                        return;
                    }

                    bool success = SetProcessPriorityBoost(processHandle, true);
                    CloseHandle(processHandle);
                    if (success)
                    {
                        WriteColored("Priority boost disabled for process.", ConsoleColor.Green);
                    }
                    else
                    {
                        WriteColored("WARNING: Failed to disable priority boost.", ConsoleColor.Yellow);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteColored($"ERROR: Failed to apply process settings: {ex.Message}", ConsoleColor.Red);
            }
        }

        static void EnumerateProcessThreads(Process process, string processName, Dictionary<string, Dictionary<string, ThreadConfig>> configs)
        {
            try
            {
                if (!configs.TryGetValue(processName, out var threadConfigs))
                    return;

                ProcessThreadCollection threads = process.Threads;
                
                WriteColored($"Total threads found: {threads.Count}", ConsoleColor.Cyan);
                
                int modifiedThreads = 0;
                HashSet<string> processedThreadNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                foreach (ProcessThread thread in threads.Cast<ProcessThread>())
                {
                    try
                    {
                        bool? wasModified = DisplayThreadInfo(thread, process.Id, threadConfigs, processedThreadNames);
                        if (wasModified.HasValue && wasModified.Value)
                            modifiedThreads++;
                    }
                    catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
                    {
                        WriteColored($"{thread.Id} | ACCESS DENIED | N/A | N/A | N/A | N/A", ConsoleColor.Red);
                    }
                }
                
                var missingThreads = threadConfigs.Keys.Except(processedThreadNames, StringComparer.OrdinalIgnoreCase);
                if (missingThreads.Any())
                {
                    WriteColored("Configured threads not found or could not be applied:", ConsoleColor.Yellow);
                    foreach (var missing in missingThreads)
                    {
                        Console.WriteLine(missing);
                    }
                }
                
                WriteColored($"\nSummary: Modified {modifiedThreads} threads", ConsoleColor.Cyan);
                if (modifiedThreads > 0)
                {
                    WriteColored("Optimizations applied.", ConsoleColor.Green);
                }
                else if (threadConfigs.Count > 0)
                {
                    WriteColored("No matching threads found for configuration.", ConsoleColor.Yellow);
                }
            }
            catch (Exception ex)
            {
                WriteColored($"ERROR: Failed to enumerate threads for {processName}: {ex.Message}", ConsoleColor.Red);
            }
        }

        static bool? DisplayThreadInfo(ProcessThread thread, int processId, Dictionary<string, ThreadConfig> threadConfigs, HashSet<string> processedThreadNames)
        {
            int threadId = thread.Id;
            ThreadPriorityLevel currentPriority = thread.PriorityLevel;
            string currentPrioritySymbol = GetPrioritySymbol(currentPriority);
            string threadName = GetThreadName(threadId);

            if (string.IsNullOrEmpty(threadName) || threadName.StartsWith("N/A") || threadName == "NO_NAME" || threadName == "EMPTY")
                return null;

            if (!threadConfigs.TryGetValue(threadName, out ThreadConfig config))
                return null;

            List<string> actions = new List<string>();
            bool wasModified = false;

            ThreadPriorityLevel targetPriority = ConvertToThreadPriorityLevel(config.Priority);
            if (currentPriority != targetPriority)
            {
                try
                {
                    thread.PriorityLevel = targetPriority;
                    actions.Add($"PRIORITY: {currentPrioritySymbol}->{PriorityMap[config.Priority]}");
                    wasModified = true;
                }
                catch (Exception ex)
                {
                    actions.Add($"PRIORITY FAIL: {ex.Message}");
                }
            }

            if (config.Affinity != "ALL")
            {
                try
                {
                    IntPtr targetAffinity = ParseAffinity(config.Affinity);
                    IntPtr threadHandle = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION, false, (uint)threadId);
                    if (threadHandle == IntPtr.Zero)
                    {
                        int error = Marshal.GetLastWin32Error();
                        actions.Add($"AFFINITY FAIL: Failed to open thread handle. Error: {error}");
                    }
                    else
                    {
                        IntPtr prev = SetThreadAffinityMask(threadHandle, targetAffinity);
                        int setError = (prev == IntPtr.Zero) ? Marshal.GetLastWin32Error() : 0;
                        if (prev != IntPtr.Zero)
                        {
                            actions.Add($"AFFINITY: {config.Affinity}");
                            wasModified = true;
                        }
                        else
                        {
                            actions.Add($"AFFINITY FAIL: Failed to set affinity. Error: {setError}");
                        }
                        CloseHandle(threadHandle);
                    }
                }
                catch (Exception ex)
                {
                    actions.Add($"AFFINITY FAIL: {ex.Message}");
                }
            }

            string action = actions.Count > 0 ? string.Join(" | ", actions) : "NO CHANGE";

            string affinityDisplay = config.Affinity;
            int displayPriority = config.Priority;

            ConsoleColor priorityColor = displayPriority switch
            {
                15 => ConsoleColor.Red,
                2 => ConsoleColor.Yellow,
                1 => ConsoleColor.Yellow,
                0 => ConsoleColor.Green,
                -1 => ConsoleColor.Blue,
                -2 => ConsoleColor.DarkBlue,
                -15 => ConsoleColor.DarkGray,
                _ => ConsoleColor.White
            };

            ConsoleColor actionColor = action.Contains("FAIL") ? ConsoleColor.Red :
                                       action == "NO CHANGE" ? ConsoleColor.Gray :
                                       ConsoleColor.Green;

            Console.Write($"{threadId} | ");
            Console.ForegroundColor = priorityColor;
            Console.Write($"{displayPriority} | ");
            Console.ResetColor();
            Console.Write($"{currentPrioritySymbol} | {affinityDisplay} | {threadName} | ");
            Console.ForegroundColor = actionColor;
            Console.WriteLine(action);
            Console.ResetColor();
            
            processedThreadNames.Add(threadName);
            
            return wasModified;
        }

        static IntPtr ParseAffinity(string affinity)
        {
            if (affinity == "ALL")
                return (IntPtr)((1L << Environment.ProcessorCount) - 1);

            long mask = 0;
            string[] parts = affinity.Split(',');

            foreach (string part in parts)
            {
                string trimmedPart = part.Trim();
                
                if (trimmedPart.Contains("-"))
                {
                    string[] range = trimmedPart.Split('-');
                    if (range.Length == 2 && int.TryParse(range[0].Trim(), out int start) && int.TryParse(range[1].Trim(), out int end))
                    {
                        for (int i = start; i <= end; i++)
                        {
                            if (i >= 0 && i < 64)
                                mask |= (1L << i);
                        }
                    }
                }
                else if (int.TryParse(trimmedPart, out int core))
                {
                    if (core >= 0 && core < 64)
                        mask |= (1L << core);
                }
            }

            return (IntPtr)mask;
        }

        static ThreadPriorityLevel ConvertToThreadPriorityLevel(int priority)
        {
            return priority switch
            {
                15 => ThreadPriorityLevel.TimeCritical,
                2 => ThreadPriorityLevel.Highest,
                1 => ThreadPriorityLevel.AboveNormal,
                0 => ThreadPriorityLevel.Normal,
                -1 => ThreadPriorityLevel.BelowNormal,
                -2 => ThreadPriorityLevel.Lowest,
                -15 => ThreadPriorityLevel.Idle,
                _ => ThreadPriorityLevel.Normal
            };
        }

        static string GetPrioritySymbol(ThreadPriorityLevel priority)
        {
            int numeric = priority switch
            {
                ThreadPriorityLevel.TimeCritical => 15,
                ThreadPriorityLevel.Highest => 2,
                ThreadPriorityLevel.AboveNormal => 1,
                ThreadPriorityLevel.Normal => 0,
                ThreadPriorityLevel.BelowNormal => -1,
                ThreadPriorityLevel.Lowest => -2,
                ThreadPriorityLevel.Idle => -15,
                _ => 0
            };

            return PriorityMap.TryGetValue(numeric, out string symbol) ? symbol : "UNKNOWN";
        }

        static string GetThreadName(int threadId)
        {
            IntPtr threadHandle = IntPtr.Zero;
            IntPtr namePtr = IntPtr.Zero;
            
            try
            {
                threadHandle = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, (uint)threadId);
                
                if (threadHandle == IntPtr.Zero)
                    return "NO_ACCESS";

                int result = GetThreadDescription(threadHandle, out namePtr);
                
                if (result != 0 && namePtr != IntPtr.Zero)
                {
                    string name = Marshal.PtrToStringUni(namePtr);
                    return string.IsNullOrEmpty(name) ? "EMPTY" : name;
                }

                return "NO_NAME";
            }
            finally
            {
                if (namePtr != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(namePtr);
                
                if (threadHandle != IntPtr.Zero)
                    CloseHandle(threadHandle);
            }
        }
    }
}
