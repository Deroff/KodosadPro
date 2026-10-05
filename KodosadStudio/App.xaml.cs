using System.Configuration;
using System.Data;
using System.Windows;

namespace KodosadStudio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The modal auth window is the only window during startup. Prevent WPF's
        // default OnLastWindowClose behavior from shutting the process down
        // between a successful login and showing the main workspace.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var database = new KodosadDatabase();
            await database.EnsureDatabaseAsync();
            var auth = new AuthWindow(database);
            if (auth.ShowDialog() == true && auth.AuthenticatedSession is { } session)
            {
                var main = new MainWindow(session, database);
                MainWindow = main;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                main.Show();
            }
            else Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось подключиться к базе приложения. Проверьте, что установлен SQL Server LocalDB.\n\n" + ex.Message,
                "Kodosad Studio · подключение", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }
}

