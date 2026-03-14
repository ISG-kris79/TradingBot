-- GeneralSettings 테이블 생성 스크립트 (MSSQL 2019)
-- 이 스크립트를 SQL Server Management Studio에서 실행하여 테이블을 생성하세요.

-- 기존 테이블 삭제 (재초기화 시만)
-- DROP TABLE IF EXISTS dbo.GeneralSettings;

-- GeneralSettings 테이블 생성 (Id = Users.Id)
CREATE TABLE dbo.GeneralSettings (
    Id INT NOT NULL,  -- 사용자 키 (Users.Id)
    DefaultLeverage INT NOT NULL DEFAULT 20,
    DefaultMargin DECIMAL(18, 4) NOT NULL DEFAULT 200.0,
    TargetRoe DECIMAL(18, 4) NOT NULL DEFAULT 20.0,
    StopLossRoe DECIMAL(18, 4) NOT NULL DEFAULT 15.0,
    TrailingStartRoe DECIMAL(18, 4) NOT NULL DEFAULT 20.0,
    TrailingDropRoe DECIMAL(18, 4) NOT NULL DEFAULT 5.0,
    UpdatedAt DATETIME2 DEFAULT GETUTCDATE(),
    CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
    CONSTRAINT PK_GeneralSettings_Id PRIMARY KEY (Id),
    CONSTRAINT FK_GeneralSettings_Users_Id FOREIGN KEY (Id) REFERENCES Users(Id) ON DELETE CASCADE
);

-- 인덱스 생성 (조회 최적화)
CREATE INDEX IX_GeneralSettings_Id ON dbo.GeneralSettings(Id DESC, UpdatedAt DESC);

-- 초기 데이터 삽입 (각 사용자별 기본 설정)
-- 참고: Users 테이블에 이미 사용자가 있어야 합니다
-- INSERT INTO dbo.GeneralSettings (Id, DefaultLeverage, DefaultMargin, TargetRoe, StopLossRoe, TrailingStartRoe, TrailingDropRoe)
-- SELECT Id, 20, 200.0, 20.0, 15.0, 20.0, 5.0 FROM dbo.Users WHERE Id IS NOT NULL;

-- ─── 마이그레이션: PUMP 전용 손절/진입 증거금 컬럼 추가 ─────────────────────
-- 기존 테이블에 신규 컬럼이 없는 경우 아래 ALTER TABLE을 실행하세요.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpStopLossRoe')
    ALTER TABLE dbo.GeneralSettings ADD PumpStopLossRoe DECIMAL(18, 4) NOT NULL DEFAULT 60.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpMargin')
    ALTER TABLE dbo.GeneralSettings ADD PumpMargin DECIMAL(18, 4) NOT NULL DEFAULT 200.0;

-- ─── 마이그레이션: PUMP 추가 컬럼 (PumpBreakEvenRoe, TrailingStartRoe, TrailingGapRoe) ──
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpBreakEvenRoe')
    ALTER TABLE dbo.GeneralSettings ADD PumpBreakEvenRoe DECIMAL(18, 4) NOT NULL DEFAULT 20.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpTrailingStartRoe')
    ALTER TABLE dbo.GeneralSettings ADD PumpTrailingStartRoe DECIMAL(18, 4) NOT NULL DEFAULT 40.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpTrailingGapRoe')
    ALTER TABLE dbo.GeneralSettings ADD PumpTrailingGapRoe DECIMAL(18, 4) NOT NULL DEFAULT 20.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpLeverage')
    ALTER TABLE dbo.GeneralSettings ADD PumpLeverage INT NOT NULL DEFAULT 20;

-- ─── 마이그레이션: PUMP 추세홀딩 튜닝 컬럼 (1차 익절 비중/계단식 ROE) ─────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpFirstTakeProfitRatioPct')
    ALTER TABLE dbo.GeneralSettings ADD PumpFirstTakeProfitRatioPct DECIMAL(18, 4) NOT NULL DEFAULT 15.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpStairStep1Roe')
    ALTER TABLE dbo.GeneralSettings ADD PumpStairStep1Roe DECIMAL(18, 4) NOT NULL DEFAULT 50.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpStairStep2Roe')
    ALTER TABLE dbo.GeneralSettings ADD PumpStairStep2Roe DECIMAL(18, 4) NOT NULL DEFAULT 100.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpStairStep3Roe')
    ALTER TABLE dbo.GeneralSettings ADD PumpStairStep3Roe DECIMAL(18, 4) NOT NULL DEFAULT 200.0;

-- ─── 마이그레이션: 메이저 코인 완전 분리 (v2.5+) ────────────────────────────────────────
-- 메이저 전용 레버리지/증거금/ROE 5단계 전부 독립 관리
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'MajorLeverage')
    ALTER TABLE dbo.GeneralSettings ADD MajorLeverage INT NOT NULL DEFAULT 20;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'MajorMargin')
    ALTER TABLE dbo.GeneralSettings ADD MajorMargin DECIMAL(18, 4) NOT NULL DEFAULT 200.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'MajorBreakEvenRoe')
    ALTER TABLE dbo.GeneralSettings ADD MajorBreakEvenRoe DECIMAL(18, 4) NOT NULL DEFAULT 7.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'MajorTp1Roe')
    ALTER TABLE dbo.GeneralSettings ADD MajorTp1Roe DECIMAL(18, 4) NOT NULL DEFAULT 20.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'MajorTp2Roe')
    ALTER TABLE dbo.GeneralSettings ADD MajorTp2Roe DECIMAL(18, 4) NOT NULL DEFAULT 40.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'MajorTrailingStartRoe')
    ALTER TABLE dbo.GeneralSettings ADD MajorTrailingStartRoe DECIMAL(18, 4) NOT NULL DEFAULT 40.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'MajorTrailingGapRoe')
    ALTER TABLE dbo.GeneralSettings ADD MajorTrailingGapRoe DECIMAL(18, 4) NOT NULL DEFAULT 5.0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'MajorStopLossRoe')
    ALTER TABLE dbo.GeneralSettings ADD MajorStopLossRoe DECIMAL(18, 4) NOT NULL DEFAULT 20.0;

-- 확인
SELECT * FROM dbo.GeneralSettings;
