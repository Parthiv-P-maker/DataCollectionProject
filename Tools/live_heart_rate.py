"""Live heart-rate window for the experimenter (laptop only).

    python Tools/live_heart_rate.py

Reads the "HeartRate" and "RRinterval" LSL streams published by polar_h10_lsl.py and, when the bridge is
running, the "StudyMarkers" stream with the headset's events. Shows the last two minutes, the current value,
image periods (blue band, red line = image starts, green line = image ends) and what the participant is doing.
Nothing is saved; the headset and the bridge record the real data.
"""
import collections
import json

import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation
from pylsl import StreamInlet, local_clock, resolve_byprop

WINDOW_S = 120
STALE_S = 5

PHASES = {
    "ExperimentStart": "Session started",
    "FixationShown": "Cross on screen",
    "StimulusShown": "IMAGE on screen",
    "StimulusHidden": "Cross on screen",
    "UserInteraction": "Answering the question",
    "TrialEnd": "Question answered",
    "ExperimentComplete": "Session finished",
    "ExperimentEnd": "Session finished",
}


class LiveHeartRate:
    def __init__(self):
        self.hr = collections.deque()
        self.markers = collections.deque(maxlen=600)
        self.last_rr = None
        self.hr_inlet = self.rr_inlet = self.marker_inlet = None
        self.next_resolve = 0.0
        self.marker_artists = []
        self.fig, self.ax = plt.subplots(figsize=(10, 5))
        self.fig.canvas.manager.set_window_title("Live heart rate - Polar H10")
        (self.line,) = self.ax.plot([], [], "-o", color="#1f77b4", lw=1.8, ms=4, zorder=3)
        self.big = self.fig.text(0.98, 0.95, "", ha="right", va="top", fontsize=26, weight="bold")
        self.status = self.fig.text(0.02, 0.95, "", ha="left", va="top", fontsize=12)
        self.phase = self.fig.text(0.02, 0.89, "", ha="left", va="top", fontsize=12, color="#333333")
        self.ax.set_xlim(-WINDOW_S, 0)
        self.ax.set_xlabel("Seconds ago")
        self.ax.set_ylabel("Heart rate (BPM)")
        self.ax.grid(alpha=0.3)
        self.fig.subplots_adjust(top=0.8)

    def connect(self):
        if (self.hr_inlet and self.rr_inlet and self.marker_inlet) or local_clock() < self.next_resolve:
            return
        self.next_resolve = local_clock() + 3
        for attr, name in (("hr_inlet", "HeartRate"), ("rr_inlet", "RRinterval"), ("marker_inlet", "StudyMarkers")):
            if not getattr(self, attr):
                found = resolve_byprop("name", name, timeout=0.3)
                if found:
                    setattr(self, attr, StreamInlet(found[0], max_buflen=WINDOW_S))

    def pull(self):
        if self.hr_inlet:
            samples, stamps = self.hr_inlet.pull_chunk(timeout=0.0)
            for sample, stamp in zip(samples, stamps):
                self.hr.append((stamp, float(sample[0])))
        if self.rr_inlet:
            samples, _ = self.rr_inlet.pull_chunk(timeout=0.0)
            if samples:
                self.last_rr = float(samples[-1][0])
        if self.marker_inlet:
            samples, stamps = self.marker_inlet.pull_chunk(timeout=0.0)
            for sample, stamp in zip(samples, stamps):
                try:
                    self.markers.append((stamp, json.loads(sample[0])))
                except (ValueError, TypeError):
                    pass
        now = local_clock()
        while self.hr and now - self.hr[0][0] > WINDOW_S:
            self.hr.popleft()

    def draw_markers(self, now):
        for artist in self.marker_artists:
            artist.remove()
        self.marker_artists = []
        image_start = None
        name = ""
        for stamp, m in self.markers:
            kind = m.get("marker")
            if kind == "StimulusShown":
                image_start, name = stamp, m.get("object_id") or f"image {m.get('trial', '')}"
            elif kind == "StimulusHidden" and image_start is not None:
                self.add_image_band(image_start, stamp, name, now)
                image_start = None
        if image_start is not None:
            self.add_image_band(image_start, now, name, now, open_ended=True)

    def add_image_band(self, start, end, name, now, open_ended=False):
        x0, x1 = start - now, end - now
        if x1 < -WINDOW_S:
            return
        a = self.ax
        self.marker_artists.append(a.axvspan(max(x0, -WINDOW_S), x1, color="#4aa3ff", alpha=0.15, zorder=1))
        if x0 >= -WINDOW_S:
            self.marker_artists.append(a.axvline(x0, color="#d62728", ls="--", lw=1.4, zorder=2))
            self.marker_artists.append(a.text(x0, 1.01, name, transform=a.get_xaxis_transform(), fontsize=8,
                                              rotation=45, ha="left", va="bottom", color="#d62728"))
        if not open_ended:
            self.marker_artists.append(a.axvline(x1, color="#2ca02c", ls="--", lw=1.4, zorder=2))

    def update(self, _frame=None):
        self.connect()
        self.pull()
        now = local_clock()
        xs = [t - now for t, _ in self.hr]
        ys = [v for _, v in self.hr]
        self.line.set_data(xs, ys)
        good = [v for v in ys if v >= 30]
        self.ax.set_ylim((min(good) - 5, max(good) + 5) if good else (40, 120))
        self.draw_markers(now)

        if not self.hr_inlet:
            text, colour, value = "Waiting for the strap... is the 'Study - Polar H10' window running?", "#b00020", "--"
        elif not self.hr or now - self.hr[-1][0] > STALE_S:
            text, colour, value = "NO DATA from the strap", "#b00020", "--"
        elif self.hr[-1][1] < 30:
            text, colour, value = "NO SKIN CONTACT - wet the strap and tighten it", "#b00020", "0"
        else:
            rr = f"   last beat interval {self.last_rr:.0f} ms" if self.last_rr else ""
            text, colour, value = "Strap OK" + rr, "#0a7d2c", f"{self.hr[-1][1]:.0f} bpm"
        self.status.set_text(text); self.status.set_color(colour)
        self.big.set_text(value); self.big.set_color(colour)

        if not self.marker_inlet:
            self.phase.set_text("Headset events: waiting for the bridge")
        elif not self.markers:
            self.phase.set_text("Headset events: none yet (session not started)")
        else:
            m = self.markers[-1][1]
            phase = PHASES.get(m.get("marker"), m.get("marker", ""))
            if m.get("marker") == "StimulusShown" and m.get("object_id"):
                phase += f": {m['object_id']}"
            trial = f"Image {m['trial']}  |  " if m.get("trial") else ""
            self.phase.set_text(f"{trial}{phase}")
        return self.line, self.status, self.big, self.phase


def main():
    view = LiveHeartRate()
    animation = FuncAnimation(view.fig, view.update, interval=500, cache_frame_data=False)
    plt.show()
    return animation


if __name__ == "__main__":
    main()
