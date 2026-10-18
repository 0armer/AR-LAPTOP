@echo off
chcp 65001 >nul
setlocal
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.3.14f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
echo ============================================
echo   AR LAPTOP 무선 연결 (USB 없이 쓰기)
echo ============================================
echo.
echo 준비: 퀘스트를 USB로 꽂고, 헤드셋을 쓰거나 깨워두세요.
echo       퀘스트와 이 컴퓨터는 같은 공유기에 연결돼 있어야 합니다.
pause
echo.
echo [1/5] USB로 퀘스트 확인
"%ADB%" get-state 1>nul 2>nul
if errorlevel 1 (
  echo [오류] USB로 퀘스트가 안 잡힘. 케이블 확인 / 헤드셋에서 USB 디버깅 허용
  pause & exit /b 1
)
echo.
echo [2/5] 퀘스트 와이파이 IP 찾기
set "QIP="
for /f "tokens=9" %%a in ('"%ADB%" shell ip route ^| findstr wlan0 ^| findstr src') do set "QIP=%%a"
if "%QIP%"=="" (
  echo [오류] 퀘스트가 와이파이에 연결돼 있지 않음. 퀘스트 설정에서 와이파이 연결 후 다시 실행
  pause & exit /b 1
)
echo   퀘스트 IP = %QIP%
echo.
echo [3/5] 무선 모드 켜기
"%ADB%" tcpip 5555
timeout /t 3 >nul
echo.
echo ============================================
echo   이제 USB 케이블을 뽑고 아무 키나 누르세요
echo ============================================
pause >nul
echo.
echo [4/5] 와이파이로 연결: %QIP%:5555
"%ADB%" connect %QIP%:5555
timeout /t 2 >nul
"%ADB%" connect %QIP%:5555
"%ADB%" -s %QIP%:5555 get-state 1>nul 2>nul
if errorlevel 1 (
  echo.
  echo [실패] 와이파이로 퀘스트에 연결 못 함.
  echo   - 헤드셋이 잠들지 않게 쓰고 있는지
  echo   - 퀘스트와 컴퓨터가 같은 공유기인지 (게스트 와이파이 X)
  echo   - 안 되면 휴대폰 핫스팟에 둘 다 연결하고 다시 실행
  pause & exit /b 1
)
echo.
echo [5/5] 서버 통로 연결 (8080, 8081)
"%ADB%" -s %QIP%:5555 reverse tcp:8080 tcp:8080
"%ADB%" -s %QIP%:5555 reverse tcp:8081 tcp:8081
"%ADB%" -s %QIP%:5555 reverse --list
echo.
echo 성공! 위에 8080, 8081 두 줄이 보이면 됩니다.
echo 이제 서버시작.bat 을 켜고 퀘스트에서 AR LAPTOP 을 실행하세요.
echo (헤드셋을 재부팅하면 이 파일을 USB 꽂고 다시 실행)
pause
