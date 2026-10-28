using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteWinDoctor.Services;

namespace RemoteWinDoctor.Desktop;

public partial class MainWindow : Window
{
    private readonly ProfileStore _profileStore;
    private readonly NetworkScanner _scanner;
    private readonly PowerShellExecutor _executor;
    private List<MachineProfile> _profiles = new();
    private Dictionary<RadioButton, FrameworkElement>? _navMap;

    public MainWindow()
    {
        InitializeComponent();

        _profileStore = new ProfileStore();
        _scanner = new NetworkScanner();
        _executor = new PowerShellExecutor();

        LoadProfiles();
        UpdateModeBadge();
        UpdateHostIpLabel();
        Closed += (_, _) => _live?.Dispose();
    }

    private void UpdateHostIpLabel()
    {
        string? lanIp = null;
        try
        {
            // The adapter holding the default gateway is the real LAN one; VPN/virtual adapters usually have none.
            lanIp = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        TxtHostIpLabel.Text = $"This PC: {lanIp ?? Environment.MachineName}";
        if (lanIp != null) TxtSubnet.Text = lanIp[..lanIp.LastIndexOf('.')];
    }

    /// <summary>Shows/hides the full-content busy overlay, blocking input so a remote
    /// operation can't be launched twice in parallel by a double click.</summary>
    private void SetBusy(bool busy, string message = "Working...", bool blockView = true)
    {
        TxtBusyMessage.Text = message;
        // Non-blocking mode keeps the content visible (e.g. live charts during optimization)
        BusyOverlay.Visibility = busy && blockView ? Visibility.Visible : Visibility.Collapsed;
        SidebarNavPanel.IsEnabled = !busy;
        if (busy && !blockView) TxtStatus.Text = message;
    }

    #region Window Controls & Navigation

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewProfiles == null) return;

        _navMap ??= new Dictionary<RadioButton, FrameworkElement>
        {
            [NavProfiles] = ViewProfiles,
            [NavRadar] = ViewRadar,
            [NavDiag] = ViewDiag,
            [NavOpt] = ViewOpt,
            [NavAi] = ViewAi,
            [NavDrivers] = ViewDrivers,
            [NavDevDb] = ViewDevDb,
            [NavRescue] = ViewRescue,
            [NavOffice] = ViewOffice,
            [NavToolbox] = ViewToolbox,
            [NavTerminal] = ViewTerminal,
        };

        foreach (var (nav, view) in _navMap)
        {
            view.Visibility = nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        if (sender is RadioButton { IsChecked: true } && _navMap.TryGetValue((RadioButton)sender, out var activeView))
        {
            FadeIn(activeView);
        }
    }

    // ConvertTo-Json drops properties whose PowerShell value is $null, so a key that's
    // normally present can be missing from the JSON on some systems/CPU states.
    // JsonElement.GetProperty throws in that case; these helpers degrade to a safe default instead.
    private static string GetStr(JsonElement el, string prop, string fallback) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static int? GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? GetDouble(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static string Fmt(int? value) => value?.ToString() ?? "--";

    /// <summary>Fills the 4-slot DIMM map from the real Win32_PhysicalMemory scan instead of
    /// the static placeholder text the XAML used to ship with (always showed one fake 8GB module).</summary>
    private void PopulateDimmSlots(JsonElement mem)
    {
        var slotTexts = new[] { TxtSlot1Info, TxtSlot2Info, TxtSlot3Info, TxtSlot4Info };
        var slotBorders = new[] { SlotDimm1, SlotDimm2, SlotDimm3, SlotDimm4 };
        var emptyBrush = (Brush)FindResource("BorderGlassBrush");
        var channelABrush = (Brush)FindResource("AccentBrush");
        var channelBBrush = (Brush)FindResource("PurpleBrush");
        var primaryBrush = (Brush)FindResource("TextPrimaryBrush");
        var mutedBrush = (Brush)FindResource("TextSecondaryBrush");

        var modules = mem.TryGetProperty("Modules", out var mods) && mods.ValueKind == JsonValueKind.Array
            ? mods.EnumerateArray().ToList()
            : new List<JsonElement>();
        var physicalSlots = GetInt(mem, "SlotCount") ?? 4;

        for (int i = 0; i < 4; i++)
        {
            if (i < modules.Count)
            {
                var capacityBytes = GetLong(modules[i], "Capacity");
                var capacityGb = capacityBytes.HasValue ? Math.Round(capacityBytes.Value / 1024.0 / 1024.0 / 1024.0) : (double?)null;
                var speed = GetInt(modules[i], "Speed");
                var locator = GetStr(modules[i], "DeviceLocator", "");
                slotTexts[i].Text = capacityGb.HasValue ? $"{capacityGb}GB @ {Fmt(speed)}MHz" : "Installed (no details)";
                if (!string.IsNullOrEmpty(locator)) slotTexts[i].Text += $"\n{locator}";
                // Locators look like "ChannelA-DIMM1" / "ChannelB-DIMM0"; B, D... get the second color
                var ch = System.Text.RegularExpressions.Regex.Match(locator, @"(?i)channel\s*-?\s*([a-d])");
                var isSecondChannel = ch.Success && "BbDd".Contains(ch.Groups[1].Value);
                slotTexts[i].Foreground = primaryBrush;
                slotBorders[i].BorderBrush = isSecondChannel ? channelBBrush : channelABrush;
            }
            else
            {
                slotTexts[i].Text = i < physicalSlots ? "Empty" : "No slot";
                slotTexts[i].Foreground = mutedBrush;
                slotBorders[i].BorderBrush = emptyBrush;
            }
        }
    }

    private static long? GetLong(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    /// <summary>Score ring, colored grade line and one row per finding.</summary>
    private void RenderHealth(int score, List<string> issues)
    {
        var (brushKey, grade) = score switch
        {
            >= 90 => ("SuccessBrush", "Grade A · Healthy and optimized"),
            >= 75 => ("AccentBrush", "Grade B · Needs minor tuning"),
            >= 55 => ("WarningBrush", "Grade C · Several notable issues"),
            _ => ("DangerBrush", "Grade F · Needs attention now")
        };
        var brush = (Brush)FindResource(brushKey);

        TxtDiagHealthScore.Text = score.ToString();
        TxtDiagHealthScore.Foreground = brush;
        TxtDiagHealthGrade.Text = grade;
        TxtDiagHealthGrade.Foreground = brush;
        TxtDiagHealthSummary.Text = issues.Count == 0
            ? "No issues found."
            : $"{issues.Count} issue(s) need attention; see the full list below.";

        // Arc of a 104px ring (stroke 8 -> radius 48) starting at 12 o'clock, clockwise
        const double c = 52, r = 48;
        var fraction = Math.Clamp(score, 0, 100) / 100.0;
        HealthArc.Stroke = brush;
        if (fraction >= 0.999)
        {
            HealthArc.Data = new EllipseGeometry(new Point(c, c), r, r);
        }
        else
        {
            var angle = fraction * 2 * Math.PI;
            var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
            var figure = new PathFigure { StartPoint = new Point(c, c - r) };
            figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            HealthArc.Data = new PathGeometry(new[] { figure });
        }

        HealthIssuesPanel.Children.Clear();
        for (int i = 0; i < issues.Count; i++)
        {
            var row = new Border { Style = (Style)FindResource(i == issues.Count - 1 ? "LastRowStyle" : "RowStyle") };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = "",
                Style = (Style)FindResource("IconStyle"),
                Foreground = (Brush)FindResource("WarningBrush"),
                Margin = new Thickness(0, 0, 12, 0)
            });
            panel.Children.Add(new TextBlock { Text = issues[i], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            row.Child = panel;
            HealthIssuesPanel.Children.Add(row);
        }
        HealthIssuesSection.Visibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One card per physical disk: kind, status pill, SMART figures that are available, volumes and notes.</summary>
    private void RenderStorage(List<StorageDisk>? disks)
    {
        StoragePanel.Children.Clear();
        if (disks == null || disks.Count == 0)
        {
            TxtStorageMessage.Text = "Could not read the disks.";
            return;
        }
        var anyNeedsAdmin = disks.Any(d => d.Notes.Any(n => n.Contains("administrator")));
        TxtStorageMessage.Text = anyNeedsAdmin
            ? "SMART details (health %, power-on hours, bad sectors) need administrator rights: run RemoteWinDoctor as administrator. Remote PCs always connect as administrator."
            : $"{disks.Count} disk(s). Health and counters come straight from each drive's SMART data.";

        foreach (var d in disks)
        {
            var (statusText, brushKey, softKey) = d.Status switch
            {
                "Good" => ("Good", "SuccessBrush", "SuccessSoftBrush"),
                "Caution" => ("Caution", "WarningBrush", "WarningSoftBrush"),
                "Bad" => ("Bad", "DangerBrush", "DangerSoftBrush"),
                _ => ("Unknown", "TextSecondaryBrush", "CardHoverBrush")
            };
            var card = new Border { Style = (Style)FindResource("GlassPanelStyle"), Margin = new Thickness(0, 0, 0, 10) };
            var stack = new StackPanel();

            // Header: model + tags on the left, status/health on the right
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = d.Model, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            foreach (var tag in new[] { d.Kind, $"{d.SizeGB:0} GB", d.IsSystem ? "Windows" : null }.Where(t => !string.IsNullOrEmpty(t)))
                title.Children.Add(new Border { Style = (Style)FindResource("TagStyle"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = tag, FontSize = 11, Foreground = (Brush)FindResource("AccentBrush") } });
            header.Children.Add(title);

            var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (d.HealthPercent is int hp)
                status.Children.Add(new TextBlock { Text = $"{hp}%", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(brushKey), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            status.Children.Add(new Border { Background = (Brush)FindResource(softKey), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = statusText, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(brushKey) } });
            Grid.SetColumn(status, 1);
            header.Children.Add(status);
            stack.Children.Add(header);

            // SMART figures that the drive actually reported
            var metrics = new List<(string Label, string Value)>();
            if (d.PowerOnHours is long poh) metrics.Add(("Power-on time", StorageHealthHelper.FormatHours(poh)));
            if (d.TemperatureC is int t) metrics.Add(("Temperature", $"{t} °C"));
            if (d.PowerCycles is long pc) metrics.Add(("Power cycles", $"{pc:N0}"));
            if (d.WrittenTB is double tb) metrics.Add(("Total written", $"{tb:0.0} TB"));
            if (d.Reallocated is long r) metrics.Add(("Reallocated sectors", r.ToString()));
            if (d.Pending is long p) metrics.Add(("Pending sectors", p.ToString()));
            if (d.Uncorrectable is long u) metrics.Add(("Unreadable sectors", u.ToString()));
            if (d.MediaErrors is long me) metrics.Add(("Media errors", me.ToString()));
            if (d.UnsafeShutdowns is long us) metrics.Add(("Unsafe shutdowns", $"{us:N0}"));
            if (d.AvailableSpare is int sp) metrics.Add(("Spare blocks left", $"{sp}%"));
            if (metrics.Count > 0)
            {
                var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 0) };
                foreach (var (label, value) in metrics)
                {
                    var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 8) };
                    cell.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("CaptionStyle") });
                    cell.Children.Add(new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) });
                    grid.Children.Add(cell);
                }
                stack.Children.Add(grid);
            }

            // Volumes with a usage bar
            foreach (var v in d.Volumes)
            {
                var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                var usedFraction = v.SizeGB > 0 ? 1 - v.FreeGB / v.SizeGB : 0;
                row.Children.Add(new TextBlock { Text = v.Letter, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = usedFraction, Height = 6, VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("LoadingBarStyle"), Foreground = (Brush)FindResource(usedFraction >= 0.9 ? "WarningBrush" : "AccentBrush") };
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                var label = new TextBlock { Text = $"{v.FreeGB:0.#} GB free of {v.SizeGB:0.#} GB", Style = (Style)FindResource("RowDescStyle"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 2);
                row.Children.Add(label);
                stack.Children.Add(row);
            }

            foreach (var note in d.Notes.Where(n => !n.Contains("administrator")))
                stack.Children.Add(new TextBlock { Text = note, Style = (Style)FindResource("RowDescStyle"), Foreground = (Brush)FindResource(d.Status is "Bad" or "Caution" ? brushKey : "TextSecondaryBrush"), Margin = new Thickness(0, 10, 0, 0) });

            card.Child = stack;
            StoragePanel.Children.Add(card);
        }
    }

    private static void FadeIn(FrameworkElement element)
    {
        element.Opacity = 0;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    #endregion

    #region Profile Management

    private void LoadProfiles()
    {
        _profiles = _profileStore.GetAll();

        CmbProfiles.Items.Clear();
        foreach (var p in _profiles)
        {
            CmbProfiles.Items.Add($"{p.Name} ({p.IpAddress})");
        }

        if (CmbProfiles.Items.Count > 0)
        {
            CmbProfiles.SelectedIndex = 0;
        }

        RenderProfilesGrid();
    }

    private void RenderProfilesGrid()
    {
        ProfilesContainer.Children.Clear();

        if (_profiles.Count == 0)
        {
            ProfilesContainer.Children.Add(new Border
            {
                Style = (Style)FindResource("LastRowStyle"),
                Child = new TextBlock { Text = "No saved systems yet. Enter an IP in the card at the bottom of the sidebar and click \"Test connection\"; it is saved automatically once it connects.", Style = (Style)FindResource("RowDescStyle"), Margin = new Thickness(0) }
            });
            return;
        }

        for (int i = 0; i < _profiles.Count; i++)
        {
            var p = _profiles[i];
            bool isLocalProfile = IsLocalTarget(p.IpAddress);

            var row = new Border { Style = (Style)FindResource(i == _profiles.Count - 1 ? "LastRowStyle" : "RowStyle") };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
            titleLine.Children.Add(new TextBlock { Text = p.Name, Style = (Style)FindResource("RowTitleStyle"), FlowDirection = FlowDirection.LeftToRight });
            titleLine.Children.Add(new Border
            {
                Style = (Style)FindResource("TagStyle"),
                Background = (Brush)FindResource(isLocalProfile ? "SuccessSoftBrush" : "AccentSoftBrush"),
                Margin = new Thickness(10, 0, 0, 0),
                Child = new TextBlock { Text = isLocalProfile ? "Local" : "LAN", FontSize = 11, Foreground = (Brush)FindResource(isLocalProfile ? "SuccessBrush" : "AccentBrush") }
            });
            info.Children.Add(titleLine);
            info.Children.Add(new TextBlock
            {
                Text = $"{p.IpAddress}  ·  {p.Username}",
                Style = (Style)FindResource("RowDescStyle"),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FlowDirection = FlowDirection.LeftToRight,
                HorizontalAlignment = HorizontalAlignment.Left
            });
            if (!string.IsNullOrWhiteSpace(p.Specs))
            {
                info.Children.Add(new TextBlock { Text = p.Specs, Style = (Style)FindResource("CaptionStyle"), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) });
            }
            grid.Children.Add(info);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(actions, 1);
            var btnSelect = new Button { Content = "Select", Style = (Style)FindResource("SecondaryButtonStyle") };
            btnSelect.Click += (s, e) =>
            {
                if (isLocalProfile)
                {
                    SwitchToLocalMode(false);
                    return;
                }
                TxtIp.Text = p.IpAddress;
                TxtUser.Text = p.Username;
                TxtPass.Password = p.Password;
                UpdateModeBadge();
                TxtStatus.Text = $"Target: {p.Name} ({p.IpAddress})";
                LogTerminal($"Target set to {p.Name} ({p.IpAddress}).");
            };
            actions.Children.Add(btnSelect);

            if (!isLocalProfile)
            {
                var btnDelete = new Button
                {
                    Content = "\uE74D",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = "Remove from list"
                };
                btnDelete.Click += (s, e) =>
                {
                    if (MessageBox.Show($"Remove \"{p.Name}\" from the list?", "Remove system", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        _profileStore.Delete(p.Id);
                        LoadProfiles();
                    }
                };
                actions.Children.Add(btnDelete);
            }
            grid.Children.Add(actions);

            row.Child = grid;
            ProfilesContainer.Children.Add(row);
        }
    }

    private void CmbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbProfiles.SelectedIndex >= 0 && CmbProfiles.SelectedIndex < _profiles.Count)
        {
            var p = _profiles[CmbProfiles.SelectedIndex];
            TxtIp.Text = p.IpAddress;
            TxtUser.Text = p.Username;
            TxtPass.Password = p.Password;
            UpdateModeBadge();
        }
    }

    private void TxtIp_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_live != null && TxtIp.Text.Trim() != _liveIp) StopLive("Target changed; monitoring stopped");
        UpdateModeBadge();
        UpdateWizardHostLabel();
    }

    private void BtnSwitchLocal_Click(object sender, RoutedEventArgs e)
    {
        SwitchToLocalMode(triggerDiagnostics: true);
    }

    private void BtnSwitchRemote_Click(object sender, RoutedEventArgs e)
    {
        var remote = _profiles.FirstOrDefault(p => !IsLocalTarget(p.IpAddress));
        if (remote != null)
        {
            TxtIp.Text = remote.IpAddress;
            TxtUser.Text = remote.Username;
            TxtPass.Password = remote.Password;
        }
        else
        {
            TxtIp.Text = "192.168.1.100";
            TxtUser.Text = "Administrator";
            TxtPass.Password = "";
        }
        UpdateModeBadge();
        LogTerminal($"[REMOTE MODE] LAN mode on. Target: {TxtIp.Text}");
        TxtStatus.Text = $"Ready to connect to {TxtIp.Text} on port 5985.";
    }

    public void SwitchToLocalMode(bool triggerDiagnostics = false)
    {
        TxtIp.Text = "127.0.0.1";
        TxtUser.Text = Environment.UserName;
        TxtPass.Password = "";
        UpdateModeBadge();

        LogTerminal($"[LOCAL MODE] Working on this PC: {Environment.MachineName} (127.0.0.1)");
        TxtStatus.Text = "Working on this PC (runs directly, no network needed).";

        if (triggerDiagnostics)
        {
            NavDiag.IsChecked = true;
            BtnRunDiag_Click(this, new RoutedEventArgs());
        }
    }

    private static bool IsLocalTarget(string? ip) => PowerShellExecutor.IsLocalTarget(ip);

    private void UpdateModeBadge()
    {
        if (TxtModeBadge == null || TxtTargetSummary == null) return;

        var ip = TxtIp?.Text.Trim() ?? "";
        bool isLocal = IsLocalTarget(ip);
        var profile = _profiles.FirstOrDefault(p => p.IpAddress == ip);

        var shownIp = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip;
        var summary = isLocal ? Environment.MachineName : profile?.Name ?? shownIp;
        TxtTargetSummary.Text = summary;
        TxtTargetSummary.ToolTip = summary;
        TxtModeBadge.Text = isLocal ? "This PC" : $"{shownIp} · remote";
        BadgeMode.Background = (Brush)FindResource(isLocal ? "SuccessBrush" : "AccentBrush");
    }

    private void BtnTargetCard_Click(object sender, RoutedEventArgs e) => PopupTarget.IsOpen = !PopupTarget.IsOpen;

    /// <summary>Saves the current target (IP/user/password) as a profile, updating an existing one with the same IP.</summary>
    private MachineProfile? SaveCurrentTarget(string? name)
    {
        var ip = TxtIp.Text.Trim();
        if (IsLocalTarget(ip)) return null;

        var existing = _profiles.FirstOrDefault(p => p.IpAddress == ip);
        var profile = existing ?? new MachineProfile { IpAddress = ip };
        profile.Name = !string.IsNullOrWhiteSpace(name) ? name : (existing?.Name ?? ip);
        profile.Username = TxtUser.Text.Trim();
        profile.Password = TxtPass.Password;
        profile.LastChecked = DateTime.Now;
        _profileStore.Save(profile);
        LoadProfiles();
        CmbProfiles.SelectedIndex = _profiles.FindIndex(p => p.IpAddress == ip);
        return profile;
    }

    private void BtnAddProfile_Click(object sender, RoutedEventArgs e)
    {
        if (IsLocalTarget(TxtIp.Text))
        {
            PopupTarget.IsOpen = true;
            TxtStatus.Text = "Enter the remote IP, username and password, then click \"Add system\" or \"Test connection\" again.";
            return;
        }
        var saved = SaveCurrentTarget(null);
        if (saved != null) TxtStatus.Text = $"\"{saved.Name}\" saved to the systems list.";
    }

    #endregion

    #region Remote Setup Wizard

    private void BtnSaveTargetSetupScript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = RemoteSetupHelper.GetTargetSetupScriptContent();
            var targetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RemoteSetupTarget.bat");
            File.WriteAllText(targetPath, content, System.Text.Encoding.Default);
            MessageBox.Show(
                $"Setup script saved to the Desktop:\n{targetPath}\n\n" +
                "Copy it to the target PC (USB stick or network share) and run it there with \"Run as administrator\". " +
                "When it finishes it shows the target IP; note it down with that PC's Windows username and password.",
                "Setup script saved", MessageBoxButton.OK, MessageBoxImage.Information);
            LogTerminal($"Target setup script created on the Desktop: {targetPath}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save the file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnRunSourceSetup_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "This starts the WinRM service on this PC and lets it connect to other PCs on the local network. " +
                "RemoteWinDoctor must be running as administrator. Continue?",
                "Set up this PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        BtnRunSourceSetup.IsEnabled = false;
        SetBusy(true, "Setting up this PC for remote connections...");
        LogTerminal("[SETUP] Setting up source PC (WinRM client + TrustedHosts)...");

        try
        {
            var script = RemoteSetupHelper.GetSourceReadinessScript();
            var res = await _executor.ExecuteAsync("127.0.0.1", Environment.UserName, "", script, timeoutSeconds: 30);
            LogTerminal(res.Output);

            if (res.Success)
            {
                MessageBox.Show("This PC is ready to connect to other PCs on the network.", "Setup complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Setup failed. Make sure RemoteWinDoctor is running as administrator.\n\n{res.Error}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            LogTerminal($"[ERR] {ex.Message}");
        }
        finally
        {
            BtnRunSourceSetup.IsEnabled = true;
            SetBusy(false);
        }
    }

    #endregion

    #region Connection Test & Safety Reboot

    private async void BtnTestConnection_Click(object sender, RoutedEventArgs e)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;

        BtnTestConnection.IsEnabled = false;
        TxtStatus.Text = $"Testing connection to {ip}...";
        LogTerminal($"Testing connection to {ip} and checking port 5985...");

        try
        {
            var probe = await _scanner.ProbeHostAsync(ip);
            bool isLocal = IsLocalTarget(ip);

            if (!isLocal && !probe.WinRmPortOpen && !probe.PingSucceeded)
            {
                TxtStatus.Text = "Could not connect.";
                LogTerminal($"[ERROR] {ip} did not answer ping or port 5985.");
                MessageBox.Show($"{ip} is unreachable or its WinRM port is blocked.", "Connection error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var cmdResult = await _executor.ExecuteAsync(ip, user, pass, "hostname; (Get-WmiObject Win32_OperatingSystem).Caption", timeoutSeconds: 20);

            if (cmdResult.Success)
            {
                var lines = cmdResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var hostName = lines.FirstOrDefault();
                var saved = isLocal ? null : SaveCurrentTarget(hostName);
                TxtStatus.Text = saved != null ? $"Connected; \"{saved.Name}\" saved to the systems list." : "Connected.";
                LogTerminal($"Connected to {ip}:\n{cmdResult.Output.Trim()}");
                MessageBox.Show($"Connected.\n\n{cmdResult.Output.Trim()}", "Test connection", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                TxtStatus.Text = "Authentication failed.";
                LogTerminal($"[ERR] Port is open but authentication failed: {cmdResult.Error}");
                MessageBox.Show($"Port 5985 is open but sign-in failed:\n{cmdResult.Error}", "Authentication failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
            LogTerminal($"[ERR] {ex.Message}");
        }
        finally
        {
            BtnTestConnection.IsEnabled = true;
        }
    }

    private async void BtnRemoteReboot_Click(object sender, RoutedEventArgs e)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;
        bool isLocal = IsLocalTarget(ip);

        var currentProfile = _profiles.FirstOrDefault(p => p.IpAddress == ip);
        string name = currentProfile?.Name ?? (isLocal ? "Local Admin Workstation" : "Client PC");

        var dialog = new SafetyRebootWindow(ip, name, isLocal) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.IsConfirmed)
        {
            TxtStatus.Text = $"Sending restart to {ip}...";
            LogTerminal($"[SAFETY PASS] Sending forced restart to {ip}...");

            var script = "Restart-Computer -Force -Confirm:$false; Write-Host 'Reboot signal dispatched.'";
            await _executor.ExecuteAsync(ip, user, pass, script, timeoutSeconds: 15);

            TxtStatus.Text = "Restart command sent.";
            LogTerminal($"Restart sent to {ip}.");
            MessageBox.Show($"Restart command sent to {ip}.", "Command sent", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    #endregion

    #region Radar Scanner

    private async void BtnStartRadar_Click(object sender, RoutedEventArgs e)
    {
        var subnet = TxtSubnet.Text.Trim();
        BtnStartRadar.IsEnabled = false;
        SidebarNavPanel.IsEnabled = false; // radar shows its own live progress bar, so no full overlay here
        RadarProgress.Value = 0;
        TxtRadarStatus.Text = "Scanning 254 addresses in parallel...";
        LogTerminal($"Scanning {subnet}.1 to {subnet}.254...");

        try
        {
            var results = await _scanner.ScanSubnetAsync(subnet, 1, 254, (current, total) =>
            {
                Dispatcher.Invoke(() =>
                {
                    RadarProgress.Value = current;
                    TxtRadarStatus.Text = $"Scanned {current} of {total}";
                });
            });

            GridRadarResults.ItemsSource = results;
            TxtRadarStatus.Text = $"Scan finished. {results.Count} active device(s) found.";
            LogTerminal($"Radar scan finished. Active systems: {results.Count}");
        }
        catch (Exception ex)
        {
            TxtRadarStatus.Text = $"Error: {ex.Message}";
            LogTerminal($"[ERR] {ex.Message}");
        }
        finally
        {
            BtnStartRadar.IsEnabled = true;
            SidebarNavPanel.IsEnabled = true;
        }
    }

    private void BtnSelectRadarDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is DiscoveredDevice dev)
        {
            TxtIp.Text = dev.IpAddress;
            LogTerminal($"Selected {dev.IpAddress} from radar.");
            NavDiag.IsChecked = true;
        }
    }

    #endregion

    #region Deep Diagnostics

    private async void BtnRunDiag_Click(object sender, RoutedEventArgs e)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;

        BtnRunDiag.IsEnabled = false;
        SetBusy(true, "Running deep hardware diagnostics...");
        TxtStatus.Text = "Running deep diagnostics...";
        LogTerminal($"Running hardware audit on {ip}...");

        try
        {
            var script = DiagnosticEngine.GetDeepDiagnosticScript();
            var result = await _executor.ExecuteAsync(ip, user, pass, script, timeoutSeconds: 90);

            if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
            {
                throw new Exception(result.Error);
            }

            var json = result.Output.Trim();
            var startIndex = json.IndexOf('{');
            var endIndex = json.LastIndexOf('}');
            if (startIndex >= 0 && endIndex > startIndex)
            {
                var extracted = json.Substring(startIndex, endIndex - startIndex + 1);
                using var doc = JsonDocument.Parse(extracted);
                var root = doc.RootElement;

                // 1. Processor
                if (root.TryGetProperty("Processor", out var proc))
                {
                    TxtCpuName.Text = GetStr(proc, "Name", "Unknown");
                    var cores = GetInt(proc, "Cores");
                    var threads = GetInt(proc, "Threads");
                    var curClock = GetInt(proc, "CurrentClockMHz");
                    var maxClock = GetInt(proc, "MaxClockMHz");
                    TxtCpuMetrics.Text = $"{Fmt(cores)} cores / {Fmt(threads)} threads\nCurrent clock: {Fmt(curClock)} MHz\nMax clock: {Fmt(maxClock)} MHz";
                }

                // 2. Memory & Channels
                if (root.TryGetProperty("Memory", out var mem))
                {
                    var totalGb = GetDouble(mem, "TotalGB");
                    var installedGb = GetDouble(mem, "InstalledGB");
                    TxtRamTotal.Text = installedGb.HasValue ? $"{installedGb} GB"
                        : totalGb.HasValue ? $"{totalGb} GB" : "-- GB";
                    var chMode = GetStr(mem, "ChannelMode", "Unknown");
                    var chText = chMode.Contains("Single", StringComparison.OrdinalIgnoreCase) ? "Single-channel"
                        : chMode.Contains("Dual", StringComparison.OrdinalIgnoreCase)
                            ? (chMode.Contains("estimated", StringComparison.OrdinalIgnoreCase) ? "Dual-channel (estimated)" : "Dual-channel")
                            : "Unknown";
                    TxtRamChannel.Text = $"Mode: {chText}" + (totalGb.HasValue ? $"\nUsable: {totalGb} GB" : "");

                    if (chMode.Contains("Single", StringComparison.OrdinalIgnoreCase))
                    {
                        BannerBottleneck.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        BannerBottleneck.Visibility = Visibility.Collapsed;
                    }

                    PopulateDimmSlots(mem);
                }

                // 3. Graphics
                if (root.TryGetProperty("Graphics", out var gpu))
                {
                    if (gpu.TryGetProperty("Controllers", out var controllers) && controllers.ValueKind == JsonValueKind.Array && controllers.GetArrayLength() > 0)
                    {
                        TxtGpuName.Text = GetStr(controllers[0], "Name", "Unknown");
                    }
                    var hasCuda = gpu.TryGetProperty("HasNvidiaCuda", out var hc) && hc.ValueKind == JsonValueKind.True;
                    var vram = GetDouble(gpu, "NvidiaVramGB");
                    TxtGpuVram.Text = hasCuda
                        ? $"VRAM: {(vram.HasValue ? vram.ToString() : "--")} GB\nCUDA: yes"
                        : "Integrated graphics\nCUDA: no";
                }

                // 4. Storage - the disk that actually holds C:, not just the first disk in the list
                if (root.TryGetProperty("Storage", out var st) && st.TryGetProperty("SystemDisk", out var sysDisk) && sysDisk.ValueKind == JsonValueKind.Object)
                {
                    TxtDiskModel.Text = GetStr(sysDisk, "FriendlyName", "Unknown");
                    var health = GetStr(sysDisk, "HealthStatus", "Unknown");
                    var type = GetStr(sysDisk, "Type", "Unknown");
                    var free = GetDouble(sysDisk, "PercentFree");
                    if (string.IsNullOrWhiteSpace(health)) health = "Unknown";
                    TxtDiskHealth.Text = $"Health: {health}\nType: {type}\nFree space: {(free.HasValue ? $"{free}%" : "--")}";
                }

                // 5. AI Readiness
                if (root.TryGetProperty("AIReadiness", out var ai))
                {
                    var aiScore = GetInt(ai, "Score");
                    TxtAiScore.Text = aiScore.HasValue ? aiScore.ToString()! : "--";
                    TxtAiTier.Text = GetStr(ai, "Tier", "Unknown");

                    AiRecommendationsContainer.Children.Clear();
                    if (ai.TryGetProperty("Recommendations", out var recs))
                    {
                        foreach (var r in recs.EnumerateArray())
                        {
                            AiRecommendationsContainer.Children.Add(new TextBlock
                            {
                                Text = $"•  {r.GetString()}",
                                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                                FontSize = 12,
                                Margin = new Thickness(0, 2, 0, 2)
                            });
                        }
                    }
                }

                // Composite score is computed in the script from real findings
                if (root.TryGetProperty("Health", out var healthEl))
                {
                    var score = GetInt(healthEl, "Score") ?? 0;
                    var issues = healthEl.TryGetProperty("Issues", out var iss) && iss.ValueKind == JsonValueKind.Array
                        ? iss.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                        : new List<string>();

                    RenderHealth(score, issues);

                    // Per-disk SMART runs as its own script so a slow or failing disk query can't sink the main scan
                    SetBusy(true, "Reading disk health (SMART)...");
                    var storage = await _executor.ExecuteAsync(ip, user, pass, StorageHealthHelper.GetStorageScript(), timeoutSeconds: 90);
                    var disks = StorageHealthHelper.Parse(storage.Output);
                    RenderStorage(disks);
                    if (disks != null)
                    {
                        var diskIssues = disks.Where(d => d.Status is "Bad" or "Caution")
                            .Select(d => $"Disk {d.Model}: {(d.Status == "Bad" ? "failing" : "needs attention")}{(d.Notes.Count > 0 ? " - " + d.Notes[0] : "")}");
                        if (diskIssues.Any()) RenderHealth(score, issues.Concat(diskIssues).ToList());
                    }
                }

                LogTerminal("Deep diagnostics finished.");
                TxtStatus.Text = $"Scan finished ({DateTime.Now:HH:mm}).";
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Diagnostics failed: {ex.Message}";
            LogTerminal($"[ERR] {ex.Message}");
        }
        finally
        {
            BtnRunDiag.IsEnabled = true;
            SetBusy(false);
        }
    }

    #endregion

    #region Turbo Optimizer

    private readonly SnapshotStore _snapshots = new();

    private async Task<SystemSnapshot?> TakeSnapshotAsync(string ip, string user, string pass, string label)
    {
        var res = await _executor.ExecuteAsync(ip, user, pass, SnapshotHelper.GetSnapshotScript(), timeoutSeconds: 120);
        var snap = SnapshotHelper.Parse(res.Output);
        if (snap == null)
        {
            LogTerminal($"[ERR] Snapshot failed: {res.Error}");
            return null;
        }
        snap.Label = label;
        _snapshots.Save(snap);
        return snap;
    }

    private async void BtnRunOptimize_Click(object sender, RoutedEventArgs e)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;

        if (MessageBox.Show($"Run optimization on {ip}?\nThe state before and after is measured and saved.", "Optimizer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        // Row 0 and the last row wrap the script; rows 1..7 match its "STEP n:" lines
        string[] steps =
        {
            "Record current state",
            "Stop and disable SysMain",
            "Clear the Windows Update download cache",
            "Clear Temp folders",
            "Switch to the High Performance power plan",
            "Disable Fast Startup",
            "ReTrim drive C:",
            "Limit Defender scans to 25% CPU",
            "Measure the result"
        };
        int last = steps.Length - 1;
        var progress = new OptimizeProgressWindow(IsLocalTarget(ip) ? $"This PC ({Environment.MachineName})" : ip, steps) { Owner = this };
        progress.Show();

        BtnRunOptimize.IsEnabled = false;
        try
        {
            BtnCompareAfterReboot.IsEnabled = false;
            SetBusy(true, "Recording state before optimizing...", blockView: false);
            progress.Start(0);
            var before = await TakeSnapshotAsync(ip, user, pass, "before");
            if (before == null) progress.Fail(0);

            SetBusy(true, "Optimizing...", blockView: false);
            progress.Start(1);
            if (_live != null) { _optStartIndex = _liveCounter; _optEndIndex = null; }
            var res = await _executor.ExecuteAsync(ip, user, pass, DiagnosticEngine.GetOptimizationScript(), timeoutSeconds: 180,
                line =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"^STEP (\d+):");
                    if (m.Success) Dispatcher.BeginInvoke(() => progress.Start(int.Parse(m.Groups[1].Value)));
                });
            LogTerminal(res.Output);
            if (!res.Success && string.IsNullOrWhiteSpace(res.Output))
            {
                TxtStatus.Text = "Optimization did not run.";
                progress.Finish(false, $"Did not run: {res.Error.Trim()}");
                return;
            }

            if (_live != null) _optEndIndex = _liveCounter;
            SetBusy(true, "Recording state after optimizing...", blockView: false);
            progress.Start(last);
            var after = await TakeSnapshotAsync(ip, user, pass, "after");

            if (before != null && after != null)
            {
                progress.Complete(last);
                RenderComparison(before, after);
                var needsRestart = after.FastStartup != before.FastStartup || after.PendingReboot;
                TxtStatus.Text = needsRestart
                    ? "Optimization done. Some changes finish after a restart; after restarting, click \"Compare after restart\"."
                    : "Optimization done.";
                progress.Finish(true, needsRestart ? "Done. Some changes take effect after a restart." : "Done. See \"Before and after\" for the results.");
            }
            else
            {
                progress.Fail(last);
                TxtStatus.Text = "Optimization ran, but the before/after measurement failed (details in the console).";
                progress.Finish(true, "Optimization ran, but the before/after measurement failed.");
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
            LogTerminal($"[ERR] {ex.Message}");
            progress.Finish(false, ex.Message);
        }
        finally
        {
            BtnRunOptimize.IsEnabled = true;
            BtnCompareAfterReboot.IsEnabled = true;
            SetBusy(false);
        }
    }

    private async void BtnCompareAfterReboot_Click(object sender, RoutedEventArgs e)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;

        BtnCompareAfterReboot.IsEnabled = false;
        SetBusy(true, "Measuring current state...");
        try
        {
            var now = await TakeSnapshotAsync(ip, user, pass, "after");
            if (now == null)
            {
                TxtStatus.Text = "Could not measure the current state.";
                return;
            }
            var before = _snapshots.LatestFor(now.Host, "before");
            if (before == null)
            {
                TxtStatus.Text = $"No saved \"before\" state for {now.Host} yet; click \"Run optimization\" first.";
                return;
            }
            RenderComparison(before, now);
            TxtStatus.Text = $"Compared with the state saved at {before.TakenAt}.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            BtnCompareAfterReboot.IsEnabled = true;
            SetBusy(false);
        }
    }

    private enum Outcome { Improved, AlreadyGood, Unchanged, Worse, Info }

    private void RenderComparison(SystemSnapshot b, SystemSnapshot a)
    {
        TxtOptCompareInfo.Text = $"{a.Host}  ·  before: {b.TakenAt}  ·  after: {a.TakenAt}";
        static string OnOff(bool v) => v ? "On" : "Off";
        static bool SysMainOff(SystemSnapshot s) => s.SysMainStartType.Equals("Disabled", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(s.SysMainStatus);

        var rows = new List<(string Label, string Before, string After, Outcome Outcome)>
        {
            ("SysMain service", $"{b.SysMainStatus} / {b.SysMainStartType}", $"{a.SysMainStatus} / {a.SysMainStartType}",
                Judge(SysMainOff(b), SysMainOff(a))),
            ("Power plan", b.PowerPlanName, a.PowerPlanName, Judge(b.PowerPlanIsHighPerf, a.PowerPlanIsHighPerf)),
            ("Fast Startup", OnOff(b.FastStartup), OnOff(a.FastStartup), Judge(!b.FastStartup, !a.FastStartup)),
            ("Windows Update cache", $"{b.UpdateCacheMB} MB", $"{a.UpdateCacheMB} MB", JudgeLess(b.UpdateCacheMB, a.UpdateCacheMB, 1)),
            ("Temp files", $"{b.TempMB} MB", $"{a.TempMB} MB", JudgeLess(b.TempMB, a.TempMB, 1)),
            ("Free space on C:", $"{b.CFreeGB} GB", $"{a.CFreeGB} GB", JudgeLess(-b.CFreeGB, -a.CFreeGB, 0.1)),
            ("Pending restart", b.PendingReboot ? "Yes" : "No", a.PendingReboot ? "Yes" : "No", Outcome.Info),
            ("Startup programs", b.StartupCount.ToString(), a.StartupCount.ToString(), Outcome.Info),
        };

        OptCompareContainer.Children.Clear();
        OptCompareContainer.Children.Add(BuildCompareRow("Item", "Before", "After", null, header: true, last: false));
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            OptCompareContainer.Children.Add(BuildCompareRow(r.Label, r.Before, r.After, r.Outcome, header: false, last: i == rows.Count - 1));
        }
    }

    // goodBefore/goodAfter: whether the setting is in the desired state at each point
    private static Outcome Judge(bool goodBefore, bool goodAfter) =>
        goodAfter ? (goodBefore ? Outcome.AlreadyGood : Outcome.Improved) : (goodBefore ? Outcome.Worse : Outcome.Unchanged);

    // lower is better; differences within the threshold count as unchanged
    private static Outcome JudgeLess(double before, double after, double threshold) =>
        after < before - threshold ? Outcome.Improved : after > before + threshold ? Outcome.Worse : Outcome.Unchanged;

    private Border BuildCompareRow(string label, string before, string after, Outcome? outcome, bool header, bool last)
    {
        var row = new Border { Style = (Style)FindResource(last ? "LastRowStyle" : "RowStyle"), Padding = new Thickness(18, header ? 10 : 12, 18, header ? 10 : 12) };
        if (header) row.Background = (Brush)FindResource("ChromeBrush");
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });

        TextBlock Cell(string text, int col, bool mono)
        {
            var t = new TextBlock
            {
                Text = text,
                Style = (Style)FindResource(header ? "CaptionStyle" : "RowTitleStyle"),
                FontSize = header ? 12 : 13,
                FontWeight = header ? FontWeights.Normal : (col == 0 ? FontWeights.Medium : FontWeights.Normal),
                Foreground = (Brush)FindResource(header || col > 0 ? "TextSecondaryBrush" : "TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (mono && !header) { t.FlowDirection = FlowDirection.LeftToRight; t.HorizontalAlignment = HorizontalAlignment.Left; }
            Grid.SetColumn(t, col);
            return t;
        }
        g.Children.Add(Cell(label, 0, false));
        g.Children.Add(Cell(before, 1, true));
        g.Children.Add(Cell(after, 2, true));

        if (outcome.HasValue)
        {
            var (text, fg, bg) = outcome.Value switch
            {
                Outcome.Improved => ("Improved", "SuccessBrush", "SuccessSoftBrush"),
                Outcome.AlreadyGood => ("Already good", "TextSecondaryBrush", "CardHoverBrush"),
                Outcome.Worse => ("Worse", "DangerBrush", "DangerSoftBrush"),
                Outcome.Info => ("—", "TextMutedBrush", "CardBrush"),
                _ => ("Unchanged", "WarningBrush", "WarningSoftBrush"),
            };
            var tag = new Border
            {
                Style = (Style)FindResource("TagStyle"),
                Background = (Brush)FindResource(bg),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = text, FontSize = 12, Foreground = (Brush)FindResource(fg) }
            };
            Grid.SetColumn(tag, 3);
            g.Children.Add(tag);
        }
        else if (header)
        {
            g.Children.Add(Cell("Result", 3, false));
        }

        row.Child = g;
        return row;
    }

    #endregion

    #region Driver Injector

    private void BtnBrowseDriverPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder with the driver packages (.EXE)",
            InitialDirectory = Directory.Exists(TxtDriverPath.Text) ? TxtDriverPath.Text : ""
        };

        if (dialog.ShowDialog() == true)
        {
            TxtDriverPath.Text = dialog.FolderName;
        }
    }

    private async void BtnInstallDrivers_Click(object sender, RoutedEventArgs e)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;
        var folder = TxtDriverPath.Text.Trim();

        BtnInstallDrivers.IsEnabled = false;
        SetBusy(true, "Extracting and installing drivers...");
        TxtStatus.Text = "Installing drivers...";
        LogTerminal($"Extracting and installing INF files from {folder} on {ip}...");

        try
        {
            var script = DiagnosticEngine.GetDriverInstallScript(folder);
            var res = await _executor.ExecuteAsync(ip, user, pass, script, timeoutSeconds: 300);

            LogTerminal(res.Output);
            MessageBox.Show("Driver installation finished.", "Driver installer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogTerminal($"[ERR] {ex.Message}");
        }
        finally
        {
            BtnInstallDrivers.IsEnabled = true;
            SetBusy(false);
        }
    }

    #endregion

    #region Dev & Database Fixer

    private async void RunDevFix(string fixType, string actionName)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;

        SetBusy(true, $"Running {actionName}...");
        TxtStatus.Text = $"Running {actionName}...";
        LogTerminal($"[FIX] Running {actionName} on {ip}...");

        try
        {
            var script = DevDbFixer.GetFixScript(fixType);
            var res = await _executor.ExecuteAsync(ip, user, pass, script, timeoutSeconds: 120);
            LogTerminal(res.Output);
            MessageBox.Show($"{actionName} finished.", "Repair result", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogTerminal($"[ERR] {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BtnFixSqlSector_Click(object sender, RoutedEventArgs e) => RunDevFix("sql-nvme-4kb", "NVMe sector size patch for SQL Server");
    private void BtnFixPendingReboot_Click(object sender, RoutedEventArgs e) => RunDevFix("purge-pending-reboot", "Clear pending-restart locks");
    private void BtnCleanVs_Click(object sender, RoutedEventArgs e) => RunDevFix("clean-vs-installer", "Clean Visual Studio cache");
    private void BtnFixWmi_Click(object sender, RoutedEventArgs e) => RunDevFix("salvage-wmi", "Repair WMI repository");

    #endregion

    #region Rescue Doctor

    private async void RunRescueFix(string fixType, string actionName)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;

        SetBusy(true, $"Running {actionName}...");
        TxtStatus.Text = $"Running {actionName}...";
        LogTerminal($"[RESCUE] Running {actionName} on {ip}...");

        try
        {
            var script = RescueDoctor.GetRescueFixScript(fixType);
            var res = await _executor.ExecuteAsync(ip, user, pass, script, timeoutSeconds: 180);
            LogTerminal(res.Output);
            MessageBox.Show($"{actionName} finished.", "Boot rescue", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogTerminal($"[ERR] {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BtnRebuildBcd_Click(object sender, RoutedEventArgs e) => RunRescueFix("rebuild-bcd", "Rebuild UEFI BCD bootloader");
    private void BtnRevertUpdates_Click(object sender, RoutedEventArgs e) => RunRescueFix("revert-updates", "Roll back broken updates");
    private void BtnRepairSfc_Click(object sender, RoutedEventArgs e) => RunRescueFix("dism-restore", "SFC and DISM repair");

    private void BtnSaveRescueScript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = RescueDoctor.GetOfflineRescueScriptContent();
            var targetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RescueBootDoctor.bat");
            File.WriteAllText(targetPath, content, System.Text.Encoding.Default);
            MessageBox.Show($"Boot rescue script saved to the Desktop:\n{targetPath}\n\nCopy it to a USB stick and run it from WinRE.", "Rescue script saved", MessageBoxButton.OK, MessageBoxImage.Information);
            LogTerminal($"RescueBootDoctor.bat created on the Desktop: {targetPath}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save the file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Office LAN, Printer & Anti-Sanction

    private async void RunOfficeAction(string actionType, string actionName, string folder = "", string share = "")
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;

        SetBusy(true, $"Running {actionName}...");
        TxtStatus.Text = $"Running {actionName}...";
        LogTerminal($"[LAN] Running {actionName} on {ip}...");

        try
        {
            var script = LocalOfficeToolkit.GetLanActionScript(actionType, folder, share, "");
            var res = await _executor.ExecuteAsync(ip, user, pass, script, timeoutSeconds: 120);
            LogTerminal(res.Output);
            MessageBox.Show($"{actionName} applied.", "Office network", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogTerminal($"[ERR] {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BtnProtectCracks_Click(object sender, RoutedEventArgs e) => RunOfficeAction("protect-cracks", "Defender exclusions for activators");
    private void BtnScheduleMaintenance_Click(object sender, RoutedEventArgs e) => RunOfficeAction("schedule-weekly-maintenance", "Weekly maintenance task");

    private void BtnDnsShecan_Click(object sender, RoutedEventArgs e) => RunOfficeAction("set-dns-shecan", "DNS: Shecan");
    private void BtnDnsElectro_Click(object sender, RoutedEventArgs e) => RunOfficeAction("set-dns-electro", "DNS: Electro");
    private void BtnDnsRadar_Click(object sender, RoutedEventArgs e) => RunOfficeAction("set-dns-radar", "DNS: Radar Game");
    private void BtnDns403_Click(object sender, RoutedEventArgs e) => RunOfficeAction("set-dns-403", "DNS: 403.online");
    private void BtnDnsBegzar_Click(object sender, RoutedEventArgs e) => RunOfficeAction("set-dns-begzar", "DNS: Begzar");
    private void BtnDnsCloudflare_Click(object sender, RoutedEventArgs e) => RunOfficeAction("set-dns-cloudflare", "DNS: Cloudflare 1.1.1.1");
    private void BtnDnsGoogle_Click(object sender, RoutedEventArgs e) => RunOfficeAction("set-dns-google", "DNS: Google 8.8.8.8");
    private void BtnDnsAuto_Click(object sender, RoutedEventArgs e) => RunOfficeAction("reset-dns-auto", "DNS: back to automatic");

    #endregion

    #region Office network wizard

    private WizardHostInfo? _wizHost;
    private string _wizHostIp = "", _wizUser = "", _wizPass = "", _wizAccount = "";
    private string _wizPrinterShare = "", _wizFolderShare = "";
    private const string NoPrinter = "Don't share a printer";

    private void UpdateWizardHostLabel()
    {
        if (TxtWizHost == null) return;
        var ip = TxtIp.Text.Trim();
        TxtWizHost.Text = IsLocalTarget(ip) ? $"Host: This PC ({Environment.MachineName})" : $"Host: {ip}";
        // A different target invalidates the previous check
        if (_wizHost != null && ip != _wizTargetIp)
        {
            _wizHost = null;
            WizHostOptions.Visibility = Visibility.Collapsed;
            BtnWizConnect.IsEnabled = false;
        }
    }

    private string _wizTargetIp = "";

    private async void BtnWizCheck_Click(object sender, RoutedEventArgs e)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;
        bool isLocal = IsLocalTarget(ip);

        SetBusy(true, "Checking the host...");
        try
        {
            var res = await _executor.ExecuteAsync(ip, user, pass, OfficeNetworkWizard.GetHostCheckScript(), timeoutSeconds: 60);
            var info = OfficeNetworkWizard.ParseHostInfo(res.Output);
            if (info == null)
            {
                TxtStatus.Text = "Could not check the host.";
                MessageBox.Show($"Could not read the host's network settings.\n\n{res.Error.Trim()}", "Office network wizard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _wizHost = info;
            _wizTargetIp = ip;
            _wizUser = user;
            _wizPass = pass;
            // The account others sign in with: the target-card account for a remote host, the current user for this PC
            _wizAccount = isLocal ? Environment.UserName : user.Split('\\').Last();
            _wizHostIp = isLocal ? info.Adapters.FirstOrDefault()?.Ip ?? ip : ip;

            var adapters = info.Adapters.Count == 0 ? "no wired/Wi-Fi network found"
                : string.Join(", ", info.Adapters.Select(a => $"{a.Name} {a.Ip} ({a.Category})"));
            TxtWizHostInfo.Text = $"{info.Host} · workgroup {info.Workgroup} · {adapters}";

            var notes = new List<string>();
            if (info.Adapters.Any(a => a.Category == "Public")) notes.Add("The network is set to Public; setup will switch it to Private.");
            if (info.PublicSharingRules > 0) notes.Add($"Sharing is currently open on public networks ({info.PublicSharingRules} firewall rules); setup will close it.");
            if (info.Vpns.Count > 0) notes.Add($"A VPN is active on the host ({string.Join(", ", info.Vpns)}); other PCs may need to use the IP address instead of the name.");
            if (info.Printers.Count == 0) notes.Add("No physical printer is installed on the host. To share one, install it and its driver there first, then check again.");
            if (isLocal) notes.Add("This PC is the host: others will sign in with your Windows account, so it must have a password.");
            TxtWizHostNotes.Text = string.Join("\n", notes.Select(n => "• " + n));
            WizHostNotes.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            CmbWizPrinter.Items.Clear();
            CmbWizPrinter.Items.Add(NoPrinter);
            foreach (var p in info.Printers) CmbWizPrinter.Items.Add(p.Name);
            CmbWizPrinter.SelectedIndex = info.Printers.Count > 0 ? 1 : 0;

            TxtWizAccount.Text = $"Access is password-protected: other PCs sign in as {info.Host}\\{_wizAccount}.";
            WizHostOptions.Visibility = Visibility.Visible;
            WizHostResults.Visibility = Visibility.Collapsed;
            WizClientResults.Visibility = Visibility.Collapsed;
            BtnWizConnect.IsEnabled = false;
            TxtWizClientDesc.Text = isLocal
                ? "This PC is the host, so there is nothing to connect here. Connect the other PCs with step 3."
                : "Saves the host's sign-in for this Windows user, connects the printer and tests the folder with a temporary file.";
            UpdateWizardManualSteps();
            TxtStatus.Text = $"Host {info.Host} checked.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
            MessageBox.Show(ex.Message, "Office network wizard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnWizSetupHost_Click(object sender, RoutedEventArgs e)
    {
        if (_wizHost == null) return;
        var printer = CmbWizPrinter.SelectedItem as string ?? "";
        if (printer == NoPrinter) printer = "";
        var existing = _wizHost.Printers.FirstOrDefault(p => p.Name == printer);
        _wizPrinterShare = printer.Length == 0 ? ""
            : existing is { Shared: true } && existing.ShareName.Length > 0 ? existing.ShareName
            : MakeShareName(printer);
        var folder = TxtWizFolder.Text.Trim();
        var shareName = TxtWizShareName.Text.Trim();
        _wizFolderShare = folder.Length == 0 ? "" : MakeShareName(shareName.Length > 0 ? shareName : "Shared");

        if (printer.Length == 0 && folder.Length == 0)
        {
            MessageBox.Show("Choose a printer or a folder to share.", "Office network wizard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var summary = new List<string> { "• Set the LAN to Private and allow sharing only on Private networks", "• Close sharing on public networks" };
        if (printer.Length > 0) summary.Add($"• Share the printer \"{printer}\" as {_wizPrinterShare}");
        if (folder.Length > 0) summary.Add($"• Share {folder} as {_wizFolderShare} (created if missing)");
        summary.Add($"• Access: sign in as {_wizHost.Host}\\{_wizAccount}");
        if (MessageBox.Show($"Apply these changes on {_wizHost.Host}?\n\n{string.Join("\n", summary)}", "Set up host",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true, "Setting up the host...");
        try
        {
            var script = OfficeNetworkWizard.GetHostSetupScript(printer, _wizPrinterShare, folder, _wizFolderShare, _wizAccount);
            var res = await _executor.ExecuteAsync(_wizTargetIp, _wizUser, _wizPass, script, timeoutSeconds: 120);
            LogTerminal(res.Output);
            var steps = OfficeNetworkWizard.ParseSteps(res.Output);
            if (steps.Count == 0) steps.Add(new WizardStep("fail", "Setup did not run", res.Error.Trim()));
            RenderWizardSteps(WizHostResults, WizHostResultsPanel, steps);

            var failed = steps.Count(s => s.Status == "fail");
            BtnWizConnect.IsEnabled = failed == 0 && !IsLocalTarget(_wizTargetIp);
            UpdateWizardManualSteps();
            TxtStatus.Text = failed == 0 ? "Host is set up." : $"Host setup finished with {failed} problem(s); see the list.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnWizConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_wizHost == null) return;
        SetBusy(true, "Connecting this PC...");
        try
        {
            var script = OfficeNetworkWizard.GetClientSetupScript(_wizHostIp, _wizHost.Host, _wizAccount, _wizPrinterShare, _wizFolderShare);
            // Runs locally as the signed-in user; the host password goes in via the environment, not the script file
            var res = await _executor.ExecuteAsync("127.0.0.1", "", "", script, timeoutSeconds: 180,
                secretEnv: new Dictionary<string, string> { ["RWD_SHARE_PASS"] = _wizPass });
            LogTerminal(res.Output);
            var steps = OfficeNetworkWizard.ParseSteps(res.Output);
            if (steps.Count == 0) steps.Add(new WizardStep("fail", "Connection did not run", res.Error.Trim()));
            RenderWizardSteps(WizClientResults, WizClientResultsPanel, steps);
            var failed = steps.Count(s => s.Status == "fail");
            TxtStatus.Text = failed == 0 ? "This PC is connected." : $"{failed} problem(s) connecting this PC; see the list.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderWizardSteps(Border card, StackPanel panel, List<WizardStep> steps)
    {
        panel.Children.Clear();
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var (glyph, brush) = s.Status switch
            {
                "ok" => ("\uE73E", "SuccessBrush"),
                "warn" => ("\uE7BA", "WarningBrush"),
                "fail" => ("\uE711", "DangerBrush"),
                _ => ("\uE946", "AccentBrush")
            };
            var row = new Border { Style = (Style)FindResource(i == steps.Count - 1 ? "LastRowStyle" : "RowStyle"), Padding = new Thickness(16, 10, 16, 10) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("IconStyle"), Foreground = (Brush)FindResource(brush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = s.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(s.Detail))
                text.Children.Add(new TextBlock { Text = s.Detail, Style = (Style)FindResource("RowDescStyle") });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            row.Child = grid;
            panel.Children.Add(row);
        }
        card.Visibility = Visibility.Visible;
    }

    private void UpdateWizardManualSteps()
    {
        if (_wizHost == null) return;
        var lines = new List<string>
        {
            $"1. Press Win + R, type \\\\{_wizHostIp} and press Enter.",
            $"2. Sign in as {_wizHost.Host}\\{_wizAccount} with that account's password, and tick \"Remember my credentials\"."
        };
        int n = 3;
        if (_wizPrinterShare.Length > 0) lines.Add($"{n++}. Double-click {_wizPrinterShare} to connect the printer (Windows installs the driver; this may need an administrator).");
        if (_wizFolderShare.Length > 0) lines.Add($"{n++}. Right-click {_wizFolderShare} > Map network drive to give the folder a drive letter.");
        if (_wizPrinterShare.Length == 0 && _wizFolderShare.Length == 0) lines.Add($"{n}. Run \"Set up host\" first to see the shared printer and folder.");
        lines.Add("If the name doesn't work on a PC with a VPN, keep using the IP address above.");
        TxtWizManual.Text = string.Join("\n", lines);
    }

    private void BtnWizCopySteps_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(TxtWizManual.Text);
        TxtStatus.Text = "Steps copied to the clipboard.";
    }

    /// <summary>Share names: letters, digits and dashes, so they are easy to type in \\host\name.</summary>
    private static string MakeShareName(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9-]+", "-").Trim('-');
        if (s.Length > 40) s = s[..40].Trim('-');
        return s.Length == 0 ? "Shared" : s;
    }

    #endregion

    #region System Health Toolbox

    private async void RunToolboxTool(string script, string toolName, Func<string, string>? format = null)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;

        bool isLocal = IsLocalTarget(ip);
        if (!isLocal && string.IsNullOrEmpty(pass))
        {
            // Without this guard, WinRM auth failure pops Windows' own native credential
            // dialog, which can hang the app waiting for input behind the main window.
            MessageBox.Show(
                $"To run \"{toolName}\" on {ip}, first enter that system's password in the target card.",
                "Password missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, $"Running {toolName}...");
        LogToolbox($"\r\n--- {toolName} ({DateTime.Now:HH:mm:ss}) ---");

        try
        {
            var res = await _executor.ExecuteAsync(ip, user, pass, script, timeoutSeconds: 60);
            LogToolbox(res.Success ? (format != null ? format(res.Output) : res.Output.Trim()) : $"[ERROR] {res.Error}");
        }
        catch (Exception ex)
        {
            LogToolbox($"[ERR] {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LogToolbox(string message)
    {
        TxtToolboxOutput.AppendText($"{message}\n");
        TxtToolboxOutput.ScrollToEnd();
    }

    private void BtnToolResourceSnapshot_Click(object sender, RoutedEventArgs e) => RunToolboxTool(ToolboxHelper.GetResourceSnapshotScript(), "Resource snapshot");
    private void BtnToolDiskHealth_Click(object sender, RoutedEventArgs e) => RunToolboxTool(StorageHealthHelper.GetStorageScript(), "Disk health (SMART)",
        output => StorageHealthHelper.Parse(output) is { } disks ? StorageHealthHelper.ToText(disks) : output.Trim());
    private void BtnToolBattery_Click(object sender, RoutedEventArgs e) => RunToolboxTool(ToolboxHelper.GetBatteryHealthScript(), "Battery health");
    private void BtnToolMemTest_Click(object sender, RoutedEventArgs e) => RunToolboxTool(ToolboxHelper.GetScheduleMemoryTestScript(), "Schedule RAM test");
    private void BtnToolRecentErrors_Click(object sender, RoutedEventArgs e) => RunToolboxTool(ToolboxHelper.GetRecentErrorsScript(), "Recent Windows errors");
    private void BtnToolLargestFiles_Click(object sender, RoutedEventArgs e) => RunToolboxTool(ToolboxHelper.GetLargestFilesScript(), "Largest files on C:");
    private void BtnToolExportDrivers_Click(object sender, RoutedEventArgs e) => RunToolboxTool(ToolboxHelper.GetExportDriversScript(), "Driver backup");
    private void BtnToolDuplicateFiles_Click(object sender, RoutedEventArgs e) => RunToolboxTool(ToolboxHelper.GetDuplicateFilesScript(), "Duplicate file scan");

    private void BtnToolStartupManager_Click(object sender, RoutedEventArgs e)
    {
        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;
        bool isLocal = IsLocalTarget(ip);
        if (!isLocal && string.IsNullOrEmpty(pass))
        {
            MessageBox.Show($"To manage startup programs on {ip}, first enter that system's password in the target card.",
                "Password missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var label = isLocal ? $"This PC ({Environment.MachineName})" : ip;
        new StartupManagerWindow(label, script => _executor.ExecuteAsync(ip, user, pass, script, timeoutSeconds: 60)) { Owner = this }.ShowDialog();
    }

    private void BtnToolClearOutput_Click(object sender, RoutedEventArgs e)
    {
        TxtToolboxOutput.Text = "[SYSTEM] Output cleared.\n";
    }

    #endregion

    #region Wake-on-LAN

    private void BtnSendWol_Click(object sender, RoutedEventArgs e)
    {
        var mac = TxtWolMac.Text.Trim();
        try
        {
            SendWakeOnLan(mac);
            LogToolbox($"[OK] Magic packet for {mac} broadcast on the local network.");
            TxtStatus.Text = $"Wake-on-LAN sent to {mac}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Invalid MAC address or the packet could not be sent:\n{ex.Message}", "Wake-on-LAN error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void SendWakeOnLan(string macAddress)
    {
        var macBytes = macAddress
            .Split(new[] { ':', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(h => Convert.ToByte(h, 16))
            .ToArray();

        if (macBytes.Length != 6)
            throw new FormatException("A MAC address is 6 bytes separated by : or -, e.g. AA:BB:CC:DD:EE:FF");

        var packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 1; i <= 16; i++) Array.Copy(macBytes, 0, packet, i * 6, 6);

        using var client = new System.Net.Sockets.UdpClient();
        client.EnableBroadcast = true;
        client.Send(packet, packet.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast, 9));
    }

    #endregion

    #region Live Monitor

    private const int SparkWindow = 90;          // ~3 minutes at ~2s per sample
    private const int AverageWindow = 30;        // samples averaged on each side of an optimization
    private PowerShellStream? _live;
    private string? _liveIp;
    private string? _liveLastError;
    private readonly List<(long Index, LiveSample Sample)> _liveSamples = new();
    private long _liveCounter;
    private long? _optStartIndex, _optEndIndex;

    private async void BtnLiveMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_live != null)
        {
            StopLive("Stopped");
            return;
        }

        var ip = TxtIp.Text.Trim();
        _liveSamples.Clear();
        _liveCounter = 0;
        _optStartIndex = _optEndIndex = null;
        _liveLastError = null;
        TxtLiveAverages.Visibility = Visibility.Collapsed;
        RedrawSparks();

        try
        {
            BtnLiveMonitor.IsEnabled = false;
            TxtLiveInfo.Text = "Connecting...";
            _live = await _executor.StartStreamAsync(ip, TxtUser.Text.Trim(), TxtPass.Password, LiveMonitorHelper.GetSamplingScript(),
                line => Dispatcher.BeginInvoke(() => OnLiveLine(line)),
                err => Dispatcher.BeginInvoke(() => _liveLastError = err),
                () => Dispatcher.BeginInvoke(OnLiveExited));
            _liveIp = ip;
            BtnLiveMonitor.Content = "Stop monitoring";
        }
        catch (Exception ex)
        {
            TxtLiveInfo.Text = ex.Message;
        }
        finally
        {
            BtnLiveMonitor.IsEnabled = true;
        }
    }

    private void StopLive(string info)
    {
        var s = _live;
        _live = null;
        _liveIp = null;
        s?.Dispose();
        BtnLiveMonitor.Content = "Start monitoring";
        TxtLiveInfo.Text = info;
    }

    private void OnLiveExited()
    {
        if (_live == null) return; // stopped on purpose
        StopLive(string.IsNullOrEmpty(_liveLastError) ? "Disconnected" : $"Disconnected: {_liveLastError}");
    }

    private void OnLiveLine(string line)
    {
        if (_live == null) return;
        var s = LiveMonitorHelper.Parse(line);
        if (s == null) return;

        _liveSamples.Add((_liveCounter++, s));
        TxtLiveCpu.Text = $"{s.Cpu:0}%";
        TxtLiveRam.Text = $"{s.Ram:0}%";
        TxtLiveDisk.Text = $"{s.Disk:0}%";
        TxtLiveDisk.Foreground = (Brush)FindResource(s.Disk >= 80 ? "WarningBrush" : "TextPrimaryBrush");
        TxtLiveCpu.Foreground = (Brush)FindResource(s.Cpu >= 85 ? "WarningBrush" : "TextPrimaryBrush");
        TxtLiveTop.Text = s.Top.Count == 0
            ? "Top processes: —"
            : "Top processes:  " + string.Join("   ·   ", s.Top.Select(p => $"{p.N} {p.C:0}%"));
        var who = PowerShellExecutor.IsLocalTarget(_liveIp ?? "") ? "This PC" : _liveIp;
        TxtLiveInfo.Text = $"{who} · every ~2 s · {_liveSamples.Count} samples";

        UpdateLiveAverages();
        RedrawSparks();
    }

    private void UpdateLiveAverages()
    {
        if (_optStartIndex is not long start || _optEndIndex is not long end) return;

        var before = _liveSamples.Where(x => x.Index < start).TakeLast(AverageWindow).Select(x => x.Sample).ToList();
        var after = _liveSamples.Where(x => x.Index > end).TakeLast(AverageWindow).Select(x => x.Sample).ToList();
        TxtLiveAverages.Visibility = Visibility.Visible;
        if (before.Count < 3)
        {
            TxtLiveAverages.Text = "To compare averages, start monitoring at least a few seconds before optimizing.";
            return;
        }
        if (after.Count < 3)
        {
            TxtLiveAverages.Text = $"Collecting post-optimization samples... ({after.Count}/3)";
            return;
        }
        static string Pair(string name, double b, double a) => $"{name} {b:0}% → {a:0}%";
        TxtLiveAverages.Text = $"Average before → after optimizing ({before.Count} / {after.Count} samples):   " +
            string.Join("   ·   ",
                Pair("CPU", before.Average(x => x.Cpu), after.Average(x => x.Cpu)),
                Pair("RAM", before.Average(x => x.Ram), after.Average(x => x.Ram)),
                Pair("Disk", before.Average(x => x.Disk), after.Average(x => x.Disk)));
    }

    private void Spark_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawSparks();

    private void RedrawSparks()
    {
        DrawSpark(SparkCpu, x => x.Cpu, "ColorAccent");
        DrawSpark(SparkRam, x => x.Ram, "ColorPurple");
        DrawSpark(SparkDisk, x => x.Disk, "ColorWarning");
    }

    private void DrawSpark(Canvas canvas, Func<LiveSample, double> value, string colorKey)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0 || h <= 0 || _liveSamples.Count == 0) return;

        var color = (Color)FindResource(colorKey);
        var window = _liveSamples.TakeLast(SparkWindow).ToList();
        long newest = window[^1].Index;
        double step = w / (SparkWindow - 1);
        double X(long index) => w - (newest - index) * step;   // newest sample on the right edge
        double Y(double v) => h - 2 - Math.Clamp(v, 0, 100) / 100.0 * (h - 4);

        var line = new System.Windows.Shapes.Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
        var fill = new System.Windows.Shapes.Polygon { Fill = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)) };
        fill.Points.Add(new Point(X(window[0].Index), h));
        foreach (var (index, sample) in window)
        {
            var p = new Point(X(index), Y(value(sample)));
            line.Points.Add(p);
            fill.Points.Add(p);
        }
        fill.Points.Add(new Point(X(newest), h));
        canvas.Children.Add(fill);
        canvas.Children.Add(line);

        foreach (var marker in new[] { _optStartIndex, _optEndIndex })
        {
            if (marker is not long m || m < window[0].Index) continue;
            var x = X(m);
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = h,
                Stroke = (Brush)FindResource("TextSecondaryBrush"),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });
        }
    }

    #endregion

    #region Terminal

    private void BtnClearTerminal_Click(object sender, RoutedEventArgs e)
    {
        TxtTerminalOutput.Text = "[SYSTEM] Console cleared.\n";
    }

    private void BtnChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string cmd)
        {
            TxtTerminalInput.Text = cmd;
            ExecuteTerminalCommand();
        }
    }

    private void TxtTerminalInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ExecuteTerminalCommand();
        }
    }

    private void BtnSendCmd_Click(object sender, RoutedEventArgs e) => ExecuteTerminalCommand();

    private async void ExecuteTerminalCommand()
    {
        var cmd = TxtTerminalInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(cmd)) return;

        var ip = TxtIp.Text.Trim();
        var user = TxtUser.Text.Trim();
        var pass = TxtPass.Password;

        TxtTerminalInput.Text = "";
        TxtTerminalInput.IsEnabled = false;
        LogTerminal($"PS {ip}> {cmd}");

        try
        {
            var res = await _executor.ExecuteAsync(ip, user, pass, cmd, timeoutSeconds: 60);
            if (res.Success)
            {
                LogTerminal(string.IsNullOrWhiteSpace(res.Output) ? "(command finished with no output)" : res.Output.Trim());
            }
            else
            {
                LogTerminal($"[ERROR] {res.Error}");
            }
        }
        catch (Exception ex)
        {
            LogTerminal($"[ERR] {ex.Message}");
        }
        finally
        {
            TxtTerminalInput.IsEnabled = true;
            TxtTerminalInput.Focus();
        }
    }

    private void LogTerminal(string message)
    {
        Dispatcher.Invoke(() =>
        {
            TxtTerminalOutput.AppendText($"{message}\n");
            TxtTerminalOutput.ScrollToEnd();
        });
    }

    #endregion
}