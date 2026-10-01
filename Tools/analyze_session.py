"""Create a synchronized gaze/event/physiology table and basic RR-derived summary.

Usage: python Tools/analyze_session.py <Unity session directory>
Only physiologically plausible RR intervals (300--2000 ms) are used for RMSSD/SDNN.
"""
import bisect
import csv
import json
import math
import sys
from collections import defaultdict
from pathlib import Path

def rows(path):
    with path.open(newline="", encoding="utf-8-sig") as f: return list(csv.DictReader(f))
def number(value):
    try: return float(value) if value not in (None, "") else None
    except ValueError: return None
def nearest_before(samples, timestamps, stamp):
    if stamp is None: return None
    i = bisect.bisect_right(timestamps, stamp) - 1
    return samples[i] if i >= 0 else None

def find(folder, *patterns):
    for pattern in patterns:
        exact = folder / pattern
        if exact.exists(): return exact
        hits = sorted(folder.glob(pattern))
        if hits: return hits[0]
    raise SystemExit(f"No file matching {patterns[0]} in {folder}")

def main(folder):
    folder = Path(folder); physiology = rows(find(folder, "*_heartrate.csv", "physiology.csv"))
    phys = [(number(r["timestamp_lsl"]), r) for r in physiology if number(r["timestamp_lsl"]) is not None]
    phys.sort(key=lambda p: p[0]); times = [p[0] for p in phys]
    output, gaze = [], rows(find(folder, "*_gaze_lsl.csv", "gaze.csv"))
    for sample in gaze:
        stamp = number(sample["timestamp_lsl"]); prev = nearest_before(phys, times, stamp)
        row = {"timestamp_lsl": sample["timestamp_lsl"], "trial_id": sample["trial_id"], "gaze_object": sample["hit_object"],
               "gaze_valid": sample["gaze_valid"], "heart_rate_bpm": prev[1]["heart_rate_bpm"] if prev else "",
               "rr_interval_ms": prev[1]["rr_interval_ms"] if prev else "",
               "phase": prev[1].get("phase", "") if prev else "", "event": "", "event_detail": ""}
        output.append(row)
    events = rows(find(folder, "*_events.csv", "events.csv"))
    for event in events:
        output.append({"timestamp_lsl": event["timestamp_lsl"], "trial_id": event["trial_id"], "gaze_object": "", "gaze_valid": "",
                       "heart_rate_bpm": "", "rr_interval_ms": "", "phase": "",
                       "event": event["event_type"], "event_detail": event.get("metadata", "")})
    for stamp, sample in phys:
        output.append({"timestamp_lsl": sample["timestamp_lsl"], "trial_id": sample["trial_id"], "gaze_object": "", "gaze_valid": "",
                       "heart_rate_bpm": sample["heart_rate_bpm"], "rr_interval_ms": sample["rr_interval_ms"],
                       "phase": sample.get("phase", ""), "event": "", "event_detail": ""})
    output.sort(key=lambda r: number(r["timestamp_lsl"]) if number(r["timestamp_lsl"]) is not None else float("inf"))
    with (folder / "synchronized.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=output[0].keys() if output else ["timestamp_lsl"]); writer.writeheader(); writer.writerows(output)
    rr = [number(r["rr_interval_ms"]) for _, r in phys]; rr = [x for x in rr if x is not None and 300 <= x <= 2000]
    diffs = [rr[i] - rr[i - 1] for i in range(1, len(rr))]
    summary = {"rr_samples_valid": len(rr), "mean_rr_ms": sum(rr) / len(rr) if rr else None,
               "rmssd_ms": math.sqrt(sum(d*d for d in diffs) / len(diffs)) if diffs else None,
               "sdnn_ms": math.sqrt(sum((x - sum(rr)/len(rr))**2 for x in rr) / (len(rr)-1)) if len(rr) > 1 else None}
    with (folder / "analysis_summary.json").open("w", encoding="utf-8") as f: json.dump(summary, f, indent=2)
    print("Wrote synchronized.csv and analysis_summary.json", summary)
if __name__ == "__main__": main(sys.argv[1])
