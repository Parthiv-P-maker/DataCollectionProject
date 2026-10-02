# VR Study - Instructions for Running Sessions

This study shows each participant a series of 360-degree street images (currently 18) in a Meta Quest Pro headset while recording where they look (eye tracking) and their heart rate (Polar H10 chest strap). Everything is saved automatically, one folder per participant.

You do **not** need to use Unity. You only need the files in this folder.

---

## What you need

- The Windows laptop with this folder on it
- Meta Quest Pro headset, charged, with its USB cable
- Polar H10 chest strap
- Wi-Fi network that **both the laptop and the headset are connected to**
  (a phone hotspot or your own router works best; some university/office Wi-Fi blocks devices from talking to each other)

---

## One-time setup (do this once, before the first participant)

### On the laptop
1. Right-click **Setup_Once.bat** and choose **Run as administrator**. Wait until it says "Setup finished".
2. Make sure the Windows **Excite-O-Meter** app is **closed**. It is not needed any more.

### On the headset
1. Turn on **Developer Mode** (Meta Horizon phone app -> Devices -> your headset -> Developer Mode).
2. In the headset: **Settings -> Movement tracking -> Eye tracking: On**.
3. Connect the headset to the same Wi-Fi as the laptop.

### Install the study app
1. Plug the headset into the laptop with the USB cable and put the headset on.
2. If it asks "Allow USB debugging?", choose **Always allow** and **Allow**.
3. Double-click **Install_App.bat**. Wait for the word `Success`.

### First time the app opens
The headset will ask permission to use **eye tracking**. Choose **Allow**.
Then **close the app completely and open it again once** so eye tracking starts.
(Find it in the headset under Library -> Unknown Sources.)

---

## For every participant

### 1. Prepare the strap
- **Wet the two rubber/fabric electrode patches** on the strap with water.
- Fasten the strap snugly just below the chest, logo upright. Snap the sensor onto it.
- The sensor only switches on when it touches skin.

### 2. Start the software
1. Double-click **StartSession.bat**.
2. More windows open. **Leave them open.**
   - **"Study - Polar H10"** should start printing lines like `HR 72 bpm`. If it keeps saying "Not found", re-wet the strap and make sure Excite-O-Meter is closed.
   - **"Study - Bridge"** will say `Connected LSL stream: HeartRate`.
   - **"Live heart rate"** shows a live chart. Green "Strap OK" with a number is good. Red **NO SKIN CONTACT** means wet the strap and tighten it. Keep this window visible during the session. Nothing in it is saved.
3. The first window prints **this laptop's address** (for example `192.168.1.106`). Keep it in mind for step 4.

### 3. Put the headset on the participant
- Adjust it so the image is sharp.
- **Calibrate the eyes first**: Settings -> Movement tracking -> Eye tracking -> Calibrate. Finish the whole calibration.
- **Only then** open the study app (Library -> Unknown Sources). If the app was already open, close it completely and open it again. The app only picks up eye tracking when it starts.

### 4. The "System Check" page (shown first in the headset)
It shows four lines:

| Line | What you want |
|---|---|
| Heart rate | **OK** with a number (for example 74 bpm) |
| Clock sync | **OK** |
| Eye tracking | **OK** |
| Laptop IP | Same as the address shown on the laptop |

- If **Laptop IP** is different from the address on the laptop, choose **Change laptop IP**, type the laptop's address with the on-screen number pad, and press **Save**. The headset remembers it for next time.
- The page tells you in words whether everything is fine. **Only press Ready when all lines are OK.**
- Nothing on this page is saved.

### 5. The experiment (runs by itself)
1. After you press Ready, an **instructions page** appears. Let the participant read it (or read it to them), then the participant presses **Start**.
2. For each image (currently 18): a blank white screen with a cross, then the image, then the cross again, then one question: "How safe do you feel in this alley (or street corridor)?" answered on a 1-5 scale.
3. A "Thank you" screen with an Exit button.

Tell the participant to keep the headset on and look around naturally. They use the controller to point and press.

### 6. When the participant is finished
- The last screen has an **Exit** button. Press it. The data is already saved.
- You can now take the headset off the participant.

---

## After a session (or at the end of the day)

1. Plug the headset into the laptop with the USB cable (put it on and choose **Allow** if asked).
2. Double-click **FinishSession.bat**.
3. It closes the two windows, copies all results to the laptop, creates the pictures, and opens the results folder:

```
CollectedData\ExperimentData\
    P001\       <- first participant
    P002\       <- second participant ...
    Combined\   <- all participants together
```

**Participant numbers are automatic** (P001, P002, ...). There is nothing to type.

### What is in each participant folder

| File | What it is |
|---|---|
| `P001_gaze.csv` | Where they looked, with answers (head direction only if eye tracking was off) |
| `P001_heartrate.csv` | Heart rate (bpm) and beat intervals for each image, including the cross before and after it |
| `P001_events.csv` | Exact times: cross shown, image start, image end, answers |
| `P001_responses.csv` | All questionnaire answers |
| `P001_image_N_overlay.png` | Heatmap of where they looked, drawn on the image |
| `P001_image_N_scanpath.png` | Their gaze path drawn on the image |
| `P001_heartrate_trial_N.png` | Heart-rate chart for one image, with lines where the image starts and ends |
| `P001_heartrate_all_trials.png` | All images' heart-rate charts together |
| `P001_fixations.csv` | Every fixation (where the eyes stopped on something): start time, duration, position on the image |
| `P001_fixation_summary.csv` | Per image: % valid eye data, number of fixations, average fixation length |
| `P001_image_N_fixations.png` | Fixations drawn on the image (bigger circle = longer look, number = order) |
| `Combined\combined_image_N_overlay.png` | Everyone's gaze on one image |

**Quick quality check:** open `P001_fixation_summary.csv`. If `valid_percent` is well below 80 for an image, eye tracking was poor for that image (recalibrate before the next participant).

In `P001_gaze.csv`, rows with `gaze_valid` = 1 are real eye tracking. Rows with 0 were head direction only (eye tracking lost).

---

## Important rules

- **Never uninstall the app** from the headset. It deletes all saved results and restarts the numbering at P001.
- Copy the results to the laptop regularly with **FinishSession.bat**. Copying does not delete anything on the headset.
- Do not use Excite-O-Meter at the same time as this software.

---

## If something goes wrong

| Problem | What to do |
|---|---|
| **Heart rate: NO DATA** | Check the "Study - Polar H10" window shows `HR ... bpm`. If not: re-wet the strap, tighten it, close Excite-O-Meter, close both windows and run StartSession.bat again. If that window is fine, the "Study - Bridge" window must say `Connected LSL stream: HeartRate`. |
| **"Bridge unavailable"** (shown under heart rate) | Check the headset and laptop are on the **same Wi-Fi**. Check **Laptop IP** matches the address from StartSession.bat (use Change laptop IP). Make sure the "Study - Bridge" window is open. |
| **Clock sync: waiting** | Normally turns OK within a few seconds of the heart rate working. If not, see the line above. |
| **Eye tracking: NOT TRACKED** | Check Settings -> Movement tracking -> Eye tracking is On. Run the eye calibration. If you never saw the "Allow eye tracking" question, close the app, open it again and choose Allow. Re-seat the headset on the face. |
| **Heart-rate values are 0 or jump wildly** | The strap has lost skin contact. Wet the patches and tighten the strap. |
| **FinishSession: "HEADSET NOT FOUND"** | Plug in the USB cable, put the headset on and choose Allow. Try again. |
| **The app closed in the middle of a session** | Open it again; the next participant number is used automatically. Note down that the earlier participant's session was incomplete. Their partial data is still in its folder. |
| **Nothing works** | Close both black windows, close the app, take the sensor off the strap and snap it back on, and start again from StartSession.bat. |

---

## Quick reference

| File | When |
|---|---|
| `Setup_Once.bat` | Once per laptop (as administrator) |
| `Install_App.bat` | Once per headset, or after getting a new app version |
| `StartSession.bat` | Before each participant (or once for a whole day) |
| `FinishSession.bat` | After sessions, to collect the results |
