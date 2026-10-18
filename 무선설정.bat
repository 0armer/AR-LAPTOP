@echo off
chcp 65001 >nul
rem ── 관리자 권한이 아니면 스스로 관리자 권한으로 다시 실행 ──
net session >nul 2>&1
if errorlevel 1 (
  echo 관리자 권한이 필요합니다. 뜨는 창에서 "예"를 눌러주세요.
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ============================================
echo   AR LAPTOP 무선 연결 설정 (한 번만 하면 됨)
echo ============================================
echo.
echo [1/3] 8080 접속 허용
netsh http delete urlacl url=http://+:8080/ >nul 2>&1
netsh http add urlacl url=http://+:8080/ sddl=D:(A;;GX;;;WD)
echo [2/3] 8081 접속 허용
netsh http delete urlacl url=http://+:8081/ >nul 2>&1
netsh http add urlacl url=http://+:8081/ sddl=D:(A;;GX;;;WD)
echo [3/3] 방화벽 열기
netsh advfirewall firewall delete rule name="AR LAPTOP" >nul 2>&1
netsh advfirewall firewall add rule name="AR LAPTOP" dir=in action=allow protocol=TCP localport=8080,8081
echo.
echo ---- 이 컴퓨터의 IP 주소 (유니티 Server Host 에 넣을 값) ----
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /c:"IPv4"') do echo   %%a
echo.
echo 끝. 이제 서버 창을 껐다가(Ctrl+C) 서버시작.bat 을 다시 실행하세요.
pause
