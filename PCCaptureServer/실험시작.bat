@echo off
title AR 실험 서버
cd /d "%~dp0"

echo ============================================
echo   AR 창 배치 실험 - 서버 시작
echo ============================================
echo.

rem ---- adb.exe 찾기 ----------------------------------------------------
set "ADB="
if exist "%~dp0adb.exe" set "ADB=%~dp0adb.exe"
if not defined ADB if exist "%~dp0platform-tools\adb.exe" set "ADB=%~dp0platform-tools\adb.exe"
if not defined ADB if exist "%~dp0..\platform-tools\adb.exe" set "ADB=%~dp0..\platform-tools\adb.exe"
if not defined ADB if exist "C:\실험\platform-tools\adb.exe" set "ADB=C:\실험\platform-tools\adb.exe"
if not defined ADB where adb >nul 2>nul && set "ADB=adb"

if not defined ADB goto NOADB

rem ---- 1. 퀘스트 연결 확인 ---------------------------------------------
echo [1/3] 퀘스트 연결 확인...
"%ADB%" devices
"%ADB%" devices | findstr /r /c:"	device$" >nul
if errorlevel 1 goto NODEVICE
echo       연결됨.
echo.

rem ---- 2. 포트 넘기기 --------------------------------------------------
echo [2/3] 포트 연결...
"%ADB%" reverse tcp:8080 tcp:8080
"%ADB%" reverse tcp:8081 tcp:8081
"%ADB%" reverse --list
echo.

rem ---- 3. 서버 시작 ----------------------------------------------------
echo [3/3] 서버 시작...
echo.
echo   퀘스트에서 [앱] - [알 수 없는 출처] - [AR SYSTEM] 실행하세요.
echo.
echo   진행자 명령:  p07   c1~c4   t1   .   x   ?      끝낼 때 Ctrl+C
echo.
echo --------------------------------------------
PCCaptureServer.exe
goto END

:NODEVICE
echo.
echo   [!] 퀘스트가 잡히지 않았습니다.
echo.
echo       - USB 케이블이 데이터 전송용인지 확인 (충전 전용 케이블 주의)
echo       - 헤드셋을 쓰고 "USB 디버깅을 허용하시겠습니까?" 에 허용
echo       - 그래도 안 되면 케이블을 뽑았다 다시 꽂기
echo.
pause
goto END

:NOADB
echo.
echo   [!] adb.exe 를 찾지 못했습니다.
echo.
echo       platform-tools 폴더를 이 폴더 안이나 바로 옆에 두세요.
echo       예:  C:\실험\platform-tools\adb.exe
echo            C:\실험\publish\실험시작.bat
echo.
pause

:END
