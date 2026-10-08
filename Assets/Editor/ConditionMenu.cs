#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// 유니티 에디터에서 "이 조건으로 실행" 을 고르는 곳.
//
// 상단 메뉴  [실험]
//   조건/모니터 1 - 선 없음  (M1L0)
//   조건/모니터 1 - 선 있음  (M1L1)
//   조건/모니터 2 - 선 없음  (M2L0)
//   조건/모니터 2 - 선 있음  (M2L1)          ← 지금 고른 것에 체크 표시
//   조건 창 열기                              ← 버튼 4개짜리 작은 창
//
// 고른 값은 EditorPrefs에 저장돼서 Play를 눌러도, 유니티를 껐다 켜도 유지된다.
// Play 중에 바꾸면 즉시 반영되고 기록 파일도 새로 열린다 (조건마다 따로 저장).
//
// 이 파일은 반드시 Assets/Editor/ 폴더 안에 있어야 한다 (빌드에 안 들어가게).
// ─────────────────────────────────────────────────────────────────────────────
static class ConditionMenu
{
    const string M1L0 = "실험/조건/모니터 1 - 선 없음  (M1L0)";
    const string M1L1 = "실험/조건/모니터 1 - 선 있음  (M1L1)";
    const string M2L0 = "실험/조건/모니터 2 - 선 없음  (M2L0)";
    const string M2L1 = "실험/조건/모니터 2 - 선 있음  (M2L1)";

    public static int Mons
    {
        get => EditorPrefs.GetInt(ConditionManager.PrefMons, 2);
        set => EditorPrefs.SetInt(ConditionManager.PrefMons, value);
    }
    public static bool Links
    {
        get => EditorPrefs.GetBool(ConditionManager.PrefLinks, true);
        set => EditorPrefs.SetBool(ConditionManager.PrefLinks, value);
    }
    public static string Code => "M" + Mons + "L" + (Links ? 1 : 0);

    public static void Set(int mons, bool links)
    {
        Mons = mons; Links = links;

        // Play 중이면 지금 바로 반영한다 (기록 파일도 새로 열린다)
        if (Application.isPlaying && ConditionManager.Instance != null)
            ConditionManager.Instance.Apply(mons, links);
        else
            Debug.Log($"[Cond] 다음 실행 조건: {Code}  (모니터 {mons}대 · 선 {(links ? "있음" : "없음")})");
    }

    [MenuItem(M1L0, false, 1)] static void S00() => Set(1, false);
    [MenuItem(M1L1, false, 2)] static void S01() => Set(1, true);
    [MenuItem(M2L0, false, 3)] static void S10() => Set(2, false);
    [MenuItem(M2L1, false, 4)] static void S11() => Set(2, true);

    // 지금 고른 조건에 체크 표시
    [MenuItem(M1L0, true)] static bool V00() { Menu.SetChecked(M1L0, Mons == 1 && !Links); return true; }
    [MenuItem(M1L1, true)] static bool V01() { Menu.SetChecked(M1L1, Mons == 1 &&  Links); return true; }
    [MenuItem(M2L0, true)] static bool V10() { Menu.SetChecked(M2L0, Mons == 2 && !Links); return true; }
    [MenuItem(M2L1, true)] static bool V11() { Menu.SetChecked(M2L1, Mons == 2 &&  Links); return true; }

    [MenuItem("실험/조건 창 열기", false, 20)]
    static void OpenWindow() => ConditionWindow.Open();

    [MenuItem("실험/공중의 창 전부 되돌리기", false, 40)]
    static void ClearWindows()
    {
        if (Application.isPlaying && ConditionManager.Instance != null)
            ConditionManager.Instance.ClearAllWindows();
        else
            Debug.Log("[Cond] Play 중에만 됩니다");
    }
}

// 버튼 4개짜리 작은 창. Play 전에도, Play 중에도 쓸 수 있다.
class ConditionWindow : EditorWindow
{
    public static void Open()
    {
        var w = GetWindow<ConditionWindow>(false, "실험 조건", true);
        w.minSize = new Vector2(300, 190);
        w.Show();
    }

    void OnEnable() { EditorApplication.playModeStateChanged += _ => Repaint(); }
    void OnInspectorUpdate() { Repaint(); }

    void OnGUI()
    {
        int mons = ConditionMenu.Mons;
        bool links = ConditionMenu.Links;

        EditorGUILayout.Space(6);
        var title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        EditorGUILayout.LabelField(Application.isPlaying ? "지금 조건" : "다음 실행 조건", title);

        var big = new GUIStyle(EditorStyles.label)
        {
            fontSize = 22,
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold
        };
        EditorGUILayout.LabelField(ConditionMenu.Code, big, GUILayout.Height(30));
        EditorGUILayout.LabelField(
            $"모니터 {mons}대 · 선 {(links ? "있음" : "없음")}",
            new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });

        EditorGUILayout.Space(8);

        // 2×2 — 행 = 모니터 수, 열 = 선 유무
        Row(1, mons, links);
        Row(2, mons, links);

        EditorGUILayout.Space(10);
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            if (GUILayout.Button("공중의 창 전부 데스크탑으로 되돌리기", GUILayout.Height(24)))
                ConditionManager.Instance?.ClearAllWindows();
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(
            Application.isPlaying
                ? "누르면 즉시 바뀌고, 기록 파일도 새로 열립니다 (조건마다 따로 저장)."
                : "여기서 고른 조건으로 Play가 시작됩니다.",
            MessageType.None);
    }

    void Row(int mons, int curMons, bool curLinks)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            Btn(mons, false, curMons, curLinks);
            Btn(mons, true,  curMons, curLinks);
        }
    }

    void Btn(int mons, bool links, int curMons, bool curLinks)
    {
        bool active = mons == curMons && links == curLinks;
        var style = new GUIStyle(GUI.skin.button)
        {
            fontStyle = active ? FontStyle.Bold : FontStyle.Normal,
            alignment = TextAnchor.MiddleCenter
        };
        var prev = GUI.backgroundColor;
        if (active) GUI.backgroundColor = new Color(0.35f, 0.85f, 0.95f);
        string label = $"모니터 {mons}\n선 {(links ? "있음" : "없음")}";
        if (GUILayout.Button(label, style, GUILayout.Height(46)))
            ConditionMenu.Set(mons, links);
        GUI.backgroundColor = prev;
    }
}
#endif
