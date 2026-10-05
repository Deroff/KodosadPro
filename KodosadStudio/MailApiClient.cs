using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace KodosadStudio;

public sealed record MailApiSettings(string BaseUrl = "", string Token = "");

public static class MailApiSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KodosadStudio.MailApi.v1");
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KodosadStudio", "mail-api.dat");
    public static MailApiSettings Load()
    {
        var envUrl = Environment.GetEnvironmentVariable("KODOSAD_MAIL_API_URL");
        var envToken = Environment.GetEnvironmentVariable("KODOSAD_MAIL_API_TOKEN");
        var saved = Read();
        var shared = ReadRouteStudioSettings();
        return new(string.IsNullOrWhiteSpace(envUrl) ? (string.IsNullOrWhiteSpace(saved.BaseUrl) ? shared.BaseUrl : saved.BaseUrl) : envUrl,
            string.IsNullOrWhiteSpace(envToken) ? (string.IsNullOrWhiteSpace(saved.Token) ? shared.Token : saved.Token) : envToken);
    }
    private static MailApiSettings ReadRouteStudioSettings()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouteStudio"); string url = "", token = "";
        try
        {
            var file = Path.Combine(folder, "provider-credentials.dat");
            if (File.Exists(file))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Encoding.UTF8.GetBytes("RouteStudio.ProviderCredentials.v1"), DataProtectionScope.CurrentUser);
                try { using var json = JsonDocument.Parse(plain); var root = json.RootElement;
                    if (root.TryGetProperty("MailApiBaseUrl", out var value)) url = value.GetString() ?? "";
                    if (root.TryGetProperty("MailApiToken", out value)) token = value.GetString() ?? "";
                } finally { CryptographicOperations.ZeroMemory(plain); }
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                file = Path.Combine(folder, "mail-api-token.dat");
                if (File.Exists(file))
                {
                    var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Encoding.UTF8.GetBytes("RouteStudio.MailApi.LocalToken.v1"), DataProtectionScope.CurrentUser);
                    try { token = Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
                }
            }
            if (!string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(url)) url = "http://127.0.0.1:5080";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return new(); }
        return new(url, token);
    }
    private static MailApiSettings Read()
    {
        try
        {
            var protectedBytes = File.ReadAllBytes(PathName);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<MailApiSettings>(bytes) ?? new(); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return new(); }
    }
    public static void Save(MailApiSettings settings)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            var temp = PathName + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, PathName, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public sealed class MailApiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly MailApiSettings _settings;
    private readonly HttpClient _http;
    public MailApiClient(MailApiSettings? settings = null, HttpClient? http = null)
    {
        _settings = settings ?? MailApiSettingsStore.Load();
        _http = http ?? Http;
    }
    public bool IsConfigured => Uri.TryCreate(_settings.BaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && _settings.Token.Trim().Length >= 24;

    public async Task SendRegistrationCodeAsync(string email, string displayName, string code)
    {
        if (!IsConfigured) throw new InvalidOperationException("Почта не настроена. Укажите HTTPS-адрес Mail API и токен в настройках почты.");
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.BaseUrl.TrimEnd('/') + "/api/mail/account-code")
        {
            Content = JsonContent.Create(new { appName = "Kodosad Studio", email, displayName, purpose = "registration", code })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
        using var response = await _http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            using var legacy = new HttpRequestMessage(HttpMethod.Post, _settings.BaseUrl.TrimEnd('/') + "/api/mail/registration-code")
            { Content = JsonContent.Create(new { email, displayName, code }) };
            legacy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
            using var legacyResponse = await _http.SendAsync(legacy);
            if (legacyResponse.IsSuccessStatusCode) return;
            var legacyError = await Error(legacyResponse);
            throw new InvalidOperationException(legacyError ?? "Сервер почты устарел или не смог отправить код (HTTP " + (int)legacyResponse.StatusCode + ").");
        }
        if (response.IsSuccessStatusCode) return;
        var detail = await Error(response);
        throw new InvalidOperationException(detail ?? response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Mail API отклонил токен. Проверьте настройки доступа.",
            System.Net.HttpStatusCode.TooManyRequests => "Слишком много запросов. Подождите минуту и попробуйте снова.",
            _ => "Почтовый сервер не смог отправить письмо (HTTP " + (int)response.StatusCode + ")."
        });
    }

    public async Task SendPasswordResetCodeAsync(string email, string code)
    {
        if (!IsConfigured) throw new InvalidOperationException("Почта не настроена. Укажите HTTPS-адрес Mail API и токен в настройках почты.");
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.BaseUrl.TrimEnd('/') + "/api/mail/account-code")
        {
            Content = JsonContent.Create(new { appName = "Kodosad Studio", email, purpose = "password-reset", code })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
        using var response = await _http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            using var legacy = new HttpRequestMessage(HttpMethod.Post, _settings.BaseUrl.TrimEnd('/') + "/api/mail/password-reset")
            { Content = JsonContent.Create(new { email, code }) };
            legacy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
            using var legacyResponse = await _http.SendAsync(legacy);
            if (legacyResponse.IsSuccessStatusCode) return;
            throw new InvalidOperationException(await Error(legacyResponse) ?? "Почтовый сервер не поддерживает восстановление пароля (HTTP " + (int)legacyResponse.StatusCode + ").");
        }
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException(await Error(response) ?? response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Mail API отклонил токен. Проверьте настройки доступа.",
            System.Net.HttpStatusCode.TooManyRequests => "Слишком много запросов. Подождите минуту и попробуйте снова.",
            _ => "Почтовый сервер не смог отправить код восстановления (HTTP " + (int)response.StatusCode + ")."
        });
    }

    private static async Task<string?> Error(HttpResponseMessage response)
    {
        try { using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync()); return json.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null; }
        catch (JsonException) { return null; }
    }
}

public sealed class EmailCodeChallenge
{
    private readonly byte[] _hash = SHA256.HashData(RandomNumberGenerator.GetBytes(32));
    private DateTime _expiresUtc;
    private int _failedAttempts;
    public string Issue()
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var bytes = Encoding.UTF8.GetBytes(code);
        var hash = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        CryptographicOperations.ZeroMemory(_hash);
        hash.CopyTo(_hash, 0);
        _expiresUtc = DateTime.UtcNow.AddMinutes(10);
        _failedAttempts = 0;
        return code;
    }
    public bool Verify(string code)
    {
        if (DateTime.UtcNow > _expiresUtc || _failedAttempts >= 5 || code.Trim().Length != 6 || !code.Trim().All(char.IsAsciiDigit)) return false;
        var bytes = Encoding.UTF8.GetBytes(code.Trim());
        var candidate = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        var valid = CryptographicOperations.FixedTimeEquals(_hash, candidate);
        CryptographicOperations.ZeroMemory(candidate);
        if (valid) CryptographicOperations.ZeroMemory(_hash); else if (++_failedAttempts >= 5) CryptographicOperations.ZeroMemory(_hash);
        return valid;
    }
}

public sealed class MailApiSettingsWindow : Window
{
    private readonly TextBox _url = new() { MinWidth = 390, Margin = new Thickness(0, 4, 0, 14), Padding = new Thickness(9) };
    private readonly PasswordBox _token = new() { MinWidth = 390, Margin = new Thickness(0, 4, 0, 14), Padding = new Thickness(9) };
    public MailApiSettingsWindow()
    {
        Title = "Kodosad Studio · почтовый API"; Width = 520; Height = 340; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White; Foreground = System.Windows.Media.Brushes.Black;
        var settings = MailApiSettingsStore.Load(); _url.Text = settings.BaseUrl; _token.Password = settings.Token;
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = "Настройка отправки почты", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock { Text = "Адрес API (HTTPS; HTTP разрешён только для localhost). Секрет SMTP хранится на сервере, токен API шифруется DPAPI для текущего пользователя Windows.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        stack.Children.Add(new TextBlock { Text = "АДРЕС MAIL API" }); stack.Children.Add(_url);
        stack.Children.Add(new TextBlock { Text = "ТОКЕН ДОСТУПА" }); stack.Children.Add(_token);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Сохранить", Padding = new Thickness(16, 8, 16, 8), IsDefault = true };
        save.Click += (_, _) =>
        {
            if (!Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || _token.Password.Trim().Length < 24)
            { MessageBox.Show(this, "Введите HTTPS/локальный HTTP-адрес API без логина и query-параметров и токен не короче 24 символов.", "Почта не настроена", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            MailApiSettingsStore.Save(new(_url.Text.Trim().TrimEnd('/'), _token.Password.Trim())); DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); stack.Children.Add(buttons); Content = stack;
    }
}
