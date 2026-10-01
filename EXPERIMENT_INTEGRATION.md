# Quest + Polar H10 integration

This project contains Unity 6.4, OpenXR 1.16.1, Meta OpenXR 2.5.1 and the existing Excite-O-Meter/LSL4Unity asset. The OpenXR **Eye Gaze Interaction Profile is present but disabled for Android**; enable it in **Project Settings > XR Plug-in Management > OpenXR > Android** before a Quest Pro build. The current `SimpleVrWorldBuilder` reads `<EyeGaze>/pose`; it is real eye tracking only on a headset/runtime that exposes it. The Editor/XR simulator is not a validation of Quest Pro gaze.

## Chosen architecture

Use the laptop bridge for participant runs: `Polar H10 -> Excite-O-Meter -> LSL on laptop -> TCP bridge -> Quest`. Although this project includes ARM64 `liblsl.so`, direct LSL discovery from Android depends on multicast/firewall/routing and has not been validated for this project/runtime. TCP needs only the Quest and laptop on the same Wi-Fi/LAN plus one permitted firewall port, and reconnects after a temporary loss. LabRecorder can simultaneously record the original LSL streams to XDF.

`physiology_lsl_bridge.py` keeps the original LSL timestamps. The Quest sends periodic clock pings; the bridge replies with its `pylsl.local_clock()` timestamps. Unity calculates a midpoint offset, so gaze and event timestamps are estimates in the **same LSL clock domain**. Do not begin a participant session until the bridge status says `LSL clock synchronized`. Blank CSV timestamps mean clock sync was unavailable; they are intentionally not zero-filled.

## Unity hookup

1. In the scene object holding `SimpleVrWorldBuilder`, add `ExperimentRecordingSystem` and `PhysiologyBridgeClient`.
2. Assign the recorder in both components and set `PhysiologyBridgeClient > Laptop Ip Address` to the laptop's LAN IPv4 address. Do not use `localhost` on Quest.
3. Enable Android Eye Gaze Interaction Profile, enable Quest Pro eye tracking in headset settings, build ARM64 Android, then test with a physical Quest Pro.
4. On the laptop, install/use the existing Python environment with `pylsl`, start Excite-O-Meter and confirm HeartRate/RRinterval, start LabRecorder, then run `python Tools/physiology_lsl_bridge.py --quest-port 8765`.

The new recorder writes `gaze.csv`, `physiology.csv`, `events.csv`, and `metadata.json` below `Application.persistentDataPath/ExperimentSessions/participant_<id>/session_<id>_<utc>/`. It buffers non-blocking records and flushes every 25 writes. Raw ECG is forwarded and logged, but keep it disabled in the bridge if you do not need it because it increases network and storage load.

## Verification checklist

- The bridge console lists HeartRate and RRinterval, then reports a Quest connection.
- The Unity component status becomes `Bridge connected; LSL clock synchronized` before Start.
- A Quest Pro sample has `source=eye_gaze` and `gaze_valid=1`; editor/head fallback samples identify themselves explicitly.
- Compare bridge backup timestamps, LabRecorder XDF stream timestamps, and `physiology.csv`: they should be the same LSL domain.
- Interrupt Wi-Fi briefly: bridge reconnects; the gap remains a gap rather than becoming zeros.

For analysis, join gaze/events to physiology by `timestamp_lsl`, using the nearest prior HR sample and the nearest RR interval event in a bounded window. Calculate RMSSD and SDNN only after artifact-screening RR intervals; never derive them from heart rate alone.

`python Tools/analyze_session.py <session-folder>` writes `synchronized.csv` and `analysis_summary.json`. Its RR screening is deliberately conservative (300–2000 ms); review/replace it with your study's documented artifact policy before publication.
