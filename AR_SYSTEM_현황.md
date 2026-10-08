# AR SYSTEM — 구현 현황 요약

데스크톱 창을 AR 공중으로 꺼내 배치하는 크로스리얼리티 시스템.
학부연구생 논문(데스크톱–AR 하이브리드 환경에서 모니터 구성과 연결선이 창 배치 전략에 미치는 영향)용 실험 도구.

프로젝트 경로: `C:\Users\llouf\Documents\unity\AR SYSTEM`
최종 갱신: 2026-09-16

---

## 0. 한눈에 보기

프로그램 **셋**이 포트 **둘**로 대화한다.

| | 하는 일 |
|---|---|
| **Quest 앱** (Unity 6 + URP, Meta XR SDK) | 손 추적, 레이, AR 쿼드 배치, 30Hz 기록 |
| **PC 서버** (`PCCaptureServer`, C#/.NET 콘솔) | Win32 창 조작, 창 캡처, 기록 저장, 실험자 콘솔 |
| **실제 앱 창들** (Word, Chrome, 메모장 …) | 조작 대상 |

- **8080** — 제어. 짧은 글자 명령이 양방향으로 오감
- **8081** — 창 캡처. JPEG 프레임이 PC→Quest 단방향

핵심 분업: **Quest는 의도를 알고, PC는 권한을 가진다.** 손이 어디를 가리키는지는 Quest만 알고, 실제 창을 리사이즈할 수 있는 건 PC뿐.

---

## 1. 구현된 기능

### 1-1. 창 꺼내기 / 되돌리기

| 기능 | 조작 |
|---|---|
| 창 꺼내기 | 데스크톱 판 위에서 **주먹**(또는 핀치)으로 잡고 화면 밖으로 → 0.5초 유지 → AR 쿼드 생성 |
| 되돌리기 | AR 창을 잡은 채 데스크톱 판에 겹치기 (0.25초 디바운스) |
| **들고 있는 채로 되돌리기** | 방금 꺼낸 창을 손 안 떼고 다시 데스크톱에 대면 들어감. 다시 빼면 또 나옴 |
| 되돌린 뒤 이어서 이동 | `reinsert` 후 손을 안 떼면 그대로 데스크톱에서 창 이동 계속 |
| 창 클릭 | AR 창을 핀치 → 실제 마우스 클릭이 그 창에 들어감 |
| 창 스크롤 | AR 창을 핀치한 채 손을 위아래로 (스마트폰처럼) |

> 들고 있는 동안 그 창의 콜라이더와 핸들이 꺼진다. 안 그러면 들고 있는 창이 레이를 먼저 막아 뒤의 데스크톱 판을 못 맞힘.

### 1-2. 크기 조절 (`EdgeResizer`)

쿼드 주위에 핸들 **8개**. 평소엔 완전 투명, 레이가 근처에 가면 나타남.

| 핸들 | 색 | 동작 |
|---|---|---|
| 변 4개 (상하좌우) | 청록 | **실제 PC 창**을 리사이즈 → 앱이 리플로우 → 내용이 실제로 더 보임 |
| 꼭짓점 4개 | 주황 | **AR 쿼드만** 확대·축소 (실제 창은 안 건드림) |

- **자석**: 콜라이더는 넉넉하고(`grabPadding 0.05`) 보이는 막대는 얇음(`handleThickness 0.012`). 레이가 닿으면 끝점이 막대 중심선으로 붙음
- 핸들은 쿼드 **바깥**에 배치 (`outsideGap 0.008`) — 쿼드를 잘라 보이지 않게
- 쿼드 크기에 따라 핸들도 같이 커지고 작아짐 (`minHandleScale 0.4` ~ `maxHandleScale 1.6`)
- **클라이언트 예측**: 쿼드 모양은 즉시 바뀌고, 실제 창 내용은 뒤따라 채워짐

### 1-3. 두 손 조작 (`TwoHandGrabResize`)

- 두 손으로 잡아 **회전 / 기울이기 / 이동** — 가능
- 두 손 간격으로 **크기 변경** — **꺼둠** (`allowScale = false`). 크기는 꼭짓점 핸들 담당

### 1-4. 연결선 (실험 조건 RQ2)

**현재 방식: 꼬집기 두 번.**

| 만드는 법 | 결과 |
|---|---|
| 한 손으로 A 꼬집 → 다른 손으로 B 꼬집 | **A ——▶ B** 화살표 |
| 반대 순서로 다시 | 화살표 방향 뒤집힘 |
| 선 중앙 손잡이를 주먹으로 잡고 멀리 치우기 (0.2m) | 삭제 |

- A, B는 **AR 창** 또는 **데스크톱 모니터 판** 둘 다 가능
- 데스크톱이 낀 선 = **청록**, 창끼리 = **보라**
- 모니터끼리는 안 이어짐
- 연결 제스처로 판정되면 그 손의 클릭/마우스다운은 **취소**됨
- 선 삭제 시 꺾이지 않고 **직선 그대로** 평행이동

> **삭제된 것**: 데스크톱 가장자리의 상호작용 직육면체(`DesktopTetherSpawner`), 창 꺼낼 때 자동으로 붙던 `WindowTether`. 둘 다 지금은 안 씀.

### 1-5. 손 조작 토글 (`HandInteractionToggle`)

- **박수** 한 번 → 손 조작 전체 on/off (토글)
- 오작동 방지 3중 장치:
  - 두 손 모두 `HandConfidence.High`
  - 트래킹 재획득 후 `0.35초` 안정화
  - **접근 속도** `0.45 m/s` 이상 — 그냥 가까이 있는 것만으론 안 됨

### 1-6. 실험 조건 (2×2)

| 코드 | 모니터 | 선 |
|---|---|---|
| `M1L0` | 1대 | 없음 |
| `M1L1` | 1대 | 있음 |
| `M2L0` | 2대 | 없음 |
| `M2L1` | 2대 | 있음 |

**씬은 하나.** 스위치 두 개(`MonitorCount`, `LinksEnabled`)만 바꾼다.

**고르는 법 — 유니티 상단 `실험` 메뉴**
```
실험 ▸ 조건 ▸ 모니터 1 - 선 없음  (M1L0)
             모니터 1 - 선 있음  (M1L1)
             모니터 2 - 선 없음  (M2L0)
           ✓ 모니터 2 - 선 있음  (M2L1)
      ▸ 조건 창 열기            ← 버튼 4개짜리 창, Play 중에도 즉시 전환
      ▸ 공중의 창 전부 되돌리기
```
고른 값은 `EditorPrefs`에 저장 → Play 눌러도, 유니티 껐다 켜도 유지.

**조건이 바뀌면 일어나는 일**
1. 공중의 창 전부 데스크톱으로 되돌림
2. 안 쓰는 모니터 쿼드는 **GameObject째로 끔** (Renderer는 절대 안 건드림 — 투명 메쉬 유지)
3. 선 기능 on/off (`QuadLinkManager.enabled`, 기존 선 정리)
4. **기록 파일을 새로 엶** → 조건마다 파일이 따로 남음

`MonitorHandCalibrator`도 조건이 정한 개수까지만 슬롯을 봄 — M1 조건이면 두 번째 모니터를 잡으라고 요구하지 않음.

### 1-7. 기록 (`SessionRecorder`)

**30Hz**로 좌표만 적는다. 영상이 아님.

| 레코드 | 내용 |
|---|---|
| `meta` | 참가자, 조건 코드, 모니터 수, 선 유무, 세션 시각, `pixelsToMeters` |
| `mon` | 모니터 판 위치·회전·크기. **움직이면 다시 적음** (공간 앵커가 늦게 붙어서) |
| `monoff` | 조건이 바뀌어 꺼진 모니터 |
| `win` / `gone` | 창 생성(짧은 번호 부여) / 데스크톱 복귀 |
| `wtitle` | 실제 창 제목 (PC가 보냄) |
| `f` | 프레임: 머리(pos+euler) + 양손(pos+dir+pinch) + 모든 창(pos/rot/size) |
| `lk` | 선 생성/삭제/방향뒤집기. 끝점: 창 = 0,1,2… / 모니터 = **-1, -2** |
| `ev` | 클릭·스크롤·리사이즈·박수 토글·표시·메모 |
| `task` | 태스크 시작/종료 + 요약(걸린 시간, 이동거리, 인터랙션 수) |
| `desk` | 데스크톱 창들의 픽셀 사각형 (PC가 0.5초마다) |

**저장 위치**
```
PCCaptureServer/replay/<참가자>/<참가자>_<조건>_<시각>.jsonl
헤드셋:  Application.persistentDataPath/replay/<참가자>_<조건>_<시각>.jsonl  (백업)
```
JSONL(한 줄에 JSON 하나) — 크래시가 나도 그때까지 적힌 줄은 전부 유효.

### 1-8. 리플레이 뷰어 (`replay/replay.html`)

브라우저에서 여는 HTML 한 장. 설치 불필요.

- `session.jsonl`을 **끌어다 놓으면** 3D로 재생
- **유튜브식 재생바**: 클릭/드래그로 이동, 호버하면 시각 + 그 지점 사건 이름, 사건마다 컬러 눈금, 태스크 구간은 띠
- **시점 이동 목록** — 클릭하면 그 순간으로 점프. 지금 지나는 항목 강조
- 카메라: **정면 / 위 / 옆 / 참가자 시점**. 마우스로 자유 회전·확대
- 궤적 / 손 / 데스크톱 표시 토글
- 속도 0.5× ~ 4×
- 키보드: `Space` 재생, `←→` 5초, `,` `.` 이전·다음 시점, `Home` 처음
- 우측 패널: 열린 창 수, 연결선 수, 총 이동거리, 창별 **모니터 기준 좌표**(cm)
- 카메라는 **창 + 머리**의 바운딩 박스에 자동으로 맞춤. "정면"은 참가자가 평균적으로 바라보던 방향 기준

### 1-9. PC 서버

| 기능 | 방법 |
|---|---|
| 창 캡처 | `PrintWindow` + `PW_RENDERFULLCONTENT` (가려져 있어도 찍힘), 33ms마다, JPEG 품질 95 |
| 창 숨기기 | 레이어드 윈도우(투명+클릭통과). 화면 밖으로 **안 옮김** — 크로미움이 화면 밖 창을 렌더링 안 해서 |
| 창 리사이즈 | `SetWindowPos` + `SWP_NOSENDCHANGING` (앱의 크기 제한 거부를 건너뜀) |
| 창 클릭 | 실제 커서 이동 + `mouse_event`. 화면 밖이면 잠깐 안으로 당겨서 클릭 후 복원 |
| 스크롤 | 실제 커서로 휠 (Word가 `PostMessage`를 무시함). 알파 1로 잠깐 보이게 하고 0.4초 뒤 복원 |
| 종료 시 복원 | `GetWindowPlacement`로 서버 실행 전 상태 기억 → Ctrl+C에 전부 원상복구 |
| 플리커 제거 | 검은 픽셀 비율이 갑자기 80% 넘는 프레임은 버림 (최대 30연속) |
| LAN 접속 | `http://+:port/` 우선, 안 되면 localhost. 시작 시 쓸 수 있는 IP 출력 |

**서버 명령 (Unity→PC)**
```
down / move / up            핀치 마우스 드래그
gdown / gmove / gup         주먹 창 이동
eject / geject              창 꺼내기      → 답장: spawn,id,w,h
insert,id                   되돌리기
reinsert,id,u,v,mon         들고 있는 채로 되돌리기
wclick,id,u,v               AR 창 클릭
wscroll,id,u,v,delta        AR 창 스크롤
wresize,id,pxW,pxH          실제 창 리사이즈
srec,<줄들>                  기록 묶음 저장
study,...                   (PC→Unity) 실험 진행 명령
```

### 1-10. 실험자 콘솔 (서버 창에 타이핑)

```
p0        참가자 P0 설정 (p07, p P07 도 됨) — 새 파일이 열림
t1        태스크 1 시작 (t2, t3 …)
p0 t1     한 줄에 붙여 쳐도 됨
Ctrl+C    종료 — 태스크 마감 + 저장 + 창 복원
x         공중의 창 전부 데스크톱으로
m 라벨     영상 동기화 표시 (unix_ms 기록)
?         도움말
```

- 태스크 마감 시점: `t2` 입력 / `p` 입력 / **Ctrl+C**
- 참가자는 **콘솔에서 친 값이 최우선** (헤드셋 기본값 `P00`이 못 덮어씀)
- 파일 경로가 콘솔에 바로 표시됨
- 헤드셋 미연결 시 경고 출력

---

## 2. 파일 지도

### Unity — `Assets/Scripts/`

| 파일 | 역할 |
|---|---|
| `HandRayPointer.cs` | **핵심.** 손 레이, 히트 판정, 상태 머신(`else if` 체인 = 우선순위) |
| `SimpleRayPointer.cs` | 컨트롤러용 대응판 |
| `EdgeResizer.cs` | 쿼드 주변 핸들 8개 |
| `WindowEdgeHandle.cs` / `TetherHandle.cs` / `DesktopEdgeHandle.cs` | 핸들 마커 |
| `TwoHandGrabResize.cs` | 두 손 회전·기울이기·이동 |
| `HandInteractionToggle.cs` | 박수 토글 |
| `QuadLink.cs` / `QuadLinkManager.cs` | 연결선 + 꼬집기 제스처 판정 |
| `ArrowLine.cs` | 화살촉 원뿔 메시 생성 |
| `ITetherCarry.cs` | 선 두 종류의 "잡아서 치우기" 공통 인터페이스 |
| `ConditionManager.cs` | 2×2 조건 적용 |
| `Editor/ConditionMenu.cs` | 유니티 `실험` 메뉴 + 조건 창 |
| `ConditionPanel.cs` | 헤드셋 내 조건 패널 (기본 **꺼짐**) |
| `SessionRecorder.cs` | 30Hz 기록 |
| `WindowQuadReceiver.cs` | AR 창 쿼드 하나 |
| `WindowCaptureHub.cs` | 8081 클라이언트, ID별 프레임 분배, 더블 버퍼링 |
| `PCStreamReceiver.cs` | 8080 클라이언트 |
| `MonitorPlane.cs` | 이 판이 몇 번 모니터인지 |
| `MonitorHandCalibrator.cs` | 손으로 모니터 위치 잡기 + 공간 앵커 저장/복원 |
| `MonitorAnchorCalibrator.cs` / `MonitorScreenAnchor.cs` | MRUK 앵커 보조 |
| `DesktopTetherSpawner.cs` | **사용 안 함** (자동 생성 제거). `MakeUnlit` 헬퍼만 쓰임 |
| `WindowTether.cs` | **사용 안 함** (자동 부착 제거) |
| `StudyLogger.cs` / `StudyRecorder.cs` | **비어 있음** — 지워도 됨 |

### PC — `PCCaptureServer/`

| 파일 | 역할 |
|---|---|
| `Program.cs` | 웹소켓 2개 + Win32 + 캡처 + 기록 + 실험자 콘솔. 전부 여기 |
| `replay/replay.html` | 리플레이 뷰어 |
| `replay/<참가자>/*.jsonl` | 기록 |

---

## 3. 주요 설정값

### `HandRayPointer`
| 설정 | 값 | 뜻 |
|---|---|---|
| `pinchThreshold` | 0.7 | 핀치 인식 강도 |
| `grabThreshold` | 0.4 | 주먹 인식 강도 (낮을수록 민감) |
| `dragDelay` | 0.5초 | 화면 밖 유지 → eject |
| `reinsertDelay` | 0.25초 | 되돌리기 디바운스 |
| `raySmoothing` | 14 | 손 떨림 보정 |
| `holdDistance` | 0.8m | 꺼낸 창을 드는 거리 |
| `pixelsToMeters` | 0.0005 | 1px = 0.5mm |
| `scrollPixelsPerNotch` | 60 | 스크롤 한 칸 |
| `sendRate` | 60Hz | 명령 전송 |

### `EdgeResizer`
`handleThickness 0.012` · `cornerLength 0.026` · `outsideGap 0.008` · `grabPadding 0.05` · `idleAlpha 0` · `hoverAlpha 0.75` · `minSize 0.15m` · `maxSize 6m`

### `HandInteractionToggle`
`clapDistance 0.09m` · `releaseDistance 0.30m` · `minApproachSpeed 0.45m/s` · `trackingSettleTime 0.35초` · `cooldown 0.5초`

### `WindowCaptureHub` (화질)
`useMipmaps true` · `anisoLevel 8` · `mipMapBias -0.75` · `textureFilter Trilinear` · 텍스처 2장 더블 버퍼링

### `SessionRecorder`
`recordHz 30` · `rescanInterval 0.2초` · `writeLocalFile true` · `forwardToPC true`

### 서버
`JpegQuality 95` · 캡처 33ms · `ScrollIdleMs 400` · `ScrollWithRealCursor true` · 데스크톱 창 샘플링 500ms

---

## 4. 실행 순서

1. **PC**: `cd PCCaptureServer && dotnet run` → 콘솔에 IP·도움말 출력
2. **Unity**: `실험 ▸ 조건`에서 조건 선택 → **Play** (APK면 `PCStreamReceiver.serverHost`에 PC의 LAN IP)
3. **캘리브레이션**: 처음 한 번만. 왼주먹 쥠 → 핀치로 모서리 → 손 빼서 크기 → 기울여 각도 → 주먹 펴서 확정. 이후 자동 복원
4. **콘솔**: `p0` → `t1` → 수행 → `t2` … → `Ctrl+C`
5. **분석**: `replay/replay.html` 더블클릭 → `.jsonl` 끌어다 놓기

---

## 5. 알려진 한계 / 주의

- **앱이 스스로 거는 크기 제한**은 `SWP_NOSENDCHANGING`으로도 못 넘음. DLL 인젝션이 필요한데 실험 도구로는 과함 → 앱을 골라 쓰는 게 현실적
- **해상도 상한 = 원본 창 픽셀 수**. 더 선명하게 보려면 앱에서 Ctrl+ 로 확대한 뒤 꼭짓점으로 쿼드를 줄이는 조합이 가장 효과적
- 스크롤은 **실제 커서를 0.4초 빌림**
- 서버는 **시작할 때 한 번만** 모니터를 셈 → 실행 중 디스플레이 구성을 바꾸면 재시작 필요
- 조건은 **Play 전에** 고르는 게 안전 (실행 중 1대→2대 전환 시 두 번째 모니터 앵커가 아직 로드 안 됐을 수 있음)
- 모니터 1대 조건에서도 **실제 두 번째 모니터는 책상에 남아 있음**. 물리적으로 끄거나 `ConditionManager.maskUnusedMonitors`를 켜면 불투명 판으로 덮임 (기본 꺼짐)
- `SessionRecorder`가 0.2초마다 씬 전체를 `FindObjectsByType`으로 훑음. 창이 수십 개가 되면 비용이 보일 수 있음 (지금 규모는 괜찮음)
- 기록 형식을 바꾸면 `replay.html`의 `ingest()`도 같이 고쳐야 함. **이 둘만 짝**

---

## 6. 코드 수정할 때 규칙

- **새 상호작용 추가** → `HandRayPointer`의 `else if` 체인에서 **어느 위치에 넣을지**부터 정한다. 위일수록 다른 것을 이김
- **새 매니저** → `[RuntimeInitializeOnLoadMethod]`로 자동 생성. 씬 파일을 건드리지 않는다
- **Awake에서 남의 값을 읽지 않는다.** 부트스트랩 순서는 보장 안 됨. 읽어야 하면 `Start`에서
- **서버에 새 명령 추가** → 값에 쉼표가 들어갈 수 있으면 `Split(',')` 앞에서 `StartsWith`로 먼저 거른다 (`srec`가 그 예)
- **모니터 판의 `Renderer`는 건드리지 않는다.** 투명 메쉬로 세팅돼 있음. 끄려면 GameObject째로

---

## 7. 해결된 주요 버그 (재발 방지용)

| 증상 | 원인 | 해결 |
|---|---|---|
| 비율이 엉망 | 최대화 해제 후 크기가 바뀌기 전에 읽어 보냄 | eject에선 창을 안 건드림. 해제를 리사이즈 시점으로 |
| 꼭짓점이 실제 창을 리사이즈 | 변·꼭짓점이 같은 함수를 탐 | `if (isCorner) return;` |
| 박수 토글이 시야 밖에서 재무장 | 트래킹 끊길 때 손 위치가 순간 겹침 | 접근 **속도** 조건 추가 |
| 격자무늬가 고개 따라 움직임 | 선명도 위해 밉맵을 껐던 것 = 모아레 | 밉맵 다시 켜고 `mipMapBias -0.75` |
| 창이 가로로 어긋남 | GPU가 읽는 중에 텍스처 덮어씀 | 더블 버퍼링 |
| 들고 있는 채로 되돌리기 안 됨 | 들고 있는 창이 레이를 먼저 막음 | 드는 동안 콜라이더·핸들 끔 |
| 긴 기록 메시지가 깨짐 | 웹소켓 `EndOfMessage` 미확인 (버퍼 1024) | `MemoryStream`으로 조각 조립 |
| replay에서 창과 모니터가 따로 놈 | 공간 앵커가 늦게 붙는데 `mon`을 Start에 한 번만 적음 | 바뀌면 다시 적고, 뷰어는 마지막 것 사용 |
| 창 이름이 "창 #1"로만 나옴 | `wtitle`이 `win`보다 먼저 와서 버려짐 | 제목을 따로 모았다가 마지막에 붙임 |
| 참가자가 P00으로 들어감 | 헤드셋 meta가 콘솔 입력을 덮어씀 | `recParticipantPinned` — 콘솔 값이 최우선 |

---

## 8. 아직 안 한 것

- 새 기록으로 리플레이 검증 (모니터 위치·창 이름·유령 판 수정 후)
- 분석 스크립트 (`summary.csv`, 배치 그림) — 이전 버전은 롤백됨
- `StudyLogger.cs` / `StudyRecorder.cs` 빈 파일 삭제
- APK 배포 테스트 (LAN IP 설정 필요)
