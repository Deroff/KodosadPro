using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KodosadStudio;

namespace KodosadStudio.VisualTests;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var outputPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine("outputs", "qa", "kodosad-2026-09-25", "history-current.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new AnalysisHistoryWindow(
        [
            new SavedAnalysisRun(2048, "Хаффман · пример проекта", "Хаффман", 64, 93, 142, 3.218, 2.218, 38.2,
                "Архив хранит точный исходный текст для выбранного запуска.", "0010110101", DateTime.UtcNow),
            new SavedAnalysisRun(2047, "Сжатие заметки", "Шеннон–Фано", 88, 137, 190, 3.041, 2.159, 41.6,
                "Вторая запись для проверки полосатых строк таблицы.", "0110011010", DateTime.UtcNow.AddMinutes(-12))
        ]);
        app.MainWindow = window;
        window.Show();
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            window.UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            using var stream = File.Create(outputPath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            Console.WriteLine($"Saved archive-window visual to {outputPath}");
            window.Close();
        });
        app.Run();
    }
}
