@echo off
chcp 949 >nul
setlocal enabledelayedexpansion
title AR SYSTEM 노트북 설치

set "BASE=%USERPROFILE%\Documents\AR-SYSTEM"
set "REPO=https://github.com/0armer/AR-SYSTEM.git"

echo ============================================
echo   AR SYSTEM  노트북 설치
echo ============================================
echo.
echo  설치 위치 : %BASE%
echo.

REM ---------- 1. 준비물 확인 ----------
echo [1/5] 준비물 확인 중...

where git >nul 2>&1
if errorlevel 1 goto NOGIT

where dotnet >nul 2>&1
if errorlevel 1 goto NODOTNET

echo       Git   : 있음
echo       .NET  : 있음
echo.

REM ---------- 2. 프로젝트 받기 ----------
echo [2/5] 프로젝트 받는 중... (처음이면 1~3분)

if exist "%BASE%\.git" goto PULL

cd /d "%USERPROFILE%\Documents"
git clone "%REPO%" AR-SYSTEM
if errorlevel 1 goto FAIL_CLONE
echo       새로 받았습니다.
goto GOTPROJ

:PULL
cd /d "%BASE%"
git pull
if errorlevel 1 goto FAIL_PULL
echo       최신으로 갱신했습니다.

:GOTPROJ
echo.

REM ---------- 3. csproj 확인 ----------
REM  프로젝트 루트 .gitignore 의  *.csproj  규칙 때문에
REM  PCCaptureServer.csproj 가 GitHub 에 안 올라가 있다.
REM  없으면 여기서 똑같이 만들어 준다.
echo [3/5] 서버 프로젝트 파일 확인 중...

set "CSPROJ=%BASE%\PCCaptureServer\PCCaptureServer.csproj"
if exist "%CSPROJ%" goto CSPROJOK

echo       없어서 새로 만듭니다.
> "%CSPROJ%" echo ^<Project Sdk="Microsoft.NET.Sdk"^>
>>"%CSPROJ%" echo.
>>"%CSPROJ%" echo   ^<PropertyGroup^>
>>"%CSPROJ%" echo     ^<OutputType^>Exe^</OutputType^>
>>"%CSPROJ%" echo     ^<UseWindows^>true^</UseWindows^>
>>"%CSPROJ%" echo     ^<TargetFramework^>net10.0^</TargetFramework^>
>>"%CSPROJ%" echo     ^<ImplicitUsings^>enable^</ImplicitUsings^>
>>"%CSPROJ%" echo     ^<Nullable^>enable^</Nullable^>
>>"%CSPROJ%" echo   ^</PropertyGroup^>
>>"%CSPROJ%" echo.
>>"%CSPROJ%" echo   ^<ItemGroup^>
>>"%CSPROJ%" echo     ^<PackageReference Include="System.Drawing.Common" Version="10.0.8" /^>
>>"%CSPROJ%" echo   ^</ItemGroup^>
>>"%CSPROJ%" echo.
>>"%CSPROJ%" echo ^</Project^>

:CSPROJOK
if not exist "%CSPROJ%" goto FAIL_CSPROJ
echo       확인 완료.
echo.

REM ---------- 4. 서버 만들기 ----------
echo [4/5] PC 서버 만드는 중... (처음이면 2~4분)

cd /d "%BASE%\PCCaptureServer"
dotnet publish -c Release -r win-x64 --self-contained true
if errorlevel 1 goto FAIL_BUILD

set "EXE=%BASE%\PCCaptureServer\bin\Release\net10.0\win-x64\publish\PCCaptureServer.exe"
if not exist "%EXE%" goto FAIL_BUILD
echo       서버 준비 완료.
echo.

REM ---------- 5. adb 받기 ----------
echo [5/5] adb 받는 중... (10MB)

if exist "%BASE%\platform-tools\adb.exe" goto ADBOK

cd /d "%BASE%"
curl -L -s -o platform-tools.zip https://dl.google.com/android/repository/platform-tools-latest-windows.zip
if errorlevel 1 goto FAIL_ADB
tar -xf platform-tools.zip
if errorlevel 1 goto FAIL_ADB
del platform-tools.zip >nul 2>&1

:ADBOK
if not exist "%BASE%\platform-tools\adb.exe" goto FAIL_ADB
echo       adb 준비 완료.
echo.

REM ---------- 실행 바로가기 만들기 ----------
set "RUN=%BASE%\실험시작_노트북.bat"
> "%RUN%" echo @echo off
>>"%RUN%" echo chcp 949 ^>nul
>>"%RUN%" echo title AR SYSTEM 실험
>>"%RUN%" echo set "ADB=%BASE%\platform-tools\adb.exe"
>>"%RUN%" echo echo [1/2] 퀘스트 연결 확인...
>>"%RUN%" echo "%%ADB%%" devices
>>"%RUN%" echo "%%ADB%%" reverse tcp:8080 tcp:8080
>>"%RUN%" echo "%%ADB%%" reverse tcp:8081 tcp:8081
>>"%RUN%" echo echo.
>>"%RUN%" echo echo [2/2] 서버 시작...
>>"%RUN%" echo cd /d "%BASE%\PCCaptureServer\bin\Release\net10.0\win-x64\publish"
>>"%RUN%" echo PCCaptureServer.exe
>>"%RUN%" echo pause

echo ============================================
echo   설치 끝났습니다!
echo ============================================
echo.
echo  실험할 때는 이 파일만 더블클릭하세요:
echo.
echo    %RUN%
echo.
echo  (퀘스트를 USB로 먼저 꽂아두세요)
echo.
pause
exit /b 0

REM ================= 오류 =================
:NOGIT
echo.
echo  [오류] Git 이 없습니다.
echo   https://git-scm.com/download/win
echo  받아서 계속 Next 만 눌러 설치한 뒤 이 파일을 다시 더블클릭하세요.
start https://git-scm.com/download/win
pause
exit /b 1

:NODOTNET
echo.
echo  [오류] .NET SDK 가 없습니다.
echo   https://dotnet.microsoft.com/download
echo  설치한 뒤 이 파일을 다시 더블클릭하세요.
start https://dotnet.microsoft.com/download
pause
exit /b 1

:FAIL_CLONE
echo.
echo  [오류] 프로젝트를 받지 못했습니다.
echo  인터넷 연결과 GitHub 로그인을 확인하세요.
pause
exit /b 1

:FAIL_PULL
echo.
echo  [오류] 갱신 실패. 위 메시지를 그대로 보내주세요.
pause
exit /b 1

:FAIL_CSPROJ
echo.
echo  [오류] 프로젝트 파일을 만들지 못했습니다.
echo  경로: %CSPROJ%
pause
exit /b 1

:FAIL_BUILD
echo.
echo  [오류] 서버를 만들지 못했습니다.
echo  위에 뜬 빨간 메시지를 그대로 보내주세요.
echo  ( .NET 10 이 아니라 8 이나 9 가 깔려 있으면 이 오류가 납니다 )
pause
exit /b 1

:FAIL_ADB
echo.
echo  [오류] adb 를 받지 못했습니다.
echo  아래에서 직접 받아 %BASE% 에 압축을 푸세요.
echo   https://developer.android.com/tools/releases/platform-tools
pause
exit /b 1
