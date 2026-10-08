using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// 세션 기록기 (Session Recorder)
//
// 참가자가 한 일을 "좌표"로 남긴다. 영상이 아니라 숫자라서 가볍고,
// 나중에 replay.html에서 아무 각도로나 다시 볼 수 있다.
//
// 남는 것 (session.jsonl — 한 줄에 레코드 하나):
//   meta   세션 정보 (참가자, 조건, 시작 시각)
//   mon    모니터 판의 위치/크기            ← 모든 좌표의 기준점
//   win    창이 하나 생김 (짧은 번호를 매긴다)
//   gone   창이 데스크탑으로 돌아감
//   f      프레임: 머리 + 양손 + 모든 창의 위치/회전/크기   ← 초당 30번
//   lk     선이 생기거나 사라짐
//   ev     클릭·스크롤·리사이즈 같은 단발 사건
//   task   태스크 시작/종료
//
// 저장 위치 (조건마다 파일이 따로 생긴다):
//   (A) 헤드셋   Application.persistentDataPath/replay/<참가자>_<조건>_<시각>.jsonl
//   (B) PC 서버  PCCaptureServer/replay/<참가자>/<참가자>_<조건>_<시각>.jsonl  ← 이게 편하다
//
// 씬에 아무것도 안 넣어도 실행 시 자동 생성된다.
// ─────────────────────────────────────────────────────────────────────────────
public class SessionRecorder : MonoBehaviour
{
    public static SessionRecorder Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("SessionRecorder");
        DontDestroyOnLoad(go);
        go.AddComponent<SessionRecorder>();
    }

    [Header("참가자 / 조건")]
    [Tooltip("PC 서버 콘솔에서 'p P07'을 치면 실행 중에도 바뀐다")]
    public string participantId = "P00";
    public string condition = "default";
    public string taskId = "none";

    [Header("기록")]
    [Tooltip("초당 몇 프레임을 남길지. 30이면 부드럽게 재생된다. 파일을 줄이려면 20.")]
    public float recordHz = 30f;
    [Tooltip("새 창/새 선이 생겼는지 확인하는 주기(초)")]
    public float rescanInterval = 0.2f;
    public bool writeLocalFile = true;
    public bool forwardToPC = true;

    // ── 세션 상태 ────────────────────────────────────────────────────────────
    public string SessionId { get; private set; }
    public float Now => Time.realtimeSinceStartup - sessionStart;
    public bool TaskRunning => taskStart >= 0f;   // 조건 패널이 태스크 중엔 숨기려고 본다

    private float sessionStart;
    private float taskStart = -1f;
    private StreamWriter file;
    private float frameTimer, scanTimer, sendTimer;

    // 모니터 판 위치를 주기적으로 "다시" 적는 타이머.
    // EmitMonitors는 위치가 안 바뀌면 건너뛰는데, 녹화 파일은 rec start 때 새로 열린다.
    // 배치를 먼저 하고 녹화를 시작하면 그 한 번의 기록이 이전 파일에 들어가서
    // 새 파일엔 모니터 정보가 없고 → replay에서 데스크탑 판이 안 뜨고 좌표가 어긋난다.
    // 몇 초마다 강제로 다시 적어서 어느 파일이든 곧 모니터 정보를 갖게 한다.
    private float monHeartbeat;
    public float monHeartbeatInterval = 3f;

    // 창마다 짧은 번호(n)를 매긴다. 프레임마다 긴 HWND를 쓰면 파일이 커진다.
    class Win
    {
        public int n;
        public WindowQuadReceiver recv;
        public Vector3 lastPos;
        public float path;      // 누적 이동 거리(m)
        public float bornAt;
    }
    private readonly Dictionary<int, Win> wins = new();   // GetInstanceID → Win
    private int nextN = 0;

    private readonly Dictionary<int, int[]> links = new(); // link InstanceID → {a, b}
    private readonly HashSet<int> tethers = new();

    private Transform head;
    private Transform monitor0;
    private float pixelsToMeters = 0.0005f;
    private bool lastHandsEnabled = true;

    // PC로 보낼 줄을 잠깐 모았다가 한 번에 보낸다
    private readonly List<string> pending = new();

    // 서버는 meta 줄을 받아야 기록 파일을 연다.
    // 연결이 끊겼다 다시 붙으면 서버는 이 세션을 모르는 상태이므로 meta를 다시 보내야 한다.
    private string metaLine = "";
    private bool resendMeta = false;
    private readonly StringBuilder sb = new StringBuilder(1024);

    // 태스크 카운터
    private readonly Dictionary<string, int> counters = new();

    void Awake()
    {
        Instance = this;
        sessionStart = Time.realtimeSinceStartup;
        SessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        // 파일은 Start에서 연다. 조건(ConditionManager)이 Awake에서 정해지므로,
        // 여기서 열면 파일 이름에 이전 조건이 박힐 수 있다.
    }

    void Start()
    {
        head = Camera.main != null ? Camera.main.transform : null;
        foreach (var p in HandRayPointer.All) { if (p != null) { pixelsToMeters = p.pixelsToMeters; break; } }
        OpenFile();
        WriteMeta();
    }

    void OpenFile()
    {
        if (!writeLocalFile) return;
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "replay");
            Directory.CreateDirectory(dir);
            string name = $"{participantId}_{ConditionManager.Code}_{SessionId}.jsonl";
            file = new StreamWriter(Path.Combine(dir, name), true, Encoding.UTF8);
            Debug.Log($"[Rec] 기록 파일: {Path.Combine(dir, name)}");
        }
        catch (Exception e)
        {
            Debug.LogError("[Rec] 파일 열기 실패: " + e.Message);
            file = null;
        }
    }

    void WriteMeta()
    {
        metaLine = "{\"k\":\"meta\",\"v\":1"
           + ",\"p\":\"" + Esc(participantId) + "\""
           + ",\"c\":\"" + Esc(condition) + "\""
           + ",\"cond\":\"" + ConditionManager.Code + "\""
           + ",\"mons\":" + ConditionManager.MonitorCount
           + ",\"links\":" + (ConditionManager.LinksEnabled ? "true" : "false")
           + ",\"session\":\"" + SessionId + "\""
           + ",\"started\":\"" + DateTime.Now.ToString("o") + "\""
           + ",\"hz\":" + F(recordHz, 1)
           + ",\"p2m\":" + F(pixelsToMeters, 6)
           + ",\"device\":\"" + Esc(SystemInfo.deviceModel) + "\"}";
        Emit(metaLine);
        EmitMonitors();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PC와 다시 연결됐을 때 PCStreamReceiver가 불러준다.
    // 서버 쪽은 방금 붙은 클라이언트가 누군지 모르므로, meta를 다시 보내 파일을 열게 하고
    // 모니터 위치도 다시 적어준다. (헤드셋 로컬 파일에는 이미 다 들어가 있다)
    // ─────────────────────────────────────────────────────────────────────────
    public void OnPCReconnected()
    {
        resendMeta = true;
        EmitMonitors(true);
        Debug.Log("[Rec] PC 재연결 — meta를 다시 보냅니다");
    }

    // 모니터 판은 공간 앵커가 로드된 "뒤에" 제자리로 옮겨진다.
    // Start에서 한 번만 적으면 아직 옮겨지기 전의 엉뚱한 좌표가 박혀서,
    // 나중에 replay에서 창들이 모니터와 따로 노는 것처럼 보인다.
    // 그래서 바뀔 때마다 다시 적고, 재생할 땐 마지막 것을 쓴다.
    private readonly Dictionary<int, Vector3> lastMonPos = new();
    private readonly Dictionary<int, Quaternion> lastMonRot = new();
    private readonly Dictionary<int, Vector2> lastMonSize = new();

    void EmitMonitors(bool force = false)
    {
        var active = new HashSet<int>();
        var planes = FindObjectsByType<MonitorPlane>(FindObjectsSortMode.None);
        if (planes.Length == 0 && force)
            Debug.LogWarning("[Rec] 씬에 켜져 있는 MonitorPlane이 없습니다. " +
                             "배치(cal)를 안 했거나 조건이 다 꺼둔 상태라, replay에 데스크탑 판이 안 나옵니다.");
        foreach (var m in planes)
        {
            active.Add(m.monitorIndex);
            int i = m.monitorIndex;
            if (i == 0) monitor0 = m.transform;
            var t = m.transform;
            Vector3 pos = t.position;
            Quaternion rot = t.rotation;
            Vector2 size = new Vector2(t.lossyScale.x, t.lossyScale.y);

            if (!force && lastMonPos.TryGetValue(i, out var pp))
            {
                bool same = Vector3.Distance(pp, pos) < 0.003f
                         && Quaternion.Angle(lastMonRot[i], rot) < 0.4f
                         && (lastMonSize[i] - size).magnitude < 0.003f;
                if (same) continue;               // 안 움직였으면 다시 안 적는다
            }
            lastMonPos[i] = pos; lastMonRot[i] = rot; lastMonSize[i] = size;

            Emit("{\"k\":\"mon\",\"t\":" + F(Now, 3) + ",\"i\":" + i
               + ",\"pos\":" + V3(pos)
               + ",\"rot\":" + V3(rot.eulerAngles)
               + ",\"size\":[" + F(size.x, 4) + "," + F(size.y, 4) + "]}");
        }

        // 조건이 바뀌어 꺼진 모니터는 "이제 없다"고 남긴다.
        // 안 그러면 replay에 예전 위치의 유령 판이 계속 떠 있는다.
        var gone = new List<int>();
        foreach (var i in lastMonPos.Keys) if (!active.Contains(i)) gone.Add(i);
        foreach (int i in gone)
        {
            lastMonPos.Remove(i); lastMonRot.Remove(i); lastMonSize.Remove(i);
            Emit("{\"k\":\"monoff\",\"t\":" + F(Now, 3) + ",\"i\":" + i + "}");
        }
    }

    // ── 루프 ─────────────────────────────────────────────────────────────────
    void Update()
    {
        if (head == null && Camera.main != null) head = Camera.main.transform;

        scanTimer += Time.unscaledDeltaTime;
        if (scanTimer >= rescanInterval) { scanTimer = 0f; Rescan(); }

        TrackHandToggle();

        float period = 1f / Mathf.Max(1f, recordHz);
        frameTimer += Time.unscaledDeltaTime;
        if (frameTimer >= period) { frameTimer = 0f; WriteFrame(); }

        sendTimer += Time.unscaledDeltaTime;
        if (sendTimer >= 0.2f) { sendTimer = 0f; FlushToPC(); }

        monHeartbeat += Time.unscaledDeltaTime;
        if (monHeartbeat >= Mathf.Max(0.5f, monHeartbeatInterval))
        {
            monHeartbeat = 0f;
            EmitMonitors(true);   // 안 움직였어도 강제로 다시 적는다
        }
    }

    // ── 새 창 / 사라진 창 / 선 ───────────────────────────────────────────────
    void Rescan()
    {
        EmitMonitors();   // 앵커가 붙어 위치가 바뀌었으면 새로 적는다

        var found = FindObjectsByType<WindowQuadReceiver>(FindObjectsSortMode.None);
        var alive = new HashSet<int>();
        foreach (var r in found)
        {
            int key = r.GetInstanceID();
            alive.Add(key);
            if (wins.ContainsKey(key)) continue;

            var w = new Win { n = nextN++, recv = r, lastPos = r.transform.position, bornAt = Now };
            wins[key] = w;
            Count("windows_ejected");

            var sc = r.transform.localScale;
            Emit("{\"k\":\"win\",\"n\":" + w.n
               + ",\"id\":" + r.windowId
               + ",\"mon\":" + r.sourceMonitorIndex
               + ",\"px\":[" + F(sc.x / Mathf.Max(pixelsToMeters, 1e-6f), 0) + "," + F(sc.y / Mathf.Max(pixelsToMeters, 1e-6f), 0) + "]"
               + ",\"t\":" + F(Now, 3) + "}");
        }

        var gone = new List<int>();
        foreach (var kv in wins) if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (int key in gone)
        {
            var w = wins[key];
            Count("windows_inserted");
            Emit("{\"k\":\"gone\",\"n\":" + w.n
               + ",\"t\":" + F(Now, 3)
               + ",\"life\":" + F(Now - w.bornAt, 2)
               + ",\"path\":" + F(w.path, 3) + "}");
            wins.Remove(key);
        }

        // 창↔창 선
        var qls = FindObjectsByType<QuadLink>(FindObjectsSortMode.None);
        var aliveLinks = new HashSet<int>();
        foreach (var l in qls)
        {
            int key = l.GetInstanceID();
            aliveLinks.Add(key);
            // 끝점은 AR 창(0,1,2...)일 수도 있고 데스크탑 모니터 판일 수도 있다.
            // 모니터는 음수로 적는다: -1 = 모니터 0, -2 = 모니터 1 ...
            int a = EndpointOf(l.from), b = EndpointOf(l.to);
            if (a == NONE || b == NONE) continue;
            string kind = (a < 0 || b < 0) ? "dq" : "qq";

            if (!links.TryGetValue(key, out var cur))
            {
                links[key] = new[] { a, b };
                Count("links_created");
                Emit("{\"k\":\"lk\",\"t\":" + F(Now, 3) + ",\"op\":1,\"a\":" + a + ",\"b\":" + b + ",\"kind\":\"" + kind + "\"}");
            }
            else if (cur[0] != a || cur[1] != b)
            {
                links[key] = new[] { a, b };
                Count("links_flipped");
                Emit("{\"k\":\"lk\",\"t\":" + F(Now, 3) + ",\"op\":2,\"a\":" + a + ",\"b\":" + b + ",\"kind\":\"" + kind + "\"}");
            }
        }
        var goneLinks = new List<int>();
        foreach (var k in links.Keys) if (!aliveLinks.Contains(k)) goneLinks.Add(k);
        foreach (int k in goneLinks)
        {
            var ab = links[k];
            links.Remove(k);
            Count("links_removed");
            Emit("{\"k\":\"lk\",\"t\":" + F(Now, 3) + ",\"op\":0,\"a\":" + ab[0] + ",\"b\":" + ab[1] + ",\"kind\":\"qq\"}");
        }

        // 데스크탑↔창 선
        var wts = FindObjectsByType<WindowTether>(FindObjectsSortMode.None);
        var aliveT = new HashSet<int>();
        foreach (var t in wts)
        {
            int key = t.GetInstanceID();
            aliveT.Add(key);
            if (tethers.Contains(key)) continue;
            tethers.Add(key);
            var r = t.GetComponent<WindowQuadReceiver>();
            int n = r != null ? NOfId(r.GetInstanceID()) : -1;
            if (n < 0) continue;
            Emit("{\"k\":\"lk\",\"t\":" + F(Now, 3) + ",\"op\":1,\"a\":-1,\"b\":" + n
               + ",\"mon\":" + (r != null ? r.sourceMonitorIndex : 0) + ",\"kind\":\"dq\"}");
        }
        var goneT = new List<int>();
        foreach (int k in tethers) if (!aliveT.Contains(k)) goneT.Add(k);
        foreach (int k in goneT) tethers.Remove(k);
    }

    const int NONE = -9999;

    // 창이면 0,1,2... / 모니터 판이면 -1, -2, ... / 둘 다 아니면 NONE
    int EndpointOf(Transform t)
    {
        if (t == null) return NONE;
        var r = t.GetComponent<WindowQuadReceiver>();
        if (r != null)
        {
            int n = NOfId(r.GetInstanceID());
            return n < 0 ? NONE : n;
        }
        var m = t.GetComponent<MonitorPlane>();
        if (m != null) return -1 - m.monitorIndex;
        return NONE;
    }

    int NOf(Transform t)
    {
        if (t == null) return -1;
        var r = t.GetComponent<WindowQuadReceiver>();
        return r != null ? NOfId(r.GetInstanceID()) : -1;
    }
    int NOfId(int instanceId) => wins.TryGetValue(instanceId, out var w) ? w.n : -1;

    void TrackHandToggle()
    {
        if (HandInteractionToggle.HandsEnabled == lastHandsEnabled) return;
        lastHandsEnabled = HandInteractionToggle.HandsEnabled;
        Count("hand_toggles");
        Event("hand_toggle", "\"on\":" + (lastHandsEnabled ? "true" : "false"));
    }

    // ── 한 프레임 ────────────────────────────────────────────────────────────
    void WriteFrame()
    {
        sb.Length = 0;
        sb.Append("{\"k\":\"f\",\"t\":").Append(F(Now, 3));

        if (head != null)
        {
            var e = head.rotation.eulerAngles;
            sb.Append(",\"h\":[")
              .Append(F(head.position.x, 4)).Append(',').Append(F(head.position.y, 4)).Append(',').Append(F(head.position.z, 4)).Append(',')
              .Append(F(e.x, 1)).Append(',').Append(F(e.y, 1)).Append(',').Append(F(e.z, 1)).Append(']');
        }

        // 손: 손목 위치 + 조준 방향 + 꼬집기 세기
        foreach (var p in HandRayPointer.All)
        {
            if (p == null || p.hand == null || !p.hand.IsTracked) continue;
            Transform pose = p.hand.PointerPose != null ? p.hand.PointerPose
                           : (p.pointerPoseFallback != null ? p.pointerPoseFallback : p.transform);
            float pinch = 0f;
            try { pinch = p.hand.GetFingerPinchStrength(OVRHand.HandFinger.Index); } catch { }
            Vector3 hp = pose.position, hd = pose.forward;
            sb.Append(",\"").Append(p.handId == "L" ? "L" : "R").Append("\":[")
              .Append(F(hp.x, 4)).Append(',').Append(F(hp.y, 4)).Append(',').Append(F(hp.z, 4)).Append(',')
              .Append(F(hd.x, 3)).Append(',').Append(F(hd.y, 3)).Append(',').Append(F(hd.z, 3)).Append(',')
              .Append(F(pinch, 2)).Append(']');
        }

        // 창들: [n, x,y,z, rx,ry,rz, w,h]
        sb.Append(",\"w\":[");
        bool first = true;
        foreach (var kv in wins)
        {
            var w = kv.Value;
            if (w.recv == null) continue;
            var tr = w.recv.transform;
            Vector3 pos = tr.position;
            w.path += Vector3.Distance(pos, w.lastPos);
            w.lastPos = pos;

            var e = tr.rotation.eulerAngles;
            if (!first) sb.Append(',');
            first = false;
            sb.Append('[').Append(w.n).Append(',')
              .Append(F(pos.x, 4)).Append(',').Append(F(pos.y, 4)).Append(',').Append(F(pos.z, 4)).Append(',')
              .Append(F(e.x, 1)).Append(',').Append(F(e.y, 1)).Append(',').Append(F(e.z, 1)).Append(',')
              .Append(F(tr.localScale.x, 4)).Append(',').Append(F(tr.localScale.y, 4)).Append(']');
        }
        sb.Append("]}");
        Emit(sb.ToString());
    }

    // ── 바깥에서 부르는 것들 ─────────────────────────────────────────────────
    // 단발 사건 기록.  SessionRecorder.Ev("click", "\"n\":0");
    public static void Ev(string type, string extraJson = null)
    {
        if (Instance != null) Instance.Event(type, extraJson);
    }
    public static void Bump(string key, int by = 1)
    {
        if (Instance != null) Instance.Count(key, by);
    }

    void Event(string type, string extraJson)
    {
        string s = "{\"k\":\"ev\",\"t\":" + F(Now, 3) + ",\"e\":\"" + Esc(type) + "\"";
        if (!string.IsNullOrEmpty(extraJson)) s += "," + extraJson;
        Emit(s + "}");
    }
    void Count(string key, int by = 1)
    {
        counters.TryGetValue(key, out int c);
        counters[key] = c + by;
    }

    // ── 실험 진행 명령 (PC 콘솔에서 온다) ────────────────────────────────────
    // "study,participant,P07" / "study,condition,hybrid" / "study,task,1" /
    // "study,end" / "study,note,..." / "study,mark,..."
    public void HandleStudyCommand(string msg)
    {
        var parts = msg.Split(new[] { ',' }, 3);
        if (parts.Length < 2) return;
        string cmd = parts[1].Trim().ToLowerInvariant();
        string arg = parts.Length >= 3 ? parts[2].Trim() : "";

        switch (cmd)
        {
            case "participant":
                participantId = arg;
                BeginConditionRun();      // 참가자가 바뀌면 파일도 새로
                Debug.Log($"[Rec] 참가자 = {participantId}");
                break;

            // "study,cond,2 on"  →  모니터 2대 · 선 있음
            case "cond":
            {
                var a = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int mons = ConditionManager.MonitorCount;
                bool lk = ConditionManager.LinksEnabled;
                if (a.Length >= 1) int.TryParse(a[0], out mons);
                if (a.Length >= 2) lk = a[1] == "on" || a[1] == "1" || a[1] == "true";
                if (ConditionManager.Instance != null)
                    ConditionManager.Instance.Apply(mons, lk);   // 안에서 BeginConditionRun까지 부른다
                break;
            }

            // "study,cal,"   → 저장된 모니터 배치를 지우고 처음부터 다시
            // "study,cal,1"  → 1대만 배치  /  "study,cal,2" → 2대 배치
            case "cal":
            {
                int n = 0;
                if (arg.Length > 0) int.TryParse(arg.Trim(), out n);
                if (MonitorHandCalibrator.Instance != null)
                    MonitorHandCalibrator.Instance.Recalibrate(n);
                else
                    Debug.LogWarning("[Rec] MonitorHandCalibrator가 씬에 없습니다");
                break;
            }

            // "aim" 명령은 진단용이었고, HandRayPointer를 커밋 버전으로 되돌리면서 함께 제거됨.
            case "aim":
            {
                var pcAim = PCStreamReceiver.Instance;
                if (pcAim != null && pcAim.IsConnected)
                    pcAim.SendText("msg,aim 명령은 지금 버전에서는 사용할 수 없습니다");
                break;
            }

            // 다음 시행 전에 공중의 창을 전부 데스크탑으로
            case "clear":
                if (ConditionManager.Instance != null) ConditionManager.Instance.ClearAllWindows();
                break;

            case "condition":
                condition = arg;
                Event("condition_set", "\"c\":\"" + Esc(arg) + "\"");
                break;

            case "task": StartTask(arg); break;
            case "end": EndTask(); break;
            case "note": Event("note", "\"text\":\"" + Esc(arg) + "\""); break;
            case "mark":
                Event("mark", "\"label\":\"" + Esc(arg) + "\",\"unix_ms\":" + DateTimeOffset.Now.ToUnixTimeMilliseconds());
                break;
        }
    }

    // 조건이 바뀌면 기록을 새 파일로 시작한다 (조건마다 따로 저장)
    public void BeginConditionRun()
    {
        EndTask();
        CloseFile();
        SessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        sessionStart = Time.realtimeSinceStartup;
        nextN = 0; wins.Clear(); links.Clear(); tethers.Clear(); counters.Clear();
        OpenFile();
        WriteMeta();
        Debug.Log($"[Rec] 새 기록 시작 — {participantId} / {ConditionManager.Code}");
    }

    public void StartTask(string id)
    {
        EndTask();
        taskId = string.IsNullOrEmpty(id) ? "task" : id;
        taskStart = Time.realtimeSinceStartup;
        counters.Clear();
        Emit("{\"k\":\"task\",\"t\":" + F(Now, 3) + ",\"op\":\"start\",\"name\":\"" + Esc(taskId) + "\""
           + ",\"p\":\"" + Esc(participantId) + "\",\"c\":\"" + Esc(condition) + "\""
           + ",\"cond\":\"" + ConditionManager.Code + "\"}");
        Debug.Log($"[Rec] 태스크 시작: {taskId}");
    }

    public void EndTask()
    {
        if (taskStart < 0f) return;
        float dur = Time.realtimeSinceStartup - taskStart;

        float path = 0f;
        foreach (var kv in wins) path += kv.Value.path;

        var s = new StringBuilder();
        s.Append("{\"k\":\"task\",\"t\":").Append(F(Now, 3))
         .Append(",\"op\":\"end\",\"name\":\"").Append(Esc(taskId)).Append('"')
         .Append(",\"dur\":").Append(F(dur, 2))
         .Append(",\"open\":").Append(wins.Count)
         .Append(",\"path\":").Append(F(path, 3));
        foreach (var kv in counters) s.Append(",\"").Append(Esc(kv.Key)).Append("\":").Append(kv.Value);
        s.Append('}');
        Emit(s.ToString());

        Debug.Log($"[Rec] 태스크 끝: {taskId}  {dur:0.0}초");
        taskStart = -1f;
        taskId = "none";
        Flush();
    }

    // ── 출력 ─────────────────────────────────────────────────────────────────
    void Emit(string line)
    {
        try { file?.WriteLine(line); } catch { }
        if (forwardToPC) pending.Add(line);
    }

    void FlushToPC()
    {
        var pc = PCStreamReceiver.Instance;
        if (pc == null || !pc.IsConnected)
        {
            // 아직 연결 전이면 너무 쌓이지 않게 잘라둔다 (헤드셋 파일에는 이미 다 들어갔다)
            if (pending.Count > 4000) pending.RemoveRange(0, pending.Count - 2000);
            return;
        }

        // 재연결 직후면 meta를 맨 앞에 붙여 보낸다. 그래야 서버가 파일을 연다.
        if (resendMeta)
        {
            resendMeta = false;
            if (metaLine.Length > 0) pending.Insert(0, metaLine);
        }

        if (pending.Count == 0) return;
        pc.SendText("srec," + string.Join("\n", pending));
        pending.Clear();
    }

    void Flush() { try { file?.Flush(); } catch { } FlushToPC(); }

    void CloseFile()
    {
        Flush();
        try { file?.Dispose(); } catch { }
        file = null;
    }

    void OnApplicationPause(bool paused) { if (paused) Flush(); }
    void OnApplicationFocus(bool focus) { if (!focus) Flush(); }
    void OnApplicationQuit()
    {
        EndTask();
        Emit("{\"k\":\"end\",\"t\":" + F(Now, 3) + "}");
        CloseFile();
    }
    void OnDestroy() { if (Instance == this) Instance = null; }

    // ── 숫자/문자 포맷 ───────────────────────────────────────────────────────
    static readonly string[] FMT = { "0", "0.#", "0.##", "0.###", "0.####", "0.#####", "0.######" };
    static string F(float v, int dec)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
        return v.ToString(FMT[Mathf.Clamp(dec, 0, 6)], CultureInfo.InvariantCulture);
    }
    static string V3(Vector3 v) => "[" + F(v.x, 4) + "," + F(v.y, 4) + "," + F(v.z, 4) + "]";

    static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var b = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '"') b.Append("\\\"");
            else if (c == '\\') b.Append("\\\\");
            else if (c == '\n' || c == '\r' || c == '\t') b.Append(' ');
            else if (c < 32) b.Append(' ');
            else b.Append(c);
        }
        return b.ToString();
    }
}
