using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteWinDoctor.Services;

namespace RemoteWinDoctor.Desktop;

/// <summary>
/// Task Manager style startup list for the target PC: one row per entry with an on/off switch.
/// Switching writes the StartupApproved flag immediately and reverts the switch if that fails.
/// </summary>
public partial class StartupManagerWindow : Window
{
    private readonly Func<string, Task<ExecutionResult>> _run;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public StartupManagerWindow(string targetLabel, Func<string, Task<ExecutionResult>> run)
    {
        InitializeComponent();
        _run = run;
        TxtTarget.Text = targetLabel;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        BtnRefresh.IsEnabled = false;
        RowsPanel.Children.Clear();
        TxtListMessage.Text = "Loading...";
        TxtListMessage.Visibility = Visibility.Visible;
        TxtFooter.Text = "";
        try
        {
            var res = await _run(ToolboxHelper.GetStartupEntriesScript());
            var json = res.Output.Trim();
            var start = json.IndexOf('[');
            if (start < 0) throw new Exception(string.IsNullOrWhiteSpace(res.Error) ? "No data returned." : res.Error.Trim());

            var entries = JsonSerializer.Deserialize<List<StartupEntry>>(json[start..], JsonOptions) ?? new();
            if (entries.Count == 0)
            {
                TxtListMessage.Text = "No startup programs found.";
                return;
            }
            TxtListMessage.Visibility = Visibility.Collapsed;
            for (int i = 0; i < entries.Count; i++)
                RowsPanel.Children.Add(BuildRow(entries[i], i == entries.Count - 1));
            UpdateFooter(entries);
        }
        catch (Exception ex)
        {
            TxtListMessage.Text = $"Could not read the startup list: {ex.Message}";
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
        }
    }

    private UIElement BuildRow(StartupEntry entry, bool last)
    {
        var row = new Border { Style = (Style)FindResource(last ? "LastRowStyle" : "RowStyle"), Padding = new Thickness(16, 10, 16, 10), Tag = entry };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });

        // Prefer the file's own description ("Internet Download Manager") over the raw value name ("IDMan")
        var title = string.IsNullOrWhiteSpace(entry.Description) ? entry.Name : entry.Description;
        var nameStack = new StackPanel { Margin = new Thickness(0, 0, 12, 0), ToolTip = entry.Command };
        nameStack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.Equals(title, entry.Name, StringComparison.OrdinalIgnoreCase))
            nameStack.Children.Add(new TextBlock { Text = entry.Name, Style = (Style)FindResource("CaptionStyle"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });

        var publisher = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Publisher) ? "Unknown" : entry.Publisher,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        var scope = new TextBlock { Text = entry.Scope, Foreground = (Brush)FindResource("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

        var toggle = new CheckBox { Style = (Style)FindResource("SwitchStyle"), IsChecked = entry.Enabled, VerticalAlignment = VerticalAlignment.Center };
        toggle.Click += async (_, _) => await ToggleAsync(entry, toggle);

        Grid.SetColumn(publisher, 1);
        Grid.SetColumn(scope, 2);
        Grid.SetColumn(toggle, 3);
        grid.Children.Add(nameStack);
        grid.Children.Add(publisher);
        grid.Children.Add(scope);
        grid.Children.Add(toggle);
        row.Child = grid;
        return row;
    }

    private async Task ToggleAsync(StartupEntry entry, CheckBox toggle)
    {
        var enable = toggle.IsChecked == true;
        toggle.IsEnabled = false;
        try
        {
            var res = await _run(ToolboxHelper.GetSetStartupEntryScript(entry.ApprovedKey, entry.ValueName, enable));
            if (!res.Output.Contains("OK")) throw new Exception(string.IsNullOrWhiteSpace(res.Error) ? "No confirmation from the target." : res.Error.Trim());
            entry.Enabled = enable;
            TxtFooter.Foreground = (Brush)FindResource("TextMutedBrush");
            UpdateFooter(RowsPanel.Children.OfType<Border>().Select(b => (StartupEntry)b.Tag).ToList());
        }
        catch (Exception ex)
        {
            toggle.IsChecked = !enable;
            TxtFooter.Text = $"Could not change {entry.Name}: {ex.Message}";
            TxtFooter.Foreground = (Brush)FindResource("DangerBrush");
        }
        finally
        {
            toggle.IsEnabled = true;
        }
    }

    private void UpdateFooter(List<StartupEntry> entries)
    {
        var on = entries.Count(e => e.Enabled);
        TxtFooter.Text = $"{entries.Count} programs · {on} enabled · {entries.Count - on} disabled";
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
