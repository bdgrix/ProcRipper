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
using System.Text;
using System.Management;

namespace ProcRipper
{
    class Program
    {
        #region Constants
        private const string GAME_CONFIG_FILE = "GAME_PRIORITY.GCFG";
        private const string SYSTEM_CONFIG_FILE = "PROC_PRIORITY.GCFG";
        private const int CHECK_INTERVAL = 2000;
        private const int FAST_CHECK_INTERVAL = 100;
        private const int SLOW_CHECK_INTERVAL = 5000;
        private const int DWM_FORCE_INTERVAL = 50;
        private const int SYSTEM_PROCESS_CHECK_INTERVAL = 7 * 60 * 1000; // 7 minutes
        private const int MAX_REVERT_ATTEMPTS = 3;
        
        private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
        private const uint THREAD_SET_INFORMATION = 0x0020;
        private const uint THREAD_QUERY_INFORMATION = 0x0040;
        private const uint THREAD_SUSPEND_RESUME = 0x0002;
        private const uint THREAD_TERMINATE = 0x0001;
        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint LIST_MODULES_ALL = 0x03;
        
        private const uint REALTIME_PRIORITY_CLASS = 0x00000100;
        private const uint HIGH_PRIORITY_CLASS = 0x00000080;
        private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
        private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        private const uint IDLE_PRIORITY_CLASS = 0x00000040;
        
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_H = 0x48;
        private const int VK_G = 0x47;
        private const int VK_V = 0x56;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        #endregion

        #region Static Fields
        // Process tracking collections
        private static readonly HashSet<int> _processedProcesses = new HashSet<int>();
        private static readonly Dictionary<int, HashSet<string>> _pendingThreads = new Dictionary<int, HashSet<string>>();
        private static readonly HashSet<int> _ignoredSecondaryProcesses = new HashSet<int>();
        private static readonly HashSet<int> _disableBoostApplied = new HashSet<int>();
        
        // Thread tracking collections
        private static readonly Dictionary<int, Dictionary<int, int>> _threadPriorityHistory = new Dictionary<int, Dictionary<int, int>>();
        private static readonly HashSet<int> _blacklistedThreads = new HashSet<int>();
        private static readonly Dictionary<int, int> _threadRevertCounts = new Dictionary<int, int>();
        private static readonly Dictionary<int, DateTime> _lastThreadOperationTime = new Dictionary<int, DateTime>();
        private static readonly HashSet<int> _permanentlyTerminatedThreads = new HashSet<int>();
        private static readonly Dictionary<int, int> _threadSuspendCounts = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> _threadMonitoringCycles = new Dictionary<int, int>();
        private static readonly Dictionary<int, HashSet<string>> _appliedModuleThreads = new Dictionary<int, HashSet<string>>();
        
        // DWM tracking
        private static readonly Dictionary<string, DateTime> _lastDwmSetTime = new Dictionary<string, DateTime>();
        
        // Configuration tracking
        private static readonly Dictionary<string, Dictionary<string, int>> _moduleConfigs = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ProcessConfig> _gameConfigs = new Dictionary<string, ProcessConfig>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ProcessConfig> _systemConfigs = new Dictionary<string, ProcessConfig>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _disableBoostProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // CPU topology
        private static readonly HashSet<int> _pCores = new HashSet<int>();
        private static readonly HashSet<int> _eCores = new HashSet<int>();
        private static bool _cpuTopologyDetected = false;
        
        // Watcher tracking - FIXED: Now tracked per process and thread
        private static readonly Dictionary<string, int> _processWatcherCycles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, int>> _threadWatcherCycles = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> _processWatcherActive = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> _threadWatcherActive = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        
        // Cache
        private static IntPtr _cachedAutoAffinity = IntPtr.Zero;
        private static int _cachedCoreCount = 0;
        
        // State tracking
        private static bool _gameOptimized = false;
        private static int _gameProcessId = -1;
        private static bool _forceSystemOptimizations = false;
        private static bool _verboseLogging = false;  // Default to false for clean console
        
        // Timers
        private static DateTime _lastFastCheck = DateTime.MinValue;
        private static DateTime _lastSlowCheck = DateTime.MinValue;
        private static DateTime _lastHotkeyCheck = DateTime.MinValue;
        private static DateTime _lastRevertCheck = DateTime.MinValue;
        private static DateTime _lastSystemProcessCheck = DateTime.MinValue;
        
        // Logging
        private static string _logFilePath = "";
        private static readonly object _logLock = new object();
        
        // UI
        private static NotifyIcon? _notifyIcon;
        
        // Priority mapping
        private static readonly Dictionary<int, string> PriorityMap = new Dictionary<int, string>
        {
            { 300, "SUSPEND" }, { 200, "TERMINATE" }, { 15, "TIME_CRITICAL" },
            { 2, "HIGHEST" }, { 1, "ABOVE_NORMAL" }, { 0, "NORMAL" },
            { -1, "BELOW_NORMAL" }, { -2, "LOWEST" }, { -15, "IDLE" }
        };
        #endregion

        #region P/Invoke Declarations
        [DllImport("kernel32.dll")]
        private static extern int GetThreadPriority(IntPtr hThread);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

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

        [DllImport("kernel32.dll")]
        private static extern void GetCurrentProcessorNumberEx(out PROCESSOR_NUMBER procNumber);

        [DllImport("kernel32.dll")]
        private static extern bool GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP relationshipType, 
                                                                   IntPtr buffer, ref uint returnLength);

        [DllImport("psapi.dll")]
        private static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);

        [DllImport("psapi.dll")]
        private static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, [Out] StringBuilder lpFilename, uint nSize);

        [DllImport("psapi.dll")]
        private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool LookupPrivilegeValue(string? lpSystemName, string? lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        #region Structures
        [StructLayout(LayoutKind.Sequential)]
        public struct MODULEINFO
        {
            public IntPtr lpBaseOfDll;
            public uint SizeOfImage;
            public IntPtr EntryPoint;
        }

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

        private enum LOGICAL_PROCESSOR_RELATIONSHIP
        {
            RelationProcessorCore = 0,
            RelationNumaNode = 1,
            RelationCache = 2,
            RelationProcessorPackage = 3,
            RelationGroup = 4,
            RelationAll = 0xffff
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSOR_RELATIONSHIP
        {
            public byte Flags;
            public byte EfficiencyClass;
            public byte Reserved0;
            public byte Reserved1;
            public ushort GroupCount;
            public IntPtr GroupMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
        {
            public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
            public uint Size;
            public PROCESSOR_RELATIONSHIP Processor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSOR_NUMBER
        {
            public ushort Group;
            public byte Number;
            public byte Reserved;
        }
        #endregion
        #endregion

        #region Configuration Classes
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
            public Dictionary<string, int> Modules { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        #endregion

        #region Main Entry Point
        [STAThread]
        static void Main(string[] args)
        {
            InitializeLogFile();
            ShowConsole();
            ShowBanner();
            SetupTrayIcon();
            
            DetectCpuTopology();
            
            try
            {
                LoadConfigurations();
            }
            catch (FileNotFoundException ex)
            {
                WriteColored($"ERROR: Configuration file not found: {ex.Message}", ConsoleColor.Red);
                WriteLog($"ERROR: Configuration file not found: {ex.Message}");
                MessageBox.Show($"Configuration file not found: {ex.Message}\n\nPlease ensure both {GAME_CONFIG_FILE} and {SYSTEM_CONFIG_FILE} exist in the application directory.", 
                              "ProcRipper - Missing Configuration", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (_gameConfigs.Count == 0 && _systemConfigs.Count == 0)
            {
                WriteColored("No processes configured. Exiting.", ConsoleColor.Red);
                WriteLog("No processes configured. Exiting.");
                return;
            }

            WriteColored("PROC RIPPER v2.0.0-beta Started", ConsoleColor.Green);
            WriteLog("PROC RIPPER v2.0.0-beta Started");
            WriteColored("===================", ConsoleColor.Green);
            WriteColored($"Monitoring {_gameConfigs.Count} game processes, {_systemConfigs.Count} system processes", ConsoleColor.Cyan);
            WriteColored($"System processes will activate after first game launch.", ConsoleColor.Cyan);
            WriteColored("Hotkeys: CTRL+SHIFT+H=Hide/Show, CTRL+SHIFT+G=Force System, CTRL+SHIFT+V=Verbose", ConsoleColor.Magenta);
            WriteColored($"Console output is clean by default. Press CTRL+SHIFT+V for verbose mode.", ConsoleColor.DarkGray);
            
            Thread monitoringThread = new Thread(MonitoringLoop);
            monitoringThread.IsBackground = true;
            monitoringThread.Start();

            Application.Run();
        }
        #endregion

        #region Logging
        static void InitializeLogFile()
        {
            try
            {
                string logFolder = "log";
                
                if (!Directory.Exists(logFolder))
                {
                    Directory.CreateDirectory(logFolder);
                    WriteLog($"Log folder created: {logFolder}");
                }
                
                CleanupOldLogs(logFolder);
                
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
                _logFilePath = Path.Combine(logFolder, $"log-{timestamp}.gtxt");
                
                string header = $"ProcRipper Log - Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
                header += "==========================================\n\n";
                
                File.WriteAllText(_logFilePath, header);
                WriteLog($"Log file: {_logFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOG ERROR] {ex.Message}");
            }
        }

        static void CleanupOldLogs(string logFolder)
        {
            try
            {
                if (!Directory.Exists(logFolder))
                    return;
                    
                var logFiles = Directory.GetFiles(logFolder, "log-*.gtxt")
                                       .Select(f => new FileInfo(f))
                                       .OrderByDescending(f => f.CreationTime)
                                       .ToList();
                
                if (logFiles.Count > 5)
                {
                    foreach (var file in logFiles.Skip(5))
                    {
                        try
                        {
                            file.Delete();
                            WriteLog($"Removed old log file: {file.Name}");
                        }
                        catch (Exception ex)
                        {
                            WriteLog($"Failed to remove old log {file.Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error cleaning up old logs: {ex.Message}");
            }
        }

        static void WriteLog(string message)
        {
            lock (_logLock)
            {
                try
                {
                    string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    string logEntry = $"[{timestamp}] {message}\n";
                    File.AppendAllText(_logFilePath, logEntry);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LOG ERROR] {ex.Message}");
                }
            }
        }

        // FIXED: Verbose logging - only writes to console if verbose mode is enabled
        static void WriteVerbose(string message, ConsoleColor color = ConsoleColor.Gray)
        {
            WriteLog($"VERBOSE: {message}");
            if (_verboseLogging)
            {
                Console.ForegroundColor = color;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                Console.ResetColor();
            }
        }

        // FIXED: Clean console output - writes to console always, log always
        static void WriteColored(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            Console.ResetColor();
            WriteLog(text); // Always log to file
        }
        #endregion


        #region CPU Topology Detection
        private static void DetectCpuTopology()
        {
            try
            {
                _pCores.Clear();
                _eCores.Clear();
                
                int logicalCores = Environment.ProcessorCount;
                int physicalCores = GetPhysicalCoreCount();
                bool hyperthreading = logicalCores > physicalCores;
                
                // For non-hybrid CPUs like 12400F, all cores are P-cores
                // For hybrid CPUs, we try to detect P/E cores
                
                if (IsHybridCpu())
                {
                    DetectHybridCpuTopology(logicalCores);
                }
                else
                {
                    // Non-hybrid CPU: all logical cores are treated as P-cores
                    for (int i = 0; i < logicalCores; i++)
                    {
                        _pCores.Add(i);
                    }
                }
                
                _cpuTopologyDetected = true;
                
                // Display CPU info
                string htStatus = hyperthreading ? "HT Enabled" : "HT Disabled";
                WriteColored($"CPU: {physicalCores}P ({logicalCores} threads {htStatus}) + {_eCores.Count}E cores detected", ConsoleColor.Green);
                WriteLog($"CPU Topology: {physicalCores} physical cores, {logicalCores} logical cores, {_eCores.Count} E-cores");
                
                // Show detailed info only in verbose mode
                if (_verboseLogging)
                {
                    WriteVerbose($"P-cores (logical): [{string.Join(", ", _pCores.OrderBy(x => x))}]", ConsoleColor.DarkCyan);
                    if (_eCores.Count > 0)
                    {
                        WriteVerbose($"E-cores: [{string.Join(", ", _eCores.OrderBy(x => x))}]", ConsoleColor.Cyan);
                    }
                    else
                    {
                        WriteVerbose("No E-cores detected (non-hybrid CPU)", ConsoleColor.Cyan);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteColored($"CPU Topology detection failed: {ex.Message}", ConsoleColor.Red);
                WriteLog($"CPU Topology detection failed: {ex.Message}");
                
                // Fallback: all cores as P-cores
                int totalCores = Environment.ProcessorCount;
                for (int i = 0; i < totalCores; i++)
                {
                    _pCores.Add(i);
                }
                WriteColored($"Fallback: All {totalCores} logical cores treated as P-cores", ConsoleColor.Yellow);
                WriteLog($"Fallback: All {totalCores} logical cores treated as P-cores");
            }
        }

        private static int GetPhysicalCoreCount()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        return Convert.ToInt32(obj["NumberOfCores"]);
                    }
                }
            }
            catch
            {
                // If WMI fails, estimate physical cores (logical/2 or same as logical)
                int logical = Environment.ProcessorCount;
                return logical <= 4 ? logical : logical / 2;
            }
            return Environment.ProcessorCount;
        }

        private static bool IsHybridCpu()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = (obj["Name"]?.ToString() ?? "").ToLower();
                        
                        // Check for Intel hybrid CPUs (12th gen+)
                        if (name.Contains("i9-12") || name.Contains("i9-13") || name.Contains("i9-14") ||
                            name.Contains("i7-12") || name.Contains("i7-13") || name.Contains("i7-14") ||
                            name.Contains("i5-12") || name.Contains("i5-13") || name.Contains("i5-14"))
                        {
                            // Check for non-hybrid exceptions like 12400F
                            if (name.Contains("12400") || name.Contains("12500") || 
                                name.Contains("12600") && !name.Contains("12600k") && !name.Contains("12600kf"))
                            {
                                return false; // These are non-hybrid
                            }
                            return true; // Likely hybrid
                        }
                    }
                }
            }
            catch
            {
                // If detection fails, assume non-hybrid
            }
            return false;
        }

        private static void DetectHybridCpuTopology(int logicalCores)
        {
            // Common hybrid configurations
            if (logicalCores == 24 || logicalCores == 32) // i9-13900K/KF, i9-14900K/KF
            {
                // 8P + 16E logical cores (8P with HT = 16, 16E without HT = 16)
                for (int i = 0; i < 16; i++) _pCores.Add(i);
                for (int i = 16; i < logicalCores; i++) _eCores.Add(i);
                WriteVerbose($"Detected: 8P (16 threads) + 16E configuration", ConsoleColor.Yellow);
            }
            else if (logicalCores == 20) // i7-13700K/KF, i7-14700K/KF
            {
                // 8P + 12E logical cores (8P with HT = 16, 12E without HT = 12)
                // Actually: i7-13700K has 8P (16 threads) + 8E (8 threads) = 24 threads
                // Let me fix: i7-13700K has 8P+8E = 16 physical, 24 logical
                // 8P with HT = 16, 8E without HT = 8, total = 24
                // So for 20 logical cores, it's something else
                // Let's use generic approach
                int pCores = logicalCores / 3 * 2; // Estimate 2/3 as P-cores
                for (int i = 0; i < pCores; i++) _pCores.Add(i);
                for (int i = pCores; i < logicalCores; i++) _eCores.Add(i);
                WriteVerbose($"Detected: Generic hybrid configuration", ConsoleColor.Yellow);
            }
            else if (logicalCores == 16) // i5-13600K/KF, i5-14600K/KF
            {
                // 6P + 8E logical cores (6P with HT = 12, 8E without HT = 8)
                for (int i = 0; i < 12; i++) _pCores.Add(i);
                for (int i = 12; i < logicalCores; i++) _eCores.Add(i);
                WriteVerbose($"Detected: 6P (12 threads) + 8E configuration", ConsoleColor.Yellow);
            }
            else
            {
                // Generic hybrid assumption: first 2/3 are P-cores, rest are E-cores
                int pCores = (logicalCores * 2) / 3;
                for (int i = 0; i < pCores; i++) _pCores.Add(i);
                for (int i = pCores; i < logicalCores; i++) _eCores.Add(i);
                WriteVerbose($"Detected: Generic hybrid {pCores}P + {logicalCores - pCores}E", ConsoleColor.Yellow);
            }
        }
        #endregion


      





        #region Affinity Parsing
        static IntPtr ParseAffinity(string affinity)
        {
            if (affinity == "ALL")
                return (IntPtr)((1L << Environment.ProcessorCount) - 1);

            if (affinity.Equals("ht on", StringComparison.OrdinalIgnoreCase))
                return (IntPtr)((1L << Environment.ProcessorCount) - 1);
            
            if (affinity.Equals("ht off", StringComparison.OrdinalIgnoreCase))
                return GetPhysicalCoresOnly();
            
            if (affinity.Equals("ht only", StringComparison.OrdinalIgnoreCase))
                return GetHyperThreadedCoresOnly();

            if (affinity.Equals("p-core", StringComparison.OrdinalIgnoreCase))
                return ParsePCoreAffinity();
            
            if (affinity.Equals("e-core", StringComparison.OrdinalIgnoreCase))
                return ParseECoreAffinity();
            
            if (affinity.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
                return GetAutoAffinity();

            return ParseManualAffinity(affinity);
        }

        private static IntPtr ParsePCoreAffinity()
        {
            if (!_cpuTopologyDetected)
                DetectCpuTopology();
                
            if (_pCores.Count == 0)
            {
                WriteColored("ERROR: P-core affinity requested but no P-cores detected!", ConsoleColor.Red);
                WriteLog("ERROR: P-core affinity requested but no P-cores detected");
                return (IntPtr)0;
            }
            
            long mask = 0;
            foreach (int core in _pCores)
            {
                mask |= (1L << core);
            }
            
            WriteVerbose($"Using P-cores only: [{string.Join(", ", _pCores.OrderBy(x => x))}]", ConsoleColor.DarkCyan);
            WriteLog($"Using P-cores only: [{string.Join(", ", _pCores.OrderBy(x => x))}]");
            return (IntPtr)mask;
        }

        private static IntPtr ParseECoreAffinity()
        {
            if (!_cpuTopologyDetected)
                DetectCpuTopology();
                
            if (_eCores.Count == 0)
            {
                WriteColored("ERROR: E-core affinity requested but no E-cores detected!", ConsoleColor.Red);
                WriteLog("ERROR: E-core affinity requested but no E-cores detected");
                return (IntPtr)0;
            }
            
            long mask = 0;
            foreach (int core in _eCores)
            {
                mask |= (1L << core);
            }
            
            WriteVerbose($"Using E-cores only: [{string.Join(", ", _eCores.OrderBy(x => x))}]", ConsoleColor.DarkCyan);
            WriteLog($"Using E-cores only: [{string.Join(", ", _eCores.OrderBy(x => x))}]");
            return (IntPtr)mask;
        }

        private static IntPtr GetAutoAffinity()
        {
            int currentCoreCount = Environment.ProcessorCount;
            if (_cachedAutoAffinity == IntPtr.Zero || _cachedCoreCount != currentCoreCount)
            {
                _cachedAutoAffinity = CalculateAutoAffinity();
                _cachedCoreCount = currentCoreCount;
            }
            return _cachedAutoAffinity;
        }

        private static IntPtr ParseManualAffinity(string affinity)
        {
            long manualMask = 0;
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
                                manualMask |= (1L << i);
                        }
                    }
                }
                else if (int.TryParse(trimmedPart, out int core))
                {
                    if (core >= 0 && core < 64)
                        manualMask |= (1L << core);
                }
            }

            return (IntPtr)manualMask;
        }

        private static IntPtr GetPhysicalCoresOnly()
        {
            int totalCores = Environment.ProcessorCount;
            long mask = 0;
            
            for (int i = 0; i < totalCores; i += 2)
            {
                mask |= (1L << i);
            }
            
            if (totalCores % 2 == 1)
            {
                mask |= (1L << (totalCores - 1));
            }
            
            WriteVerbose($"HT Off - Physical cores only: {Convert.ToString(mask, 2).PadLeft(totalCores, '0')}", ConsoleColor.DarkCyan);
            WriteLog($"HT Off - Physical cores only: {Convert.ToString(mask, 2).PadLeft(totalCores, '0')}");
            
            return (IntPtr)mask;
        }

        private static IntPtr GetHyperThreadedCoresOnly()
        {
            int totalCores = Environment.ProcessorCount;
            long mask = 0;
            
            for (int i = 1; i < totalCores; i += 2)
            {
                mask |= (1L << i);
            }
            
            WriteVerbose($"HT Only - Hyper-threaded cores only: {Convert.ToString(mask, 2).PadLeft(totalCores, '0')}", ConsoleColor.DarkCyan);
            WriteLog($"HT Only - Hyper-threaded cores only: {Convert.ToString(mask, 2).PadLeft(totalCores, '0')}");
            
            return (IntPtr)mask;
        }

        private static IntPtr CalculateAutoAffinity()
        {
            int totalCores = Environment.ProcessorCount;
            long mask = 1L << 0; // Always reserve core 0 for input
            
            if (totalCores <= 1)
                return (IntPtr)mask;
            
            if (totalCores <= 4)
            {
                for (int i = 1; i < totalCores; i++)
                {
                    mask |= (1L << i);
                }
                WriteVerbose($"Small system auto affinity: Core 0=input, {totalCores-1} cores=game", ConsoleColor.DarkCyan);
            }
            else if (totalCores <= 8)
            {
                int gameCores = (totalCores - 1) / 2;
                int systemCores = (totalCores - 1) - gameCores;
                
                for (int i = 1; i <= gameCores; i++)
                {
                    mask |= (1L << i);
                }
                for (int i = gameCores + 1; i < totalCores; i++)
                {
                    mask |= (1L << i);
                }
                WriteVerbose($"Medium system auto affinity: 1 input, {gameCores} game, {systemCores} system", ConsoleColor.DarkCyan);
            }
            else
            {
                int remainingCores = totalCores - 1;
                int gameCores = (int)Math.Round(remainingCores * 0.4);
                int renderCores = (int)Math.Round(remainingCores * 0.2);
                int systemCores = remainingCores - gameCores - renderCores;
                
                if (gameCores < 1) gameCores = 1;
                if (renderCores < 1) renderCores = 1;
                if (systemCores < 1) systemCores = 1;
                
                int currentCore = 1;
                
                for (int i = 0; i < gameCores && currentCore < totalCores; i++, currentCore++)
                {
                    mask |= (1L << currentCore);
                }
                
                for (int i = 0; i < renderCores && currentCore < totalCores; i++, currentCore++)
                {
                    mask |= (1L << currentCore);
                }
                
                for (int i = 0; i < systemCores && currentCore < totalCores; i++, currentCore++)
                {
                    mask |= (1L << currentCore);
                }
                
                WriteVerbose($"Large system auto affinity: 1 input, {gameCores} game, {renderCores} render, {systemCores} system", ConsoleColor.DarkCyan);
            }
            
            WriteVerbose($"Auto affinity mask for {totalCores} cores: {Convert.ToString(mask, 2).PadLeft(totalCores, '0')}", ConsoleColor.DarkCyan);
            WriteLog($"Auto affinity calculated: {totalCores} cores, mask: {Convert.ToString(mask, 2).PadLeft(totalCores, '0')}");
            
            return (IntPtr)mask;
        }
        #endregion

        #region UI & Hotkeys
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
                Text = "ProcRipper v2.0.0-beta - Running"
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            
            ToolStripMenuItem showHideItem = new ToolStripMenuItem("Show/Hide Console");
            showHideItem.Click += (s, e) => ToggleConsole();
            menu.Items.Add(showHideItem);

            ToolStripMenuItem verboseItem = new ToolStripMenuItem("Toggle Verbose Logging");
            verboseItem.Click += (s, e) => ToggleVerboseLogging();
            menu.Items.Add(verboseItem);
            
            menu.Items.Add(new ToolStripSeparator());
            
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => 
            {
                WriteLog("Application shutdown requested");
                _notifyIcon.Visible = false;
                Application.Exit();
                Environment.Exit(0);
            };
            menu.Items.Add(exitItem);
            
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (s, e) => ToggleConsole();
            _notifyIcon.ShowBalloonTip(3000, "ProcRipper v2.0.0-beta", "Monitoring started successfully", ToolTipIcon.Info);
        }

        static void ToggleConsole()
        {
            IntPtr handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
            {
                if (IsWindowVisible(handle))
                {
                    ShowWindow(handle, SW_HIDE);
                    _notifyIcon!.Text = "ProcRipper v2.0.0-beta - Hidden";
                    WriteLog("Console hidden");
                }
                else
                {
                    ShowWindow(handle, SW_SHOW);
                    BringWindowToTop(handle);
                    _notifyIcon!.Text = "ProcRipper v2.0.0-beta - Running";
                    WriteLog("Console shown");
                }
            }
        }

        static void ToggleVerboseLogging()
        {
            _verboseLogging = !_verboseLogging;
            if (_verboseLogging)
            {
                WriteColored("VERBOSE LOGGING ENABLED", ConsoleColor.Magenta);
                WriteLog("Verbose logging enabled");
                if (_notifyIcon != null)
                {
                    _notifyIcon.Text = "ProcRipper v2.0.0-beta - Verbose";
                    _notifyIcon.ShowBalloonTip(2000, "ProcRipper - Verbose Mode", 
                        "Detailed logging enabled. Check console for verbose output.", ToolTipIcon.Info);
                }
            }
            else
            {
                WriteColored("VERBOSE LOGGING DISABLED", ConsoleColor.Magenta);
                WriteLog("Verbose logging disabled");
                if (_notifyIcon != null)
                {
                    _notifyIcon.Text = "ProcRipper v2.0.0-beta - Running";
                    _notifyIcon.ShowBalloonTip(2000, "ProcRipper - Normal Mode", 
                        "Verbose logging disabled. Console output is now clean.", ToolTipIcon.Info);
                }
            }
        }

        static void CheckHotkeys()
        {
            if ((DateTime.Now - _lastHotkeyCheck).TotalMilliseconds < 200) return;

            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            bool h = (GetAsyncKeyState(VK_H) & 0x8000) != 0;
            bool g = (GetAsyncKeyState(VK_G) & 0x8000) != 0;
            bool v = (GetAsyncKeyState(VK_V) & 0x8000) != 0;

            if (ctrl && shift && h)
            {
                ToggleConsole();
                _lastHotkeyCheck = DateTime.Now;
            }

            if (ctrl && shift && v)
            {
                ToggleVerboseLogging();
                _lastHotkeyCheck = DateTime.Now;
            }

            if (ctrl && shift && g)
            {
                _forceSystemOptimizations = !_forceSystemOptimizations;
                if (_forceSystemOptimizations)
                {
                    WriteColored("SYSTEM OPTIMIZATIONS FORCED BY USER (Ctrl+Shift+G)", ConsoleColor.Magenta);
                    WriteLog("SYSTEM OPTIMIZATIONS FORCED BY USER (Ctrl+Shift+G)");
                    
                    if (_notifyIcon != null)
                    {
                        _notifyIcon.Text = "ProcRipper v2.0.0-beta - System Optimizations FORCED";
                        _notifyIcon.ShowBalloonTip(3000, "ProcRipper - System Optimizations FORCED", 
                            "System process optimizations have been manually activated", ToolTipIcon.Info);
                    }
                    
                    CheckForConfiguredProcesses();
                }
                else
                {
                    WriteColored("SYSTEM OPTIMIZATIONS RETURNED TO NORMAL MODE", ConsoleColor.Magenta);
                    WriteLog("SYSTEM OPTIMIZATIONS RETURNED TO NORMAL MODE");
                    
                    if (_notifyIcon != null)
                    {
                        _notifyIcon.Text = "ProcRipper v2.0.0-beta - Running";
                        _notifyIcon.ShowBalloonTip(3000, "ProcRipper - Normal Mode", 
                            "System process optimizations returned to automatic mode", ToolTipIcon.Info);
                    }
                }
                
                _lastHotkeyCheck = DateTime.Now;
            }
        }

        static void ShowBanner()
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine(@"
        ██████╗ ██████╗  ██████╗ ██████╗    ██████╗ ██╗██████╗ ██████╗ ███████╗██████╗ 
        ██╔══██╗██╔══██╗██╔═══██╗██╔════╝   ██╔══██╗██║██╔══██╗██╔══██╗██╔════╝██╔══██╗
        ██████╔╝██████╔╝██║   ██║██║        ██████╔╝██║██████╔╝██████╔╝█████╗  ██████╔╝
        ██╔═══╝ ██╔══██╗██║   ██║██║        ██╔═══╝ ██║██╔═══╝ ██╔══██╗██╔══╝  ██╔══██╗
        ██║     ██║  ██║╚██████╔╝╚██████╗   ██║     ██║██║     ██║  ██║███████╗██║  ██║
        ╚═╝     ╚═╝  ╚═╝ ╚═════╝  ╚═════╝   ╚═╝     ╚═╝╚═╝     ╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝
        ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("               Process Thread Optimizer v2.0.0-beta");
            Console.ResetColor();
            Console.WriteLine();
        }
        #endregion

        #region Monitoring Loop
        private static void MonitoringLoop()
        {
            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (!isAdmin)
            {
                WriteColored("WARNING: Not running as administrator. Access to system processes may be limited.", ConsoleColor.Yellow);
                WriteLog("WARNING: Not running as administrator");
            }
            else
            {
                if (EnablePrivilege("SeDebugPrivilege"))
                {
                    WriteVerbose("SeDebugPrivilege enabled successfully.", ConsoleColor.Green);
                    WriteLog("SeDebugPrivilege enabled successfully");
                }
            }

            while (true)
            {
                try
                {
                    CheckHotkeys();
                    
                    var now = DateTime.Now;
                    
                    if ((now - _lastFastCheck).TotalMilliseconds >= FAST_CHECK_INTERVAL)
                    {
                        CheckForPriorityReverts();
                        _lastFastCheck = now;
                    }
                    
                    if ((now - _lastRevertCheck).TotalMilliseconds >= 20000)
                    {
                        CheckForThreadReverts();
                        _lastRevertCheck = now;
                    }
                    
                    if ((now - _lastSlowCheck).TotalMilliseconds >= SLOW_CHECK_INTERVAL)
                    {
                        CheckForConfiguredProcesses();
                        CheckPendingThreads();
                        CleanupThreadOperationTracking();
                        _lastSlowCheck = now;
                    }

                    if ((now - _lastSystemProcessCheck).TotalMilliseconds >= SYSTEM_PROCESS_CHECK_INTERVAL)
                    {
                        CheckForNewSystemProcesses();
                        RunSmartProcessWatchers(); // FIXED: Watcher now properly tracks activation state
                        _lastSystemProcessCheck = now;
                    }
                    
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    WriteColored($"Error in monitoring loop: {ex.Message}", ConsoleColor.Red);
                    WriteLog($"Error in monitoring loop: {ex.Message}");
                    Thread.Sleep(CHECK_INTERVAL);
                }
            }
        }
        #endregion

        #region Process & Thread Management
        private static void RunSmartProcessWatchers()
        {
            CheckMissingProcesses();
            CheckMissingThreads();
        }

        // FIXED: Process watcher - Only start counting cycles after game optimization or system optimization
        private static void CheckMissingProcesses()
        {
            try
            {
                // Only run watchers if game is optimized or system optimizations are forced
                if (!_gameOptimized && !_forceSystemOptimizations)
                    return;

                var processesToRemove = new List<string>();
                
                foreach (var processName in _gameConfigs.Keys.Concat(_systemConfigs.Keys))
                {
                    // Initialize watcher activation state if not exists
                    if (!_processWatcherActive.ContainsKey(processName))
                    {
                        _processWatcherActive[processName] = false;
                        _processWatcherCycles[processName] = 0;
                    }

                    // Activate watcher only if game is optimized or system optimizations are forced
                    if (!_processWatcherActive[processName])
                    {
                        if ((_gameOptimized && _gameConfigs.ContainsKey(processName)) || 
                            ((_gameOptimized || _forceSystemOptimizations) && _systemConfigs.ContainsKey(processName)))
                        {
                            _processWatcherActive[processName] = true;
                            _processWatcherCycles[processName] = 0;
                            WriteVerbose($"Process watcher activated for: {processName}", ConsoleColor.DarkCyan);
                            WriteLog($"Process watcher activated for: {processName}");
                        }
                        continue;
                    }

                    // Skip if watcher is not active
                    if (!_processWatcherActive[processName])
                        continue;

                    // Check if process exists
                    if (Process.GetProcessesByName(processName).Length > 0)
                    {
                        // Process found, reset cycle count
                        if (_processWatcherCycles[processName] > 0)
                        {
                            _processWatcherCycles[processName] = 0;
                            WriteVerbose($"Process watcher reset: {processName} found", ConsoleColor.DarkGreen);
                            WriteLog($"Process watcher reset: {processName} found");
                        }
                        continue;
                    }

                    // Process not found, increment cycle
                    _processWatcherCycles[processName]++;
                    
                    WriteVerbose($"Process watcher: {processName} not found (cycle {_processWatcherCycles[processName]}/4)", ConsoleColor.DarkYellow);
                    WriteLog($"Process watcher: {processName} not found (cycle {_processWatcherCycles[processName]}/4)");

                    // Stop after 4 cycles (40 minutes)
                    if (_processWatcherCycles[processName] >= 4)
                    {
                        WriteColored($"Process watcher stopped: {processName} not found after 4 cycles", ConsoleColor.DarkGray);
                        WriteLog($"Process watcher stopped: {processName} not found after 4 cycles");
                        _processWatcherActive[processName] = false;
                        processesToRemove.Add(processName);
                    }
                }

                // Clean up stopped watchers
                foreach (var processName in processesToRemove)
                {
                    _processWatcherCycles.Remove(processName);
                    _processWatcherActive.Remove(processName);
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error in CheckMissingProcesses: {ex.Message}");
            }
        }

        // FIXED: Thread watcher - Only start counting cycles after process is optimized
        private static void CheckMissingThreads()
        {
            try
            {
                // Only run if game is optimized or system optimizations are forced
                if (!_gameOptimized && !_forceSystemOptimizations)
                    return;

                var threadsToRemove = new List<(string processName, string threadName)>();

                lock (_pendingThreads)
                {
                    foreach (var kvp in _pendingThreads.ToList())
                    {
                        int processId = kvp.Key;
                        var pendingThreads = kvp.Value;

                        // Skip if process no longer exists
                        string processName = GetProcessName(processId);
                        if (processName == null)
                        {
                            _pendingThreads.Remove(processId);
                            continue;
                        }

                        foreach (var threadName in pendingThreads.ToList())
                        {
                            string threadKey = $"{processId}_{threadName}";

                            // Initialize thread watcher tracking if not exists
                            if (!_threadWatcherActive.ContainsKey(threadKey))
                            {
                                _threadWatcherActive[threadKey] = false;
                                if (!_threadWatcherCycles.ContainsKey(processName))
                                    _threadWatcherCycles[processName] = new Dictionary<string, int>();
                                
                                _threadWatcherCycles[processName][threadName] = 0;
                            }

                            // Activate watcher only if the process has been processed
                            if (!_threadWatcherActive[threadKey] && _processedProcesses.Contains(processId))
                            {
                                _threadWatcherActive[threadKey] = true;
                                _threadWatcherCycles[processName][threadName] = 0;
                                WriteVerbose($"Thread watcher activated: {processName} -> {threadName}", ConsoleColor.DarkCyan);
                                WriteLog($"Thread watcher activated: {processName} -> {threadName}");
                            }

                            // Skip if watcher not active
                            if (!_threadWatcherActive[threadKey])
                                continue;

                            // Thread still pending, increment cycle
                            _threadWatcherCycles[processName][threadName]++;

                            WriteVerbose($"Thread watcher: {processName} -> {threadName} not found (cycle {_threadWatcherCycles[processName][threadName]}/5)", ConsoleColor.DarkYellow);
                            WriteLog($"Thread watcher: {processName} -> {threadName} not found (cycle {_threadWatcherCycles[processName][threadName]}/5)");

                            // Stop after 5 cycles (25 minutes)
                            if (_threadWatcherCycles[processName][threadName] >= 5)
                            {
                                WriteColored($"Thread watcher stopped: {processName} -> {threadName} not found after 5 cycles", ConsoleColor.DarkGray);
                                WriteLog($"Thread watcher stopped: {processName} -> {threadName} not found after 5 cycles");
                                _threadWatcherActive[threadKey] = false;
                                threadsToRemove.Add((processName, threadName));
                            }
                        }
                    }

                    // Remove stopped threads from pending
                    foreach (var (procName, threadName) in threadsToRemove)
                    {
                        // Find the process ID for this thread
                        var processEntry = _pendingThreads.FirstOrDefault(x => 
                        {
                            try
                            {
                                var p = Process.GetProcessById(x.Key);
                                return p.ProcessName.Equals(procName, StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                                return false;
                            }
                        });

                        if (processEntry.Key != 0 && _pendingThreads.ContainsKey(processEntry.Key))
                        {
                            string threadKey = $"{processEntry.Key}_{threadName}";
                            _pendingThreads[processEntry.Key].Remove(threadName);
                            _threadWatcherActive.Remove(threadKey);
                            
                            if (_pendingThreads[processEntry.Key].Count == 0)
                            {
                                _pendingThreads.Remove(processEntry.Key);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error in CheckMissingThreads: {ex.Message}");
            }
        }

        private static string? GetProcessName(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        private static void CheckForNewSystemProcesses()
        {
            try
            {
                if (!_gameOptimized && !_forceSystemOptimizations) 
                {
                    WriteVerbose("System process check skipped - game not optimized and system optimizations not forced", ConsoleColor.DarkGray);
                    return;
                }

                WriteVerbose("Checking for new system processes...", ConsoleColor.Cyan);
                WriteLog("Checking for new system processes");

                foreach (var processConfig in _systemConfigs)
                {
                    string processName = processConfig.Key;
                    Process[] processes = Process.GetProcessesByName(processName);
                    
                    foreach (Process process in processes)
                    {
                        if (_processedProcesses.Contains(process.Id) || _ignoredSecondaryProcesses.Contains(process.Id))
                            continue;

                        try
                        {
                            WriteColored($"New system process detected: {processName} (PID {process.Id})", ConsoleColor.Magenta);
                            WriteLog($"New system process detected: {processName} (PID {process.Id})");

                            ApplyProcessSettings(process, processName, _systemConfigs);
                            EnumerateProcessThreads(process, processName, _systemConfigs);
                            
                            ApplyModuleOptimizations(process, processName, _systemConfigs);

                            _processedProcesses.Add(process.Id);
                            
                            process.EnableRaisingEvents = true;
                            process.Exited += (sender, e) =>
                            {
                                WriteColored($"System process {processName} (PID {process.Id}) exited.", ConsoleColor.DarkYellow);
                                WriteLog($"System process {processName} (PID {process.Id}) exited.");
                                CleanupProcessData(process.Id);
                                _processedProcesses.Remove(process.Id);
                                process.Dispose();
                            };
                        }
                        catch (Exception ex)
                        {
                            WriteColored($"Error processing new system process {processName}: {ex.Message}", ConsoleColor.Red);
                            WriteLog($"Error processing new system process {processName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteColored($"Error checking for new system processes: {ex.Message}", ConsoleColor.Red);
                WriteLog($"Error checking for new system processes: {ex.Message}");
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
            }
            catch { }
        }

        private static void CheckForPriorityReverts()
        {
            // Empty - moved to CheckForThreadReverts
        }

        private static void CheckForThreadReverts()
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
                    catch (Exception ex)
                    {
                        WriteLog($"Error checking thread reverts for process {processId}: {ex.Message}");
                    }
                }
                
                ApplyPersistentThreadOptimizations();
                CleanupDwmTracking();
            }
            catch (Exception ex)
            {
                WriteLog($"Error in CheckForThreadReverts: {ex.Message}");
            }
        }

        private static bool IsBlacklisted(int threadId)
        {
            return _blacklistedThreads.Contains(threadId);
        }

        private static void MarkThreadFailure(int threadId, string threadName)
        {
            if (!_threadRevertCounts.ContainsKey(threadId))
                _threadRevertCounts[threadId] = 0;

            _threadRevertCounts[threadId]++;

            if (_threadRevertCounts[threadId] >= MAX_REVERT_ATTEMPTS)
            {
                if (!_blacklistedThreads.Contains(threadId))
                {
                    _blacklistedThreads.Add(threadId);
                    WriteColored($"[IGNORED] Thread {threadName} ({threadId}) is fighting back. Giving up to prevent loop.", ConsoleColor.DarkRed);
                    WriteLog($"Thread blacklisted: {threadName} ({threadId}) - exceeded max revert attempts");
                }
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
                                if (IsBlacklisted(thread.Id)) continue;

                                string threadName = GetThreadName(thread.Id);
                                
                                string threadKey = $"{process.Id}_{thread.Id}";
                                if (_lastDwmSetTime.TryGetValue(threadKey, out DateTime lastSet) && 
                                    (now - lastSet).TotalMilliseconds < DWM_FORCE_INTERVAL)
                                    continue;

                                if (threadName == "DWM Kernel Sensor Thread")
                                {
                                    OptimizeDwmThread(thread, -2, "1,3,5,7", threadKey, threadName);
                                }
                                else if (threadName == "DWM Master Input Thread")
                                {
                                    OptimizeDwmThread(thread, 15, "0,2,4,6", threadKey, threadName);
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

        private static void OptimizeDwmThread(ProcessThread thread, int targetPriority, string affinityStr, string key, string name)
        {
             IntPtr threadHandle = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION, false, (uint)thread.Id);
             if (threadHandle != IntPtr.Zero)
             {
                 try
                 {
                     int currentPrio = GetThreadPriority(threadHandle);
                     
                     if (currentPrio != targetPriority)
                     {
                         SetThreadPriority(threadHandle, targetPriority);
                         SetThreadPriorityBoost(threadHandle, true);
                         IntPtr affinity = ParseAffinity(affinityStr);
                         SetThreadAffinityMask(threadHandle, affinity);
                         _lastDwmSetTime[key] = DateTime.Now;

                         int checkPrio = GetThreadPriority(threadHandle);
                         if (checkPrio != targetPriority)
                         {
                             MarkThreadFailure(thread.Id, name);
                         }
                     }
                 }
                 finally
                 {
                     CloseHandle(threadHandle);
                 }
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
                        if (_permanentlyTerminatedThreads.Contains(thread.Id) || IsBlacklisted(thread.Id))
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
                                WriteVerbose($"Thread priority reverted: {processName} -> {threadName} ({currentPriority} -> {targetPriority})", ConsoleColor.Yellow);
                                WriteLog($"Thread priority reverted: {processName} -> {threadName} ({currentPriority} -> {targetPriority})");
                                
                                bool success = NativeSetThreadPriority(thread.Id, targetPriority);
                                
                                int verifyPriority = NativeGetThreadPriority(thread.Id);
                                
                                if (success && verifyPriority == targetPriority)
                                {
                                    WriteColored($"Restored priority for: {threadName}", ConsoleColor.Green);
                                    WriteLog($"Restored priority for: {threadName}");
                                    if (_threadRevertCounts.ContainsKey(thread.Id)) _threadRevertCounts.Remove(thread.Id);
                                }
                                else
                                {
                                    WriteColored($"FAILED to restore priority for: {threadName} (Actual: {verifyPriority})", ConsoleColor.Red);
                                    WriteLog($"FAILED to restore priority for: {threadName} (Actual: {verifyPriority})");
                                    MarkThreadFailure(thread.Id, threadName);
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

                    bool shouldDisableBoost = (processConfig.DisableBoost || 
                                             _disableBoostProcesses.Contains(process.ProcessName)) &&
                                             !_disableBoostApplied.Contains(process.Id);

                    if (shouldDisableBoost && (processConfig.Priority != 0 || processConfig.Affinity != "ALL"))
                    {
                        SetProcessPriorityBoost(processHandle, true);
                        _disableBoostApplied.Add(process.Id);
                        WriteVerbose($"Disabled priority boost for: {process.ProcessName}", ConsoleColor.DarkCyan);
                        WriteLog($"Disabled priority boost for: {process.ProcessName}");
                    }
                }
                finally
                {
                    CloseHandle(processHandle);
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error applying process settings for {process.ProcessName}: {ex.Message}");
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
                                WriteLog($"Suspended thread: {processName} -> {threadName}");
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
                            WriteLog($"Terminated thread: {processName} -> {threadName}");
                        }
                        else
                        {
                            WriteColored($"Failed to terminate thread: {processName} -> {threadName}", ConsoleColor.Red);
                            WriteLog($"Failed to terminate thread: {processName} -> {threadName}");
                        }
                        CloseHandle(threadHandle);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteColored($"Error in thread operation {operation} for {threadName}: {ex.Message}", ConsoleColor.Red);
                WriteLog($"Error in thread operation {operation} for {threadName}: {ex.Message}");
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
                                if (_permanentlyTerminatedThreads.Contains(thread.Id) || IsBlacklisted(thread.Id))
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
                                        WriteLog($"Applied delayed optimization: {processName} -> {threadName}");
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }

                        if (foundThreads.Count > 0)
                        {
                            foreach (string foundThread in foundThreads)
                            {
                                string threadKey = $"{processId}_{foundThread}";
                                
                                // Reset thread watcher when thread is found
                                _threadWatcherActive.Remove(threadKey);
                                
                                if (_threadWatcherCycles.ContainsKey(processName) && 
                                    _threadWatcherCycles[processName].ContainsKey(foundThread))
                                {
                                    _threadWatcherCycles[processName].Remove(foundThread);
                                }
                            }
                            
                            WriteVerbose($"Thread watcher reset: {foundThreads.Count} threads found in {processName}", ConsoleColor.DarkGreen);
                            WriteLog($"Thread watcher reset: {foundThreads.Count} threads found in {processName}");
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
            
            _threadMonitoringCycles.Remove(processId);
            _appliedModuleThreads.Remove(processId);
            _disableBoostApplied.Remove(processId);
        }
        #endregion

        #region Utility Methods
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
        #endregion

        #region Configuration Loading
        static void LoadConfigurations()
        {
            LoadConfigFile(GAME_CONFIG_FILE, _gameConfigs, true);
            LoadConfigFile(SYSTEM_CONFIG_FILE, _systemConfigs, false);
        }

        static void LoadConfigFile(string fileName, Dictionary<string, ProcessConfig> configs, bool isGame)
        {
            if (!File.Exists(fileName))
            {
                throw new FileNotFoundException($"Configuration file not found: {fileName}");
            }

            string[] lines = File.ReadAllLines(fileName);
            ProcessConfig? currentProcessConfig = null;
            string? currentProcess = null;
            bool inDisableBoostSection = false;

            string type = isGame ? "Game" : "System";
            WriteColored($"Loading {type} configuration: {fileName}", ConsoleColor.Cyan);

            foreach (string line in lines)
            {
                string trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                    continue;

                if (trimmed.Equals("[DisableBoost]", StringComparison.OrdinalIgnoreCase))
                {
                    inDisableBoostSection = true;
                    currentProcessConfig = null;
                    currentProcess = null;
                    WriteVerbose($"Loading DisableBoost section from {fileName}", ConsoleColor.Cyan);
                    WriteLog($"Loading DisableBoost section from {fileName}");
                    continue;
                }

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inDisableBoostSection = false;
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
                        WriteVerbose($"Loaded Process Config: {currentProcess} (Prio:{currentProcessConfig.Priority}, Aff:{currentProcessConfig.Affinity}, Boost:{!currentProcessConfig.DisableBoost})", ConsoleColor.Cyan);
                        WriteLog($"Loaded Process Config: {currentProcess}");
                    }
                    continue;
                }

                if (inDisableBoostSection)
                {
                    string processName = trimmed.Replace(".exe", "").Trim();
                    if (!string.IsNullOrEmpty(processName))
                    {
                        _disableBoostProcesses.Add(processName);
                        WriteVerbose($"  DisableBoost: {processName}", ConsoleColor.DarkYellow);
                        WriteLog($"  DisableBoost: {processName}");
                    }
                    continue;
                }

                if (currentProcess == null || currentProcessConfig == null)
                    continue;

                if (trimmed.StartsWith("module=", StringComparison.OrdinalIgnoreCase))
                {
                    string moduleLine = trimmed.Substring(7).Trim();
                    string[] moduleParts = moduleLine.Split(',');
                    
                    if (moduleParts.Length >= 2)
                    {
                        string moduleName = moduleParts[0].Trim();
                        if (int.TryParse(moduleParts[1].Trim(), out int modulePriority))
                        {
                            currentProcessConfig.Modules[moduleName] = modulePriority;
                            WriteVerbose($"  Module Config: {moduleName} -> Priority {modulePriority}", ConsoleColor.DarkCyan);
                            WriteLog($"  Module Config: {moduleName} -> Priority {modulePriority}");
                        }
                    }
                    continue;
                }

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
                        }
                        else
                        {
                            WriteColored($"[WARNING] Invalid priority value {priority} for thread {threadName}", ConsoleColor.Yellow);
                            WriteLog($"[WARNING] Invalid priority value {priority} for thread {threadName}");
                        }
                    }
                }
            }

            WriteColored($"\nтЬУ LOADED {configs.Count} PROCESS CONFIGURATIONS FROM {fileName}", ConsoleColor.Green);
            WriteColored($"тЬУ LOADED {_disableBoostProcesses.Count} DISABLEBOOST PROCESSES FROM {fileName}", ConsoleColor.Green);
            WriteLog($"LOADED {configs.Count} PROCESS CONFIGURATIONS FROM {fileName}");
            WriteLog($"LOADED {_disableBoostProcesses.Count} DISABLEBOOST PROCESSES FROM {fileName}");
            Console.WriteLine();
        }
        #endregion

        #region Process Checking & Optimization
        static void CheckForConfiguredProcesses()
        {
            bool processedGameThisLoop = CheckProcesses(_gameConfigs, true);

            if (processedGameThisLoop && !_gameOptimized)
            {
                _gameOptimized = true;
                WriteColored("GAME OPTIMIZATION ACTIVATED - System process monitoring now enabled", ConsoleColor.Magenta);
                WriteLog("GAME OPTIMIZATION ACTIVATED - System process monitoring now enabled");
            }

            if (_gameOptimized || _forceSystemOptimizations)
            {
                CheckProcesses(_systemConfigs, false);
            }
        }

        static bool CheckProcesses(Dictionary<string, ProcessConfig> configs, bool isGame)
        {
            bool anyProcessed = false;

            foreach (string processName in configs.Keys)
            {
                Process[] processes = Process.GetProcessesByName(processName);
                if (processes.Length == 0) continue;

                // Initialize process watcher state
                if (!_processWatcherActive.ContainsKey(processName))
                {
                    _processWatcherActive[processName] = false;
                    _processWatcherCycles[processName] = 0;
                }

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
                    {
                        ApplyModuleOptimizations(targetProcess, processName, configs);
                        continue;
                    }

                    string type = isGame ? "Game" : "System";
                    WriteColored($"\n{type} MAIN process detected: {processName} (PID {targetProcess.Id}) at {DateTime.Now:HH:mm:ss}", ConsoleColor.Magenta);
                    WriteLog($"{type} MAIN process detected: {processName} (PID {targetProcess.Id})");
                    
                    if (isGame)
                    {
                        _gameProcessId = targetProcess.Id;
                        
                        WriteVerbose("Waiting 10 seconds for process initialization...", ConsoleColor.Cyan);
                        WriteLog("Waiting 10 seconds for process initialization...");
                        Thread.Sleep(10000);
                        
                        if (targetProcess.HasExited)
                        {
                            WriteColored("Game process exited during initialization wait.", ConsoleColor.Red);
                            WriteLog("Game process exited during initialization wait.");
                            continue;
                        }
                    }

                    ApplyProcessSettings(targetProcess, processName, configs);
                    EnumerateProcessThreads(targetProcess, processName, configs);
                    
                    ApplyModuleOptimizations(targetProcess, processName, configs);

                    WriteColored("Thread optimization applied.", ConsoleColor.Green);
                    WriteLog("Thread optimization applied.");
                    _processedProcesses.Add(targetProcess.Id);
                    anyProcessed = true;
                    
                    _threadMonitoringCycles[targetProcess.Id] = 0;
                    
                    targetProcess.EnableRaisingEvents = true;
                    targetProcess.Exited += (sender, e) =>
                    {
                        WriteColored($"\n{type} process {processName} (PID {targetProcess.Id}) exited.", ConsoleColor.DarkYellow);
                        WriteLog($"{type} process {processName} (PID {targetProcess.Id}) exited.");
                        CleanupProcessData(targetProcess.Id);
                        _processedProcesses.Remove(targetProcess.Id);
                        targetProcess.Dispose();
                        if (isGame)
                        {
                            _gameOptimized = false;
                            _gameProcessId = -1;
                            WriteColored("GAME OPTIMIZATION DEACTIVATED - System process monitoring paused", ConsoleColor.DarkYellow);
                            WriteLog("GAME OPTIMIZATION DEACTIVATED - System process monitoring paused");
                        }
                    };
                }
                catch (Exception ex)
                {
                    WriteColored($"ERROR: Processing {processName} (PID {targetProcess.Id}): {ex.Message}", ConsoleColor.Red);
                    WriteLog($"ERROR: Processing {processName} (PID {targetProcess.Id}): {ex.Message}");
                }
            }

            return anyProcessed;
        }

        private static void ApplyModuleOptimizations(Process process, string processName, Dictionary<string, ProcessConfig> configs)
        {
            try
            {
                if (!configs.TryGetValue(processName, out ProcessConfig? processConfig) || 
                    processConfig.Modules.Count == 0)
                    return;

                if (!_appliedModuleThreads.TryGetValue(process.Id, out HashSet<string>? appliedModules))
                {
                    appliedModules = new HashSet<string>();
                    _appliedModuleThreads[process.Id] = appliedModules;
                }

                var modules = GetProcessModules(process);
                if (modules == null) return;

                foreach (var module in modules)
                {
                    string moduleName = Path.GetFileName(module).ToLower();
                    
                    foreach (var configModule in processConfig.Modules)
                    {
                        string configModuleName = configModule.Key.ToLower();
                        
                        if (moduleName.Contains(configModuleName) && !appliedModules.Contains(moduleName))
                        {
                            int targetPriority = configModule.Value;
                            WriteVerbose($"Applying module priority: {moduleName} -> {targetPriority}", ConsoleColor.DarkYellow);
                            WriteLog($"Applying module priority: {moduleName} -> {targetPriority}");
                            
                            foreach (ProcessThread thread in process.Threads)
                            {
                                try
                                {
                                    if (_permanentlyTerminatedThreads.Contains(thread.Id) || IsBlacklisted(thread.Id))
                                        continue;

                                    NativeSetThreadPriority(thread.Id, targetPriority);
                                }
                                catch
                                {
                                }
                            }
                            
                            appliedModules.Add(moduleName);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error applying module optimizations for {processName}: {ex.Message}");
            }
        }

        private static List<string>? GetProcessModules(Process process)
        {
            try
            {
                IntPtr processHandle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, (uint)process.Id);
                if (processHandle == IntPtr.Zero)
                    return null;

                try
                {
                    IntPtr[] modules = new IntPtr[1024];
                    uint cbNeeded;
                    
                    if (EnumProcessModulesEx(processHandle, modules, (uint)(IntPtr.Size * modules.Length), out cbNeeded, LIST_MODULES_ALL))
                    {
                        int moduleCount = (int)(cbNeeded / IntPtr.Size);
                        var moduleList = new List<string>();
                        
                        for (int i = 0; i < moduleCount; i++)
                        {
                            StringBuilder modulePath = new StringBuilder(260);
                            if (GetModuleFileNameEx(processHandle, modules[i], modulePath, (uint)modulePath.Capacity) > 0)
                            {
                                moduleList.Add(modulePath.ToString());
                            }
                        }
                        
                        return moduleList;
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
            
            return null;
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

        static void ApplyProcessSettings(Process process, string processName, Dictionary<string, ProcessConfig> configs)
        {
            try
            {
                if (!configs.TryGetValue(processName, out ProcessConfig? processConfig))
                    return;

                ApplyProcessLevelSettings(process, processConfig!);
            }
            catch (Exception ex)
            {
                WriteColored($"ERROR: Failed to apply process settings: {ex.Message}", ConsoleColor.Red);
                WriteLog($"ERROR: Failed to apply process settings: {ex.Message}");
            }
        }

        static void EnumerateProcessThreads(Process process, string processName, Dictionary<string, ProcessConfig> configs)
        {
            try
            {
                if (!configs.TryGetValue(processName, out ProcessConfig? processConfig))
                    return;

                ProcessThreadCollection threads = process.Threads;
                
                WriteVerbose($"Total threads found: {threads.Count}", ConsoleColor.Cyan);
                WriteLog($"Total threads found: {threads.Count} for {processName}");
                
                int modifiedThreads = 0;
                HashSet<string> foundThreads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> configuredThreads = new HashSet<string>(processConfig!.Threads.Keys, StringComparer.OrdinalIgnoreCase);
                
                // Only show detailed thread table in verbose mode
                if (_verboseLogging)
                {
                    Console.WriteLine($"{"Thread ID",-10} {"Config Prio",-10} {"Current",-10} {"Affinity",-15} {"Thread Name",-40} {"Action"}");
                    Console.WriteLine(new string('-', 120));
                }
                
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
                    }
                }
                
                var missingThreads = configuredThreads.Except(foundThreads, StringComparer.OrdinalIgnoreCase).ToList();
                if (missingThreads.Any())
                {
                    WriteVerbose($"Some configured threads not found initially ({missingThreads.Count}). Will keep watching for them.", ConsoleColor.Yellow);
                    WriteLog($"Some configured threads not found initially ({missingThreads.Count}) for {processName}");
                    
                    lock (_pendingThreads)
                    {
                        _pendingThreads[process.Id] = new HashSet<string>(missingThreads, StringComparer.OrdinalIgnoreCase);
                    }
                    
                    _threadMonitoringCycles[process.Id] = 0;
                }
                
                WriteVerbose($"Summary: Modified {modifiedThreads} threads, {missingThreads.Count} pending", ConsoleColor.Cyan);
                WriteLog($"Summary for {processName}: Modified {modifiedThreads} threads, {missingThreads.Count} pending");
            }
            catch (Exception ex)
            {
                WriteColored($"ERROR: Failed to enumerate threads for {processName}: {ex.Message}", ConsoleColor.Red);
                WriteLog($"ERROR: Failed to enumerate threads for {processName}: {ex.Message}");
            }
        }

        static bool? DisplayThreadInfo(ProcessThread thread, int processId, Dictionary<string, ThreadConfig> threadConfigs, HashSet<string> processedThreadNames)
        {
            int threadId = thread.Id;
            List<string> actions = new List<string>();
            bool wasModified = false;
            int currentPriority = NativeGetThreadPriority(threadId);
            if (currentPriority == -999) return false;
            
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
                        int newPrio = NativeGetThreadPriority(threadId);
                        if (newPrio == targetPriority)
                        {
                            actions.Add($"PRIORITY: {currentPrioritySymbol}->{PriorityMap[config.Priority]}");
                            wasModified = true;
                        }
                        else
                        {
                            actions.Add($"PRIORITY FAIL: {currentPrioritySymbol}->{PriorityMap[config.Priority]} (Got {newPrio})");
                        }
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
                        if (threadHandle != IntPtr.Zero)
                        {
                            SetThreadAffinityMask(threadHandle, targetAffinity);
                            actions.Add($"AFFINITY: {config.Affinity}");
                            wasModified = true;
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

            // Only show thread details in verbose mode
            if (_verboseLogging)
            {
                ConsoleColor priorityColor = config.Priority switch
                {
                    300 => ConsoleColor.DarkRed,
                    200 => ConsoleColor.Red,
                    15 => ConsoleColor.Red,
                    2 => ConsoleColor.Yellow,
                    1 => ConsoleColor.Yellow,
                    0 => ConsoleColor.Green,
                    _ => ConsoleColor.White
                };

                ConsoleColor actionColor = action.Contains("FAIL") ? ConsoleColor.Red :
                                           action == "NO CHANGE" ? ConsoleColor.Gray :
                                           ConsoleColor.Green;

                Console.Write($"{threadId,-10} ");
                Console.ForegroundColor = priorityColor;
                Console.Write($"{config.Priority,-10} ");
                Console.ResetColor();
                Console.Write($"{currentPrioritySymbol,-10} {config.Affinity,-15} {threadName,-40} ");
                Console.ForegroundColor = actionColor;
                Console.WriteLine(action);
                Console.ResetColor();
            }
            
            processedThreadNames.Add(threadName);
            
            return wasModified;
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
        #endregion
    }
}