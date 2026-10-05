using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace KodosadStudio;

public partial class MainWindow : Window
{
    public sealed record CodeRow(string Symbol, int Frequency, string Code);
    public sealed record CompareRow(string Algorithm, int Bits, string Ratio, string Average);
    private CompressionResult? _current;
    private readonly List<string> _history = [];
    private string _projectName = "Новый проект";
    private bool _denseTree;
    private int _stage = 2;
    private int _stepIndex = -1;
    private readonly DispatcherTimer _stepTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DraftAutosaveCoordinator _draftAutosave = new();
    private readonly UserSession _session;
    private readonly KodosadDatabase _database;

    public MainWindow(UserSession session, KodosadDatabase database)
    {
        InitializeComponent();
        _session = session;
        _database = database;
        ProfileButton.Content = "АККАУНТ · " + session.UserName;
        _stepTimer.Tick += (_, _) => AdvanceStep();
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveDraftSafelyAsync(); };
        SourceBox.TextChanged += (_, _) => QueueAutosave();
        AlgorithmBox.SelectionChanged += (_, _) => QueueAutosave();
        Loaded += async (_, _) => await RestoreWorkspaceAsync();
    }

    private async Task RestoreWorkspaceAsync()
    {
        try
        {
            var projects = await _database.GetProjectsAsync(_session);
            if (projects.Count > 0)
            {
                var project = projects[0];
                _projectName = project.Name;
                SourceBox.Text = project.Source;
                AlgorithmBox.SelectedIndex = project.Algorithm switch { "Шеннон–Фано" => 1, "RLE" => 2, _ => 0 };
                SessionText.Text = project.Name;
                FooterText.Text = $"ВОССТАНОВЛЕНО ИЗ БД · {projects.Count} проектов";
            }
            else FooterText.Text = "SQL SERVER · автосохранение включено";
            SessionInfo.Text = $"профиль: {_session.UserName}";
            RunAnalysis(persistRun: false);
        }
        catch (Exception ex) { ValidationText.Text = "Не удалось восстановить проекты: " + ex.Message; RunAnalysis(persistRun: false); }
    }

    private void QueueAutosave()
    {
        if (!IsLoaded) return;
        _draftAutosave.MarkChanged();
        ScheduleAutosave(TimeSpan.FromMilliseconds(900));
        FooterText.Text = "ИЗМЕНЕНИЯ · автосохранение через 1 с";
    }

    private void ScheduleAutosave(TimeSpan delay)
    {
        _saveTimer.Stop();
        _saveTimer.Interval = delay;
        _saveTimer.Start();
    }

    private async Task SaveDraftSafelyAsync()
    {
        var start = _draftAutosave.TryBegin(out var revision);
        if (start == DraftSaveStart.Clean) return;
        if (start == DraftSaveStart.Busy)
        {
            ScheduleAutosave(TimeSpan.FromMilliseconds(900));
            return;
        }

        try
        {
            var projectName = _projectName;
            var algorithm = Algorithm;
            var source = SourceBox.Text;
            await _database.SaveDraftAsync(_session, projectName, algorithm, source);
            var hasPendingChanges = _draftAutosave.Complete(revision, succeeded: true);
            if (hasPendingChanges)
            {
                FooterText.Text = "ЕСТЬ НОВЫЕ ИЗМЕНЕНИЯ · автосохранение продолжится";
                if (!_saveTimer.IsEnabled) ScheduleAutosave(TimeSpan.FromMilliseconds(900));
            }
            else
            {
                FooterText.Text = $"СИНХРОНИЗИРОВАНО С БД · {DateTime.Now:HH:mm:ss}";
                SessionInfo.Text = $"{_session.UserName} · сохранено {DateTime.Now:HH:mm}";
            }
        }
        catch (Exception ex)
        {
            var hasPendingChanges = _draftAutosave.Complete(revision, succeeded: false);
            FooterText.Text = "Не удалось сохранить; повтор запланирован: " + ex.Message;
            if (hasPendingChanges)
            {
                var delay = _draftAutosave.Revision == revision
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromMilliseconds(900);
                ScheduleAutosave(delay);
            }
        }
    }

    private async Task SaveRunSafelyAsync(CompressionResult result)
    {
        try
        {
            await _database.SaveRunAsync(_session, _projectName, SourceBox.Text, result);
            var runCount = await _database.GetRunCountAsync(_session);
            FooterText.Text = $"ЗАПУСК #{runCount} · результат сохранён в БД";
        }
        catch (Exception ex) { FooterText.Text = "Результат готов; запись в БД не выполнена: " + ex.Message; }
    }
    private string Algorithm => ((ComboBoxItem)AlgorithmBox.SelectedItem).Content.ToString()!;
    private void Profile_Click(object sender, RoutedEventArgs e) => new ProfileWindow(_session, _database) { Owner = this }.ShowDialog();
    private void FocusSource_Click(object sender, RoutedEventArgs e) { SourceBox.Focus(); SourceBox.CaretIndex = SourceBox.Text.Length; }
    private void CompareNav_Click(object sender, RoutedEventArgs e) => Compare_Click(sender, e);
    private void FileNav_Click(object sender, RoutedEventArgs e) => OpenFile_Click(sender, e);
    private async void History_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var runs = await _database.GetAnalysisHistoryAsync(_session);
            new AnalysisHistoryWindow(runs) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { ValidationText.Text = "Архив расчётов недоступен: " + ex.Message; }
    }
    private async void Projects_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var projects = await _database.GetProjectsAsync(_session);
            var picker = new ProjectPickerWindow(projects) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedProject is not { } project) return;
            _projectName = project.Name;
            SessionText.Text = project.Name;
            AlgorithmBox.SelectedIndex = project.Algorithm switch { "Шеннон–Фано" => 1, "RLE" => 2, _ => 0 };
            SourceBox.Text = project.Source;
            RunAnalysis(persistRun: false);
            FooterText.Text = "ОТКРЫТ ИЗ БИБЛИОТЕКИ SQL · автосохранение активно";
        }
        catch (Exception ex) { ValidationText.Text = "Библиотека недоступна: " + ex.Message; }
    }
    private void Run_Click(object sender, RoutedEventArgs e) => RunAnalysis();
    private void RunAnalysis(bool persistRun = true)
    {
        try
        {
            ValidationText.Text = ""; StateText.Text = "RUNNING"; _stepTimer.Stop(); AutoStepButton.Content = "АВТО"; _stepIndex = -1; _current = CompressionEngine.Run(Algorithm, SourceBox.Text); RenderResult(_current); SetStage(2); ShowStep(); AddHistory($"{_current.Algorithm}: {_current.SourceCharacters} симв. → {_current.OutputBits} бит"); StateText.Text = "COMPLETE"; SessionInfo.Text = $"{_session.UserName} · {DateTime.Now:HH:mm}";
            if (persistRun) _ = SaveRunSafelyAsync(_current);
        }
        catch (Exception ex) { ValidationText.Text = ex.Message; StateText.Text = "ERROR"; }
    }
    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var results = new[] { "Хаффман", "Шеннон–Фано", "RLE" }.Select(x => CompressionEngine.Run(x, SourceBox.Text)).ToList(); ComparisonGrid.ItemsSource = results.Select(x => new CompareRow(x.Algorithm, x.OutputBits, $"{x.RatioPercent:0.0}%", $"{x.AverageCodeLength:0.000}")).ToList(); var best = results.MinBy(x => x.RatioPercent)!; FooterText.Text = $"Лучший результат: {best.Algorithm} · {best.RatioPercent:0.0}% от UTF-8"; AddHistory($"Сравнение: победил {best.Algorithm}");
        }
        catch (Exception ex) { ValidationText.Text = ex.Message; }
    }
    private void Decode_Click(object sender, RoutedEventArgs e)
    {
        try { if (_current is null) throw new InvalidOperationException("Сначала выполните кодирование."); SourceBox.Text = CompressionEngine.Decode(_current.Algorithm, ResultBox.Text.Trim(), _current.Tree, _current.Codes); ValidationText.Text = "Декодирование выполнено и помещено в редактор."; SetStage(5); ShowStep(); AddHistory($"Декодирование {_current.Algorithm}"); }
        catch (Exception ex) { ValidationText.Text = ex.Message; }
    }
    private void RenderResult(CompressionResult result)
    {
        ResultBox.Text = result.Output; EntropyText.Text = result.Entropy.ToString("0.000"); AverageText.Text = result.AverageCodeLength.ToString("0.000"); RatioText.Text = result.RatioPercent.ToString("0.0") + "%"; RedundancyText.Text = result.Redundancy.ToString("0.000"); InspectorTitle.Text = result.Algorithm + (result.Tree is null ? " / анализ" : " / дерево"); TreeInfo.Text = $"Источник: {result.SourceBytes} байт · результат: {result.OutputBits} бит · алфавит: {result.Frequencies.Count} символов";
        CodesGrid.ItemsSource = result.Codes.OrderBy(x => x.Value.Length).ThenBy(x => x.Key).Select(x => new CodeRow(Display(x.Key), result.Frequencies[x.Key], x.Value)).ToList(); StepsList.ItemsSource = result.Steps; RenderFrequencyStrip(result); DrawTree();
    }
    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Архив Хаффмана KHF (*.khf)|*.khf|Проекты Kodosad (*.kodo.json)|*.kodo.json|Текстовые файлы (*.txt;*.csv;*.log)|*.txt;*.csv;*.log|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > 64_000_000) throw new InvalidDataException("Файл превышает допустимый размер 64 МБ.");
            if (dialog.FileName.EndsWith(".khf", StringComparison.OrdinalIgnoreCase))
            {
                SourceBox.Text = HuffmanArchiveCodec.Deserialize(File.ReadAllBytes(dialog.FileName));
                _projectName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
                SessionText.Text = _projectName;
                AlgorithmBox.SelectedIndex = 0;
                RunAnalysis(persistRun: false);
                FooterText.Text = $"KHF · исходный текст восстановлен · {info.Length:N0} байт";
            }
            else if (dialog.FileName.EndsWith(".kodo.json", StringComparison.OrdinalIgnoreCase))
            {
                var project = KodoProjectCodec.Deserialize(File.ReadAllText(dialog.FileName));
                _projectName = project.Name;
                SourceBox.Text = project.Source;
                AlgorithmBox.SelectedIndex = project.Algorithm switch { "Шеннон–Фано" => 1, "RLE" => 2, _ => 0 };
                SessionText.Text = _projectName;
                RunAnalysis(persistRun: false);
                FooterText.Text = "ЗАЩИЩЁННЫЙ KODO-ПРОЕКТ ОТКРЫТ · исходный результат сохранён в архиве";
            }
            else
            {
                SourceBox.Text = File.ReadAllText(dialog.FileName);
                _projectName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
                SessionText.Text = _projectName;
                FooterText.Text = $"Загружено: {System.IO.Path.GetFileName(dialog.FileName)} · {info.Length:N0} байт";
                RunAnalysis(persistRun: false);
            }
            AddHistory("Открыт файл " + System.IO.Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { ValidationText.Text = ex.Message; }
    }
    private void SaveHuffmanArchive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = CompressionEngine.Huffman(SourceBox.Text);
            var archive = HuffmanArchiveCodec.Serialize(result);
            var dialog = new SaveFileDialog
            {
                Filter = "Сжатый архив Хаффмана (*.khf)|*.khf",
                DefaultExt = ".khf",
                AddExtension = true,
                FileName = _projectName + ".khf"
            };
            if (dialog.ShowDialog() != true) return;
            File.WriteAllBytes(dialog.FileName, archive);
            FooterText.Text = $"KHF · {archive.Length:N0} байт · {archive.Length / (double)result.SourceBytes * 100:0.0}% от UTF-8";
            SessionInfo.Text = "архив Хаффмана сохранён с контрольной суммой";
            AddHistory("Экспортирован бинарный архив KHF");
        }
        catch (Exception ex) { ValidationText.Text = "Не удалось создать архив KHF: " + ex.Message; }
    }
    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Защищённый проект Kodosad (*.kodo.json)|*.kodo.json", FileName = _projectName + ".kodo.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var project = new KodoProjectDocument(_projectName, Algorithm, SourceBox.Text, _current?.Output, DateTime.Now);
            File.WriteAllText(dialog.FileName, KodoProjectCodec.Serialize(project));
            SessionInfo.Text = "защищённый проект сохранён";
            FooterText.Text = "KODO · DPAPI · " + dialog.FileName;
        }
        catch (Exception ex) { ValidationText.Text = "Не удалось сохранить проект: " + ex.Message; }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return; var d = new SaveFileDialog { Filter = "Текстовый отчёт (*.txt)|*.txt|CSV таблица кодов (*.csv)|*.csv", FileName = _projectName + "-result.txt" }; if (d.ShowDialog() != true) return; if (d.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) File.WriteAllLines(d.FileName, new[] { "symbol;frequency;code" }.Concat(_current.Codes.Select(x => $"{(int)x.Key};{_current.Frequencies[x.Key]};{x.Value}"))); else File.WriteAllText(d.FileName, $"КОДОСАД STUDIO\nАлгоритм: {_current.Algorithm}\nЭнтропия: {_current.Entropy:0.000}\nСредняя длина: {_current.AverageCodeLength:0.000}\nКоэффициент: {_current.RatioPercent:0.0}%\n\nРезультат:\n{_current.Output}"); FooterText.Text = "Экспортировано: " + d.FileName;
    }
    private void AddHistory(string message) { _history.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}"); while (_history.Count > 20) _history.RemoveAt(_history.Count - 1); HistoryList.ItemsSource = null; HistoryList.ItemsSource = _history; }
    private void SetStage(int stage)
    {
        _stage = Math.Clamp(stage, 1, 5); StageLabel.Text = $"ЭТАП {_stage} / 5";
        var dots = new[] { StageDot1, StageDot2, StageDot3, StageDot4, StageDot5 };
        for (var i = 0; i < dots.Length; i++) dots[i].Background = Brush(i < _stage ? "#15B77E" : "#263138");
    }
    private void ShowStep()
    {
        if (_current is null) return;
        if (_stage == 2) { OperationTitleText.Text = $"Частоты готовы · {_current.Frequencies.Count} символов"; OperationDescriptionText.Text = "Алфавит отсортирован по весу. Запустите пошаговое построение."; }
        else if (_stage == 3 && _stepIndex >= 0 && _stepIndex < _current.Steps.Count) { OperationTitleText.Text = $"Операция {_stepIndex + 1} из {_current.Steps.Count}"; OperationDescriptionText.Text = _current.Steps[_stepIndex]; StepsList.SelectedIndex = _stepIndex; StepsList.ScrollIntoView(StepsList.SelectedItem); }
        else if (_stage == 4) { OperationTitleText.Text = "Кодовая книга построена"; OperationDescriptionText.Text = $"Получено {_current.Codes.Count} кодов · {_current.OutputBits} бит в выходном потоке."; }
        else if (_stage == 5) { OperationTitleText.Text = "Обратный проход проверен"; OperationDescriptionText.Text = "Битовая последовательность декодирована по текущей модели."; }
    }
    private void AdvanceStep()
    {
        if (_current is null) return;
        if (_stepIndex < _current.Steps.Count - 1) { _stepIndex++; SetStage(3); }
        else { SetStage(4); _stepTimer.Stop(); AutoStepButton.Content = "АВТО"; }
        ShowStep();
    }
    private void StepNext_Click(object sender, RoutedEventArgs e) { _stepTimer.Stop(); AutoStepButton.Content = "АВТО"; AdvanceStep(); }
    private void StepBack_Click(object sender, RoutedEventArgs e)
    {
        _stepTimer.Stop(); AutoStepButton.Content = "АВТО";
        if (_stepIndex > 0) { _stepIndex--; SetStage(3); } else { _stepIndex = -1; SetStage(2); StepsList.SelectedIndex = -1; }
        ShowStep();
    }
    private void StepAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_stepTimer.IsEnabled) { _stepTimer.Stop(); AutoStepButton.Content = "АВТО"; }
        else { if (_stage >= 4) { _stepIndex = -1; SetStage(2); } _stepTimer.Start(); AutoStepButton.Content = "ПАУЗА"; AdvanceStep(); }
    }
    private void RenderFrequencyStrip(CompressionResult result)
    {
        FrequencyStripPanel.Children.Clear(); var max = Math.Max(1, result.Frequencies.Values.Max());
        foreach (var pair in result.Frequencies.OrderByDescending(x => x.Value).Take(24))
        {
            var size = 28d + 16d * pair.Value / max;
            var chip = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), BorderBrush = Brush("#15B77E"), BorderThickness = new Thickness(1), Background = Brush("#102A22"), Margin = new Thickness(0, 0, 8, 0), ToolTip = $"{Display(pair.Key)} · {pair.Value}" };
            chip.Child = new TextBlock { Text = Display(pair.Key), Foreground = Brush("#F7F9FA"), FontFamily = new FontFamily("Cascadia Mono"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            FrequencyStripPanel.Children.Add(chip);
        }
    }
    private void TreeZoomIn_Click(object sender, RoutedEventArgs e) => SetTreeZoom(TreeScale.ScaleX + .12);
    private void TreeZoomOut_Click(object sender, RoutedEventArgs e) => SetTreeZoom(TreeScale.ScaleX - .12);
    private void TreeReset_Click(object sender, RoutedEventArgs e) => SetTreeZoom(1);
    private void TreeCanvas_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e) { SetTreeZoom(TreeScale.ScaleX + (e.Delta > 0 ? .1 : -.1)); e.Handled = true; }
    private void SetTreeZoom(double value) { value = Math.Clamp(value, .7, 1.8); TreeScale.ScaleX = value; TreeScale.ScaleY = value; }
    private void DrawTree()
    {
        TreeCanvas.Children.Clear(); var tree = _current?.Tree; if (tree is null || TreeCanvas.ActualWidth < 80) { if (_current is not null) { var t = new TextBlock { Text = _current.Algorithm == "RLE" ? "RLE использует серии,\nдерево не требуется" : "Для этого метода показана\nтаблица кодов", Foreground = Brush("#93A1A9"), TextAlignment = TextAlignment.Center }; Canvas.SetLeft(t, 120); Canvas.SetTop(t, 120); TreeCanvas.Children.Add(t); } return; }
        var leaves = Math.Max(1, CountLeaves(tree)); _denseTree = leaves > 16; var index = 0; Layout(tree, 0, ref index, leaves, TreeCanvas.ActualWidth, TreeCanvas.ActualHeight); Edges(tree); Nodes(tree);
        if (_denseTree)
        {
            var hint = new TextBlock { Text = $"Компактный режим · {leaves} листьев · полные коды на вкладке «КОДЫ»", Foreground = Brush("#71808A"), FontSize = 10 };
            Canvas.SetLeft(hint, 12); Canvas.SetTop(hint, TreeCanvas.ActualHeight - 18); TreeCanvas.Children.Add(hint);
        }
    }
    private static int CountLeaves(HuffmanNode? n) => n is null ? 0 : n.Symbol is not null ? 1 : CountLeaves(n.Left) + CountLeaves(n.Right);
    private static Point Layout(HuffmanNode n, int depth, ref int index, int leaves, double width, double height)
    {
        if (n.Symbol is not null) { n.Position = new Point(24 + index++ * ((width - 48) / Math.Max(1, leaves - 1)), height - 30); return n.Position; } var a = n.Left is null ? new Point(width / 2, height - 30) : Layout(n.Left, depth + 1, ref index, leaves, width, height); var b = n.Right is null ? a : Layout(n.Right, depth + 1, ref index, leaves, width, height); n.Position = new Point((a.X + b.X) / 2, 25 + depth * Math.Min(52, (height - 70) / 7)); return n.Position;
    }
    private void Edges(HuffmanNode n) { if (n.Left is not null) { Edge(n.Position, n.Left.Position, "0"); Edges(n.Left); } if (n.Right is not null) { Edge(n.Position, n.Right.Position, "1"); Edges(n.Right); } }
    private void Edge(Point a, Point b, string bit) { TreeCanvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Brush("#334047"), StrokeThickness = 1.4 }); var t = new TextBlock { Text = bit, Foreground = Brush(bit == "0" ? "#15B77E" : "#2878FF"), FontSize = 10, FontFamily = new FontFamily("Cascadia Mono") }; Canvas.SetLeft(t, (a.X + b.X) / 2 + 3); Canvas.SetTop(t, (a.Y + b.Y) / 2 - 9); TreeCanvas.Children.Add(t); }
    private void Nodes(HuffmanNode n) { if (n.Left is not null) Nodes(n.Left); if (n.Right is not null) Nodes(n.Right); var leaf = n.Symbol is not null; var r = _denseTree && leaf ? 6.0 : leaf ? 14.0 : 10.0; var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = Brush(leaf ? (_denseTree ? "#15B77E" : "#F7F9FA") : "#171D22"), Stroke = Brush(leaf ? "#15B77E" : "#2878FF"), StrokeThickness = _denseTree && leaf ? 1 : 2 }; Canvas.SetLeft(e, n.Position.X - r); Canvas.SetTop(e, n.Position.Y - r); TreeCanvas.Children.Add(e); if (_denseTree && leaf) return; var label = new TextBlock { Text = leaf ? Display(n.Symbol!.Value) : n.Frequency.ToString(), Foreground = Brush(leaf ? "#101418" : "#F7F9FA"), FontSize = 9, FontFamily = new FontFamily("Cascadia Mono") }; label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); Canvas.SetLeft(label, n.Position.X - label.DesiredSize.Width / 2); Canvas.SetTop(label, n.Position.Y - label.DesiredSize.Height / 2); TreeCanvas.Children.Add(label); }
    private void TreeCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTree();
    private static string Display(char c) => c == ' ' ? "␠" : c == '\n' ? "↵" : c.ToString(); private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
