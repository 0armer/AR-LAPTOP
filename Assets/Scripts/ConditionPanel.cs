using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// 조건 전환 패널 (헤드셋 안에서 손으로 눌러서 조건을 바꾼다)
//
//   ┌─────────────────────────────┐
//   │  CONDITION                  │
//   │  ┌─────────┐  ┌─────────┐   │
//   │  │ 1 MON   │  │ 1 MON   │   │   왼쪽 열 = 선 없음
//   │  │ NO LINK │  │ LINK    │   │   오른쪽 열 = 선 있음
//   │  ├─────────┤  ├─────────┤   │
//   │  │ 2 MON   │  │ 2 MON   │   │   위 행 = 모니터 1대
//   │  │ NO LINK │  │ LINK    │   │   아래 행 = 모니터 2대
//   │  └─────────┘  └─────────┘   │
//   │  [ CLEAR ]        M2L1      │
//   └─────────────────────────────┘
//
// · 지금 조건인 버튼은 밝게 채워진다.
// · 버튼을 누르면 창을 전부 데스크탑으로 되돌리고, 조건을 바꾸고,
//   기록 파일을 새로 연다 (조건마다 파일이 따로 남는다).
// · 태스크가 진행 중일 때는 자동으로 숨는다 — 참가자가 실수로 누르지 않게.
// · 글자는 영문/숫자만 쓴다 (헤드셋 기본 폰트에 한글 글리프가 없을 수 있어서).
//
// 씬에 아무것도 안 넣어도 실행 시 자동 생성된다.
// ─────────────────────────────────────────────────────────────────────────────
public class ConditionPanel : MonoBehaviour
{
    public static ConditionPanel Instance { get; private set; }

    // 기본적으로는 안 만든다. 조건은 유니티 에디터의 [실험] 메뉴에서 고른다.
    // 헤드셋 안에서 손으로 바꾸고 싶을 때만 ConditionManager의
    // showPanelInHeadset을 켜면 이 패널이 뜬다.
    public static void Show()
    {
        if (Instance != null) return;
        var go = new GameObject("ConditionPanel");
        DontDestroyOnLoad(go);
        go.AddComponent<ConditionPanel>();
    }
    public static void Hide()
    {
        if (Instance != null) Destroy(Instance.gameObject);
    }

    [Header("배치")]
    [Tooltip("모니터 왼쪽으로 얼마나 떨어뜨릴지 (m). 참가자 작업 공간과 겹치지 않게.")]
    public float sideOffset = 0.62f;
    [Tooltip("모니터 기준 높이 차 (m)")]
    public float heightOffset = -0.12f;
    [Tooltip("모니터보다 앞으로 (m)")]
    public float forwardOffset = 0.22f;
    public float panelWidth = 0.34f;
    public float panelHeight = 0.26f;

    [Header("동작")]
    [Tooltip("태스크가 진행 중이면 패널을 숨긴다 (참가자 오조작 방지)")]
    public bool hideDuringTask = true;
    [Tooltip("꼬집기 세기가 이 값을 넘으면 눌린 것으로 본다")]
    public float pinchThreshold = 0.6f;
    public float rayLength = 3f;

    // 버튼 표식
    public class CondButton : MonoBehaviour
    {
        public int monitors;      // 1 또는 2
        public bool links;
        public bool isClear;      // "CLEAR" 버튼
        public Renderer fill;
        public Transform visual;
    }

    private Transform root;
    private readonly List<CondButton> buttons = new();
    private Material matIdle, matHover, matActive, matPanel, matClear;
    private TextMesh statusText;
    private readonly Dictionary<int, bool> wasPinching = new();
    private CondButton hovered;
    private bool built = false;

    void Awake() { Instance = this; }

    void Update()
    {
        if (!built) { TryBuild(); return; }

        // 태스크 중에는 숨긴다
        bool show = true;   // [AR LAPTOP] 기록(태스크) 기능 제거
        if (root.gameObject.activeSelf != show) root.gameObject.SetActive(show);
        if (!show) { hovered = null; return; }

        RefreshHighlight();
        HandleHands();
    }

    // ── 만들기 ───────────────────────────────────────────────────────────────
    void TryBuild()
    {
        // 모니터 판이 생긴 다음에 그 옆에 붙인다 (MRUK 앵커는 조금 늦게 뜬다)
        MonitorPlane m0 = null;
        foreach (var m in FindObjectsByType<MonitorPlane>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (m.monitorIndex == 0) { m0 = m; break; }

        Vector3 pos; Quaternion rot;
        if (m0 != null)
        {
            var t = m0.transform;
            pos = t.position - t.right * sideOffset + t.up * heightOffset + t.forward * forwardOffset;
            rot = t.rotation;
        }
        else
        {
            var cam = Camera.main;
            if (cam == null) return;                       // 카메라도 없으면 다음 프레임에
            pos = cam.transform.position + cam.transform.forward * 0.8f - cam.transform.right * 0.5f;
            rot = Quaternion.LookRotation(pos - cam.transform.position);
        }

        matPanel  = DesktopTetherSpawner.MakeUnlit(new Color(0.06f, 0.09f, 0.12f), 0.86f);
        matIdle   = DesktopTetherSpawner.MakeUnlit(new Color(0.22f, 0.28f, 0.34f), 0.95f);
        matHover  = DesktopTetherSpawner.MakeUnlit(new Color(0.38f, 0.50f, 0.58f), 1f);
        matActive = DesktopTetherSpawner.MakeUnlit(new Color(0.10f, 0.68f, 0.80f), 1f);
        matClear  = DesktopTetherSpawner.MakeUnlit(new Color(0.45f, 0.22f, 0.24f), 0.95f);

        root = new GameObject("ConditionPanel_Root").transform;
        root.SetPositionAndRotation(pos, rot);

        // 바탕
        var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bg.name = "bg";
        Destroy(bg.GetComponent<Collider>());
        bg.transform.SetParent(root, false);
        bg.transform.localPosition = new Vector3(0, 0, 0.001f);
        bg.transform.localScale = new Vector3(panelWidth, panelHeight, 1f);
        bg.GetComponent<Renderer>().sharedMaterial = matPanel;

        MakeText("CONDITION", new Vector3(-panelWidth / 2 + 0.018f, panelHeight / 2 - 0.022f, 0f),
                 0.010f, TextAnchor.MiddleLeft, new Color(0.55f, 0.68f, 0.76f));

        // 2×2 버튼
        float bw = panelWidth * 0.42f, bh = panelHeight * 0.26f;
        float gx = panelWidth * 0.235f, gy = panelHeight * 0.145f;
        AddButton(1, false, new Vector3(-gx,  gy, 0f), bw, bh, "1 MON", "NO LINK");
        AddButton(1, true,  new Vector3( gx,  gy, 0f), bw, bh, "1 MON", "LINK");
        AddButton(2, false, new Vector3(-gx, -gy, 0f), bw, bh, "2 MON", "NO LINK");
        AddButton(2, true,  new Vector3( gx, -gy, 0f), bw, bh, "2 MON", "LINK");

        // CLEAR + 현재 조건
        AddClear(new Vector3(-gx, -panelHeight / 2 + 0.028f), bw, bh * 0.62f);
        statusText = MakeText(ConditionManager.Code,
                              new Vector3(gx, -panelHeight / 2 + 0.028f, 0f),
                              0.014f, TextAnchor.MiddleCenter, new Color(0.55f, 0.90f, 1f));

        built = true;
        Debug.Log("[CondPanel] 조건 패널 생성됨");
    }

    void AddButton(int monitors, bool links, Vector3 local, float w, float h, string l1, string l2)
    {
        var go = new GameObject($"btn_M{monitors}L{(links ? 1 : 0)}");
        go.transform.SetParent(root, false);
        go.transform.localPosition = local;

        var col = go.AddComponent<BoxCollider>();
        col.size = new Vector3(w, h, 0.03f);

        var vis = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(vis.GetComponent<Collider>());
        vis.transform.SetParent(go.transform, false);
        vis.transform.localScale = new Vector3(w, h, 1f);
        var rend = vis.GetComponent<Renderer>();
        rend.sharedMaterial = matIdle;

        var b = go.AddComponent<CondButton>();
        b.monitors = monitors; b.links = links; b.fill = rend; b.visual = vis.transform;
        buttons.Add(b);

        MakeText(l1, local + new Vector3(0f,  h * 0.20f, -0.002f), 0.0115f, TextAnchor.MiddleCenter, Color.white);
        MakeText(l2, local + new Vector3(0f, -h * 0.22f, -0.002f), 0.0100f, TextAnchor.MiddleCenter, new Color(0.85f, 0.92f, 0.96f));
    }

    void AddClear(Vector3 local, float w, float h)
    {
        var go = new GameObject("btn_CLEAR");
        go.transform.SetParent(root, false);
        go.transform.localPosition = local;

        var col = go.AddComponent<BoxCollider>();
        col.size = new Vector3(w, h, 0.03f);

        var vis = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(vis.GetComponent<Collider>());
        vis.transform.SetParent(go.transform, false);
        vis.transform.localScale = new Vector3(w, h, 1f);
        vis.GetComponent<Renderer>().sharedMaterial = matClear;

        var b = go.AddComponent<CondButton>();
        b.isClear = true; b.fill = vis.GetComponent<Renderer>(); b.visual = vis.transform;
        buttons.Add(b);

        MakeText("CLEAR", local + new Vector3(0f, 0f, -0.002f), 0.0105f, TextAnchor.MiddleCenter, Color.white);
    }

    // 헤드셋 기본 폰트로 글자 만들기 (영문/숫자만 — 한글은 글리프가 없을 수 있다)
    TextMesh MakeText(string s, Vector3 local, float size, TextAnchor anchor, Color color)
    {
        var go = new GameObject("t_" + s);
        go.transform.SetParent(root, false);
        go.transform.localPosition = local + new Vector3(0, 0, -0.003f);
        var tm = go.AddComponent<TextMesh>();
        tm.text = s;
        tm.anchor = anchor;
        tm.alignment = TextAlignment.Center;
        tm.color = color;
        tm.fontSize = 64;
        tm.characterSize = size * 16f / 64f * 4f;   // 대략 size(m) 높이가 되도록
        tm.font = BuiltinFont();
        var r = go.GetComponent<MeshRenderer>();
        if (tm.font != null) r.sharedMaterial = tm.font.material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.transform.localScale = Vector3.one * 0.25f;
        return tm;
    }

    static Font builtinFont;
    static bool fontTried;
    static Font BuiltinFont()
    {
        if (fontTried) return builtinFont;
        fontTried = true;
        try { builtinFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (builtinFont == null) { try { builtinFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
        if (builtinFont == null) Debug.LogWarning("[CondPanel] 기본 폰트를 못 찾음 — 버튼 글자가 안 보일 수 있다");
        return builtinFont;
    }

    // ── 지금 조건 표시 ───────────────────────────────────────────────────────
    void RefreshHighlight()
    {
        foreach (var b in buttons)
        {
            if (b == null || b.fill == null) continue;
            if (b.isClear) { b.fill.sharedMaterial = (b == hovered) ? matHover : matClear; continue; }
            bool active = b.monitors == ConditionManager.MonitorCount && b.links == ConditionManager.LinksEnabled;
            // 강조는 색으로만 한다. 크기를 바꾸면 조준한 버튼이 움직여서 누르기 어려워진다.
            b.fill.sharedMaterial = active ? matActive : (b == hovered ? matHover : matIdle);
        }
        if (statusText != null) statusText.text = ConditionManager.Code;
    }

    // ── 손으로 누르기 ────────────────────────────────────────────────────────
    void HandleHands()
    {
        hovered = null;

        foreach (var p in HandRayPointer.All)
        {
            if (p == null || p.hand == null || !p.hand.IsTracked) continue;

            Transform pose = p.hand.PointerPose != null ? p.hand.PointerPose
                           : (p.pointerPoseFallback != null ? p.pointerPoseFallback : p.transform);

            CondButton hit = null;
            if (Physics.Raycast(pose.position, pose.forward, out RaycastHit h, rayLength))
                hit = h.collider.GetComponent<CondButton>();
            if (hit != null) hovered = hit;

            float pinch = 0f;
            try { pinch = p.hand.GetFingerPinchStrength(OVRHand.HandFinger.Index); } catch { }
            bool now = pinch >= pinchThreshold;

            int key = p.GetInstanceID();
            wasPinching.TryGetValue(key, out bool before);
            wasPinching[key] = now;

            if (now && !before && hit != null) Press(hit);
        }
    }

    void Press(CondButton b)
    {
        if (ConditionManager.Instance == null) return;

        if (b.isClear)
        {
            ConditionManager.Instance.ClearAllWindows();
            Debug.Log("[CondPanel] 창 정리");
            return;
        }
        if (b.monitors == ConditionManager.MonitorCount && b.links == ConditionManager.LinksEnabled)
        {
            Debug.Log("[CondPanel] 이미 그 조건");
            return;
        }
        ConditionManager.Instance.Apply(b.monitors, b.links);
    }

    void OnDestroy()
    {
        if (root != null) Destroy(root.gameObject);
        if (Instance == this) Instance = null;
    }
}
