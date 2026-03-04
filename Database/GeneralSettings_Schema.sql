-- GeneralSettings 테이블 생성 스크립트 (MSSQL 2019)
-- 이 스크립트를 SQL Server Management Studio에서 실행하여 테이블을 생성하세요.

-- 기존 테이블 삭제 (재초기화 시만)
-- DROP TABLE IF EXISTS dbo.GeneralSettings;

-- GeneralSettings 테이블 생성 (Id = Users.UserId)
CREATE TABLE dbo.GeneralSettings (
    Id INT NOT NULL,  -- 사용자 키 (Users.UserId)
    DefaultLeverage INT NOT NULL DEFAULT 20,
    DefaultMargin DECIMAL(18, 4) NOT NULL DEFAULT 200.0,
    TargetRoe DECIMAL(18, 4) NOT NULL DEFAULT 20.0,
    StopLossRoe DECIMAL(18, 4) NOT NULL DEFAULT 15.0,
    TrailingStartRoe DECIMAL(18, 4) NOT NULL DEFAULT 20.0,
    TrailingDropRoe DECIMAL(18, 4) NOT NULL DEFAULT 5.0,
    UpdatedAt DATETIME2 DEFAULT GETUTCDATE(),
    CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
    CONSTRAINT PK_GeneralSettings_Id PRIMARY KEY (Id),
    CONSTRAINT FK_GeneralSettings_Users_Id FOREIGN KEY (Id) REFERENCES Users(UserId) ON DELETE CASCADE
);

-- 인덱스 생성 (조회 최적화)
CREATE INDEX IX_GeneralSettings_Id ON dbo.GeneralSettings(Id DESC, UpdatedAt DESC);

-- 초기 데이터 삽입 (각 사용자별 기본 설정)
-- 참고: Users 테이블에 이미 사용자가 있어야 합니다
-- INSERT INTO dbo.GeneralSettings (Id, DefaultLeverage, DefaultMargin, TargetRoe, StopLossRoe, TrailingStartRoe, TrailingDropRoe)
-- SELECT UserId, 20, 200.0, 20.0, 15.0, 20.0, 5.0 FROM dbo.Users WHERE UserId IS NOT NULL;

-- 확인
SELECT * FROM dbo.GeneralSettings;
