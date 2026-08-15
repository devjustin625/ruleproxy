using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
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
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly bool _startMinimized;
    private readonly ObservableCollection<ConnectionSession> _connections = [];
    private readonly List<ConnectionSession> _allConnections = [];
    private readonly ObservableCollection<LogEntry> _logs = [];
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _trayProxyItem;
    private System.Windows.Forms.ToolStripMenuItem? _traySysItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayAutostartItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayStatusItem;
    private bool _exiting;
    private bool _sysProxyActive;
    private bool _loadingEditor;
    private bool _ruleEditorDirty;
    private bool _upstreamEditorDirty;
    private bool _settingsDirty;
    private bool _loadingSettings;
    private bool _initializingUi = true;
    private ProxyRule? _editingRule;
    private UpstreamConfig? _editingUpstream;

    public MainWindow(bool startMinimized = false)
    {
        InitializeComponent();
        _startMinimized = startMinimized;
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

        _sysProxyActive = WinProxy.IsSetTo(_config.ListenHost, _config.HttpPort);
        UpdateSysProxyButton();
        UpdateProxyButton();
        UpdateBrowseButtons();
        StatusText.Text = "代理未启动";

        // 设置页初始化
        LoadSettings();
        Autostart.SyncToCurrentPath(_config.StartMinimized); // 如果 exe 换了位置，自动更新注册表
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
                RestoreSystemProxyIfStillSet();
            }
        }
        else if (_config.AutoStartProxy)
        {
            StartProxy();
        }
        Loaded += async (_, _) => await CheckForUpdatesAsync(automatic: true, _lifetimeCts.Token);
        _initializingUi = false;
    }

    // ------------------------------------------------------------- 代理启停

    private void OnToggleProxy(object sender, RoutedEventArgs e)
    {
        if (_engine.Running)
        {
            if (WinProxy.IsSetTo(_config.ListenHost, _config.HttpPort))
            {
                var result = System.Windows.MessageBox.Show(this,
                    "系统代理当前正在使用 RuleProxy。停止代理后系统流量可能无法连接。是否同时关闭系统代理？",
                    "停止代理", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Cancel)
                {
                    return;
                }
                if (result == MessageBoxResult.Yes)
                {
                    WinProxy.ClearProxy();
                }
            }
            _engine.Stop();
            SyncSystemProxyState();
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

    private void OnEngineStateChanged() => Dispatcher.Invoke(() =>
    {
        UpdateProxyButton();
        RefreshSystemProxyIfReady();
    });

    private void UpdateProxyButton()
    {
        var running = _engine.Running;
        var httpListening = _engine.HttpListening;
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
        if (_trayStatusItem is not null)
        {
            _trayStatusItem.Text = $"状态：{(running ? "运行中" : "未启动")} · HTTP {(httpListening ? "已监听" : "未监听")} · SOCKS5 {(_engine.Running ? "已监听" : "未监听")}";
            _trayStatusItem.Enabled = false;
        }
        StatusDot.Fill = running ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E))
                                 : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF));
        StatusText.Text = running
            ? (httpListening ? "代理运行中" : "代理运行中（HTTP 未监听）")
            : (_sysProxyActive ? "代理未启动（系统代理仍开启）" : "代理未启动");
    }

    // ------------------------------------------------------------- 系统代理

    private void OnToggleSystemProxy(object sender, RoutedEventArgs e) => ToggleSystemProxy();

    private void ToggleSystemProxy()
    {
        if (WinProxy.IsSetTo(_config.ListenHost, _config.HttpPort))
        {
            WinProxy.ClearProxy();
        }
        else
        {
            EnableSystemProxy();
        }
        SyncSystemProxyState();
    }

    private void EnableSystemProxy()
    {
        if (!_engine.HttpListening)
        {
            StartProxy();
        }
        if (!_engine.HttpListening)
        {
            System.Windows.MessageBox.Show(this, "HTTP 代理监听未就绪，未设置系统代理。", "RuleProxy",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SyncSystemProxyState();
            return;
        }
        WinProxy.SetProxy(_config.ListenHost, _config.HttpPort);
        SyncSystemProxyState();
    }

    private void SyncSystemProxyState()
    {
        var wasActive = _sysProxyActive;
        _sysProxyActive = WinProxy.IsSetTo(_config.ListenHost, _config.HttpPort);
        if (wasActive && !_sysProxyActive)
        {
            AppendLog("系统代理已被其他程序关闭或改写；RuleProxy 未自动恢复。请手动重新设置系统代理。");
        }
        UpdateSysProxyButton();
    }

    private void RestoreSystemProxyIfStillSet()
    {
        if (WinProxy.IsSetTo(_config.ListenHost, _config.HttpPort))
        {
            RefreshSystemProxyIfReady();
            return;
        }

        AppendLog("上次的系统代理未恢复：当前设置不再指向 RuleProxy，未自动覆盖其他程序。可手动设置系统代理恢复流量。");
    }

    private void RefreshSystemProxyIfReady()
    {
        if (_engine.HttpListening && WinProxy.IsSetTo(_config.ListenHost, _config.HttpPort))
        {
            WinProxy.Refresh();
        }
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
        if (_editingRule is not null && !ReferenceEquals(_editingRule, rule) && !ConfirmPendingRuleEdit())
        {
            RulesGrid.SelectedItem = _editingRule;
            return;
        }
        _loadingEditor = true;
        RuleNameBox.Text = rule.Name;
        SelectComboByTag(RuleTypeBox, rule.MatchType);
        RuleValueBox.Text = rule.MatchValue;
        SelectActionProxyComboBox(rule.Action, rule.Proxy);
        RuleNoteBox.Text = rule.Note;
        RuleEnabledBox.IsChecked = rule.Enabled;
        _editingRule = rule;
        _ruleEditorDirty = false;
        _loadingEditor = false;
        UpdateDirtyIndicators();
        UpdateRuleActionButtons();
    }

    private void OnRuleTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBrowseButtons();
        OnRuleEditorSelectionChanged(sender, e);
    }

    private void OnRuleEditorChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingEditor && !_initializingUi)
        {
            _ruleEditorDirty = true;
            UpdateDirtyIndicators();
            UpdateRuleActionButtons();
        }
    }

    private void OnRuleEditorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingEditor && !_initializingUi)
        {
            _ruleEditorDirty = true;
            UpdateDirtyIndicators();
            UpdateRuleActionButtons();
        }
    }

    private void UpdateBrowseButtons()
    {
        var isProcess = (RuleTypeBox.SelectedItem as ComboBoxItem)?.Tag as string == "process";
        BrowseFileButton.IsEnabled = isProcess;
        BrowseFolderButton.IsEnabled = isProcess;
    }

    private void UpdateRuleActionButtons()
    {
        var selectedRule = RulesGrid.SelectedItem as ProxyRule;
        var index = selectedRule is null ? -1 : _config.Rules.IndexOf(selectedRule);
        MoveRuleUpButton.IsEnabled = index > 0;
        MoveRuleDownButton.IsEnabled = index >= 0 && index < _config.Rules.Count - 1;
        DeleteRuleButton.IsEnabled = selectedRule is not null;
        SaveRuleButton.IsEnabled = selectedRule is not null && _ruleEditorDirty;
    }

    private void UpdateUpstreamActionButtons()
    {
        var selected = UpstreamsGrid.SelectedItem is UpstreamConfig;
        TestUpstreamButton.IsEnabled = selected || !string.IsNullOrWhiteSpace(UpHostBox.Text);
        DeleteUpstreamButton.IsEnabled = selected;
        SaveUpstreamButton.IsEnabled = selected && _upstreamEditorDirty;
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

    private void OnNewRule(object sender, RoutedEventArgs e)
    {
        if (!ConfirmPendingRuleEdit())
        {
            return;
        }

        _loadingEditor = true;
        RulesGrid.SelectedItem = null;
        RuleNameBox.Text = "";
        SelectComboByTag(RuleTypeBox, "dest_port");
        RuleValueBox.Text = "";
        SelectActionProxyComboBox("direct", "");
        RuleNoteBox.Text = "";
        RuleEnabledBox.IsChecked = true;
        _editingRule = null;
        _ruleEditorDirty = false;
        _loadingEditor = false;
        UpdateDirtyIndicators();
        UpdateRuleActionButtons();
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
        if (TrySaveConfig())
        {
            _editingRule = rule;
            _ruleEditorDirty = false;
            UpdateDirtyIndicators();
        }
        else
        {
            _config.Rules.Remove(rule);
            ReloadRuleList();
            _editingRule = null;
            UpdateRuleActionButtons();
        }
    }

    private void OnSaveRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not ProxyRule selectedRule)
        {
            System.Windows.MessageBox.Show(this, "请先从列表中选择要修改的规则", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var updatedRule = ReadRuleFromEditor();
        if (updatedRule is null)
        {
            return;
        }
        var backup = new ProxyRule
        {
            Name = selectedRule.Name,
            MatchType = selectedRule.MatchType,
            MatchValue = selectedRule.MatchValue,
            Action = selectedRule.Action,
            Proxy = selectedRule.Proxy,
            Note = selectedRule.Note,
            Enabled = selectedRule.Enabled
        };
        selectedRule.Name = updatedRule.Name;
        selectedRule.MatchType = updatedRule.MatchType;
        selectedRule.MatchValue = updatedRule.MatchValue;
        selectedRule.Action = updatedRule.Action;
        selectedRule.Proxy = updatedRule.Proxy;
        selectedRule.Note = updatedRule.Note;
        selectedRule.Enabled = updatedRule.Enabled;
        ReloadRuleList();
        RulesGrid.SelectedItem = selectedRule;
        if (TrySaveConfig())
        {
            _editingRule = selectedRule;
            _ruleEditorDirty = false;
            UpdateDirtyIndicators();
        }
        else
        {
            selectedRule.Name = backup.Name;
            selectedRule.MatchType = backup.MatchType;
            selectedRule.MatchValue = backup.MatchValue;
            selectedRule.Action = backup.Action;
            selectedRule.Proxy = backup.Proxy;
            selectedRule.Note = backup.Note;
            selectedRule.Enabled = backup.Enabled;
            ReloadRuleList();
            RulesGrid.SelectedItem = selectedRule;
        }
    }

    /// <summary>规则列表中“启用”复选框单击即生效并保存（无需先选中行再编辑）。</summary>
    private void OnRuleEnabledClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox box && box.DataContext is ProxyRule rule)
        {
            var previous = rule.Enabled;
            rule.Enabled = box.IsChecked == true;
            if (!TrySaveConfig())
            {
                rule.Enabled = previous;
                box.IsChecked = previous;
            }
        }
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not ProxyRule rule)
        {
            return;
        }
        var result = System.Windows.MessageBox.Show(this, $"确定删除规则“{rule.Name}”吗？", "确认删除",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }
        _config.Rules.Remove(rule);
        ReloadRuleList();
        TrySaveConfig();
        _editingRule = null;
        _ruleEditorDirty = false;
        UpdateDirtyIndicators();
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
        TrySaveConfig();
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
        TrySaveConfig();
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
        var selectedAction = _editingRule?.Action ?? "direct";
        var selectedProxy = _editingRule?.Proxy ?? "";
        _loadingEditor = true;
        try
        {
            RuleActionProxyBox.Items.Clear();
            RuleActionProxyBox.Items.Add(new ComboBoxItem { Content = "直连（不走代理）", Tag = "direct" });
            RuleActionProxyBox.Items.Add(new ComboBoxItem { Content = "阻止（拦截连接）", Tag = "block" });
            foreach (var upstream in _config.Proxies.Where(p => p.Enabled))
            {
                RuleActionProxyBox.Items.Add(upstream);
            }
            SelectActionProxyComboBox(selectedAction, selectedProxy);
        }
        finally
        {
            _loadingEditor = false;
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
        if (_editingUpstream is not null && !ReferenceEquals(_editingUpstream, upstream) && !ConfirmPendingUpstreamEdit())
        {
            UpstreamsGrid.SelectedItem = _editingUpstream;
            return;
        }
        _loadingEditor = true;
        UpNameBox.Text = upstream.Name;
        SelectComboByTag(UpTypeBox, upstream.Type);
        UpHostBox.Text = upstream.Host;
        UpPortBox.Text = upstream.Port.ToString();
        UpUserBox.Text = upstream.Username;
        UpPassBox.Password = upstream.Password;
        UpEnabledBox.IsChecked = upstream.Enabled;
        _editingUpstream = upstream;
        _upstreamEditorDirty = false;
        _loadingEditor = false;
        UpdateDirtyIndicators();
        UpdateUpstreamActionButtons();
    }

    private void OnUpstreamEditorChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingEditor && !_initializingUi)
        {
            _upstreamEditorDirty = true;
            UpdateDirtyIndicators();
            UpdateUpstreamActionButtons();
        }
    }

    private void OnUpstreamEditorSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OnUpstreamEditorChanged(sender, new RoutedEventArgs());

    /// <summary>上游列表中“启用”复选框单击即生效并保存。</summary>
    private void OnUpstreamEnabledClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox box && box.DataContext is UpstreamConfig upstream)
        {
            var previous = upstream.Enabled;
            upstream.Enabled = box.IsChecked == true;
            ReloadActionProxyComboBox();
            if (!TrySaveConfig())
            {
                upstream.Enabled = previous;
                box.IsChecked = previous;
                ReloadActionProxyComboBox();
            }
        }
    }

    private async void OnTestUpstream(object sender, RoutedEventArgs e)
    {
        var upstream = ReadUpstreamFromEditor();
        if (upstream is null)
        {
            return;
        }

        AppendLog("正在测试上游连接...");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var route = new RouteResult("proxy", upstream, "上游测试");
            await using var stream = await UpstreamClient.ConnectViaAsync(route, "example.com", 443, timeout.Token);
            AppendLog("上游连接测试成功（已完成代理握手）");
            System.Windows.MessageBox.Show(this, "上游代理连接成功，代理握手已完成。", "连接测试",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (!_exiting)
        {
            AppendLog("上游连接测试超时");
            System.Windows.MessageBox.Show(this, "连接测试超时（15 秒）。", "连接测试",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (!_exiting)
        {
            AppendLog($"上游连接测试失败：{ex.Message}");
            System.Windows.MessageBox.Show(this, $"上游连接测试失败：{ex.Message}", "连接测试",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnAddUpstream(object sender, RoutedEventArgs e)
    {
        var upstream = ReadUpstreamFromEditor();
        if (upstream is null)
        {
            return;
        }
        if (!UpstreamNameValidator.IsAvailable(_config.Proxies, upstream.Name))
        {
            System.Windows.MessageBox.Show(this, "上游代理名称已存在，请使用不重复的名称", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _config.Proxies.Add(upstream);
        ReloadUpstreamList();
        ReloadActionProxyComboBox();
        UpstreamsGrid.SelectedItem = upstream;
        if (TrySaveConfig())
        {
            _editingUpstream = upstream;
            _upstreamEditorDirty = false;
            UpdateDirtyIndicators();
        }
        else
        {
            _config.Proxies.Remove(upstream);
            ReloadUpstreamList();
            ReloadActionProxyComboBox();
            _editingUpstream = null;
            UpdateUpstreamActionButtons();
        }
    }

    private void OnNewUpstream(object sender, RoutedEventArgs e)
    {
        if (!ConfirmPendingUpstreamEdit())
        {
            return;
        }

        _loadingEditor = true;
        UpstreamsGrid.SelectedItem = null;
        UpNameBox.Text = "";
        SelectComboByTag(UpTypeBox, "http");
        UpHostBox.Text = "127.0.0.1";
        UpPortBox.Text = "7890";
        UpUserBox.Text = "";
        UpPassBox.Password = "";
        UpEnabledBox.IsChecked = true;
        _editingUpstream = null;
        _upstreamEditorDirty = false;
        _loadingEditor = false;
        UpdateDirtyIndicators();
        UpdateUpstreamActionButtons();
    }

    private void OnSaveUpstream(object sender, RoutedEventArgs e)
    {
        if (UpstreamsGrid.SelectedItem is not UpstreamConfig selectedUpstream)
        {
            System.Windows.MessageBox.Show(this, "请先从列表中选择要修改的上游代理", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var updatedUpstream = ReadUpstreamFromEditor();
        if (updatedUpstream is null)
        {
            return;
        }
        if (!UpstreamNameValidator.IsAvailable(_config.Proxies, updatedUpstream.Name, selectedUpstream))
        {
            System.Windows.MessageBox.Show(this, "上游代理名称已存在，请使用不重复的名称", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var previousName = selectedUpstream.Name;
        var upstreamBackup = new UpstreamConfig
        {
            Name = selectedUpstream.Name,
            Type = selectedUpstream.Type,
            Host = selectedUpstream.Host,
            Port = selectedUpstream.Port,
            Username = selectedUpstream.Username,
            Password = selectedUpstream.Password,
            Enabled = selectedUpstream.Enabled
        };
        selectedUpstream.Name = updatedUpstream.Name;
        selectedUpstream.Type = updatedUpstream.Type;
        selectedUpstream.Host = updatedUpstream.Host;
        selectedUpstream.Port = updatedUpstream.Port;
        selectedUpstream.Username = updatedUpstream.Username;
        selectedUpstream.Password = updatedUpstream.Password;
        selectedUpstream.Enabled = updatedUpstream.Enabled;

        if (!string.Equals(previousName, selectedUpstream.Name, StringComparison.Ordinal))
        {
            foreach (var rule in _config.Rules.Where(rule => rule.Proxy == previousName))
            {
                rule.Proxy = selectedUpstream.Name;
            }
            if (_config.DefaultProxy == previousName)
            {
                _config.DefaultProxy = selectedUpstream.Name;
            }
            ReloadRuleList();
        }
        ReloadUpstreamList();
        ReloadActionProxyComboBox();
        UpstreamsGrid.SelectedItem = selectedUpstream;
        if (TrySaveConfig())
        {
            _editingUpstream = selectedUpstream;
            _upstreamEditorDirty = false;
            UpdateDirtyIndicators();
        }
        else
        {
            selectedUpstream.Name = upstreamBackup.Name;
            selectedUpstream.Type = upstreamBackup.Type;
            selectedUpstream.Host = upstreamBackup.Host;
            selectedUpstream.Port = upstreamBackup.Port;
            selectedUpstream.Username = upstreamBackup.Username;
            selectedUpstream.Password = upstreamBackup.Password;
            selectedUpstream.Enabled = upstreamBackup.Enabled;
            ReloadUpstreamList();
            ReloadActionProxyComboBox();
            UpstreamsGrid.SelectedItem = selectedUpstream;
        }
    }

    private void OnDeleteUpstream(object sender, RoutedEventArgs e)
    {
        if (UpstreamsGrid.SelectedItem is not UpstreamConfig upstream)
        {
            return;
        }
        var references = _config.Rules
            .Where(rule => string.Equals(rule.Proxy, upstream.Name, StringComparison.OrdinalIgnoreCase))
            .Select(rule => rule.Name)
            .ToList();
        var referenceText = references.Count == 0
            ? "没有规则引用此上游。"
            : $"以下规则仍引用它，删除后这些规则将按‘无可用代理’阻止连接：{Environment.NewLine}" +
              string.Join(Environment.NewLine, references.Select(name => "· " + name));
        var result = System.Windows.MessageBox.Show(this, $"确定删除上游“{upstream.Name}”吗？{Environment.NewLine}{referenceText}",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }
        _config.Proxies.Remove(upstream);
        ReloadUpstreamList();
        ReloadActionProxyComboBox();
        TrySaveConfig();
        _editingUpstream = null;
        _upstreamEditorDirty = false;
        UpdateDirtyIndicators();
    }

    private UpstreamConfig? ReadUpstreamFromEditor()
    {
        if (string.IsNullOrWhiteSpace(UpNameBox.Text) || string.IsNullOrWhiteSpace(UpHostBox.Text) ||
            !int.TryParse(UpPortBox.Text, out var port))
        {
            System.Windows.MessageBox.Show(this, "请填写名称、主机与 1-65535 范围内的有效端口", "提示",
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
            Password = UpPassBox.Password,
            Enabled = UpEnabledBox.IsChecked == true
        };
    }

    private bool ConfirmPendingRuleEdit()
    {
        if (!_ruleEditorDirty)
        {
            return true;
        }

        var result = System.Windows.MessageBox.Show(this, "当前规则有未保存修改，要保存吗？", "未保存修改",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }
        if (result == MessageBoxResult.Yes)
        {
            OnSaveRule(this, new RoutedEventArgs());
            return !_ruleEditorDirty;
        }

        if (_editingRule is not null)
        {
            _loadingEditor = true;
            RuleNameBox.Text = _editingRule.Name;
            SelectComboByTag(RuleTypeBox, _editingRule.MatchType);
            RuleValueBox.Text = _editingRule.MatchValue;
            SelectActionProxyComboBox(_editingRule.Action, _editingRule.Proxy);
            RuleNoteBox.Text = _editingRule.Note;
            RuleEnabledBox.IsChecked = _editingRule.Enabled;
            _loadingEditor = false;
        }
        _ruleEditorDirty = false;
        UpdateDirtyIndicators();
        return true;
    }

    private bool ConfirmPendingUpstreamEdit()
    {
        if (!_upstreamEditorDirty)
        {
            return true;
        }

        var result = System.Windows.MessageBox.Show(this, "当前上游代理有未保存修改，要保存吗？", "未保存修改",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }
        if (result == MessageBoxResult.Yes)
        {
            OnSaveUpstream(this, new RoutedEventArgs());
            return !_upstreamEditorDirty;
        }

        if (_editingUpstream is not null)
        {
            _loadingEditor = true;
            UpNameBox.Text = _editingUpstream.Name;
            SelectComboByTag(UpTypeBox, _editingUpstream.Type);
            UpHostBox.Text = _editingUpstream.Host;
            UpPortBox.Text = _editingUpstream.Port.ToString();
            UpUserBox.Text = _editingUpstream.Username;
            UpPassBox.Password = _editingUpstream.Password;
            UpEnabledBox.IsChecked = _editingUpstream.Enabled;
            _loadingEditor = false;
        }
        _upstreamEditorDirty = false;
        UpdateDirtyIndicators();
        return true;
    }

    private void UpdateDirtyIndicators()
    {
        var dirty = _ruleEditorDirty || _upstreamEditorDirty || _settingsDirty;
        if (SettingsDirtyText is not null)
        {
            SettingsDirtyText.Visibility = _settingsDirty ? Visibility.Visible : Visibility.Collapsed;
        }
        if (SaveSettingsButton is not null)
        {
            SaveSettingsButton.Content = _settingsDirty ? "保存设置 *" : "保存设置";
        }
        Title = dirty ? "RuleProxy — 有未保存修改" : "RuleProxy — 分应用 / 分端口代理";
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingSettings && !_initializingUi)
        {
            _settingsDirty = true;
            UpdateDirtyIndicators();
        }
    }

    // ------------------------------------------------------------- 界面刷新

    private void RefreshTick()
    {
        SyncSystemProxyState();
        if (MainTabs.SelectedIndex == 0)
        {
            var snapshot = _engine.Snapshot();
            _allConnections.Clear();
            _allConnections.AddRange(snapshot.Active);
            _allConnections.AddRange(snapshot.History);
            _connections.Clear();
            foreach (var session in FilterConnections(_allConnections))
                _connections.Add(session);
            StatsText.Text = $"活动连接 {snapshot.Active.Count} · 累计上行 {FormatBytes(snapshot.TotalUp)} · 累计下行 {FormatBytes(snapshot.TotalDown)}";
        }

        foreach (var line in _engine.DrainLogs())
        {
            AppendLog(line);
        }
    }

    private IEnumerable<ConnectionSession> FilterConnections(IEnumerable<ConnectionSession> sessions)
    {
        var search = ConnectionSearchBox.Text.Trim();
        var status = (ConnectionStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        return sessions.Where(session =>
            (string.IsNullOrEmpty(search) ||
             session.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             session.Destination.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             session.RuleName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             session.Status.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(status) ||
             status == "active" && !session.Done ||
             status != "active" && session.Status.Contains(status, StringComparison.OrdinalIgnoreCase)));
    }

    private void OnConnectionFilterChanged(object sender, RoutedEventArgs e)
    {
        _connections.Clear();
        foreach (var session in FilterConnections(_allConnections))
            _connections.Add(session);
    }

    private void OnClearConnectionHistory(object sender, RoutedEventArgs e)
    {
        _engine.ClearHistory();
        _allConnections.RemoveAll(session => session.Done);
        _connections.Clear();
        foreach (var session in FilterConnections(_allConnections))
            _connections.Add(session);
    }

    private void OnExportConnections(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出连接记录",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"ruleproxy-connections-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.WriteLine("时间,PID,进程,目标,规则,动作,状态,上行字节,下行字节");
            foreach (var session in FilterConnections(_allConnections))
            {
                writer.WriteLine(string.Join(",", new[]
                {
                    CsvEscape(session.Timestamp),
                    CsvEscape(session.Pid?.ToString() ?? ""),
                    CsvEscape(session.ProcessName),
                    CsvEscape(session.Destination),
                    CsvEscape(session.RuleName),
                    CsvEscape(session.ActionText),
                    CsvEscape(session.Status),
                    session.UpBytes.ToString(),
                    session.DownBytes.ToString()
                }));
            }
            AppendLog($"已导出连接记录：{dialog.FileName}");
            System.Windows.MessageBox.Show(this, "连接记录已导出。", "导出成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(this, $"导出失败：{ex.Message}", "导出失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static string CsvEscape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private void OnConnectionDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ConnectionsGrid.SelectedItem is not ConnectionSession session)
            return;

        var details = $"时间：{session.Timestamp}{Environment.NewLine}" +
                      $"PID：{session.Pid?.ToString() ?? "未知"}{Environment.NewLine}" +
                      $"进程：{session.ProcessName}{Environment.NewLine}" +
                      $"源端口：{session.SrcPort}{Environment.NewLine}" +
                      $"目标：{session.Destination}{Environment.NewLine}" +
                      $"规则：{session.RuleName}{Environment.NewLine}" +
                      $"动作：{session.ActionText}{Environment.NewLine}" +
                      $"状态：{session.Status}{Environment.NewLine}" +
                      $"上行：{FormatBytes(session.UpBytes)}{Environment.NewLine}" +
                      $"下行：{FormatBytes(session.DownBytes)}";
        System.Windows.MessageBox.Show(this, details, "连接详情", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (TrySaveConfig())
        {
            System.Windows.MessageBox.Show(this, "配置已保存到 " + _store.ConfigPath, "已保存",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool TrySaveConfig()
    {
        try
        {
            _store.Save(_config);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            AppendLog($"配置保存失败：{e.Message}");
            System.Windows.MessageBox.Show(this, $"配置保存失败：{e.Message}", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
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
        _trayStatusItem = new System.Windows.Forms.ToolStripMenuItem("状态：未启动") { Enabled = false };
        menu.Items.Add(_trayStatusItem);
        _traySysItem = new System.Windows.Forms.ToolStripMenuItem("设置系统代理");
        _traySysItem.Click += (_, _) => ToggleSystemProxy();
        menu.Items.Add(_traySysItem);
        _trayAutostartItem = new System.Windows.Forms.ToolStripMenuItem("开机自启动")
        {
            Checked = Autostart.IsEnabled
        };
        _trayAutostartItem.Click += (_, _) =>
        {
            Autostart.SetEnabled(!_trayAutostartItem.Checked, _config.StartMinimized);
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

    internal void HideToTray(bool showNotification = true)
    {
        Hide();
        if (showNotification && _tray is not null)
        {
            _tray.Visible = true;
            _tray.ShowBalloonTip(1200, "RuleProxy", "已最小化到托盘，代理仍在后台运行", System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    internal void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnExit()
    {
        if (_exiting)
        {
            return;
        }
        if (!ConfirmPendingChanges())
        {
            return;
        }
        _exiting = true;
        _lifetimeCts.Cancel();
        _timer.Stop();
        try
        {
            SaveLastState();
        }
        finally
        {
            try { CleanupSystemProxy(); } catch { }
            _engine.StateChanged -= OnEngineStateChanged;
            _engine.LogsChanged -= OnLogsChanged;
            try { _engine.Stop(); } catch { }
            _tray?.Dispose();
            _tray = null;
            _lifetimeCts.Dispose();
            System.Windows.Application.Current.Shutdown();
        }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(automatic: false, _lifetimeCts.Token);

    private async Task CheckForUpdatesAsync(bool automatic, CancellationToken cancellationToken)
    {
        if (automatic && !_config.CheckUpdates)
        {
            return;
        }

        if (_exiting)
        {
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        try
        {
            var updateService = new UpdateService();
            var update = await updateService.CheckForUpdateAsync(cancellationToken);
            if (_exiting)
            {
                return;
            }
            if (update is null)
            {
                if (!automatic)
                {
                    System.Windows.MessageBox.Show(this, "当前已是最新版本，或暂时无法检查更新。", "RuleProxy",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            AppendLog($"发现新版本 {update.TagName}");
            if (automatic && _startMinimized)
            {
                return;
            }
            var result = System.Windows.MessageBox.Show(this, $"发现新版本 {update.TagName}，现在下载并更新吗？", "RuleProxy 更新",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var downloadedExe = await updateService.DownloadAsync(update, cancellationToken);
            if (_exiting)
            {
                return;
            }
            if (downloadedExe is null)
            {
                System.Windows.MessageBox.Show(this, "更新下载失败或文件验证失败。", "RuleProxy 更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StartUpdaterAndShutdown(downloadedExe);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_exiting)
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }
    }

    private void StartUpdaterAndShutdown(string downloadedExe)
    {
        var targetExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(targetExe) || !File.Exists(targetExe))
        {
            System.Windows.MessageBox.Show(this, "无法确定当前程序路径，未执行更新。", "RuleProxy 更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            downloadedExe = Path.GetFullPath(downloadedExe);
            targetExe = Path.GetFullPath(targetExe);
            var updaterExe = Path.Combine(Path.GetDirectoryName(downloadedExe)!, $"updater-{Guid.NewGuid():N}.exe");
            File.Copy(targetExe, updaterExe, true);
            var startInfo = new ProcessStartInfo(updaterExe) { UseShellExecute = false };
            foreach (var argument in UpdateService.BuildApplyUpdateArguments(Environment.ProcessId, downloadedExe, targetExe, _startMinimized))
            {
                startInfo.ArgumentList.Add(argument);
            }
            Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show(this, "无法启动更新程序。", "RuleProxy 更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExitForUpdate();
    }

    private void ExitForUpdate() => OnExit();

    private void SaveLastState()
    {
        _config.LastProxyRunning = _engine.Running;
        _config.LastSysProxyEnabled = _sysProxyActive;
        TrySaveConfig();
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
        if (!_exiting)
        {
            if (!ConfirmPendingChanges())
            {
                e.Cancel = true;
                return;
            }
            SaveLastState();
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    private bool ConfirmPendingChanges()
    {
        if (!ConfirmPendingRuleEdit() || !ConfirmPendingUpstreamEdit())
        {
            return false;
        }
        if (!_settingsDirty)
        {
            return true;
        }

        var result = System.Windows.MessageBox.Show(this, "设置页有未保存修改，要保存吗？", "未保存修改",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }
        if (result == MessageBoxResult.Yes)
        {
            OnSaveSettings(this, new RoutedEventArgs());
            return !_settingsDirty;
        }

        _settingsDirty = false;
        UpdateDirtyIndicators();
        return true;
    }

    // ------------------------------------------------------------- 设置页

    private void LoadSettings()
    {
        _loadingSettings = true;
        StartMinimizedCheckBox.IsChecked = _config.StartMinimized;
        AutoStartProxyCheckBox.IsChecked = _config.AutoStartProxy;
        RememberLastStateCheckBox.IsChecked = _config.RememberLastState;
        CheckUpdatesCheckBox.IsChecked = _config.CheckUpdates;
        CurrentVersionText.Text = $"当前版本：v{UpdateService.CurrentVersion.ToString(3)}";
        AutostartCheckBox.IsChecked = Autostart.IsEnabled;
        _settingsDirty = false;
        _loadingSettings = false;
        UpdateDirtyIndicators();
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
        Autostart.SetEnabled(enabled, _config.StartMinimized);
        AutostartCheckBox.IsChecked = Autostart.IsEnabled;
        UpdateAutostartPathText();
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        _config.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
        _config.AutoStartProxy = AutoStartProxyCheckBox.IsChecked == true;
        _config.RememberLastState = RememberLastStateCheckBox.IsChecked == true;
        _config.CheckUpdates = CheckUpdatesCheckBox.IsChecked == true;
        if (!_config.RememberLastState)
        {
            // 取消延续状态时，清除上次记录的状态
            _config.LastProxyRunning = false;
            _config.LastSysProxyEnabled = false;
        }
        if (!TrySaveConfig())
        {
            return;
        }
        _settingsDirty = false;
        UpdateDirtyIndicators();
        if (Autostart.IsEnabled)
        {
            Autostart.SetEnabled(true, _config.StartMinimized);
        }
        System.Windows.MessageBox.Show(this, "设置已保存", "RuleProxy",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnSaveConfigFile(object sender, RoutedEventArgs e)
    {
        SaveConfig();
    }
}