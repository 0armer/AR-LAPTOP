# 가장자리(한 변) 리사이즈 — 추가 기능

기존 `TwoHandGrabResize`(양손 = 균일 스케일, **비율 고정**)에 더해,
**한 변만 잡아 늘려서 가로세로 비율 자체를 바꾸는** 리사이즈를 추가했습니다.
꺼내 놓은 창의 네 변에 청록색 막대(핸들)가 생기고, 그 막대를 잡아 끌면 그 축만 늘어납니다.
(요청대로 **꼭짓점 핸들은 없음** — 변 4개만)

## 파일

넣을 위치는 전부 `Assets/Scripts/`.

**새 파일 (2개)**
- `EdgeResizer.cs` — 핵심. 핸들 생성 + 한 축 리사이즈 + PC로 `wresize` 전송
- `WindowEdgeHandle.cs` — 각 핸들 막대에 붙는 표식(owner + edge)

**교체 파일 (4개)** — 기존 것을 덮어씀 (내 추가분만 들어가 있고 나머지는 그대로)
- `WindowQuadReceiver.cs` — 창이 등록될 때 `EdgeResizer`도 자동 부착 (프리팹/씬 수정 불필요)
- `HandRayPointer.cs` — 손 핀치로 핸들 잡아 리사이즈 연결
- `SimpleRayPointer.cs` — 컨트롤러 트리거로 리사이즈 연결
- `PCCaptureServer/Program.cs` — **`wresize` 핸들러 추가 완료** (실제 창 리사이즈 + 리플로우 스트리밍)

> 프리팹·씬은 건드릴 필요 없습니다. `WindowQuadReceiver`가 `TwoHandGrabResize`를 자동으로 붙이던 것처럼 `EdgeResizer`도 자동으로 붙입니다.

## 조작 방법

- **손 트래킹**: 창 가장자리 막대를 **검지 핀치**로 잡고 끌기 → 그 변만 늘어남
- **컨트롤러**: 막대를 **검지 트리거**로 잡고 끌기
- 반대쪽 변은 그 자리에 고정되고, 잡은 쪽만 늘어납니다.
- 창 몸통(가운데)을 핀치/주먹으로 잡는 기존 동작(클릭 / 통째 이동 / 양손 균일 스케일)은 그대로입니다.

## 동작 원리 (중요)

늘리면 두 가지가 동시에 일어납니다:

1. **쿼드 지오메트리가 즉시 반응** (`localScale`의 한 축) → "공중에서 늘어나는" 즉각적인 느낌 (클라이언트 예측)
2. **PC로 `wresize,<id>,<pxW>,<pxH>` 전송** → 실제 창을 그 크기로 리사이즈 → 앱이 리플로우 → 새 프레임이 `WindowCaptureHub`로 스트리밍되어 채워짐

즉 늘리는 순간엔 (기존 텍스처가 늘어난) 예측 화면이 잠깐 보이다가, 곧 **진짜 리플로우된 내용**으로 바뀝니다.

`pixelsToMeters` 값은 창을 스폰할 때 쓰는 값(현재 **0.0005**)과 **같게** 맞춰야 픽셀 크기 계산이 정확합니다. (`EdgeResizer`의 인스펙터 필드로 노출돼 있음)

## PC 서버(PCCaptureServer / Program.cs) — `wresize` 추가 완료

이미 반영해 뒀습니다. 서버가 `windowId = hwnd.ToInt64()`를 쓰고 있어서 `id → HWND`는 `new IntPtr(id)`로 바로 되고, `SetWindowPos`도 이미 선언돼 있었습니다. 추가된 것:

- `HandleMessage`에 `"wresize,id,pxW,pxH"` 케이스 (wclick/reinsert 옆)
- `WindowResize(hwnd, pxW, pxH)` 메서드 — `SetWindowPos(..., SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS)`로 좌상단 고정, 오른쪽/아래로 리사이즈

캡처 루프(`CaptureAndSend`)가 매 프레임 `GetWindowRect`로 크기를 다시 읽어 `PrintWindow`로 찍으므로, 리사이즈되면 **리플로우된 새 크기 프레임이 그대로 AR로 스트리밍**됩니다. **서버를 다시 빌드/실행**하기만 하면 됩니다.

### 참고 / 다음 단계
- 위 스니펫은 `SWP_NOMOVE`라 **좌상단 위치는 고정, 오른쪽·아래로 커집니다.** 특정 방향(예: 왼쪽)으로 늘리고 싶으면 `SWP_NOMOVE`를 빼고 새 `X,Y`도 같이 계산해 주면 됩니다.
- 모니터보다 크게 만들려면(오프스크린), 창을 화면 밖 좌표로 옮긴 뒤 리사이즈해도 됩니다. `Windows.Graphics.Capture`는 화면 밖 부분도 캡처하므로 AR엔 온전히 보입니다.
- 클릭할 때 그 지점이 화면 밖이면 실제 커서가 못 닿으므로, 서버가 `wclick` 직전에 해당 지점이 데스크탑 안에 들어오도록 창을 잠깐 이동(auto-pan)시키는 처리를 넣으면 우리가 얘기한 "자동" 버전이 됩니다.
- 원하면 서버가 리사이즈 후 실제 적용된 크기를 `wsize,<id>,<w>,<h>`로 되돌려 보내고, 유니티에서 쿼드 `localScale`을 그 값에 맞춰 보정할 수도 있습니다(예측/실제 오차 제거). 지금은 없어도 동작합니다.

## 조정 가능한 값 (EdgeResizer 인스펙터)
- `pixelsToMeters` (0.0005) — 스폰 값과 일치
- `minSize` / `maxSize` (m) — 늘릴 수 있는 범위
- `sendRate` (30) — 드래그 중 PC 전송 빈도
- `handleThickness` / `handleDepth` / `handleColor` — 막대 외형
