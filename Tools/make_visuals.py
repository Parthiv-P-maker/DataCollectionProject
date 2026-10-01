"""Make gaze heatmap overlays, scan paths and heart-rate plots from saved experiment data.

    python Tools/make_visuals.py <ExperimentData folder>          # every participant + combined
    python Tools/make_visuals.py <ExperimentData folder> --participant P007

Per participant (written into that participant's folder):
    P007_image_N_overlay.png      heatmap drawn on the panorama
    P007_image_N_scanpath.png     gaze path drawn on the panorama
    P007_heartrate_trial_N.png    heart rate with image start/end markers
    P007_heartrate_all_trials.png the three trials stacked
Combined (written into <ExperimentData>/Combined):
    combined_image_N_overlay.png  all participants' gaze on the panorama

The panorama for each trial is read from Assets/Scenes/BasicScene.unity. Existing files are never touched.
If an overlay looks mirrored left/right, rerun with --flip-u.
"""
import argparse
import csv
import re
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter

PROJECT = Path(__file__).resolve().parent.parent


def num(value):
    try:
        return float(value) if value not in (None, "") else None
    except ValueError:
        return None


def read_csv(path):
    with open(path, newline="", encoding="utf-8-sig") as f:
        return list(csv.DictReader(f))


def trial_images(scene, assets):
    """[(trial_name, image_path)] in trial order, resolved from the scene's GUIDs."""
    guid_to_path = {}
    for meta in assets.rglob("*.meta"):
        match = re.search(r"^guid: (\w+)", meta.read_text(encoding="utf-8", errors="ignore"), re.M)
        if match:
            guid_to_path[match.group(1)] = meta.with_suffix("")
    text = scene.read_text(encoding="utf-8", errors="ignore")
    found = re.findall(r"trialName: (.+?)\r?\n\s+image: \{fileID: \d+, guid: (\w+)", text)
    return [(name.strip(), guid_to_path.get(guid)) for name, guid in found]


def gaze_points(rows, trial_index, flip_u):
    pts = [(num(r["pano_u"]), num(r["pano_v"]), r.get("gaze_valid", "0") == "1")
           for r in rows if r["trial_index"] == str(trial_index)]
    pts = [(u, v, ok) for u, v, ok in pts if u is not None and v is not None]
    if flip_u:
        pts = [(1.0 - u, v, ok) for u, v, ok in pts]
    return pts


def source_note(pts):
    if not pts:
        return ""
    eye = sum(1 for p in pts if p[2])
    return "" if eye > len(pts) / 2 else "head direction (eye tracking not available)"


def panorama_figure(img):
    h, w = img.shape[:2]
    fig = plt.figure(figsize=(12, 12 * h / w))
    ax = fig.add_axes([0, 0, 1, 1])
    ax.imshow(img, extent=(0, 1, 1, 0), aspect="auto")
    ax.set_axis_off()
    return fig, ax


def label(ax, text):
    ax.text(0.01, 0.97, text, transform=ax.transAxes, va="top", color="white", fontsize=11,
            bbox=dict(facecolor="black", alpha=0.55, pad=3, edgecolor="none"))


def heat_grid(pts, shape):
    h, w = shape[:2]
    grid = np.zeros((h // 4, w // 4))
    for u, v, _ in pts:
        grid[min(int(v * grid.shape[0]), grid.shape[0] - 1), min(int(u * grid.shape[1]), grid.shape[1] - 1)] += 1
    # wrap horizontally: the panorama is a full 360 degrees
    sigma = grid.shape[1] * 0.02
    grid = gaussian_filter(grid, sigma=(sigma, sigma), mode=("nearest", "wrap"))
    return grid / grid.max() if grid.max() > 0 else grid


def save_heatmap_overlay(img, pts, title, path):
    fig, ax = panorama_figure(img)
    grid = heat_grid(pts, img.shape)
    rgba = plt.get_cmap("jet")(grid)
    rgba[..., 3] = np.clip(grid * 1.6, 0, 0.85) * (grid > 0.04)
    ax.imshow(rgba, extent=(0, 1, 1, 0), aspect="auto", interpolation="bilinear")
    label(ax, f"{title}  ({len(pts)} samples)" + (f" - {source_note(pts)}" if source_note(pts) else ""))
    fig.savefig(path, dpi=130)
    plt.close(fig)


def save_scanpath(img, pts, title, path):
    fig, ax = panorama_figure(img)
    xs = np.array([p[0] for p in pts]); ys = np.array([p[1] for p in pts])
    start = 0
    for i in range(1, len(xs) + 1):
        if i == len(xs) or abs(xs[i] - xs[i - 1]) > 0.5:   # do not draw a line across the image seam
            ax.plot(xs[start:i], ys[start:i], "-", color="#7ed321", lw=2.2, alpha=0.9)
            start = i
    ax.scatter(xs, ys, s=14, color="#7ed321", edgecolor="white", linewidth=0.4, zorder=3)
    if len(xs):
        ax.scatter(xs[0], ys[0], s=120, marker="o", color="white", edgecolor="black", zorder=4)
        ax.scatter(xs[-1], ys[-1], s=120, marker="s", color="white", edgecolor="black", zorder=4)
    label(ax, f"{title}  (circle = start, square = end)" + (f" - {source_note(pts)}" if source_note(pts) else ""))
    fig.savefig(path, dpi=130)
    plt.close(fig)


def trial_timeline(events, trial):
    """Marker times for one trial, using LSL time when every marker has it, otherwise local monotonic time."""
    rows = [r for r in events if r["trial_id"] == str(trial)]
    pick = {}
    for r in rows:
        kind, meta = r["event_type"], r["metadata"]
        if kind == "FixationShown" and meta == "before_image": pick["start"] = r
        elif kind == "StimulusShown": pick["image_start"] = r
        elif kind == "StimulusHidden": pick["image_end"] = r
        elif kind == "FixationHidden" and meta == "after_image": pick["end"] = r
    if len(pick) < 4:
        return None
    use_lsl = all(num(pick[k]["timestamp_lsl"]) is not None for k in pick)
    col = "timestamp_lsl" if use_lsl else "local_monotonic_s"
    return use_lsl, {k: num(v[col]) for k, v in pick.items()}


def heart_series(hr_rows, trial, use_lsl):
    col = "timestamp_lsl" if use_lsl else "received_local_monotonic_s"
    pts = [(num(r[col]), num(r["heart_rate_bpm"])) for r in hr_rows
           if r["trial_id"] == str(trial) and num(r["heart_rate_bpm"]) is not None]
    return sorted(p for p in pts if p[0] is not None)


def draw_heart(ax, series, marks, title):
    t0 = marks["start"]
    t = lambda x: x - t0
    ax.axvspan(0, t(marks["image_start"]), color="#999999", alpha=0.18, label="fixation cross (before)")
    ax.axvspan(t(marks["image_start"]), t(marks["image_end"]), color="#4aa3ff", alpha=0.18, label="image")
    ax.axvspan(t(marks["image_end"]), t(marks["end"]), color="#999999", alpha=0.18, label="fixation cross (after)")
    ax.axvline(t(marks["image_start"]), color="#d62728", ls="--", lw=1.6, label="image starts")
    ax.axvline(t(marks["image_end"]), color="#2ca02c", ls="--", lw=1.6, label="image ends")
    if series:
        ax.plot([t(s[0]) for s in series], [s[1] for s in series], "-o", color="#1f77b4", lw=1.8, ms=5)
        lo, hi = min(s[1] for s in series), max(s[1] for s in series)
        ax.set_ylim(lo - 3, hi + 3)
    else:
        ax.text(0.5, 0.5, "no heart-rate samples", transform=ax.transAxes, ha="center")
    ax.set_xlim(0, t(marks["end"]))
    ax.set_ylabel("Heart rate (BPM)")
    ax.set_title(title)
    ax.grid(alpha=0.3)


def heart_plots(folder, pid, trials):
    hr_path, ev_path = next(folder.glob("*_heartrate.csv"), None), next(folder.glob("*_events.csv"), None)
    if not hr_path or not ev_path:
        print(f"  {pid}: no heart-rate/events file, skipping heart plots"); return
    hr_rows, events = read_csv(hr_path), read_csv(ev_path)
    made = []
    for index, (name, _) in enumerate(trials, 1):
        timeline = trial_timeline(events, index)
        if not timeline:
            continue
        use_lsl, marks = timeline
        series = heart_series(hr_rows, index, use_lsl)
        fig, ax = plt.subplots(figsize=(11, 5))
        draw_heart(ax, series, marks, f"{pid} - {name} - Polar H10 heart rate")
        ax.set_xlabel("Seconds since the first fixation cross appeared")
        handles, labels = ax.get_legend_handles_labels()
        fig.legend(handles, labels, loc="lower center", fontsize=8, ncol=5, frameon=False)
        fig.tight_layout(rect=(0, 0.06, 1, 1)); fig.savefig(folder / f"{pid}_heartrate_trial_{index}.png", dpi=140); plt.close(fig)
        made.append((index, name, series, marks))
    if made:
        fig, axes = plt.subplots(len(made), 1, figsize=(11, 3.6 * len(made)), squeeze=False)
        for ax, (index, name, series, marks) in zip(axes[:, 0], made):
            draw_heart(ax, series, marks, f"{name}")
        axes[-1, 0].set_xlabel("Seconds since the first fixation cross appeared")
        handles, labels = axes[0, 0].get_legend_handles_labels()
        fig.legend(handles, labels, loc="lower center", fontsize=8, ncol=5, frameon=False)
        fig.suptitle(f"{pid} - Polar H10 heart rate")
        fig.tight_layout(rect=(0, 0.04, 1, 0.97)); fig.savefig(folder / f"{pid}_heartrate_all_trials.png", dpi=140); plt.close(fig)
    print(f"  {pid}: {len(made)} heart-rate plot(s)")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("data_root", type=Path)
    parser.add_argument("--participant", help="only this participant, e.g. P007")
    parser.add_argument("--flip-u", action="store_true", help="mirror gaze left/right if overlays look reversed")
    parser.add_argument("--scene", type=Path, default=PROJECT / "Assets" / "Scenes" / "BasicScene.unity")
    args = parser.parse_args()

    trials = trial_images(args.scene, PROJECT / "Assets")
    if not trials:
        raise SystemExit("No trials found in the scene.")
    images = {}
    for i, (name, path) in enumerate(trials, 1):
        print(f"Trial {i}: {name} -> {path.name if path else 'MISSING IMAGE'}")
        if path and path.exists():
            images[i] = np.asarray(Image.open(path).convert("RGB"))

    folders = sorted(d for d in args.data_root.iterdir()
                     if d.is_dir() and re.fullmatch(r"P\d+", d.name) and (not args.participant or d.name == args.participant))
    combined = {i: [] for i in images}
    for folder in folders:
        pid = folder.name
        gaze_path = folder / f"{pid}_gaze.csv"
        print(pid)
        if gaze_path.exists():
            rows = read_csv(gaze_path)
            for i, img in images.items():
                pts = gaze_points(rows, i, args.flip_u)
                combined[i].extend(pts)
                if pts:
                    title = f"{pid} - {trials[i - 1][0]}"
                    save_heatmap_overlay(img, pts, title, folder / f"{pid}_image_{i}_overlay.png")
                    save_scanpath(img, pts, title, folder / f"{pid}_image_{i}_scanpath.png")
            print(f"  {pid}: gaze overlays done")
        else:
            print(f"  {pid}: no {pid}_gaze.csv (session not finished), skipping gaze overlays")
        heart_plots(folder, pid, trials)

    if not args.participant:
        out = args.data_root / "Combined"
        out.mkdir(exist_ok=True)
        for i, img in images.items():
            if combined[i]:
                save_heatmap_overlay(img, combined[i], f"All participants - {trials[i - 1][0]}",
                                     out / f"combined_image_{i}_overlay.png")
        print(f"Combined overlays written to {out}")


if __name__ == "__main__":
    main()
