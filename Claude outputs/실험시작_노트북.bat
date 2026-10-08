@echo off
chcp 949 >nul
title AR SYSTEM 실험

set "BASE=%USERPROFILE%\Documents\AR-SYSTEM"
set "ADB=%BASE%\platform-tools\adb.exe"
set "SRV=%BASE%\PCCaptureServer\bin\Release\net10.0\win-x64\publish"

echo ============================================
echo   AR SYSTEM  실험 시작
echo ============================================
echo.

if not exist "%ADB%" goto NOADB
if not exist "%SRV%\PCCaptureServer.exe" goto NOSRV

echo [1/3] 퀘스트 찾는 중...
"%ADB%" start-server >nul 2>&1
"%ADB%" devices
echo.

"%ADB%" get-state >nul 2>&1
if errorlevel 1 goto NODEV

echo [2/3] 퀘스트-노트북 연결 통로 여는 중...
"%ADB%" reverse --remove-all >nul 2>&1
"%ADB%" reverse tcp:8080 tcp:8080
if errorlevel 1 goto FAIL_REV
"%ADB%" reverse tcp:8081 tcp:8081
if errorlevel 1 goto FAIL_REV
echo       8080 / 8081 연결 완료.
echo.

echo [3/3] 서버 시작...
echo.
echo  ------------------------------------------
echo   퀘스트에서 ARsystem 을 실행하세요.
echo   이 창에 명령을 칠 수 있습니다:
echo     cal      배치 다시 하기
echo     c1 ~ c4  실험 조건 바꾸기
echo     help     전체 명령 보기
echo  ------------------------------------------
echo.

cd /d "%SRV%"
PCCaptureServer.exe
goto END

:NOADB
echo  [오류] adb 가 없습니다.
echo.
echo  아래 한 줄을 명령 프롬프트에 붙여넣어 받으세요:
echo.
echo   cd /d "%BASE%" ^&^& curl -L -o pt.zip https://dl.google.com/android/repository/platform-tools-latest-windows.zip ^&^& tar -xf pt.zip ^&^& del pt.zip
echo.
goto END

:NOSRV
echo  [오류] 서버 파일이 없습니다.
echo  찾은 위치: %SRV%
echo.
echo  아래를 붙여넣어 서버를 먼저 만드세요:
echo.
echo   cd /d "%BASE%\PCCaptureServer" ^&^& dotnet publish -c Release -r win-x64 --self-contained true
echo.
goto END

:NODEV
echo  [오류] 퀘스트를 찾지 못했습니다.
echo.
echo   1. USB 케이블이 꽂혀 있는지
echo   2. 헤드셋이 켜져 있는지 (화면이 꺼져 있으면 한 번 써서 깨우세요)
echo   3. 헤드셋 안에 "USB 디버깅을 허용하시겠습니까?" 창이 떠 있는지
echo      -^> "항상 허용" 체크하고 확인
echo.
echo  셋 다 확인한 뒤 이 파일을 다시 더블클릭하세요.
echo.
goto END

:FAIL_REV
echo.
echo  [오류] 연결 통로를 열지 못했습니다.
echo  위에 뜬 메시지를 그대로 보내주세요.
echo.

:END
echo.
pause
