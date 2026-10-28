using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteWinDoctor.Services;

namespace RemoteWinDoctor.Desktop;

/// <summary>
/// Scans the target for known registry breakages (WindowsRepairHelper) and offers one confirmed,
/// backed-up repair per finding. The list is re-checked after every repair.
/// </summary>
public partial class WindowsRepairsWindow : Window
{
    private readonly Func<string, int, Task<ExecutionResult>> _run;
    private readonly bool _isLocal;
    private bool _busy;

    public WindowsRepairsWindow(string targetLabel, bool isLocal, Func<string, int, Task<ExecutionResult>> run)
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 20);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 20);
        _run = run;
        _isLocal = isLocal;
        TxtTarget.Text = targetLabel;
        Loaded += async (_, _) => await ScanAsync();
    }

    private async Task ScanAsync()
    {
        SetBusy(true);
        RowsPanel.Children.Clear();
        TxtListMessage.Text = "Checking...";
        TxtListMessage.Visibility = Visibility.Visible;
        TxtFooter.Text = "";
        try
        {
            var res = await _run(WindowsRepairHelper.GetScanScript(), 120);
            var checks = WindowsRepairHelper.ParseScan(res.Output);
            if (checks.Count == 0)
                throw new Exception(string.IsNullOrWhiteSpace(res.Error) ? "No data returned." : res.Error.Trim());

            TxtListMessage.Visibility = Visibility.Collapsed;
            for (int i = 0; i < checks.Count; i++)
                RowsPanel.Children.Add(BuildRow(checks[i], i == checks.Count - 1));
            var problems = checks.Count(c => c.Status == "problem");
            TxtFooter.Foreground = (Brush)FindResource("TextMutedBrush");
            TxtFooter.Text = problems == 0 ? $"{checks.Count} checks · no problems found" : $"{checks.Count} checks · {problems} problem(s) found";
        }
        catch (Exception ex)
        {
            TxtListMessage.Text = $"Could not run the checks: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private UIElement BuildRow(RepairCheck check, bool last)
    {
        var row = new Border { Style = (Style)FindResource(last ? "LastRowStyle" : "RowStyle"), Padding = new Thickness(16, 12, 16, 12) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (glyph, brush) = check.Status switch
        {
            "ok" => ("", "SuccessBrush"),
            "problem" => ("", "DangerBrush"),
            _ => ("", "TextSecondaryBrush")
        };
        var icon = new TextBlock
        {
            Text = glyph,
            Style = (Style)FindResource("IconStyle"),
            Foreground = (Brush)FindResource(brush),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 14, 0)
        };

        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = check.Title, FontWeight = FontWeights.SemiBold });
        // Scripts separate the finding and its advice with " | "
        foreach (var part in check.Detail.Split(" | "))
            text.Children.Add(new TextBlock { Text = part, Style = (Style)FindResource("RowDescStyle"), TextWrapping = TextWrapping.Wrap });

        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);

        if (check.CanFix)
        {
            var btn = new Button
            {
                Content = WindowsRepairHelper.FixLabel(check.Id),
                Style = (Style)FindResource(check.Status == "problem" ? "PrimaryButtonStyle" : "SecondaryButtonStyle"),
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Click += async (_, _) => await FixAsync(check);
            Grid.SetColumn(btn, 2);
            grid.Children.Add(btn);
        }

        row.Child = grid;
        return row;
    }

    private async Task FixAsync(RepairCheck check)
    {
        if (_busy) return;
        if (MessageBox.Show(this, WindowsRepairHelper.ConfirmText(check.Id), check.Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        TxtFooter.Foreground = (Brush)FindResource("TextMutedBrush");
        TxtFooter.Text = check.Id == "defender" ? "Scanning... this can take several minutes." : $"Running: {WindowsRepairHelper.FixLabel(check.Id)}...";
        ResultsPanel.Children.Clear();
        ResultsCard.Visibility = Visibility.Visible;
        try
        {
            var res = await _run(WindowsRepairHelper.GetFixScript(check.Id, _isLocal), check.Id == "defender" ? 1200 : 120);
            var steps = OfficeNetworkWizard.ParseSteps(res.Output);
            if (steps.Count == 0)
                steps.Add(new WizardStep("fail", "No result from the target", string.IsNullOrWhiteSpace(res.Error) ? "" : res.Error.Trim()));
            foreach (var s in steps)
                ResultsPanel.Children.Add(BuildResult(s));
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Add(BuildResult(new WizardStep("fail", "Repair failed", ex.Message)));
        }
        finally
        {
            SetBusy(false);
        }
        await ScanAsync();
    }

    private UIElement BuildResult(WizardStep step)
    {
        var (glyph, brush) = step.Status switch
        {
            "ok" => ("", "SuccessBrush"),
            "warn" => ("", "WarningBrush"),
            "fail" => ("", "DangerBrush"),
            _ => ("", "TextSecondaryBrush")
        };
        var panel = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("IconStyle"), Foreground = (Brush)FindResource(brush), Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left);
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Inlines.Add(new System.Windows.Documents.Run(step.Title) { FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(step.Detail))
            body.Inlines.Add(new System.Windows.Documents.Run("  " + step.Detail) { Foreground = (Brush)FindResource("TextSecondaryBrush") });
        panel.Children.Add(icon);
        panel.Children.Add(body);
        return panel;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnRefresh.IsEnabled = !busy;
        RowsPanel.IsEnabled = !busy;
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (_busy && MessageBox.Show(this, "A check or repair is still running on the target. Close anyway? It keeps running there.",
                "Windows repairs", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Close();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
