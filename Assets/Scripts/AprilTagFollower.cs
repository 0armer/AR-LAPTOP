using System;
using AprilTag;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// [AR LAPTOP] 노트북(물체)에 붙인 AprilTag를 패스스루 카메라로 찾아서, 모니터 판이 그 물체를 따라가게 한다.
//
// 흐름
//   1. PassthroughCameraAccess(Meta, 헤드셋 바깥 카메라 영상)로 영상을 받는다
//   2. GPU → CPU로 비동기 복사 (AsyncGPUReadback, 메인 스레드를 안 멈춤)
//   3. keijiro AprilTag(jp.keijiro.apriltag)로 태그 찾기 → 카메라 기준 위치·회전
//   4. 그 영상을 찍은 순간의 카메라 위치를 곱해서 월드 좌표로
//   5. "태그 → 판" 오프셋을 적용해서 판을 부드럽게 옮긴다
//   → 판이 움직이면 WindowsFollowDesktop가 꺼낸 창들도 같이 옮긴다
//
// 오프셋(태그와 판 사이 상대 위치)
//   · 손으로 판을 그려서 확정하면, 그 직후 태그가 보이는 순간의 상대 위치를 저장한다 (PlayerPrefs)
//   · 저장된 게 없고 판이 이미 캘리브레이션돼 있으면, 태그를 처음 본 순간 저장한다
//   · 다시 잡으려면: 손으로 판을 다시 그리거나, 유니티 메뉴 `실험 ▸ AprilTag ▸ 오프셋 다시 잡기`
//
// 태그: tagStandard41h12 (keijiro는 이 종류만 읽음). 크기값 = 검은 띠 안쪽 흰 네모 한 변(m)
// 주의: 패스스루 카메라는 APK에서만 확실히 된다. 에디터(Link)에서는 지원 안 될 수 있음 → 로그 확인
[DefaultExecutionOrder(10000)]   // 판을 옮긴 뒤에 WindowsFollowDesktop(20000)가 창을 옮김
public class AprilTagFollower : MonoBehaviour
{
    public static AprilTagFollower Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("AprilTagFollower");
        DontDestroyOnLoad(go);
        go.AddComponent<AprilTagFollower>();
    }

    [Header("태그")]
    [Tooltip("따라갈 태그 ID")]
    public int tagId = 0;
    [Tooltip("검은 띠 안쪽 흰 네모 한 변(m). 5.4cm 태그 = 0.030, 7.2cm = 0.040")]
    public float tagSize = 0.030f;
    [Tooltip("움직일 모니터 판 번호")]
    public int monitorIndex = 0;

    [Header("카메라")]
    [Tooltip("요청 해상도. 클수록 멀리서 잡히지만 느려진다")]
    public Vector2Int resolution = new Vector2Int(640, 480);
    [Tooltip("영상을 줄여서 찾는 배수. 1=정확·느림, 2=빠름·작은 태그 놓침")]
    [Range(1, 4)] public int decimation = 2;
    [Tooltip("태그 위아래가 뒤집혀 잡히면(판이 거꾸로 움직이면) 켠다")]
    public bool flipVertical = false;

    [Header("따라가기")]
    [Tooltip("0 = 새 위치로 바로 점프. 값을 주면 부드럽게 따라감 (클수록 빠름, 기본 25). " +
             "작은 떨림(2mm/0.5도 이하)은 무시하고, 5cm/10도 이상 튀면 바로 따라감")]
    [Range(0f, 60f)] public float followSpeed = 25f;
    [Tooltip("물체 움직임을 앞으로 예측하는 시간. 노이즈를 증폭시켜 떨림의 원인이 되므로 기본 0 (끔)")]
    [Range(0f, 0.15f)] public float predictSeconds = 0f;
    [Tooltip("태그를 놓쳐도 이 시간(초)까지는 마지막 위치에 보여줌. 넘으면 화면 판을 숨긴다")]
    public float lostTimeout = 0.5f;
    [Tooltip("태그 위치에 작은 큐브를 띄워 인식 확인")]
    public bool showTagGizmo = true;
    [Tooltip("끄면 판을 안 움직인다 (인식만)")]
    public bool follow = true;

    [Header("판 배치 (태그 = 판의 왼쪽 위 모서리)")]
    [Tooltip("켜면 태그를 보는 순간 판을 태그 기준 고정 위치에 붙인다 (손 캘리브레이션 불필요). " +
             "끄면 예전 방식(손으로 그린 판과 태그 사이 거리를 저장해서 따라감)")]
    public bool fixedLayout = true;
    [Tooltip("판 가로(m). A4 가로 = 0.297")]
    public float quadWidth = 0.297f;
    [Tooltip("판 세로(m). A4 세로 = 0.210")]
    public float quadHeight = 0.210f;
    [Tooltip("태그 바깥 왼쪽 위 꼭짓점에서 판의 왼쪽 위 꼭짓점까지 (오른쪽+, 아래+) m. 0이면 딱 맞춤")]
    public Vector2 cornerOffset = new Vector2(-0.02f, -0.02f);   // 태그보다 2cm 더 바깥에서 시작 → 태그가 판 안쪽에 들어감
    [Tooltip("판을 태그 면에서 보는 쪽으로 띄우는 거리(m)")]
    public float liftFromSurface = 0.002f;
    [Tooltip("판을 보는 사람 쪽으로 당기는 거리(m). 판이 태그보다 뒤에 있어 보이면 +, 앞에 떠 보이면 -")]
    public float depthPull = 0f;
    [Tooltip("화면이 좌우로 뒤집혀 보이면 (0,180,0)")]
    public Vector3 rotationOffset = Vector3.zero;

    // 한 번이라도 태그를 봤는지. 보기 전엔 화면 판을 숨긴다(DesktopScreenView가 읽음).
    public bool HasEverSeen { get; private set; }
    // 지금 태그가 보이는지 (마지막으로 본 지 lostTimeout 이내). 아니면 화면 판을 숨기고 터치도 막는다.
    public bool IsTracking => hasObs && Time.time - obsTime <= lostTimeout;

    PassthroughCameraAccess pca;

    [Header("깊이 센서로 거리 재기")]
    [Tooltip("켜면 태그까지 거리를 퀘스트 깊이 센서로 직접 잰다 → 태그 크기값이 틀려도 판이 종이 면에 붙는다. " +
             "못 재면 크기값으로 계산한 거리를 쓴다")]
    public bool useDepthSensor = true;
    [Tooltip("깊이 보정 비율을 얼마나 부드럽게 바꿀지 (0~1, 작을수록 부드럽고 느림)")]
    [Range(0.02f, 1f)] public float depthSmoothing = 0.1f;
    [Tooltip("센서로 잰 거리가 크기 기준 거리의 이 배수 범위를 벗어나면 (손에 가려짐 등) 무시")]
    public Vector2 depthAcceptRatio = new Vector2(0.5f, 1.8f);
    EnvironmentRaycastManager envRay;
    float depthRatio = 1f;          // 센서 거리 / 크기 기준 거리 (부드럽게 갱신)
    bool hasDepthRatio;
    int statDepthHits;
    TagDetector detector;
    int detW, detH;
    bool pending;
    bool permissionAsked;

    // 최근 관측 (월드)
    bool hasObs;
    Vector3 obsPos;
    Quaternion obsRot;
    float obsTime;
    Vector3 velPos;          // m/s
    Vector3 velRot;          // 축 × 도/초

    // 태그 기준 판 오프셋
    bool hasOffset;
    Vector3 offPos;
    Quaternion offRot = Quaternion.identity;
    bool recaptureRequested;

    Transform plane;
    Transform gizmo;
    float statTime;
    int statFrames, statHits;
    double statMs;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnEnable() { MonitorHandCalibrator.OnQuadCalibrated += OnCalibrated; }
    void OnDisable() { MonitorHandCalibrator.OnQuadCalibrated -= OnCalibrated; }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        detector?.Dispose();
        detector = null;
    }

    void Start()
    {
        hasOffset = LoadOffset();
        LoadLayout();
        RequestPermission();

        if (useDepthSensor)
        {
            if (EnvironmentRaycastManager.IsSupported)
            {
                envRay = FindAnyObjectByType<EnvironmentRaycastManager>();
                if (envRay == null) envRay = gameObject.AddComponent<EnvironmentRaycastManager>();
                Debug.Log("[AprilTag] 깊이 센서로 거리 재기 켬");
            }
            else Debug.LogWarning("[AprilTag] 깊이 센서 미지원 → 태그 크기값으로 거리 계산");
        }

        // [AR LAPTOP] 고정 배치면 손으로 판 그리기는 안 쓴다 (주먹 쥐어도 배치 모드가 안 뜨게)
        if (fixedLayout && MonitorHandCalibrator.Instance != null)
        {
            MonitorHandCalibrator.Instance.enabled = false;
            Debug.Log("[AprilTag] 고정 배치 모드 → 손 캘리브레이션 끔");
        }

        if (!PassthroughCameraAccess.IsSupported)
            Debug.LogWarning("[AprilTag] 이 환경에선 패스스루 카메라를 못 씀 (에디터/Link면 정상). APK로 실행해볼 것");

        // 해상도는 켜지기 전에 정해야 하므로 꺼진 상태로 만들고 설정 후 켠다
        var go = new GameObject("AprilTagCamera");
        go.SetActive(false);
        go.transform.SetParent(transform, false);
        pca = go.AddComponent<PassthroughCameraAccess>();
        pca.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
        pca.RequestedResolution = resolution;
        go.SetActive(true);

        Debug.Log($"[AprilTag] 시작 — 태그 ID {tagId}, 크기 {tagSize}m, 오프셋 {(hasOffset ? "불러옴" : "없음(판 캘리브레이션 후 자동 저장)")}");
    }

    void RequestPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const string perm = "horizonos.permission.HEADSET_CAMERA";
        const string scene = "com.oculus.permission.USE_SCENE";   // 깊이 센서(공간 데이터)
        if (!permissionAsked)
        {
            permissionAsked = true;
            var need = new System.Collections.Generic.List<string>();
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(perm)) need.Add(perm);
            if (useDepthSensor && !UnityEngine.Android.Permission.HasUserAuthorizedPermission(scene)) need.Add(scene);
            if (need.Count > 0) UnityEngine.Android.Permission.RequestUserPermissions(need.ToArray());
        }
#endif
    }

    void OnCalibrated(int index)
    {
        if (index != monitorIndex) return;
        recaptureRequested = true;
        Debug.Log("[AprilTag] 판을 새로 그림 → 태그가 보이면 오프셋 다시 저장");
    }

    // ── 영상 받기 → 태그 찾기 ───────────────────────────────────────────────
    void Update()
    {
        if (pca == null || !pca.isActiveAndEnabled || !pca.IsPlaying) return;
        if (pending || !pca.IsUpdatedThisFrame) return;

        var tex = pca.GetTexture();
        if (tex == null) return;

        Pose camPose = pca.GetCameraPose();         // 이 영상을 찍은 순간의 카메라 위치
        Vector2Int cur = pca.CurrentResolution;
        var intr = pca.Intrinsics;
        pending = true;

        AsyncGPUReadback.Request(tex, 0, TextureFormat.RGBA32, req =>
        {
            pending = false;
            if (req.hasError || this == null) return;
            var data = req.GetData<Color32>();
            Detect(data, req.width, req.height, camPose, cur, intr);
        });
    }

    void Detect(NativeArray<Color32> data, int w, int h, Pose camPose, Vector2Int cur,
                PassthroughCameraAccess.CameraIntrinsics intr)
    {
        if (data.Length < w * h) return;
        if (detector == null || detW != w || detH != h)
        {
            detector?.Dispose();
            detector = new TagDetector(w, h, Mathf.Clamp(decimation, 1, 4));
            detW = w; detH = h;
            Debug.Log($"[AprilTag] 영상 {w}x{h}, 감지기 준비");
        }

        // 렌즈 값(초점거리·중심점)을 지금 해상도 픽셀로 환산 (센서가 잘려서 들어올 수 있음)
        Vector2 sensor = intr.SensorResolution;
        Vector2 scale = (Vector2)cur / sensor;
        scale /= Mathf.Max(scale.x, scale.y);
        Vector2 cropSize = new Vector2(sensor.x * scale.x, sensor.y * scale.y);
        Vector2 cropOrigin = (sensor - cropSize) * 0.5f;
        float k = h / cropSize.y;                               // 센서 px → 영상 px
        float fx = intr.FocalLength.x * k, fy = intr.FocalLength.y * k;
        float cx = (intr.PrincipalPoint.x - cropOrigin.x) * k;
        float cy = (intr.PrincipalPoint.y - cropOrigin.y) * k;
        float fovY = 2f * Mathf.Atan(h * 0.5f / fy);            // keijiro는 세로 시야각(라디안)을 받는다

        var span = data.AsReadOnlySpan().Slice(0, w * h);
        Color32[] flipped = null;
        if (flipVertical)
        {
            flipped = new Color32[w * h];
            for (int y = 0; y < h; y++)
                span.Slice((h - 1 - y) * w, w).CopyTo(new Span<Color32>(flipped, y * w, w));
        }

        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (flipped != null) detector.ProcessImage(flipped, fovY, tagSize);
        else detector.ProcessImage(span, fovY, tagSize);
        statMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        statFrames++;

        foreach (var tag in detector.DetectedTags)
        {
            if (tag.ID != tagId) continue;

            // keijiro는 중심점이 영상 정중앙이라고 가정한다 → 실제 중심점과의 차이만큼 보정
            Vector3 p = tag.Position;
            p.x += p.z * ((w * 0.5f - cx) / fx);
            p.y += p.z * ((h * 0.5f - cy) / fy);

            // 깊이 센서: 카메라 → 태그 중심 방향으로 레이를 쏴서 실제 표면까지 거리를 잰다.
            // 방향(영상 속 위치)은 그대로 두고 거리만 바꾸므로, 태그 크기값이 틀려도 판이 종이 면에 붙는다.
            if (envRay != null && envRay.isActiveAndEnabled)
            {
                float estDist = p.magnitude;
                Vector3 dirWorld = camPose.rotation * (p / Mathf.Max(estDist, 1e-4f));
                if (envRay.Raycast(new Ray(camPose.position, dirWorld), out var eh, 5f))
                {
                    float sensed = Vector3.Distance(camPose.position, eh.point);
                    float r = sensed / Mathf.Max(estDist, 1e-4f);
                    if (r >= depthAcceptRatio.x && r <= depthAcceptRatio.y)
                    {
                        depthRatio = hasDepthRatio ? Mathf.Lerp(depthRatio, r, depthSmoothing) : r;
                        hasDepthRatio = true;
                        statDepthHits++;
                    }
                }
            }
            if (useDepthSensor && hasDepthRatio) p *= depthRatio;   // 못 잰 프레임은 마지막 비율 유지

            Vector3 newPos = camPose.position + camPose.rotation * p;
            Quaternion newRot = camPose.rotation * tag.Rotation;
            float dtObs = Time.time - obsTime;
            if (hasObs && dtObs > 0.005f && dtObs < 0.3f)
            {
                // 이동 속도(m/s)와 회전 속도를 살짝 평균 내서 기억 → 예측에 씀
                Vector3 v = (newPos - obsPos) / dtObs;
                velPos = Vector3.Lerp(velPos, v, 0.5f);
                (newRot * Quaternion.Inverse(obsRot)).ToAngleAxis(out float ang, out Vector3 axis);
                if (ang > 180f) ang -= 360f;
                Vector3 angVel = float.IsFinite(axis.x) ? axis * (ang / dtObs) : Vector3.zero;
                velRot = Vector3.Lerp(velRot, angVel, 0.5f);
            }
            else { velPos = Vector3.zero; velRot = Vector3.zero; }
            obsPos = newPos;
            obsRot = newRot;
            obsTime = Time.time;
            hasObs = true;
            statHits++;
            break;
        }
    }

    // ── 판 옮기기 ───────────────────────────────────────────────────────────
    void LateUpdate()
    {
        StatLog();

        bool fresh = hasObs && Time.time - obsTime <= lostTimeout;
        UpdateGizmo(fresh);
        if (!fresh) return;

        if (plane == null && !FindPlane()) return;
        if (!plane.gameObject.activeInHierarchy) return;

        // 판을 그리는 중이면 손이 우선
        var cal = MonitorHandCalibrator.Instance;
        if (cal != null && cal.IsPlacing) return;

        if (fixedLayout && follow)
        {
            PlaceFixed();
            return;
        }

        if (recaptureRequested || (!hasOffset && IsCalibrated()))
        {
            Capture();
            recaptureRequested = false;
            return;
        }
        if (!hasOffset || !follow) return;

        Vector3 targetPos = obsPos + obsRot * offPos;
        Quaternion targetRot = obsRot * offRot;
        float a = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        plane.SetPositionAndRotation(Vector3.Lerp(plane.position, targetPos, a),
                                     Quaternion.Slerp(plane.rotation, targetRot, a));
    }

    // 태그 = 판의 왼쪽 위 모서리. 판은 태그와 같은 면 위에서 오른쪽·아래로 펼쳐진다.
    //
    // keijiro가 주는 태그 회전(유니티 기준): 로컬 +x = 인쇄된 태그의 오른쪽, +y = 위쪽,
    // +z = 종이 속으로(보는 사람 반대쪽). 유니티 Quad는 -z 쪽에서 보이므로 태그 회전을 그대로 쓰면
    // 화면이 뒤집히지 않고 똑바로 보인다.
    // 태그 크기값(tagSize)은 흰 네모(가운데 5칸) 한 변이고 전체는 9칸 → 바깥 꼭짓점 = ±0.9 × tagSize.
    void PlaceFixed()
    {
        float half = 0.9f * EffectiveTagSize;   // 깊이 센서로 보정된 실제 태그 크기 기준
        Vector3 local = new Vector3(
            -half + cornerOffset.x + quadWidth * 0.5f,
             half - cornerOffset.y - quadHeight * 0.5f,
            -liftFromSurface);
        // 예측: 마지막 관측 이후 흐른 시간 + 처리 지연만큼 앞으로
        float ahead = predictSeconds > 0f ? Mathf.Min(Time.time - obsTime + predictSeconds, 0.2f) : 0f;
        Vector3 pPos = obsPos + velPos * ahead;
        Quaternion pRot = obsRot;
        if (ahead > 0f && velRot.sqrMagnitude > 1e-6f)
            pRot = Quaternion.AngleAxis(velRot.magnitude * ahead, velRot.normalized) * obsRot;

        Vector3 targetPos = pPos + pRot * local;
        Quaternion targetRot = pRot * Quaternion.Euler(rotationOffset);

        // 깊이 보정: 눈 → 판 방향 선을 따라 당긴다 (눈에서 본 위치는 그대로, 거리만 바뀜)
        var cam = Camera.main;
        if (cam != null && Mathf.Abs(depthPull) > 1e-5f)
        {
            Vector3 toEye = cam.transform.position - targetPos;
            if (toEye.sqrMagnitude > 1e-6f) targetPos += toEye.normalized * depthPull;
        }

        // 앵커 밑에 붙어 있으면 앵커가 움직일 때 같이 흔들리므로 떼어낸다 (크기도 월드 기준으로 맞추기 위해)
        if (plane.parent != null) plane.SetParent(null, true);
        var s = plane.localScale;
        plane.localScale = new Vector3(quadWidth, quadHeight, s.z);

        if (!HasEverSeen)
        {
            plane.SetPositionAndRotation(targetPos, targetRot);   // 처음엔 바로 붙인다
            HasEverSeen = true;
            Debug.Log("[AprilTag] 태그 발견 → 판을 태그 오른쪽 아래로 붙임 (A4 크기)");
            return;
        }
        if (followSpeed <= 0f) { plane.SetPositionAndRotation(targetPos, targetRot); return; }

        // 떨림 억제: 아주 작은 차이는 무시, 크게 튀면 바로 따라감
        float dist = Vector3.Distance(plane.position, targetPos);
        float ang = Quaternion.Angle(plane.rotation, targetRot);
        if (dist < 0.002f && ang < 0.5f) return;
        if (dist > 0.05f || ang > 10f) { plane.SetPositionAndRotation(targetPos, targetRot); return; }

        float a = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        plane.SetPositionAndRotation(Vector3.Lerp(plane.position, targetPos, a),
                                     Quaternion.Slerp(plane.rotation, targetRot, a));
    }

    // 깊이 센서가 알려준 실제 태그 크기 (흰 네모 한 변, m)
    float EffectiveTagSize => useDepthSensor && hasDepthRatio ? tagSize * depthRatio : tagSize;

    // ── 판 위치·크기 조절 (PC 콘솔: off x y / size w h, 단위 cm) ──────────────
    const string LayoutKey = "aprilTag_layout_";

    public void SetCornerOffsetCm(float rightCm, float downCm)
    {
        cornerOffset = new Vector2(rightCm / 100f, downCm / 100f);
        PlayerPrefs.SetFloat(LayoutKey + "ox", cornerOffset.x);
        PlayerPrefs.SetFloat(LayoutKey + "oy", cornerOffset.y);
        PlayerPrefs.Save();
        Debug.Log($"[AprilTag] 판 모서리 = 태그 모서리에서 오른쪽 {rightCm}cm, 아래 {downCm}cm");
    }

    public void SetDepthCm(float cm)
    {
        depthPull = cm / 100f;
        PlayerPrefs.SetFloat(LayoutKey + "dp", depthPull);
        PlayerPrefs.Save();
        Debug.Log($"[AprilTag] 판을 {cm}cm 앞으로 당김");
    }

    // 태그 흰 네모 한 변(cm). 인쇄 크기와 다르면 거리가 틀어져 판이 앞/뒤로 밀린다
    public void SetTagCm(float innerCm)
    {
        tagSize = Mathf.Max(0.005f, innerCm / 100f);
        PlayerPrefs.SetFloat(LayoutKey + "ts", tagSize);
        PlayerPrefs.Save();
        Debug.Log($"[AprilTag] 태그 크기값 = {innerCm}cm (흰 네모 한 변)");
    }

    public void SetSizeCm(float wCm, float hCm)
    {
        quadWidth = Mathf.Max(0.05f, wCm / 100f);
        quadHeight = Mathf.Max(0.05f, hCm / 100f);
        PlayerPrefs.SetFloat(LayoutKey + "w", quadWidth);
        PlayerPrefs.SetFloat(LayoutKey + "h", quadHeight);
        PlayerPrefs.Save();
        Debug.Log($"[AprilTag] 판 크기 = {wCm} x {hCm} cm");
    }

    void LoadLayout()
    {
        if (PlayerPrefs.HasKey(LayoutKey + "ox"))
            cornerOffset = new Vector2(PlayerPrefs.GetFloat(LayoutKey + "ox"), PlayerPrefs.GetFloat(LayoutKey + "oy"));
        if (PlayerPrefs.HasKey(LayoutKey + "dp")) depthPull = PlayerPrefs.GetFloat(LayoutKey + "dp");
        if (PlayerPrefs.HasKey(LayoutKey + "ts")) tagSize = PlayerPrefs.GetFloat(LayoutKey + "ts");
        if (PlayerPrefs.HasKey(LayoutKey + "w"))
        {
            quadWidth = PlayerPrefs.GetFloat(LayoutKey + "w");
            quadHeight = PlayerPrefs.GetFloat(LayoutKey + "h");
        }
    }

    bool FindPlane()
    {
        foreach (var p in FindObjectsByType<MonitorPlane>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (p.monitorIndex == monitorIndex) { plane = p.transform; return true; }
        return false;
    }

    bool IsCalibrated()
    {
        var cal = MonitorHandCalibrator.Instance;
        if (cal == null || cal.slotKeys == null || monitorIndex >= cal.slotKeys.Length) return false;
        string key = cal.slotKeys[monitorIndex] + "_uuid";
        return PlayerPrefs.HasKey(key) && !string.IsNullOrEmpty(PlayerPrefs.GetString(key));
    }

    // 지금 판 위치를 태그 기준으로 저장
    public void Capture()
    {
        if (plane == null && !FindPlane()) return;
        if (!hasObs) { Debug.LogWarning("[AprilTag] 태그가 안 보여서 오프셋을 못 잡음"); return; }
        Quaternion inv = Quaternion.Inverse(obsRot);
        offPos = inv * (plane.position - obsPos);
        offRot = inv * plane.rotation;
        hasOffset = true;
        SaveOffset();
        Debug.Log($"[AprilTag] 오프셋 저장 — 태그에서 판 중심까지 {offPos.magnitude * 100f:F1}cm");
    }

    public void ClearOffset()
    {
        foreach (var s in new[] { "px", "py", "pz", "rx", "ry", "rz", "rw" }) PlayerPrefs.DeleteKey(Key + s);
        hasOffset = false;
        Debug.Log("[AprilTag] 오프셋 삭제");
    }

    string Key => $"apriltag_mon{monitorIndex}_id{tagId}_";

    void SaveOffset()
    {
        PlayerPrefs.SetFloat(Key + "px", offPos.x); PlayerPrefs.SetFloat(Key + "py", offPos.y); PlayerPrefs.SetFloat(Key + "pz", offPos.z);
        PlayerPrefs.SetFloat(Key + "rx", offRot.x); PlayerPrefs.SetFloat(Key + "ry", offRot.y);
        PlayerPrefs.SetFloat(Key + "rz", offRot.z); PlayerPrefs.SetFloat(Key + "rw", offRot.w);
        PlayerPrefs.Save();
    }

    bool LoadOffset()
    {
        if (!PlayerPrefs.HasKey(Key + "rw")) return false;
        offPos = new Vector3(PlayerPrefs.GetFloat(Key + "px"), PlayerPrefs.GetFloat(Key + "py"), PlayerPrefs.GetFloat(Key + "pz"));
        offRot = new Quaternion(PlayerPrefs.GetFloat(Key + "rx"), PlayerPrefs.GetFloat(Key + "ry"),
                                PlayerPrefs.GetFloat(Key + "rz"), PlayerPrefs.GetFloat(Key + "rw")).normalized;
        return true;
    }

    void UpdateGizmo(bool visible)
    {
        if (!showTagGizmo)
        {
            if (gizmo != null) gizmo.gameObject.SetActive(false);
            return;
        }
        if (gizmo == null)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = "AprilTagGizmo";
            DestroyImmediate(g.GetComponent<Collider>());   // 레이를 막지 않게
            g.transform.SetParent(transform, false);
            gizmo = g.transform;
        }
        gizmo.gameObject.SetActive(visible);
        if (visible)
        {
            // 태그 전체(바깥 테두리 = 1.8 × tagSize) 크기의 얇은 판 → 실제 태그와 겹치면 인식 위치가 맞는 것
            gizmo.SetPositionAndRotation(obsPos, obsRot);
            gizmo.localScale = new Vector3(1.8f * EffectiveTagSize, 1.8f * EffectiveTagSize, 0.002f);
        }
    }

    void StatLog()
    {
        if (Time.time - statTime < 2f) return;
        float dt = Time.time - statTime;
        statTime = Time.time;
        if (pca == null) return;
        if (!pca.IsPlaying) { Debug.Log("[AprilTag] 카메라 대기 중 (권한/지원 확인)"); return; }
        if (statFrames > 0)
            Debug.Log($"[AprilTag] 처리 {statFrames / dt:F0}장/초 · 평균 {statMs / statFrames:F1}ms · 태그 {tagId} 발견 {statHits}/{statFrames}"
                      + (useDepthSensor ? $" · 깊이센서 {statDepthHits}회 · 보정 x{depthRatio:F2} (실제 태그 흰네모 약 {tagSize * depthRatio * 100f:F1}cm)" : ""));
        statFrames = 0; statHits = 0; statMs = 0; statDepthHits = 0;
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("실험/AprilTag/오프셋 다시 잡기 (판을 먼저 맞춰둘 것)")]
    static void MenuCapture() { if (Instance) Instance.Capture(); else Debug.LogWarning("[AprilTag] Play 중에만 됨"); }

    [UnityEditor.MenuItem("실험/AprilTag/오프셋 삭제")]
    static void MenuClear() { if (Instance) Instance.ClearOffset(); else Debug.LogWarning("[AprilTag] Play 중에만 됨"); }
#endif
}
