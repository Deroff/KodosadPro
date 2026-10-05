IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SchemaVersions
    (
        VersionNumber int NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
        AppliedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_SchemaVersions_AppliedAt DEFAULT SYSUTCDATETIME()
    );
END;

IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE VersionNumber = 1)
BEGIN
    BEGIN TRANSACTION;

    CREATE TABLE dbo.AppUsers
    (
        UserId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AppUsers PRIMARY KEY,
        Email nvarchar(254) NOT NULL,
        NormalizedEmail nvarchar(254) NOT NULL,
        UserName nvarchar(40) NOT NULL,
        NormalizedUserName nvarchar(40) NOT NULL,
        DisplayName nvarchar(80) NOT NULL,
        PasswordSalt varbinary(16) NOT NULL,
        PasswordHash varbinary(32) NOT NULL,
        PasswordIterations int NOT NULL,
        FailedAttempts tinyint NOT NULL CONSTRAINT DF_AppUsers_FailedAttempts DEFAULT 0,
        LockoutUntilUtc datetime2(0) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_AppUsers_CreatedAt DEFAULT SYSUTCDATETIME(),
        LastLoginAtUtc datetime2(0) NULL
    );
    CREATE UNIQUE INDEX UX_AppUsers_Email ON dbo.AppUsers(NormalizedEmail);
    CREATE UNIQUE INDEX UX_AppUsers_UserName ON dbo.AppUsers(NormalizedUserName);

    CREATE TABLE dbo.AlgorithmCatalog
    (
        AlgorithmId tinyint NOT NULL CONSTRAINT PK_AlgorithmCatalog PRIMARY KEY,
        AlgorithmKey varchar(24) NOT NULL CONSTRAINT UQ_AlgorithmCatalog_Key UNIQUE,
        DisplayName nvarchar(40) NOT NULL CONSTRAINT UQ_AlgorithmCatalog_Name UNIQUE
    );

    CREATE TABLE dbo.Projects
    (
        ProjectId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Projects PRIMARY KEY,
        OwnerUserId int NOT NULL,
        ProjectName nvarchar(120) NOT NULL,
        AlgorithmId tinyint NOT NULL,
        SourceProtected varbinary(max) NOT NULL,
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_Projects_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_Projects_AppUsers FOREIGN KEY (OwnerUserId) REFERENCES dbo.AppUsers(UserId),
        CONSTRAINT FK_Projects_AlgorithmCatalog FOREIGN KEY (AlgorithmId) REFERENCES dbo.AlgorithmCatalog(AlgorithmId),
        CONSTRAINT UQ_Projects_Owner_Name UNIQUE (OwnerUserId, ProjectName)
    );

    CREATE TABLE dbo.AnalysisRuns
    (
        RunId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AnalysisRuns PRIMARY KEY,
        ProjectId int NOT NULL,
        AlgorithmId tinyint NOT NULL,
        SourceCharacters int NOT NULL,
        SourceBytes int NOT NULL,
        OutputBits int NOT NULL,
        Entropy float NOT NULL,
        AverageCodeLength float NOT NULL,
        RatioPercent float NOT NULL,
        OutputProtected varbinary(max) NOT NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_AnalysisRuns_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_AnalysisRuns_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE,
        CONSTRAINT FK_AnalysisRuns_AlgorithmCatalog FOREIGN KEY (AlgorithmId) REFERENCES dbo.AlgorithmCatalog(AlgorithmId)
    );
    CREATE INDEX IX_AnalysisRuns_Project_Date ON dbo.AnalysisRuns(ProjectId, CreatedAtUtc DESC);

    CREATE TABLE dbo.RunSymbols
    (
        RunId bigint NOT NULL,
        SymbolValue int NOT NULL,
        Frequency int NOT NULL,
        Codeword varchar(4096) NOT NULL,
        CONSTRAINT PK_RunSymbols PRIMARY KEY (RunId, SymbolValue),
        CONSTRAINT FK_RunSymbols_AnalysisRuns FOREIGN KEY (RunId) REFERENCES dbo.AnalysisRuns(RunId) ON DELETE CASCADE
    );

    CREATE TABLE dbo.RunSteps
    (
        RunId bigint NOT NULL,
        StepNumber int NOT NULL,
        Description nvarchar(1000) NOT NULL,
        CONSTRAINT PK_RunSteps PRIMARY KEY (RunId, StepNumber),
        CONSTRAINT FK_RunSteps_AnalysisRuns FOREIGN KEY (RunId) REFERENCES dbo.AnalysisRuns(RunId) ON DELETE CASCADE
    );

    CREATE TABLE dbo.UserSettings
    (
        UserId int NOT NULL CONSTRAINT PK_UserSettings PRIMARY KEY,
        PreferredAlgorithmId tinyint NOT NULL,
        AutoSaveEnabled bit NOT NULL CONSTRAINT DF_UserSettings_AutoSave DEFAULT 1,
        CONSTRAINT FK_UserSettings_AppUsers FOREIGN KEY (UserId) REFERENCES dbo.AppUsers(UserId) ON DELETE CASCADE,
        CONSTRAINT FK_UserSettings_AlgorithmCatalog FOREIGN KEY (PreferredAlgorithmId) REFERENCES dbo.AlgorithmCatalog(AlgorithmId)
    );

    INSERT dbo.AlgorithmCatalog (AlgorithmId, AlgorithmKey, DisplayName) VALUES
        (1, 'huffman', N'Хаффман'),
        (2, 'shannon-fano', N'Шеннон–Фано'),
        (3, 'rle', N'RLE');

    INSERT dbo.SchemaVersions (VersionNumber) VALUES (1);
    COMMIT TRANSACTION;
END;

-- Preserve source snapshots per run while upgrading an existing v1 database.
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE VersionNumber = 2)
BEGIN
    BEGIN TRANSACTION;
    IF COL_LENGTH(N'dbo.AnalysisRuns', N'SourceProtected') IS NULL
        ALTER TABLE dbo.AnalysisRuns ADD SourceProtected varbinary(max) NULL;
    INSERT dbo.SchemaVersions (VersionNumber) VALUES (2);
    COMMIT TRANSACTION;
END;

-- Legacy accounts remain unverified until their owner confirms the stored address.
-- Both kinds of one-time mail challenge store only a code hash.
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersions WHERE VersionNumber = 3)
BEGIN
    BEGIN TRANSACTION;
    IF COL_LENGTH(N'dbo.AppUsers', N'EmailVerifiedAtUtc') IS NULL
        ALTER TABLE dbo.AppUsers ADD EmailVerifiedAtUtc datetime2(0) NULL;
    IF OBJECT_ID(N'dbo.PasswordResetChallenges', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PasswordResetChallenges
        (
            UserId int NOT NULL CONSTRAINT PK_PasswordResetChallenges PRIMARY KEY,
            CodeHash varbinary(32) NOT NULL,
            IssuedUtc datetime2(0) NOT NULL,
            ExpiresUtc datetime2(0) NOT NULL,
            DeliveredUtc datetime2(0) NULL,
            FailedAttempts tinyint NOT NULL CONSTRAINT DF_PasswordResetChallenges_FailedAttempts DEFAULT 0,
            CONSTRAINT FK_PasswordResetChallenges_AppUsers FOREIGN KEY (UserId) REFERENCES dbo.AppUsers(UserId) ON DELETE CASCADE,
            CONSTRAINT CK_PasswordResetChallenges_Expiry CHECK (ExpiresUtc > IssuedUtc),
            CONSTRAINT CK_PasswordResetChallenges_Attempts CHECK (FailedAttempts BETWEEN 0 AND 5)
        );
    END;
    IF OBJECT_ID(N'dbo.EmailVerificationChallenges', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EmailVerificationChallenges
        (
            UserId int NOT NULL CONSTRAINT PK_EmailVerificationChallenges PRIMARY KEY,
            CodeHash varbinary(32) NOT NULL,
            IssuedUtc datetime2(0) NOT NULL,
            ExpiresUtc datetime2(0) NOT NULL,
            DeliveredUtc datetime2(0) NULL,
            FailedAttempts tinyint NOT NULL CONSTRAINT DF_EmailVerificationChallenges_FailedAttempts DEFAULT 0,
            CONSTRAINT FK_EmailVerificationChallenges_AppUsers FOREIGN KEY (UserId) REFERENCES dbo.AppUsers(UserId) ON DELETE CASCADE,
            CONSTRAINT CK_EmailVerificationChallenges_Expiry CHECK (ExpiresUtc > IssuedUtc),
            CONSTRAINT CK_EmailVerificationChallenges_Attempts CHECK (FailedAttempts BETWEEN 0 AND 5)
        );
    END;
    INSERT dbo.SchemaVersions (VersionNumber) VALUES (3);
    COMMIT TRANSACTION;
END;
