# VR Experiment Setup

The project now uses `Assets/Scenes/BasicScene.unity` as the build scene. The `Experiment Controller` in that scene creates the simple flat world and runs a configurable panorama experiment.

## What is already implemented

- User starts in the center of a simple flat floor.
- A 1-to-9 comfort rating is collected at the start and end of the study. The opening rating must be answered before the Start button becomes active.
- A blank white fixation screen showing only a black `+` is shown before and after every image for `Blank Duration Seconds` (Experiment Controller; currently 1 s).
- Each 360-degree equirectangular image is shown inside an inward-facing sphere for `Image Duration Seconds` (currently 10 s). Both durations are set in the Inspector and need a rebuild.
- Eye gaze is sampled every 0.05 seconds while each image is visible.
- If eye tracking is unavailable, including on Quest 3S, head direction is recorded as `head_fallback` so the flow can still be tested.
- Startup checks the detected headset and shows a compatibility warning for unsupported or unknown hardware. Initialization errors are shown on-screen instead of leaving a blank view.
- The experiment UI is attached in front of the XR camera, so it is visible in both Meta XR Simulator and a physical headset.
- Instructions and response screens use a white background with black text, hiding the environment while a participant answers.
- Per-image questions use their Inspector-configured MCQ option buttons. Number keys select an option during desktop testing; Enter activates Start on the opening comfort screen and Next during questions.
- Select an MCQ option and press Next to move to the next question; after the final question, the experiment advances to the next image.
- A unique participant ID is generated for every run, so 60 or more participants do not overwrite one another.
- CSV and heatmap files are written to `Application.persistentDataPath/ExperimentData`.

## Steps you must do in Unity

1. Open the project in Unity `6000.4.8f1` and open `Assets/Scenes/BasicScene.unity`.
2. Import equirectangular panorama images into `Assets`.
3. Select `Experiment Controller`. The `Trials` list is the source of truth for the experiment. Replace an image by changing that trial's `Image` field. To add more images, increase the `Trials` list size and assign the image for each additional trial directly in that list. `Image 1` through `Image 3` are only used as initial defaults.
4. Edit the opening and ending comfort prompts or endpoint labels, assign an optional `Comfort Scale Reference Image` texture, and edit each trial's `Trial Name`, `Questions`, question `Prompt`, and MCQ `Options` directly in the Inspector. Increase a question list's size to add questions.
5. In the OpenXR settings for Android, confirm Meta Quest support includes `Quest 3S`. Enable `Eye Gaze Interaction Profile` only when using a headset with eye tracking; it is not required for Quest 3S.
6. Build and run on a Meta Quest 3S or another enabled Meta Quest target. The app will warn if the headset is unknown or unsupported. On Quest Pro, enable eye tracking, grant the app eye-tracking permission, and complete calibration if eye-gaze data is required.
7. Use a controller or hand ray to select the start and answer buttons. Verify the complete timing once on the headset before collecting participants.

## Running sessions without the developer

See `README_CLIENT.md` and the batch files in the project root: `Setup_Once.bat`, `Install_App.bat`, `StartSession.bat`, `FinishSession.bat`. The Quest build adds the eye-tracking permission to the manifest (`Assets/Scripts/Editor/EyeTrackingManifestPostprocessor.cs`) and asks for it at launch (`Assets/Scripts/QuestEyeTrackingPermission.cs`). The laptop IP is editable on the System Check page and remembered on the headset.

## Heart rate (Polar H10)

The Excite-O-Meter Windows app cannot reliably share LSL streams with other programs on the same PC (Windows Store app sandbox). Use the Python script instead; it publishes the same `HeartRate` and `RRinterval` streams:

1. Close Excite-O-Meter. Wet the strap and put it on.
2. Terminal 1: `python Tools/polar_h10_lsl.py --device 1B77F535` and wait for `Connected ... Streaming`.
3. Terminal 2: `python Tools/physiology_lsl_bridge.py --quest-port 8765 --backup-dir bridge_recordings/P001` and wait for `Connected LSL stream: HeartRate`.
4. In Unity, `Laptop Ip Address` = `127.0.0.1` for Editor/Link, or the laptop's Wi-Fi IP for a Quest build.

Requires `pip install bleak pylsl`. ECG is not streamed.

## Session flow

1. **System check**: shows the auto-generated participant ID (`P001`, `P002`, ...), live Polar H10 heart rate, LSL clock sync and eye-tracking status. Nothing on this page is saved. Press **Ready**.
2. **Instructions page**: title, text and button label are set on the Experiment Controller (`Instructions Title`, `Instructions Text`, `Instructions Button Label`, `Instructions Page Scale` = how much larger this page is drawn than the others; untick `Show Instructions` to skip it). The participant presses **Start** (Enter in the Editor).
3. Fixation / image / fixation / questions for each image. The opening and closing 1-9 comfort ratings are switched off by default; tick `Ask Comfort Questions` on the Experiment Controller to bring them back (their columns stay in the CSVs, blank when off).
3. **Experiment Complete** screen with an **Exit** button that closes the files and quits the app.

The participant ID is the next free number after the highest existing `P###` folder. Pull the data off the headset after every session: uninstalling the app deletes its data folder and restarts numbering at `P001`.

## Output files

```
ExperimentData/
  P001/
    P001_gaze.csv              gaze per sample with panorama u/v and answers
    P001_gaze_lsl.csv          gaze with LSL timestamps (for joining with heart rate)
    P001_heartrate.csv         Polar H10 HR / RR for each trial's window (fixation + image + fixation)
    P001_events.csv            fixation, image shown/hidden, answers (timestamped)
    P001_responses.csv         one row per questionnaire answer
    P001_image_<n>_heatmap.csv / .png
    P001_metadata.json
  P002/ ...
  Combined/
    combined_gaze.csv          all participants' gaze rows
    combined_image_<n>_heatmap.csv / .png
```

Heart rate is recorded for each trial from the opening fixation cross to the end of the closing fixation cross: fixation + image + fixation per image (1 s + 10 s + 1 s with the current settings), so a pre-image baseline and a post-image recovery are included. Every sample carries a `phase` column of `fixation_before`, `image` or `fixation_after`, based on its own LSL timestamp (receipt time is used until the clock is synchronized). Set `Record Physiology Only During Stimulus` off on `ExperimentRecordingSystem` to record the whole session instead.

`P001_events.csv` marks the image boundaries as `StimulusShown` / `StimulusHidden` with `image_start_marker` / `image_end_marker`, and the two fixation crosses as `before_image` / `after_image`.

`python Tools/make_visuals.py <ExperimentData folder>` (use Anaconda Python) draws, without touching existing files: `P001_image_N_overlay.png` (heatmap on the panorama), `P001_image_N_scanpath.png`, `P001_heartrate_trial_N.png` and `P001_heartrate_all_trials.png` (heart rate with image start/end lines) in each participant folder, and `Combined/combined_image_N_overlay.png`. Add `--participant P007` for one person. Run it on the laptop after copying data off the headset; if an overlay looks mirrored left/right, add `--flip-u`.

`python Tools/gaze_fixations.py <ExperimentData folder>` detects fixations offline with I-DT (dispersion 1.5 deg, minimum 100 ms, adjacent same-spot fixations merged; `--dispersion-deg` / `--min-duration` to change) from the ~20 Hz eye samples (`gaze_valid = 1` only), writing `P001_fixations.csv`, `P001_fixation_summary.csv` and `P001_image_N_fixations.png`. FinishSession.bat runs it automatically. Timing resolution is about 50 ms because the app samples gaze at 20 Hz.

`python Tools/live_heart_rate.py` shows a live laptop chart of the Polar H10 heart rate with a no-contact warning (opened by StartSession.bat), plus image bands and the current phase from the headset. The app forwards every logged event over the bridge connection (background thread, dropped if disconnected); the bridge republishes them as the LSL string stream `StudyMarkers` (JSON: trial, marker, object_id, metadata), stamped on arrival, and saves them as `Marker` rows in its backup CSV. The headset's `events.csv` remains the exact record.

`python Tools/remove_participant.py P003` (or RemoveParticipant.bat) pulls the newest headset data, moves the participant folder to `CollectedData/_removed/`, deletes it from the headset, and rebuilds `Combined/` (gaze rows and heatmap sums, identical to the app's own format) on the laptop and headset. `--list` shows participants and whether they are complete; `--laptop-only` leaves the headset alone.

`python Tools/analyze_session.py <participant-folder>` merges gaze, events and heart rate into `synchronized.csv`, one row per sample sorted by time, carrying the `phase` column and the image start/end markers for plotting.

The CSV stores normalized equirectangular coordinates as `pano_u` and `pano_v`, so samples can be mapped back onto the panorama later. It also includes both the opening and ending comfort ratings.
Start a fresh `combined_gaze.csv` before using this version so all rows use the same CSV column layout.

## Accessing data

- In the Unity Editor on this computer, check `C:\Users\parth\AppData\LocalLow\DefaultCompany\DataCollectionProject\ExperimentData`.
- On Quest, files are written to the app's persistent-data directory. Use `adb pull /sdcard/Android/data/<package-id>/files/ExperimentData <destination-folder>` after connecting the headset with USB debugging enabled.
- The final Unity Console message shows the exact persistent-data folder used by the current run.
