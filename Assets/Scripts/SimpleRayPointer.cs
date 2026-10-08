using UnityEngine;

// 레이저: 모니터 조작 + 창 꺼내기/배치 + 놓인 창 클릭/재조작.
// 모니터(투명판): 짧게=클릭, 길게+드래그=드래그/꺼내기
// 놓인 창 쿼드:
//   검지(트리거)로 클릭 = 그 창 클릭
//   중지(그랩)로 쥐기   = 그 창을 잡아 이동 + 조이스틱 상하=거리, 좌우=크기
[RequireComponent(typeof(LineRenderer))]
public class SimpleRayPointer : MonoBehaviour
{
    public float length = 10f;
    public float width = 0.003f;
    public Color idleColor = Color.white;
    public Color clickColor = Color.green;
    public float triggerThreshold = 0.5f;
    public LayerMask hitLayers = ~0;

    [Header("드래그 시작까지 기다리는 시간(초)")]
    public float dragDelay = 0.5f;

    [Header("전송 최적화")]
    public float sendRate = 60f;
    public float minMoveDelta = 0.002f;

    [Header("연결 (비워두면 자동으로 찾음)")]
    public PCStreamReceiver sender;
    public Renderer targetQuadRenderer;

    [Header("꺼낸 창을 띄울 새 쿼드 프리팹")]
    public GameObject spawnQuadPrefab;

    [Header("새 쿼드 크기 = 창 픽셀 × 이 배율")]
    public float pixelsToMeters = 0.0005f;

    [Header("들고 있을 때 거리(m)")]
    public float holdDistance = 0.8f;

    [Header("조이스틱 거리 조절")]
    public float distanceSpeed = 1.5f;
    public float minDistance = 0.3f;
    public float maxDistance = 5f;

    [Header("조이스틱 크기 조절")]
    public float resizeSpeed = 1.5f;
    public float minScale = 0.1f;
    public float maxScale = 3f;

    private LineRenderer line;
    private Material mat;
    private bool wasPressed = false;
    private bool wasGrab = false;
    private float pressTime = 0f;
    private float lastSendTime = 0f;
    private Vector2 lastSentUV;

    private bool dragging = false;
    private bool ejected = false;
    private Vector3 lastRayEnd;

    private int hitMonitorIndex = 0;    // 이번 프레임에 맞힌 판의 모니터 index
    private int dragMonitorIndex = 0;   // 드래그 시작 시 고정 (move/eject/up에 사용)
    private string lastHitInfo = "none";  // 디버그: 레이가 맞힌 것
    private bool dbgHitQuad = false;
    public bool showRayDebug = true;      // 화면 오버레이 디버그

    private GameObject heldQuad = null;   // 새로 꺼내서 든 것
    private bool waitingForSpawn = false;

    private GameObject grabbedQuad = null;      // 중지로 잡은 놓인 창
    private float grabHoldDistance = 0.8f;

    private GameObject clickTargetQuad = null;  // 검지로 클릭하려고 누른 놓인 창
    private WindowQuadReceiver clickTarget = null;
    private Vector2 clickDownUV;

    // === 가장자리(한 변) 리사이즈 상태 ===
    private bool resizingEdge = false;
    private EdgeResizer activeResizer = null;
    private EdgeResizer.Edge activeEdge;

    // === 연결선(tether) 치우기 상태 ===
    private ITetherCarry carriedTether = null;
    private float carryDist = 0.5f;

    // === 데스크탑 가장자리에서 화살표를 끌어내 AR 창에 잇는 중 ===
    private DesktopTetherSpawner spawnDragOwner = null;

    void Start()
    {
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
        OVRInput.Update();

        if (sender != null && sender.TryGetSpawnMessage(out string spawnMsg))
            HandleSpawn(spawnMsg);

        Vector3 start = transform.position;
        Vector3 direction = transform.forward;

        Vector3 end;
        bool hitQuad = false;
        Vector2 uv = Vector2.zero;
        WindowQuadReceiver hitWindow = null;
        WindowEdgeHandle hitHandle = null;   // 이번 프레임 레이가 맞힌 가장자리 핸들
        TetherHandle hitTetherHandle = null; // 이번 프레임 레이가 맞힌 연결선 손잡이
        DesktopEdgeHandle hitDesktopHandle = null; // 데스크탑 가장자리 핸들 (여기서 화살표를 끌어냄)
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
            lastHitInfo = hit.collider.name;   // 디버그: 맞힌 콜라이더 이름
            if (hitWindow != null) winUV = QuadUV(hitWindow.transform, hit.point);
            else
            {
                // 모니터 판: MonitorPlane 컴포넌트가 있으면 그 monitorIndex 사용
                var plane = hit.collider.GetComponentInParent<MonitorPlane>();
                if (plane != null)
                {
                    hitQuad = true;
                    uv = QuadUV(plane.transform, hit.point);
                    hitMonitorIndex = plane.monitorIndex;
                    lastHitInfo += $" (MonitorPlane idx={plane.monitorIndex})";
                }
                // (구버전 호환) MonitorPlane 없이 targetQuadRenderer만 쓰던 경우 → 주 모니터
                else if (targetQuadRenderer != null && hit.collider.GetComponent<Renderer>() == targetQuadRenderer)
                {
                    hitQuad = true;
                    uv = QuadUV(hit.collider.transform, hit.point);
                    hitMonitorIndex = 0;
                }
            }
        }
        else { end = start + direction * length; lastHitInfo = "none(허공)"; }
        lastRayEnd = end;
        dbgHitQuad = hitQuad;   // 화면 오버레이용

        line.SetPosition(0, start);
        line.SetPosition(1, end);

        // 검지 트리거 (양손)
        float rIndex = OVRInput.Get(OVRInput.Axis1D.SecondaryIndexTrigger);
        float lIndex = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger);
        bool pressed = Mathf.Max(rIndex, lIndex) >= triggerThreshold;

        // 중지 그랩 (양손)
        float rGrip = OVRInput.Get(OVRInput.Axis1D.SecondaryHandTrigger);
        float lGrip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger);
        bool grab = Mathf.Max(rGrip, lGrip) >= 0.5f;

        // 그랩을 쥐고 있는 동안엔 데스크탑 가장자리 핸들을 통과시킨다 (창 꺼낼 때 방해되지 않도록)
        if (grab && DesktopTetherSpawner.Instance != null)
            DesktopTetherSpawner.Instance.NotifyPassThrough();

        SetColor(pressed ? clickColor : idleColor);

        Vector2 rStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        Vector2 lStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        float stickX = Mathf.Abs(rStick.x) > Mathf.Abs(lStick.x) ? rStick.x : lStick.x;
        float stickY = Mathf.Abs(rStick.y) > Mathf.Abs(lStick.y) ? rStick.y : lStick.y;

        float u = uv.x;
        float v = 1f - uv.y;
        Vector2 sendUV = new Vector2(u, v);

        bool justPressed = pressed && !wasPressed;
        bool justReleased = !pressed && wasPressed;

        if (justPressed)
            Debug.Log($"[Ray] 트리거: 맞힌것='{lastHitInfo}'  hitQuad={hitQuad}  mon={hitMonitorIndex}");
        bool justGrab = grab && !wasGrab;
        bool justUngrab = !grab && wasGrab;

        // === 중지로 놓인 창 잡기 (최우선) ===
        if (grabbedQuad != null && grab)
        {
            if (Mathf.Abs(stickY) > 0.1f)
                grabHoldDistance += stickY * distanceSpeed * Time.deltaTime;
            grabHoldDistance = Mathf.Clamp(grabHoldDistance, minDistance, maxDistance);
            if (Mathf.Abs(stickX) > 0.1f)
            {
                float factor = 1f + stickX * resizeSpeed * Time.deltaTime;
                Vector3 s = grabbedQuad.transform.localScale * factor;
                float clamped = Mathf.Clamp(s.x, minScale, maxScale) / Mathf.Max(s.x, 0.0001f);
                grabbedQuad.transform.localScale = s * clamped;
            }
            Vector3 hp = start + direction * grabHoldDistance;
            grabbedQuad.transform.position = hp;
            Vector3 ld = hp - start;
            if (ld.sqrMagnitude > 0.0001f) grabbedQuad.transform.rotation = Quaternion.LookRotation(ld);
        }

        // === 연결선(tether) 잡아서 치우기 === (선 중앙 구체를 그랩으로 잡고 멀리 치우면 삭제)
        if (carriedTether != null)
        {
            if (grab)
            {
                Vector3 cp = start + direction * carryDist;
                carriedTether.UpdateCarry(cp);
            }
            if (justUngrab)
            {
                carriedTether.EndCarry();
                carriedTether = null;
            }
        }
        else if (justGrab && grabbedQuad == null && hitTetherHandle != null && hitTetherHandle.tether != null)
        {
            carriedTether = hitTetherHandle.tether;
            carryDist = Vector3.Distance(start, hitTetherHandle.transform.position);
            carriedTether.BeginCarry();
            Debug.Log("[Ray] 연결선 잡음");
        }

        // 중지 쥐는 순간: 가리킨 놓인 창 잡기 (연결선을 잡고 있는 중엔 창은 안 잡음)
        if (justGrab && grabbedQuad == null && carriedTether == null && hitWindow != null)
        {
            grabbedQuad = hitWindow.gameObject;
            grabHoldDistance = Mathf.Clamp(Vector3.Distance(start, grabbedQuad.transform.position), minDistance, maxDistance);
            Debug.Log("[Ray] 중지로 놓인 창 잡음");
        }
        // 중지 놓는 순간: 배치
        if (justUngrab && grabbedQuad != null)
        {
            Debug.Log("[Ray] 놓인 창 배치 완료");
            grabbedQuad = null;
        }

        // === 새로 꺼내서 들고 있는 중 ===
        if (heldQuad != null && pressed)
        {
            if (Mathf.Abs(stickY) > 0.1f)
                holdDistance += stickY * distanceSpeed * Time.deltaTime;
            holdDistance = Mathf.Clamp(holdDistance, minDistance, maxDistance);
            Vector3 hp = start + direction * holdDistance;
            heldQuad.transform.position = hp;
            Vector3 ld = hp - start;
            if (ld.sqrMagnitude > 0.0001f) heldQuad.transform.rotation = Quaternion.LookRotation(ld);
        }

        // === 검지 트리거: 상태 전이 ===
        // 가장자리 리사이즈가 최우선: 트리거로 핸들을 잡고 끄는 동안은 다른 상호작용을 막는다.
        if (resizingEdge)
        {
            if (pressed && activeResizer != null) activeResizer.UpdateResize(start, direction);
            if (justReleased || activeResizer == null)
            {
                if (activeResizer != null) activeResizer.EndResize();
                resizingEdge = false;
                activeResizer = null;
                Debug.Log("[Ray] 가장자리 리사이즈 끝");
            }
        }
        // 데스크탑 가장자리에서 화살표 끌어내는 중: 놓는 순간 가리키던 AR 창에 연결선을 다시 만든다
        else if (spawnDragOwner != null)
        {
            if (pressed) spawnDragOwner.UpdateDrag(end);
            if (justReleased)
            {
                spawnDragOwner.EndDrag(hitWindow);
                spawnDragOwner = null;
            }
        }
        // 데스크탑 가장자리 핸들을 트리거로 잡음 = 화살표 끌어내기 시작
        else if (justPressed && hitDesktopHandle != null && hitDesktopHandle.owner != null && grabbedQuad == null)
        {
            spawnDragOwner = hitDesktopHandle.owner;
            spawnDragOwner.BeginDrag(hitDesktopHandle, end);
            Debug.Log("[Ray] 데스크탑에서 화살표 끌어내기 시작");
        }
        // 트리거 누르는 순간 레이가 가장자리 핸들에 맞아 있으면 = 그 변을 늘리기 시작
        else if (justPressed && hitHandle != null && hitHandle.owner != null && grabbedQuad == null)
        {
            activeResizer = hitHandle.owner;
            activeEdge = hitHandle.edge;
            activeResizer.BeginResize(activeEdge, start, direction);
            resizingEdge = true;
            Debug.Log("[Ray] 가장자리 리사이즈 시작 edge=" + activeEdge);
        }
        else if (heldQuad != null)
        {
            if (justReleased) { Debug.Log("[Ray] 배치 완료"); heldQuad = null; dragging = false; }
        }
        else if (clickTargetQuad != null)
        {
            // 놓인 창을 검지로 눌렀다 뗌 = 클릭
            if (justReleased)
            {
                long id = clickTarget.windowId;
                SendW("wclick", id, clickDownUV.x, clickDownUV.y);
                Debug.Log($"[Ray] 꺼낸 창 클릭 id={id}");
                clickTargetQuad = null; clickTarget = null;
            }
        }
        else if (waitingForSpawn)
        {
            if (justReleased) { waitingForSpawn = false; dragging = false; }
        }
        else if (justPressed && hitWindow != null && grabbedQuad == null)
        {
            // 검지로 놓인 창 누름 = 클릭 대기
            clickTargetQuad = hitWindow.gameObject;
            clickTarget = hitWindow;
            clickDownUV = new Vector2(winUV.x, 1f - winUV.y);
        }
        else if (justPressed && hitQuad)
        {
            pressTime = Time.time;
            lastSentUV = sendUV;
            dragging = true;
            ejected = false;
            dragMonitorIndex = hitMonitorIndex;   // 이 드래그가 어느 모니터인지 고정
            Send("down", u, v, dragMonitorIndex);
        }
        else if (pressed && dragging && !ejected)
        {
            if (hitQuad)
            {
                // 누르는 즉시 커서가 따라간다 (진짜 마우스처럼)
                bool rateOk = (Time.time - lastSendTime) >= (1f / sendRate);
                bool moved = (sendUV - lastSentUV).magnitude >= minMoveDelta;
                if (rateOk && moved) { Send("move", u, v, dragMonitorIndex); lastSendTime = Time.time; lastSentUV = sendUV; }
            }
            else
            {
                // 쿼드 밖으로 나감 = 꺼내기 (실수 방지로 약간의 시간은 유지)
                if (Time.time - pressTime >= dragDelay)
                {
                    Send("eject", lastSentUV.x, lastSentUV.y, dragMonitorIndex);
                    ejected = true; waitingForSpawn = true;
                    Debug.Log("[Ray] eject 전송, spawn 대기");
                }
            }
        }
        else if (justReleased)
        {
            if (dragging) Send("up", u, v, dragMonitorIndex);
            dragging = false;
        }

        wasPressed = pressed;
        wasGrab = grab;
    }

    void HandleSpawn(string msg)
    {
        var parts = msg.Split(',');
        if (parts.Length != 4 || parts[0] != "spawn") return;
        long id = long.Parse(parts[1]);
        float w = float.Parse(parts[2]);
        float h = float.Parse(parts[3]);
        if (spawnQuadPrefab == null) { waitingForSpawn = false; return; }

        Vector3 start = transform.position;
        Vector3 direction = transform.forward;
        Vector3 spawnPos = start + direction * holdDistance;

        GameObject q = Instantiate(spawnQuadPrefab, spawnPos, Quaternion.identity);
        q.transform.localScale = new Vector3(w * pixelsToMeters, h * pixelsToMeters, 1f);
        var recv = q.GetComponent<WindowQuadReceiver>();
        if (recv != null) { recv.sourceMonitorIndex = dragMonitorIndex; recv.Init(id); }
        heldQuad = q;
        waitingForSpawn = false;
        Debug.Log($"[Ray] 쿼드 생성 id={id} 창={w}x{h}");
    }

    void Send(string type, float u, float v, int mon)
    {
        if (sender == null) return;
        sender.SendText(string.Format("{0},{1:F4},{2:F4},{3}", type, u, v, mon));
    }

    void SendW(string type, long id, float u, float v)
    {
        if (sender == null) return;
        sender.SendText(string.Format("{0},{1},{2:F4},{3:F4}", type, id, u, v));
    }

    void OnGUI()
    {
        if (!showRayDebug) return;
        var style = new GUIStyle(GUI.skin.box) { fontSize = 20, alignment = TextAnchor.UpperLeft };
        string s = $"[Ray]\n맞힌것: {lastHitInfo}\nhitQuad: {dbgHitQuad}   mon: {hitMonitorIndex}";
        GUI.Label(new Rect(20, 250, 700, 110), s, style);
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

    // ─────────────────────────────────────────────────────────────────────────
    // 쿼드 위의 UV를 직접 계산한다.
    //
    // 원래는 hit.textureCoord를 썼는데, 이건 MeshCollider에서만 값이 나온다.
    // 게다가 빌드(APK)에서는 유니티 기본 Quad 메시가 "읽기 불가"라 MeshCollider로도
    // (0,0)만 돌아온다. → 클릭/드래그/잡기가 전부 좌상단 구석으로 가버린다.
    //
    // 쿼드 메시는 로컬 XY 평면의 1x1 사각형(중심 원점)이므로,
    // 로컬 좌표에 0.5를 더하면 그게 곧 UV다. 콜라이더 종류와 무관하게 동작한다.
    // ─────────────────────────────────────────────────────────────────────────
    static Vector2 QuadUV(Transform quad, Vector3 worldPoint)
    {
        if (quad == null) return Vector2.zero;
        Vector3 local = quad.InverseTransformPoint(worldPoint);
        return new Vector2(Mathf.Clamp01(local.x + 0.5f), Mathf.Clamp01(local.y + 0.5f));
    }
}