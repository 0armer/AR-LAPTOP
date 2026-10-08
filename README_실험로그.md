# 실험 데이터 측정 (Study Logging)

교수님이 말씀하신 것들을 자동으로 남기게 만든 것.

| 필요한 것 | 어디서 나오나 |
|---|---|
| 걸린 시간 | `events.jsonl`의 `task_end` → `duration_s` |
| 인터랙션 수 | `task_end`의 `move_episodes`, `resize_episodes`, `windows_ejected`, `links_created/removed/flipped` |
| 윈도우를 얼마나 움직였는지 | `total_path_m` (총 이동 거리, m) + 창마다 `path_m` |
| 선을 얼마나 수정했는지 | `link_create` / `link_remove` / `link_flip` 이벤트 |
| 언제 어디서 윈도우를 뽑아냈는지 | `pc_eject` (PC 좌표·창 제목·픽셀 크기) + `window_appear` (AR 공간 좌표) |
| 각 윈도우의 위치·사이즈 | `snapshots.csv` (0.1초마다 전부) + `desktop_windows.csv` (데스크탑 쪽) |
| 사람들이 구성했던 것 그대로 | `layout` 이벤트 = 태스크 끝나는 순간의 배치 전체 |

---

## 1. 파일 구성

실행하면 **PC의 `PCCaptureServer/StudyLogs/<시작시각>/`** 아래에 쌓인다.
헤드셋 안에도 같은 게 저장되지만(`Application.persistentDataPath/StudyLogs/`),
PC 쪽을 쓰면 adb로 뽑을 필요가 없다.

```
StudyLogs/20260911_143000/
  events.jsonl          "언제 무슨 일이" — 한 줄에 이벤트 하나 (JSON)
  snapshots.csv         0.1초마다 모든 AR 창 + 머리의 위치/회전/크기
  desktop_windows.csv   0.25초마다 데스크탑에 있는 창들의 위치/크기 (PC 픽셀 좌표)
```

### snapshots.csv 열

```
p          참가자
t          세션 시작부터의 초
task       태스크 이름
kind       head | quad
id          창 ID (HWND)
pos_x/y/z  유니티 월드 좌표
rot_x/y/z  오일러 각
size_w_m   창 가로 (m)        size_h_m 세로 (m)
size_w_px  원본 창 픽셀 가로   size_h_px 세로
mon_x/y/z  ★ 주 모니터를 원점으로 한 좌표 — 참가자마다 플레이 공간 원점이 달라도
           이 좌표로는 바로 비교·평균을 낼 수 있다. 분석은 이걸 쓰는 게 맞다.
head_dist  머리에서의 거리 (m)
head_az    좌우각 (도, + = 오른쪽)
head_el    상하각 (도, + = 위)
moved_m    그 창의 누적 이동 거리
held       지금 조작 중인지 (1/0)
```

### 주요 이벤트 종류

| type | 뜻 |
|---|---|
| `session_start` / `task_start` / `task_end` | 세션·태스크 경계. `task_end`에 요약이 전부 들어 있다 |
| `window_appear` / `window_disappear` | AR에 창이 생김 / 데스크탑으로 되돌아감 |
| `window_move` | 한 번의 이동 조작 (from/to/거리/회전각/걸린 시간) |
| `window_resize_ar` | 꼭짓점으로 쿼드 크기를 바꾼 조작 |
| `pc_eject` / `pc_insert` / `pc_resize` | PC 쪽에서 본 같은 사건. **창 제목과 픽셀 사각형이 여기 있다** |
| `link_create` / `link_remove` / `link_flip` | 선(화살표) 생성·삭제·방향 뒤집기 |
| `hand_toggle` | 박수로 손 조작 껐다 켬 |
| `layout` | 그 순간의 배치 전체 (창 목록 + 선 목록) |
| `mark` / `note` | 실험자가 직접 찍은 표시·메모 |

---

## 2. 실험 진행 방법

**서버 콘솔에 그냥 타이핑하면 된다.** 헤드셋을 벗을 필요 없음.

```
p P07            참가자 번호 설정 (로그의 모든 줄에 P07이 붙는다)
c hybrid         조건 설정 (baseline / hybrid 등)
task 1           태스크 1 시작  ← 여기서부터 시간이 재진다
end              태스크 종료    ← 요약 한 줄 + 배치 스냅샷이 자동으로 남는다
layout           지금 배치를 통째로 기록 (중간에 한 번 더 찍고 싶을 때)
mark rec_start   영상 동기화용 표시
note 참가자가 헤맴   자유 메모
where            로그 폴더 경로 출력
```

전형적인 진행:

```
p P07
c hybrid
task 1
   … 참가자 수행 …
end
task 2
   …
end
```

`task 2`를 치면 태스크 1은 자동으로 종료된다. 빼먹어도 괜찮음.

---

## 3. 분석

```
cd PCCaptureServer
python analyze_study.py StudyLogs
```

`StudyLogs/<세션>/analysis/` 안에:

- **`summary.csv`** — 참가자 × 태스크 한 줄 요약. 논문 표에 바로 들어갈 수 있는 형태.
- **`final_layouts.csv`** — 태스크 끝난 순간 각 창이 어디에 어떤 크기로 있었는지.
- **`final_links.csv`** — 그때 어떤 창이 어떤 창과 이어져 있었는지.
- **`events_flat.csv`** — 이벤트 전체를 표로 편 것. 더 파보고 싶을 때.
- **`layout_<참가자>_<태스크>_task_end.png`** — 최종 배치 그림 (정면도 + 평면도).

`matplotlib`이 있으면 그림까지, 없으면 CSV만 만든다 (`pip install matplotlib`).

---

## 4. 동영상

코드로는 안 하는 게 낫다. 이미 있는 걸 쓰는 게 화질도 안정성도 낫다.

**추천: Meta Quest Developer Hub (MQDH)**
Device Manager → 기기 선택 → *Record Video*.
패스스루가 그대로 찍히니까 **실제 모니터와 AR 창이 한 화면에 같이 나온다** — 논문 그림/영상에 제일 좋다. 파일이 PC로 바로 떨어진다.

**헤드셋 단독**
유니버설 메뉴 → 카메라 → 녹화. 나중에 `adb pull /sdcard/Oculus/VideoShots/ .`

**adb**
```
adb shell screenrecord --bit-rate 12000000 --time-limit 180 /sdcard/rec.mp4
adb pull /sdcard/rec.mp4
```

**PC 화면도 같이 찍기**
데스크탑 쪽에서 무슨 일이 있었는지 보려면 OBS로 PC 화면을 따로 녹화해서 나란히 붙이면 된다.

### 영상과 로그 시간 맞추기

녹화를 시작한 직후에 **박수를 한 번 치면** 된다.
- 영상: 손뼉 치는 게 보인다
- 로그: `hand_toggle` 이벤트가 그 시각에 찍힌다

더 확실하게 하려면 서버 콘솔에 `mark rec_start`를 치면 되는데,
이 이벤트에는 `unix_ms`(절대 시각)가 들어가 있어서 영상 파일의 생성 시각과 바로 맞출 수 있다.

> IRB: 참가자 녹화는 동의서에 들어가 있어야 한다. 패스스루 영상에는 실험실 배경과
> 참가자의 손이 찍히니까 보관·삭제 계획도 같이 적어두는 게 좋다.

---

## 5. 설치

**Unity** — `Assets/Scripts/`에 넣으면 끝. 씬에 뭘 붙일 필요 없다 (실행 시 자동 생성됨).

- `StudyLogger.cs` (새 파일)
- `StudyRecorder.cs` (새 파일)
- `PCStreamReceiver.cs` (수정 — 실험 명령 수신 + 연결 상태 확인)

**서버** — `PCCaptureServer/`

- `Program.cs` (수정 — 로그 파일 쓰기, 실험자 콘솔, 데스크탑 창 샘플러, 긴 메시지 조립 수정)
- `analyze_study.py` (새 파일)

인스펙터에서 바꿀 만한 것:
- `StudyLogger.snapshotHz` — 기본 10Hz. 더 촘촘히 보고 싶으면 20, 파일 줄이려면 5.
- `StudyLogger.forwardToPC` — 끄면 헤드셋 안에만 저장.
- `StudyRecorder`의 `moveThreshold` / `settleTime` — "한 번의 조작"을 어디서 끊을지.
  기본값은 0.35초 멈추면 한 번의 이동이 끝난 걸로 본다.

---

## 6. 주의

- 참가자마다 `p P07`을 **먼저** 치고 시작할 것. 안 치면 전부 `P00`으로 들어간다.
- 서버는 Ctrl+C로 끄기. 그래야 로그가 flush되고 창들도 원래대로 복원된다.
- `snapshots.csv`는 커진다: 창 5개 × 10Hz ≈ 1분에 3천 줄(약 300KB). 1시간이면 20MB 정도.
  부담되면 `snapshotHz`를 5로 낮추면 된다.
