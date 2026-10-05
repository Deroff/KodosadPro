using System.Windows;

namespace KodosadStudio;

public partial class AnalysisHistoryWindow : Window
{
    public AnalysisHistoryWindow(IReadOnlyList<SavedAnalysisRun> runs)
    {
        InitializeComponent();
        RunsGrid.ItemsSource = runs;
        RunCountText.Text = $"{runs.Count} сохранённых прогонов · личная история аккаунта";
        if (runs.Count > 0) RunsGrid.SelectedIndex = 0;
    }

    private void RunsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RunsGrid.SelectedItem is not SavedAnalysisRun run) return;
        SelectionTitle.Text = $"{run.Algorithm} · {run.ProjectName}";
        MetricsText.Text = $"{run.CreatedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}   /   {run.SourceCharacters:N0} символов   /   {run.SourceBytes:N0} байт UTF-8\nВыход: {run.OutputBits:N0} бит   /   коэффициент {run.RatioPercent:0.0}%   /   H(X) {run.Entropy:0.000}";
        SourcePreview.Text = run.Source;
        OutputPreview.Text = run.Output;
        StatusText.Text = $"RUN #{run.RunId}";
    }

    private void CopySource_Click(object sender, RoutedEventArgs e)
    {
        if (RunsGrid.SelectedItem is not SavedAnalysisRun run) return;
        Clipboard.SetText(run.Source);
        StatusText.Text = "ИСХОДНИК СКОПИРОВАН";
    }

    private void CopyOutput_Click(object sender, RoutedEventArgs e)
    {
        if (RunsGrid.SelectedItem is not SavedAnalysisRun run) return;
        Clipboard.SetText(run.Output);
        StatusText.Text = "ПОТОК СКОПИРОВАН";
    }
}
