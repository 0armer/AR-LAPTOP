#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
실험 로그 분석기.

쓰는 법:
    python analyze_study.py StudyLogs/20260911_143000
    python analyze_study.py StudyLogs            # 여러 세션 폴더를 한꺼번에

만들어 주는 것 (입력 폴더 아래 analysis/ 에):
    summary.csv        참가자 × 태스크 한 줄 요약
                       (걸린 시간, 창 뽑은 횟수, 이동 횟수, 총 이동거리, 선 만든/지운 수 …)
    final_layouts.csv  태스크가 끝난 순간 각 창이 어디에 어떤 크기로 있었는지
    events_flat.csv    이벤트 로그를 표로 편 것 (원하는 대로 더 파보기 좋게)
    layout_<참가자>_<태스크>.png   최종 배치 그림 (정면도 + 평면도)

matplotlib이 없으면 그림만 건너뛰고 CSV는 그대로 만든다.
"""

import csv
import json
import os
import sys
from collections import defaultdict


# ── 읽기 ─────────────────────────────────────────────────────────────────────
def load_events(folder):
    path = os.path.join(folder, "events.jsonl")
    if not os.path.exists(path):
        return []
    out = []
    with open(path, encoding="utf-8") as f:
        for n, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                out.append(json.loads(line))
            except json.JSONDecodeError:
                print(f"  ! {os.path.basename(path)}:{n} 파싱 실패 (건너뜀)")
    return out


def load_csv(folder, name):
    path = os.path.join(folder, name)
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def fnum(v, default=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


# ── 요약 만들기 ──────────────────────────────────────────────────────────────
SUMMARY_FIELDS = [
    "participant", "condition", "task", "duration_s",
    "windows_ejected", "windows_inserted", "windows_open_at_end",
    "move_episodes", "resize_episodes", "total_path_m",
    "links_created", "links_removed", "links_flipped",
    "clicks", "scrolls", "pc_resizes", "hand_toggles",
]


def build_summary(events):
    """task_end 이벤트가 이미 요약을 들고 있다. 없으면 직접 세어서 채운다."""
    rows = []
    condition_of = {}
    # 조건은 session_start / condition_set 에만 나온다
    for e in events:
        if e.get("type") in ("session_start", "condition_set"):
            condition_of[e.get("p", "?")] = e.get("condition", "")

    # 태스크별로 직접 센 값 (task_end가 없는 경우 대비 + PC쪽 이벤트 보강)
    counted = defaultdict(lambda: defaultdict(int))
    for e in events:
        key = (e.get("p", "?"), e.get("task", "none"))
        t = e.get("type", "")
        if t == "pc_resize":
            counted[key]["pc_resizes"] += 1
        elif t == "cmd" and e.get("cmd") == "wclick":
            counted[key]["clicks"] += 1
        elif t == "cmd" and e.get("cmd") == "wscroll":
            counted[key]["scrolls"] += 1
        elif t == "hand_toggle":
            counted[key]["hand_toggles"] += 1

    seen = set()
    for e in events:
        if e.get("type") != "task_end":
            continue
        key = (e.get("p", "?"), e.get("task", "none"))
        seen.add(key)
        row = {k: "" for k in SUMMARY_FIELDS}
        row["participant"] = key[0]
        row["task"] = key[1]
        row["condition"] = condition_of.get(key[0], "")
        for k in SUMMARY_FIELDS:
            if k in e:
                row[k] = e[k]
        for k, v in counted[key].items():
            if not row.get(k):
                row[k] = v
        rows.append(row)

    # task_end 없이 끝난(중간에 종료된) 태스크도 흘리지 않는다
    for key, c in counted.items():
        if key in seen or key[1] == "none":
            continue
        row = {k: "" for k in SUMMARY_FIELDS}
        row["participant"], row["task"] = key
        row["condition"] = condition_of.get(key[0], "")
        row.update(c)
        row["duration_s"] = "(task_end 없음)"
        rows.append(row)

    rows.sort(key=lambda r: (str(r["participant"]), str(r["task"])))
    return rows


# ── 최종 배치 ────────────────────────────────────────────────────────────────
LAYOUT_FIELDS = [
    "participant", "task", "label", "window_id", "monitor",
    "pos_x", "pos_y", "pos_z", "rot_x", "rot_y", "rot_z",
    "size_w_m", "size_h_m", "size_w_px", "size_h_px",
    "mon_x", "mon_y", "mon_z", "head_dist", "head_az", "head_el", "path_m",
]


def build_layouts(events):
    rows = []
    links = []
    for e in events:
        if e.get("type") != "layout":
            continue
        p, task, label = e.get("p", "?"), e.get("task", "none"), e.get("label", "")
        for w in e.get("windows", []) or []:
            pos = w.get("pos", [0, 0, 0])
            rot = w.get("rot", [0, 0, 0])
            sm = w.get("size_m", [0, 0])
            spx = w.get("size_px", [0, 0])
            ml = w.get("mon_local", [0, 0, 0])
            rows.append({
                "participant": p, "task": task, "label": label,
                "window_id": w.get("id"), "monitor": w.get("mon"),
                "pos_x": pos[0], "pos_y": pos[1], "pos_z": pos[2],
                "rot_x": rot[0], "rot_y": rot[1], "rot_z": rot[2],
                "size_w_m": sm[0], "size_h_m": sm[1],
                "size_w_px": spx[0], "size_h_px": spx[1],
                "mon_x": ml[0], "mon_y": ml[1], "mon_z": ml[2],
                "head_dist": w.get("head_dist"), "head_az": w.get("head_az"),
                "head_el": w.get("head_el"), "path_m": w.get("path_m"),
            })
        for l in e.get("links", []) or []:
            links.append({"participant": p, "task": task, "label": label,
                          "from": l.get("from"), "to": l.get("to")})
    return rows, links


# ── 이벤트 평탄화 ────────────────────────────────────────────────────────────
def flatten_events(events):
    keys, rows = [], []
    for e in events:
        row = {}
        for k, v in e.items():
            if isinstance(v, (list, dict)):
                if k in ("windows", "links"):
                    row[k + "_n"] = len(v)
                    if k + "_n" not in keys:
                        keys.append(k + "_n")
                    continue
                if isinstance(v, list) and all(isinstance(x, (int, float)) for x in v):
                    for i, axis in enumerate("xyz"[:len(v)]):
                        kk = f"{k}_{axis}"
                        row[kk] = v[i]
                        if kk not in keys:
                            keys.append(kk)
                    continue
                v = json.dumps(v, ensure_ascii=False)
            row[k] = v
            if k not in keys:
                keys.append(k)
        rows.append(row)
    order = [k for k in ("p", "t", "task", "src", "type") if k in keys]
    order += [k for k in keys if k not in order]
    return order, rows


# ── 그림 ─────────────────────────────────────────────────────────────────────
def draw_layouts(layout_rows, out_dir):
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        from matplotlib.patches import Rectangle
    except ImportError:
        print("  (matplotlib 없음 — 그림은 건너뜀. pip install matplotlib)")
        return 0

    groups = defaultdict(list)
    for r in layout_rows:
        if r["label"] not in ("task_end", "manual"):
            continue
        groups[(r["participant"], r["task"], r["label"])].append(r)

    made = 0
    for (p, task, label), rows in groups.items():
        fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(11, 4.6))

        # 정면도: 모니터 기준 좌우(x) × 높이(y)
        for r in rows:
            x, y = fnum(r["mon_x"]), fnum(r["mon_y"])
            w, h = fnum(r["size_w_m"]), fnum(r["size_h_m"])
            ax1.add_patch(Rectangle((x - w / 2, y - h / 2), w, h,
                                    fill=True, alpha=0.28, edgecolor="k", linewidth=1.1))
            ax1.text(x, y, str(r["window_id"])[-4:], ha="center", va="center", fontsize=7)
        ax1.plot(0, 0, "r+", markersize=12)
        ax1.set_title(f"{p} / {task} — front (monitor origin)")
        ax1.set_xlabel("left–right (m)"); ax1.set_ylabel("height (m)")
        ax1.set_aspect("equal"); ax1.grid(alpha=0.25); ax1.autoscale_view()

        # 평면도: 좌우(x) × 깊이(z)
        for r in rows:
            x, z = fnum(r["mon_x"]), fnum(r["mon_z"])
            w = fnum(r["size_w_m"])
            ax2.plot([x - w / 2, x + w / 2], [z, z], linewidth=3, alpha=0.7)
            ax2.text(x, z, str(r["window_id"])[-4:], ha="center", va="bottom", fontsize=7)
        ax2.plot(0, 0, "r+", markersize=12)
        ax2.set_title("top-down")
        ax2.set_xlabel("left–right (m)"); ax2.set_ylabel("depth (m)")
        ax2.set_aspect("equal"); ax2.grid(alpha=0.25); ax2.autoscale_view()

        fig.tight_layout()
        name = f"layout_{p}_{task}_{label}.png".replace("/", "_")
        fig.savefig(os.path.join(out_dir, name), dpi=150)
        plt.close(fig)
        made += 1
    return made


# ── 쓰기 ─────────────────────────────────────────────────────────────────────
def write_csv(path, fields, rows):
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        w.writeheader()
        for r in rows:
            w.writerow(r)


def process(folder):
    print(f"\n■ {folder}")
    events = load_events(folder)
    if not events:
        print("  events.jsonl 이 없거나 비어 있음 — 건너뜀")
        return
    print(f"  이벤트 {len(events)}개")

    out_dir = os.path.join(folder, "analysis")
    os.makedirs(out_dir, exist_ok=True)

    summary = build_summary(events)
    write_csv(os.path.join(out_dir, "summary.csv"), SUMMARY_FIELDS, summary)
    print(f"  summary.csv        — {len(summary)}행 (참가자 × 태스크)")

    layouts, links = build_layouts(events)
    write_csv(os.path.join(out_dir, "final_layouts.csv"), LAYOUT_FIELDS, layouts)
    print(f"  final_layouts.csv  — {len(layouts)}행 (창 배치)")
    if links:
        write_csv(os.path.join(out_dir, "final_links.csv"),
                  ["participant", "task", "label", "from", "to"], links)
        print(f"  final_links.csv    — {len(links)}행 (연결선)")

    fields, flat = flatten_events(events)
    write_csv(os.path.join(out_dir, "events_flat.csv"), fields, flat)
    print(f"  events_flat.csv    — {len(flat)}행")

    snaps = load_csv(folder, "snapshots.csv")
    if snaps:
        print(f"  snapshots.csv      — {len(snaps)}행 (그대로 두고 필요할 때 읽으면 됨)")

    n = draw_layouts(layouts, out_dir)
    if n:
        print(f"  그림 {n}장")

    if summary:
        print("\n  ── 요약 ──")
        head = ["participant", "task", "duration_s", "windows_ejected",
                "move_episodes", "total_path_m", "links_created"]
        print("  " + " | ".join(f"{h:>15}" for h in head))
        for r in summary:
            print("  " + " | ".join(f"{str(r.get(h, '')):>15}" for h in head))


def main():
    args = sys.argv[1:] or ["StudyLogs"]
    for root in args:
        if not os.path.isdir(root):
            print(f"폴더 없음: {root}")
            continue
        if os.path.exists(os.path.join(root, "events.jsonl")):
            process(root)
        else:
            subs = sorted(os.path.join(root, d) for d in os.listdir(root)
                          if os.path.isdir(os.path.join(root, d)))
            if not subs:
                print(f"세션 폴더가 없음: {root}")
            for sub in subs:
                process(sub)


if __name__ == "__main__":
    main()
