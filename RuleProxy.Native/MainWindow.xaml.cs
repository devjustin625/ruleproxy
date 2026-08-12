using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RuleProxy.Native.Core;

namespace RuleProxy.Native;

/// <summary>主窗口：代理启停、系统代理、规则/上游管理、连接与日志展示、托盘常驻。</summary>
public partial class MainWindow : Window
{
    private readonly ConfigStore _store = new();
    private readonly AppConfig _config;
    private readonly ProxyEngine _engine;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<ConnectionSession> _connections = [];
    private System.Windows.Forms.NotifyIcon? _tray;
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
        ReloadRuleList();
        ReloadUpstreamList();
        SetupTray();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => RefreshTick();
        _timer.Start();

        _sysProxyActive = WinProxy.IsEnabled;
        UpdateSysProxyButton();
        UpdateProxyButtons();
        StatusText.Text = "代理未启动";

        if (startMinimized)
        {
            HideToTray();
        }
    }

    // ------------------------------------------------------------- 代理启停

    private void OnStartProxy(object sender, RoutedEventArgs e) => StartProxy();

    private void StartProxy()
    {
        _config.ListenHost = _config.ListenHost;
        _engine.Start();
    }

    private void OnStopProxy(object sender, RoutedEventArgs e) => _engine.Stop();

    private void OnEngineStateChanged() => Dispatcher.Invoke(UpdateProxyButtons);

    private void UpdateProxyButtons()
    {
        var running = _engine.Running;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        StatusDot.Fill = running ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E))
                                 : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF));
        StatusText.Text = running ? "代理运行中" : "代理未启动";
    }

    // ------------------------------------------------------------- 系统代理

    private void OnToggleSystemProxy(object sender, RoutedEventArgs e)
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
        SelectComboByTag(RuleActionBox, rule.Action);
        RuleProxyBox.Text = rule.Proxy;
        RuleNoteBox.Text = rule.Note;
        RuleEnabledBox.IsChecked = rule.Enabled;
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

    private void OnUpdateRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not ProxyRule rule)
        {
            return;
        }
        var updated = ReadRuleFromEditor();
        if (updated is null)
        {
            return;
        }
        var index = _config.Rules.IndexOf(rule);
        _config.Rules[index] = updated;
        ReloadRuleList();
        RulesGrid.SelectedItem = _config.Rules[index];
        _store.Save(_config);
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
            Action = (RuleActionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "direct",
            Proxy = RuleProxyBox.Text.Trim(),
            Note = RuleNoteBox.Text.Trim(),
            Enabled = RuleEnabledBox.IsChecked == true
        };
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
            LogBox.AppendText(line + "\r\n");
        }
        if (LogBox.Text.Length > 100_000)
        {
            LogBox.Clear();
        }
    }

    private void OnLogsChanged() => Dispatcher.Invoke(() =>
    {
        foreach (var line in _engine.DrainLogs())
        {
            LogBox.AppendText(line + "\r\n");
        }
    });

    private void OnSaveConfig(object sender, RoutedEventArgs e)
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
        menu.Items.Add("启动代理", null, (_, _) => StartProxy());
        menu.Items.Add("停止代理", null, (_, _) => _engine.Stop());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => OnExit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void OnMinimizeToTray(object sender, RoutedEventArgs e) => HideToTray();

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
        _exiting = true;
        _engine.Stop();
        _tray?.Dispose();
        _tray = null;
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }
}