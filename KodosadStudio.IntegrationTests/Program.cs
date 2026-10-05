using KodosadStudio;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Security.Cryptography;

if (args.Contains("--initialize-production", StringComparer.Ordinal))
{
    Environment.SetEnvironmentVariable("KODOSAD_DATABASE_NAME", "KodosadStudioDb");
    var production = new KodosadDatabase();
    await production.EnsureDatabaseAsync();
    Console.WriteLine($"PRODUCTION_DB_READY: {Environment.GetEnvironmentVariable("KODOSAD_SQL_SERVER") ?? @"(localdb)\MSSQLLocalDB"}/KodosadStudioDb");
    return 0;
}

var originalDatabaseName = Environment.GetEnvironmentVariable("KODOSAD_DATABASE_NAME");
var ownsTestDatabase = string.IsNullOrWhiteSpace(originalDatabaseName);
if (ownsTestDatabase)
    Environment.SetEnvironmentVariable("KODOSAD_DATABASE_NAME", "KodosadStudio_IT_" + Guid.NewGuid().ToString("N"));

var stamp = Guid.NewGuid().ToString("N")[..12];
var emailA = $"kodo-it-{stamp}@example.invalid";
var emailB = $"kodo-it-b-{stamp}@example.invalid";
var userA = $"kodo_it_{stamp}";
var userB = $"kodo_it_b_{stamp}";
UserSession? sessionA = null;
UserSession? sessionB = null;
var passed = 0;

void Check(string name, bool condition)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

var database = new KodosadDatabase();
try
{
    await database.EnsureDatabaseAsync();
    if (ownsTestDatabase)
    {
        await ExecuteAsync("""
            BEGIN TRANSACTION;
            DELETE FROM dbo.SchemaVersions WHERE VersionNumber IN (2,3);
            DROP TABLE dbo.PasswordResetChallenges;
            DROP TABLE dbo.EmailVerificationChallenges;
            ALTER TABLE dbo.AppUsers DROP COLUMN EmailVerifiedAtUtc;
            ALTER TABLE dbo.AnalysisRuns DROP COLUMN SourceProtected;
            COMMIT TRANSACTION;
            """);
        await database.EnsureDatabaseAsync();
        Check("existing v1 schema upgrades to v3 without recreating core tables", await ScalarAsync("SELECT COUNT(*) FROM dbo.SchemaVersions WHERE VersionNumber IN (1,2,3)") == 3 &&
            await ScalarAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.AnalysisRuns') AND name=N'SourceProtected'") == 1 &&
            await ScalarAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.AppUsers') AND name=N'EmailVerifiedAtUtc'") == 1);
    }
    await database.EnsureDatabaseAsync();
    Check("schema v1→v3 migrations are idempotent", await ScalarAsync("SELECT COUNT(*) FROM dbo.SchemaVersions WHERE VersionNumber IN (1,2,3)") == 3);
    Check("v2 stores a per-run source snapshot", await ScalarAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.AnalysisRuns') AND name=N'SourceProtected'") == 1);
    Check("account, projects, catalog and normalized run tables exist", await ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE name IN ('AppUsers','UserSettings','Projects','AlgorithmCatalog','AnalysisRuns','RunSymbols','RunSteps')") == 7);
    Check("v3 has one-time challenge tables and trusted foreign keys", await ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE name IN ('PasswordResetChallenges','EmailVerificationChallenges')") == 2 &&
        await ScalarAsync("SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN ('FK_PasswordResetChallenges_AppUsers','FK_EmailVerificationChallenges_AppUsers') AND is_not_trusted=0") == 2);

    sessionA = await database.RegisterAsync(emailA, userA, "Integration A", "RouteLab!2026A");
    sessionB = await database.RegisterAsync(emailB, userB, "Integration B", "RouteLab!2026B");
    var authenticated = await database.LoginAsync(emailA, "RouteLab!2026A");
    Check("registered account can authenticate", authenticated.UserId == sessionA.UserId);
    try
    {
        await database.ChangePasswordAsync(sessionA, "Incorrect!2026", "RouteLab!2027A");
        throw new InvalidOperationException("incorrect current password was accepted");
    }
    catch (AccountException) { Check("profile rejects an incorrect current password", true); }
    await database.ChangePasswordAsync(sessionA, "RouteLab!2026A", "RouteLab!2027A");
    Check("profile password change keeps the account usable", (await database.LoginAsync(emailA, "RouteLab!2027A")).UserId == sessionA.UserId);
    try
    {
        await database.LoginAsync(emailA, "RouteLab!2026A");
        throw new InvalidOperationException("old password remained valid");
    }
    catch (AccountException) { Check("old password is invalid after profile update", true); }

    try
    {
        await database.RegisterAsync(emailA.ToUpperInvariant(), $"duplicate_{stamp}", "Duplicate", "RouteLab!2026C");
        throw new InvalidOperationException("duplicate email was accepted");
    }
    catch (AccountException) { Check("normalized email uniqueness is enforced", true); }

    Check("legacy registration stays unverified", !await database.IsEmailVerifiedAsync(sessionA));
    Check("unknown and unverified emails receive no reset challenge", await database.BeginPasswordResetAsync(emailA) is null &&
        await database.BeginPasswordResetAsync($"absent-{stamp}@example.invalid") is null);
    try
    {
        await database.BeginEmailVerificationAsync(sessionA, "wrong-current-password");
        throw new InvalidOperationException("email verification accepted a wrong password");
    }
    catch (AccountException) { Check("legacy email verification requires current password", true); }
    var verify = await database.BeginEmailVerificationAsync(sessionA, "RouteLab!2027A") ?? throw new Exception("Expected email verification challenge.");
    Check("verification code is six digits and only its hash is stored", verify.Code.Length == 6 && verify.Code.All(char.IsAsciiDigit) &&
        await ScalarTextAsync("SELECT CONVERT(varchar(64),CodeHash,2) FROM dbo.EmailVerificationChallenges WHERE UserId=@userId", sessionA.UserId) == Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(verify.Code))));
    try
    {
        await database.CompleteEmailVerificationAsync(sessionA, verify.Code);
        throw new InvalidOperationException("undelivered verification code was accepted");
    }
    catch (AccountException) { Check("email verification rejects undelivered code", true); }
    await database.MarkEmailVerificationDeliveredAsync(verify);
    try
    {
        await database.BeginEmailVerificationAsync(sessionA, "RouteLab!2027A");
        throw new InvalidOperationException("verification cooldown was bypassed");
    }
    catch (AccountException) { Check("email verification enforces one-minute cooldown", true); }
    try
    {
        await database.CompleteEmailVerificationAsync(sessionA, verify.Code == "000000" ? "000001" : "000000");
        throw new InvalidOperationException("wrong verification code was accepted");
    }
    catch (AccountException) { Check("email verification rejects an incorrect code", await ScalarAsync("SELECT FailedAttempts FROM dbo.EmailVerificationChallenges WHERE UserId=" + sessionA.UserId) == 1); }
    await database.CompleteEmailVerificationAsync(sessionA, verify.Code);
    Check("legacy account can verify only its stored email", await database.IsEmailVerifiedAsync(sessionA) &&
        await ScalarAsync("SELECT COUNT(*) FROM dbo.EmailVerificationChallenges WHERE UserId=" + sessionA.UserId) == 0);
    try
    {
        await database.CompleteEmailVerificationAsync(sessionA, verify.Code);
        throw new InvalidOperationException("verification code was reused");
    }
    catch (AccountException) { Check("email verification code is one-time", true); }

    var reset = await database.BeginPasswordResetAsync(emailA) ?? throw new Exception("Expected password-reset challenge.");
    Check("reset code has 128 bits and only its hash is stored", reset.Code.Length == 32 && reset.Code.All(char.IsAsciiHexDigit) &&
        await ScalarTextAsync("SELECT CONVERT(varchar(64),CodeHash,2) FROM dbo.PasswordResetChallenges WHERE UserId=@userId", sessionA.UserId) == Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(reset.Code))));
    try
    {
        await database.CompletePasswordResetAsync(emailA, reset.Code, "RouteLab!2028A");
        throw new InvalidOperationException("undelivered reset code was accepted");
    }
    catch (AccountException) { Check("reset rejects an undelivered code", true); }
    await database.MarkPasswordResetDeliveredAsync(reset);
    Check("reset requests enforce one-minute cooldown", await database.BeginPasswordResetAsync(emailA) is null);
    var wrongResetCode = (reset.Code[0] == '0' ? "1" : "0") + reset.Code[1..];
    for (var attempt = 0; attempt < 5; attempt++)
    {
        try
        {
            await database.CompletePasswordResetAsync(emailA, wrongResetCode, "RouteLab!2028A");
            throw new InvalidOperationException("wrong reset code was accepted");
        }
        catch (AccountException) { }
    }
    try
    {
        await database.CompletePasswordResetAsync(emailA, reset.Code, "RouteLab!2028A");
        throw new InvalidOperationException("reset code survived the five-attempt limit");
    }
    catch (AccountException) { Check("reset code locks after five wrong attempts", await ScalarAsync("SELECT FailedAttempts FROM dbo.PasswordResetChallenges WHERE UserId=" + sessionA.UserId) == 5); }
    await ExecuteAsync("UPDATE dbo.PasswordResetChallenges SET IssuedUtc=DATEADD(minute,-2,SYSUTCDATETIME()) WHERE UserId=" + sessionA.UserId);
    var expired = await database.BeginPasswordResetAsync(emailA) ?? throw new Exception("Expected second reset challenge.");
    await database.MarkPasswordResetDeliveredAsync(expired);
    await ExecuteAsync("UPDATE dbo.PasswordResetChallenges SET IssuedUtc=DATEADD(minute,-20,SYSUTCDATETIME()),ExpiresUtc=DATEADD(minute,-10,SYSUTCDATETIME()) WHERE UserId=" + sessionA.UserId);
    try
    {
        await database.CompletePasswordResetAsync(emailA, expired.Code, "RouteLab!2028A");
        throw new InvalidOperationException("expired reset code was accepted");
    }
    catch (AccountException) { Check("reset code expires after ten minutes", true); }
    var current = await database.BeginPasswordResetAsync(emailA) ?? throw new Exception("Expected final reset challenge.");
    await database.MarkPasswordResetDeliveredAsync(current);
    await database.CompletePasswordResetAsync(emailA, current.Code, "RouteLab!2028A");
    Check("delivered one-time reset updates password", (await database.LoginAsync(emailA, "RouteLab!2028A")).UserId == sessionA.UserId &&
        await ScalarAsync("SELECT COUNT(*) FROM dbo.PasswordResetChallenges WHERE UserId=" + sessionA.UserId) == 0);
    try
    {
        await database.CompletePasswordResetAsync(emailA, current.Code, "RouteLab!2029A");
        throw new InvalidOperationException("reset code was reused");
    }
    catch (AccountException) { Check("reset code cannot be reused", true); }

    const string sourceSnapshot = "first immutable source · дятел 🐦";
    const string laterDraft = "second project draft, not the old run";
    var result = CompressionEngine.Run("Хаффман", sourceSnapshot);
    await database.SaveRunAsync(sessionA, "integration-history", sourceSnapshot, result);
    await database.SaveDraftAsync(sessionA, "integration-history", "Хаффман", laterDraft);
    var runs = await database.GetAnalysisHistoryAsync(sessionA);
    Check("analysis archive returns the matching historical source snapshot", runs.Count == 1 && runs[0].Source == sourceSnapshot);
    Check("analysis archive decrypts the saved output", runs.Count == 1 && runs[0].Output == result.Output);
    Check("analysis archive keeps per-run metrics", runs.Count == 1 && runs[0].OutputBits == result.OutputBits && runs[0].Algorithm == result.Algorithm);
    Check("project library retains the latest draft separately", (await database.GetProjectsAsync(sessionA)).Single().Source == laterDraft);
    Check("run history is isolated by account", (await database.GetAnalysisHistoryAsync(sessionB)).Count == 0);
    Check("history grows only for the owner", await database.GetRunCountAsync(sessionA) == 1 && await database.GetRunCountAsync(sessionB) == 0);

    var stored = await ScalarTextAsync("SELECT CONVERT(varchar(max), r.SourceProtected, 2) FROM dbo.AnalysisRuns r JOIN dbo.Projects p ON p.ProjectId=r.ProjectId WHERE p.OwnerUserId=@userId", sessionA.UserId);
    Check("historical source is not stored as readable text", stored.Length > 0 && !stored.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes(sourceSnapshot)), StringComparison.OrdinalIgnoreCase));

    Console.WriteLine($"ИТОГ: {passed} интеграционных проверок пройдено.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Интеграционный прогон остановлен после {passed} проверок: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
finally
{
    try
    {
        if (sessionA is not null || sessionB is not null)
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = Environment.GetEnvironmentVariable("KODOSAD_SQL_SERVER") ?? @"(localdb)\MSSQLLocalDB",
                InitialCatalog = KodosadDatabase.DatabaseName,
                IntegratedSecurity = true,
                Encrypt = SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = false,
                ConnectTimeout = 5
            }.ConnectionString);
            await connection.OpenAsync();
            await using var cleanup = new SqlCommand("""
                DELETE FROM dbo.Projects WHERE OwnerUserId IN
                    (SELECT UserId FROM dbo.AppUsers WHERE NormalizedEmail IN (@emailA,@emailB));
                DELETE FROM dbo.AppUsers WHERE NormalizedEmail IN (@emailA,@emailB);
                """, connection);
            cleanup.Parameters.AddWithValue("@emailA", AccountSecurity.NormalizeEmail(emailA));
            cleanup.Parameters.AddWithValue("@emailB", AccountSecurity.NormalizeEmail(emailB));
            await cleanup.ExecuteNonQueryAsync();
        }
    }
    catch (Exception ex) { Console.Error.WriteLine("Внимание: автоматическая очистка тестовых аккаунтов не удалась: " + ex.Message); }

    if (ownsTestDatabase)
    {
        try
        {
            var testDatabaseName = KodosadDatabase.DatabaseName;
            await using var master = new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = Environment.GetEnvironmentVariable("KODOSAD_SQL_SERVER") ?? @"(localdb)\MSSQLLocalDB",
                InitialCatalog = "master",
                IntegratedSecurity = true,
                Encrypt = SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = false,
                ConnectTimeout = 5
            }.ConnectionString);
            await master.OpenAsync();
            await using var drop = new SqlCommand($"IF DB_ID(N'{testDatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{testDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{testDatabaseName}]; END", master);
            await drop.ExecuteNonQueryAsync();
            Console.WriteLine("Временная база интеграционного прогона удалена.");
        }
        catch (Exception ex) { Console.Error.WriteLine("Внимание: временную БД тестирования не удалось удалить: " + ex.Message); }
        finally { Environment.SetEnvironmentVariable("KODOSAD_DATABASE_NAME", originalDatabaseName); }
    }
}

async Task<int> ScalarAsync(string sql)
{
    await using var connection = await OpenAsync();
    await using var command = new SqlCommand(sql, connection);
    return Convert.ToInt32(await command.ExecuteScalarAsync());
}

async Task ExecuteAsync(string sql)
{
    await using var connection = await OpenAsync();
    await using var command = new SqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

async Task<string> ScalarTextAsync(string sql, int userId)
{
    await using var connection = await OpenAsync();
    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@userId", userId);
    return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
}

async Task<SqlConnection> OpenAsync()
{
    var connection = new SqlConnection(new SqlConnectionStringBuilder
    {
        DataSource = Environment.GetEnvironmentVariable("KODOSAD_SQL_SERVER") ?? @"(localdb)\MSSQLLocalDB",
        InitialCatalog = KodosadDatabase.DatabaseName,
        IntegratedSecurity = true,
        Encrypt = SqlConnectionEncryptOption.Optional,
        TrustServerCertificate = false,
        ConnectTimeout = 5
    }.ConnectionString);
    await connection.OpenAsync();
    return connection;
}
