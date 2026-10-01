"""Forward Excite-O-Meter LSL physiology streams to one Quest over TCP.

Run on the Windows laptop hosting Excite-O-Meter, not on the headset:
    python physiology_lsl_bridge.py --quest-port 8765 --backup-dir recordings

Messages retain the source LSL timestamp. Sync replies allow the Quest to map its
monotonic clock into the same LSL time domain using a midpoint (NTP-style) estimate.
"""
import argparse
import csv
import json
import queue
import socket
import threading
import time
from pathlib import Path

from pylsl import StreamInlet, local_clock, resolve_byprop

STREAMS = {"HeartRate": ("heart_rate_bpm", int), "RRinterval": ("rr_interval_ms", float), "RawECG": ("ecg_value", float)}

def writer(path, data_queue, stop):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as output:
        csv.writer(output).writerow(["timestamp_lsl", "stream", "value"])
        while not stop.is_set() or not data_queue.empty():
            try: item = data_queue.get(timeout=.2)
            except queue.Empty: continue
            csv.writer(output).writerow(item); output.flush()

def inlet_worker(stream_name, output_queue, backup_queue, stop):
    while not stop.is_set():
        streams = resolve_byprop("name", stream_name, timeout=2.0)
        if not streams: continue
        inlet = StreamInlet(streams[0], max_buflen=120)
        print(f"Connected LSL stream: {stream_name}")
        while not stop.is_set():
            sample, timestamp = inlet.pull_sample(timeout=.25)
            if timestamp is None: continue
            try: value = STREAMS[stream_name][1](sample[0])
            except (IndexError, TypeError, ValueError): continue
            message = {"type": "physiology", "stream": stream_name, "timestamp_lsl": timestamp,
                       "has_hr": False, "has_rr": False, "has_ecg": False}
            message[STREAMS[stream_name][0]] = value
            message[{"HeartRate": "has_hr", "RRinterval": "has_rr", "RawECG": "has_ecg"}[stream_name]] = True
            output_queue.put(message); backup_queue.put((timestamp, stream_name, value))

def send_json(conn, payload, lock):
    encoded = (json.dumps(payload, separators=(",", ":")) + "\n").encode("utf-8")
    with lock: conn.sendall(encoded)

def serve(port, physiological, stop):
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", port)); server.listen(1); server.settimeout(.5)
    print(f"Waiting for Quest TCP client on port {port} (allow this port through Windows Firewall).")
    while not stop.is_set():
        try: conn, address = server.accept()
        except socket.timeout: continue
        print("Quest connected:", address); conn.settimeout(.1); lock = threading.Lock()
        try:
            buffered = b""
            while not stop.is_set():
                try:
                    chunk = conn.recv(4096)
                    if not chunk: break
                    buffered += chunk
                    while b"\n" in buffered:
                        line, buffered = buffered.split(b"\n", 1)
                        request_received = local_clock()
                        message = json.loads(line.decode("utf-8"))
                        if message.get("type") == "sync_ping":
                            send_json(conn, {"type":"sync_reply", "quest_sent_monotonic":message["quest_sent_monotonic"],
                                             "received_lsl":request_received, "sent_lsl":local_clock()}, lock)
                except socket.timeout: pass
                while True:
                    try: send_json(conn, physiological.get_nowait(), lock)
                    except queue.Empty: break
        except (ConnectionError, OSError, json.JSONDecodeError) as error: print("Quest disconnected:", error)
        finally: conn.close()
    server.close()

def main():
    parser = argparse.ArgumentParser(); parser.add_argument("--quest-port", type=int, default=8765); parser.add_argument("--backup-dir", type=Path, default=Path("bridge_recordings")); args = parser.parse_args()
    stop = threading.Event(); physiological, backup = queue.Queue(), queue.Queue()
    threads = [threading.Thread(target=inlet_worker, args=(name, physiological, backup, stop), daemon=True) for name in STREAMS]
    threads.append(threading.Thread(target=writer, args=(args.backup_dir / "physiology_bridge_backup.csv", backup, stop), daemon=True))
    for thread in threads: thread.start()
    try: serve(args.quest_port, physiological, stop)
    except KeyboardInterrupt: print("Stopping bridge.")
    finally: stop.set()

if __name__ == "__main__": main()
