using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

// [역할 분담]
//   · P (엄지끝·검지끝 중점)  → A 의 대각 반대편 모서리에 정확히 붙는다
//   · 엄지·검지의 기울기       → 판의 "법선"(= 판이 향하는 방향)만 조절한다. 손바닥/손목은 안 쓴다
//   · 판의 세로축              → 월드 업 기준으로 고정. 손가락을 평면 안에서 돌려도 사각형은 안 돈다
//     → 그래서 엄지·검지는 판의 X·Y 안에서 자유롭게 놀 수 있고,
//        두 손가락을 기울였을 때만 판이 기운다.
//
// [기울기를 손가락에서만 뽑는 법]
//   엄지 끝마디 방향  tDir = ThumbTip - Thumb3    (Thumb1 이 아니라 Thumb3 = 마지막 관절)
//   검지 끝마디 방향  fDir = IndexTip - Index3
//   법선 = tDir × fDir
//   → 뿌리 관절(Thumb1/Index1)을 쓰면 손바닥 자세가 섞여 들어온다.
//      끝마디만 쓰면 순수하게 두 손가락이 이루는 면만 잡힌다.
//
// [계산]
//   d  = P - A,  dh = d/|d|
//   n  = 손가락 법선에서 d 성분을 제거      ← n ⊥ d 이므로 평면이 A, P 를 둘 다 지난다
//                                            = P 가 정확한 모서리. 손가락 기울기는 여기에만 반영된다.
//   up = 월드 업에서 n 성분을 제거          ← 사각형이 스스로 돌지 않는다
//   right = up × n
//   dx = d·right, dy = d·up  →  가로 |dx|, 세로 |dy|
//
// [제스처]
//   1) 왼주먹 쥠                   → 소환 모드 ON
//   2) 오른손 엄지+검지 맞댐(핀치)  → 손끝 중점이 "A" 로 월드 고정
//   3) 손을 뺀다                   → 손끝 중점 "P" 가 대각 반대편 모서리에 붙어서 따라온다
//   4) 손을 기울인다               → 판의 기울기가 따라 바뀐다
//   5) 왼주먹 폄                   → 확정 → Spatial Anchor 저장
public class MonitorHandCalibrator : MonoBehaviour
{
    [Header("손 (비워두면 자동 탐색)")]
    public OVRHand leftHand;
    public OVRHand rightHand;
    public OVRSkeleton leftSkeleton;
    public OVRSkeleton rightSkeleton;

    [Header("모니터별 쿼드 & 저장 슬롯 (순서대로)")]
    [Tooltip("슬롯 개수만큼 모니터를 배치할 수 있음. Inspector에서 배열 크기를 늘리면 한도가 늘어남")]
    public string[] slotKeys = { "monitor1", "monitor2", "monitor3", "monitor4", "monitor5", "monitor6" };
    [Tooltip("각 슬롯에 붙일 쿼드. slotKeys보다 적으면 나머지는 앵커만 저장됨")]
    public Transform[] monitorQuads;

    [Header("소환 트리거 (오른손 핀치)")]
    public OVRHand.HandFinger pinchFinger = OVRHand.HandFinger.Index;
    [Range(0.3f, 1f)] public float pinchOn = 0.7f;

    // 판 평면은 n = (손끝 선) x (대각선) 으로 정해진다.
    // 손끝 선은 항상 판 평면 위에 놓이므로, 손가락을 눕히면 판도 눕는다.
    [Header("판의 기울기 (엄지·검지 선이 결정)")]
    [Tooltip("손끝 선 방향 스무딩. 손가락 관절은 노이즈가 있어 조금 낮게 두는 게 낫다")]
    [Range(1f, 40f)] public float tiltSmoothing = 10f;
    [Tooltip("첫 프레임에만 판 앞면을 사용자 쪽으로 맞춘다. 이후에는 연속성으로 유지하므로 뒤집히지 않는다")]
    public bool autoFaceUser = true;

    [Header("판의 축")]
    [Tooltip("가로변은 항상 Y축과 수직(수평)으로 고정된다. 롤 없음 = 진짜 직사각형")]
    public bool indexIsUp = true;
    [Tooltip("이보다 손끝이 붙으면 선의 방향이 튀므로 직전 방향을 유지한다(m)")]
    public float minSpread = 0.010f;

    [Header("크기 (A → P 드래그)")]
    [Tooltip("너무 크면 한쪽으로 치우친 드래그에서 꼭짓점이 P 에서 밀린다")]
    public float minSize = 0.005f;
    public float maxSize = 2.5f;
    [Tooltip("켜면 손끝 중점에 스무딩을 건다. 지연이 생겨 꼭짓점이 손에서 떨어져 보이므로 기본은 끔")]
    public bool smoothPointer = false;
    [Tooltip("smoothPointer 를 켰을 때만 사용")]
    [Range(1f, 60f)] public float positionSmoothing = 45f;

    [Header("왼손 주먹 게이트")]
    public bool requireLeftFist = true;
    public float curlOpenDistance = 0.14f;
    public float curlClosedDistance = 0.06f;
    [Range(0.1f, 1f)] public float fistThreshold = 0.7f;
    [Tooltip("주먹을 '폈다'로 볼 값. fistThreshold보다 낮게 두어 떨림 방지")]
    [Range(0.05f, 1f)] public float fistReleaseThreshold = 0.45f;
    public float interactionRadius = 0.06f;

    [Header("연출")]
    public Color quadColor = new Color(0.2f, 0.7f, 1f, 0.6f);
    public Color firstAnchorColor = new Color(1f, 0.3f, 0.2f, 1f);
    public Color cursorColor = new Color(0.2f, 1f, 0.3f, 1f);
    public float markerSize = 0.016f;
    public float holdDuration = 0.7f;
    public float fadeDuration = 0.3f;

    [Header("디버그")]
    public bool showDebug = true;
    public bool showFingerMarkers = false;
    public bool showDiagonal = true;
    public bool showTiltAxis = true;
    [Tooltip("엄지·검지가 만드는 법선을 막대로 표시")]
    public bool showNormalRay = true;
    public bool showAxisGizmo = true;
    public float axisGizmoLength = 0.12f;

    [Header("개발용")]
    public bool clearSavedOnPlay = false;

    [Tooltip("켜면 쿼드가 앵커 오브젝트를 그대로 따라간다. 런타임이 앵커를 재보정(re-localize)하면 " +
             "그린 곳에서 어긋난다. 꺼두면 손으로 그린 좌표에 그대로 고정된다")]
    public bool followAnchorPose = false;

    // ─────────────────────────────────────────────────────────────────────────
    // 몇 대를 배치할지. APK에서는 PC 콘솔의  cal 1 / cal 2  로 바꾼다.
    // 실험 조건(c1~c4)과는 별개다 — 물리 모니터 개수만큼 한 번 잡아두면,
    // 조건은 그 중 몇 개를 쓸지만 정한다.
    // ─────────────────────────────────────────────────────────────────────────
    [Header("배치할 모니터 수")]
    [Tooltip("이 개수만큼만 배치를 요구한다. PC 콘솔에서 cal 1 / cal 2 로 바꿀 수 있다")]
    public int calibrateCount = 2;

    public static MonitorHandCalibrator Instance { get; private set; }

    int UsableSlots => Mathf.Clamp(calibrateCount, 1, slotKeys != null ? slotKeys.Length : 1);

    // ---------------- 내부 상태 ----------------
    private int targetIndex = 0;
    private bool rightPinchedPrev = false;
    private bool armedPrev = false;
    private bool busy = false;

    private bool quadSpawned = false;
    private Vector3 firstAnchor;
    private Vector3 livePoint;
    private Vector3 quadCenter;
    private Quaternion quadRot = Quaternion.identity;
    private float rectW, rectH;

    private Vector3 smoothedMid;
    private Vector3 smoothedFingerLine = Vector3.up;
    private Vector3 lastQuadNormal = Vector3.back;
    private Vector3 lastQuadRight = Vector3.right;
    private bool hasLastNormal = false;
    private bool hasLastUp = false;
    private bool hasSmoothedMid = false;
    private bool hasSmoothedFinger = false;

    private GameObject previewQuad, anchorDot, cursorDot, diagonalBar, tiltBar, normalBar, axisRoot;
    private Transform[] axisBars;
    private GameObject[] fingerDots;

    // 디버그
    private float dbgFist, dbgPinch, dbgDiag, dbgSpread, dbgMinDist, dbgCornerErr, dbgTiltAngle, dbgLevelErr;
    private bool dbgLT, dbgRT, dbgTouch, dbgArmed, dbgRConf, dbgClamped, dbgDegenerate;

    // =========================================================
    void Awake() { Instance = this; }
    void OnDestroy() { if (Instance == this) Instance = null; }

    async void Start()
    {
        AutoFindHands();
        if (clearSavedOnPlay) DeleteAllPrefs();

        for (int i = 0; i < slotKeys.Length; i++)
            if (HasSaved(slotKeys[i]))
                await LoadSavedAsync(i);

        targetIndex = NextUncalibrated(0);
        ReportTarget();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 헤드셋 안에서는 OnGUI 디버그 글씨가 안 보인다(IMGUI는 VR에 안 그려진다).
    // 그래서 진행 상황을 PC 콘솔로 보낸다 — 진행자가 노트북 화면을 보고 안내하면 된다.
    // ─────────────────────────────────────────────────────────────────────────
    void ToPC(string text)
    {
        Debug.Log("[HandCalib] " + text);
        var pc = PCStreamReceiver.Instance;
        if (pc != null && pc.IsConnected) pc.SendText("msg," + text);
    }

    void ReportTarget()
    {
        if (targetIndex >= 0)
            ToPC($"배치 대기: {targetIndex + 1}번 모니터 ({targetIndex + 1}/{UsableSlots}) — "
               + "왼주먹 쥠 → 오른손 핀치로 왼쪽 위 찍기 → 손 빼서 크기 → 기울여 각도 → 왼주먹 폄");
        else
            ToPC($"모니터 배치 완료 ({UsableSlots}대). 이제 p07 c1 t1 로 시작하세요.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 다시 배치하기. PC 콘솔의  cal / cal 1 / cal 2  가 여기로 온다.
    // 저장된 앵커를 전부 지우고 1번부터 다시 받는다.
    // ─────────────────────────────────────────────────────────────────────────
    public void Recalibrate(int count)
    {
        if (count > 0) calibrateCount = count;

        // 진행 중이던 배치를 취소
        quadSpawned = false;
        busy = false;
        ShowPreview(false, default, default, 0, 0);
        ShowDot(ref anchorDot, false, default, firstAnchorColor);
        ShowDot(ref cursorDot, false, default, cursorColor);
        ShowDiagonal(false, default, default);
        ShowTiltAxis(false);
        ShowNormalRay(false, default);
        ShowAxisGizmo(false, default, default);

        ClearAllSaved();
        ToPC($"모니터 배치를 처음부터 다시 합니다 — 총 {UsableSlots}대");
        ReportTarget();
    }

    void AutoFindHands()
    {
        var hands = FindObjectsOfType<OVRHand>();
        foreach (var h in hands)
        {
            var sk = h.GetComponent<OVRSkeleton>();
            var t = sk != null ? sk.GetSkeletonType() : OVRSkeleton.SkeletonType.None;
            bool isLeft = t == OVRSkeleton.SkeletonType.HandLeft || t == OVRSkeleton.SkeletonType.XRHandLeft;
            bool isRight = t == OVRSkeleton.SkeletonType.HandRight || t == OVRSkeleton.SkeletonType.XRHandRight;
            if (isLeft) { if (leftHand == null) leftHand = h; if (leftSkeleton == null) leftSkeleton = sk; }
            if (isRight) { if (rightHand == null) rightHand = h; if (rightSkeleton == null) rightSkeleton = sk; }
        }
        Debug.Log($"[HandCalib] 손 자동탐색: left={(leftHand ? leftHand.name : "없음")}, right={(rightHand ? rightHand.name : "없음")}");
    }

    // =========================================================
    void Update()
    {
        dbgLT = leftHand != null && leftHand.IsTracked;
        dbgRT = rightHand != null && rightHand.IsTracked;
        dbgRConf = rightHand != null && rightHand.IsDataHighConfidence;
        dbgFist = (dbgLT && leftSkeleton != null) ? FistStrength(leftSkeleton) : 0f;
        dbgPinch = dbgRT ? rightHand.GetFingerPinchStrength(pinchFinger) : 0f;

        if (busy || targetIndex < 0 || rightHand == null)
        {
            dbgArmed = false;
            Despawn();
            return;
        }

        UpdateFingerMarkers();

        dbgTouch = requireLeftFist && LeftTouchingObject();
        bool armed;
        if (!requireLeftFist) armed = true;
        else if (!dbgLT || dbgTouch) armed = false;
        else armed = armedPrev ? dbgFist > fistReleaseThreshold : dbgFist > fistThreshold;
        dbgArmed = armed;

        bool fistJustOpened = armedPrev && !armed;
        armedPrev = armed;

        bool pinched = dbgPinch > pinchOn;
        bool pinchDown = pinched && !rightPinchedPrev;
        rightPinchedPrev = pinched;

        bool pValid = TryGetMidpoint(out Vector3 P);
        livePoint = P;
        UpdateFingerTilt(P);
        // 커서 구체는 왼주먹으로 배치 모드가 켜졌을 때(armed)만 표시
        ShowDot(ref cursorDot, armed && dbgRT && pValid, P, cursorColor);

        if (!quadSpawned && armed && pinchDown && pValid)
        {
            hasSmoothedMid = false;
            hasLastNormal = false;
            hasLastUp = false;
            TryGetMidpoint(out P);
            firstAnchor = P;
            livePoint = P;
            quadSpawned = true;
        }

        if (quadSpawned && pValid)
            BuildQuad(firstAnchor, P);

        if (fistJustOpened && quadSpawned)
        {
            FinalizePlacement();
            return;
        }

        ShowPreview(quadSpawned, quadCenter, quadRot, rectW, rectH);
        ShowDot(ref anchorDot, quadSpawned, firstAnchor, firstAnchorColor);
        ShowDiagonal(quadSpawned && showDiagonal, firstAnchor, livePoint);
        ShowTiltAxis(quadSpawned && showTiltAxis);
        ShowNormalRay(quadSpawned && showNormalRay, livePoint);
        ShowAxisGizmo(quadSpawned && showAxisGizmo, quadCenter, quadRot);
    }

    // =========================================================
    void BuildQuad(Vector3 A, Vector3 P)
    {
        Vector3 d = P - A;
        dbgDiag = d.magnitude;
        if (dbgDiag < 1e-4f) return;
        Vector3 dh = d / dbgDiag;
        Vector3 mid = (A + P) * 0.5f;

        // ---- 법선 : n = t × d ----
        //   t = 엄지끝→검지끝 (손끝 선), d = A→P (대각선)
        //   외적이므로 n ⊥ t 와 n ⊥ d 가 동시에 성립한다.
        //     n ⊥ t → 손끝 선이 판 평면 위에 놓인다. 손가락을 눕히면 판도 눕는다.
        //     n ⊥ d → A 와 P 가 판 평면 위에 놓인다. P 가 정확한 대각 모서리.
        //   세로축은 t 를 쓰지 않으므로 손가락은 평면 안에서 자유롭게 떠돌 수 있다.
        Vector3 t = smoothedFingerLine;
        Vector3 n = Vector3.Cross(t, dh);
        dbgDegenerate = n.sqrMagnitude < 1e-7f;
        if (dbgDegenerate)
        {
            // 손끝 선이 대각선과 거의 평행 → 평면이 정의되지 않는다. 직전 평면을 d 에 맞춰 유지
            n = lastQuadNormal - Vector3.Dot(lastQuadNormal, dh) * dh;
            if (n.sqrMagnitude < 1e-8f)
            {
                n = Vector3.up - Vector3.Dot(Vector3.up, dh) * dh;
                if (n.sqrMagnitude < 1e-8f) return;
            }
        }
        n.Normalize();

        // 부호는 '직전 프레임'과의 연속성으로 결정한다.
        // 카메라 방향으로 매번 판정하면 판을 눕히는 순간 내적이 0 근처가 되어 180도씩 뒤집힌다.
        if (hasLastNormal && Vector3.Dot(n, lastQuadNormal) < 0f) n = -n;
        else if (!hasLastNormal && autoFaceUser && Camera.main != null &&
                 Vector3.Dot(n, Camera.main.transform.position - mid) < 0f) n = -n;
        lastQuadNormal = n;
        hasLastNormal = true;

        // ---- 축 : 가로변을 항상 수평으로 ----
        // right = worldUp × n  이면 right · worldUp = 0 이 수학적으로 보장된다.
        // 즉 가로변이 Y축과 수직 → 롤이 0 → 마름모가 아니라 진짜 직사각형.
        // 평면 안에서 어떤 직교 기저를 써도 d 분해는 정확하므로 꼭짓점 정확도는 그대로다.
        Vector3 right = Vector3.Cross(Vector3.up, n);
        if (right.sqrMagnitude < 1e-8f)
        {
            // 판이 거의 수평으로 누움 (n ≈ worldUp) → 가로축이 정의되지 않는다. 직전 값 유지
            right = hasLastUp ? lastQuadRight : Vector3.right;
            right = right - Vector3.Dot(right, n) * n;
            if (right.sqrMagnitude < 1e-8f) return;
        }
        right.Normalize();
        if (hasLastUp && Vector3.Dot(right, lastQuadRight) < 0f) right = -right;   // 부호 연속성

        Vector3 up = Vector3.Cross(n, right).normalized;   // 월드 업을 평면에 투영한 것과 동일
        quadRot = Quaternion.LookRotation(n, up);

        // 다음 프레임 연속성 기준으로 저장
        lastQuadRight = right;
        hasLastUp = true;

        // ---- d 분해 ----
        // d ⊥ n 이므로 d = dx·right + dy·up 으로 손실 없이 분해된다.
        float dx = Vector3.Dot(d, right);
        float dy = Vector3.Dot(d, up);

        // 중심은 A 와 P 의 중점. 평면이 A, P 를 모두 지나므로 중점도 평면 위에 있다.
        // A 에서 w/h 로 역산하면 클램프가 걸리는 순간 중심이 밀려 꼭짓점이 P 에서 떨어진다.
        // 중점을 직접 쓰면 클램프와 무관하게 A 와 P 가 항상 정확한 대각 꼭짓점이 된다.
        quadCenter = (A + P) * 0.5f;

        float w = Mathf.Abs(dx), h = Mathf.Abs(dy);
        dbgClamped = w < minSize || h < minSize || w > maxSize || h > maxSize;
        rectW = Mathf.Clamp(w, minSize, maxSize);
        rectH = Mathf.Clamp(h, minSize, maxSize);

        // 검증: 계산된 반대편 꼭짓점과 P 의 거리 (클램프가 없으면 0)
        float sx = dx >= 0f ? 1f : -1f;
        float sy = dy >= 0f ? 1f : -1f;
        Vector3 opposite = quadCenter + right * (sx * rectW * 0.5f) + up * (sy * rectH * 0.5f);
        dbgCornerErr = (opposite - P).magnitude;
        dbgTiltAngle = Vector3.Angle(n, Vector3.ProjectOnPlane(n, Vector3.up));
        dbgLevelErr = 90f - Vector3.Angle(right, Vector3.up);   // 0 이면 가로변이 완전 수평
    }

    // 손끝 선(엄지끝 → 검지끝)만 방향 상태로 스무딩한다.
    // 이 선이 판 평면을 결정하므로, 평면을 만들기 '전에' 스무딩해야
    // n ⊥ t 관계가 깨지지 않는다.
    void UpdateFingerTilt(Vector3 at)
    {
        Transform thumbTip = Tip(rightSkeleton, OVRSkeleton.BoneId.Hand_ThumbTip, OVRSkeleton.BoneId.XRHand_ThumbTip);
        Transform indexTip = Tip(rightSkeleton, OVRSkeleton.BoneId.Hand_IndexTip, OVRSkeleton.BoneId.XRHand_IndexTip);
        if (thumbTip == null || indexTip == null) { return; }

        Vector3 line = indexIsUp
            ? (indexTip.position - thumbTip.position)
            : (thumbTip.position - indexTip.position);
        dbgSpread = line.magnitude;

        // 손끝이 너무 붙으면 선의 방향이 튄다 → 직전 방향 유지
        if (dbgSpread < minSpread) { return; }

        line /= dbgSpread;

        // 부호 연속성: 손끝 선은 방향만 쓰므로 직전과 반대로 뒤집히면 되돌린다
        if (hasSmoothedFinger && Vector3.Dot(line, smoothedFingerLine) < 0f) line = -line;

        if (!hasSmoothedFinger) { smoothedFingerLine = line; hasSmoothedFinger = true; }
        else smoothedFingerLine = Vector3.Slerp(smoothedFingerLine, line,
                1f - Mathf.Exp(-tiltSmoothing * Time.deltaTime)).normalized;
    }

    // 손끝 중점 = 마우스 포인터. 기본은 스무딩 없이 원본 좌표를 그대로 쓴다.
    // 스무딩을 걸면 실제 손끝보다 뒤처져서, 계산이 맞아도 꼭짓점이 손에서
    // 떨어져 보인다 (윈도우 드래그에는 이런 지연이 없다).
    bool TryGetMidpoint(out Vector3 mid)
    {
        mid = Vector3.zero;
        Transform thumbTip = Tip(rightSkeleton, OVRSkeleton.BoneId.Hand_ThumbTip, OVRSkeleton.BoneId.XRHand_ThumbTip);
        Transform indexTip = Tip(rightSkeleton, OVRSkeleton.BoneId.Hand_IndexTip, OVRSkeleton.BoneId.XRHand_IndexTip);
        if (!dbgRT || thumbTip == null || indexTip == null) return false;

        Vector3 raw = (thumbTip.position + indexTip.position) * 0.5f;

        if (!smoothPointer)
        {
            smoothedMid = raw;
            hasSmoothedMid = true;
            mid = raw;
            return true;
        }

        if (!hasSmoothedMid) { smoothedMid = raw; hasSmoothedMid = true; }
        else smoothedMid = Vector3.Lerp(smoothedMid, raw, 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime));
        mid = smoothedMid;
        return true;
    }

    void Despawn()
    {
        quadSpawned = false;
        hasSmoothedMid = false;
        hasLastNormal = false;
        hasLastUp = false;
        ShowPreview(false, default, default, 0, 0);
        ShowDot(ref anchorDot, false, Vector3.zero, firstAnchorColor);
        ShowDot(ref cursorDot, false, Vector3.zero, cursorColor);
        ShowDiagonal(false, default, default);
        ShowTiltAxis(false);
        ShowNormalRay(false, Vector3.zero);
        ShowAxisGizmo(false, default, default);
    }

    void FinalizePlacement()
    {
        quadSpawned = false;
        hasSmoothedMid = false;
        hasLastNormal = false;
        hasLastUp = false;
        ShowPreview(false, default, default, 0, 0);
        ShowDot(ref anchorDot, false, Vector3.zero, firstAnchorColor);
        ShowDiagonal(false, default, default);
        ShowTiltAxis(false);
        ShowNormalRay(false, Vector3.zero);
        ShowAxisGizmo(false, default, default);
        _ = FinalizeAsync(targetIndex, quadCenter, quadRot, rectW, rectH);
    }

    // =========================================================
    // 프리뷰 / 기즈모
    // =========================================================
    void ShowPreview(bool show, Vector3 pos, Quaternion rot, float w, float h)
    {
        if (previewQuad == null)
        {
            previewQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(previewQuad.GetComponent<Collider>());
            // CreatePrimitive의 기본 머티리얼은 빌드(APK)에서 셰이더가 빠져 안 보인다.
            // URP Unlit으로 직접 만들어 준다.
            previewQuad.GetComponent<Renderer>().material =
                DesktopTetherSpawner.MakeUnlit(quadColor, quadColor.a);
        }
        if (previewQuad.activeSelf != show) previewQuad.SetActive(show);
        if (!show) return;
        SetMatColor(previewQuad, quadColor);
        previewQuad.transform.SetPositionAndRotation(pos, rot);
        previewQuad.transform.localScale = new Vector3(w, h, 1f);
    }

    // URP 빌드에서 CreatePrimitive 기본 머티리얼은 셰이더가 스트립돼 안 보인다.
    // 여기를 거쳐 만든 머티리얼만 헤드셋(APK)에서 제대로 그려진다.
    static void SetMatColor(GameObject go, Color c)
    {
        if (go == null) return;
        var r = go.GetComponent<Renderer>();
        if (r == null || r.material == null) return;
        if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c);
        if (r.material.HasProperty("_Color")) r.material.SetColor("_Color", c);
    }

    void ShowDot(ref GameObject dot, bool show, Vector3 pos, Color col)
    {
        if (dot == null)
        {
            dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(dot.GetComponent<Collider>());
            dot.transform.localScale = Vector3.one * markerSize;
            dot.GetComponent<Renderer>().material = DesktopTetherSpawner.MakeUnlit(col, col.a);
        }
        if (dot.activeSelf != show) dot.SetActive(show);
        if (show) dot.transform.position = pos;
    }

    void ShowBar(ref GameObject bar, bool show, Vector3 a, Vector3 b, Color col, float thickness)
    {
        if (bar == null)
        {
            bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(bar.GetComponent<Collider>());
            bar.GetComponent<Renderer>().material = DesktopTetherSpawner.MakeUnlit(col, 1f);
        }
        Vector3 d = b - a;
        float len = d.magnitude;
        bool ok = show && len > 1e-4f;
        if (bar.activeSelf != ok) bar.SetActive(ok);
        if (!ok) return;
        bar.transform.position = (a + b) * 0.5f;
        bar.transform.rotation = Quaternion.LookRotation(d / len, Vector3.up);
        bar.transform.localScale = new Vector3(thickness, thickness, len);
    }

    void ShowDiagonal(bool show, Vector3 a, Vector3 b)
    {
        ShowBar(ref diagonalBar, show, a, b, Color.white, 0.004f);
    }

    void ShowTiltAxis(bool show)
    {
        Transform thumbTip = Tip(rightSkeleton, OVRSkeleton.BoneId.Hand_ThumbTip, OVRSkeleton.BoneId.XRHand_ThumbTip);
        Transform indexTip = Tip(rightSkeleton, OVRSkeleton.BoneId.Hand_IndexTip, OVRSkeleton.BoneId.XRHand_IndexTip);
        bool ok = show && thumbTip != null && indexTip != null;
        ShowBar(ref tiltBar, ok, ok ? thumbTip.position : Vector3.zero, ok ? indexTip.position : Vector3.zero,
                Color.yellow, 0.005f);
    }

    // 엄지·검지가 만드는 법선을 시안색 막대로 표시
    void ShowNormalRay(bool show, Vector3 at)
    {
        ShowBar(ref normalBar, show && hasLastNormal, at, at + lastQuadNormal * 0.10f,
                Color.cyan, 0.004f);
    }

    void ShowAxisGizmo(bool show, Vector3 pos, Quaternion rot)
    {
        if (axisRoot == null)
        {
            axisRoot = new GameObject("QuadAxisGizmo");
            axisBars = new Transform[3];
            Color[] cols = { Color.red, Color.green, Color.blue };
            for (int i = 0; i < 3; i++)
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(bar.GetComponent<Collider>());
                bar.transform.SetParent(axisRoot.transform, false);
                bar.GetComponent<Renderer>().material = DesktopTetherSpawner.MakeUnlit(cols[i], 1f);
                axisBars[i] = bar.transform;
            }
        }
        if (axisRoot.activeSelf != show) axisRoot.SetActive(show);
        if (!show) return;

        axisRoot.transform.SetPositionAndRotation(pos, rot);
        float L = axisGizmoLength, T = axisGizmoLength * 0.06f;
        axisBars[0].localPosition = new Vector3(L * 0.5f, 0, 0);
        axisBars[0].localScale = new Vector3(L, T, T);
        axisBars[1].localPosition = new Vector3(0, L * 0.5f, 0);
        axisBars[1].localScale = new Vector3(T, L, T);
        axisBars[2].localPosition = new Vector3(0, 0, L * 0.5f);
        axisBars[2].localScale = new Vector3(T, T, L);
    }

    void UpdateFingerMarkers()
    {
        if (!showFingerMarkers)
        {
            if (fingerDots != null) foreach (var d in fingerDots) if (d) d.SetActive(false);
            return;
        }
        if (fingerDots == null)
        {
            Color[] cols = { Color.red, Color.green, Color.blue, Color.yellow, Color.magenta };
            fingerDots = new GameObject[5];
            for (int i = 0; i < 5; i++)
            {
                var d = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(d.GetComponent<Collider>());
                d.transform.localScale = Vector3.one * 0.012f;
                d.GetComponent<Renderer>().material = DesktopTetherSpawner.MakeUnlit(cols[i], 1f);
                fingerDots[i] = d;
            }
        }
        var ids = new (OVRSkeleton.BoneId l, OVRSkeleton.BoneId x)[]
        {
            (OVRSkeleton.BoneId.Hand_ThumbTip,  OVRSkeleton.BoneId.XRHand_ThumbTip),
            (OVRSkeleton.BoneId.Hand_IndexTip,  OVRSkeleton.BoneId.XRHand_IndexTip),
            (OVRSkeleton.BoneId.Hand_MiddleTip, OVRSkeleton.BoneId.XRHand_MiddleTip),
            (OVRSkeleton.BoneId.Hand_RingTip,   OVRSkeleton.BoneId.XRHand_RingTip),
            (OVRSkeleton.BoneId.Hand_PinkyTip,  OVRSkeleton.BoneId.XRHand_LittleTip),
        };
        for (int i = 0; i < 5; i++)
        {
            var t = Tip(rightSkeleton, ids[i].l, ids[i].x);
            bool ok = t != null;
            if (fingerDots[i].activeSelf != ok) fingerDots[i].SetActive(ok);
            if (ok) fingerDots[i].transform.position = t.position;
        }
    }

    // =========================================================
    // 본 접근
    // =========================================================
    Transform GetTip(OVRSkeleton sk, OVRSkeleton.BoneId id)
    {
        if (sk == null || sk.Bones == null) return null;
        foreach (var b in sk.Bones)
            if (b.Id == id) return b.Transform;
        return null;
    }

    bool IsXRSkeleton(OVRSkeleton sk)
    {
        if (sk == null) return false;
        var t = sk.GetSkeletonType();
        return t == OVRSkeleton.SkeletonType.XRHandLeft || t == OVRSkeleton.SkeletonType.XRHandRight;
    }

    Transform Tip(OVRSkeleton sk, OVRSkeleton.BoneId legacy, OVRSkeleton.BoneId xr)
    {
        return IsXRSkeleton(sk) ? GetTip(sk, xr) : GetTip(sk, legacy);
    }

    [ContextMenu("오른손 본 덤프")]
    public void DumpRightBones()
    {
        if (rightSkeleton == null || rightSkeleton.Bones == null)
        {
            Debug.Log("[HandCalib][DUMP] rightSkeleton 없음");
            return;
        }
        Transform wrist = GetTip(rightSkeleton, OVRSkeleton.BoneId.Hand_WristRoot);
        Vector3 origin = wrist != null ? wrist.position : Vector3.zero;
        Quaternion inv = wrist != null ? Quaternion.Inverse(wrist.rotation) : Quaternion.identity;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[HandCalib][DUMP] rightSkel type={rightSkeleton.GetSkeletonType()} bones={rightSkeleton.Bones.Count} conf={dbgRConf}");
        foreach (var b in rightSkeleton.Bones)
        {
            Vector3 local = b.Transform != null ? inv * (b.Transform.position - origin) : Vector3.zero;
            sb.AppendLine($"{(int)b.Id} {b.Id}  local=({local.x:F3},{local.y:F3},{local.z:F3})");
        }
        Debug.Log(sb.ToString());
    }

    // =========================================================
    // 확정 / 앵커
    // =========================================================
    async Task FinalizeAsync(int index, Vector3 center, Quaternion rot, float w, float h)
    {
        busy = true;
        StartCoroutine(FadeQuad(center, rot, w, h));

        var anchorGo = new GameObject($"MonitorAnchor_{slotKeys[index]}");
        anchorGo.transform.SetPositionAndRotation(center, rot);
        var anchor = anchorGo.AddComponent<OVRSpatialAnchor>();

        if (!await WaitForCreated(anchor))
            Debug.LogError("[HandCalib] 앵커 생성 실패. OVRManager Anchor Support 확인.");
        else
        {
            var saveResult = await OVRSpatialAnchor.SaveAnchorsAsync(new List<OVRSpatialAnchor> { anchor });
            if (!saveResult.Success)
                Debug.LogWarning($"[HandCalib] 앵커 저장 실패: {saveResult.Status}");
            else
            {
                SaveToPrefs(slotKeys[index], anchor.Uuid, w, h);
                Debug.Log($"[HandCalib] '{slotKeys[index]}' 저장 완료 ({w:F2}x{h:F2}m)");
            }
        }

        // 앵커가 런타임 재보정으로 그린 위치에서 얼마나 밀렸는지 기록한다.
        float drift = Vector3.Distance(anchorGo.transform.position, center);
        float driftAng = Quaternion.Angle(anchorGo.transform.rotation, rot);
        if (drift > 0.005f || driftAng > 1f)
            Debug.LogWarning($"[HandCalib] 앵커가 그린 위치에서 {drift * 100f:F1}cm / {driftAng:F1}도 밀림 " +
                             $"(그린곳={center}, 앵커={anchorGo.transform.position})");

        AttachQuad(index, anchorGo.transform, w, h, center, rot);

        targetIndex = NextUncalibrated(index + 1);
        busy = false;
        ToPC($"{index + 1}번 모니터 저장됨 ({w:F2} x {h:F2} m)");
        ReportTarget();
    }

    IEnumerator FadeQuad(Vector3 pos, Quaternion rot, float w, float h)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(quad.GetComponent<Collider>());
        quad.transform.SetPositionAndRotation(pos, rot);
        quad.transform.localScale = new Vector3(w, h, 1f);
        float baseA = quadColor.a;
        var mat = DesktopTetherSpawner.MakeUnlit(quadColor, baseA);
        quad.GetComponent<Renderer>().material = mat;

        var c = quadColor; c.a = baseA;
        yield return new WaitForSeconds(holdDuration);
        float t = 0f;
        while (t < fadeDuration)
        {
            t += Time.deltaTime;
            c.a = Mathf.Lerp(baseA, 0f, t / fadeDuration);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            yield return null;
        }
        Destroy(quad);
    }

    // worldPos/worldRot = 손으로 그린(또는 저장된) 좌표.
    // followAnchorPose 가 꺼져 있으면 앵커가 어디로 밀리든 이 좌표에 쿼드를 고정한다.
    void AttachQuad(int index, Transform anchorT, float width, float height,
                    Vector3 worldPos, Quaternion worldRot)
    {
        if (monitorQuads == null || index >= monitorQuads.Length || monitorQuads[index] == null)
        {
            Debug.LogWarning($"[HandCalib] monitorQuads[{index}] 비어있어 붙일 쿼드 없음(앵커만 저장).");
            return;
        }
        var q = monitorQuads[index];

        if (followAnchorPose)
        {
            q.SetParent(anchorT, worldPositionStays: false);
            q.localPosition = Vector3.zero;
            q.localRotation = Quaternion.identity;
        }
        else
        {
            // 부모를 떼고 월드 포즈를 정확히 찍은 뒤, 그 포즈를 유지한 채 앵커 밑으로 넣는다
            q.SetParent(null, worldPositionStays: true);
            q.SetPositionAndRotation(worldPos, worldRot);
            q.SetParent(anchorT, worldPositionStays: true);
        }
        q.localScale = new Vector3(width, height, 1f);

        Debug.Log($"[HandCalib] {index + 1}번 쿼드 배치 pos={q.position} (그린좌표={worldPos}, 앵커추종={followAnchorPose})");
    }

    async Task<bool> WaitForCreated(OVRSpatialAnchor anchor)
    {
        float t0 = Time.time;
        while (anchor && !anchor.Created)
        {
            if (Time.time - t0 > 5f) return false;
            await Task.Yield();
        }
        return anchor && anchor.Created;
    }

    async Task LoadSavedAsync(int index)
    {
        string slot = slotKeys[index];
        Guid uuid = new Guid(PlayerPrefs.GetString(Key(slot, "uuid")));
        float width = PlayerPrefs.GetFloat(Key(slot, "w"), 0.5f);
        float height = PlayerPrefs.GetFloat(Key(slot, "h"), 0.3f);

        var unbound = new List<OVRSpatialAnchor.UnboundAnchor>();
        var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { uuid }, unbound);
        if (!result.Success || unbound.Count == 0)
        {
            Debug.LogWarning($"[HandCalib] '{slot}' 앵커 로드 실패: {result.Status}.");
            return;
        }
        var ua = unbound[0];
        if (!ua.Localized && !await ua.LocalizeAsync())
        {
            Debug.LogWarning($"[HandCalib] '{slot}' 로컬라이즈 실패.");
            return;
        }
        ua.TryGetPose(out var pose);
        var anchorGo = new GameObject($"MonitorAnchor_{slot}");
        anchorGo.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var anchor = anchorGo.AddComponent<OVRSpatialAnchor>();
        ua.BindTo(anchor);
        AttachQuad(index, anchorGo.transform, width, height, pose.position, pose.rotation);
        Debug.Log($"[HandCalib] '{slot}' 저장된 앵커로 자동 배치 완료.");
    }

    // 배치할 개수(UsableSlots)까지만 본다. 3대째부터는 묻지 않는다.
    int NextUncalibrated(int from)
    {
        for (int i = from; i < UsableSlots; i++)
            if (!HasSaved(slotKeys[i])) return i;
        return -1;
    }

    // =========================================================
    // 왼손 주먹
    // =========================================================
    float FistStrength(OVRSkeleton sk)
    {
        Transform wrist = Tip(sk, OVRSkeleton.BoneId.Hand_WristRoot, OVRSkeleton.BoneId.XRHand_Wrist);
        if (wrist == null) return 0f;
        dbgMinDist = 999f;
        float c1 = Curl(sk, wrist, OVRSkeleton.BoneId.Hand_IndexTip, OVRSkeleton.BoneId.XRHand_IndexTip);
        float c2 = Curl(sk, wrist, OVRSkeleton.BoneId.Hand_MiddleTip, OVRSkeleton.BoneId.XRHand_MiddleTip);
        float c3 = Curl(sk, wrist, OVRSkeleton.BoneId.Hand_RingTip, OVRSkeleton.BoneId.XRHand_RingTip);
        float c4 = Curl(sk, wrist, OVRSkeleton.BoneId.Hand_PinkyTip, OVRSkeleton.BoneId.XRHand_LittleTip);
        return (c1 + c2 + c3 + c4) * 0.25f;
    }

    float Curl(OVRSkeleton sk, Transform wrist, OVRSkeleton.BoneId legacy, OVRSkeleton.BoneId xr)
    {
        Transform tip = Tip(sk, legacy, xr);
        if (tip == null) return 0f;
        float d = Vector3.Distance(tip.position, wrist.position);
        if (d < dbgMinDist) dbgMinDist = d;
        return Mathf.Clamp01(Mathf.InverseLerp(curlOpenDistance, curlClosedDistance, d));
    }

    bool LeftTouchingObject()
    {
        if (leftHand == null) return false;
        var hits = Physics.OverlapSphere(leftHand.transform.position, interactionRadius);
        foreach (var h in hits)
        {
            if (h == null || h.isTrigger) continue;
            if (h.GetComponentInParent<OVRHand>() != null) continue;
            if (h.GetComponentInParent<OVRSkeleton>() != null) continue;
            return true;
        }
        return false;
    }

    // =========================================================
    // PlayerPrefs
    // =========================================================
    string Key(string slot, string s) => $"{slot}_{s}";
    bool HasSaved(string slot) => PlayerPrefs.HasKey(Key(slot, "uuid")) &&
                                   !string.IsNullOrEmpty(PlayerPrefs.GetString(Key(slot, "uuid")));

    void SaveToPrefs(string slot, Guid uuid, float w, float h)
    {
        PlayerPrefs.SetString(Key(slot, "uuid"), uuid.ToString());
        PlayerPrefs.SetFloat(Key(slot, "w"), w);
        PlayerPrefs.SetFloat(Key(slot, "h"), h);
        PlayerPrefs.Save();
    }

    void DeleteAllPrefs()
    {
        foreach (var slot in slotKeys)
        {
            PlayerPrefs.DeleteKey(Key(slot, "uuid"));
            PlayerPrefs.DeleteKey(Key(slot, "w"));
            PlayerPrefs.DeleteKey(Key(slot, "h"));
        }
        PlayerPrefs.Save();
    }

    [ContextMenu("저장된 앵커 전체 삭제 (재캘리브레이션)")]
    public void ClearAllSaved()
    {
        DeleteAllPrefs();
        for (int i = 0; i < slotKeys.Length; i++)
        {
            if (monitorQuads != null && i < monitorQuads.Length && monitorQuads[i] != null)
                monitorQuads[i].SetParent(null, worldPositionStays: true);
            var go = GameObject.Find($"MonitorAnchor_{slotKeys[i]}");
            if (go != null) Destroy(go);
        }
        targetIndex = NextUncalibrated(0);
        Debug.Log($"[HandCalib] 저장 삭제 완료. 다시 '{(targetIndex >= 0 ? slotKeys[targetIndex] : "완료")}'부터.");
    }

    // =========================================================
    void OnGUI()
    {
        if (!showDebug) return;
        var style = new GUIStyle(GUI.skin.box) { fontSize = 20, alignment = TextAnchor.UpperLeft };
        string s =
            $"[HandCalib]  쿼드:{(quadSpawned ? "조절중" : "없음")}\n" +
            $"오른손:{(dbgRT ? "O" : "X")} 신뢰도:{(dbgRConf ? "높음" : "낮음")} 꼬집:{dbgPinch:F2}\n" +
            $"A↔P:{dbgDiag * 100f:F1}cm → {rectW:F2}x{rectH:F2}m{(dbgClamped ? " [클램프]" : "")}\n" +
            $"모서리오차:{dbgCornerErr * 1000f:F2}mm  기울기:{dbgTiltAngle:F1}도{(dbgDegenerate ? " [손끝선≈대각선]" : "")}\n" +
            $"수평오차:{dbgLevelErr:F3}도  배치모드:{(dbgArmed ? "ON(주먹)" : "OFF")}" + (requireLeftFist ? $" 왼주먹:{dbgFist:F2}" : "") + "\n" +
            $"타깃:{(targetIndex >= 0 ? slotKeys[targetIndex] : "완료")}";
        GUI.Label(new Rect(20, 20, 740, 210), s, style);
    }
}