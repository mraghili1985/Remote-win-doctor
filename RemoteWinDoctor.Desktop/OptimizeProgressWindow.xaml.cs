using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RemoteWinDoctor.Desktop;

/// <summary>
/// Modeless checklist shown while the optimizer runs, so the live charts behind it stay visible.
/// Rows go pending -> running -> done/failed; the window can only be closed once finished.
/// </summary>
public partial class OptimizeProgressWindow : Window
{
    private enum StepState { Pending, Running, Done, Failed }

    private sealed class StepRow
    {
        public required TextBlock Icon;
        public required TextBlock Title;
        public required TextBlock Time;
        public StepState State;
        public readonly Stopwatch Clock = new();
    }

    private readonly List<StepRow> _rows = new();
    private readonly System.Windows.Threading.DispatcherTimer _ticker;
    private bool _finished;

    public OptimizeProgressWindow(string target, IReadOnlyList<string> steps)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        TxtTarget.Text = $"Target: {target}";

        for (int i = 0; i < steps.Count; i++)
        {
            var row = new Border { Style = (Style)FindResource(i == steps.Count - 1 ? "LastRowStyle" : "RowStyle"), Padding = new Thickness(16, 10, 16, 10) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock { Style = (Style)FindResource("IconStyle"), FontSize = 14, Width = 18, Margin = new Thickness(0, 0, 12, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
            var title = new TextBlock { Text = steps[i], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var time = new TextBlock { Style = (Style)FindResource("CaptionStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(title, 1);
            Grid.SetColumn(time, 2);
            grid.Children.Add(icon);
            grid.Children.Add(title);
            grid.Children.Add(time);
            row.Child = grid;
            StepsPanel.Children.Add(row);

            var step = new StepRow { Icon = icon, Title = title, Time = time };
            _rows.Add(step);
            Apply(step, StepState.Pending);
        }

        Progress.Maximum = steps.Count;
        _ticker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _ticker.Tick += (_, _) =>
        {
            foreach (var r in _rows.Where(r => r.State == StepState.Running))
                r.Time.Text = $"{r.Clock.Elapsed.TotalSeconds:0}s";
        };
        _ticker.Start();
    }

    /// <summary>Marks every earlier unfinished row done and starts <paramref name="index"/>.</summary>
    public void Start(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        for (int i = 0; i < index; i++)
            if (_rows[i].State is StepState.Pending or StepState.Running) Apply(_rows[i], StepState.Done);
        if (_rows[index].State == StepState.Pending) Apply(_rows[index], StepState.Running);
    }

    public void Complete(int index)
    {
        if (index >= 0 && index < _rows.Count && _rows[index].State != StepState.Failed) Apply(_rows[index], StepState.Done);
    }

    public void Fail(int index)
    {
        if (index >= 0 && index < _rows.Count) Apply(_rows[index], StepState.Failed);
    }

    public void Finish(bool success, string summary)
    {
        _finished = true;
        _ticker.Stop();
        foreach (var r in _rows.Where(r => r.State == StepState.Running))
            Apply(r, success ? StepState.Done : StepState.Failed);
        TxtTitle.Text = success ? "Optimization complete" : "Optimization stopped";
        TxtSummary.Text = summary;
        TxtSummary.Foreground = (Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
        BtnClose.IsEnabled = true;
        BtnClose.Style = (Style)FindResource("PrimaryButtonStyle");
        BtnClose.Focus();
    }

    private void Apply(StepRow r, StepState state)
    {
        r.State = state;
        r.Icon.BeginAnimation(OpacityProperty, null);
        r.Icon.Opacity = 1;
        switch (state)
        {
            case StepState.Pending:
                r.Icon.Text = "";   // empty circle
                r.Icon.Foreground = (Brush)FindResource("TextMutedBrush");
                r.Title.Foreground = (Brush)FindResource("TextSecondaryBrush");
                break;
            case StepState.Running:
                r.Icon.Text = "";   // filled circle, pulsing
                r.Icon.Foreground = (Brush)FindResource("AccentBrush");
                r.Title.Foreground = (Brush)FindResource("TextPrimaryBrush");
                r.Icon.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                r.Clock.Restart();
                break;
            case StepState.Done:
                r.Icon.Text = "";   // check mark
                r.Icon.Foreground = (Brush)FindResource("SuccessBrush");
                r.Title.Foreground = (Brush)FindResource("TextPrimaryBrush");
                break;
            case StepState.Failed:
                r.Icon.Text = "";   // cross
                r.Icon.Foreground = (Brush)FindResource("DangerBrush");
                r.Title.Foreground = (Brush)FindResource("TextPrimaryBrush");
                break;
        }
        if (state is StepState.Done or StepState.Failed && r.Clock.IsRunning)
        {
            r.Clock.Stop();
            r.Time.Text = $"{r.Clock.Elapsed.TotalSeconds:0}s";
        }
        Progress.Value = _rows.Count(x => x.State is StepState.Done or StepState.Failed);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing mid-run would hide progress without stopping the remote work
        if (!_finished) e.Cancel = true;
        base.OnClosing(e);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
