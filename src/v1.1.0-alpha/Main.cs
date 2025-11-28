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
        private const int FAST_CHECK_INTERVAL = 100;
        private const int SLOW_CHECK_INTERVAL = 5000;
        private const int DWM_FORCE_INTERVAL = 50;

        private static readonly HashSet<int> _processedProcesses = new HashSet<int>();
        private static readonly Dictionary<int, HashSet<string>> _pendingThreads = new Dictionary<int, HashSet<string>>();
        private static readonly Dictionary<int, Dictionary<int, int>> _threadPriorityHistory = new Dictionary<int, Dictionary<int, int>>();
        private static readonly Dictionary<string, DateTime> _lastDwmSetTime = new Dictionary<string, DateTime>();
        private static readonly HashSet<int> _ignoredSecondaryProcesses = new HashSet<int>();
        
      
        private static readonly Dictionary<int, DateTime> _lastThreadOperationTime = new Dictionary<int, DateTime>();
        private static readonly HashSet<int> _permanentlyTerminatedThreads = new HashSet<int>();
        private static readonly Dictionary<int, int> _threadSuspendCounts = new Dictionary<int, int>();
        
        private static bool _gameOptimized = false;
        private static int _gameProcessId = -1;
        private static DateTime _lastFastCheck = DateTime.MinValue;
        private static DateTime _lastSlowCheck = DateTime.MinValue;

        private static readonly Dictionary<int, string> PriorityMap = new Dictionary<int, string>
        {
            { 300, "SUSPEND" }, { 200, "TERMINATE" }, { 15, "TIME_CRITICAL" },
            { 2, "HIGHEST" }, { 1, "ABOVE_NORMAL" }, { 0, "NORMAL" },
            { -1, "BELOW_NORMAL" }, { -2, "LOWEST" }, { -15, "IDLE" }
        };

        private static readonly Dictionary<string, ProcessConfig> _gameConfigs = new Dictionary<string, ProcessConfig>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ProcessConfig> _systemConfigs = new Dictionary<string, ProcessConfig>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> CriticalProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csrss", "winlogon", "smss", "wininit", "services", "lsass"
        };

        private static NotifyIcon? _notifyIcon;

        [DllImport("kernel32.dll")]
        private static extern int GetThreadPriority(IntPtr hThread);

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

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr dwProcessAffinityMask);

        [DllImport("kernel32.dll")]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
        private const uint THREAD_SET_INFORMATION = 0x0020;
        private const uint THREAD_QUERY_INFORMATION = 0x0040;
        private const uint THREAD_SUSPEND_RESUME = 0x0002;
        private const uint THREAD_TERMINATE = 0x0001;
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int GetThreadDescription(IntPtr hThread, out IntPtr ppszThreadDescription);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);

        [DllImport("kernel32.dll")]
        private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

        [DllImport("kernel32.dll")]
        private static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        private static extern bool TerminateThread(IntPtr hThread, uint dwExitCode);

        [DllImport("kernel32.dll")]
        private static extern bool SetThreadPriorityBoost(IntPtr hThread, bool bDisablePriorityBoost);

        private const uint PROCESS_SET_INFORMATION = 0x0200;

        private const uint REALTIME_PRIORITY_CLASS = 0x00000100;
        private const uint HIGH_PRIORITY_CLASS = 0x00000080;
        private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
        private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        private const uint IDLE_PRIORITY_CLASS = 0x00000040;

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
            public LUID_AND_ATTRIBUTES[]? Privileges;
        }

        const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool LookupPrivilegeValue(string? lpSystemName, string? lpName, out LUID lpLuid);

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

        class ProcessConfig
        {
            public int Priority { get; set; }
            public string Affinity { get; set; } = "ALL";
            public bool DisableBoost { get; set; } = false;
            public Dictionary<string, ThreadConfig> Threads { get; set; } = new Dictionary<string, ThreadConfig>(StringComparer.OrdinalIgnoreCase);
        }

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
            
            Icon? appIcon = null;
            try
            {
                appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                appIcon = SystemIcons.Shield;
            }
            
            _notifyIcon = new NotifyIcon
            {
                Icon = appIcon,
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
                    _notifyIcon!.Text = "ProcRipper - Hidden";
                }
                else
                {
                    ShowWindow(handle, SW_SHOW);
                    BringWindowToTop(handle);
                    _notifyIcon!.Text = "ProcRipper - Running";
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
                    var now = DateTime.Now;
                    
                    if ((now - _lastFastCheck).TotalMilliseconds >= FAST_CHECK_INTERVAL)
                    {
                        CheckForPriorityReverts();
                        _lastFastCheck = now;
                    }
                    
                    if ((now - _lastSlowCheck).TotalMilliseconds >= SLOW_CHECK_INTERVAL)
                    {
                        CheckForConfiguredProcesses();
                        CheckPendingThreads();
                        CleanupThreadOperationTracking();
                        _lastSlowCheck = now;
                    }
                    
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    WriteColored($"Error in monitoring loop: {ex.Message}", ConsoleColor.Red);
                    Thread.Sleep(CHECK_INTERVAL);
                }
            }
        }

        private static void CleanupThreadOperationTracking()
        {
            try
            {
                var now = DateTime.Now;
                var threadsToRemove = new List<int>();
                
                foreach (var kvp in _lastThreadOperationTime)
                {
                    if ((now - kvp.Value).TotalMinutes > 1)
                    {
                        threadsToRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var threadId in threadsToRemove)
                {
                    _lastThreadOperationTime.Remove(threadId);
                    _threadSuspendCounts.Remove(threadId);
                }
                
                var terminatedToRemove = new List<int>();
                foreach (var threadId in _permanentlyTerminatedThreads)
                {
                    try
                    {
                        bool threadExists = false;
                        foreach (var process in Process.GetProcesses())
                        {
                            try
                            {
                                if (process.Threads.Cast<ProcessThread>().Any(t => t.Id == threadId))
                                {
                                    threadExists = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                        
                        if (!threadExists)
                        {
                            terminatedToRemove.Add(threadId);
                        }
                    }
                    catch { }
                }
                
                foreach (var threadId in terminatedToRemove)
                {
                    _permanentlyTerminatedThreads.Remove(threadId);
                }
            }
            catch { }
        }

        private static void CheckForPriorityReverts()
        {
            try
            {
                var processesToCheck = new List<int>();
                
                lock (_processedProcesses)
                {
                    processesToCheck.AddRange(_processedProcesses);
                }
                
                foreach (int processId in processesToCheck)
                {
                    try
                    {
                        Process process = Process.GetProcessById(processId);
                        if (process.HasExited)
                        {
                            CleanupProcessData(processId);
                            continue;
                        }

                        CheckProcessThreadPriorities(process);
                    }
                    catch (ArgumentException)
                    {
                        CleanupProcessData(processId);
                    }
                    catch
                    {
                    }
                }
                
                ApplyPersistentThreadOptimizations();
                CleanupDwmTracking();
            }
            catch
            {
            }
        }

        private static void ApplyPersistentThreadOptimizations()
        {
            try
            {
                var now = DateTime.Now;
                
                foreach (Process process in Process.GetProcessesByName("dwm"))
                {
                    try
                    {
                        if (process.HasExited) continue;

                        foreach (ProcessThread thread in process.Threads)
                        {
                            try
                            {
                                string threadName = GetThreadName(thread.Id);
                                
                                string threadKey = $"{process.Id}_{thread.Id}";
                                if (_lastDwmSetTime.TryGetValue(threadKey, out DateTime lastSet) && 
                                    (now - lastSet).TotalMilliseconds < DWM_FORCE_INTERVAL)
                                    continue;

                                if (threadName == "DWM Kernel Sensor Thread")
                                {
                                    IntPtr threadHandle = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION, false, (uint)thread.Id);
                                    if (threadHandle != IntPtr.Zero)
                                    {
                                        try
                                        {
                                            SetThreadPriority(threadHandle, -2);
                                            SetThreadPriorityBoost(threadHandle, true);
                                            IntPtr affinity = ParseAffinity("1,3,5,7");
                                            SetThreadAffinityMask(threadHandle, affinity);
                                            _lastDwmSetTime[threadKey] = now;
                                        }
                                        finally
                                        {
                                            CloseHandle(threadHandle);
                                        }
                                    }
                                }
                                else if (threadName == "DWM Master Input Thread")
                                {
                                    IntPtr threadHandle = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION, false, (uint)thread.Id);
                                    if (threadHandle != IntPtr.Zero)
                                    {
                                        try
                                        {
                                            SetThreadPriority(threadHandle, 15);
                                            SetThreadPriorityBoost(threadHandle, true);
                                            IntPtr affinity = ParseAffinity("0,2,4,6");
                                            SetThreadAffinityMask(threadHandle, affinity);
                                            _lastDwmSetTime[threadKey] = now;
                                        }
                                        finally
                                        {
                                            CloseHandle(threadHandle);
                                        }
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static void CleanupDwmTracking()
        {
            try
            {
                var now = DateTime.Now;
                var toRemove = new List<string>();
                
                foreach (var kvp in _lastDwmSetTime)
                {
                    if ((now - kvp.Value).TotalMinutes > 5)
                    {
                        toRemove.Add(kvp.Key);
                    }
                }
                
                foreach (string key in toRemove)
                {
                    _lastDwmSetTime.Remove(key);
                }
            }
            catch
            {
            }
        }

        private static void CheckProcessThreadPriorities(Process process)
        {
            try
            {
                string processName = process.ProcessName;
                Dictionary<string, ProcessConfig>? configs = null;
                
                if (_gameConfigs.ContainsKey(processName))
                    configs = _gameConfigs;
                else if (_systemConfigs.ContainsKey(processName))
                    configs = _systemConfigs;
                else
                    return;

                if (!configs.TryGetValue(processName, out ProcessConfig? processConfig))
                    return;

                ApplyProcessLevelSettings(process, processConfig!);

                foreach (ProcessThread thread in process.Threads)
                {
                    try
                    {
                        if (_permanentlyTerminatedThreads.Contains(thread.Id))
                            continue;

                        string threadName = GetThreadName(thread.Id);
                        if (string.IsNullOrEmpty(threadName) || !processConfig!.Threads.TryGetValue(threadName, out ThreadConfig? config))
                            continue;

                        if (_lastThreadOperationTime.TryGetValue(thread.Id, out DateTime lastOpTime) && 
                            (DateTime.Now - lastOpTime).TotalSeconds < 2)
                            continue;

                        if (config!.Priority == 300 || config.Priority == 200)
                        {
                            HandleSpecialThreadOperation(thread, config.Priority, threadName, processName);
                            continue;
                        }

                        int currentPriority = NativeGetThreadPriority(thread.Id);
                        if (currentPriority == -999) continue;

                        int targetPriority = config.Priority;

                        if (currentPriority != targetPriority)
                        {
                            if (IsStableRevert(process.Id, thread.Id, currentPriority, targetPriority))
                            {
                                WriteColored($"Thread priority reverted: {processName} -> {threadName} ({currentPriority} -> {targetPriority})", ConsoleColor.Yellow);
                                
                                if (NativeSetThreadPriority(thread.Id, targetPriority))
                                {
                                    WriteColored($"Restored priority for: {threadName}", ConsoleColor.Green);
                                }
                                else
                                {
                                    WriteColored($"Failed to restore priority for: {threadName}", ConsoleColor.Yellow);
                                }
                                
                                UpdateThreadHistory(process.Id, thread.Id, targetPriority);
                            }
                        }
                        else
                        {
                            UpdateThreadHistory(process.Id, thread.Id, currentPriority);
                        }

                        if (config.DisableBoost)
                        {
                            IntPtr threadHandle = OpenThread(THREAD_SET_INFORMATION, false, (uint)thread.Id);
                            if (threadHandle != IntPtr.Zero)
                            {
                                SetThreadPriorityBoost(threadHandle, true);
                                CloseHandle(threadHandle);
                            }
                        }

                        if (config.Affinity != "ALL")
                        {
                            IntPtr targetAffinity = ParseAffinity(config.Affinity);
                            IntPtr threadHandle = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION, false, (uint)thread.Id);
                            if (threadHandle != IntPtr.Zero)
                            {
                                SetThreadAffinityMask(threadHandle, targetAffinity);
                                CloseHandle(threadHandle);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static void ApplyProcessLevelSettings(Process process, ProcessConfig processConfig)
        {
            try
            {
                IntPtr processHandle = OpenProcess(PROCESS_SET_INFORMATION, false, (uint)process.Id);
                if (processHandle == IntPtr.Zero)
                    return;

                try
                {
                    if (processConfig.Priority != 0) 
                    {
                        uint priorityClass = ConvertToPriorityClass(processConfig.Priority);
                        SetPriorityClass(processHandle, priorityClass);
                    }

                    if (processConfig.Affinity != "ALL")
                    {
                        IntPtr affinity = ParseAffinity(processConfig.Affinity);
                        SetProcessAffinityMask(processHandle, affinity);
                    }

                    if (processConfig.DisableBoost)
                    {
                        SetProcessPriorityBoost(processHandle, true);
                    }
                }
                finally
                {
                    CloseHandle(processHandle);
                }
            }
            catch
            {
            }
        }

        private static uint ConvertToPriorityClass(int priority)
        {
            return priority switch
            {
                15 => REALTIME_PRIORITY_CLASS,
                2 => HIGH_PRIORITY_CLASS,
                1 => ABOVE_NORMAL_PRIORITY_CLASS,
                0 => NORMAL_PRIORITY_CLASS,
                -1 => BELOW_NORMAL_PRIORITY_CLASS,
                -2 => IDLE_PRIORITY_CLASS,
                -15 => IDLE_PRIORITY_CLASS,
                _ => NORMAL_PRIORITY_CLASS
            };
        }

        private static void HandleSpecialThreadOperation(ProcessThread thread, int operation, string threadName, string processName)
        {
            try
            {
                if (_lastThreadOperationTime.TryGetValue(thread.Id, out DateTime lastOpTime) && 
                    (DateTime.Now - lastOpTime).TotalSeconds < 2)
                    return;

                IntPtr threadHandle = IntPtr.Zero;
                
                if (operation == 300) 
                {
                    if (!_threadSuspendCounts.ContainsKey(thread.Id))
                        _threadSuspendCounts[thread.Id] = 0;

                    if (_threadSuspendCounts[thread.Id] > 10)
                    {
                        return;
                    }

                    threadHandle = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                    if (threadHandle != IntPtr.Zero)
                    {
                        uint previous = SuspendThread(threadHandle);
                        if (previous != 0xFFFFFFFF)
                        {
                            _threadSuspendCounts[thread.Id]++;
                            _lastThreadOperationTime[thread.Id] = DateTime.Now;
                            if (previous == 0)
                            {
                                WriteColored($"Suspended thread: {processName} -> {threadName}", ConsoleColor.Red);
                            }
                        }
                        CloseHandle(threadHandle);
                    }
                }
                else if (operation == 200) 
                {
                   
                    _permanentlyTerminatedThreads.Add(thread.Id);
                    _lastThreadOperationTime[thread.Id] = DateTime.Now;

                    threadHandle = OpenThread(THREAD_TERMINATE, false, (uint)thread.Id);
                    if (threadHandle != IntPtr.Zero)
                    {
                        bool success = TerminateThread(threadHandle, 0);
                        if (success)
                        {
                            WriteColored($"Terminated thread: {processName} -> {threadName}", ConsoleColor.DarkRed);
                        }
                        else
                        {
                            WriteColored($"Failed to terminate thread: {processName} -> {threadName}", ConsoleColor.Red);
                        }
                        CloseHandle(threadHandle);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteColored($"Error in thread operation {operation} for {threadName}: {ex.Message}", ConsoleColor.Red);
            }
        }

        private static void CheckPendingThreads()
        {
            try
            {
                var processesToCheck = new List<int>();
                
                lock (_pendingThreads)
                {
                    processesToCheck.AddRange(_pendingThreads.Keys);
                }
                
                foreach (int processId in processesToCheck)
                {
                    try
                    {
                        Process process = Process.GetProcessById(processId);
                        if (process.HasExited)
                        {
                            lock (_pendingThreads)
                            {
                                _pendingThreads.Remove(processId);
                            }
                            continue;
                        }

                        string processName = process.ProcessName;
                        Dictionary<string, ProcessConfig>? configs = null;
                        
                        if (_gameConfigs.ContainsKey(processName))
                            configs = _gameConfigs;
                        else if (_systemConfigs.ContainsKey(processName))
                            configs = _systemConfigs;
                        else
                            continue;

                        if (!configs.TryGetValue(processName, out ProcessConfig? processConfig))
                            continue;

                        HashSet<string>? pendingThreads;
                        lock (_pendingThreads)
                        {
                            if (!_pendingThreads.TryGetValue(processId, out pendingThreads))
                                continue;
                        }

                        var foundThreads = new List<string>();
                        int appliedCount = 0;

                        foreach (ProcessThread thread in process.Threads)
                        {
                            try
                            {
                                
                                if (_permanentlyTerminatedThreads.Contains(thread.Id))
                                    continue;

                                string threadName = GetThreadName(thread.Id);
                                if (!string.IsNullOrEmpty(threadName) && pendingThreads!.Contains(threadName))
                                {
                                    if (processConfig!.Threads.TryGetValue(threadName, out ThreadConfig? config))
                                    {
                                        if (config!.Priority == 300 || config.Priority == 200)
                                        {
                                            HandleSpecialThreadOperation(thread, config.Priority, threadName, processName);
                                            foundThreads.Add(threadName);
                                            continue;
                                        }

                                        NativeSetThreadPriority(thread.Id, config.Priority);
                                        
                                        if (config.Affinity != "ALL")
                                        {
                                            IntPtr targetAffinity = ParseAffinity(config.Affinity);
                                            IntPtr threadHandle = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION, false, (uint)thread.Id);
                                            if (threadHandle != IntPtr.Zero)
                                            {
                                                SetThreadAffinityMask(threadHandle, targetAffinity);
                                                CloseHandle(threadHandle);
                                            }
                                        }
                                        
                                        foundThreads.Add(threadName);
                                        appliedCount++;
                                        WriteColored($"Applied delayed optimization: {processName} -> {threadName}", ConsoleColor.Green);
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }

                        if (foundThreads.Count > 0)
                        {
                            lock (_pendingThreads)
                            {
                                if (_pendingThreads.TryGetValue(processId, out var currentPending))
                                {
                                    currentPending!.ExceptWith(foundThreads);
                                    if (currentPending.Count == 0)
                                    {
                                        _pendingThreads.Remove(processId);
                                    }
                                }
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                        lock (_pendingThreads)
                        {
                            _pendingThreads.Remove(processId);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsStableRevert(int processId, int threadId, int current, int target)
        {
            lock (_threadPriorityHistory)
            {
                if (!_threadPriorityHistory.TryGetValue(processId, out var threadHistory))
                {
                    threadHistory = new Dictionary<int, int>();
                    _threadPriorityHistory[processId] = threadHistory;
                }

                if (threadHistory!.TryGetValue(threadId, out var lastPriority))
                {
                    if (lastPriority == current && current != target)
                    {
                        return true;
                    }
                }
                
                threadHistory[threadId] = current;
                return false;
            }
        }

        private static void UpdateThreadHistory(int processId, int threadId, int priority)
        {
            lock (_threadPriorityHistory)
            {
                if (!_threadPriorityHistory.TryGetValue(processId, out var threadHistory))
                {
                    threadHistory = new Dictionary<int, int>();
                    _threadPriorityHistory[processId] = threadHistory;
                }
                threadHistory![threadId] = priority;
            }
        }

        private static void CleanupProcessData(int processId)
        {
            lock (_processedProcesses)
            {
                _processedProcesses.Remove(processId);
            }
            
            lock (_pendingThreads)
            {
                _pendingThreads.Remove(processId);
            }
            
            lock (_threadPriorityHistory)
            {
                _threadPriorityHistory.Remove(processId);
            }

           
            var threadsToRemove = new List<int>();
            foreach (var threadId in _lastThreadOperationTime.Keys)
            {
             
            }
            
            foreach (var threadId in _threadSuspendCounts.Keys)
            {
             
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

        static void LoadConfigFile(string fileName, Dictionary<string, ProcessConfig> configs, bool isGame)
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
                ProcessConfig? currentProcessConfig = null;
                string? currentProcess = null;

                foreach (string line in lines)
                {
                    string trimmed = line.Trim();

                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                        continue;

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        string processLine = trimmed.Substring(1, trimmed.Length - 2).Trim();
                        string[] processParts = processLine.Split(',');
                        
                        if (processParts.Length >= 1)
                        {
                            currentProcess = processParts[0].Trim().Replace(".exe", string.Empty);
                            currentProcessConfig = new ProcessConfig();

                            if (processParts.Length >= 2 && int.TryParse(processParts[1].Trim(), out int procPriority))
                            {
                                currentProcessConfig.Priority = procPriority;
                            }

                            if (processParts.Length >= 3)
                            {
                                currentProcessConfig.Affinity = processParts[2].Trim().ToUpper();
                            }

                            if (processParts.Length >= 4 && bool.TryParse(processParts[3].Trim(), out bool procBoost))
                            {
                                currentProcessConfig.DisableBoost = procBoost;
                            }

                            configs[currentProcess] = currentProcessConfig;
                        }
                        continue;
                    }

                    if (currentProcess == null || currentProcessConfig == null)
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
                                currentProcessConfig.Threads[threadName] = config;
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
                    "# Format: [ProcessName,ProcessPriority,ProcessAffinity,ProcessBoost]",
                    "#         ThreadName=ThreadPriority,ThreadAffinity,ThreadBoost",
                    "#",
                    "# Available priorities:",
                    "# 300=SUSPEND, 200=TERMINATE, 15=TIME_CRITICAL, 2=HIGHEST, 1=ABOVE_NORMAL, 0=NORMAL, -1=BELOW_NORMAL, -2=LOWEST, -15=IDLE",
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
                    "# Process-level configuration (applies to entire process)",
                    "[FortniteClient-Win64-Shipping.exe,2,ALL,true]",
                    "",
                    "# Thread-level configurations (override process settings for specific threads)",
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
                    "WindowsRawInputThread=-2,1,3,5,7,true"
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
                _gameOptimized = true;
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
                _notifyIcon!.Text = "ProcRipper - Running in Background";
                _notifyIcon.ShowBalloonTip(2000, "ProcRipper", "Now running in background. Double-click tray icon to show console.", ToolTipIcon.Info);
            }
        }

        static bool CheckProcesses(Dictionary<string, ProcessConfig> configs, bool isGame)
        {
            bool anyProcessed = false;

            foreach (string processName in configs.Keys)
            {
                Process[] processes = Process.GetProcessesByName(processName);
                if (processes.Length == 0) continue;

                Process? targetProcess = null;

                if (processes.Length > 1)
                {
                    if (isGame && _gameProcessId != -1)
                    {
                        targetProcess = processes.FirstOrDefault(p => p.Id == _gameProcessId);
                    }

                    if (targetProcess == null)
                    {
                        if (configs.TryGetValue(processName, out ProcessConfig? config))
                        {
                            targetProcess = IdentifyMainProcess(processes, config!);
                            
                            if (targetProcess != null)
                            {
                                foreach (var p in processes)
                                {
                                    if (p.Id != targetProcess.Id && !_ignoredSecondaryProcesses.Contains(p.Id))
                                    {
                                        WriteColored($"Ignoring secondary process: {processName} (PID {p.Id})", ConsoleColor.DarkGray);
                                        _ignoredSecondaryProcesses.Add(p.Id);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    targetProcess = processes[0];
                }

                if (targetProcess == null) continue; 

                try
                {
                    if (_processedProcesses.Contains(targetProcess.Id))
                        continue;

                    string type = isGame ? "Game" : "System";
                    WriteColored($"\n{type} MAIN process detected: {processName} (PID {targetProcess.Id}) at {DateTime.Now:HH:mm:ss}", ConsoleColor.Magenta);
                    
                    if (isGame)
                    {
                        _gameProcessId = targetProcess.Id;
                        
                        if (!WaitForGameThreadInitialization(targetProcess))
                        {
                            WriteColored("Game process exited during initialization wait.", ConsoleColor.Red);
                            continue;
                        }
                    }

                    ApplyProcessSettings(targetProcess, processName, configs);
                    EnumerateProcessThreads(targetProcess, processName, configs);

                    WriteColored("Thread optimization applied.", ConsoleColor.Green);
                    _processedProcesses.Add(targetProcess.Id);
                    anyProcessed = true;
                    
                    targetProcess.EnableRaisingEvents = true;
                    targetProcess.Exited += (sender, e) =>
                    {
                        WriteColored($"\n{type} process {processName} (PID {targetProcess.Id}) exited.", ConsoleColor.DarkYellow);
                        CleanupProcessData(targetProcess.Id);
                        _processedProcesses.Remove(targetProcess.Id);
                        targetProcess.Dispose();
                        if (isGame)
                        {
                            _gameOptimized = false;
                            _gameProcessId = -1;
                        }
                    };
                }
                catch (Exception ex)
                {
                    WriteColored($"ERROR: Processing {processName} (PID {targetProcess.Id}): {ex.Message}", ConsoleColor.Red);
                }
            }

            return anyProcessed;
        }

        static Process? IdentifyMainProcess(Process[] candidates, ProcessConfig config)
        {
            foreach (var p in candidates)
            {
                if (_ignoredSecondaryProcesses.Contains(p.Id)) continue;

                try
                {
                    p.Refresh();
                    foreach (ProcessThread t in p.Threads)
                    {
                        string tName = GetThreadName(t.Id);
                        if (!string.IsNullOrEmpty(tName) && config.Threads.ContainsKey(tName))
                        {
                            return p;
                        }
                    }
                }
                catch
                {
                }
            }
            return null; 
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

        static void ApplyProcessSettings(Process process, string processName, Dictionary<string, ProcessConfig> configs)
        {
            try
            {
                if (!configs.TryGetValue(processName, out ProcessConfig? processConfig))
                    return;

                ApplyProcessLevelSettings(process, processConfig!);

                bool disableBoost = processConfig!.DisableBoost || processConfig.Threads.Values.Any(c => c.DisableBoost);
                
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

        static void EnumerateProcessThreads(Process process, string processName, Dictionary<string, ProcessConfig> configs)
        {
            try
            {
                if (!configs.TryGetValue(processName, out ProcessConfig? processConfig))
                    return;

                ProcessThreadCollection threads = process.Threads;
                
                WriteColored($"Total threads found: {threads.Count}", ConsoleColor.Cyan);
                
                int modifiedThreads = 0;
                HashSet<string> foundThreads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> configuredThreads = new HashSet<string>(processConfig!.Threads.Keys, StringComparer.OrdinalIgnoreCase);
                
                foreach (ProcessThread thread in threads.Cast<ProcessThread>())
                {
                    try
                    {
                        bool? wasModified = DisplayThreadInfo(thread, process.Id, processConfig.Threads, foundThreads);
                        if (wasModified.HasValue && wasModified.Value)
                            modifiedThreads++;
                    }
                    catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
                    {
                        WriteColored($"{thread.Id} | ACCESS DENIED | N/A | N/A | N/A | N/A", ConsoleColor.Red);
                    }
                }
                
                var missingThreads = configuredThreads.Except(foundThreads, StringComparer.OrdinalIgnoreCase).ToList();
                if (missingThreads.Any())
                {
                    WriteColored($"Some configured threads not found initially ({missingThreads.Count}). Will keep watching for them.", ConsoleColor.Yellow);
                    
                    lock (_pendingThreads)
                    {
                        _pendingThreads[process.Id] = new HashSet<string>(missingThreads, StringComparer.OrdinalIgnoreCase);
                    }
                }
                
                WriteColored($"\nSummary: Modified {modifiedThreads} threads, {missingThreads.Count} pending", ConsoleColor.Cyan);
                if (modifiedThreads > 0)
                {
                    WriteColored("Initial optimizations applied.", ConsoleColor.Green);
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
            List<string> actions = new List<string>();
            bool wasModified = false;
            int currentPriority = NativeGetThreadPriority(threadId);
            if (currentPriority == -999)
            {
                actions.Add("PRIORITY FAIL: Access denied");
                return false;
            }
            string currentPrioritySymbol = GetPrioritySymbol(currentPriority);
            string threadName = GetThreadName(threadId);

            if (string.IsNullOrEmpty(threadName) || threadName.StartsWith("N/A") || threadName == "NO_NAME" || threadName == "EMPTY")
                return null;

            if (!threadConfigs.TryGetValue(threadName, out ThreadConfig? config))
                return null;

            if (config!.Priority == 300) 
            {
                IntPtr threadHandle = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)threadId);
                if (threadHandle != IntPtr.Zero)
                {
                    uint result = SuspendThread(threadHandle);
                    if (result != 0xFFFFFFFF)
                    {
                        actions.Add("SUSPENDED");
                        wasModified = true;
                        _lastThreadOperationTime[threadId] = DateTime.Now;
                    }
                    CloseHandle(threadHandle);
                }
            }
            else if (config.Priority == 200) 
            {
                _permanentlyTerminatedThreads.Add(threadId);
                _lastThreadOperationTime[threadId] = DateTime.Now;

                IntPtr threadHandle = OpenThread(THREAD_TERMINATE, false, (uint)threadId);
                if (threadHandle != IntPtr.Zero)
                {
                    bool success = TerminateThread(threadHandle, 0);
                    if (success)
                    {
                        actions.Add("TERMINATED");
                        wasModified = true;
                    }
                    CloseHandle(threadHandle);
                }
            }
            else
            {
                int targetPriority = config.Priority;
                if (currentPriority != targetPriority)
                {
                    if (NativeSetThreadPriority(threadId, targetPriority))
                    {
                        actions.Add($"PRIORITY: {currentPrioritySymbol}->{PriorityMap[config.Priority]}");
                        wasModified = true;
                    }
                    else
                    {
                        actions.Add("PRIORITY FAIL: Set failed");
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
            }

            string action = actions.Count > 0 ? string.Join(" | ", actions) : "NO CHANGE";

            string affinityDisplay = config.Affinity;
            int displayPriority = config.Priority;

            ConsoleColor priorityColor = displayPriority switch
            {
                300 => ConsoleColor.DarkRed,
                200 => ConsoleColor.Red,
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

        static string GetPrioritySymbol(int priority)
        {
            return PriorityMap.TryGetValue(priority, out string? symbol) ? symbol! : "UNKNOWN";
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
                    string? name = Marshal.PtrToStringUni(namePtr);
                    return string.IsNullOrEmpty(name) ? "EMPTY" : name!;
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

        private static bool NativeSetThreadPriority(int threadId, int priority)
        {
            IntPtr hThread = OpenThread(THREAD_SET_INFORMATION, false, (uint)threadId);
            if (hThread == IntPtr.Zero)
            {
                return false;
            }

            bool success = SetThreadPriority(hThread, priority);
            CloseHandle(hThread);
            return success;
        }

        private static int NativeGetThreadPriority(int threadId)
        {
            IntPtr hThread = OpenThread(THREAD_QUERY_INFORMATION, false, (uint)threadId);
            if (hThread == IntPtr.Zero)
            {
                return -999;
            }

            int priority = GetThreadPriority(hThread);
            CloseHandle(hThread);
            return priority;
        }
    }
}