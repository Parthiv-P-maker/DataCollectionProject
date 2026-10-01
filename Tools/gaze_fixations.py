"""Detect gaze fixations from the saved eye-tracking data (no app changes needed).

    python Tools/gaze_fixations.py <ExperimentData folder>
    python Tools/gaze_fixations.py <ExperimentData folder> --participant P001

Uses dispersion-threshold identification (I-DT) on the gaze direction vectors in P###_gaze.csv.
Only real eye samples (gaze_valid = 1) are used. Writes, into each participant folder:
    P001_fixations.csv          one row per fixation (start, duration, position on the panorama)
    P001_fixation_summary.csv   one row per image (valid-sample %, fixation count, mean duration, ...)
    P001_image_N_fixations.png  fixations drawn on the panorama (circle size = duration, numbers = order)

The app samples gaze about 20 times per second, so fixation start/end times are accurate to ~50 ms.
"""
import argparse
import csv
import math
import re
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image

from make_visuals import PROJECT, label, panorama_figure, read_csv, trial_images


def num(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def to_uv(direction, flip_u):
    x, y, z = direction
    u = (0.5 + math.atan2(x, z) / (2 * math.pi)) % 1.0
    v = min(max(0.5 - math.asin(max(-1.0, min(1.0, y))) / math.pi, 0.0), 1.0)
    return (1.0 - u if flip_u else u), v


def dispersion_deg(vectors):
    centre = vectors.mean(axis=0)
    centre /= np.linalg.norm(centre)
    cos = np.clip(vectors @ centre, -1.0, 1.0)
    return math.degrees(math.acos(cos.min())), centre


def detect(times, vectors, max_dispersion, min_duration, max_gap):
    """I-DT: grow a window that lasts at least min_duration while all samples stay within max_dispersion
    degrees of the window centre; a gap longer than max_gap ends the window."""
    fixations, i, n = [], 0, len(times)
    while i < n:
        j = i
        while j + 1 < n and times[j + 1] - times[j] <= max_gap and times[j] - times[i] < min_duration:
            j += 1
        if times[j] - times[i] < min_duration:
            i = j + 1
            continue
        spread, centre = dispersion_deg(vectors[i:j + 1])
        if spread > max_dispersion:
            i += 1
            continue
        while j + 1 < n and times[j + 1] - times[j] <= max_gap:
            wider, wider_centre = dispersion_deg(vectors[i:j + 2])
            if wider > max_dispersion:
                break
            j, spread, centre = j + 1, wider, wider_centre
        fixations.append((i, j, spread, centre))
        i = j + 1
    return merge_adjacent(fixations, times, vectors, max_dispersion, max_gap)


def merge_adjacent(fixations, times, vectors, max_dispersion, max_gap):
    """Join back-to-back fixations on the same spot (a split caused by one noisy sample)."""
    merged = []
    for fix in fixations:
        if merged:
            pi, pj, _, pcentre = merged[-1]
            i, j, _, centre = fix
            close = math.degrees(math.acos(min(1.0, float(pcentre @ centre)))) <= max_dispersion
            if close and times[i] - times[pj] <= max_gap:
                spread, joined = dispersion_deg(vectors[pi:j + 1])
                merged[-1] = (pi, j, spread, joined)
                continue
        merged.append(fix)
    return merged


def save_fixation_image(img, rows, title, path):
    fig, ax = panorama_figure(img)
    us = [r["center_u"] for r in rows]; vs = [r["center_v"] for r in rows]
    for k in range(1, len(rows)):
        if abs(us[k] - us[k - 1]) <= 0.5:   # do not draw saccade lines across the image seam
            ax.plot(us[k - 1:k + 1], vs[k - 1:k + 1], "-", color="white", lw=1.2, alpha=0.8)
    sizes = [60 + 900 * r["duration_s"] for r in rows]
    ax.scatter(us, vs, s=sizes, color="#ffcc00", alpha=0.55, edgecolor="black", linewidth=0.8, zorder=3)
    for k, r in enumerate(rows, 1):
        ax.text(r["center_u"], r["center_v"], str(k), ha="center", va="center", fontsize=8, weight="bold", zorder=4)
    label(ax, f"{title}  ({len(rows)} fixations; circle size = duration, number = order)")
    fig.savefig(path, dpi=130)
    plt.close(fig)


def process(folder, trials, images, args):
    pid = folder.name
    gaze_path = folder / f"{pid}_gaze.csv"
    if not gaze_path.exists():
        print(f"  {pid}: no {pid}_gaze.csv (session not finished), skipping")
        return
    rows = read_csv(gaze_path)
    all_fix, summary = [], []
    for index, (name, _) in enumerate(trials, 1):
        trial_rows = [r for r in rows if r["trial_index"] == str(index)]
        if not trial_rows:
            continue
        valid = [r for r in trial_rows if r.get("gaze_valid") == "1"]
        times = np.array([num(r["trial_time_seconds"]) for r in valid], dtype=float)
        vectors = np.array([[num(r["direction_x"]), num(r["direction_y"]), num(r["direction_z"])] for r in valid], dtype=float)
        trial_fix = []
        if len(valid) >= 2:
            order = np.argsort(times); times, vectors = times[order], vectors[order]
            vectors /= np.linalg.norm(vectors, axis=1, keepdims=True)
            step = float(np.median(np.diff(times))) if len(times) > 1 else 0.05
            for k, (i, j, spread, centre) in enumerate(detect(times, vectors, args.dispersion_deg, args.min_duration,
                                                               max(2.5 * step, 0.15)), 1):
                u, v = to_uv(centre, args.flip_u)
                trial_fix.append({"trial_index": index, "image_name": trial_rows[0]["image_name"], "fixation_index": k,
                                  "start_s": round(times[i], 3), "end_s": round(times[j] + step, 3),
                                  "duration_s": round(times[j] - times[i] + step, 3), "samples": j - i + 1,
                                  "center_u": round(u, 4), "center_v": round(v, 4), "dispersion_deg": round(spread, 3)})
        all_fix.extend(trial_fix)
        span = (max(num(r["trial_time_seconds"]) for r in trial_rows) or 0) + 0.05
        durations = [f["duration_s"] for f in trial_fix]
        summary.append({"trial_index": index, "image_name": trial_rows[0]["image_name"],
                        "samples": len(trial_rows), "valid_eye_samples": len(valid),
                        "valid_percent": round(100.0 * len(valid) / len(trial_rows), 1),
                        "fixation_count": len(trial_fix),
                        "mean_fixation_duration_s": round(sum(durations) / len(durations), 3) if durations else "",
                        "total_fixation_time_s": round(sum(durations), 3),
                        "fixations_per_second": round(len(trial_fix) / span, 3) if span > 0 else ""})
        if trial_fix and index in images:
            save_fixation_image(images[index], trial_fix, f"{pid} - {name}", folder / f"{pid}_image_{index}_fixations.png")

    for path, data, fields in ((folder / f"{pid}_fixations.csv", all_fix,
                                ["trial_index", "image_name", "fixation_index", "start_s", "end_s", "duration_s",
                                 "samples", "center_u", "center_v", "dispersion_deg"]),
                               (folder / f"{pid}_fixation_summary.csv", summary,
                                ["trial_index", "image_name", "samples", "valid_eye_samples", "valid_percent",
                                 "fixation_count", "mean_fixation_duration_s", "total_fixation_time_s",
                                 "fixations_per_second"])):
        with open(path, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=fields); writer.writeheader(); writer.writerows(data)
    print(f"  {pid}: {len(all_fix)} fixations across {len(summary)} images")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("data_root", type=Path)
    parser.add_argument("--participant", help="only this participant, e.g. P001")
    parser.add_argument("--dispersion-deg", type=float, default=1.5,
                        help="max angle (degrees) of any sample from the fixation centre (default 1.5)")
    parser.add_argument("--min-duration", type=float, default=0.1, help="minimum fixation length in seconds (default 0.1)")
    parser.add_argument("--flip-u", action="store_true", help="mirror left/right, same as make_visuals.py")
    parser.add_argument("--scene", type=Path, default=PROJECT / "Assets" / "Scenes" / "BasicScene.unity")
    args = parser.parse_args()

    trials = trial_images(args.scene, PROJECT / "Assets")
    images = {i: np.asarray(Image.open(p).convert("RGB")) for i, (_, p) in enumerate(trials, 1) if p and p.exists()}
    folders = sorted(d for d in args.data_root.iterdir()
                     if d.is_dir() and re.fullmatch(r"P\d+", d.name) and (not args.participant or d.name == args.participant))
    for folder in folders:
        process(folder, trials, images, args)


if __name__ == "__main__":
    main()
