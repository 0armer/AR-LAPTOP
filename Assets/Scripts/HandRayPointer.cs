using System.Collections.Generic;
using UnityEngine;

// SimpleRayPointer의 손 트래킹 버전.
// 검지 핀치(pinch)      = 실제 마우스 클릭/드래그 (down/move/up, 기존 트리거 역할)
// 주먹 쥐기(fist/grab)  = 데스크탑 화면(투명판) 위에서는 창을 "통째로" 옮기는 전용 메시지(gdown/gmove/gup)를
//                         보낸다. 타이틀바를 정확히 잡을 필요 없이 창 몸통 아무데나 주먹 쥐면 그 창 전체가
//                         손 이동량만큼 따라온다 (PCCaptureServer의 Program.cs에서 SetWindowPos로 처리).
//                         꺼내 놓은 창 위에서는 그 창을 잡아 이동 + 손 전체 회전(롤 포함)을 그대로 따라 회전.
//                         양손이 동시에 같은 창을 쥐면, 각 손이 처음 맞춘 지점(앵커)을 그 손과 고정
//                         거리로 계속 붙잡고 있다고 보고, 두 앵커의 현재 3D 위치로 TwoHandGrabResize가
//                         크기(앵커 사이 거리 변화)와 회전+위치(앵커 두 점을 잇는 축 정렬)를 계산한다.
// eject(화면 밖으로 끌고 나가 AR로 꺼내기)는 핀치/주먹 드래그 둘 다 가능. 핀치로 나가면 "eject",
// 주먹으로 나가면 "geject"를 보내서 서버가 어떤 창 핸들을 꺼낼지 구분한다.
//
// 컨트롤러 없이 왼손/오른손에 각각 하나씩 붙여서 쓴다 (handId를 "L"/"R"로 구분).
[RequireComponent(typeof(LineRenderer))]
public class HandRayPointer : MonoBehaviour
{
    [Header("이 레이가 속한 손")]
    public OVRHand hand;                    // Inspector에서 Left/RightHandAnchor 밑 OVRHand 연결
    public Transform pointerPoseFallback;   // hand.PointerPose를 못 쓸 때 대신 쓸 트랜스폼(비워두면 이 오브젝트 자신)
    [Tooltip("주먹(grab) 판정용. hand와 같은 오브젝트에 있는 OVRSkeleton을 연결하면 손가락이 실제로 얼마나 " +
             "말려있는지(curl)로 판단한다. 비워두면 예전처럼 핀치 강도로 대체 판단(정확도 낮음).")]
    public OVRSkeleton skeleton;

    [Header("손 식별자 (양손 동시 잡기 구분용, L 또는 R)")]
    public string handId = "R";

    [Header("레이캐스트")]
    public float length = 10f;
    public float width = 0.003f;
    public Color idleColor = Color.white;
    public Color clickColor = Color.green;
    public LayerMask hitLayers = ~0;

    [Header("핀치 / 주먹 민감도 (0~1, 높일수록 더 확실히 쥐어야 인식됨)")]
    public float pinchThreshold = 0.7f;
    public float grabThreshold = 0.4f;   // 낮출수록 주먹 인식이 더 민감(살짝만 쥐어도 잡힘)

    [Tooltip("주먹 신호가 잠깐(트래킹 흔들림, 손이 카메라 시야를 벗어나거나 다른 손에 가려짐 등)으로 끊겨도 " +
             "이 시간 안에 돌아오면 계속 잡고 있는 것으로 취급 (살짝씩 놓치는 것 방지). 양손으로 같은 창을 잡을 때는 " +
             "손끼리 가까워지면서 서로 가리는 경우가 잦으니 여유를 넉넉히 두는 게 좋다.")]
    public float grabReleaseGrace = 0.6f;

    [Tooltip("한번 grab으로 잡힌 뒤에는 문턱값을 이 비율만큼 낮춰서 유지한다 (작을수록 잘 안 풀림). 0.6이면 원래 문턱값의 60%만 넘어도 계속 grab 유지.")]
    [Range(0.1f, 0.9f)]
    public float grabHoldRatio = 0.25f;

    [Header("주먹 판정용 손가락 말림(curl) 기준 - skeleton 연결했을 때만 사용")]
    [Tooltip("손가락을 쫙 폈을 때, 손끝-손목 거리(미터, 평균 손 크기 기준). 이보다 멀면 curl=0")]
    public float curlOpenDistance = 0.16f;
    [Tooltip("주먹을 완전히 쥐었을 때, 손끝-손목 거리(미터, 평균 손 크기 기준). 이보다 가까우면 curl=1")]
    public float curlClosedDistance = 0.07f;

    [Header("손가락 신호 떨림 보정 (핀치/주먹 강도 값 자체를 부드럽게)")]
    [Tooltip("주먹 쥐는(값이 커지는) 방향으로는 이 속도로 빠르게 반응 - 커야 잡으려는 순간이 안 씹힘")]
    public float fingerAttackSmoothing = 30f;
    [Tooltip("주먹 푸는(값이 작아지는) 방향으로는 이 속도로 천천히 반응 - 작아야 살짝 흔들려도 안 놓침")]
    public float fingerReleaseSmoothing = 10f;

    [Header("손 떨림 보정 (레이 흔들림 완화)")]
    [Tooltip("레이 시작점/방향을 얼마나 부드럽게 따라갈지. 값이 클수록 원래 손 움직임에 더 빠르게 반응(떨림도 더 그대로 전달됨), 작을수록 떨림은 줄지만 반응이 느려짐(딜레이 생김). 0 이하면 보정 끔.")]
    public float raySmoothing = 14f;

    [Header("화면 밖으로 나갔을 때 eject(AR로 꺼내기)까지 기다리는 시간(초) - 핀치/주먹 드래그 공통 적용")]
    public float dragDelay = 0.5f;

    [Tooltip("놓인 창을 주먹으로 잡은 채로 레이가 데스크탑 화면에 겹친 뒤, 실제로 데스크탑으로 되돌리기까지 기다리는 시간(초). 스치듯 지나가는 것 방지.")]
    public float reinsertDelay = 0.25f;

    [Header("전송 최적화")]
    public float sendRate = 60f;
    public float minMoveDelta = 0.002f;

    [Header("연결 (비워두면 자동으로 찾음)")]
    public PCStreamReceiver sender;
    public Renderer targetQuadRenderer;
    [Tooltip("꺼낸/잡은 창이 향할 기준 머리 위치. 비워두면 Camera.main을 자동으로 씀")]
    public Transform headTransform;

    [Header("꺼낸 창 스크롤 (핀치한 채로 손을 위아래로)")]
    [Tooltip("이만큼(원본 창 픽셀) 움직일 때마다 휠 한 칸. 작을수록 조금만 움직여도 많이 스크롤된다.")]
    public float scrollPixelsPerNotch = 60f;
    [Tooltip("스마트폰처럼 '내용이 손을 따라오는' 방향. 반대가 편하면 체크 해제")]
    public bool phoneStyleScroll = true;
    [Tooltip("이 픽셀 이상 움직이면 클릭이 아니라 스크롤로 판정해서, 손 뗄 때 클릭이 안 나간다")]
    public float scrollClickCancelPixels = 25f;

    [Header("꺼낸 창을 띄울 새 쿼드 프리팹")]
    public GameObject spawnQuadPrefab;
    [Tooltip("창 픽셀 → 미터. 작을수록 꺼낸 창이 작아진다. 0.0005면 1400px 창이 0.7m라 " +
             "모니터 판(약 0.6m)보다 커져서 어색하다")]
    public float pixelsToMeters = 0.00028f;

    [Tooltip("아무것도 안 겨눴을 때 창을 손 앞 몇 m에 들지")]
    public float holdDistance = 0.55f;

    [Tooltip("데스크탑 판을 겨누고 있으면 그 판보다 '앞'에 창을 든다. " +
             "끄면 항상 Hold Distance를 써서 창이 판 뒤로 들어간다")]
    public bool holdInFrontOfHit = true;

    [Tooltip("겨눈 지점보다 이만큼(m) 내 쪽으로 당겨서 든다")]
    public float holdFrontGap = 0.4f;

    [Tooltip("최종 거리에 곱하는 배율. 0.7이면 지금보다 30% 가까이 온다")]
    public float holdDistanceScale = 0.7f;

    [Tooltip("꺼낸 직후 창을 데스크탑 판과 같은 방향으로 맞춘다(평평하게 나온다). " +
             "끄면 손이 향한 방향 그대로 나와서 판과 40~50도 어긋난다. " +
             "손을 놓은 뒤 기울이거나 돌리는 건 이것과 무관하게 계속 된다")]
    public bool spawnAlignToMonitor = false;

    private Transform lastMonitorT = null;   // 마지막으로 레이가 맞힌 모니터 판

    [Tooltip("꺼내서 들고 있는 동안에도 손목을 돌리면 창이 같이 돌아간다. " +
             "끄면 창이 항상 레이 방향만 보고 있어서, 놓았다가 다시 잡아야 기울일 수 있다")]
    public bool heldFollowsWrist = true;
    private Quaternion heldOffsetRot = Quaternion.identity;   // 들 때의 손↔창 상대 회전
    private Quaternion lastHandRot = Quaternion.identity;     // 이번 프레임 손 회전

    private float lastHitDist = -1f;   // 이번 프레임 데스크탑 판까지 거리 (못 맞혔으면 -1)

    [Tooltip("켜면 꺼낸 창이 항상 똑바로 선다. 기본은 꺼짐 — 손이 향한 각도대로 기울어지는 게 " +
             "원래 동작이고, AR에서 자유롭게 기울이려면 이게 맞다")]
    public bool keepQuadUpright = false;

    [Header("놓인 창 잡았을 때 크기 제한 (한손/양손 공통)")]
    public float minDistance = 0.3f;
    public float maxDistance = 5f;

    private LineRenderer line;
    private Material mat;

    private bool smoothInit = false;
    private Vector3 smoothedStart;
    private Quaternion smoothedRotation; // 방향 벡터만이 아니라 손의 전체 회전(롤 포함)을 스무딩

    private bool fingerSmoothInit = false;
    private float smoothedPinchStrength;
    private float smoothedOtherFingers;
    private float lastGoodPinchStrengthRaw;
    private float lastGoodOtherFingersRaw;

    private bool wasPinch, wasGrab, wasActivate;
    private float pressTime;
    private float lastSendTime;
    private Vector2 lastSentUV;

    private bool dragging = false;
    private bool ejected = false;
    private bool dragIsGrab = false; // 지금 진행 중인 down/move/up이 핀치가 아니라 주먹으로 시작된 것인지
    private int hitMonitorIndex = 0;   // 이번 프레임에 맞힌 판의 모니터 index
    private int dragMonitorIndex = 0;  // 드래그 시작 시 고정

    // eject를 보낼 때 실제로 쥐고 있던 제스처. dragIsGrab은 서버 프로토콜(gmove/geject)을
    // 맞추느라 reinsert 뒤에 강제로 true가 되므로, "계속 들고 있는지" 판정은 이 값으로 한다.
    private bool holdWithGrab = false;
    private GameObject heldQuad = null;        // 새로 꺼내서 든 것
    private bool waitingForSpawn = false;
    private float waitingForSpawnSince = 0f;   // spawn 응답 타임아웃용
    private bool releasedBeforeSpawn = false;  // spawn 응답 오기 전에 손을 이미 놓았는지

    private GameObject grabbedQuad = null;     // 주먹으로 잡은 놓인 창
    private TwoHandGrabResize grabbedResize = null;
    private float grabLostSince = -1f;         // grab 신호가 끊긴 시각 (디바운스용)
    private bool wasTwoHandedForMe = false;    // 방금 전 프레임에 양손 모드였는지 (전환 감지용)
    private Vector3 grabOffsetLocalPos;        // 손 레이 기준 로컬 좌표계에서, 잡은 창까지의 상대 위치
    private Quaternion grabOffsetLocalRot;     // 손 레이 기준 로컬 좌표계에서, 잡은 창의 상대 회전
    private float grabbedSince = 0f;           // grabbedQuad를 잡기 시작한 시각 (고착 안전장치용)
    private float grabbedHitScreenSince = -1f; // 놓인 창을 잡은 채로 레이가 데스크탑 화면에 겹치기 시작한 시각 (되돌리기 디바운스용)

    private GameObject clickTargetQuad = null; // 핀치로 클릭 대기 중인 놓인 창
    private WindowQuadReceiver clickTarget = null;
    private Vector2 clickDownUV;

    // 창 스크롤 상태 (핀치한 채로 손을 위아래로 움직인 양을 휠로 바꿔 보냄)
    private float scrollPrevUvY;      // 직전 프레임의 세로 UV
    private float scrollNotchAccum;   // 아직 안 보낸 휠 칸수
    private float scrollMovedPixels;  // 이번 핀치 동안 움직인 총 픽셀(클릭 취소 판정용)
    private bool scrollActive;        // 스크롤로 판정됨 = 손 뗄 때 클릭 안 보냄

    // === 가장자리(한 변) 리사이즈 상태 ===
    private bool resizingEdge = false;
    private EdgeResizer activeResizer = null;
    private EdgeResizer.Edge activeEdge;

    // === 연결선(tether) 치우기 상태 ===
    private ITetherCarry carriedTether = null;
    private float carryDist = 0.5f;

    // === 데스크탑 가장자리에서 화살표를 끌어내 AR 창에 잇는 중 ===
    private DesktopTetherSpawner spawnDragOwner = null;

    // === 박수 토글(HandInteractionToggle)이 두 손 위치를 읽어가기 위한 것 ===
    public static readonly List<HandRayPointer> All = new();
    // 지금 이 손이 꼬집고 있는 AR 창 (QuadLinkManager가 창↔창 연결을 감지할 때 사용)
    public WindowQuadReceiver PinchedWindow => clickTarget;

    // [AR LAPTOP] 이 손이 지금 그 창의 위치/크기를 정하고 있는지 (꺼내 드는 중 / 주먹으로 잡음 / 가장자리 리사이즈).
    // WindowsFollowDesktop가 판을 따라 창을 옮길 때 이 창은 건너뛴다.
    public bool IsManipulating(GameObject q) =>
        q != null && (q == heldQuad || q == grabbedQuad ||
                      (resizingEdge && activeResizer != null && activeResizer.gameObject == q));

    // 데스크탑(모니터 판)을 핀치로 누르고 있는 중인지.
    // "데스크탑 꼬집 → AR 창 꼬집 = 화살표" 제스처를 QuadLinkManager가 판정할 때 쓴다.
    private Transform pinchedMonitorT = null;
    private float pinchedMonitorAt = 0f;
    private int pinchedMonitorIdx = 0;
    public Transform PinchedMonitor => pinchedMonitorT;
    public float PinchedMonitorAt => pinchedMonitorAt;
    public int PinchedMonitorIndex => pinchedMonitorIdx;

    // 그 핀치가 사실은 "연결하려던 것"이었을 때, PC로 보낸 마우스 down을 취소한다.
    public void CancelDesktopPress()
    {
        if (dragging && !dragIsGrab)
            Send("up", lastSentUV.x, lastSentUV.y, dragMonitorIndex);
        dragging = false;
        ejected = false;
        waitingForSpawn = false;
        pinchedMonitorT = null;
    }
    // 그 창을 꼬집기 시작한 시각 - 먼저 꼬집은 창이 화살표의 출발점이 된다
    public float PinchedAt => pinchStartTime;
    private float pinchStartTime;
    // 클릭 대신 다른 동작(창 연결 등)이 일어났을 때, 손 뗄 때 클릭이 나가지 않게 한다
    public void SuppressPendingClick() { scrollActive = true; }

    public bool IsHandTracked => hand != null && hand.IsTracked;
    // 트래킹 신뢰도가 높을 때만 true. 손이 시야에 막 들어온 순간엔 위치가 튀므로 박수 판정에 이걸 쓴다.
    public bool IsHandConfident => hand != null && hand.IsTracked
                                   && hand.HandConfidence == OVRHand.TrackingConfidence.High;
    public Vector3 CurrentHandPos { get; private set; }

    void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
    }
    void OnDisable() { All.Remove(this); }

    // ─────────────────────────────────────────────────────────────────────────
    // 씬/프리팹에 저장된 값이 코드 기본값을 덮어쓴다. 유니티가 씬을 열어둔 채면
    // 파일을 직접 고쳐도 저장할 때 되돌아간다. 그래서 조정값은 여기서 코드 기준으로
    // 못 박는다. Inspector에서 바꾸고 싶어지면 이 블록을 지우면 된다.
    // ─────────────────────────────────────────────────────────────────────────
    void ApplyTuning()
    {
        pixelsToMeters    = 0.000392f;  // 꺼낸 창 크기 (기준 0.00028의 1.4배)
        holdDistance      = 0.55f;      // 아무것도 안 겨눴을 때 손 앞 거리
        holdFrontGap      = 0.40f;      // 겨눈 지점에서 내 쪽으로 당기는 양
        holdDistanceScale = 0.7f;       // 최종 거리 배율
        minDistance       = 0.02f;      // [AR LAPTOP] 가까이서 잡아도 그 거리 그대로 (예전 0.2 → 가까우면 20cm로 밀려남)
        holdInFrontOfHit  = true;       // 판보다 앞에 들기
        keepQuadUpright   = false;      // AR에서는 위아래 기울기가 살아 있어야 한다
        spawnAlignToMonitor = false;    // 예전 동작(손이 향한 방향 그대로). true면 판과 나란히
        heldFollowsWrist    = true;     // 들고 있는 동안 손목 회전이 그대로 먹게
    }

    void Start()
    {
        ApplyTuning();

        line = GetComponent<LineRenderer>();
        line.positionCount = 2;
        line.useWorldSpace = true;
        line.startWidth = width;
        line.endWidth = width;
        mat = line.material;
        SetColor(idleColor);

        if (sender == null) sender = PCStreamReceiver.Instance;
        if (targetQuadRenderer == null && sender != null)
            targetQuadRenderer = sender.targetRenderer;
    }

    void Update()
    {
        // spawn 메시지 큐는 PCStreamReceiver 하나에 양손이 공유해서 쓴다. waitingForSpawn이
        // 아닌 손까지 무작정 꺼내가면, eject를 실제로 시킨 손이 아니라 엉뚱한 손이 메시지를
        // 가로채서 그 손 쪽에 쿼드를 만들어버리거나(위치 이상), 정작 기다리던 손은 영원히
        // 메시지를 못 받아 쿼드가 안 생기는 문제가 있었다. 그래서 "내가 진짜 기다리는 중"일
        // 때만 큐에서 꺼내가도록 제한한다.
        if (waitingForSpawn && sender != null && sender.TryGetSpawnMessage(out string spawnMsg))
            HandleSpawn(spawnMsg);

        // === 레이 시작점/방향: 손의 PointerPose(검지 기준 조준) 우선 ===
        Transform originT = (hand != null && hand.IsTracked && hand.PointerPose != null)
            ? hand.PointerPose
            : (pointerPoseFallback != null ? pointerPoseFallback : transform);

        Vector3 rawStart = originT.position;
        Quaternion rawRotation = originT.rotation; // forward 벡터만이 아니라 전체 회전(롤 포함) 사용 - 손목 비틀기 반영용

        // 박수 토글이 두 손 거리를 재려면 손 위치는 꺼져 있을 때도 계속 갱신해야 한다
        CurrentHandPos = rawStart;

        // 손 조작이 꺼져 있으면(박수로 토글) 여기서 끝. 진행 중이던 것들은 안전하게 놓아준다.
        if (!HandInteractionToggle.HandsEnabled)
        {
            ReleaseAllInteractions();
            if (line != null) line.enabled = false;
            wasPinch = false; wasGrab = false; wasActivate = false;
            return;
        }
        if (line != null && !line.enabled) line.enabled = true;

        // [AR LAPTOP] 가까이 모드: 검지 끝이 화면 판/꺼낸 창에 가까우면 레이·핀치·주먹을 끄고
        // 검지 터치(FingerTouch)만 쓴다. 이미 잡고/끌고 있는 중이면 그 동작이 끝날 때까지 기다린다.
        UpdateNearMode();
        if (IsNearMode)
        {
            if (line != null) line.enabled = false;
            wasPinch = false; wasGrab = false; wasActivate = false;
            grabLostSince = -1f;
            return;
        }

        Vector3 start;
        Quaternion handRot;
        if (raySmoothing <= 0f)
        {
            start = rawStart;
            handRot = rawRotation;
        }
        else if (!smoothInit)
        {
            smoothedStart = rawStart;
            smoothedRotation = rawRotation;
            smoothInit = true;
            start = rawStart;
            handRot = rawRotation;
        }
        else
        {
            // 지수 스무딩: 손 미세 떨림을 걸러내면서 실제 움직임에는 자연스럽게 따라감
            float t = 1f - Mathf.Exp(-raySmoothing * Time.deltaTime);
            smoothedStart = Vector3.Lerp(smoothedStart, rawStart, t);
            smoothedRotation = Quaternion.Slerp(smoothedRotation, rawRotation, t);
            start = smoothedStart;
            handRot = smoothedRotation;
        }
        lastHandRot = handRot;
        Vector3 direction = handRot * Vector3.forward;

        Vector3 end;
        bool hitQuad = false;
        lastHitDist = -1f;        // 매 프레임 초기화. 안 하면 옛 거리가 남아 엉뚱한 곳에 든다
        Vector2 uv = Vector2.zero;
        WindowQuadReceiver hitWindow = null;
        WindowEdgeHandle hitHandle = null;   // 이번 프레임 레이가 맞힌 가장자리 핸들
        TetherHandle hitTetherHandle = null; // 이번 프레임 레이가 맞힌 연결선 손잡이
        DesktopEdgeHandle hitDesktopHandle = null; // 데스크탑 가장자리 핸들 (여기서 화살표를 끌어냄)
        Transform hitMonitorT = null;              // 이번 프레임 레이가 맞힌 모니터 판
        Vector2 winUV = Vector2.zero;

        if (Physics.Raycast(start, direction, out RaycastHit hit, length, hitLayers))
        {
            end = hit.point;
            hitHandle = hit.collider.GetComponent<WindowEdgeHandle>();
            // 자석: 핸들 근처(넉넉한 콜라이더)에 닿으면 강조 표시 + 레이 끝이 핸들 중심선에 착 달라붙는다
            if (hitHandle != null) { hitHandle.NotifyHover(); end = hitHandle.SnapPoint(hit.point); }
            hitTetherHandle = hit.collider.GetComponent<TetherHandle>();
            if (hitTetherHandle != null) hitTetherHandle.NotifyHover();
            hitDesktopHandle = hit.collider.GetComponent<DesktopEdgeHandle>();
            if (hitDesktopHandle != null) { hitDesktopHandle.NotifyHover(); end = hitDesktopHandle.SnapPoint(hit.point); }
            hitWindow = hit.collider.GetComponentInParent<WindowQuadReceiver>();
            if (hitWindow != null) winUV = hit.textureCoord;
            else
            {
                // 모니터 판: MonitorPlane 있으면 그 index, 없으면 (구버전) targetQuadRenderer → 주 모니터
                var plane = hit.collider.GetComponentInParent<MonitorPlane>();
                if (plane != null)
                {
                    hitQuad = true;
                    lastHitDist = hit.distance;
                    uv = hit.textureCoord;
                    hitMonitorIndex = plane.monitorIndex;
                    hitMonitorT = plane.transform;
                    lastMonitorT = plane.transform;
                }
                else if (targetQuadRenderer != null && hit.collider.GetComponent<Renderer>() == targetQuadRenderer)
                {
                    hitQuad = true;
                    uv = hit.textureCoord;
                    hitMonitorIndex = 0;
                }
            }
        }
        else end = start + direction * length;

        // 이미 놓인 창을 주먹으로 잡고 있는 중에는, 이번 프레임 레이가 다른 창에 스치듯
        // 걸쳐도 그쪽(클릭/잡기)을 절대 시작하지 않는다 - 지금 잡고 있는 그 창 하나만 조작
        // 대상이어야 하므로. 단, 데스크탑 화면(hitQuad)은 예외 - 화면에 겹치면 "다시
        // 데스크탑으로 되돌리기"의 트리거로 아래에서 직접 쓴다.
        if (grabbedQuad != null)
        {
            hitWindow = null;
        }

        line.SetPosition(0, start);
        line.SetPosition(1, end);

        // === 손가락 상태 읽기 ===
        // pinch는 예전과 똑같이 "검지 핀치 강도"만으로 판단(원래 잘 되던 방식, 회귀 없게 유지).
        // grab(주먹)은 검지를 뺀 나머지 세 손가락(중지/약지/새끼)이 오므라든 정도로 독립 판단.
        // 실제 주먹을 쥐면 검지도 같이 오므라들어서 pinch 조건도 같이 만족하는데, 이 경우엔
        // "나머지 세 손가락도 오므라들었다"는 신호(otherFingersCurled)가 있으니 grab을 우선시키고 pinch는 취소한다.
        bool handValid = hand != null && hand.IsTracked;
        float pinchStrengthRaw = 0f;  // 검지 핀치 강도
        float otherFingersRaw = 0f;   // 중지/약지/새끼 중 "가장 많이 오므라든" 손가락 기준 (평균 대신 max: 더 민감하게, grab을 우선시키기 위함)

        if (handValid)
        {
            pinchStrengthRaw = hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);

            if (skeleton != null && skeleton.IsInitialized)
            {
                // 실제 손가락 말림(curl)으로 판단: 핀치 강도(엄지-손끝 거리)는 엄지가 각 손끝을
                // 정확히 짚어야만 값이 오르는데, 실제 주먹은 엄지가 손끝을 안 짚고 손가락 위를
                // 덮듯이 감싸는 경우가 많아서 핀치 강도로는 "눈에는 분명 주먹인데 인식이 안 되는"
                // 일이 생겼다. 손끝-손목 거리가 가까울수록(=말려있을수록) curl이 커지게 계산.
                float midCurl = FingerCurl(OVRSkeleton.BoneId.Hand_MiddleTip);
                float ringCurl = FingerCurl(OVRSkeleton.BoneId.Hand_RingTip);
                float pinkyCurl = FingerCurl(OVRSkeleton.BoneId.Hand_PinkyTip);
                otherFingersRaw = Mathf.Max(midCurl, Mathf.Max(ringCurl, pinkyCurl));
            }
            else
            {
                // skeleton 미연결 시 예전 방식(핀치 강도)으로 대체 - 정확도는 떨어짐
                float mid = hand.GetFingerPinchStrength(OVRHand.HandFinger.Middle);
                float ring = hand.GetFingerPinchStrength(OVRHand.HandFinger.Ring);
                float pinky = hand.GetFingerPinchStrength(OVRHand.HandFinger.Pinky);
                otherFingersRaw = Mathf.Max(mid, Mathf.Max(ring, pinky));
            }

            // 트래킹 신뢰도가 낮은 순간(손이 카메라 시야 가장자리/자기 손에 가려지는 등)에는
            // 뼈 위치 자체가 잠깐 튀거나 잘못 계산될 수 있다. 이럴 땐 이번 프레임 값을 믿지 않고
            // "마지막으로 신뢰도 높았을 때의 값"을 그대로 유지한다 (0으로 훅 떨어뜨리지도,
            // 노이즈를 그대로 받아들이지도 않음 - 그래서 "됐다 안됐다" 하는 게 줄어들 것).
            if (hand.HandConfidence == OVRHand.TrackingConfidence.High)
            {
                lastGoodPinchStrengthRaw = pinchStrengthRaw;
                lastGoodOtherFingersRaw = otherFingersRaw;
            }
            else
            {
                pinchStrengthRaw = lastGoodPinchStrengthRaw;
                otherFingersRaw = lastGoodOtherFingersRaw;
            }
        }

        // 손가락 강도 값 자체도 떨림이 있어서(특히 정적으로 주먹을 쥐고 있을 때 트래킹 신뢰도가 흔들림),
        // 판정 전에 살짝 스무딩한다. 다만 한쪽 속도로만 하면 문제가 있었다: 부드럽게(느리게) 하면
        // 잡으려는 순간(값이 올라가는 것)도 같이 느려져서 "씹히는" 느낌이 나고, 빠르게 하면 떨림을
        // 그대로 통과시켜서 잘 놓치게 된다. 그래서 "오므라들 때는 빠르게, 풀릴 때는 천천히" 비대칭
        // 스무딩(attack/release)을 쓴다 - 오디오 컴프레서랑 같은 원리.
        if (!fingerSmoothInit)
        {
            smoothedPinchStrength = pinchStrengthRaw;
            smoothedOtherFingers = otherFingersRaw;
            fingerSmoothInit = true;
        }
        else
        {
            SmoothAttackRelease(ref smoothedPinchStrength, pinchStrengthRaw);
            SmoothAttackRelease(ref smoothedOtherFingers, otherFingersRaw);
        }
        float pinchStrength = smoothedPinchStrength;
        float otherFingers = smoothedOtherFingers;

        // grab을 pinch보다 우선 인식시키기 위한 히스테리시스:
        // 이미 grab 상태였다면 더 낮은 문턱값으로도 grab을 유지(잘 안 풀리게),
        // 아직 grab이 아니었다면 원래 문턱값을 넘어야 grab으로 전환.
        float grabEnterThreshold = wasGrab ? grabThreshold * grabHoldRatio : grabThreshold;
        bool rawGrabSignal = handValid && otherFingers >= grabEnterThreshold;

        // 디바운스: 트래킹이 한두 프레임 흔들려서 신호가 잠깐 끊겨도, grabReleaseGrace 안에
        // 신호가 돌아오면 계속 잡고 있는 걸로 친다. 그 시간 넘도록 안 돌아와야 진짜로 놓은 것.
        bool grab;
        if (rawGrabSignal)
        {
            grab = true;
            grabLostSince = -1f;
        }
        else if (wasGrab)
        {
            if (grabLostSince < 0f) grabLostSince = Time.time;
            grab = (Time.time - grabLostSince) < grabReleaseGrace;
            if (!grab) grabLostSince = -1f;
        }
        else
        {
            grab = false;
        }
        bool otherFingersCurled = grab; // 순수 핀치 판정에서 grab 신호를 배제할 때 씀

        // 주먹을 쥐고 있는 동안엔 데스크탑 가장자리 핸들을 통과시킨다 (창 꺼낼 때 방해되지 않도록)
        if (grab && DesktopTetherSpawner.Instance != null)
            DesktopTetherSpawner.Instance.NotifyPassThrough();
        // 순수 핀치: 검지 강도는 예전 기준 그대로, 단 주먹 신호(grab)가 조금이라도 뜨면
        // pinch는 무조건 취소하고 grab을 우선시킨다 (핀치 상태에서 손을 더 오므려도 grab으로 넘어가고,
        // 반대로 주먹을 풀다가 잠깐 핀치 모양을 거쳐도 grab 신호가 남아있는 동안은 grab 유지)
        bool pinch = handValid && pinchStrength >= pinchThreshold && !otherFingersCurled;

        SetColor((pinch || grab) ? clickColor : idleColor);

        float u = uv.x;
        float v = 1f - uv.y;
        Vector2 sendUV = new Vector2(u, v);

        bool justPinch = pinch && !wasPinch;
        bool justUnpinch = !pinch && wasPinch;
        bool justGrab = grab && !wasGrab;
        bool justUngrab = !grab && wasGrab;

        // === 놓인 창을 주먹으로 잡은 채로 레이가 데스크탑 화면에 겹치면: 실제 데스크탑으로
        // 되돌리고, 손을 떼지 않은 채로 곧바로 "화면 위에서 창 통째로 옮기는" 흐름(gdown과
        // 동일한 이어짐)으로 전환한다. 스치듯 지나가는 걸 막기 위해 짧은 디바운스를 둔다. ===
        // 방금 꺼내서 들고 있는 창(heldQuad)도 똑같이 되돌릴 수 있어야 한다 - 손을 놓지 않은
        // 채로 데스크탑 쪽에 다시 대면 그대로 들어가고, 다시 끌어내면 또 나온다.
        bool holdActiveNow = holdWithGrab ? grab : pinch;
        GameObject returningQuad = null;
        bool returningIsHeld = false;
        if (grabbedQuad != null && grab) returningQuad = grabbedQuad;
        else if (heldQuad != null && holdActiveNow) { returningQuad = heldQuad; returningIsHeld = true; }

        if (returningQuad != null && hitQuad)
        {
            if (grabbedHitScreenSince < 0f) grabbedHitScreenSince = Time.time;
            if (Time.time - grabbedHitScreenSince >= reinsertDelay)
            {
                var returningWindow = returningQuad.GetComponent<WindowQuadReceiver>();
                if (returningWindow != null)
                {
                    SendW("reinsert", returningWindow.windowId, u, v, hitMonitorIndex);
                    if (WindowCaptureHub.Instance != null) WindowCaptureHub.Instance.Unregister(returningWindow.windowId);
                    Debug.Log($"[HandRay:{handId}] 데스크탑으로 되돌림 id={returningWindow.windowId}");

                    if (!returningIsHeld && grabbedResize != null) grabbedResize.OnGrabEnd(handId);
                    Destroy(returningQuad);
                    if (returningIsHeld) heldQuad = null;
                    else { grabbedQuad = null; grabbedResize = null; }
                    wasTwoHandedForMe = false;
                    grabbedHitScreenSince = -1f;

                    // 서버가 reinsert에서 이미 이 창을 grabMoveWindow로 등록해뒀으므로, 여기서
                    // 새로 "gdown"을 또 보내면 안 된다 (덮어쓰기/이중 등록 위험). wasActivate를
                    // 미리 켜서 이번 프레임 justActivate가 뜨지 않게 하고, dragging만 이어받는다.
                    dragging = true;
                    dragIsGrab = true;
                    dragMonitorIndex = hitMonitorIndex;   // 되돌린 그 모니터에서 이어서 이동
                    ejected = false;
                    pressTime = Time.time;
                    lastSentUV = sendUV;
                    wasActivate = true;
                }
            }
        }
        else
        {
            grabbedHitScreenSince = -1f;
        }

        // === 데스크탑 화면(투명판) 위에서는 주먹도 클릭/드래그로 취급 (실제 창 이동용) ===
        // 처음 시작할 때는 반드시 화면(hitQuad)을 가리키고 있어야 하지만, 일단 드래그가
        // 시작된 뒤에는(dragging && dragIsGrab) 화면 밖으로 손이 나가도(hitQuad=false)
        // activate가 꺼지면 안 된다 — 그래야 eject 판정(화면 밖으로 dragDelay 이상 유지)까지 도달함.
        // hitQuad 단독 조건은 grabbedQuad가 없을 때만 인정한다 - 그래야 놓인 창을 잡은 채로
        // 화면을 스치는 순간(위에서 처리 중/디바운스 대기 중) 여기서 또 새 gdown이 끼어들지 않는다.
        bool desktopGrabActivate = grab && carriedTether == null && (((hitQuad && grabbedQuad == null) && hitWindow == null) || (dragging && dragIsGrab));
        bool activate = pinch || desktopGrabActivate;
        bool justActivate = activate && !wasActivate;
        bool justDeactivate = !activate && wasActivate;

        // === 이미 주먹으로 잡고 있는 놓인 창: 위치/회전 갱신 ===
        if (grabbedQuad != null && grab)
        {
            bool isTwoHandedNow = grabbedResize != null && grabbedResize.IsTwoHanded;

            // 이 손의 현재 위치/회전은 솔로/양손 모드와 상관없이 매 프레임 항상 최신으로 갱신해둔다.
            // (이걸 양손 모드일 때만 갱신하면, 한 손으로 오래 들고 돌아다니다가 나중에 다른 손이
            // 합류하는 순간 그 손 정보가 "맨 처음 잡았을 때"의 낡은 값으로 남아있게 되고, 양손 앵커
            // 계산이 그 낡은 위치를 기준으로 튀어서 창이 엉뚱한 곳(화면 밖 등)으로 순간이동해
            // 사라져 보이는 문제가 있었다.)
            if (grabbedResize != null) grabbedResize.UpdateHand(handId, start, handRot);

            if (isTwoHandedNow)
            {
                // 양손이 같이 잡은 상태 -> 크기/회전/위치는 TwoHandGrabResize가 전담 (UpdateHand가 이미 처리)
                wasTwoHandedForMe = true;
            }
            else
            {
                // 방금 양손 -> 한손으로 전환된 순간이면, 지금 위치/회전 기준으로 손과의
                // 상대적 오프셋을 다시 잡는다. 옛날처럼 무조건 "레이 정중앙"으로 스냅시키면
                // 두손 리사이즈 중이던 위치에서 벗어나 있어서 순간이동처럼 튀어보였다.
                if (wasTwoHandedForMe)
                {
                    RecalibrateGrabOffset(start, handRot, grabbedQuad.transform);
                    wasTwoHandedForMe = false;
                }

                // handRot: 손의 전체 회전(롤/비틀기까지 포함). 예전엔 Quaternion.LookRotation(direction)만
                // 써서 손목을 비틀어도(롤) 반영이 안 됐는데, 이제는 손의 전체 회전을 그대로 기준으로 삼는다.
                // 그래서 멀리서 한 손으로 창을 잡은 채 손을 돌리면 창도 손과 함께 그대로 돌아간다.
                Vector3 offset = handRot * grabOffsetLocalPos;
                float dist = Mathf.Clamp(offset.magnitude, minDistance, maxDistance);
                Vector3 hp = start + (offset.sqrMagnitude > 0.0001f ? offset.normalized * dist : direction * dist);
                grabbedQuad.transform.position = hp;

                // 손목 각도가 그대로 창에 실린다. keepQuadUpright면 좌우(yaw)만 남기고
                // 앞뒤로 눕는 것(pitch)과 옆으로 도는 것(roll)은 버려서 창이 계속 수직으로 선다.
                // 일부러 기울이고 싶으면 양손으로 잡으면 된다 (TwoHandGrabResize는 그대로 동작).
                Quaternion gr = handRot * grabOffsetLocalRot;
                if (keepQuadUpright)
                {
                    Vector3 fwd = gr * Vector3.forward;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 1e-6f) gr = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                }
                grabbedQuad.transform.rotation = gr;
            }

            // 안전장치: 트래킹 오류 등으로 grab 신호가 계속 true에 눌러붙어서 영원히 안 놓이는
            // 경우를 대비한 타임아웃. 이 시간 넘게 계속 잡고 있으면 강제로 놓는다.
            if (Time.time - grabbedSince > 20f)
            {
                Debug.LogWarning($"[HandRay:{handId}] 너무 오래 잡혀있어서 강제로 놓음 (트래킹 오류 의심)");
                if (grabbedResize != null) grabbedResize.OnGrabEnd(handId);
                grabbedQuad = null;
                grabbedResize = null;
                wasTwoHandedForMe = false;
            }
        }

        // === 연결선(tether) 잡아서 치우기 ===
        // 선 중앙 구체를 주먹으로 잡고 멀리 끌고 가서 놓으면 선이 사라진다.
        if (carriedTether != null)
        {
            if (grab)
            {
                Vector3 cp = start + direction * carryDist;
                carriedTether.UpdateCarry(cp);
            }
            if (justUngrab)
            {
                carriedTether.EndCarry();   // 멀리 치웠으면 EndCarry 내부에서 스스로 삭제
                carriedTether = null;
            }
        }
        else if (justGrab && grabbedQuad == null && hitTetherHandle != null && hitTetherHandle.tether != null)
        {
            carriedTether = hitTetherHandle.tether;
            carryDist = Vector3.Distance(start, hitTetherHandle.transform.position);
            carriedTether.BeginCarry();
            Debug.Log($"[HandRay:{handId}] 연결선 잡음");
        }

        // 주먹 쥐는 순간: 가리킨 놓인 창 잡기 (연결선을 잡고 있는 중엔 창은 안 잡음)
        if (justGrab && grabbedQuad == null && carriedTether == null && hitWindow != null)
        {
            grabbedQuad = hitWindow.gameObject;
            grabbedResize = grabbedQuad.GetComponent<TwoHandGrabResize>();
            if (grabbedResize != null)
            {
                // 지금 레이가 쿼드에 맞은 지점을 앵커로 고정(로컬 좌표) + 그 지점까지의 거리도 고정.
                // 양손이 다 잡으면 이 두 앵커를 이용해서 TwoHandGrabResize가 회전/위치/크기를 계산한다.
                Vector3 anchorLocal = grabbedQuad.transform.InverseTransformPoint(hit.point);
                float anchorHoldDistance = Vector3.Distance(start, hit.point);
                grabbedResize.OnGrabStart(handId, start, handRot, anchorLocal, anchorHoldDistance);
            }
            RecalibrateGrabOffset(start, handRot, grabbedQuad.transform);
            wasTwoHandedForMe = false;
            grabbedSince = Time.time;
            Debug.Log($"[HandRay:{handId}] 주먹으로 놓인 창 잡음");
        }
        // 주먹 펴는 순간: 배치
        if (justUngrab && grabbedQuad != null)
        {
            if (grabbedResize != null) grabbedResize.OnGrabEnd(handId);
            Debug.Log($"[HandRay:{handId}] 놓인 창 배치 완료");
            grabbedQuad = null;
            grabbedResize = null;
            wasTwoHandedForMe = false;
        }

        // === 새로 꺼내서 들고 있는 중 ===
        // 어떤 제스처로 eject를 시작했느냐(dragIsGrab)에 따라, 계속 그 제스처로 들고 있어야 함
        bool holdActive = holdWithGrab ? grab : pinch;
        bool justReleaseHold = holdWithGrab ? justUngrab : justUnpinch;

        if (heldQuad != null && holdActive)
        {
            // 들고 있는 동안은 계속 레이 통과 상태로 유지한다.
            // (EdgeResizer는 생성 다음 프레임에 붙기 때문에 spawn 때 한 번만 꺼선 부족하다)
            SetQuadRaycastBlocking(heldQuad, false);

            // 판을 겨누고 있으면 그 판보다 앞에 든다. 안 그러면 창이 데스크탑 판 뒤로 들어간다.
            float hd = holdDistance;
            if (holdInFrontOfHit && hitQuad && lastHitDist > 0f)
                hd = lastHitDist - holdFrontGap;
            hd = Mathf.Max(minDistance, hd * Mathf.Max(0.05f, holdDistanceScale));

            Vector3 hp = start + direction * hd;
            heldQuad.transform.position = hp;

            // 꺼내는 동안의 자세: 데스크탑 판과 나란히 둔다.
            // 예전에는 "손에서 창으로 향하는 방향"을 그대로 썼다. 그러면 손이 향한
            // 각도가 그대로 창의 자세가 되어, 판과 40~50도 어긋난 채로 놓인다.
            // (손을 놓은 뒤 주먹·양손으로 기울이는 건 여기서 안 건드린다)
            // ── 들고 있는 동안의 회전 ────────────────────────────────────────────
            // 예전에는 "손에서 창으로 향하는 방향"만 봤다. 그 방향은 손목을 비틀어도
            // 거의 안 바뀌기 때문에, 꺼내서 들고 있는 동안에는 기울이기가 아예 안 먹었다.
            // (놓았다가 주먹으로 다시 잡아야만 회전이 됐다 - 그쪽은 handRot을 쓰니까)
            // 여기서도 손의 전체 회전(handRot, 롤·비틀기 포함)을 그대로 따라가게 한다.
            if (heldFollowsWrist)
            {
                heldQuad.transform.rotation = handRot * heldOffsetRot;
            }
            else
            {
                Vector3 ld = hp - start;
                if (keepQuadUpright) ld.y = 0f;
                if (spawnAlignToMonitor && lastMonitorT != null)
                    heldQuad.transform.rotation = lastMonitorT.rotation;
                else if (ld.sqrMagnitude > 0.0001f)
                    heldQuad.transform.rotation = Quaternion.LookRotation(ld.normalized, Vector3.up);
            }
        }

        // === 상태 전이 ===
        // 가장자리 리사이즈가 최우선: 핀치로 핸들을 잡고 끄는 동안은 다른 상호작용을 막는다.
        if (resizingEdge)
        {
            if (pinch && activeResizer != null) activeResizer.UpdateResize(start, direction);
            if (justUnpinch || activeResizer == null)
            {
                if (activeResizer != null) activeResizer.EndResize();
                resizingEdge = false;
                activeResizer = null;
                Debug.Log($"[HandRay:{handId}] 가장자리 리사이즈 끝");
            }
        }
        // 데스크탑 가장자리에서 화살표 끌어내는 중: 놓는 순간 가리키던 AR 창에 연결선을 다시 만든다
        else if (spawnDragOwner != null)
        {
            if (pinch) spawnDragOwner.UpdateDrag(end);
            if (justUnpinch)
            {
                spawnDragOwner.EndDrag(hitWindow);
                spawnDragOwner = null;
            }
        }
        // 데스크탑 가장자리 핸들을 핀치로 잡음 = 화살표 끌어내기 시작
        else if (justPinch && hitDesktopHandle != null && hitDesktopHandle.owner != null && grabbedQuad == null)
        {
            spawnDragOwner = hitDesktopHandle.owner;
            spawnDragOwner.BeginDrag(hitDesktopHandle, end);
            Debug.Log($"[HandRay:{handId}] 데스크탑에서 화살표 끌어내기 시작");
        }
        // 핀치 시작 순간 레이가 가장자리 핸들에 맞아 있으면 = 그 변을 늘리기 시작
        else if (justPinch && hitHandle != null && hitHandle.owner != null && grabbedQuad == null)
        {
            activeResizer = hitHandle.owner;
            activeEdge = hitHandle.edge;
            activeResizer.BeginResize(activeEdge, start, direction);
            resizingEdge = true;
            Debug.Log($"[HandRay:{handId}] 가장자리 리사이즈 시작 edge={activeEdge}");
        }
        else if (heldQuad != null)
        {
            if (justReleaseHold)
            {
                Debug.Log($"[HandRay:{handId}] 배치 완료");
                SetQuadRaycastBlocking(heldQuad, true);   // 놓았으니 다시 레이에 맞게
                heldQuad = null; dragging = false;
            }
        }
        else if (clickTargetQuad != null)
        {
            // 핀치를 유지한 채 손을 위아래로 움직이면 = 스크롤 (스마트폰에서 밀어 올리는 것처럼)
            if (pinch && hitWindow != null && hitWindow == clickTarget)
            {
                float uvY = winUV.y;                       // 0=아래, 1=위
                float dUv = uvY - scrollPrevUvY;
                scrollPrevUvY = uvY;

                // UV 변화량을 "원본 창 픽셀"로 환산
                float quadPxH = clickTargetQuad.transform.localScale.y / Mathf.Max(pixelsToMeters, 1e-6f);
                float movedPx = dUv * quadPxH;
                scrollMovedPixels += Mathf.Abs(movedPx);

                if (!scrollActive && scrollMovedPixels >= scrollClickCancelPixels)
                    scrollActive = true;                   // 충분히 움직였으면 클릭이 아니라 스크롤

                if (scrollActive && scrollPixelsPerNotch > 0.01f)
                {
                    scrollNotchAccum += movedPx / scrollPixelsPerNotch;
                    int notches = (int)scrollNotchAccum;   // 0쪽으로 내림
                    if (notches != 0)
                    {
                        scrollNotchAccum -= notches;
                        // 손이 위로 → 내용이 따라 올라감 = 문서를 아래로 내리는 것 = 휠 아래(음수)
                        int delta = notches * 120 * (phoneStyleScroll ? -1 : 1);
                        SendScroll(clickTarget.windowId, clickDownUV.x, clickDownUV.y, delta);
                    }
                }
            }

            // 손을 뗌: 거의 안 움직였으면 클릭, 스크롤했으면 클릭은 안 보냄
            if (justUnpinch)
            {
                if (!scrollActive)
                {
                    long id = clickTarget.windowId;
                    SendW("wclick", id, clickDownUV.x, clickDownUV.y);
                    Debug.Log($"[HandRay:{handId}] 꺼낸 창 클릭 id={id}");
                }
                clickTargetQuad = null; clickTarget = null;
                scrollActive = false;
            }
        }
        else if (waitingForSpawn)
        {
            // 서버 왕복(네트워크 + Thread.Sleep들)이 끝나기 전에 손을 놓아버릴 수 있다.
            // 여기서 바로 waitingForSpawn을 꺼버리면, 나중에 spawn 메시지가 도착했을 때
            // 아무도 기다리고 있지 않아서 메시지가 그냥 버려지고 쿼드가 안 생긴다.
            // 그래서 손을 놓아도 "이미 놓았다"만 기억해두고, spawn 메시지 자체는 계속 기다린다.
            if (justReleaseHold) { releasedBeforeSpawn = true; dragging = false; }
            // 혹시 서버 응답이 영영 안 오는 경우를 대비한 타임아웃
            if (Time.time - waitingForSpawnSince > 3f)
            {
                Debug.LogWarning($"[HandRay:{handId}] spawn 응답 타임아웃, 포기");
                waitingForSpawn = false;
                releasedBeforeSpawn = false;
                dragging = false;
            }
        }
        else if (justPinch && hitWindow != null && grabbedQuad == null)
        {
            // 핀치로 놓인 창 누름 = 클릭 대기 (주먹은 클릭이 아니라 "잡기"라 여기서 제외)
            clickTargetQuad = hitWindow.gameObject;
            clickTarget = hitWindow;
            clickDownUV = new Vector2(winUV.x, 1f - winUV.y);

            pinchStartTime = Time.time;   // 꼬집은 순서(화살표 방향) 판정용

            // 스크롤 판정 초기화
            scrollPrevUvY = winUV.y;
            scrollNotchAccum = 0f;
            scrollMovedPixels = 0f;
            scrollActive = false;
        }
        else if (justActivate && hitQuad)
        {
            pressTime = Time.time;
            lastSentUV = sendUV;
            dragging = true;
            ejected = false;
            // 핀치로 시작 = 진짜 마우스 클릭/드래그(down). 주먹으로 시작 = 창 통째로 이동(gdown, 타이틀바 안 잡아도 됨)
            dragIsGrab = !pinch;
            dragMonitorIndex = hitMonitorIndex;   // 이 드래그의 모니터 고정
            Send(dragIsGrab ? "gdown" : "down", u, v, dragMonitorIndex);

            // 핀치로 데스크탑을 눌렀다면 "연결 시작점"일 수도 있으니 기억해둔다
            if (pinch && hitMonitorT != null)
            {
                pinchedMonitorT = hitMonitorT;
                pinchedMonitorAt = Time.time;
                pinchedMonitorIdx = hitMonitorIndex;
            }
        }
        else if (activate && dragging && !ejected)
        {
            if (hitQuad)
            {
                bool rateOk = (Time.time - lastSendTime) >= (1f / sendRate);
                bool moved = (sendUV - lastSentUV).magnitude >= minMoveDelta;
                if (rateOk && moved) { Send(dragIsGrab ? "gmove" : "move", u, v, dragMonitorIndex); lastSendTime = Time.time; lastSentUV = sendUV; }
            }
            else
            {
                // 화면 밖으로 나가면 eject. 핀치든 주먹이든 둘 다 꺼내기 가능 (주먹으로 시작했으면 geject로 구분해서 보냄)
                if (Time.time - pressTime >= dragDelay)
                {
                    Send(dragIsGrab ? "geject" : "eject", lastSentUV.x, lastSentUV.y, dragMonitorIndex);
                    holdWithGrab = grab;   // 이 창을 놓았는지 판정할 기준 제스처
                    ejected = true; waitingForSpawn = true;
                    waitingForSpawnSince = Time.time;
                    releasedBeforeSpawn = false;
                    Debug.Log($"[HandRay:{handId}] {(dragIsGrab ? "geject" : "eject")} 전송, spawn 대기");
                }
            }
        }
        else if (justDeactivate)
        {
            if (dragging) Send(dragIsGrab ? "gup" : "up", u, v, dragMonitorIndex);
            dragging = false;
        }

        if (!pinch) pinchedMonitorT = null;   // 손을 떼면 연결 시작점도 해제

        wasPinch = pinch;
        wasGrab = grab;
        wasActivate = activate;
    }

    // 손 조작이 꺼질 때(박수) 진행 중이던 상호작용을 전부 안전하게 정리한다.
    // 이걸 안 하면 "잡은 채로 꺼짐" 상태가 남아서 다시 켰을 때 이상하게 동작한다.
    [Header("가까이 모드 (검지 터치)")]
    [Tooltip("켜면 화면 가까이에서는 레이를 끄고 검지로 직접 누른다")]
    public bool nearTouchMode = true;
    [Tooltip("검지 끝이 화면/창에서 이 거리(m) 안으로 오면 가까이 모드")]
    public float nearEnter = 0.10f;
    [Tooltip("이 거리(m) 밖으로 나가면 다시 레이 모드 (enter보다 크게 → 경계에서 깜빡이지 않음)")]
    public float nearExit = 0.15f;
    public bool IsNearMode { get; private set; }

    [Tooltip("왼손은 화면/창 가까이 오면 무조건 꺼짐 (레이·핀치·주먹 전부). 잡고 있던 것도 놓는다")]
    public bool leftHandOffWhenNear = true;
    [Tooltip("왼손이 화면/창 사각형에서 이 거리(m) 안이면 꺼짐")]
    public float leftNearEnter = 0.15f;
    [Tooltip("이 거리(m) 밖으로 나가야 다시 켜짐")]
    public float leftNearExit = 0.22f;

    public bool IsLeftHand()
    {
        var sk = skeleton != null ? skeleton : (hand != null ? hand.GetComponent<OVRSkeleton>() : null);
        if (sk != null)
        {
            var t = sk.GetSkeletonType();
            if (t == OVRSkeleton.SkeletonType.HandLeft || t == OVRSkeleton.SkeletonType.XRHandLeft) return true;
            if (t == OVRSkeleton.SkeletonType.HandRight || t == OVRSkeleton.SkeletonType.XRHandRight) return false;
        }
        // 뼈대 정보가 없으면 오브젝트 이름으로 (LeftHandAnchor 밑 등)
        for (var tr = hand != null ? hand.transform : transform; tr != null; tr = tr.parent)
            if (tr.name.IndexOf("left", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    void UpdateNearMode()
    {
        // [AR LAPTOP] 왼손: 가까우면 무조건 비활성화 (잡는 중이어도 놓음)
        if (leftHandOffWhenNear && IsLeftHand())
        {
            bool nearNow = IsNearMode;   // 손을 잠깐 놓치면 직전 상태 유지 (켜졌다 꺼졌다 방지)
            if (hand != null && hand.IsTracked)
            {
                // 검지 끝과 손바닥 중 더 가까운 쪽으로, 화면/창 사각형까지 실제 거리를 잰다
                var lt = FingerTouch.IndexTip(this);
                float ld = FingerTouch.NearestRectDistance(hand.transform.position);
                if (lt != null) ld = Mathf.Min(ld, FingerTouch.NearestRectDistance(lt.position));
                nearNow = IsNearMode ? ld < leftNearExit : ld < leftNearEnter;
            }
            if (nearNow && !IsNearMode) ReleaseAllInteractions();
            IsNearMode = nearNow;
            return;
        }

        bool busy = wasPinch || wasGrab || dragging || heldQuad != null || grabbedQuad != null;
        if (!nearTouchMode || busy || hand == null || !hand.IsTracked)
        {
            if (!busy) IsNearMode = false;
            return;
        }
        var tip = FingerTouch.IndexTip(this);
        if (tip == null) { IsNearMode = false; return; }
        float d = FingerTouch.NearestSurfaceDistance(tip.position);
        IsNearMode = IsNearMode ? d < nearExit : d < nearEnter;
    }

    void ReleaseAllInteractions()
    {
        if (resizingEdge)
        {
            if (activeResizer != null) activeResizer.EndResize();
            resizingEdge = false; activeResizer = null;
        }
        if (carriedTether != null)
        {
            carriedTether.EndCarry();
            carriedTether = null;
        }
        if (spawnDragOwner != null)
        {
            spawnDragOwner.EndDrag(null);   // 연결 안 하고 취소
            spawnDragOwner = null;
        }
        if (grabbedQuad != null)
        {
            if (grabbedResize != null) grabbedResize.OnGrabEnd(handId);
            grabbedQuad = null; grabbedResize = null; wasTwoHandedForMe = false;
        }
        if (clickTargetQuad != null) { clickTargetQuad = null; clickTarget = null; }
        if (heldQuad != null) SetQuadRaycastBlocking(heldQuad, true);
        heldQuad = null;
        waitingForSpawn = false;
        releasedBeforeSpawn = false;

        if (dragging)
        {
            // 데스크탑에서 드래그 중이었으면 마우스 버튼을 놓아준다 (안 그러면 PC에 눌린 채로 남음)
            Send(dragIsGrab ? "gup" : "up", lastSentUV.x, lastSentUV.y, dragMonitorIndex);
            dragging = false;
        }
        ejected = false;
    }

    void HandleSpawn(string msg)
    {
        var parts = msg.Split(',');
        if (parts.Length != 4 || parts[0] != "spawn") return;
        long id = long.Parse(parts[1]);
        float w = float.Parse(parts[2]);
        float h = float.Parse(parts[3]);
        if (spawnQuadPrefab == null) { waitingForSpawn = false; return; }

        Transform originT = (hand != null && hand.IsTracked && hand.PointerPose != null)
            ? hand.PointerPose
            : (pointerPoseFallback != null ? pointerPoseFallback : transform);

        Vector3 start = originT.position;
        Vector3 direction = originT.forward;
        float sd = holdDistance;
        if (holdInFrontOfHit && lastHitDist > 0f)
            sd = lastHitDist - holdFrontGap;
        sd = Mathf.Max(minDistance, sd * Mathf.Max(0.05f, holdDistanceScale));
        Vector3 spawnPos = start + direction * sd;

        // 생성 자세: 레이 방향을 보게 (= 내 쪽을 향함)
        Quaternion spawnRot = (spawnAlignToMonitor && lastMonitorT != null)
                            ? lastMonitorT.rotation
                            : (direction.sqrMagnitude > 1e-6f
                                 ? Quaternion.LookRotation(direction.normalized, Vector3.up)
                                 : Quaternion.identity);
        GameObject q = Instantiate(spawnQuadPrefab, spawnPos, spawnRot);

        // 지금 손 자세를 기준으로 "손↔창 상대 회전"을 기억한다.
        // 이후 손목을 돌리면 이 상대 관계를 유지한 채 창도 같이 돌아간다.
        heldOffsetRot = Quaternion.Inverse(lastHandRot) * spawnRot;
        q.transform.localScale = new Vector3(w * pixelsToMeters, h * pixelsToMeters, 1f);
        var recv = q.GetComponent<WindowQuadReceiver>();
        if (recv != null) { recv.sourceMonitorIndex = dragMonitorIndex; recv.Init(id); }
        waitingForSpawn = false;

        // 선 조건이면, 꺼낸 모니터와 새 창을 화살표로 자동 연결한다.
        // (지우고 싶으면 선 가운데 손잡이를 잡아서 멀리 끌면 사라지고,
        //  다시 잇고 싶으면 양손으로 두 곳을 꼬집으면 된다)
        if (QuadLinkManager.Instance != null)
            QuadLinkManager.Instance.AutoLinkFromMonitor(dragMonitorIndex, q.transform);

        if (releasedBeforeSpawn)
        {
            // 응답 오기 전에 이미 손을 놓은 상태였으면, 들고 다니지 말고 지금 위치에 바로 배치
            releasedBeforeSpawn = false;
            Debug.Log($"[HandRay:{handId}] 쿼드 생성(이미 손 놓은 상태라 바로 배치) id={id} 창={w}x{h}");
        }
        else
        {
            heldQuad = q;
            // 손에 들고 있는 동안엔 레이가 이 창을 통과하게 한다. 안 그러면 들고 있는 창이
            // 레이를 먼저 막아서 뒤쪽 데스크탑 판을 못 맞히고, "다시 집어넣기"가 안 된다.
            SetQuadRaycastBlocking(q, false);
            Debug.Log($"[HandRay:{handId}] 쿼드 생성 id={id} 창={w}x{h}");
        }
    }

    // 창(쿼드)이 레이캐스트를 막을지 여부. 들고 있는 동안은 꺼서 레이가 뒤로 통과하게 한다.
    // 가장자리/꼭짓점 핸들도 같이 꺼야 한다 - 핸들 콜라이더가 넉넉해서 그것만으로도 레이를 막는다.
    static void SetQuadRaycastBlocking(GameObject quad, bool blocking)
    {
        if (quad == null) return;
        var cols = quad.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
            if (cols[i] != null) cols[i].enabled = blocking;
        var er = quad.GetComponent<EdgeResizer>();
        if (er != null) er.SetHandlesActive(blocking);
    }

    // 지정한 손끝 뼈(tipBoneId)가 손목에서 얼마나 가까운지로 "말림 정도"를 0~1로 계산.
    // 가까울수록(=많이 말림) 1에 가깝고, curlOpenDistance보다 멀면 0.
    float FingerCurl(OVRSkeleton.BoneId tipBoneId)
    {
        Transform tip = null;
        Transform wrist = null;
        var bones = skeleton.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].Id == tipBoneId) tip = bones[i].Transform;
            else if (bones[i].Id == OVRSkeleton.BoneId.Hand_WristRoot) wrist = bones[i].Transform;
        }
        if (tip == null || wrist == null) return 0f;

        float dist = Vector3.Distance(tip.position, wrist.position);
        return Mathf.Clamp01(Mathf.InverseLerp(curlOpenDistance, curlClosedDistance, dist));
    }

    // 쿼드가 손 방향이 아니라 "사용자 머리 쪽"을 향하도록. 손 방향 기준으로 하면 손을
    // 옆으로 향할 때 쿼드가 사용자 시점 기준으로 삐딱하게 회전하는 문제가 있었음.
    Quaternion FaceHeadRotation(Vector3 worldPos)
    {
        Transform head = headTransform != null ? headTransform : (Camera.main != null ? Camera.main.transform : null);
        if (head == null) return Quaternion.identity;
        Vector3 faceDir = head.position - worldPos;
        if (faceDir.sqrMagnitude < 0.0001f) return Quaternion.identity;
        return Quaternion.LookRotation(faceDir, Vector3.up);
    }

    // 지금 손의 전체 회전(baseRot, 롤 포함) 기준으로, 잡은 창의 상대 위치/회전을 다시 계산해둔다.
    // (첫 grab 시작 시, 그리고 양손->한손으로 전환되는 순간에 호출해서 점프 없이 이어지게 함)
    void RecalibrateGrabOffset(Vector3 start, Quaternion baseRot, Transform quad)
    {
        grabOffsetLocalPos = Quaternion.Inverse(baseRot) * (quad.position - start);
        grabOffsetLocalRot = Quaternion.Inverse(baseRot) * quad.rotation;
    }

    // 값이 커지는 방향(attack)은 빠르게, 작아지는 방향(release)은 천천히 따라가는 스무딩.
    void SmoothAttackRelease(ref float smoothed, float raw)
    {
        bool rising = raw > smoothed;
        float rate = rising ? fingerAttackSmoothing : fingerReleaseSmoothing;
        if (rate <= 0f) { smoothed = raw; return; }
        float t = 1f - Mathf.Exp(-rate * Time.deltaTime);
        smoothed = Mathf.Lerp(smoothed, raw, t);
    }

    void Send(string type, float u, float v, int mon)
    {
        if (sender == null) return;
        sender.SendText(string.Format("{0},{1:F4},{2:F4},{3}", type, u, v, mon));
    }

    void Send(string type, float u, float v)
    {
        if (sender == null) return;
        sender.SendText(string.Format("{0},{1:F4},{2:F4}", type, u, v));
    }

    void SendW(string type, long id, float u, float v)
    {
        if (sender == null) return;
        sender.SendText(string.Format("{0},{1},{2:F4},{3:F4}", type, id, u, v));
    }

    void SendW(string type, long id, float u, float v, int mon)
    {
        if (sender == null) return;
        sender.SendText(string.Format("{0},{1},{2:F4},{3:F4},{4}", type, id, u, v, mon));
    }

    // 꺼낸 창 스크롤: "wscroll,id,u,v,delta" (delta 120 = 휠 한 칸)
    void SendScroll(long id, float u, float v, int delta)
    {
        if (sender == null) return;
        sender.SendText(string.Format("wscroll,{0},{1:F4},{2:F4},{3}", id, u, v, delta));
    }

    void SetColor(Color c)
    {
        line.startColor = c; line.endColor = c;
        if (mat != null)
        {
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", c);
        }
    }
}
