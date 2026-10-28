"""Remove faulty participants and rebuild the all-participants (Combined) files.

    python Tools/remove_participant.py --list               # show every participant and whether it is complete
    python Tools/remove_participant.py P003                  # remove P003 (asks you to confirm)
    python Tools/remove_participant.py P003 P004 --yes

What happens, in order:
  1. The newest data is copied from the headset to CollectedData (skipped with --laptop-only).
  2. The participant folder is MOVED to CollectedData/_removed/<id>_<time>, so it can be restored.
  3. The participant is deleted from the headset (skipped with --laptop-only).
  4. Combined/ is rebuilt from the participants that are left (gaze rows and heatmap counts), on the
     laptop and on the headset, exactly the way the app builds it.

Participant numbers are never reused while a later one exists. Removing the newest participant means
the next session gets that number again.
"""
import argparse
import csv
import datetime
import re
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

PROJECT = Path(__file__).resolve().parent.parent
PACKAGE = "com.DefaultCompany.VRTemplate"
REMOTE = f"/sdcard/Android/data/{PACKAGE}/files/ExperimentData"
HEAT_W, HEAT_H = 64, 32


def find_adb():
    found = shutil.which("adb")
    if found:
        return found
    hub = Path("C:/Program Files/Unity/Hub/Editor")
    hits = sorted(hub.glob("*/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe")) if hub.exists() else []
    return str(hits[-1]) if hits else None


def adb(adb_path, *args, check=False):
    return subprocess.run([adb_path, *args], capture_output=True, text=True, encoding="utf-8", errors="replace", check=check)


def headset_connected(adb_path):
    if not adb_path:
        return False
    lines = adb(adb_path, "devices").stdout.splitlines()[1:]
    return any(line.split()[-1:] == ["device"] for line in lines if line.strip())


def participant_folders(root):
    return sorted((d for d in root.iterdir() if d.is_dir() and re.fullmatch(r"P\d+", d.name)),
                  key=lambda d: int(d.name[1:]))


def describe(folder):
    pid = folder.name
    has_gaze, has_answers = (folder / f"{pid}_gaze.csv").exists(), (folder / f"{pid}_responses.csv").exists()
    status = "complete" if has_gaze and has_answers else "INCOMPLETE (stopped early)"
    trials = ""
    if has_answers:
        with open(folder / f"{pid}_responses.csv", newline="", encoding="utf-8-sig") as f:
            trials = f"{len({row['trial_index'] for row in csv.DictReader(f)})} images answered"
    started = ""
    meta = folder / f"{pid}_metadata.json"
    if meta.exists():
        m = re.search(r'"utc_started":\s*"([^"]+)"', meta.read_text(encoding="utf-8-sig"))
        started = m.group(1)[:16].replace("T", " ") + " UTC" if m else ""
    return f"  {pid:5s} {status:28s} {trials:22s} {started}"


def heat_png(counts, path):
    """Same picture the app draws (256x128, transparent -> red), including Unity's bottom-up row order."""
    grid = counts.reshape(HEAT_H, HEAT_W).astype(float)
    peak = max(grid.max(), 1.0)
    rows = np.arange(128)[:, None]; cols = np.arange(256)[None, :]
    intensity = grid[((127 - rows) * HEAT_H) // 128, (cols * HEAT_W) // 256] / peak
    rgba = np.zeros((128, 256, 4), dtype=np.uint8)
    rgba[..., 0] = np.round(255 * intensity); rgba[..., 1] = np.round(255 * 0.05 * intensity)
    rgba[..., 3] = np.round(255 * 0.9 * intensity)
    Image.fromarray(rgba, "RGBA").save(path)


def rebuild_combined(root):
    combined = root / "Combined"
    if combined.exists():
        shutil.rmtree(combined)
    combined.mkdir()
    folders = [d for d in participant_folders(root) if (d / f"{d.name}_gaze.csv").exists()]
    if not folders:
        print("  no complete participants left; Combined is empty")
        return folders

    out, header = [], None
    for d in folders:
        text = (d / f"{d.name}_gaze.csv").read_text(encoding="utf-8-sig")
        head, _, rows = text.partition("\n")
        if header is None:
            header = head
        elif head.rstrip("\r") != header.rstrip("\r"):
            print(f"  WARNING: {d.name} has a different gaze column layout; its rows are still appended")
        out.append(rows)
    (combined / "combined_gaze.csv").write_bytes(("\ufeff" + header + "\n" + "".join(out)).encode("utf-8"))

    images = sorted({int(m.group(1)) for d in folders for f in d.glob(f"{d.name}_image_*_heatmap.csv")
                     if (m := re.search(r"_image_(\d+)_heatmap\.csv$", f.name))})
    for n in images:
        total = np.zeros(HEAT_W * HEAT_H, dtype=np.int64)
        for d in folders:
            f = d / f"{d.name}_image_{n}_heatmap.csv"
            if f.exists():
                with open(f, newline="", encoding="utf-8-sig") as fh:
                    for row in csv.DictReader(fh):
                        total[int(row["v_index"]) * HEAT_W + int(row["u_index"])] += int(row["count"])
        lines = ["u_index,v_index,count"] + [f"{x},{y},{total[y * HEAT_W + x]}" for y in range(HEAT_H) for x in range(HEAT_W)]
        (combined / f"combined_image_{n}_heatmap.csv").write_bytes(("\ufeff" + "\n".join(lines) + "\n").encode("utf-8"))
        heat_png(total, combined / f"combined_image_{n}_heatmap.png")
    print(f"  Combined rebuilt from {len(folders)} participant(s): {', '.join(d.name for d in folders)}")
    return folders


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("participants", nargs="*", help="e.g. P003 P004")
    parser.add_argument("--list", action="store_true", help="only list participants")
    parser.add_argument("--laptop-only", action="store_true", help="do not touch the headset")
    parser.add_argument("--yes", action="store_true", help="do not ask for confirmation")
    parser.add_argument("--data-root", type=Path, default=PROJECT / "CollectedData" / "ExperimentData")
    args = parser.parse_args()

    adb_path = None if args.laptop_only else find_adb()
    on_headset = not args.laptop_only and headset_connected(adb_path)
    if not args.laptop_only and not on_headset:
        print("The headset is not connected, so only the laptop copy can be changed.")
        print("Plug it in (and press Allow in the headset) to remove the participant from the headset too.")
        if args.participants and not args.yes and input("Continue with the laptop copy only? (yes/no): ").strip().lower() != "yes":
            return 1

    root = args.data_root
    if on_headset:
        print("Copying the newest data from the headset...")
        root.parent.mkdir(parents=True, exist_ok=True)
        adb(adb_path, "pull", REMOTE, str(root.parent))
    if not root.exists():
        print(f"No data folder at {root}.")
        return 1

    existing = participant_folders(root)
    print("\nParticipants on the laptop:")
    for d in existing:
        print(describe(d))
    if args.list or not args.participants:
        return 0

    wanted = [p.upper() for p in args.participants]
    missing = [p for p in wanted if p not in {d.name for d in existing}]
    if missing:
        print(f"\nNot found: {', '.join(missing)}. Nothing was changed.")
        return 1

    print(f"\nAbout to REMOVE: {', '.join(wanted)}" + ("" if on_headset else "  (laptop copy only)"))
    print("They are moved to CollectedData\\_removed, so they can be restored. Combined files are rebuilt.")
    if not args.yes and input("Type YES to continue: ").strip() != "YES":
        print("Cancelled. Nothing was changed.")
        return 1

    stamp = datetime.datetime.now().strftime("%Y-%m-%d_%H%M")
    trash = root.parent / "_removed"
    trash.mkdir(parents=True, exist_ok=True)
    for pid in wanted:
        shutil.move(str(root / pid), str(trash / f"{pid}_{stamp}"))
        print(f"  moved {pid} -> {trash / (pid + '_' + stamp)}")
        if on_headset:
            adb(adb_path, "shell", "rm", "-rf", f"{REMOTE}/{pid}")
            print(f"  deleted {pid} from the headset")

    kept = rebuild_combined(root)
    if on_headset:
        adb(adb_path, "shell", "rm", "-rf", f"{REMOTE}/Combined")
        adb(adb_path, "shell", "mkdir", "-p", f"{REMOTE}/Combined")
        if (root / "Combined").exists() and any((root / "Combined").iterdir()):
            adb(adb_path, "push", str(root / "Combined") + "/.", f"{REMOTE}/Combined")
        print("  headset Combined replaced")

    highest = max((int(d.name[1:]) for d in participant_folders(root)), default=0)
    print(f"\nDone. The next session will be P{highest + 1:03d}.")
    print("To restore a removed participant, move its folder out of CollectedData\\_removed and back into ExperimentData.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
