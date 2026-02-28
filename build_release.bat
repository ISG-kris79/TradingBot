@echo off
echo [1/3] Installing/Updating Velopack CLI...
dotnet tool update -g vpk

echo [2/3] Publishing .NET Application...
REM Release 모드로 빌드 및 게시 (win-x64)
dotnet publish TradingBot\TradingBot.csproj -c Release -r win-x64 --self-contained -o .\publish

echo [3/3] Packing with Velopack...
REM 버전은 필요에 따라 수정 (예: 1.0.0)
REM --packId: 패키지 ID (NuGet 패키지 ID와 유사)
REM --packVersion: 버전 번호
REM --packDir: publish 폴더 경로
REM --mainExe: 실행 파일 이름
vpk pack -u TradingBot -v 1.2.8 -p .\publish -e TradingBot.exe

echo Done! Check the 'Releases' folder.
pause