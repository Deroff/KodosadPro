using System.Windows;

namespace KodosadStudio;

public partial class EmailVerificationWindow : Window
{
    private readonly UserSession _session;
    private readonly KodosadDatabase _database;
    private bool _busy;

    public EmailVerificationWindow(UserSession session, KodosadDatabase database)
    {
        InitializeComponent();
        _session = session;
        _database = database;
        EmailText.Text = session.Email;
    }

    private async void Request_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        FeedbackText.Text = "";
        try
        {
            var mail = new MailApiClient();
            if (!mail.IsConfigured)
            {
                new MailApiSettingsWindow { Owner = this }.ShowDialog();
                mail = new MailApiClient();
                if (!mail.IsConfigured) throw new InvalidOperationException("Mail API не настроен. Укажите адрес и токен, затем повторите запрос.");
            }
            SetBusy(true);
            var request = await _database.BeginEmailVerificationAsync(_session, CurrentPasswordBox.Password);
            if (request is null)
            {
                FeedbackText.Text = "Эта почта уже подтверждена.";
                return;
            }
            try
            {
                await mail.SendRegistrationCodeAsync(request.Email, request.DisplayName, request.Code);
                await _database.MarkEmailVerificationDeliveredAsync(request);
            }
            catch
            {
                await _database.CancelEmailVerificationAsync(request);
                throw;
            }
            CurrentPasswordBox.Clear();
            FeedbackText.Text = "Письмо отправлено. Код действует 10 минут; повторный запрос возможен через минуту.";
        }
        catch (Exception ex) { FeedbackText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        FeedbackText.Text = "";
        try
        {
            SetBusy(true);
            await _database.CompleteEmailVerificationAsync(_session, CodeBox.Text);
            DialogResult = true;
        }
        catch (Exception ex) { FeedbackText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RequestButton.IsEnabled = !busy;
        CompleteButton.IsEnabled = !busy;
    }
}
