using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ProcRipper.Core;
using ProcRipper.Core.Native;
using ProcRipperConfig.UI.WinForms;

namespace ProcRipperConfig
{
    public partial class MainForm : Form
    {
        private SplitContainer mainSplitContainer = null!;
        private ListView processListView = null!;
        private ImageList processIcons = null!;
        private TextBox searchTextBox = null!;
        private TabControl mainTabControl = null!;
        private TabPage gameConfigTab = null!;
        private TabPage systemConfigTab = null!;
        private TabPage globalConfigTab = null!;

        private ListView? _memoryLimitsListView;
        private TabControl processDetailsTabControl = null!;
        private TabPage processSettingsTab = null!;
        private TabPage threadsTab = null!;
        private TabPage modulesTab = null!;
        private ListView threadsListView = null!;
        private ListView modulesListView = null!;
        private Button saveButton = null!;
        private Button loadButton = null!;
        private Button refreshProcessesButton = null!;
        Button restartWithMemCapButton = null!;
        private Button exportConfigButton = null!;
        private Button importConfigButton = null!;
        private Label statusLabel = null!;

        private readonly Dictionary<string, ProcessConfig> gameConfigs = new Dictionary<string, ProcessConfig>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProcessConfig> systemConfigs = new Dictionary<string, ProcessConfig>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> disableBoostProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ProcessConfig? selectedProcessConfig;

        public MainForm()
        {
            try
            {
                InitializeComponent();

                if (mainSplitContainer != null)
                {
                    mainSplitContainer.Dock = DockStyle.Fill;
                }

                this.Visible = true;
                LoadConfigurationFiles();
                RefreshProcessList();
                RefreshConfigTabs();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing ProcRipperConfig: {ex.Message}\n\n{ex.StackTrace}", "Initialization Error");
                throw;
            }
        }

        private void InitializeComponent()
        {
            this.Text = "ProcRipper Configuration Tool v3.0.0";
            this.Size = new Size(1200, 800);
            this.MinimumSize = new Size(1000, 650);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            const int ButtonHeight = 34;
            const int TextBoxHeight = 26;
            const int Pad = 10;

            mainSplitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                IsSplitterFixed = false,
                FixedPanel = FixedPanel.Panel1
            };

            var leftRoot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(Pad)
            };
            leftRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var searchRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true
            };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            searchTextBox = new TextBox
            {
                Dock = DockStyle.Top,
                Height = TextBoxHeight,
                PlaceholderText = "Search processes..."
            };
            searchTextBox.TextChanged += SearchTextBox_TextChanged;

            refreshProcessesButton = new Button
            {
                Text = "Refresh",
                Height = ButtonHeight,
                AutoSize = true,
                Padding = new Padding(14, 6, 14, 6),
                Margin = new Padding(Pad, 0, 0, 0)
            };
            refreshProcessesButton.Click += RefreshProcessesButton_Click;

            searchRow.Controls.Add(searchTextBox, 0, 0);
            searchRow.Controls.Add(refreshProcessesButton, 1, 0);

            processIcons = new ImageList
            {
                ImageSize = new Size(32, 32),
                ColorDepth = ColorDepth.Depth32Bit
            };

            processListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                LargeImageList = processIcons,
                SmallImageList = processIcons
            };
            processListView.Columns.Add("Process Name", 170);
            processListView.Columns.Add("PID", 70);
            processListView.Columns.Add("Memory", 90);
            processListView.SelectedIndexChanged += ProcessListView_SelectedIndexChanged;
            processListView.DoubleClick += ProcessListView_DoubleClick;

            leftRoot.Controls.Add(searchRow, 0, 0);
            leftRoot.Controls.Add(processListView, 0, 1);

            mainSplitContainer.Panel1.Controls.Add(leftRoot);

            var rightRoot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(Pad)
            };
            rightRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rightRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rightRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var rightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                IsSplitterFixed = false,
                FixedPanel = FixedPanel.None
            };

            mainTabControl = new TabControl { Dock = DockStyle.Fill };

            gameConfigTab = new TabPage("GAME_PRIORITY.GCFG");
            CreateConfigTab(gameConfigTab, gameConfigs, true);

            systemConfigTab = new TabPage("PROC_PRIORITY.GCFG");
            CreateConfigTab(systemConfigTab, systemConfigs, false);

            globalConfigTab = new TabPage("ProcRipper.GCFG");
            CreateGlobalConfigTab(globalConfigTab);

            mainTabControl.TabPages.AddRange(new TabPage[] { gameConfigTab, systemConfigTab, globalConfigTab });

            processDetailsTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Visible = false
            };

            processSettingsTab = new TabPage("Process Settings");
            CreateProcessSettingsTab();

            threadsTab = new TabPage("Thread Management");
            CreateThreadManagementTab();

            modulesTab = new TabPage("Module Management");
            CreateModuleManagementTab();

            processDetailsTabControl.TabPages.AddRange(new TabPage[] { processSettingsTab, threadsTab, modulesTab });

            rightSplit.Panel1.Controls.Add(mainTabControl);
            rightSplit.Panel2.Controls.Add(processDetailsTabControl);

            var buttonRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, Pad, 0, 0)
            };

            saveButton = new Button
            {
                Text = "Save All",
                Height = ButtonHeight,
                AutoSize = true,
                Padding = new Padding(16, 6, 16, 6),
                BackColor = Color.LightGreen
            };
            saveButton.Click += SaveButton_Click;

            loadButton = new Button
            {
                Text = "Reload",
                Height = ButtonHeight,
                AutoSize = true,
                Padding = new Padding(16, 6, 16, 6)
            };
            loadButton.Click += LoadButton_Click;

            restartWithMemCapButton = new Button
            {
                Text = "Restart && Apply Memory Cap",
                Height = ButtonHeight,
                AutoSize = true,
                Padding = new Padding(16, 6, 16, 6),
                BackColor = Color.LightGoldenrodYellow
            };
            restartWithMemCapButton.Click += RestartWithMemoryCapButton_Click;

            exportConfigButton = new Button
            {
                Text = "Export Config (ZIP)",
                Height = ButtonHeight,
                AutoSize = true,
                Padding = new Padding(16, 6, 16, 6)
            };
            exportConfigButton.Click += ExportConfigButton_Click;

            importConfigButton = new Button
            {
                Text = "Import Config (ZIP)",
                Height = ButtonHeight,
                AutoSize = true,
                Padding = new Padding(16, 6, 16, 6)
            };
            importConfigButton.Click += ImportConfigButton_Click;

            buttonRow.Controls.Add(saveButton);
            buttonRow.Controls.Add(loadButton);
            buttonRow.Controls.Add(exportConfigButton);
            buttonRow.Controls.Add(importConfigButton);
            buttonRow.Controls.Add(restartWithMemCapButton);

            statusLabel = new Label
            {
                Text = "Ready",
                Dock = DockStyle.Fill,
                Height = 25,
                BorderStyle = BorderStyle.Fixed3D
            };

            rightRoot.Controls.Add(rightSplit, 0, 0);
            rightRoot.Controls.Add(buttonRow, 0, 1);
            rightRoot.Controls.Add(statusLabel, 0, 2);

            mainSplitContainer.Panel2.Controls.Add(rightRoot);

            this.Controls.Add(mainSplitContainer);

            this.Shown += (_, __) =>
            {
                try
                {
                    mainSplitContainer.Panel1MinSize = 320;
                    mainSplitContainer.Panel2MinSize = 600;

                    int desired = 360;
                    int min = mainSplitContainer.Panel1MinSize;
                    int max = Math.Max(min, mainSplitContainer.Width - mainSplitContainer.Panel2MinSize);

                    if (desired < min) desired = min;
                    if (desired > max) desired = max;

                    mainSplitContainer.SplitterDistance = desired;
                }
                catch
                {
                }

                try
                {
                    rightSplit.Panel1MinSize = 250;
                    rightSplit.Panel2MinSize = 220;

                    int desiredH = 360;
                    int minH = rightSplit.Panel1MinSize;
                    int maxH = Math.Max(minH, rightSplit.Height - rightSplit.Panel2MinSize);

                    if (desiredH < minH) desiredH = minH;
                    if (desiredH > maxH) desiredH = maxH;

                    rightSplit.SplitterDistance = desiredH;
                }
                catch
                {
                }
            };
        }

        private void CreateProcessSettingsTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

            var processPriorityLabel = new Label { Text = "Priority:", Location = new Point(10, 10), AutoSize = true };
            var processPriorityNumeric = new NumericUpDown
            {
                Location = new Point(100, 8),
                Size = new Size(80, 25),
                Minimum = -15,
                Maximum = 15,
                Value = 0,
                Tag = "priority"
            };

            var processAffinityLabel = new Label { Text = "Affinity:", Location = new Point(10, 45), AutoSize = true };
            var processAffinityTextBox = new TextBox
            {
                Location = new Point(100, 43),
                Size = new Size(150, 25),
                Text = "ALL",
                Tag = "affinity"
            };

            var processBoostCheckBox = new CheckBox
            {
                Text = "Disable Priority Boost",
                Location = new Point(10, 80),
                AutoSize = true,
                Tag = "boost"
            };

            var gpuPriorityLabel = new Label { Text = "GPU Priority:", Location = new Point(10, 110), AutoSize = true };
            var gpuPriorityComboBox = new ComboBox
            {
                Location = new Point(100, 108),
                Size = new Size(150, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Tag = "gpu_priority"
            };
            gpuPriorityComboBox.Items.AddRange(new object[] { "None", "Very Low", "Low", "Normal", "High" });
            gpuPriorityComboBox.SelectedIndex = 0;

            var applyProcessSettingsButton = new Button
            {
                Text = "Apply Settings",
                Location = new Point(10, 150),
                Size = new Size(120, 30),
                BackColor = Color.LightBlue
            };
            applyProcessSettingsButton.Click += (s, e) => ApplyProcessSettings(processPriorityNumeric, processAffinityTextBox, processBoostCheckBox, gpuPriorityComboBox);

            panel.Controls.AddRange(new Control[] {
                processPriorityLabel, processPriorityNumeric,
                processAffinityLabel, processAffinityTextBox,
                processBoostCheckBox, gpuPriorityLabel, gpuPriorityComboBox, applyProcessSettingsButton
            });
            processSettingsTab.Controls.Add(panel);
        }

        private void CreateThreadManagementTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill };

            var threadSearchTextBox = new TextBox
            {
                Location = new Point(10, 10),
                Size = new Size(200, 25),
                PlaceholderText = "Search threads..."
            };
            threadSearchTextBox.TextChanged += (s, e) => FilterThreads(threadSearchTextBox.Text);

            threadsListView = new ListView
            {
                Location = new Point(10, 45),
                Size = new Size(550, 300),
                View = View.Details,
                FullRowSelect = true
            };
            threadsListView.Columns.Add("Thread Name", 150);
            threadsListView.Columns.Add("Priority", 80);
            threadsListView.Columns.Add("Affinity", 100);
            threadsListView.Columns.Add("Disable Boost", 100);

            var threadsContextMenu = new ContextMenuStrip();
            var addThreadItem = new ToolStripMenuItem("Add Thread");
            addThreadItem.Click += (s, e) => AddThreadToProcess();
            var editThreadItem = new ToolStripMenuItem("Edit Thread");
            editThreadItem.Click += (s, e) => EditThreadInProcess();
            var removeThreadItem = new ToolStripMenuItem("Remove Thread");
            removeThreadItem.Click += (s, e) => RemoveThreadFromProcess();

            threadsContextMenu.Items.AddRange(new ToolStripItem[] { addThreadItem, editThreadItem, removeThreadItem });
            threadsListView.ContextMenuStrip = threadsContextMenu;

            threadsListView.DoubleClick += (s, e) => EditThreadInProcess();

            panel.Controls.AddRange(new Control[] { threadSearchTextBox, threadsListView });
            threadsTab.Controls.Add(panel);
        }

        private void CreateModuleManagementTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill };

            var moduleSearchTextBox = new TextBox
            {
                Location = new Point(10, 10),
                Size = new Size(200, 25),
                PlaceholderText = "Search modules..."
            };
            moduleSearchTextBox.TextChanged += (s, e) => FilterModules(moduleSearchTextBox.Text);

            modulesListView = new ListView
            {
                Location = new Point(10, 45),
                Size = new Size(550, 300),
                View = View.Details,
                FullRowSelect = true
            };
            modulesListView.Columns.Add("Module Name", 200);
            modulesListView.Columns.Add("Priority", 100);

            var modulesContextMenu = new ContextMenuStrip();
            var addModuleItem = new ToolStripMenuItem("Add Module");
            addModuleItem.Click += (s, e) => AddModuleToProcess();
            var editModuleItem = new ToolStripMenuItem("Edit Module");
            editModuleItem.Click += (s, e) => EditModuleInProcess();
            var removeModuleItem = new ToolStripMenuItem("Remove Module");
            removeModuleItem.Click += (s, e) => RemoveModuleFromProcess();

            modulesContextMenu.Items.AddRange(new ToolStripItem[] { addModuleItem, editModuleItem, removeModuleItem });
            modulesListView.ContextMenuStrip = modulesContextMenu;

            modulesListView.DoubleClick += (s, e) => EditModuleInProcess();

            panel.Controls.AddRange(new Control[] { moduleSearchTextBox, modulesListView });
            modulesTab.Controls.Add(panel);
        }

        private void CreateConfigTab(TabPage tab, Dictionary<string, ProcessConfig> configs, bool isGame)
        {
            ListView configListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true
            };
            configListView.Columns.Add("Process Name", 150);
            configListView.Columns.Add("Priority", 80);
            configListView.Columns.Add("Affinity", 80);
            configListView.Columns.Add("Disable Boost", 100);

            ContextMenuStrip contextMenu = new ContextMenuStrip();
            ToolStripMenuItem addItem = new ToolStripMenuItem("Add Process");
            addItem.Click += (s, e) => AddProcessToConfig(configListView, configs, isGame);
            ToolStripMenuItem editItem = new ToolStripMenuItem("Edit Process");
            editItem.Click += (s, e) => EditProcessInConfig(configListView, configs);
            ToolStripMenuItem removeItem = new ToolStripMenuItem("Remove Process");
            removeItem.Click += (s, e) => RemoveProcessFromConfig(configListView, configs);

            contextMenu.Items.AddRange(new ToolStripItem[] { addItem, editItem, new ToolStripSeparator(), removeItem });
            configListView.ContextMenuStrip = contextMenu;

            configListView.DoubleClick += (s, e) =>
            {
                if (configListView.SelectedItems.Count == 0) return;

                var selectedItem = configListView.SelectedItems[0];
                string processName = selectedItem.Text;

                if (!configs.TryGetValue(processName, out var cfg) || cfg == null)
                {
                    cfg = new ProcessConfig
                    {
                        Priority = 0,
                        Affinity = "ALL",
                        DisableBoost = false
                    };
                    configs[processName] = cfg;
                }

                using (var editor = new ProcRipperConfig.UI.WinForms.ProcessDetailForm(
                    processId: 0,
                    processName: processName,
                    isGameProfile: isGame,
                    gameConfigs: gameConfigs,
                    systemConfigs: systemConfigs,
                    config: cfg))
                {
                    var result = editor.ShowDialog(this);
                    if (result == DialogResult.OK)
                    {
                        RefreshConfigTabs();
                    }
                }
            };

            tab.Controls.Add(configListView);
        }

        private void CreateGlobalConfigTab(TabPage tab)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

            int yOffset = 10;


            CheckBox networkThrottleCheckBox = new CheckBox
            {
                Text = "Enable Network Throttling",
                Location = new Point(10, yOffset),
                Checked = ConfigLoader.NetworkThrottleEnabled,
                AutoSize = true
            };
            yOffset += 30;

            CheckBox autoIdleCheckBox = new CheckBox
            {
                Text = "Enable Auto Idle Prevention",
                Location = new Point(10, yOffset),
                Checked = ConfigLoader.AutoIdleEnabled,
                AutoSize = true
            };
            yOffset += 30;

            CheckBox verboseStartupCheckBox = new CheckBox
            {
                Text = "Enable Verbose Startup",
                Location = new Point(10, yOffset),
                Checked = ConfigLoader.VerboseStartup,
                AutoSize = true
            };
            yOffset += 30;

            CheckBox autoApplyMemoryCapsOnGameLaunchCheckBox = new CheckBox
            {
                Text = "Auto-apply memory caps on GAME launch (checked apps only)",
                Location = new Point(10, yOffset),
                Checked = ConfigLoader.AutoApplyMemoryCapsOnGameLaunch,
                AutoSize = true
            };
            yOffset += 30;

            CheckBox promptBeforeAutoMemoryCapsOnGameLaunchCheckBox = new CheckBox
            {
                Text = "Prompt before auto restarting apps for memory caps on GAME launch",
                Location = new Point(10, yOffset),
                Checked = ConfigLoader.PromptBeforeAutoMemoryCapsOnGameLaunch,
                AutoSize = true
            };
            yOffset += 60;

            Label networkPriorityLabel = new Label
            {
                Text = "Network Priority:",
                Location = new Point(10, yOffset),
                Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
                AutoSize = true
            };
            yOffset += 25;

            Label networkPriorityDescLabel = new Label
            {
                Text = "Set network priority for background processes:",
                Location = new Point(10, yOffset),
                AutoSize = true
            };
            yOffset += 25;

            RadioButton networkLowRadio = new RadioButton
            {
                Text = "Low",
                Location = new Point(20, yOffset),
                AutoSize = true,
                Tag = -2
            };
            RadioButton networkNormalRadio = new RadioButton
            {
                Text = "Normal",
                Location = new Point(80, yOffset),
                AutoSize = true,
                Tag = 0,
                Checked = true
            };
            RadioButton networkHighRadio = new RadioButton
            {
                Text = "High",
                Location = new Point(150, yOffset),
                AutoSize = true,
                Tag = 1
            };
            RadioButton networkHighestRadio = new RadioButton
            {
                Text = "Highest",
                Location = new Point(210, yOffset),
                AutoSize = true,
                Tag = 2
            };

            yOffset += 40;

            Label memoryLimitsLabel = new Label
            {
                Text = "Per-Application Memory Limits (MB) - Configured Apps:",
                Location = new Point(10, yOffset),
                Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
                AutoSize = true
            };
            yOffset += 25;

            ImageList memoryLimitIcons = new ImageList
            {
                ImageSize = new Size(20, 20),
                ColorDepth = ColorDepth.Depth32Bit
            };

            ListView memoryLimitsListView = new ListView
            {
                Location = new Point(10, yOffset),
                Size = new Size(520, 220),
                View = View.Details,
                FullRowSelect = true,
                CheckBoxes = true,
                SmallImageList = memoryLimitIcons
            };

            _memoryLimitsListView = memoryLimitsListView;
            memoryLimitsListView.Columns.Add("App / Process", 220);
            memoryLimitsListView.Columns.Add("Memory Limit (MB)", 140);
            memoryLimitsListView.Columns.Add("Enabled", 100);

            Icon? defaultIcon = null;
            try { defaultIcon = SystemIcons.Application; } catch { }

            int EnsureIcon(string exeKey)
            {
                string iconKey = exeKey;
                if (memoryLimitIcons.Images.ContainsKey(iconKey))
                    return memoryLimitIcons.Images.IndexOfKey(iconKey);

                try
                {
                    string procName = exeKey.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? exeKey.Substring(0, exeKey.Length - 4)
                        : exeKey;

                    var p = Process.GetProcessesByName(procName).FirstOrDefault();
                    if (p != null)
                    {
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        if (!string.IsNullOrEmpty(path))
                        {
                            Icon? ico = null;
                            try { ico = Icon.ExtractAssociatedIcon(path); } catch { }
                            if (ico != null)
                            {
                                memoryLimitIcons.Images.Add(iconKey, ico.ToBitmap());
                                return memoryLimitIcons.Images.IndexOfKey(iconKey);
                            }
                        }
                    }
                }
                catch
                {
                }

                if (defaultIcon != null)
                {
                    memoryLimitIcons.Images.Add(iconKey, defaultIcon.ToBitmap());
                    return memoryLimitIcons.Images.IndexOfKey(iconKey);
                }

                memoryLimitIcons.Images.Add(iconKey, new Bitmap(20, 20));
                return memoryLimitIcons.Images.IndexOfKey(iconKey);
            }

            foreach (var kvp in ConfigLoader.MemoryLimitMb.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string appName = kvp.Key;
                int limitMb = kvp.Value;

                int imgIndex = EnsureIcon(appName);

                var item = new ListViewItem(appName, imgIndex);
                item.SubItems.Add(limitMb.ToString());
                item.SubItems.Add("Yes");
                item.Checked = true;

                memoryLimitsListView.Items.Add(item);
            }

            yOffset += 230;

            Button addAppMemoryLimitButton = new Button
            {
                Text = "Add App",
                Location = new Point(10, yOffset),
                Size = new Size(120, 30)
            };
            addAppMemoryLimitButton.Click += (s, e) =>
            {
                using (var picker = new ProcRipperConfig.UI.WinForms.AddMemoryLimitFromRunningForm(defaultLimitMb: 2048))
                {
                    if (picker.ShowDialog(this) != DialogResult.OK)
                        return;

                    string keyName = picker.SelectedExeName;
                    int memoryLimit = picker.SelectedLimitMb;

                    if (string.IsNullOrWhiteSpace(keyName) || memoryLimit <= 0)
                        return;

                    string baseKey = keyName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? keyName.Substring(0, keyName.Length - 4)
                        : keyName;

                    if (string.IsNullOrWhiteSpace(baseKey))
                        return;

                    ConfigLoader.MemoryLimitMb[baseKey] = memoryLimit;

                    foreach (ListViewItem item in memoryLimitsListView.Items)
                    {
                        string existingBaseKey = item.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? item.Text.Substring(0, item.Text.Length - 4)
                            : item.Text;

                        if (string.Equals(existingBaseKey, baseKey, StringComparison.OrdinalIgnoreCase))
                        {
                            item.Text = baseKey + ".exe";
                            item.SubItems[1].Text = memoryLimit.ToString();
                            item.SubItems[2].Text = "Yes";
                            item.Checked = true;
                            return;
                        }
                    }

                    string uiExe = baseKey + ".exe";

                    int imageIndex = -1;
                    if (memoryLimitsListView.SmallImageList != null)
                    {
                        try
                        {
                            if (memoryLimitsListView.SmallImageList.Images.ContainsKey(uiExe))
                            {
                                imageIndex = memoryLimitsListView.SmallImageList.Images.IndexOfKey(uiExe);
                            }
                            else
                            {
                                memoryLimitsListView.SmallImageList.Images.Add(uiExe, SystemIcons.Application.ToBitmap());
                                imageIndex = memoryLimitsListView.SmallImageList.Images.IndexOfKey(uiExe);
                            }
                        }
                        catch
                        {
                            imageIndex = -1;
                        }
                    }

                    var newItem = new ListViewItem(uiExe, imageIndex);
                    newItem.SubItems.Add(memoryLimit.ToString());
                    newItem.SubItems.Add("Yes");
                    newItem.Checked = true;
                    memoryLimitsListView.Items.Add(newItem);
                }
            };

            Button removeAppMemoryLimitButton = new Button
            {
                Text = "Remove Selected",
                Location = new Point(140, yOffset),
                Size = new Size(140, 30)
            };
            removeAppMemoryLimitButton.Click += (s, e) =>
            {
                if (memoryLimitsListView.SelectedItems.Count == 0) return;

                var selected = memoryLimitsListView.SelectedItems[0];
                string keyText = (selected.Text ?? "").Trim();

                string baseKey = keyText.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? keyText.Substring(0, keyText.Length - 4)
                    : keyText;

                if (!string.IsNullOrWhiteSpace(baseKey))
                    ConfigLoader.MemoryLimitMb.Remove(baseKey);

                memoryLimitsListView.Items.Remove(selected);
            };

            yOffset += 40;
            panel.Controls.AddRange(new Control[] {
                networkThrottleCheckBox,
                autoIdleCheckBox,
                verboseStartupCheckBox,
                autoApplyMemoryCapsOnGameLaunchCheckBox,
                promptBeforeAutoMemoryCapsOnGameLaunchCheckBox,
                networkPriorityLabel,
                networkPriorityDescLabel,
                networkLowRadio,
                networkNormalRadio,
                networkHighRadio,
                networkHighestRadio,
                memoryLimitsLabel,
                memoryLimitsListView,
                addAppMemoryLimitButton,
                removeAppMemoryLimitButton
            });

            addAppMemoryLimitButton.BringToFront();
            removeAppMemoryLimitButton.BringToFront();

            tab.Controls.Add(panel);
        }

        private void AddMemoryLimit(ListView memoryLimitsListView)
        {
            using (var dialog = new AddMemoryLimitDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string processName = dialog.ProcessName;
                    int memoryLimit = dialog.MemoryLimit;

                    if (string.IsNullOrWhiteSpace(processName))
                        return;

                    string baseKey = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? processName.Substring(0, processName.Length - 4)
                        : processName;

                    ConfigLoader.MemoryLimitMb[baseKey] = memoryLimit;

                    foreach (ListViewItem item in memoryLimitsListView.Items)
                    {
                        string existingBaseKey = item.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? item.Text.Substring(0, item.Text.Length - 4)
                            : item.Text;

                        if (string.Equals(existingBaseKey, baseKey, StringComparison.OrdinalIgnoreCase))
                        {
                            item.Text = baseKey + ".exe";
                            item.SubItems[1].Text = memoryLimit.ToString();
                            item.SubItems[2].Text = "Yes";
                            item.Checked = true;
                            return;
                        }
                    }

                    string uiExe = baseKey + ".exe";

                    int imageIndex = -1;
                    if (memoryLimitsListView.SmallImageList != null)
                    {
                        try
                        {
                            string iconKey = uiExe;
                            if (memoryLimitsListView.SmallImageList.Images.ContainsKey(iconKey))
                            {
                                imageIndex = memoryLimitsListView.SmallImageList.Images.IndexOfKey(iconKey);
                            }
                            else
                            {
                                memoryLimitsListView.SmallImageList.Images.Add(iconKey, SystemIcons.Application.ToBitmap());
                                imageIndex = memoryLimitsListView.SmallImageList.Images.IndexOfKey(iconKey);
                            }
                        }
                        catch
                        {
                            imageIndex = -1;
                        }
                    }

                    var newItem = new ListViewItem(uiExe, imageIndex);
                    newItem.SubItems.Add(memoryLimit.ToString());
                    newItem.SubItems.Add("Yes");
                    newItem.Checked = true;
                    memoryLimitsListView.Items.Add(newItem);
                }
            }
        }

        private void SearchTextBox_TextChanged(object? sender, EventArgs e)
        {
            string searchTerm = searchTextBox.Text.ToLower();

            processListView.Items.Clear();
            foreach (var process in Process.GetProcesses()
                .Where(p => !string.IsNullOrEmpty(p.ProcessName))
                .OrderBy(p => p.ProcessName))
            {
                if (string.IsNullOrEmpty(searchTerm) ||
                    process.ProcessName.ToLower().Contains(searchTerm))
                {
                    try
                    {
                        string memoryUsage = $"{process.WorkingSet64 / 1024 / 1024} MB";
                        var item = new ListViewItem(process.ProcessName);
                        item.SubItems.Add(process.Id.ToString());
                        item.SubItems.Add(memoryUsage);
                        item.Tag = process;
                        processListView.Items.Add(item);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void ProcessListView_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (processListView.SelectedItems.Count == 0)
            {
                processDetailsTabControl.Visible = false;
                selectedProcessConfig = null;
                return;
            }

            var selectedItem = processListView.SelectedItems[0];
            string processName = selectedItem.Text;

            selectedProcessConfig = null;
            if (gameConfigs.ContainsKey(processName))
                selectedProcessConfig = gameConfigs[processName];
            else if (systemConfigs.ContainsKey(processName))
                selectedProcessConfig = systemConfigs[processName];

            if (selectedProcessConfig == null)
            {
                selectedProcessConfig = new ProcessConfig();
            }

            FilterThreads("");

            FilterModules("");

            processDetailsTabControl.Visible = true;
            statusLabel.Text = $"Selected process: {processName} (Double-click to edit threads/modules)";
        }

        private void ProcessListView_DoubleClick(object? sender, EventArgs e)
        {
            if (processListView.SelectedItems.Count == 0) return;

            var selectedItem = processListView.SelectedItems[0];
            string processName = selectedItem.Text;

            int pid = 0;
            if (selectedItem.SubItems.Count > 1)
            {
                int.TryParse(selectedItem.SubItems[1].Text, out pid);
            }

            bool isGameProfile = gameConfigs.ContainsKey(processName);
            bool isSystemProfile = systemConfigs.ContainsKey(processName);

            selectedProcessConfig = null;
            if (isGameProfile)
                selectedProcessConfig = gameConfigs[processName];
            else if (isSystemProfile)
                selectedProcessConfig = systemConfigs[processName];

            if (selectedProcessConfig == null)
            {
                selectedProcessConfig = new ProcessConfig
                {
                    Priority = 0,
                    Affinity = "ALL",
                    DisableBoost = false
                };
            }

            using (var editor = new ProcessDetailForm(
                processId: pid,
                processName: processName,
                isGameProfile: isGameProfile,
                gameConfigs: gameConfigs,
                systemConfigs: systemConfigs,
                config: selectedProcessConfig))
            {
                var result = editor.ShowDialog(this);

                if (result == DialogResult.OK)
                {
                    selectedProcessConfig = isGameProfile
                        ? gameConfigs[processName]
                        : systemConfigs.ContainsKey(processName) ? systemConfigs[processName] : selectedProcessConfig;

                    RefreshConfigTabs();

                    processDetailsTabControl.Visible = true;
                    processDetailsTabControl.SelectedIndex = 0;
                    UpdateProcessSettingsTab(processName, selectedProcessConfig);

                    FilterThreads("");
                    FilterModules("");

                    statusLabel.Text = $"Updated config for: {processName}";
                }
                else
                {
                    statusLabel.Text = $"Canceled edit for: {processName}";
                }
            }
        }

        private void FilterThreads(string searchText)
        {
            if (threadsListView == null) return;

            threadsListView.Items.Clear();

            if (selectedProcessConfig == null) return;

            foreach (var thread in selectedProcessConfig.Threads)
            {
                if (string.IsNullOrEmpty(searchText) ||
                    thread.Key.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    var item = new ListViewItem(thread.Key);
                    item.SubItems.Add(thread.Value.Priority.ToString());
                    item.SubItems.Add(thread.Value.Affinity);
                    item.SubItems.Add(thread.Value.DisableBoost.ToString());
                    item.Tag = thread.Value;
                    threadsListView.Items.Add(item);
                }
            }
        }

        private void FilterModules(string searchText)
        {
            if (modulesListView == null) return;

            modulesListView.Items.Clear();

            if (selectedProcessConfig == null) return;

            foreach (var module in selectedProcessConfig.Modules)
            {
                if (string.IsNullOrEmpty(searchText) ||
                    module.Key.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    var item = new ListViewItem(module.Key);
                    item.SubItems.Add(module.Value.ToString());
                    item.Tag = module.Value;
                    modulesListView.Items.Add(item);
                }
            }
        }

        private void UpdateProcessSettingsTab(string processName, ProcessConfig config)
        {
            if (processSettingsTab.Controls.Count > 0 && processSettingsTab.Controls[0] is Panel panel)
            {
                foreach (Control control in panel.Controls)
                {
                    if (control is NumericUpDown numericUpDown && numericUpDown.Tag?.ToString() == "priority")
                    {
                        numericUpDown.Value = config.Priority;
                    }
                    else if (control is TextBox textBox && textBox.Tag?.ToString() == "affinity")
                    {
                        textBox.Text = config.Affinity;
                    }
                    else if (control is CheckBox checkBox && checkBox.Tag?.ToString() == "boost")
                    {
                        checkBox.Checked = config.DisableBoost;
                    }
                }
            }
        }

        private void RefreshProcessesButton_Click(object? sender, EventArgs e)
        {
            RefreshProcessList();
        }

        private void LoadButton_Click(object? sender, EventArgs e)
        {
            LoadConfigurationFiles();
            MessageBox.Show("Configuration reloaded from files.", "Reload Complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SaveButton_Click(object? sender, EventArgs e)
        {
            try
            {
                SaveConfigurationFiles();
                MessageBox.Show("All configuration files saved successfully!", "Save Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                statusLabel.Text = "Configuration saved";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving configuration: {ex.Message}", "Save Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Save failed";
            }
        }



        private void RestartWithMemoryCapButton_Click(object? sender, EventArgs e)
        {
            try
            {
                if (mainTabControl.SelectedTab != globalConfigTab)
                {
                    MessageBox.Show("Go to the Global tab and select apps under Memory Limits first.",
                        "Select Apps", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var memoryList = _memoryLimitsListView;
                if (memoryList == null)
                {
                    MessageBox.Show("Memory Limits list is not initialized. Re-open the Global tab and try again.", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var checkedItems = memoryList.CheckedItems.Cast<ListViewItem>().ToList();
                if (checkedItems.Count == 0)
                {
                    MessageBox.Show("Check one or more apps in the Memory Limits list first.", "Select Apps",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var targets = new List<(string exeText, string baseName, int limitMb)>();
                foreach (var it in checkedItems)
                {
                    string exeText = (it.Text ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(exeText))
                        continue;

                    string baseName = exeText.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? exeText.Substring(0, exeText.Length - 4)
                        : exeText;

                    if (!ConfigLoader.MemoryLimitMb.TryGetValue(baseName, out int limitMb) || limitMb <= 0)
                        continue;

                    targets.Add((exeText, baseName, limitMb));
                }

                if (targets.Count == 0)
                {
                    MessageBox.Show("No valid memory limit is configured for the checked apps.", "No Limits",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                bool dontShowAgain = false;
                bool showWarning = true;
                if (File.Exists(Path.Combine("config", "ProcRipper.GCFG")))
                {
                    try
                    {
                        var lines = File.ReadAllLines(Path.Combine("config", "ProcRipper.GCFG"));
                        foreach (var line in lines)
                        {
                            var t = line.Trim();
                            if (t.StartsWith("#") || t.StartsWith("
                            if (t.StartsWith("memory_cap_warning_disabled", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = t.Split('=');
                                if (parts.Length == 2 && bool.TryParse(parts[1].Trim(), out bool disabled) && disabled)
                                    showWarning = false;
                            }
                        }
                    }
                    catch { }
                }

                if (showWarning)
                {
                    var first = targets[0];
                    var result = ProcRipperConfig.UI.WinForms.MemoryCapWarningForm.ShowDialog(this, $"{first.exeText} (+{Math.Max(0, targets.Count - 1)} more)", first.limitMb, out dontShowAgain);
                    if (result != DialogResult.OK)
                        return;

                    if (dontShowAgain)
                    {
                        try
                        {
                            File.AppendAllText(Path.Combine("config", "ProcRipper.GCFG"), Environment.NewLine + "memory_cap_warning_disabled=true" + Environment.NewLine);
                        }
                        catch { }
                    }
                }

                int restarted = 0;
                var failures = new List<string>();

                foreach (var t in targets)
                {
                    string exeText = t.exeText;
                    string baseName = t.baseName;
                    int limitMb = t.limitMb;

                    string? exePath = null;

                    try
                    {
                        foreach (var p in Process.GetProcessesByName(baseName))
                        {
                            try
                            {
                                exePath = p.MainModule?.FileName;
                                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                                    break;
                            }
                            catch
                            {
                            }
                        }
                    }
                    catch
                    {
                    }

                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        try
                        {
                            exePath = AppPathResolver.ResolveExeFullPath(exeText) ?? AppPathResolver.ResolveExeFullPath(baseName);
                        }
                        catch
                        {
                        }
                    }

                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        try
                        {
                            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                            string pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                            var chromeCandidates = new[]
                            {
                                Path.Combine(pf,  "Google", "Chrome", "Application", "chrome.exe"),
                                Path.Combine(pfx, "Google", "Chrome", "Application", "chrome.exe"),
                                Path.Combine(lad, "Google", "Chrome", "Application", "chrome.exe"),
                            };

                            foreach (var c in chromeCandidates)
                            {
                                if (File.Exists(c))
                                {
                                    exePath = c;
                                    break;
                                }
                            }
                        }
                        catch
                        {
                        }
                    }

                    if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    {
                        MessageBox.Show(
                            $"Could not resolve the full executable path for \"{exeText}\".\n\n" +
                            "Fix options:\n" +
                            "1) Start the app once, then try again.\n" +
                            "2) Ensure Windows App Paths is registered for this app.\n" +
                            "3) Add the correct executable to your system PATH.",
                            "Executable Path Not Found",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return;
                    }

                    foreach (var p in Process.GetProcessesByName(baseName))
                    {
                        try { p.Kill(entireProcessTree: true); }
                        catch { }
                    }

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
                    };

                    using var limiter = new ProcRipper.Features.Native.JobObjectMemoryLimiter(name: $"ProcRipperMemCap_{baseName}");
                    limiter.SetJobMemoryLimitBytes((ulong)limitMb * 1024UL * 1024UL, killOnJobClose: true);
                    limiter.LaunchInJob(startInfo);

                    restarted++;
                }

                statusLabel.Text = $"Restarted {restarted} app(s) with memory caps";
                if (failures.Count == 0)
                {
                    MessageBox.Show($"Restarted {restarted} app(s) with hard memory caps.",
                        "Memory Caps Applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"Restarted {restarted} app(s) with hard memory caps.\n\nSome failed:\n- {string.Join("\n- ", failures)}",
                        "Memory Caps Applied (Partial)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to restart with memory cap: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Restart with memory cap failed";
            }
        }

        private void RefreshProcessList()
        {
            processListView.Items.Clear();
            processIcons.Images.Clear();

            try
            {
                Process[] processes = Process.GetProcesses()
                    .Where(p => !string.IsNullOrEmpty(p.ProcessName))
                    .OrderBy(p => p.ProcessName)
                    .ToArray();

                int iconIndex = 0;
                var addedProcesses = new HashSet<string>();

                foreach (Process process in processes)
                {
                    try
                    {
                        string processName = process.ProcessName;
                        if (addedProcesses.Contains(processName)) continue;

                        string memoryUsage = $"{process.WorkingSet64 / 1024 / 1024} MB";

                        Icon? processIcon = GetProcessIcon(process);
                        if (processIcon != null)
                        {
                            processIcons.Images.Add(processIcon);
                        }
                        else
                        {
                            processIcons.Images.Add(SystemIcons.Application);
                        }

                        var item = new ListViewItem(processName, iconIndex);
                        item.SubItems.Add(process.Id.ToString());
                        item.SubItems.Add(memoryUsage);
                        item.Tag = process;
                        processListView.Items.Add(item);

                        iconIndex++;
                        addedProcesses.Add(processName);
                    }
                    catch
                    {
                    }
                }

                statusLabel.Text = $"Loaded {processListView.Items.Count} processes";
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Error loading processes: {ex.Message}";
            }
        }

        private Icon? GetProcessIcon(Process process)
        {
            try
            {
                string? exePath = GetProcessExecutablePath(process);
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    return Icon.ExtractAssociatedIcon(exePath);
                }
            }
            catch
            {
            }
            return null;
        }

        private string? GetProcessExecutablePath(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        private void LoadConfigurationFiles()
        {
            try
            {
                ConfigLoader.Load();
                gameConfigs.Clear();
                systemConfigs.Clear();
                disableBoostProcesses.Clear();

                foreach (var kvp in ConfigLoader.GameConfigs)
                    gameConfigs[kvp.Key] = kvp.Value;
                foreach (var kvp in ConfigLoader.SystemConfigs)
                    systemConfigs[kvp.Key] = kvp.Value;
                foreach (var process in ConfigLoader.DisableBoostProcesses)
                    disableBoostProcesses.Add(process);

                RefreshConfigTabs();
                statusLabel.Text = "Configuration loaded";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading configuration: {ex.Message}", "Load Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshConfigTabs()
        {
            if (gameConfigTab.Controls.Count > 0 && gameConfigTab.Controls[0] is ListView gameListView)
            {
                gameListView.Items.Clear();
                foreach (var kvp in gameConfigs.OrderBy(x => x.Key))
                {
                    var item = new ListViewItem(kvp.Key);
                    item.SubItems.Add(kvp.Value.Priority.ToString());
                    item.SubItems.Add(kvp.Value.Affinity);
                    item.SubItems.Add(kvp.Value.DisableBoost.ToString());
                    item.Tag = kvp.Value;
                    gameListView.Items.Add(item);
                }
            }

            if (systemConfigTab.Controls.Count > 0 && systemConfigTab.Controls[0] is ListView systemListView)
            {
                systemListView.Items.Clear();
                foreach (var kvp in systemConfigs.OrderBy(x => x.Key))
                {
                    var item = new ListViewItem(kvp.Key);
                    item.SubItems.Add(kvp.Value.Priority.ToString());
                    item.SubItems.Add(kvp.Value.Affinity);
                    item.SubItems.Add(kvp.Value.DisableBoost.ToString());
                    item.Tag = kvp.Value;
                    systemListView.Items.Add(item);
                }
            }

            RefreshGlobalConfigTab();
        }

        private void RefreshGlobalConfigTab()
        {
            if (globalConfigTab.Controls.Count == 0) return;

            foreach (Control control in globalConfigTab.Controls)
            {
                if (control is Panel panel)
                {
                    foreach (Control inner in panel.Controls)
                    {
                        if (inner is CheckBox cb)
                        {
                            if (cb.Text.Contains("Enable Network Throttling", StringComparison.OrdinalIgnoreCase))
                                cb.Checked = ConfigLoader.NetworkThrottleEnabled;
                            else if (cb.Text.Contains("Enable Auto Idle Prevention", StringComparison.OrdinalIgnoreCase))
                                cb.Checked = ConfigLoader.AutoIdleEnabled;
                            else if (cb.Text.Contains("Enable Verbose Startup", StringComparison.OrdinalIgnoreCase))
                                cb.Checked = ConfigLoader.VerboseStartup;
                            else if (cb.Text.Contains("Auto-apply memory caps on GAME launch", StringComparison.OrdinalIgnoreCase))
                                cb.Checked = ConfigLoader.AutoApplyMemoryCapsOnGameLaunch;
                            else if (cb.Text.Contains("Prompt before auto restarting apps for memory caps on GAME launch", StringComparison.OrdinalIgnoreCase))
                                cb.Checked = ConfigLoader.PromptBeforeAutoMemoryCapsOnGameLaunch;
                        }
                    }

                    foreach (Control inner in panel.Controls)
                    {
                        if (inner is ListView listView && listView.CheckBoxes)
                        {
                            listView.Items.Clear();

                            foreach (var kvp in ConfigLoader.MemoryLimitMb.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                            {
                                string exeText = kvp.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                    ? kvp.Key
                                    : kvp.Key + ".exe";

                                string baseName = exeText.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                    ? exeText.Substring(0, exeText.Length - 4)
                                    : exeText;

                                bool shouldCheck = ConfigLoader.AutoMemoryCapsTargets.Contains(baseName);

                                var item = new ListViewItem(exeText);
                                item.SubItems.Add(kvp.Value.ToString());
                                item.SubItems.Add("Yes");
                                item.Checked = shouldCheck;
                                listView.Items.Add(item);
                            }
                        }
                    }
                }
            }
        }

        private static string ResolveSharedConfigDir()
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                for (int i = 0; i < 6 && !string.IsNullOrWhiteSpace(dir); i++)
                {
                    string candidateConfig = Path.Combine(dir, "config");
                    string candidateSln = Path.Combine(dir, "ProcRipper v3.0.0.sln");
                    if (Directory.Exists(candidateConfig) || File.Exists(candidateSln))
                    {
                        return candidateConfig;
                    }
                    var parent = Directory.GetParent(dir);
                    if (parent == null) break;
                    dir = parent.FullName;
                }
            }
            catch { }

            return Path.Combine(AppContext.BaseDirectory, "config");
        }

        private void SaveConfigurationFiles()
        {
            var cfgDir = ResolveSharedConfigDir();
            Directory.CreateDirectory(cfgDir);

            CreateConfigBackup();

            SaveConfigFile(Path.Combine(cfgDir, "GAME_PRIORITY.GCFG"), gameConfigs, true);

            SaveConfigFile(Path.Combine(cfgDir, "PROC_PRIORITY.GCFG"), systemConfigs, false);

            SaveGlobalConfigFile();
        }

        private void SaveGlobalConfigFile()
        {
            var cfgDir = ResolveSharedConfigDir();
            Directory.CreateDirectory(cfgDir);
            using (StreamWriter writer = new StreamWriter(Path.Combine(cfgDir, "ProcRipper.GCFG")))
            {
                writer.WriteLine("# ================================================================");
                writer.WriteLine("# ProcRipper Global Configuration (v3.0.0)");
                writer.WriteLine("# This file controls features that are NOT tied to a single game");
                writer.WriteLine("# or system process profile (see GAME_PRIORITY.GCFG / PROC_PRIORITY.GCFG).");
                writer.WriteLine("# ================================================================");
                writer.WriteLine();
                writer.WriteLine($"# Generated on {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                if (globalConfigTab.Controls.Count > 0 && globalConfigTab.Controls[0] is Panel panel)
                {
                    bool wroteTargetsAndLimits = false;

                    foreach (Control control in panel.Controls)
                    {
                        if (control is CheckBox checkBox)
                        {
                            if (checkBox.Text.Contains("Enable Network Throttling", StringComparison.OrdinalIgnoreCase) ||
                                checkBox.Text.Contains("Network Throttling", StringComparison.OrdinalIgnoreCase))
                                writer.WriteLine($"network_throttle={checkBox.Checked}");
                            else if (checkBox.Text.Contains("Enable Auto Idle Prevention", StringComparison.OrdinalIgnoreCase) ||
                                     checkBox.Text.Contains("Auto Idle Prevention", StringComparison.OrdinalIgnoreCase))
                                writer.WriteLine($"auto_idle={checkBox.Checked}");
                            else if (checkBox.Text.Contains("Enable Verbose Startup", StringComparison.OrdinalIgnoreCase) ||
                                     checkBox.Text.Contains("Verbose Startup", StringComparison.OrdinalIgnoreCase))
                                writer.WriteLine($"verbose_startup={checkBox.Checked}");
                            else if (checkBox.Text.Contains("Auto-apply memory caps on GAME launch", StringComparison.OrdinalIgnoreCase))
                                writer.WriteLine($"auto_apply_memory_caps_on_game_launch={checkBox.Checked}");
                            else if (checkBox.Text.Contains("Prompt before auto restarting apps for memory caps on GAME launch", StringComparison.OrdinalIgnoreCase))
                                writer.WriteLine($"prompt_before_auto_memory_caps_on_game_launch={checkBox.Checked}");
                        }
                        else if (control is ListView listView && listView.CheckBoxes)
                        {
                            wroteTargetsAndLimits = true;

                            var selectedTargets = new List<string>();

                            foreach (ListViewItem item in listView.Items)
                            {
                                if (item.Checked)
                                {
                                    string processName = item.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                        ? item.Text.Substring(0, item.Text.Length - 4)
                                        : item.Text;

                                    if (!string.IsNullOrWhiteSpace(processName))
                                        selectedTargets.Add(processName);
                                }

                                if (item.Checked && int.TryParse(item.SubItems[1].Text, out int memoryLimit) && memoryLimit > 0)
                                {
                                    string processName = item.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                        ? item.Text.Substring(0, item.Text.Length - 4)
                                        : item.Text;
                                    writer.WriteLine($"{processName}_memory_limit_mb={memoryLimit}");
                                }
                            }

                            if (selectedTargets.Count > 0)
                            {
                                selectedTargets.Sort(StringComparer.OrdinalIgnoreCase);
                                writer.WriteLine($"auto_memory_caps_targets={string.Join(",", selectedTargets)}");
                            }
                            else
                            {
                                writer.WriteLine("auto_memory_caps_targets=");
                            }
                        }
                        else if (control is RadioButton radioButton && radioButton.Checked)
                        {
                            if (radioButton.Text == "Low")
                                writer.WriteLine("network_priority=-2");
                            else if (radioButton.Text == "Normal")
                                writer.WriteLine("network_priority=0");
                            else if (radioButton.Text == "High")
                                writer.WriteLine("network_priority=1");
                            else if (radioButton.Text == "Highest")
                                writer.WriteLine("network_priority=2");
                        }
                    }

                    if (!wroteTargetsAndLimits)
                    {
                        writer.WriteLine("auto_memory_caps_targets=");
                    }
                }
            }

            try { LoadConfigurationFiles(); } catch { }
        }

        private void ExportConfigButton_Click(object? sender, EventArgs e)
        {
            try
            {
                var cfgDir = ResolveSharedConfigDir();
                Directory.CreateDirectory(cfgDir);

                using var sfd = new SaveFileDialog
                {
                    Title = "Export ProcRipper Config",
                    Filter = "Zip Archive (*.zip)|*.zip",
                    FileName = $"ProcRipper-config-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                    OverwritePrompt = true
                };

                if (sfd.ShowDialog(this) != DialogResult.OK)
                    return;

                string zipPath = sfd.FileName;
                if (string.IsNullOrWhiteSpace(zipPath))
                    return;

                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                ZipFile.CreateFromDirectory(cfgDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

                MessageBox.Show($"Config exported successfully:\n{zipPath}", "Export Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                statusLabel.Text = "Config exported";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Export failed";
            }
        }

        private void ImportConfigButton_Click(object? sender, EventArgs e)
        {
            try
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Import ProcRipper Config",
                    Filter = "Zip Archive (*.zip)|*.zip",
                    Multiselect = false
                };

                if (ofd.ShowDialog(this) != DialogResult.OK)
                    return;

                string zipPath = ofd.FileName;
                if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                    return;

                var confirm = MessageBox.Show(
                    "Importing a config will overwrite your current config folder.\n\n" +
                    "A backup will be created automatically.\n\nContinue?",
                    "Confirm Import",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (confirm != DialogResult.Yes)
                    return;

                var cfgDir = ResolveSharedConfigDir();
                Directory.CreateDirectory(cfgDir);

                CreateConfigBackup();

                foreach (var file in Directory.GetFiles(cfgDir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (var dir in Directory.GetDirectories(cfgDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }

                ZipFile.ExtractToDirectory(zipPath, cfgDir, overwriteFiles: true);

                LoadConfigurationFiles();
                RefreshConfigTabs();

                MessageBox.Show("Config imported successfully.\n\nYour previous config was backed up.", "Import Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                statusLabel.Text = "Config imported";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "Import Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Import failed";
            }
        }

        private void CreateConfigBackup()
        {
            try
            {
                var cfgDir = ResolveSharedConfigDir();
                Directory.CreateDirectory(cfgDir);
                Directory.CreateDirectory(Path.Combine(cfgDir, "backup"));

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string backupZip = Path.Combine(cfgDir, "backup", $"config-backup-{stamp}.zip");

                string tempDir = Path.Combine(Path.GetTempPath(), $"ProcRipper-config-backup-{stamp}");
                Directory.CreateDirectory(tempDir);

                try
                {
                    foreach (var file in Directory.GetFiles(cfgDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        string dest = Path.Combine(tempDir, Path.GetFileName(file));
                        File.Copy(file, dest, overwrite: true);
                    }
                }
                catch
                {
                }

                if (File.Exists(backupZip))
                    File.Delete(backupZip);

                ZipFile.CreateFromDirectory(tempDir, backupZip, CompressionLevel.Optimal, includeBaseDirectory: false);

                try { Directory.Delete(tempDir, recursive: true); } catch { }

                try
                {
                    var backups = Directory.GetFiles(Path.Combine(cfgDir, "backup"), "config-backup-*.zip")
                        .Select(p => new FileInfo(p))
                        .OrderByDescending(f => f.CreationTimeUtc)
                        .ToList();

                    foreach (var old in backups.Skip(10))
                    {
                        try { old.Delete(); } catch { }
                    }
                }
                catch
                {
                }
            }
            catch
            {
            }
        }

        private void SaveConfigFile(string fileName, Dictionary<string, ProcessConfig> configs, bool isGame)
        {
            var dir = Path.GetDirectoryName(fileName);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            using (StreamWriter writer = new StreamWriter(fileName))
            {
                writer.WriteLine($"# {Path.GetFileName(fileName)} - Generated by ProcRipper Config Tool v3.0.0");
                writer.WriteLine($"# Generated on {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                if (isGame)
                {
                    writer.WriteLine("#Format");
                    writer.WriteLine("#[ProcessName.exe,Priority,Affinity,DisableBoost,GpuPriority]");
                    writer.WriteLine("#per proc affinity and Priority dosent work yet");
                    writer.WriteLine("#ThreadName=Priority,Affinity,DisableBoost");
                    writer.WriteLine("# Available priorities:");
                    writer.WriteLine("# 15=TIME_CRITICAL, 2=HIGHEST, 1=ABOVE_NORMAL, 0=NORMAL, -1=BELOW_NORMAL, -2=LOWEST, -15=IDLE");
                    writer.WriteLine("#");
                    writer.WriteLine("# Affinity options:");
                    writer.WriteLine("# ALL = Use all CPU cores");
                    writer.WriteLine("# 0,2,4,6 = Use only even cores (for 8-core CPU)");
                    writer.WriteLine("# 0-3 = Use first 4 cores");
                    writer.WriteLine("# 0,1 = Use first 2 cores only");
                    writer.WriteLine("#");
                    writer.WriteLine("# DisableBoost: true/false - Disables priority boosting for more consistent performance");
                    writer.WriteLine("# true = NO boosting (recommended for gaming)");
                    writer.WriteLine("# false = YES boosting (Windows default)");
                    writer.WriteLine("#");
                    writer.WriteLine("# GPU Priority: none/very_low/low/normal/high - Controls GPU scheduling priority");
                    writer.WriteLine("# high = Maximum GPU access (for games)");
                    writer.WriteLine("# low/very_low = Reduced GPU access (for background apps)");
                    writer.WriteLine("# none = No GPU priority management (Windows default)");
                }

                foreach (var kvp in configs.OrderBy(x => x.Key))
                {
                    var config = kvp.Value;
                    var gpuPriorityStr = ProcRipper.Features.GpuPriorityManager.GpuPriorityToString(config.GpuPriority);
                    writer.WriteLine($"[{kvp.Key}.exe,{config.Priority},{config.Affinity},{config.DisableBoost},{gpuPriorityStr}]");

                    foreach (var thread in config.Threads.OrderBy(x => x.Key))
                    {
                        writer.WriteLine($"{thread.Key}={thread.Value.Priority},{thread.Value.Affinity},{thread.Value.DisableBoost}");
                    }

                    foreach (var module in config.Modules.OrderBy(x => x.Key))
                    {
                        writer.WriteLine($"module={module.Key}, {module.Value}");
                    }

                    writer.WriteLine();
                }

                if (disableBoostProcesses.Count > 0 && !isGame)
                {
                    writer.WriteLine();
                    writer.WriteLine("[DisableBoost]");
                    foreach (var process in disableBoostProcesses.OrderBy(x => x))
                    {
                        writer.WriteLine(process);
                    }
                }
            }
        }

        private void AddProcessToConfig(ListView listView, Dictionary<string, ProcessConfig> configs, bool isGame)
        {
            using (var dialog = new AddProcessDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string processName = dialog.ProcessName;
                    int priority = dialog.Priority;
                    string affinity = dialog.Affinity;
                    bool disableBoost = dialog.DisableBoost;

                    var config = new ProcessConfig
                    {
                        Priority = priority,
                        Affinity = affinity,
                        DisableBoost = disableBoost
                    };

                    configs[processName] = config;

                    var item = new ListViewItem(processName);
                    item.SubItems.Add(priority.ToString());
                    item.SubItems.Add(affinity);
                    item.SubItems.Add(disableBoost.ToString());
                    item.Tag = config;
                    listView.Items.Add(item);
                }
            }
        }

        private void EditProcessInConfig(ListView listView, Dictionary<string, ProcessConfig> configs)
        {
            if (listView.SelectedItems.Count == 0) return;

            var selectedItem = listView.SelectedItems[0];
            string processName = selectedItem.Text;

            if (!configs.ContainsKey(processName)) return;

            var config = configs[processName];

            using (var dialog = new EditProcessDialog(processName, config.Priority, config.Affinity, config.DisableBoost))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    config.Priority = dialog.Priority;
                    config.Affinity = dialog.Affinity;
                    config.DisableBoost = dialog.DisableBoost;

                    selectedItem.SubItems[1].Text = config.Priority.ToString();
                    selectedItem.SubItems[2].Text = config.Affinity;
                    selectedItem.SubItems[3].Text = config.DisableBoost.ToString();
                }
            }
        }

        private void RemoveProcessFromConfig(ListView listView, Dictionary<string, ProcessConfig> configs)
        {
            if (listView.SelectedItems.Count > 0)
            {
                var item = listView.SelectedItems[0];
                string processName = item.Text;
                configs.Remove(processName);
                listView.Items.Remove(item);
            }
        }

        private void ApplyProcessSettings(NumericUpDown priorityControl, TextBox affinityControl, CheckBox boostControl, ComboBox gpuPriorityControl)
        {
            if (selectedProcessConfig == null) return;

            selectedProcessConfig.Priority = (int)priorityControl.Value;
            selectedProcessConfig.Affinity = affinityControl.Text;
            selectedProcessConfig.DisableBoost = boostControl.Checked;

            var gpuPriorityText = gpuPriorityControl.SelectedItem?.ToString() ?? "None";
            selectedProcessConfig.GpuPriority = gpuPriorityText.ToLowerInvariant().Replace(" ", "_") switch
            {
                "very_low" => ProcRipper.Features.GpuPriority.VeryLow,
                "low" => ProcRipper.Features.GpuPriority.Low,
                "normal" => ProcRipper.Features.GpuPriority.Normal,
                "high" => ProcRipper.Features.GpuPriority.High,
                _ => ProcRipper.Features.GpuPriority.None
            };

            statusLabel.Text = "Process settings applied";
        }

        private void AddThreadToProcess()
        {
            if (selectedProcessConfig == null) return;

            using (var dialog = new AddThreadDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    var threadConfig = new ThreadConfig
                    {
                        Priority = dialog.Priority,
                        Affinity = dialog.Affinity,
                        DisableBoost = dialog.DisableBoost
                    };

                    selectedProcessConfig.Threads[dialog.ThreadName] = threadConfig;

                    FilterThreads("");
                }
            }
        }

        private void EditThreadInProcess()
        {
            if (selectedProcessConfig == null || threadsListView.SelectedItems.Count == 0) return;

            var selectedItem = threadsListView.SelectedItems[0];
            string threadName = selectedItem.Text;

            if (!selectedProcessConfig.Threads.ContainsKey(threadName)) return;

            var threadConfig = selectedProcessConfig.Threads[threadName];

            using (var dialog = new EditThreadDialog(threadName, threadConfig.Priority, threadConfig.Affinity, threadConfig.DisableBoost))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    threadConfig.Priority = dialog.Priority;
                    threadConfig.Affinity = dialog.Affinity;
                    threadConfig.DisableBoost = dialog.DisableBoost;

                    selectedItem.SubItems[1].Text = threadConfig.Priority.ToString();
                    selectedItem.SubItems[2].Text = threadConfig.Affinity;
                    selectedItem.SubItems[3].Text = threadConfig.DisableBoost.ToString();
                }
            }
        }

        private void RemoveThreadFromProcess()
        {
            if (selectedProcessConfig == null || threadsListView.SelectedItems.Count == 0) return;

            var selectedItem = threadsListView.SelectedItems[0];
            string threadName = selectedItem.Text;

            selectedProcessConfig.Threads.Remove(threadName);
            FilterThreads("");
        }

        private void AddModuleToProcess()
        {
            if (selectedProcessConfig == null) return;

            using (var dialog = new AddModuleDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    selectedProcessConfig.Modules[dialog.ModuleName] = dialog.Priority;

                    FilterModules("");
                }
            }
        }

        private void EditModuleInProcess()
        {
            if (selectedProcessConfig == null || modulesListView.SelectedItems.Count == 0) return;

            var selectedItem = modulesListView.SelectedItems[0];
            string moduleName = selectedItem.Text;

            if (!selectedProcessConfig.Modules.ContainsKey(moduleName)) return;

            int currentPriority = selectedProcessConfig.Modules[moduleName];

            using (var dialog = new EditModuleDialog(moduleName, currentPriority))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    selectedProcessConfig.Modules[moduleName] = dialog.Priority;

                    selectedItem.SubItems[1].Text = dialog.Priority.ToString();
                }
            }
        }

        private void RemoveModuleFromProcess()
        {
            if (selectedProcessConfig == null || modulesListView.SelectedItems.Count == 0) return;

            var selectedItem = modulesListView.SelectedItems[0];
            string moduleName = selectedItem.Text;

            selectedProcessConfig.Modules.Remove(moduleName);
            FilterModules("");
        }
    }

    public class AddProcessDialog : Form
    {
        private TextBox processNameTextBox;
        private NumericUpDown priorityNumericUpDown;
        private TextBox affinityTextBox;
        private CheckBox disableBoostCheckBox;
        private Button okButton;
        private Button cancelButton;

        public string ProcessName => processNameTextBox.Text.Trim();
        public int Priority => (int)priorityNumericUpDown.Value;
        public string Affinity => affinityTextBox.Text.Trim().ToUpper();
        public bool DisableBoost => disableBoostCheckBox.Checked;

        public AddProcessDialog()
        {
            this.Text = "Add Process Configuration";
            this.Size = new Size(350, 200);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            Label nameLabel = new Label { Text = "Process Name:", Location = new Point(10, 20) };
            processNameTextBox = new TextBox { Location = new Point(120, 18), Size = new Size(200, 25) };

            Label priorityLabel = new Label { Text = "Priority:", Location = new Point(10, 55) };
            priorityNumericUpDown = new NumericUpDown
            {
                Location = new Point(120, 53),
                Size = new Size(80, 25),
                Minimum = -15,
                Maximum = 15,
                Value = 0
            };

            Label affinityLabel = new Label { Text = "Affinity:", Location = new Point(10, 90) };
            affinityTextBox = new TextBox { Location = new Point(120, 88), Size = new Size(200, 25), Text = "ALL" };

            disableBoostCheckBox = new CheckBox
            {
                Text = "Disable Priority Boost",
                Location = new Point(120, 120),
                Checked = true
            };

            okButton = new Button { Text = "OK", Location = new Point(160, 150), Size = new Size(75, 30), DialogResult = DialogResult.OK };
            cancelButton = new Button { Text = "Cancel", Location = new Point(245, 150), Size = new Size(75, 30), DialogResult = DialogResult.Cancel };

            this.Controls.AddRange(new Control[] {
                nameLabel, processNameTextBox,
                priorityLabel, priorityNumericUpDown,
                affinityLabel, affinityTextBox,
                disableBoostCheckBox,
                okButton, cancelButton
            });

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }
    }

    public class EditProcessDialog : Form
    {
        private TextBox processNameTextBox;
        private NumericUpDown priorityNumericUpDown;
        private TextBox affinityTextBox;
        private CheckBox disableBoostCheckBox;
        private Button okButton;
        private Button cancelButton;

        public string ProcessName => processNameTextBox.Text.Trim();
        public int Priority => (int)priorityNumericUpDown.Value;
        public string Affinity => affinityTextBox.Text.Trim().ToUpper();
        public bool DisableBoost => disableBoostCheckBox.Checked;

        public EditProcessDialog(string processName, int priority, string affinity, bool disableBoost)
        {
            this.Text = "Edit Process Configuration";
            this.Size = new Size(350, 200);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            Label nameLabel = new Label { Text = "Process Name:", Location = new Point(10, 20) };
            processNameTextBox = new TextBox
            {
                Location = new Point(120, 18),
                Size = new Size(200, 25),
                Text = processName,
                ReadOnly = true,
                BackColor = SystemColors.Control
            };

            Label priorityLabel = new Label { Text = "Priority:", Location = new Point(10, 55) };
            priorityNumericUpDown = new NumericUpDown
            {
                Location = new Point(120, 53),
                Size = new Size(80, 25),
                Minimum = -15,
                Maximum = 15,
                Value = priority
            };

            Label affinityLabel = new Label { Text = "Affinity:", Location = new Point(10, 90) };
            affinityTextBox = new TextBox
            {
                Location = new Point(120, 88),
                Size = new Size(200, 25),
                Text = affinity
            };

            disableBoostCheckBox = new CheckBox
            {
                Text = "Disable Priority Boost",
                Location = new Point(120, 120),
                Checked = disableBoost
            };

            okButton = new Button { Text = "OK", Location = new Point(160, 150), Size = new Size(75, 30), DialogResult = DialogResult.OK };
            cancelButton = new Button { Text = "Cancel", Location = new Point(245, 150), Size = new Size(75, 30), DialogResult = DialogResult.Cancel };

            this.Controls.AddRange(new Control[] {
                nameLabel, processNameTextBox,
                priorityLabel, priorityNumericUpDown,
                affinityLabel, affinityTextBox,
                disableBoostCheckBox,
                okButton, cancelButton
            });

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }
    }

    public class AddMemoryLimitDialog : Form
    {
        private TextBox processNameTextBox;
        private NumericUpDown memoryLimitNumericUpDown;
        private Button okButton;
        private Button cancelButton;

        public string ProcessName => processNameTextBox.Text.Trim();
        public int MemoryLimit => (int)memoryLimitNumericUpDown.Value;

        public AddMemoryLimitDialog()
        {
            this.Text = "Add Memory Limit";
            this.Size = new Size(300, 150);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            Label nameLabel = new Label { Text = "Process Name:", Location = new Point(10, 20) };
            processNameTextBox = new TextBox { Location = new Point(120, 18), Size = new Size(150, 25) };

            Label memoryLabel = new Label { Text = "Memory Limit (MB):", Location = new Point(10, 55) };
            memoryLimitNumericUpDown = new NumericUpDown
            {
                Location = new Point(120, 53),
                Size = new Size(80, 25),
                Minimum = 1,
                Maximum = 10000,
                Value = 512
            };

            okButton = new Button { Text = "OK", Location = new Point(120, 90), Size = new Size(75, 30), DialogResult = DialogResult.OK };
            cancelButton = new Button { Text = "Cancel", Location = new Point(205, 90), Size = new Size(75, 30), DialogResult = DialogResult.Cancel };

            this.Controls.AddRange(new Control[] {
                nameLabel, processNameTextBox,
                memoryLabel, memoryLimitNumericUpDown,
                okButton, cancelButton
            });

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }
    }

    public class AddThreadDialog : Form
    {
        private TextBox threadNameTextBox;
        private ComboBox priorityComboBox;
        private TextBox affinityTextBox;
        private CheckBox disableBoostCheckBox;
        private Button okButton;
        private Button cancelButton;

        public string ThreadName => threadNameTextBox.Text.Trim();

        public int Priority
        {
            get
            {
                if (priorityComboBox.SelectedItem is PriorityChoice pc) return pc.Value;
                return 0;
            }
        }

        public string Affinity => affinityTextBox.Text.Trim().ToUpper();
        public bool DisableBoost => disableBoostCheckBox.Checked;

        private sealed class PriorityChoice
        {
            public int Value { get; }
            public string Name { get; }

            public PriorityChoice(int value, string name)
            {
                Value = value;
                Name = name;
            }

            public override string ToString() => $"{Value} ({Name})";
        }

        public AddThreadDialog()
        {
            this.Text = "Add Thread Configuration";
            this.Size = new Size(350, 200);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            Label nameLabel = new Label { Text = "Thread Name:", Location = new Point(10, 20) };
            threadNameTextBox = new TextBox { Location = new Point(120, 18), Size = new Size(200, 25) };

            Label priorityLabel = new Label { Text = "Priority:", Location = new Point(10, 55) };
            priorityComboBox = new ComboBox
            {
                Location = new Point(120, 53),
                Size = new Size(200, 25),
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            var priorities = ProcRipper.Core.ConfigLoader.PriorityMap
                .Where(kv => kv.Key is 15 or 2 or 1 or 0 or -1 or -2 or -15)
                .OrderByDescending(kv => kv.Key)
                .Select(kv => new PriorityChoice(kv.Key, kv.Value))
                .ToArray();

            if (priorities.Length == 0)
            {
                priorities = new[]
                {
                    new PriorityChoice(15, "TIME_CRITICAL"),
                    new PriorityChoice(2, "HIGHEST"),
                    new PriorityChoice(1, "ABOVE_NORMAL"),
                    new PriorityChoice(0, "NORMAL"),
                    new PriorityChoice(-1, "BELOW_NORMAL"),
                    new PriorityChoice(-2, "LOWEST"),
                    new PriorityChoice(-15, "IDLE"),
                };
            }

            priorityComboBox.Items.AddRange(priorities);
            priorityComboBox.SelectedIndex = priorities.ToList().FindIndex(p => p.Value == 0);
            if (priorityComboBox.SelectedIndex < 0) priorityComboBox.SelectedIndex = 0;

            Label affinityLabel = new Label { Text = "Affinity:", Location = new Point(10, 90) };
            affinityTextBox = new TextBox { Location = new Point(120, 88), Size = new Size(200, 25), Text = "ALL" };

            disableBoostCheckBox = new CheckBox
            {
                Text = "Disable Priority Boost",
                Location = new Point(120, 120),
                Checked = true
            };

            okButton = new Button { Text = "OK", Location = new Point(160, 150), Size = new Size(75, 30), DialogResult = DialogResult.OK };
            cancelButton = new Button { Text = "Cancel", Location = new Point(245, 150), Size = new Size(75, 30), DialogResult = DialogResult.Cancel };

            this.Controls.AddRange(new Control[] {
                nameLabel, threadNameTextBox,
                priorityLabel, priorityComboBox,
                affinityLabel, affinityTextBox,
                disableBoostCheckBox,
                okButton, cancelButton
            });

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }
    }

    public class EditThreadDialog : Form
    {
        private TextBox threadNameTextBox;
        private ComboBox priorityComboBox;
        private TextBox affinityTextBox;
        private CheckBox disableBoostCheckBox;
        private Button okButton;
        private Button cancelButton;

        public string ThreadName => threadNameTextBox.Text.Trim();

        public int Priority
        {
            get
            {
                if (priorityComboBox.SelectedItem is PriorityChoice pc) return pc.Value;
                return 0;
            }
        }

        public string Affinity => affinityTextBox.Text.Trim().ToUpper();
        public bool DisableBoost => disableBoostCheckBox.Checked;

        private sealed class PriorityChoice
        {
            public int Value { get; }
            public string Name { get; }

            public PriorityChoice(int value, string name)
            {
                Value = value;
                Name = name;
            }

            public override string ToString() => $"{Value} ({Name})";
        }

        public EditThreadDialog(string threadName, int priority, string affinity, bool disableBoost)
        {
            this.Text = "Edit Thread Configuration";
            this.Size = new Size(350, 200);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            Label nameLabel = new Label { Text = "Thread Name:", Location = new Point(10, 20) };
            threadNameTextBox = new TextBox
            {
                Location = new Point(120, 18),
                Size = new Size(200, 25),
                Text = threadName,
                ReadOnly = true,
                BackColor = SystemColors.Control
            };

            Label priorityLabel = new Label { Text = "Priority:", Location = new Point(10, 55) };
            priorityComboBox = new ComboBox
            {
                Location = new Point(120, 53),
                Size = new Size(200, 25),
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            var priorities = ProcRipper.Core.ConfigLoader.PriorityMap
                .Where(kv => kv.Key is 15 or 2 or 1 or 0 or -1 or -2 or -15)
                .OrderByDescending(kv => kv.Key)
                .Select(kv => new PriorityChoice(kv.Key, kv.Value))
                .ToArray();

            if (priorities.Length == 0)
            {
                priorities = new[]
                {
                    new PriorityChoice(15, "TIME_CRITICAL"),
                    new PriorityChoice(2, "HIGHEST"),
                    new PriorityChoice(1, "ABOVE_NORMAL"),
                    new PriorityChoice(0, "NORMAL"),
                    new PriorityChoice(-1, "BELOW_NORMAL"),
                    new PriorityChoice(-2, "LOWEST"),
                    new PriorityChoice(-15, "IDLE"),
                };
            }

            priorityComboBox.Items.AddRange(priorities);

            int idx = priorities.ToList().FindIndex(p => p.Value == priority);
            if (idx < 0) idx = priorities.ToList().FindIndex(p => p.Value == 0);
            if (idx < 0) idx = 0;
            priorityComboBox.SelectedIndex = idx;

            Label affinityLabel = new Label { Text = "Affinity:", Location = new Point(10, 90) };
            affinityTextBox = new TextBox
            {
                Location = new Point(120, 88),
                Size = new Size(200, 25),
                Text = affinity
            };

            disableBoostCheckBox = new CheckBox
            {
                Text = "Disable Priority Boost",
                Location = new Point(120, 120),
                Checked = disableBoost
            };

            okButton = new Button { Text = "OK", Location = new Point(160, 150), Size = new Size(75, 30), DialogResult = DialogResult.OK };
            cancelButton = new Button { Text = "Cancel", Location = new Point(245, 150), Size = new Size(75, 30), DialogResult = DialogResult.Cancel };

            this.Controls.AddRange(new Control[] {
                nameLabel, threadNameTextBox,
                priorityLabel, priorityComboBox,
                affinityLabel, affinityTextBox,
                disableBoostCheckBox,
                okButton, cancelButton
            });

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }
    }

    public class AddModuleDialog : Form
    {
        private TextBox moduleNameTextBox;
        private NumericUpDown priorityNumericUpDown;
        private Button okButton;
        private Button cancelButton;

        public string ModuleName => moduleNameTextBox.Text.Trim();
        public int Priority => (int)priorityNumericUpDown.Value;

        public AddModuleDialog()
        {
            this.Text = "Add Module Configuration";
            this.Size = new Size(300, 150);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            Label nameLabel = new Label { Text = "Module Name:", Location = new Point(10, 20) };
            moduleNameTextBox = new TextBox { Location = new Point(120, 18), Size = new Size(150, 25) };

            Label priorityLabel = new Label { Text = "Priority:", Location = new Point(10, 55) };
            priorityNumericUpDown = new NumericUpDown
            {
                Location = new Point(120, 53),
                Size = new Size(80, 25),
                Minimum = -15,
                Maximum = 15,
                Value = 0
            };

            okButton = new Button { Text = "OK", Location = new Point(120, 90), Size = new Size(75, 30), DialogResult = DialogResult.OK };
            cancelButton = new Button { Text = "Cancel", Location = new Point(205, 90), Size = new Size(75, 30), DialogResult = DialogResult.Cancel };

            this.Controls.AddRange(new Control[] {
                nameLabel, moduleNameTextBox,
                priorityLabel, priorityNumericUpDown,
                okButton, cancelButton
            });

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }
    }

    public class EditModuleDialog : Form
    {
        private TextBox moduleNameTextBox;
        private NumericUpDown priorityNumericUpDown;
        private Button okButton;
        private Button cancelButton;

        public string ModuleName => moduleNameTextBox.Text.Trim();
        public int Priority => (int)priorityNumericUpDown.Value;

        public EditModuleDialog(string moduleName, int priority)
        {
            this.Text = "Edit Module Configuration";
            this.Size = new Size(300, 150);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            Label nameLabel = new Label { Text = "Module Name:", Location = new Point(10, 20) };
            moduleNameTextBox = new TextBox
            {
                Location = new Point(120, 18),
                Size = new Size(150, 25),
                Text = moduleName,
                ReadOnly = true,
                BackColor = SystemColors.Control
            };

            Label priorityLabel = new Label { Text = "Priority:", Location = new Point(10, 55) };
            priorityNumericUpDown = new NumericUpDown
            {
                Location = new Point(120, 53),
                Size = new Size(80, 25),
                Minimum = -15,
                Maximum = 15,
                Value = priority
            };

            okButton = new Button { Text = "OK", Location = new Point(120, 90), Size = new Size(75, 30), DialogResult = DialogResult.OK };
            cancelButton = new Button { Text = "Cancel", Location = new Point(205, 90), Size = new Size(75, 30), DialogResult = DialogResult.Cancel };

            this.Controls.AddRange(new Control[] {
                nameLabel, moduleNameTextBox,
                priorityLabel, priorityNumericUpDown,
                okButton, cancelButton
            });

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }
    }
}
