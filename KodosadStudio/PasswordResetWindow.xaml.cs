using System.Windows;

namespace KodosadStudio;

public partial class PasswordResetWindow : Window
{
    private readonly KodosadDatabase _database;
    private bool _busy;

    public PasswordResetWindow(KodosadDatabase database, string email)
    {
        InitializeComponent();
        _database = database;
        EmailBox.Text = email.Trim();
    }

    private async void Request_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        FeedbackText.Text = "";
        try
        {
            AccountSecurity.ValidateEmail(EmailBox.Text);
            var mail = new MailApiClient();
            if (!mail.IsConfigured)
            {
                new MailApiSettingsWindow { Owner = this }.ShowDialog();
                mail = new MailApiClient();
                if (!mail.IsConfigured) throw new InvalidOperationException("Mail API не настроен. Укажите адрес и токен, затем повторите запрос.");
            }
            SetBusy(true);
            var request = await _database.BeginPasswordResetAsync(EmailBox.Text);
            if (request is not null)
            {
                try
                {
                    await mail.SendPasswordResetCodeAsync(request.Email, request.Code);
                    await _database.MarkPasswordResetDeliveredAsync(request);
                }
                catch
                {
                    try { await _database.CancelPasswordResetAsync(request); } catch { /* Keep the public response independent of account existence. */ }
                    // A mail failure must not reveal whether this address belongs to an account.
                }
            }
            FeedbackText.Text = "Если почта подтверждена, письмо с кодом будет отправлено. Проверьте входящие и спам; при отсутствии письма проверьте Mail API. Повторный запрос возможен через минуту.";
        }
        catch (Exception ex) { FeedbackText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        FeedbackText.Text = "";
        if (PasswordBox.Password != ConfirmBox.Password)
        {
            FeedbackText.Text = "Новые пароли не совпадают.";
            return;
        }
        try
        {
            SetBusy(true);
            await _database.CompletePasswordResetAsync(EmailBox.Text, CodeBox.Text, PasswordBox.Password);
            PasswordBox.Clear();
            ConfirmBox.Clear();
            CodeBox.Clear();
            FeedbackText.Text = "Пароль обновлён. Вернитесь ко входу с новым паролем.";
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
