"""Stream Polar H10 heart rate and RR intervals to LSL directly over Bluetooth.

Replaces the Excite-O-Meter Windows app. Publishes the same stream names
("HeartRate", "RRinterval") so physiology_lsl_bridge.py works unchanged.

    python Tools/polar_h10_lsl.py                 # first Polar H10 found
    python Tools/polar_h10_lsl.py --device 1B77F535

Close Excite-O-Meter first so it does not hold the strap's connection.
"""
import argparse
import asyncio
import struct

from bleak import BleakClient, BleakScanner
from pylsl import StreamInfo, StreamOutlet, local_clock

HR_MEASUREMENT = "00002a37-0000-1000-8000-00805f9b34fb"


def make_outlet(name, unit, source_id):
    info = StreamInfo(name, name, 1, 0, "float32", source_id)
    info.desc().append_child("channels").append_child("channel").append_child_value("unit", unit)
    return StreamOutlet(info)


def parse_heart_rate(data):
    """Standard BLE Heart Rate Measurement: returns (bpm, [rr_ms, ...])."""
    flags = data[0]
    offset = 1
    if flags & 0x01:
        bpm = struct.unpack_from("<H", data, offset)[0]; offset += 2
    else:
        bpm = data[offset]; offset += 1
    if flags & 0x08:
        offset += 2
    rr = []
    if flags & 0x10:
        while offset + 1 < len(data):
            rr.append(struct.unpack_from("<H", data, offset)[0] * 1000.0 / 1024.0)
            offset += 2
    return bpm, rr


async def run(device_filter):
    # Created once so the bridge stays attached across strap reconnects.
    source = "PolarH10_" + (device_filter.upper() or "any")
    hr_outlet = make_outlet("HeartRate", "bpm", source + "_HR")
    rr_outlet = make_outlet("RRinterval", "ms", source + "_RR")
    while True:
        print("Scanning for Polar H10" + (f" {device_filter}" if device_filter else "") + " ...")
        device = await BleakScanner.find_device_by_filter(
            lambda d, adv: (d.name or "").startswith("Polar H10") and device_filter.upper() in (d.name or "").upper(),
            timeout=15.0)
        if device is None:
            print("Not found. Is the strap worn and wet, and Excite-O-Meter closed? Retrying...")
            continue

        disconnected = asyncio.Event()

        def on_data(_, data):
            received = local_clock()
            bpm, rr_list = parse_heart_rate(data)
            hr_outlet.push_sample([float(bpm)], received)
            # A packet can carry several beats; the last one ends at receipt, earlier ones are back-dated.
            stamp = received
            for rr in reversed(rr_list):
                rr_outlet.push_sample([rr], stamp)
                stamp -= rr / 1000.0
            print(f"HR {bpm:3d} bpm   RR {', '.join(f'{x:.0f}' for x in rr_list) or '-'} ms")

        try:
            async with BleakClient(device, disconnected_callback=lambda _: disconnected.set()) as client:
                print(f"Connected to {device.name}. Streaming LSL 'HeartRate' and 'RRinterval'. Ctrl+C to stop.")
                await client.start_notify(HR_MEASUREMENT, on_data)
                await disconnected.wait()
                print("Strap disconnected. Reconnecting...")
        except Exception as error:
            print(f"Connection error: {error}. Retrying...")
        await asyncio.sleep(2)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", default="", help="Part of the strap name, e.g. 1B77F535")
    args = parser.parse_args()
    try:
        asyncio.run(run(args.device))
    except KeyboardInterrupt:
        print("Stopped.")


if __name__ == "__main__":
    main()
