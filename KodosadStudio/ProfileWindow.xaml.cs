using System.Windows;

namespace KodosadStudio;

public partial class ProfileWindow : Window
{
    private readonly KodosadDatabase _database;
    private readonly UserSession _session;

    public ProfileWindow(UserSession session, KodosadDatabase database)
    {
        InitializeComponent();
        _session = session;
        _database = database;
        DisplayNameText.Text = session.DisplayName;
        EmailText.Text = session.Email;
        UserNameText.Text = "@" + session.UserName;
        Loaded += async (_, _) => await RefreshEmailStatusAsync();
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        FeedbackText.Text = "";
        try
        {
            await _database.ChangePasswordAsync(_session, CurrentPasswordBox.Password, NewPasswordBox.Password);
            CurrentPasswordBox.Clear();
            NewPasswordBox.Clear();
            FeedbackText.Text = "Пароль успешно обновлён.";
        }
        catch (Exception ex) { FeedbackText.Text = ex.Message; }
    }

    private async void VerifyEmail_Click(object sender, RoutedEventArgs e)
    {
        new EmailVerificationWindow(_session, _database) { Owner = this }.ShowDialog();
        await RefreshEmailStatusAsync();
    }

    private async Task RefreshEmailStatusAsync()
    {
        try
        {
            var verified = await _database.IsEmailVerifiedAsync(_session);
            EmailStatusText.Text = verified ? "Почта подтверждена · восстановление доступно" : "Почта ещё не подтверждена";
            VerifyEmailButton.Visibility = verified ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex) { EmailStatusText.Text = ex.Message; }
    }
}
