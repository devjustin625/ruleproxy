using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RuleProxy.Native.Core;

namespace RuleProxy.Native;

public sealed record LogEntry(string Time, string Message);

/// <summary>主窗口：代理启停、系统代理、规则/上游管理、连接与日志展示、托盘常驻。</summary>
public partial class MainWindow : Window
{
    private readonly ConfigStore _store = new();
    private readonly AppConfig _config;
    private readonly ProxyEngine _engine;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<ConnectionSession> _connections = [];
    private readonly ObservableCollection<LogEntry> _logs = [];
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _trayProxyItem;
    private System.Windows.Forms.ToolStripMenuItem? _traySysItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayAutostartItem;
    private bool _exiting;
    private bool _sysProxyActive;

    public MainWindow(bool startMinimized)
    {
        InitializeComponent();
        _config = _store.Load();
        _engine = new ProxyEngine(() => _config);
        _engine.StateChanged += OnEngineStateChanged;
        _engine.LogsChanged += OnLogsChanged;

        ConnectionsGrid.ItemsSource = _connections;
    LogGrid.ItemsSource = _logs;
        ReloadRuleList();
        ReloadUpstreamList();
        ReloadActionProxyComboBox();
        SetupTray();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => RefreshTick();
        _timer.Start();

        _sysProxyActive = WinProxy.IsEnabled;
        UpdateSysProxyButton();
        UpdateProxyButton();
        UpdateBrowseButtons();
        StatusText.Text = "代理未启动";

        // 设置页初始化
        LoadSettings();
        Autostart.SyncToCurrentPath(); // 如果 exe 换了位置，自动更新注册表
        UpdateAutostartPathText();

        // 启动时恢复状态
        if (_config.RememberLastState)
        {
            if (_config.LastProxyRunning)
            {
                StartProxy();
            }
            if (_config.LastSysProxyEnabled)
            {
                WinProxy.SetProxy(_config.ListenHost, _config.HttpPort);
                _sysProxyActive = true;
                UpdateSysProxyButton();
            }
        }
        else if (_config.AutoStartProxy)
        {
            StartProxy();
        }

        if (startMinimized)
        {
            HideToTray();
        }
    }

    // ------------------------------------------------------------- 代理启停

    private void OnToggleProxy(object sender, RoutedEventArgs e)
    {
        if (_engine.Running)
        {
            _engine.Stop();
        }
        else
        {
            StartProxy();
        }
    }

    private void StartProxy()
    {
        _engine.Start();
    }

    private void OnEngineStateChanged() => Dispatcher.Invoke(UpdateProxyButton);

    private void UpdateProxyButton()
    {
        var running = _engine.Running;
        ToggleProxyIcon.Text = running ? "\uE769" : "\uE768"; // Stop / Play
        ToggleProxyText.Text = running ? "停止代理" : "启动代理";
        var color = running
            ? System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26)
            : System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB);
        ToggleProxyButton.Background = new SolidColorBrush(color);
        ToggleProxyButton.BorderBrush = new SolidColorBrush(color);
        if (_trayProxyItem is not null)
        {
            _trayProxyItem.Text = running ? "停止代理" : "启动代理";
        }
        StatusDot.Fill = running ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E))
                                 : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF));
        StatusText.Text = running ? "代理运行中" : "代理未启动";
    }

    // ------------------------------------------------------------- 系统代理

    private void OnToggleSystemProxy(object sender, RoutedEventArgs e) => ToggleSystemProxy();

    private void ToggleSystemProxy()
    {
        if (_sysProxyActive)
        {
            WinProxy.ClearProxy();
        }
        else
        {
            WinProxy.SetProxy(_config.ListenHost, _config.HttpPort);
        }
        _sysProxyActive = WinProxy.IsEnabled;
        UpdateSysProxyButton();
    }

    private void UpdateSysProxyButton()
    {
        SysProxyButton.Content = _sysProxyActive ? "取消系统代理" : "设置系统代理";
        SysProxyButton.Background = _sysProxyActive ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26))
                                                    : new SolidColorBrush(Colors.White);
        SysProxyButton.Foreground = _sysProxyActive ? new SolidColorBrush(Colors.White)
                                                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x37, 0x41, 0x51));
        SysProxyButton.BorderBrush = SysProxyButton.Background;
        if (_traySysItem is not null)
        {
            _traySysItem.Text = _sysProxyActive ? "取消系统代理" : "设置系统代理";
        }
    }

    // ------------------------------------------------------------- 规则管理

    private void ReloadRuleList()
    {
        RulesGrid.ItemsSource = null;
        RulesGrid.ItemsSource = _config.Rules.ToList();
    }

    private void OnRuleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not ProxyRule rule)
        {
            return;
        }
        RuleNameBox.Text = rule.Name;
        SelectComboByTag(RuleTypeBox, rule.MatchType);
        RuleValueBox.Text = rule.MatchValue;
        SelectActionProxyComboBox(rule.Action, rule.Proxy);
        RuleNoteBox.Text = rule.Note;
        RuleEnabledBox.IsChecked = rule.Enabled;
    }

    private void OnRuleTypeChanged(object sender, SelectionChangedEventArgs e) => UpdateBrowseButtons();

    private void UpdateBrowseButtons()
    {
        var isProcess = (RuleTypeBox.SelectedItem as ComboBoxItem)?.Tag as string == "process";
        BrowseFileButton.IsEnabled = isProcess;
        BrowseFolderButton.IsEnabled = isProcess;
    }

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要代理的程序",
            Filter = "可执行程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            RuleValueBox.Text = dialog.FileName;
        }
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择文件夹（该文件夹内所有程序生效）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            RuleValueBox.Text = dialog.SelectedPath + "\\";
        }
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var rule = ReadRuleFromEditor();
        if (rule is null)
        {
            return;
        }
        _config.Rules.Add(rule);
        ReloadRuleList();
        RulesGrid.SelectedItem = rule;
        _store.Save(_config);
    }

    /// <summary>规则列表中“启用”复选框单击即生效并保存（无需先选中行再编辑）。</summary>
    private void OnRuleEnabledClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox box && box.DataContext is ProxyRule rule)
        {
            rule.Enabled = box.IsChecked == true;
            _store.Save(_config);
        }
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not ProxyRule rule)
        {
            return;
        }
        _config.Rules.Remove(rule);
        ReloadRuleList();
        _store.Save(_config);
    }

    private void OnMoveRuleUp(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not ProxyRule rule)
        {
            return;
        }
        var index = _config.Rules.IndexOf(rule);
        if (index <= 0)
        {
            return;
        }
        (_config.Rules[index], _config.Rules[index - 1]) = (_config.Rules[index - 1], _config.Rules[index]);
        ReloadRuleList();
        RulesGrid.SelectedItem = _config.Rules[index - 1];
        _store.Save(_config);
    }

    private void OnMoveRuleDown(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not ProxyRule rule)
        {
            return;
        }
        var index = _config.Rules.IndexOf(rule);
        if (index < 0 || index >= _config.Rules.Count - 1)
        {
            return;
        }
        (_config.Rules[index], _config.Rules[index + 1]) = (_config.Rules[index + 1], _config.Rules[index]);
        ReloadRuleList();
        RulesGrid.SelectedItem = _config.Rules[index + 1];
        _store.Save(_config);
    }

    private ProxyRule? ReadRuleFromEditor()
    {
        if (string.IsNullOrWhiteSpace(RuleNameBox.Text) || string.IsNullOrWhiteSpace(RuleValueBox.Text))
        {
            System.Windows.MessageBox.Show(this, "请填写规则名称与匹配值", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return new ProxyRule
        {
            Name = RuleNameBox.Text.Trim(),
            MatchType = (RuleTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "dest_port",
            MatchValue = RuleValueBox.Text.Trim(),
            Action = ReadActionProxyFromEditor().Action,
            Proxy = ReadActionProxyFromEditor().Proxy,
            Note = RuleNoteBox.Text.Trim(),
            Enabled = RuleEnabledBox.IsChecked == true
        };
    }

    // ------------------------------------------------------- 动作/代理合并下拉

    /// <summary>从合并下拉框中读取动作与代理：选中上游项→代理动作；否则按固定项标签。</summary>
    private (string Action, string Proxy) ReadActionProxyFromEditor()
    {
        var item = RuleActionProxyBox.SelectedItem;
        if (item is UpstreamConfig upstream)
        {
            return ("proxy", upstream.Name);
        }
        return ((item as ComboBoxItem)?.Tag as string) == "block"
            ? ("block", "")
            : ("direct", "");
    }

    /// <summary>填充合并下拉框：固定项 直连/阻止，随后每个启用的上游代理一项。</summary>
    private void ReloadActionProxyComboBox()
    {
        RuleActionProxyBox.SelectionChanged -= OnRuleActionProxyChanged;
        try
        {
            RuleActionProxyBox.Items.Clear();
            RuleActionProxyBox.Items.Add(new ComboBoxItem { Content = "直连（不走代理）", Tag = "direct" });
            RuleActionProxyBox.Items.Add(new ComboBoxItem { Content = "阻止（拦截连接）", Tag = "block" });
            foreach (var upstream in _config.Proxies.Where(p => p.Enabled))
            {
                RuleActionProxyBox.Items.Add(upstream);
            }
            RuleActionProxyBox.SelectedIndex = 0;
        }
        finally
        {
            RuleActionProxyBox.SelectionChanged += OnRuleActionProxyChanged;
        }
    }

    /// <summary>按 动作+代理名 定位合并下拉框；action=proxy 精确匹配代理名，找不到（含 proxy="" 旧规则）则选第一个启用的代理。</summary>
    private void SelectActionProxyComboBox(string action, string proxyName)
    {
        if (action != "proxy")
        {
            SelectComboByTag(RuleActionProxyBox, action == "block" ? "block" : "direct");
            return;
        }
        var enabled = _config.Proxies.Where(p => p.Enabled).ToList();
        var index = enabled.FindIndex(p => p.Name == proxyName);
        RuleActionProxyBox.SelectedIndex = index >= 0 ? 2 + index : (enabled.Count > 0 ? 2 : 0);
    }

    private void OnRuleActionProxyChanged(object sender, SelectionChangedEventArgs e) { }

    // ------------------------------------------------------------- 上游管理

    private void ReloadUpstreamList()
    {
        UpstreamsGrid.ItemsSource = null;
        UpstreamsGrid.ItemsSource = _config.Proxies.ToList();
    }

    private void OnUpstreamSelected(object sender, SelectionChangedEventArgs e)
    {
        if (UpstreamsGrid.SelectedItem is not UpstreamConfig upstream)
        {
            return;
        }
        UpNameBox.Text = upstream.Name;
        SelectComboByTag(UpTypeBox, upstream.Type);
        UpHostBox.Text = upstream.Host;
        UpPortBox.Text = upstream.Port.ToString();
        UpUserBox.Text = upstream.Username;
        UpPassBox.Text = upstream.Password;
        UpEnabledBox.IsChecked = upstream.Enabled;
    }

    private void OnAddUpstream(object sender, RoutedEventArgs e)
    {
        var upstream = ReadUpstreamFromEditor();
        if (upstream is null)
        {
            return;
        }
        _config.Proxies.Add(upstream);
        ReloadUpstreamList();
        ReloadActionProxyComboBox();
        UpstreamsGrid.SelectedItem = upstream;
        _store.Save(_config);
    }

    private void OnUpdateUpstream(object sender, RoutedEventArgs e)
    {
        if (UpstreamsGrid.SelectedItem is not UpstreamConfig upstream)
        {
            return;
        }
        var updated = ReadUpstreamFromEditor();
        if (updated is null)
        {
            return;
        }
        var index = _config.Proxies.IndexOf(upstream);
        _config.Proxies[index] = updated;
        ReloadUpstreamList();
        ReloadActionProxyComboBox();
        UpstreamsGrid.SelectedItem = _config.Proxies[index];
        _store.Save(_config);
    }

    private void OnDeleteUpstream(object sender, RoutedEventArgs e)
    {
        if (UpstreamsGrid.SelectedItem is not UpstreamConfig upstream)
        {
            return;
        }
        _config.Proxies.Remove(upstream);
        ReloadUpstreamList();
        ReloadActionProxyComboBox();
        _store.Save(_config);
    }

    private UpstreamConfig? ReadUpstreamFromEditor()
    {
        if (string.IsNullOrWhiteSpace(UpNameBox.Text) || string.IsNullOrWhiteSpace(UpHostBox.Text) ||
            !int.TryParse(UpPortBox.Text, out var port))
        {
            System.Windows.MessageBox.Show(this, "请填写名称、主机与有效端口", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return new UpstreamConfig
        {
            Name = UpNameBox.Text.Trim(),
            Type = (UpTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "http",
            Host = UpHostBox.Text.Trim(),
            Port = port,
            Username = UpUserBox.Text,
            Password = UpPassBox.Text,
            Enabled = UpEnabledBox.IsChecked == true
        };
    }

    // ------------------------------------------------------------- 界面刷新

    private void RefreshTick()
    {
        if (MainTabs.SelectedIndex == 0)
        {
            var snapshot = _engine.Snapshot();
            _connections.Clear();
            foreach (var session in snapshot.Active)
            {
                _connections.Add(session);
            }
            foreach (var session in snapshot.History)
            {
                _connections.Add(session);
            }
            StatsText.Text = $"活动连接 {snapshot.Active.Count} · 累计上行 {FormatBytes(snapshot.TotalUp)} · 累计下行 {FormatBytes(snapshot.TotalDown)}";
        }

        foreach (var line in _engine.DrainLogs())
        {
            AppendLog(line);
        }
    }

    private void OnLogsChanged() => Dispatcher.Invoke(() =>
    {
        foreach (var line in _engine.DrainLogs())
        {
            AppendLog(line);
        }
    });

    private void AppendLog(string line)
    {
        var hasTimestamp = line.Length >= 11 && line[0] == '[' && line[9] == ']';
        var entry = hasTimestamp
            ? new LogEntry(line[1..9], line[11..])
            : new LogEntry("", line);
        _logs.Add(entry);
        if (_logs.Count > 1000)
        {
            _logs.RemoveAt(0);
        }
        LogGrid.ScrollIntoView(entry);
    }

    private void SaveConfig()
    {
        _store.Save(_config);
        System.Windows.MessageBox.Show(this, "配置已保存到 " + _store.ConfigPath, "已保存",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem box && (box.Tag as string) == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    // ------------------------------------------------------------- 托盘

    private void SetupTray()
    {
        var exePath = System.IO.Directory.GetFiles(AppContext.BaseDirectory, "*.exe").FirstOrDefault();
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = (exePath is not null ? System.Drawing.Icon.ExtractAssociatedIcon(exePath) : null)
                   ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "RuleProxy — 分应用 / 分端口代理"
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => ShowFromTray());
        _trayProxyItem = new System.Windows.Forms.ToolStripMenuItem("启动代理");
        _trayProxyItem.Click += (_, _) =>
        {
            if (_engine.Running)
            {
                _engine.Stop();
            }
            else
            {
                StartProxy();
            }
        };
        menu.Items.Add(_trayProxyItem);
        _traySysItem = new System.Windows.Forms.ToolStripMenuItem("设置系统代理");
        _traySysItem.Click += (_, _) => ToggleSystemProxy();
        menu.Items.Add(_traySysItem);
        _trayAutostartItem = new System.Windows.Forms.ToolStripMenuItem("开机自启动")
        {
            Checked = Autostart.IsEnabled
        };
        _trayAutostartItem.Click += (_, _) =>
        {
            Autostart.SetEnabled(!_trayAutostartItem.Checked);
            _trayAutostartItem.Checked = Autostart.IsEnabled;
        };
        menu.Items.Add(_trayAutostartItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("保存配置", null, (_, _) => SaveConfig());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => OnExit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void HideToTray()
    {
        Hide();
        if (_tray is not null)
        {
            _tray.Visible = true;
            _tray.ShowBalloonTip(1200, "RuleProxy", "已最小化到托盘，代理仍在后台运行", System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnExit()
    {
        // 保存上次状态
        _config.LastProxyRunning = _engine.Running;
        _config.LastSysProxyEnabled = _sysProxyActive;
        _store.Save(_config);

        _exiting = true;
        CleanupSystemProxy();
        _engine.Stop();
        _tray?.Dispose();
        _tray = null;
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>退出前清理系统代理：仅当系统代理正指向本程序监听端口时才清除，
    /// 避免误删用户/其他代理软件（如 Clash 的 7890）设置的代理导致断网。</summary>
    private void CleanupSystemProxy()
    {
        if (WinProxy.IsSetTo(_config.ListenHost, _config.HttpPort))
        {
            WinProxy.ClearProxy();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭窗口时保存状态（可能用户直接关窗口而不是点退出）
        _config.LastProxyRunning = _engine.Running;
        _config.LastSysProxyEnabled = _sysProxyActive;
        _store.Save(_config);

        if (!_exiting)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------- 设置页

    private void LoadSettings()
    {
        AutoStartProxyCheckBox.IsChecked = _config.AutoStartProxy;
        RememberLastStateCheckBox.IsChecked = _config.RememberLastState;
        AutostartCheckBox.IsChecked = Autostart.IsEnabled;
    }

    private void UpdateAutostartPathText()
    {
        var path = Autostart.RegistryExePath;
        AutostartPathText.Text = path is not null
            ? $"当前注册路径：{path}"
            : "未设置开机自启动";
    }

    private void OnAutostartCheckChanged(object sender, RoutedEventArgs e)
    {
        var enabled = AutostartCheckBox.IsChecked == true;
        Autostart.SetEnabled(enabled);
        AutostartCheckBox.IsChecked = Autostart.IsEnabled;
        UpdateAutostartPathText();
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        _config.AutoStartProxy = AutoStartProxyCheckBox.IsChecked == true;
        _config.RememberLastState = RememberLastStateCheckBox.IsChecked == true;
        if (!_config.RememberLastState)
        {
            // 取消延续状态时，清除上次记录的状态
            _config.LastProxyRunning = false;
            _config.LastSysProxyEnabled = false;
        }
        _store.Save(_config);
        System.Windows.MessageBox.Show(this, "设置已保存", "RuleProxy",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnSaveConfigFile(object sender, RoutedEventArgs e)
    {
        _store.Save(_config);
        System.Windows.MessageBox.Show(this, "配置已保存到 " + _store.ConfigPath, "已保存",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}