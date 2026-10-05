using System.Windows;

namespace KodosadStudio;

public partial class AuthWindow : Window
{
    private readonly KodosadDatabase _database;
    private bool _awaitingRegistrationCode;
    private string _challengeEmail = "";
    private string _challengeName = "";
    private string _challengeUserName = "";
    private readonly EmailCodeChallenge _challenge = new();
    public UserSession? AuthenticatedSession { get; private set; }

    public AuthWindow(KodosadDatabase database)
    {
        InitializeComponent();
        _database = database;
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginMessage.Text = "";
        try
        {
            SetBusy(true);
            AuthenticatedSession = await _database.LoginAsync(LoginEmailBox.Text, LoginPasswordBox.Password);
            DialogResult = true;
        }
        catch (Exception ex) { LoginMessage.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        RegisterMessage.Text = "";
        try
        {
            SetBusy(true);
            AccountSecurity.ValidateEmail(RegisterEmailBox.Text);
            AccountSecurity.ValidateUserName(UserNameBox.Text);
            AccountSecurity.ValidateDisplayName(DisplayNameBox.Text);
            AccountSecurity.ValidatePassword(RegisterPasswordBox.Password);
            if (!_awaitingRegistrationCode)
            {
                var mail = new MailApiClient();
                if (!mail.IsConfigured)
                {
                    new MailApiSettingsWindow { Owner = this }.ShowDialog();
                    mail = new MailApiClient();
                    if (!mail.IsConfigured) throw new InvalidOperationException("Настройте Mail API и снова нажмите «Получить код».");
                }
                _challengeEmail = RegisterEmailBox.Text.Trim(); _challengeName = DisplayNameBox.Text.Trim(); _challengeUserName = UserNameBox.Text.Trim();
                var code = _challenge.Issue();
                await mail.SendRegistrationCodeAsync(_challengeEmail, _challengeName, code);
                _awaitingRegistrationCode = true;
                VerificationField.Visibility = Visibility.Visible;
                RegisterButton.Content = "ПОДТВЕРДИТЬ ПОЧТУ И СОЗДАТЬ АККАУНТ  ↗";
                RegisterMessage.Text = "Код отправлен на " + _challengeEmail + ". Проверьте входящие и спам.";
                VerificationCodeBox.Focus();
                return;
            }
            if (!RegisterEmailBox.Text.Trim().Equals(_challengeEmail, StringComparison.OrdinalIgnoreCase) ||
                !DisplayNameBox.Text.Trim().Equals(_challengeName, StringComparison.Ordinal) ||
                !UserNameBox.Text.Trim().Equals(_challengeUserName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Почта, имя или логин изменились. Запросите новый код.");
            if (!_challenge.Verify(VerificationCodeBox.Text))
            {
                _awaitingRegistrationCode = false;
                VerificationCodeBox.Clear();
                RegisterButton.Content = "ПОЛУЧИТЬ НОВЫЙ КОД  ↗";
                throw new ArgumentException("Код неверный, исчерпаны попытки или истёк срок. Нажмите кнопку повторно, чтобы отправить новый код.");
            }
            AuthenticatedSession = await _database.RegisterVerifiedAsync(RegisterEmailBox.Text, UserNameBox.Text,
                DisplayNameBox.Text, RegisterPasswordBox.Password);
            DialogResult = true;
        }
        catch (Exception ex) { RegisterMessage.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void MailSettings_Click(object sender, RoutedEventArgs e)
    {
        new MailApiSettingsWindow { Owner = this }.ShowDialog();
        RegisterMessage.Text = new MailApiClient().IsConfigured ? "Mail API настроен для подтверждения почты." : "Mail API пока не настроен.";
    }

    private void ResetPassword_Click(object sender, RoutedEventArgs e) =>
        new PasswordResetWindow(_database, LoginEmailBox.Text) { Owner = this }.ShowDialog();

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        DatabaseState.Text = busy ? "PROCESSING" : "SECURE";
    }
}
