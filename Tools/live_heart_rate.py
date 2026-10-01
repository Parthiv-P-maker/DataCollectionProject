"""Live heart-rate window for the experimenter (laptop only; the headset app is not involved).

    python Tools/live_heart_rate.py

Reads the "HeartRate" and "RRinterval" LSL streams published by polar_h10_lsl.py and shows the
last two minutes, the current value, and a clear warning when the strap loses skin contact.
Nothing is saved; the headset and the bridge record the real data.
"""
import collections
import time

import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation
from pylsl import StreamInlet, local_clock, resolve_byprop

WINDOW_S = 120
STALE_S = 5


class LiveHeartRate:
    def __init__(self):
        self.hr = collections.deque()
        self.last_rr = None
        self.hr_inlet = self.rr_inlet = None
        self.next_resolve = 0.0
        self.fig, self.ax = plt.subplots(figsize=(9, 4.5))
        self.fig.canvas.manager.set_window_title("Live heart rate - Polar H10")
        (self.line,) = self.ax.plot([], [], "-o", color="#1f77b4", lw=1.8, ms=4)
        self.big = self.fig.text(0.98, 0.95, "", ha="right", va="top", fontsize=26, weight="bold")
        self.status = self.fig.text(0.02, 0.95, "", ha="left", va="top", fontsize=12)
        self.ax.set_xlim(-WINDOW_S, 0)
        self.ax.set_xlabel("Seconds ago")
        self.ax.set_ylabel("Heart rate (BPM)")
        self.ax.grid(alpha=0.3)
        self.fig.subplots_adjust(top=0.82)

    def connect(self):
        if self.hr_inlet and self.rr_inlet or local_clock() < self.next_resolve:
            return
        self.next_resolve = local_clock() + 3
        if not self.hr_inlet:
            found = resolve_byprop("name", "HeartRate", timeout=0.5)
            if found: self.hr_inlet = StreamInlet(found[0], max_buflen=WINDOW_S)
        if not self.rr_inlet:
            found = resolve_byprop("name", "RRinterval", timeout=0.5)
            if found: self.rr_inlet = StreamInlet(found[0], max_buflen=WINDOW_S)

    def pull(self):
        if self.hr_inlet:
            samples, stamps = self.hr_inlet.pull_chunk(timeout=0.0)
            for sample, stamp in zip(samples, stamps):
                self.hr.append((stamp, float(sample[0])))
        if self.rr_inlet:
            samples, _ = self.rr_inlet.pull_chunk(timeout=0.0)
            if samples:
                self.last_rr = float(samples[-1][0])
        now = local_clock()
        while self.hr and now - self.hr[0][0] > WINDOW_S:
            self.hr.popleft()

    def update(self, _frame=None):
        self.connect()
        self.pull()
        now = local_clock()
        xs = [t - now for t, _ in self.hr]
        ys = [v for _, v in self.hr]
        self.line.set_data(xs, ys)
        good = [v for v in ys if v >= 30]
        if good:
            self.ax.set_ylim(min(good) - 5, max(good) + 5)
        else:
            self.ax.set_ylim(40, 120)

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
        return self.line, self.status, self.big


def main():
    view = LiveHeartRate()
    animation = FuncAnimation(view.fig, view.update, interval=500, cache_frame_data=False)
    plt.show()
    return animation


if __name__ == "__main__":
    main()
