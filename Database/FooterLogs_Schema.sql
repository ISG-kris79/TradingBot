IF OBJECT_ID(N'dbo.FooterLogs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FooterLogs
    (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Timestamp] DATETIME2(3) NOT NULL CONSTRAINT DF_FooterLogs_Timestamp DEFAULT (SYSUTCDATETIME()),
        [Message] NVARCHAR(1000) NOT NULL
    );

    CREATE INDEX IX_FooterLogs_Timestamp
        ON dbo.FooterLogs([Timestamp] DESC);
END;
