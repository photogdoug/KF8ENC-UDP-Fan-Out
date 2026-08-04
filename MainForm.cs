using System.Drawing;
using System.Windows.Forms;

namespace WsjtxUdpFanout;

internal sealed class MainForm : Form
{
    private static readonly Color Navy = Color.FromArgb(25, 55, 92);
    private static readonly Color Blue = Color.FromArgb(35, 103, 176);
    private static readonly Color Green = Color.FromArgb(33, 145, 89);
    private static readonly Color Red = Color.FromArgb(190, 57, 57);
    private static readonly Color Surface = Color.White;
    private static readonly Color Page = Color.FromArgb(243, 246, 249);
    private static readonly Color Muted = Color.FromArgb(95, 108, 122);

    private readonly RelayService _relay;
    private readonly TextBox _listenAddress = new();
    private readonly NumericUpDown _listenPort = new();
    private readonly ComboBox _mode = new();
    private readonly ComboBox _themeSelector = new();
    private readonly Button _startStopButton = new();
    private readonly Label _statusBadge = new();
    private readonly Label _wsjtxPackets = new();
    private readonly Label _appPackets = new();
    private readonly Label _droppedPackets = new();
    private readonly Label _sendErrors = new();
    private readonly Label _sourceValue = new();
    private readonly Label _lastPacketValue = new();
    private readonly Label _footerStatus = new();
    private readonly Label _footerConfig = new();
    private readonly DataGridView _destinationGrid = new();
    private readonly ListBox _eventList = new();
    private readonly DestinationTrafficChart _trafficChart = new();
    private readonly FlowLayoutPanel _trafficLegend = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer _trafficTimer = new() { Interval = 1000 };
    private bool _allowClose;
    private bool _settingsInitialized;
    private bool _themeInitialized;
    private AppTheme _currentTheme = AppThemes.Light;
    private readonly Dictionary<Control, ThemeBinding> _themeBindings = [];

    public MainForm(RelayService relay)
    {
        _relay = relay;
        Text = "WSJT-X UDP Fanout by KF8ENC";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 720);
        ClientSize = new Size(1180, 880);
        BackColor = Page;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildInterface();
        CaptureThemeBindings(this, ThemeRole.Page);
        LoadSettingsFromSnapshot(_relay.GetSnapshot());
        _mode.SelectedIndexChanged += Mode_SelectedIndexChanged;
        _themeSelector.SelectedIndexChanged += ThemeSelector_SelectedIndexChanged;

        Shown += MainForm_Shown;
        FormClosing += MainForm_FormClosing;
        _refreshTimer.Tick += (_, _) => RefreshDashboard();
        _trafficTimer.Tick += (_, _) => RefreshTrafficChart();
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Page,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildConnectionPanel(), 0, 1);
        root.Controls.Add(BuildStatisticsPanel(), 0, 2);
        root.Controls.Add(BuildMainContent(), 0, 3);
        root.Controls.Add(BuildFooter(), 0, 4);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Navy, Padding = new Padding(22, 12, 22, 10) };
        var title = new Label
        {
            AutoSize = true,
            Text = "WSJT-X UDP Fanout by KF8ENC",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold),
            Location = new Point(20, 10)
        };
        var subtitle = new Label
        {
            AutoSize = true,
            Text = "Reliable UDP routing for your WSJT-X companion applications",
            ForeColor = Color.FromArgb(202, 217, 232),
            Font = new Font("Segoe UI", 9.5F),
            Location = new Point(23, 46)
        };
        _statusBadge.AutoSize = false;
        _statusBadge.Size = new Size(112, 32);
        _statusBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _statusBadge.Location = new Point(panel.Width - 136, 21);
        _statusBadge.TextAlign = ContentAlignment.MiddleCenter;
        _statusBadge.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
        _statusBadge.ForeColor = Color.White;
        _statusBadge.BackColor = Muted;
        var themeLabel = new Label
        {
            AutoSize = true,
            Text = "THEME",
            ForeColor = Color.FromArgb(202, 217, 232),
            Font = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold)
        };
        _themeSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _themeSelector.FlatStyle = FlatStyle.Flat;
        ConfigureThemedCombo(_themeSelector);
        _themeSelector.Items.AddRange(AppThemes.All.Select(theme => theme.Name).Cast<object>().ToArray());
        _themeSelector.Size = new Size(130, 27);
        _themeSelector.Font = new Font("Segoe UI", 9F);
        _themeSelector.AccessibleName = "Application theme";
        void PositionHeaderControls()
        {
            _statusBadge.Left = panel.ClientSize.Width - _statusBadge.Width - 22;
            _themeSelector.Left = _statusBadge.Left - _themeSelector.Width - 18;
            _themeSelector.Top = 31;
            themeLabel.Left = _themeSelector.Left + 1;
            themeLabel.Top = 13;
        }
        panel.Resize += (_, _) => PositionHeaderControls();
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        panel.Controls.Add(themeLabel);
        panel.Controls.Add(_themeSelector);
        panel.Controls.Add(_statusBadge);
        PositionHeaderControls();
        return panel;
    }

    private Control BuildConnectionPanel()
    {
        var outer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 8), BackColor = Page };
        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            ColumnCount = 9,
            RowCount = 2,
            Padding = new Padding(14, 8, 14, 8)
        };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));

        AddFieldLabel(card, "LISTEN ADDRESS", 0, 0, 2);
        AddFieldLabel(card, "PORT", 3, 0, 2);
        AddFieldLabel(card, "RELAY MODE", 6, 0, 2);

        _listenAddress.Dock = DockStyle.Fill;
        _listenAddress.Margin = new Padding(0, 5, 0, 4);
        _listenAddress.BorderStyle = BorderStyle.FixedSingle;
        _listenAddress.Font = new Font("Segoe UI", 10F);
        card.Controls.Add(_listenAddress, 0, 1);
        card.SetColumnSpan(_listenAddress, 2);

        _listenPort.Minimum = 1;
        _listenPort.Maximum = 65535;
        _listenPort.Dock = DockStyle.Fill;
        _listenPort.Margin = new Padding(0, 5, 0, 4);
        _listenPort.Font = new Font("Segoe UI", 10F);
        card.Controls.Add(_listenPort, 3, 1);
        card.SetColumnSpan(_listenPort, 2);

        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.FlatStyle = FlatStyle.Flat;
        ConfigureThemedCombo(_mode);
        _mode.Items.AddRange(["Bidirectional", "Read only"]);
        _mode.Dock = DockStyle.Fill;
        _mode.Margin = new Padding(0, 5, 8, 4);
        _mode.Font = new Font("Segoe UI", 10F);
        card.Controls.Add(_mode, 6, 1);
        card.SetColumnSpan(_mode, 2);

        _startStopButton.Text = "Start relay";
        _startStopButton.Dock = DockStyle.Fill;
        _startStopButton.Margin = new Padding(8, 4, 0, 3);
        StylePrimaryButton(_startStopButton, Green);
        _startStopButton.Click += StartStopButton_Click;
        card.Controls.Add(_startStopButton, 8, 1);

        outer.Controls.Add(card);
        return outer;
    }

    private Control BuildStatisticsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(18, 4, 18, 12),
            BackColor = Page
        };
        for (int i = 0; i < 4; i++)
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        panel.Controls.Add(BuildStatCard("WSJT-X → APPS", _wsjtxPackets, "Packets forwarded", Blue), 0, 0);
        panel.Controls.Add(BuildStatCard("APPS → WSJT-X", _appPackets, "Commands returned", Green), 1, 0);
        panel.Controls.Add(BuildStatCard("DROPPED", _droppedPackets, "Packets not routed", Color.FromArgb(218, 138, 38)), 2, 0);
        panel.Controls.Add(BuildStatCard("SEND ERRORS", _sendErrors, "Network send failures", Red), 3, 0);
        return panel;
    }

    private static Control BuildStatCard(string heading, Label valueLabel, string detail, Color accent)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = new Padding(0, 0, 12, 0), Padding = new Padding(15, 12, 12, 8) };
        var stripe = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = accent };
        var headingLabel = new Label
        {
            AutoSize = true,
            Text = heading,
            ForeColor = Muted,
            Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold),
            Location = new Point(17, 12)
        };
        valueLabel.AutoSize = true;
        valueLabel.Text = "0";
        valueLabel.ForeColor = Color.FromArgb(32, 43, 55);
        valueLabel.Font = new Font("Segoe UI Semibold", 22F, FontStyle.Bold);
        valueLabel.Location = new Point(15, 30);
        var detailLabel = new Label
        {
            AutoSize = true,
            Text = detail,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.5F),
            Location = new Point(18, 72)
        };
        card.Controls.Add(stripe);
        card.Controls.Add(headingLabel);
        card.Controls.Add(valueLabel);
        card.Controls.Add(detailLabel);
        return card;
    }

    private Control BuildMainContent()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 8,
            BackColor = Page,
            Padding = new Padding(18, 0, 18, 12)
        };
        bool splitInitialized = false;
        split.SizeChanged += (_, _) =>
        {
            if (splitInitialized || split.ClientSize.Width < 850)
                return;
            split.Panel1MinSize = 480;
            split.Panel2MinSize = 250;
            split.SplitterDistance = Math.Max(480, (int)(split.ClientSize.Width * 0.64));
            splitInitialized = true;
        };
        split.Panel1.Padding = new Padding(0, 0, 4, 0);
        split.Panel2.Padding = new Padding(4, 0, 0, 0);
        split.Panel1.Controls.Add(BuildDestinationsCard());
        split.Panel2.Controls.Add(BuildActivityCard());
        return split;
    }

    private Control BuildDestinationsCard()
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, BackColor = Surface, Padding = new Padding(14, 12, 14, 12) };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 62));

        var heading = new Label { AutoSize = true, Text = "Destinations", Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold), ForeColor = Color.FromArgb(35, 45, 57), Margin = new Padding(0, 3, 0, 0) };
        card.Controls.Add(heading, 0, 0);

        ConfigureDestinationGrid();
        card.Controls.Add(_destinationGrid, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 7, 0, 0), WrapContents = false };
        Button add = CreateActionButton("Add destination", Blue);
        Button edit = CreateActionButton("Edit", Color.FromArgb(89, 103, 119));
        Button remove = CreateActionButton("Remove", Red);
        Button clear = CreateActionButton("Clear statistics", Color.FromArgb(89, 103, 119));
        add.Click += (_, _) => AddDestination();
        edit.Click += (_, _) => EditSelectedDestination();
        remove.Click += (_, _) => RemoveSelectedDestination();
        clear.Click += (_, _) => _relay.ClearStatistics();
        buttons.Controls.AddRange([add, edit, remove, clear]);
        card.Controls.Add(buttons, 0, 2);

        var chartHeading = new Label
        {
            AutoSize = true,
            Text = "Total traffic  ·  packets/second  ·  last 60 seconds",
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(55, 66, 78),
            Margin = new Padding(0, 6, 0, 0)
        };
        card.Controls.Add(chartHeading, 0, 3);

        _trafficLegend.Dock = DockStyle.Fill;
        _trafficLegend.FlowDirection = FlowDirection.LeftToRight;
        _trafficLegend.WrapContents = true;
        _trafficLegend.Margin = Padding.Empty;
        _trafficLegend.Padding = Padding.Empty;
        card.Controls.Add(_trafficLegend, 0, 4);

        _trafficChart.Dock = DockStyle.Fill;
        _trafficChart.Margin = Padding.Empty;
        card.Controls.Add(_trafficChart, 0, 5);
        return card;
    }

    private Control BuildActivityCard()
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = Surface, Padding = new Padding(14, 12, 14, 12) };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(new Label { AutoSize = true, Text = "Relay activity", Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold), ForeColor = Color.FromArgb(35, 45, 57), Margin = new Padding(0, 3, 0, 0) }, 0, 0);
        card.Controls.Add(BuildInfoRow("WSJT-X source", _sourceValue), 0, 1);
        card.Controls.Add(BuildInfoRow("Last packet", _lastPacketValue), 0, 2);
        card.Controls.Add(new Label { AutoSize = true, Text = "RECENT EVENTS", ForeColor = Muted, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), Margin = new Padding(0, 7, 0, 0) }, 0, 3);
        _eventList.Dock = DockStyle.Fill;
        _eventList.BorderStyle = BorderStyle.None;
        _eventList.BackColor = Color.FromArgb(248, 250, 252);
        _eventList.ForeColor = Color.FromArgb(58, 69, 81);
        _eventList.Font = new Font("Segoe UI", 8.5F);
        _eventList.HorizontalScrollbar = true;
        card.Controls.Add(_eventList, 0, 4);
        return card;
    }

    private Control BuildFooter()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(230, 235, 240), Padding = new Padding(18, 4, 18, 3) };
        _footerStatus.AutoSize = true;
        _footerStatus.ForeColor = Color.FromArgb(63, 75, 88);
        _footerStatus.Location = new Point(18, 6);
        _footerConfig.AutoSize = true;
        _footerConfig.ForeColor = Muted;
        _footerConfig.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        panel.Resize += (_, _) => _footerConfig.Left = Math.Max(20, panel.ClientSize.Width - _footerConfig.Width - 18);
        panel.Controls.Add(_footerStatus);
        panel.Controls.Add(_footerConfig);
        return panel;
    }

    private static Control BuildInfoRow(string heading, Label value)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        panel.Controls.Add(new Label { AutoSize = true, Text = heading.ToUpperInvariant(), ForeColor = Muted, Font = new Font("Segoe UI Semibold", 7.5F, FontStyle.Bold), Location = new Point(0, 3) });
        value.AutoEllipsis = true;
        value.ForeColor = Color.FromArgb(35, 45, 57);
        value.Font = new Font("Segoe UI", 9F);
        value.Location = new Point(0, 21);
        value.Size = new Size(360, 22);
        value.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        panel.Controls.Add(value);
        return panel;
    }

    private void ConfigureDestinationGrid()
    {
        _destinationGrid.Dock = DockStyle.Fill;
        _destinationGrid.BackgroundColor = Surface;
        _destinationGrid.BorderStyle = BorderStyle.None;
        _destinationGrid.AllowUserToAddRows = false;
        _destinationGrid.AllowUserToDeleteRows = false;
        _destinationGrid.AllowUserToResizeRows = false;
        _destinationGrid.ReadOnly = true;
        _destinationGrid.MultiSelect = false;
        _destinationGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _destinationGrid.RowHeadersVisible = false;
        _destinationGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _destinationGrid.EnableHeadersVisualStyles = false;
        _destinationGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 240, 245);
        _destinationGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(59, 70, 82);
        _destinationGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold);
        _destinationGrid.ColumnHeadersHeight = 34;
        _destinationGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 234, 248);
        _destinationGrid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 48, 67);
        _destinationGrid.DefaultCellStyle.Padding = new Padding(4, 2, 4, 2);
        _destinationGrid.RowTemplate.Height = 30;
        _destinationGrid.CellDoubleClick += (_, args) => { if (args.RowIndex >= 0) EditSelectedDestination(); };
        _destinationGrid.Columns.Add("Name", "Name");
        _destinationGrid.Columns.Add("Endpoint", "Endpoint");
        _destinationGrid.Columns.Add("Packets", "Packets");
        _destinationGrid.Columns.Add("Bytes", "Bytes");
        _destinationGrid.Columns.Add("Errors", "Errors");
        _destinationGrid.Columns[0].FillWeight = 150;
        _destinationGrid.Columns[1].FillWeight = 120;
        _destinationGrid.Columns[2].FillWeight = 75;
        _destinationGrid.Columns[3].FillWeight = 85;
        _destinationGrid.Columns[4].FillWeight = 60;
    }

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        await _relay.StartAsync();
        RefreshDashboard();
        RefreshTrafficChart();
        _refreshTimer.Start();
        _trafficTimer.Start();
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        Enabled = false;
        _refreshTimer.Stop();
        _trafficTimer.Stop();
        await _relay.StopAsync();
        _allowClose = true;
        Close();
    }

    private async void StartStopButton_Click(object? sender, EventArgs e)
    {
        RelaySnapshot snapshot = _relay.GetSnapshot();
        _startStopButton.Enabled = false;
        try
        {
            if (snapshot.IsRunning)
            {
                await _relay.StopAsync();
            }
            else
            {
                if (!ApplySettings())
                    return;
                await _relay.StartAsync();
            }
        }
        finally
        {
            _startStopButton.Enabled = true;
            RefreshDashboard();
        }
    }

    private void Mode_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (!_settingsInitialized)
            return;
        _relay.ConfigureListener(
            _listenAddress.Text,
            decimal.ToInt32(_listenPort.Value),
            _mode.SelectedIndex != 1,
            out _);
    }

    private void ThemeSelector_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (!_themeInitialized || _themeSelector.SelectedItem is not string themeName)
            return;
        AppTheme theme = AppThemes.Get(themeName);
        _relay.SetTheme(theme.Name);
        ApplyTheme(theme);
        RefreshDashboard();
        RefreshTrafficChart();
    }

    private bool ApplySettings()
    {
        bool bidirectional = _mode.SelectedIndex != 1;
        if (_relay.ConfigureListener(_listenAddress.Text, decimal.ToInt32(_listenPort.Value), bidirectional, out string error))
            return true;
        MessageBox.Show(this, error, "Invalid settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private void AddDestination()
    {
        using var dialog = new DestinationDialog("Add destination", _currentTheme);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (!_relay.AddTarget(dialog.DestinationName, dialog.Address, dialog.Port, out string error))
            MessageBox.Show(this, error, "Could not add destination", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        RefreshDashboard();
    }

    private void EditSelectedDestination()
    {
        TargetSnapshot? target = GetSelectedTarget();
        if (target is null)
            return;
        using var dialog = new DestinationDialog("Edit destination", _currentTheme, target.Name, target.Address, target.Port);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (!_relay.UpdateTarget(target.Id, dialog.DestinationName, dialog.Address, dialog.Port, out string error))
            MessageBox.Show(this, error, "Could not update destination", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        RefreshDashboard();
    }

    private void RemoveSelectedDestination()
    {
        TargetSnapshot? target = GetSelectedTarget();
        if (target is null)
            return;
        DialogResult answer = MessageBox.Show(this, $"Remove '{target.Name}' from the relay destinations?", "Remove destination", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            _relay.RemoveTarget(target.Id);
            RefreshDashboard();
        }
    }

    private TargetSnapshot? GetSelectedTarget()
    {
        if (_destinationGrid.SelectedRows.Count == 0 || _destinationGrid.SelectedRows[0].Tag is not Guid id)
        {
            MessageBox.Show(this, "Select a destination first.", "Destinations", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        return _relay.GetSnapshot().Targets.FirstOrDefault(target => target.Id == id);
    }

    private void LoadSettingsFromSnapshot(RelaySnapshot snapshot)
    {
        _listenAddress.Text = snapshot.ListenAddress;
        _listenPort.Value = snapshot.ListenPort;
        _mode.SelectedIndex = snapshot.Bidirectional ? 0 : 1;
        _themeSelector.SelectedItem = AppThemes.Get(snapshot.ThemeName).Name;
        _settingsInitialized = true;
        _themeInitialized = true;
        ApplyTheme(AppThemes.Get(snapshot.ThemeName));
        RefreshDashboard();
    }

    private void RefreshDashboard()
    {
        RelaySnapshot snapshot = _relay.GetSnapshot();
        _statusBadge.Text = snapshot.IsRunning ? "●  RUNNING" : "○  STOPPED";
        _statusBadge.BackColor = snapshot.IsRunning ? _currentTheme.Success : _currentTheme.SecondaryButton;
        _startStopButton.Text = snapshot.IsRunning ? "Stop relay" : "Start relay";
        _startStopButton.BackColor = snapshot.IsRunning ? _currentTheme.Danger : _currentTheme.Success;
        _listenAddress.Enabled = !snapshot.IsRunning;
        _listenPort.Enabled = !snapshot.IsRunning;

        if (_settingsInitialized && !_mode.DroppedDown)
            _mode.SelectedIndex = snapshot.Bidirectional ? 0 : 1;

        _wsjtxPackets.Text = snapshot.Counters.WsjtxToAppsPackets.ToString("N0");
        _appPackets.Text = snapshot.Counters.AppsToWsjtxPackets.ToString("N0");
        _droppedPackets.Text = snapshot.Counters.DroppedPackets.ToString("N0");
        _sendErrors.Text = snapshot.Counters.SendErrors.ToString("N0");
        _sourceValue.Text = snapshot.WsjtxSource ?? "Waiting for WSJT-X traffic…";
        _lastPacketValue.Text = snapshot.LastPacket;
        _footerStatus.Text = snapshot.Status;
        _footerConfig.Text = $"Settings: {snapshot.ConfigPath}";
        _footerConfig.Left = Math.Max(20, _footerConfig.Parent?.ClientSize.Width - _footerConfig.Width - 18 ?? 20);

        Guid? selected = _destinationGrid.SelectedRows.Count > 0 ? _destinationGrid.SelectedRows[0].Tag as Guid? : null;
        _destinationGrid.SuspendLayout();
        _destinationGrid.Rows.Clear();
        foreach (TargetSnapshot target in snapshot.Targets)
        {
            int index = _destinationGrid.Rows.Add(target.Name, $"{target.Address}:{target.Port}", target.Packets.ToString("N0"), FormatBytes(target.Bytes), target.SendErrors.ToString("N0"));
            _destinationGrid.Rows[index].Tag = target.Id;
            if (selected == target.Id)
                _destinationGrid.Rows[index].Selected = true;
        }
        _destinationGrid.ResumeLayout();

        _eventList.BeginUpdate();
        _eventList.Items.Clear();
        foreach (string item in snapshot.Events)
            _eventList.Items.Add(item);
        _eventList.EndUpdate();
    }

    private void RefreshTrafficChart()
    {
        RelaySnapshot snapshot = _relay.GetSnapshot();
        ulong totalPackets = snapshot.Counters.WsjtxToAppsPackets + snapshot.Counters.AppsToWsjtxPackets;
        _trafficChart.Sample(totalPackets);

        _trafficLegend.SuspendLayout();
        _trafficLegend.Controls.Clear();
        TrafficLegendItem item = _trafficChart.LegendItem;
        string text = $"{item.Name}  {item.PacketsPerSecond:N0} pkt/s";
        Size textSize = TextRenderer.MeasureText(text, Font);
        var legendItem = new Panel
        {
            Width = textSize.Width + 24,
            Height = 22,
            Margin = new Padding(0, 0, 12, 0),
            BackColor = Color.Transparent
        };
        var swatch = new Panel
        {
            BackColor = item.Color,
            Size = new Size(10, 10),
            Location = new Point(0, 5)
        };
        var label = new Label
        {
            AutoSize = true,
            Text = text,
            ForeColor = _currentTheme.MutedText,
            Font = new Font("Segoe UI", 8F),
            Location = new Point(15, 2)
        };
        legendItem.Controls.Add(swatch);
        legendItem.Controls.Add(label);
        _trafficLegend.Controls.Add(legendItem);
        _trafficLegend.ResumeLayout();
    }

    private void ApplyTheme(AppTheme theme)
    {
        _currentTheme = theme;
        SuspendLayout();
        foreach ((Control control, ThemeBinding binding) in _themeBindings)
        {
            if (binding.Back != ThemeRole.None)
                control.BackColor = ResolveThemeColor(theme, binding.Back);
            if (binding.Fore != ThemeRole.None)
                control.ForeColor = ResolveThemeColor(theme, binding.Fore);
        }

        _destinationGrid.BackgroundColor = theme.Surface;
        _destinationGrid.GridColor = theme.Border;
        _destinationGrid.ColumnHeadersDefaultCellStyle.BackColor = theme.GridHeader;
        _destinationGrid.ColumnHeadersDefaultCellStyle.ForeColor = theme.MutedText;
        _destinationGrid.DefaultCellStyle.BackColor = theme.Surface;
        _destinationGrid.DefaultCellStyle.ForeColor = theme.Text;
        _destinationGrid.DefaultCellStyle.SelectionBackColor = theme.Selection;
        _destinationGrid.DefaultCellStyle.SelectionForeColor = theme.SelectionText;
        _destinationGrid.AlternatingRowsDefaultCellStyle.BackColor = theme.SurfaceAlt;
        _destinationGrid.AlternatingRowsDefaultCellStyle.ForeColor = theme.Text;
        _trafficChart.ApplyTheme(theme);
        ResumeLayout(true);
        Invalidate(true);
    }

    private void CaptureThemeBindings(Control control, ThemeRole inheritedBack)
    {
        ThemeRole back = DetermineBackRole(control, inheritedBack);
        ThemeRole fore = DetermineForeRole(control, back);
        _themeBindings[control] = new ThemeBinding(back, fore);
        ThemeRole childInherited = back == ThemeRole.None ? inheritedBack : back;
        foreach (Control child in control.Controls)
            CaptureThemeBindings(child, childInherited);
    }

    private static ThemeRole DetermineBackRole(Control control, ThemeRole inherited)
    {
        if (control is TextBoxBase or ComboBox or NumericUpDown)
            return ThemeRole.Input;
        if (control is Button)
        {
            if (control.BackColor.ToArgb() == Green.ToArgb()) return ThemeRole.Success;
            if (control.BackColor.ToArgb() == Red.ToArgb()) return ThemeRole.Danger;
            if (control.BackColor.ToArgb() == Blue.ToArgb()) return ThemeRole.Primary;
            return ThemeRole.Secondary;
        }

        int color = control.BackColor.ToArgb();
        if (color == Page.ToArgb()) return ThemeRole.Page;
        if (color == Surface.ToArgb()) return ThemeRole.Surface;
        if (color == Navy.ToArgb()) return ThemeRole.Header;
        if (color == Color.FromArgb(248, 250, 252).ToArgb()) return ThemeRole.SurfaceAlt;
        if (color == Color.FromArgb(230, 235, 240).ToArgb()) return ThemeRole.Footer;
        if (color == Blue.ToArgb()) return ThemeRole.Primary;
        if (color == Green.ToArgb()) return ThemeRole.Success;
        if (color == Red.ToArgb()) return ThemeRole.Danger;
        if (color == Color.FromArgb(218, 138, 38).ToArgb()) return ThemeRole.Warning;
        if (color == Color.FromArgb(89, 103, 119).ToArgb()) return ThemeRole.Secondary;
        if (control.BackColor == Color.Transparent) return ThemeRole.None;
        if (control is Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer or SplitterPanel)
            return inherited;
        return ThemeRole.None;
    }

    private static ThemeRole DetermineForeRole(Control control, ThemeRole back)
    {
        if (control is TextBoxBase or ComboBox or NumericUpDown)
            return ThemeRole.InputText;
        if (control is Button)
            return ThemeRole.OnAccent;
        if (back == ThemeRole.Header)
            return control.ForeColor.ToArgb() == Color.White.ToArgb() ? ThemeRole.HeaderText : ThemeRole.HeaderMuted;
        if (control is Label or ListBox)
        {
            int color = control.ForeColor.ToArgb();
            if (color == Color.White.ToArgb()) return ThemeRole.HeaderText;
            if (color == Color.FromArgb(32, 43, 55).ToArgb() || color == Color.FromArgb(35, 45, 57).ToArgb())
                return ThemeRole.Text;
            return ThemeRole.MutedText;
        }
        return ThemeRole.None;
    }

    private static Color ResolveThemeColor(AppTheme theme, ThemeRole role) => role switch
    {
        ThemeRole.Page => theme.Page,
        ThemeRole.Surface => theme.Surface,
        ThemeRole.SurfaceAlt => theme.SurfaceAlt,
        ThemeRole.Header => theme.Header,
        ThemeRole.HeaderText => theme.HeaderText,
        ThemeRole.HeaderMuted => theme.HeaderMuted,
        ThemeRole.Text => theme.Text,
        ThemeRole.MutedText => theme.MutedText,
        ThemeRole.Input => theme.InputBackground,
        ThemeRole.InputText => theme.InputText,
        ThemeRole.Footer => theme.Footer,
        ThemeRole.Primary => theme.Primary,
        ThemeRole.Success => theme.Success,
        ThemeRole.Danger => theme.Danger,
        ThemeRole.Warning => theme.Warning,
        ThemeRole.Secondary => theme.SecondaryButton,
        ThemeRole.OnAccent => theme.HeaderText,
        _ => Color.Transparent
    };

    private void ConfigureThemedCombo(ComboBox combo)
    {
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.DrawItem += ThemedCombo_DrawItem;
    }

    private void ThemedCombo_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo || e.Index < 0)
            return;
        bool editArea = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool selected = (e.State & DrawItemState.Selected) != 0 && !editArea;
        Color background = selected ? _currentTheme.Selection : _currentTheme.InputBackground;
        Color foreground = selected ? _currentTheme.SelectionText : _currentTheme.InputText;
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics,
            combo.GetItemText(combo.Items[e.Index]),
            combo.Font,
            new Rectangle(e.Bounds.X + 3, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 6), e.Bounds.Height),
            foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0 && !editArea)
            e.DrawFocusRectangle();
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1_048_576)
            return $"{bytes / 1_048_576d:N1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024d:N1} KB";
        return $"{bytes:N0} B";
    }

    private static void AddFieldLabel(TableLayoutPanel panel, string text, int column, int row, int span)
    {
        var label = new Label { AutoSize = true, Text = text, ForeColor = Muted, Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 0) };
        panel.Controls.Add(label, column, row);
        panel.SetColumnSpan(label, span);
    }

    private static Button CreateActionButton(string text, Color color)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(9, 0, 9, 0) };
        StylePrimaryButton(button, color);
        return button;
    }

    private static void StylePrimaryButton(Button button, Color color)
    {
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    private enum ThemeRole
    {
        None, Page, Surface, SurfaceAlt, Header, HeaderText, HeaderMuted, Text,
        MutedText, Input, InputText, Footer, Primary, Success, Danger, Warning,
        Secondary, OnAccent
    }

    private readonly record struct ThemeBinding(ThemeRole Back, ThemeRole Fore);
}

internal sealed class DestinationDialog : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _address = new();
    private readonly TextBox _port = new();

    public string DestinationName => _name.Text.Trim();
    public string Address => _address.Text.Trim();
    public int Port => int.TryParse(_port.Text.Trim(), out int port) ? port : 0;

    public DestinationDialog(string title, AppTheme theme, string name = "", string address = "127.0.0.1", int port = 2237)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 225);
        BackColor = theme.Surface;
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(18, 16, 18, 14), BackColor = theme.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _name.Text = name;
        _address.Text = address;
        _port.Text = Math.Clamp(port, 1, 65535).ToString();
        _port.MaxLength = 5;
        _port.KeyPress += (_, args) =>
        {
            if (!char.IsControl(args.KeyChar) && !char.IsDigit(args.KeyChar))
                args.Handled = true;
        };
        AddDialogField(layout, "Name", _name, 0, theme);
        AddDialogField(layout, "Address", _address, 1, theme);
        AddDialogField(layout, "Port", _port, 2, theme);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0), BackColor = theme.Surface };
        var save = new Button { Text = "Save", Width = 88, Height = 30, BackColor = theme.Primary, ForeColor = theme.HeaderText, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) =>
        {
            if (!int.TryParse(_port.Text.Trim(), out int enteredPort) || enteredPort is < 1 or > 65535)
            {
                MessageBox.Show(this, "Enter a port number between 1 and 65535.", "Invalid port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _port.Focus();
                _port.SelectAll();
                return;
            }
            DialogResult = DialogResult.OK;
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 88, Height = 30, Margin = new Padding(8, 0, 0, 0), BackColor = theme.SecondaryButton, ForeColor = theme.HeaderText, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
        cancel.FlatAppearance.BorderSize = 0;
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _name.Focus();
        _name.SelectAll();
    }

    private static void AddDialogField(TableLayoutPanel layout, string labelText, Control control, int row, AppTheme theme)
    {
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = theme.MutedText };
        control.BackColor = theme.InputBackground;
        control.ForeColor = theme.InputText;
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 6, 0, 6);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(control, 1, row);
    }
}
