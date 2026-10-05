using Microsoft.Data.SqlClient;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace KodosadStudio;

public sealed class KodosadDatabase
{
    public static string DatabaseName
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("KODOSAD_DATABASE_NAME");
            if (string.IsNullOrWhiteSpace(configured)) return "KodosadStudioDb";
            if (configured.Length > 100 || configured.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                throw new InvalidOperationException("KODOSAD_DATABASE_NAME может содержать только латинские буквы, цифры и подчёркивание.");
            return configured;
        }
    }
    private static readonly SemaphoreSlim InitializeLock = new(1, 1);
    private readonly string _server;

    public KodosadDatabase()
    {
        var configured = Environment.GetEnvironmentVariable("KODOSAD_SQL_SERVER");
        _server = string.IsNullOrWhiteSpace(configured) ? @"(localdb)\MSSQLLocalDB" : configured.Trim();
    }
    private string ConnectionString(string catalog) => new SqlConnectionStringBuilder
    {
        DataSource = LocalDbPipeResolver.ResolveServer(_server),
        InitialCatalog = catalog,
        IntegratedSecurity = true,
        Encrypt = SqlConnectionEncryptOption.Optional,
        TrustServerCertificate = false,
        ConnectTimeout = 8
    }.ConnectionString;

    public async Task EnsureDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await InitializeLock.WaitAsync(cancellationToken);
        try
        {
            await using (var master = new SqlConnection(ConnectionString("master")))
            {
                await master.OpenAsync(cancellationToken);
                await using var create = new SqlCommand($"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];", master);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var connection = new SqlConnection(ConnectionString(DatabaseName));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = ReadSchema();
            command.CommandTimeout = 30;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { InitializeLock.Release(); }
    }

    public Task<UserSession> RegisterAsync(string email, string userName, string displayName, string password,
        CancellationToken cancellationToken = default) =>
        RegisterCoreAsync(email, userName, displayName, password, false, cancellationToken);

    internal Task<UserSession> RegisterVerifiedAsync(string email, string userName, string displayName, string password,
        CancellationToken cancellationToken = default) =>
        RegisterCoreAsync(email, userName, displayName, password, true, cancellationToken);

    private async Task<UserSession> RegisterCoreAsync(string email, string userName, string displayName, string password,
        bool emailVerified, CancellationToken cancellationToken)
    {
        AccountSecurity.ValidateEmail(email);
        AccountSecurity.ValidateUserName(userName);
        AccountSecurity.ValidateDisplayName(displayName);
        var (salt, hash) = AccountSecurity.CreatePasswordHash(password);
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = new SqlCommand("""
                INSERT dbo.AppUsers (Email, NormalizedEmail, UserName, NormalizedUserName, DisplayName, PasswordSalt, PasswordHash, PasswordIterations, EmailVerifiedAtUtc)
                OUTPUT INSERTED.UserId
                VALUES (@email, @normalizedEmail, @userName, @normalizedUserName, @displayName, @salt, @hash, @iterations,
                    CASE WHEN @verified=1 THEN SYSUTCDATETIME() ELSE NULL END);
                """, connection, transaction);
            command.Parameters.AddWithValue("@email", email.Trim());
            command.Parameters.AddWithValue("@normalizedEmail", AccountSecurity.NormalizeEmail(email));
            command.Parameters.AddWithValue("@userName", userName.Trim());
            command.Parameters.AddWithValue("@normalizedUserName", AccountSecurity.NormalizeUserName(userName));
            command.Parameters.AddWithValue("@displayName", displayName.Trim());
            command.Parameters.Add("@salt", System.Data.SqlDbType.VarBinary, 16).Value = salt;
            command.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, 32).Value = hash;
            command.Parameters.AddWithValue("@iterations", AccountSecurity.Iterations);
            command.Parameters.Add("@verified", System.Data.SqlDbType.Bit).Value = emailVerified;
            var userId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            await using var settings = new SqlCommand("INSERT dbo.UserSettings (UserId, PreferredAlgorithmId) VALUES (@userId, 1);", connection, transaction);
            settings.Parameters.AddWithValue("@userId", userId);
            await settings.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            CryptographicOperations.ZeroMemory(hash);
            return new UserSession(userId, email.Trim(), userName.Trim(), displayName.Trim());
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new AccountException("Почта или имя пользователя уже заняты.");
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    public async Task<UserSession> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        AccountSecurity.ValidateEmail(email);
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT UserId, Email, UserName, DisplayName, PasswordSalt, PasswordHash, PasswordIterations, FailedAttempts, LockoutUntilUtc
            FROM dbo.AppUsers WITH (UPDLOCK, ROWLOCK) WHERE NormalizedEmail = @email;
            """, connection, transaction);
        command.Parameters.AddWithValue("@email", AccountSecurity.NormalizeEmail(email));
        UserSession? session = null;
        byte[]? salt = null;
        byte[]? hash = null;
        var iterations = 0;
        var failedAttempts = 0;
        DateTime? lockoutUntil = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                session = new UserSession(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
                salt = (byte[])reader[4]; hash = (byte[])reader[5]; iterations = reader.GetInt32(6);
                failedAttempts = reader.GetByte(7); lockoutUntil = reader.IsDBNull(8) ? null : reader.GetDateTime(8);
            }
        }

        if (session is null || salt is null || hash is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new AccountException("Неверная почта или пароль.");
        }
        var now = DateTime.UtcNow;
        if (lockoutUntil is not null && lockoutUntil > now)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new AccountException("Вход временно заблокирован после нескольких ошибок. Повторите попытку позже.");
        }

        if (!AccountSecurity.VerifyPassword(password, salt, hash, iterations))
        {
            failedAttempts++;
            var lockUntil = failedAttempts >= 5 ? now.AddMinutes(15) : (DateTime?)null;
            await using var failed = new SqlCommand("UPDATE dbo.AppUsers SET FailedAttempts=@attempts, LockoutUntilUtc=@lockout WHERE UserId=@userId;", connection, transaction);
            failed.Parameters.AddWithValue("@attempts", Math.Min(failedAttempts, 5));
            failed.Parameters.AddWithValue("@lockout", (object?)lockUntil ?? DBNull.Value);
            failed.Parameters.AddWithValue("@userId", session.UserId);
            await failed.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new AccountException("Неверная почта или пароль.");
        }

        await using (var success = new SqlCommand("UPDATE dbo.AppUsers SET FailedAttempts=0, LockoutUntilUtc=NULL, LastLoginAtUtc=SYSUTCDATETIME() WHERE UserId=@userId;", connection, transaction))
        {
            success.Parameters.AddWithValue("@userId", session.UserId);
            await success.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        CryptographicOperations.ZeroMemory(hash);
        return session;
    }

    public async Task ChangePasswordAsync(UserSession session, string currentPassword, string newPassword,
        CancellationToken cancellationToken = default)
    {
        AccountSecurity.ValidatePassword(newPassword);
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT PasswordSalt, PasswordHash, PasswordIterations FROM dbo.AppUsers WHERE UserId=@userId;", connection);
        command.Parameters.AddWithValue("@userId", session.UserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new AccountException("Учётная запись не найдена.");
        var salt = (byte[])reader[0]; var hash = (byte[])reader[1]; var iterations = reader.GetInt32(2);
        await reader.CloseAsync();
        if (!AccountSecurity.VerifyPassword(currentPassword, salt, hash, iterations)) throw new AccountException("Текущий пароль указан неверно.");
        var (newSalt, newHash) = AccountSecurity.CreatePasswordHash(newPassword);
        await using var update = new SqlCommand("UPDATE dbo.AppUsers SET PasswordSalt=@salt, PasswordHash=@hash, PasswordIterations=@iterations, FailedAttempts=0, LockoutUntilUtc=NULL WHERE UserId=@userId;", connection);
        update.Parameters.Add("@salt", System.Data.SqlDbType.VarBinary, 16).Value = newSalt;
        update.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, 32).Value = newHash;
        update.Parameters.AddWithValue("@iterations", AccountSecurity.Iterations);
        update.Parameters.AddWithValue("@userId", session.UserId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        CryptographicOperations.ZeroMemory(hash);
        CryptographicOperations.ZeroMemory(newHash);
    }

    public async Task<bool> IsEmailVerifiedAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT EmailVerifiedAtUtc FROM dbo.AppUsers WHERE UserId=@userId;", connection);
        command.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = session.UserId;
        return await command.ExecuteScalarAsync(cancellationToken) is DateTime;
    }

    public async Task<EmailVerificationRequest?> BeginEmailVerificationAsync(UserSession session, string currentPassword,
        CancellationToken cancellationToken = default)
    {
        var authenticated = await LoginAsync(session.Email, currentPassword, cancellationToken);
        if (authenticated.UserId != session.UserId) throw new AccountException("Учётная запись не найдена.");
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        await using (var status = new SqlCommand("SELECT EmailVerifiedAtUtc FROM dbo.AppUsers WITH (UPDLOCK,HOLDLOCK) WHERE UserId=@userId;", connection, transaction))
        {
            status.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = session.UserId;
            if (await status.ExecuteScalarAsync(cancellationToken) is DateTime)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }
        await using (var cooldown = new SqlCommand("SELECT IssuedUtc FROM dbo.EmailVerificationChallenges WITH (UPDLOCK,HOLDLOCK) WHERE UserId=@userId;", connection, transaction))
        {
            cooldown.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = session.UserId;
            if (await cooldown.ExecuteScalarAsync(cancellationToken) is DateTime issued && issued > DateTime.UtcNow.AddMinutes(-1))
                throw new AccountException("Повторный запрос кода возможен через минуту.");
        }
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var codeHash = HashResetCode(code);
        try
        {
            await using var save = new SqlCommand("""
                DECLARE @now datetime2(0) = SYSUTCDATETIME();
                UPDATE dbo.EmailVerificationChallenges SET CodeHash=@hash,IssuedUtc=@now,ExpiresUtc=DATEADD(minute,10,@now),DeliveredUtc=NULL,FailedAttempts=0 WHERE UserId=@userId;
                IF @@ROWCOUNT=0 INSERT dbo.EmailVerificationChallenges(UserId,CodeHash,IssuedUtc,ExpiresUtc) VALUES(@userId,@hash,@now,DATEADD(minute,10,@now));
                """, connection, transaction);
            save.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = session.UserId;
            save.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, 32).Value = codeHash;
            await save.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new EmailVerificationRequest(session.UserId, session.Email, session.DisplayName, code);
        }
        finally { CryptographicOperations.ZeroMemory(codeHash); }
    }

    public async Task MarkEmailVerificationDeliveredAsync(EmailVerificationRequest request, CancellationToken cancellationToken = default)
    {
        var codeHash = HashResetCode(request.Code);
        try
        {
            await using var connection = new SqlConnection(ConnectionString(DatabaseName));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("UPDATE dbo.EmailVerificationChallenges SET DeliveredUtc=SYSUTCDATETIME() WHERE UserId=@userId AND CodeHash=@hash AND DeliveredUtc IS NULL AND ExpiresUtc>SYSUTCDATETIME();", connection);
            command.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = request.UserId;
            command.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, 32).Value = codeHash;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new AccountException("Код подтверждения больше не действует. Запросите новый.");
        }
        finally { CryptographicOperations.ZeroMemory(codeHash); }
    }

    public async Task CancelEmailVerificationAsync(EmailVerificationRequest request, CancellationToken cancellationToken = default)
    {
        var codeHash = HashResetCode(request.Code);
        try
        {
            await using var connection = new SqlConnection(ConnectionString(DatabaseName));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("DELETE FROM dbo.EmailVerificationChallenges WHERE UserId=@userId AND CodeHash=@hash AND DeliveredUtc IS NULL;", connection);
            command.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = request.UserId;
            command.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, 32).Value = codeHash;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(codeHash); }
    }

    public async Task CompleteEmailVerificationAsync(UserSession session, string code, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        byte[] expectedHash;
        DateTime expiresUtc;
        DateTime? deliveredUtc;
        byte failedAttempts;
        await using (var find = new SqlCommand("SELECT CodeHash,ExpiresUtc,DeliveredUtc,FailedAttempts FROM dbo.EmailVerificationChallenges WITH (UPDLOCK,HOLDLOCK) WHERE UserId=@userId;", connection, transaction))
        {
            find.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = session.UserId;
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new AccountException("Код подтверждения неверен или истёк.");
            expectedHash = (byte[])reader[0];
            expiresUtc = reader.GetDateTime(1);
            deliveredUtc = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
            failedAttempts = reader.GetByte(3);
        }
        if (deliveredUtc is null || expiresUtc <= DateTime.UtcNow || failedAttempts >= 5)
            throw new AccountException("Код подтверждения неверен или истёк.");
        var formatted = !string.IsNullOrWhiteSpace(code) && System.Text.RegularExpressions.Regex.IsMatch(code.Trim(), "^[0-9]{6}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var suppliedHash = formatted ? HashResetCode(code) : new byte[32];
        var valid = CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash) && formatted;
        CryptographicOperations.ZeroMemory(suppliedHash);
        CryptographicOperations.ZeroMemory(expectedHash);
        if (!valid)
        {
            await using var failure = new SqlCommand("UPDATE dbo.EmailVerificationChallenges SET FailedAttempts=FailedAttempts+1 WHERE UserId=@userId AND FailedAttempts<5;", connection, transaction);
            failure.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = session.UserId;
            await failure.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new AccountException("Код подтверждения неверен или истёк.");
        }
        await using var confirm = new SqlCommand("UPDATE dbo.AppUsers SET EmailVerifiedAtUtc=SYSUTCDATETIME() WHERE UserId=@userId AND EmailVerifiedAtUtc IS NULL; DELETE FROM dbo.EmailVerificationChallenges WHERE UserId=@userId;", connection, transaction);
        confirm.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = session.UserId;
        await confirm.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PasswordResetRequest?> BeginPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        AccountSecurity.ValidateEmail(email);
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        int userId;
        string actualEmail;
        await using (var find = new SqlCommand("SELECT UserId,Email FROM dbo.AppUsers WITH (UPDLOCK,HOLDLOCK) WHERE NormalizedEmail=@email AND EmailVerifiedAtUtc IS NOT NULL;", connection, transaction))
        {
            find.Parameters.Add("@email", System.Data.SqlDbType.NVarChar, 254).Value = AccountSecurity.NormalizeEmail(email);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            userId = reader.GetInt32(0);
            actualEmail = reader.GetString(1);
        }
        await using (var cooldown = new SqlCommand("SELECT IssuedUtc FROM dbo.PasswordResetChallenges WITH (UPDLOCK,HOLDLOCK) WHERE UserId=@userId;", connection, transaction))
        {
            cooldown.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = userId;
            if (await cooldown.ExecuteScalarAsync(cancellationToken) is DateTime issued && issued > DateTime.UtcNow.AddMinutes(-1))
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var codeHash = HashResetCode(code);
        try
        {
            await using var save = new SqlCommand("""
                DECLARE @now datetime2(0) = SYSUTCDATETIME();
                UPDATE dbo.PasswordResetChallenges SET CodeHash=@hash,IssuedUtc=@now,ExpiresUtc=DATEADD(minute,10,@now),DeliveredUtc=NULL,FailedAttempts=0 WHERE UserId=@userId;
                IF @@ROWCOUNT=0 INSERT dbo.PasswordResetChallenges(UserId,CodeHash,IssuedUtc,ExpiresUtc) VALUES(@userId,@hash,@now,DATEADD(minute,10,@now));
                """, connection, transaction);
            save.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = userId;
            save.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, 32).Value = codeHash;
            await save.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PasswordResetRequest(userId, actualEmail, code);
        }
        finally { CryptographicOperations.ZeroMemory(codeHash); }
    }

    public async Task MarkPasswordResetDeliveredAsync(PasswordResetRequest request, CancellationToken cancellationToken = default)
    {
        var codeHash = HashResetCode(request.Code);
        try
        {
            await using var connection = new SqlConnection(ConnectionString(DatabaseName));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("UPDATE dbo.PasswordResetChallenges SET DeliveredUtc=SYSUTCDATETIME() WHERE UserId=@userId AND CodeHash=@hash AND DeliveredUtc IS NULL AND ExpiresUtc>SYSUTCDATETIME();", connection);
            command.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = request.UserId;
            command.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, 32).Value = codeHash;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new AccountException("Код восстановления больше не действует. Запросите новый.");
        }
        finally { CryptographicOperations.ZeroMemory(codeHash); }
    }

    public async Task CancelPasswordResetAsync(PasswordResetRequest request, CancellationToken cancellationToken = default)
    {
        var codeHash = HashResetCode(request.Code);
        try
        {
            await using var connection = new SqlConnection(ConnectionString(DatabaseName));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("DELETE FROM dbo.PasswordResetChallenges WHERE UserId=@userId AND CodeHash=@hash AND DeliveredUtc IS NULL;", connection);
            command.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = request.UserId;
            command.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, 32).Value = codeHash;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(codeHash); }
    }

    public async Task CompletePasswordResetAsync(string email, string code, string newPassword, CancellationToken cancellationToken = default)
    {
        AccountSecurity.ValidateEmail(email);
        AccountSecurity.ValidatePassword(newPassword);
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        int userId;
        byte[] expectedHash;
        DateTime expiresUtc;
        DateTime? deliveredUtc;
        byte failedAttempts;
        await using (var find = new SqlCommand("""
            SELECT c.UserId,c.CodeHash,c.ExpiresUtc,c.DeliveredUtc,c.FailedAttempts
            FROM dbo.PasswordResetChallenges c WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.AppUsers u ON u.UserId=c.UserId WHERE u.NormalizedEmail=@email AND u.EmailVerifiedAtUtc IS NOT NULL;
            """, connection, transaction))
        {
            find.Parameters.Add("@email", System.Data.SqlDbType.NVarChar, 254).Value = AccountSecurity.NormalizeEmail(email);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new AccountException("Код восстановления неверен или истёк.");
            userId = reader.GetInt32(0);
            expectedHash = (byte[])reader[1];
            expiresUtc = reader.GetDateTime(2);
            deliveredUtc = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
            failedAttempts = reader.GetByte(4);
        }
        if (deliveredUtc is null || expiresUtc <= DateTime.UtcNow || failedAttempts >= 5)
            throw new AccountException("Код восстановления неверен или истёк.");
        var formatted = !string.IsNullOrWhiteSpace(code) && System.Text.RegularExpressions.Regex.IsMatch(code.Trim(), "^[0-9A-Fa-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var suppliedHash = formatted ? HashResetCode(code) : new byte[32];
        var valid = CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash) && formatted;
        CryptographicOperations.ZeroMemory(suppliedHash);
        CryptographicOperations.ZeroMemory(expectedHash);
        if (!valid)
        {
            await using var failure = new SqlCommand("UPDATE dbo.PasswordResetChallenges SET FailedAttempts=FailedAttempts+1 WHERE UserId=@userId AND FailedAttempts<5;", connection, transaction);
            failure.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = userId;
            await failure.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new AccountException("Код восстановления неверен или истёк.");
        }
        var (salt, passwordHash) = AccountSecurity.CreatePasswordHash(newPassword);
        try
        {
            await using var update = new SqlCommand("""
                UPDATE dbo.AppUsers SET PasswordSalt=@salt,PasswordHash=@passwordHash,PasswordIterations=@iterations,FailedAttempts=0,LockoutUntilUtc=NULL WHERE UserId=@userId;
                DELETE FROM dbo.PasswordResetChallenges WHERE UserId=@userId;
                """, connection, transaction);
            update.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = userId;
            update.Parameters.Add("@salt", System.Data.SqlDbType.VarBinary, 16).Value = salt;
            update.Parameters.Add("@passwordHash", System.Data.SqlDbType.VarBinary, 32).Value = passwordHash;
            update.Parameters.Add("@iterations", System.Data.SqlDbType.Int).Value = AccountSecurity.Iterations;
            await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(passwordHash); }
    }

    private static byte[] HashResetCode(string code)
    {
        var bytes = Encoding.ASCII.GetBytes(code.Trim().ToUpperInvariant());
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task SaveDraftAsync(UserSession session, string name, string algorithm, string source,
        CancellationToken cancellationToken = default)
    {
        var protectedSource = DataProtection.Protect(source);
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            DECLARE @algorithmId tinyint = (SELECT AlgorithmId FROM dbo.AlgorithmCatalog WHERE DisplayName=@algorithm);
            IF @algorithmId IS NULL THROW 51000, 'Unknown algorithm.', 1;
            UPDATE dbo.Projects SET AlgorithmId=@algorithmId, SourceProtected=@source, UpdatedAtUtc=SYSUTCDATETIME()
              WHERE OwnerUserId=@userId AND ProjectName=@name;
            IF @@ROWCOUNT=0 INSERT dbo.Projects (OwnerUserId, ProjectName, AlgorithmId, SourceProtected)
              VALUES (@userId, @name, @algorithmId, @source);
            """, connection);
        command.Parameters.AddWithValue("@algorithm", algorithm);
        command.Parameters.Add("@source", System.Data.SqlDbType.VarBinary, -1).Value = protectedSource;
        command.Parameters.AddWithValue("@userId", session.UserId);
        command.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(name) ? "Новый проект" : name.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
        CryptographicOperations.ZeroMemory(protectedSource);
    }

    public async Task<IReadOnlyList<SavedProject>> GetProjectsAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT p.ProjectId, p.ProjectName, a.DisplayName, p.SourceProtected, p.UpdatedAtUtc
            FROM dbo.Projects p JOIN dbo.AlgorithmCatalog a ON a.AlgorithmId=p.AlgorithmId
            WHERE p.OwnerUserId=@userId ORDER BY p.UpdatedAtUtc DESC;
            """, connection);
        command.Parameters.AddWithValue("@userId", session.UserId);
        var projects = new List<SavedProject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            projects.Add(new SavedProject(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                DataProtection.Unprotect((byte[])reader[3]), reader.GetDateTime(4)));
        return projects;
    }

    public async Task SaveRunAsync(UserSession session, string name, string source, CompressionResult result,
        CancellationToken cancellationToken = default)
    {
        // Persist the current editor value with the run; never replace a draft with an empty source.
        await SaveDraftAsync(session, name, result.Algorithm, source, cancellationToken);
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var getProject = new SqlCommand("SELECT ProjectId FROM dbo.Projects WHERE OwnerUserId=@userId AND ProjectName=@name;", connection, transaction);
        getProject.Parameters.AddWithValue("@userId", session.UserId);
        getProject.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(name) ? "Новый проект" : name.Trim());
        var projectId = Convert.ToInt32(await getProject.ExecuteScalarAsync(cancellationToken));
        await using var insert = new SqlCommand("""
            INSERT dbo.AnalysisRuns (ProjectId, AlgorithmId, SourceCharacters, SourceBytes, OutputBits, Entropy, AverageCodeLength, RatioPercent, SourceProtected, OutputProtected)
            OUTPUT INSERTED.RunId
            SELECT @projectId, AlgorithmId, @chars, @bytes, @bits, @entropy, @average, @ratio, @source, @output
              FROM dbo.AlgorithmCatalog WHERE DisplayName=@algorithm;
            """, connection, transaction);
        insert.Parameters.AddWithValue("@projectId", projectId);
        insert.Parameters.AddWithValue("@algorithm", result.Algorithm);
        insert.Parameters.AddWithValue("@chars", result.SourceCharacters);
        insert.Parameters.AddWithValue("@bytes", result.SourceBytes);
        insert.Parameters.AddWithValue("@bits", result.OutputBits);
        insert.Parameters.AddWithValue("@entropy", result.Entropy);
        insert.Parameters.AddWithValue("@average", result.AverageCodeLength);
        insert.Parameters.AddWithValue("@ratio", result.RatioPercent);
        var protectedSource = DataProtection.Protect(source);
        var protectedOutput = ProtectedData.Protect(Encoding.UTF8.GetBytes(result.Output), Encoding.UTF8.GetBytes("KodosadStudio|run-output|v1"), DataProtectionScope.CurrentUser);
        insert.Parameters.Add("@source", System.Data.SqlDbType.VarBinary, -1).Value = protectedSource;
        insert.Parameters.Add("@output", System.Data.SqlDbType.VarBinary, -1).Value = protectedOutput;
        var runId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
        foreach (var pair in result.Codes)
        {
            await using var symbol = new SqlCommand("INSERT dbo.RunSymbols (RunId, SymbolValue, Frequency, Codeword) VALUES (@runId,@symbol,@frequency,@code);", connection, transaction);
            symbol.Parameters.AddWithValue("@runId", runId); symbol.Parameters.AddWithValue("@symbol", (int)pair.Key);
            symbol.Parameters.AddWithValue("@frequency", result.Frequencies[pair.Key]); symbol.Parameters.AddWithValue("@code", pair.Value);
            await symbol.ExecuteNonQueryAsync(cancellationToken);
        }
        for (var i = 0; i < result.Steps.Count; i++)
        {
            await using var step = new SqlCommand("INSERT dbo.RunSteps (RunId, StepNumber, Description) VALUES (@runId,@number,@description);", connection, transaction);
            step.Parameters.AddWithValue("@runId", runId); step.Parameters.AddWithValue("@number", i + 1); step.Parameters.AddWithValue("@description", result.Steps[i]);
            await step.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        CryptographicOperations.ZeroMemory(protectedSource);
        CryptographicOperations.ZeroMemory(protectedOutput);
    }

    public async Task<IReadOnlyList<SavedAnalysisRun>> GetAnalysisHistoryAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT r.RunId, p.ProjectName, a.DisplayName, r.SourceCharacters, r.SourceBytes, r.OutputBits,
                   r.Entropy, r.AverageCodeLength, r.RatioPercent, r.SourceProtected, r.OutputProtected, r.CreatedAtUtc
            FROM dbo.AnalysisRuns r
            JOIN dbo.Projects p ON p.ProjectId = r.ProjectId
            JOIN dbo.AppUsers u ON u.UserId = p.OwnerUserId
            JOIN dbo.AlgorithmCatalog a ON a.AlgorithmId = r.AlgorithmId
            WHERE u.UserId = @userId
            ORDER BY r.CreatedAtUtc DESC, r.RunId DESC;
            """, connection);
        command.Parameters.AddWithValue("@userId", session.UserId);
        var runs = new List<SavedAnalysisRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var source = reader.IsDBNull(9) ? string.Empty : DataProtection.Unprotect((byte[])reader[9]);
            var output = Encoding.UTF8.GetString(ProtectedData.Unprotect((byte[])reader[10],
                Encoding.UTF8.GetBytes("KodosadStudio|run-output|v1"), DataProtectionScope.CurrentUser));
            runs.Add(new SavedAnalysisRun(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetDouble(6),
                reader.GetDouble(7), reader.GetDouble(8), source, output, reader.GetDateTime(11)));
        }
        return runs;
    }

    public async Task<int> GetRunCountAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.AnalysisRuns r JOIN dbo.Projects p ON p.ProjectId=r.ProjectId WHERE p.OwnerUserId=@userId;", connection);
        command.Parameters.AddWithValue("@userId", session.UserId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string ReadSchema()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(x => x.EndsWith("001_core.sql", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Database schema resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
