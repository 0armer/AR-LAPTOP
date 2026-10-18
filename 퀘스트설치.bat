@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ============================================
echo   AR LAPTOP 퀘스트 설치
echo ============================================
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.3.14f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if not exist "%ADB%" (
  echo [오류] adb를 못 찾음: %ADB%
  echo 유니티 설치 경로가 다르면 이 파일의 ADB 줄을 고치세요.
  pause
  exit /b 1
)
if not exist "ar laptop.apk" (
  echo [오류] ar laptop.apk 가 없음. 유니티에서 Build 먼저 하세요.
  pause
  exit /b 1
)
for %%A in ("ar laptop.apk") do echo APK 빌드 시각: %%~tA
echo.
echo [1/4] 퀘스트 연결 확인
"%ADB%" devices
rem USB와 무선이 둘 다 잡히면 adb가 헷갈리므로 하나를 고른다: USB 우선, 없으면 무선(IP:5555)
set "ANDROID_SERIAL="
for /f "skip=1 tokens=1,2" %%a in ('"%ADB%" devices') do (
  if "%%b"=="device" (
    echo %%a | findstr ":" >nul
    if errorlevel 1 (set "ANDROID_SERIAL=%%a") else (if not defined ANDROID_SERIAL set "ANDROID_SERIAL=%%a")
  )
)
if not defined ANDROID_SERIAL (
  echo [오류] 퀘스트가 안 잡힘. 케이블을 다시 꽂거나, 헤드셋을 쓰고 USB 디버깅 허용을 누르세요.
  pause
  exit /b 1
)
echo 사용할 퀘스트: %ANDROID_SERIAL%
echo %ANDROID_SERIAL% | findstr ":" >nul && echo (무선으로 설치합니다 - 1~2분 걸릴 수 있음)
echo.
echo [2/4] APK를 퀘스트로 복사 중...
"%ADB%" push "ar laptop.apk" /data/local/tmp/arlaptop.apk
if errorlevel 1 (
  echo [오류] 복사 실패. 케이블/USB 포트를 바꿔보세요.
  pause
  exit /b 1
)
echo.
echo [3/4] 설치 중...
"%ADB%" shell pm install -r /data/local/tmp/arlaptop.apk
echo.
echo [4/4] 설치된 시각 확인 (방금 시각이면 성공)
"%ADB%" shell dumpsys package com.arsystem.arlaptop | findstr lastUpdateTime
"%ADB%" shell rm /data/local/tmp/arlaptop.apk
echo.
echo 앱 재시작 (예전 버전이 켜져 있으면 끄고 새로 켬)
"%ADB%" shell am force-stop com.arsystem.arlaptop
"%ADB%" shell monkey -p com.arsystem.arlaptop -c android.intent.category.LAUNCHER 1 >nul 2>nul
echo.
echo 끝. 헤드셋을 쓰고 확인하세요.
pause
