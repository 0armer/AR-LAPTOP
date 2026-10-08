@echo off
chcp 65001 >nul
title AR LAPTOP 서버
cd /d "%~dp0"

echo ============================================
echo   AR LAPTOP 서버  (주 모니터 1개 복제)
echo ============================================
echo.

rem ---- USB로 연결돼 있으면 포트 넘기기 (무선이면 건너뜀) ----
set "ADB="
if exist "%~dp0adb.exe" set "ADB=%~dp0adb.exe"
if not defined ADB if exist "%~dp0platform-tools\adb.exe" set "ADB=%~dp0platform-tools\adb.exe"
if not defined ADB if exist "%~dp0..\platform-tools\adb.exe" set "ADB=%~dp0..\platform-tools\adb.exe"
if not defined ADB if exist "%~dp0..\..\AR SYSTEM\platform-tools\adb.exe" set "ADB=%~dp0..\..\AR SYSTEM\platform-tools\adb.exe"
if not defined ADB where adb >nul 2>nul && set "ADB=adb"

if defined ADB (
  echo [USB] 퀘스트 포트 연결 시도...
  "%ADB%" reverse tcp:8080 tcp:8080
  "%ADB%" reverse tcp:8081 tcp:8081
  echo.
) else (
  echo [USB] adb 없음 - 무선이면 괜찮음. 유니티 PCStreamReceiver의 Server Host를 PC IP로 해둘 것
  echo.
)

echo 서버 빌드+실행 중... ( [데스크탑 화면] 로그가 3초마다 떠야 정상 )
echo.
dotnet run
pause
