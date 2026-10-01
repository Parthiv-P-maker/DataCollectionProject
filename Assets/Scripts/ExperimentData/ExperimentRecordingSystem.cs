using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>Single owner of the participant-session files. All public Log methods are safe to call from worker threads.</summary>
public sealed class ExperimentRecordingSystem : MonoBehaviour
{
    [Header("Recording")]
    [SerializeField] private string sessionId = "S001";
    [SerializeField, Min(1)] private int flushEveryRecords = 25;
    [Tooltip("Keep only physiology samples recorded between the opening and closing fixation cross of a trial.")]
    [SerializeField] private bool recordPhysiologyOnlyDuringStimulus = true;

    private readonly ConcurrentQueue<QueuedLine> pending = new ConcurrentQueue<QueuedLine>();
    private readonly Stopwatch monotonicClock = Stopwatch.StartNew();
    private StreamWriter gazeWriter, physiologyWriter, eventWriter;
    private int pendingSinceFlush;
    private bool recording;
    private string participantId;
    private string trialId = "";
    private double lslOffsetSeconds;
    private bool lslClockSynchronized;
    private double windowStartLsl = double.NaN, windowEndLsl = double.NaN;
    private double windowStartMono = double.NaN, windowEndMono = double.NaN;
    private string windowTrialId = "";
    private double stimulusStartLsl = double.NaN, stimulusEndLsl = double.NaN;
    private double stimulusStartMono = double.NaN, stimulusEndMono = double.NaN;
    private bool stimulusEnded;

    public bool IsRecording => recording;
    public bool IsLslClockSynchronized => lslClockSynchronized;
    public string SessionDirectory { get; private set; }
    public string CurrentTrialId => trialId;
    public double MonotonicSeconds => monotonicClock.Elapsed.TotalSeconds;
    public double EstimatedLslTime => lslClockSynchronized ? MonotonicSeconds + lslOffsetSeconds : double.NaN;

    private struct QueuedLine { public int file; public string line; }

    public void BeginSession(string newParticipantId, string newSessionId = null) => BeginSession(newParticipantId, newSessionId, null);

    /// <summary>With an outputDirectory, files are written there as "&lt;participant&gt;_gaze_lsl.csv", "_heartrate.csv", "_events.csv", "_metadata.json".</summary>
    public void BeginSession(string newParticipantId, string newSessionId, string outputDirectory)
    {
        if (recording) return;
        participantId = SafeId(newParticipantId, "unknown");
        sessionId = SafeId(string.IsNullOrWhiteSpace(newSessionId) ? sessionId : newSessionId, "S001");
        bool custom = !string.IsNullOrWhiteSpace(outputDirectory);
        string root = custom ? outputDirectory : Path.Combine(Application.persistentDataPath, "ExperimentSessions", "participant_" + participantId,
            "session_" + sessionId + "_" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture));
        string prefix = custom ? participantId + "_" : "";
        Directory.CreateDirectory(root);
        SessionDirectory = root;
        windowStartLsl = windowEndLsl = windowStartMono = windowEndMono = double.NaN;
        windowTrialId = "";
        gazeWriter = CreateWriter(prefix + (custom ? "gaze_lsl.csv" : "gaze.csv"), "timestamp_lsl,local_monotonic_s,participant_id,session_id,trial_id,gaze_valid,source,origin_x,origin_y,origin_z,direction_x,direction_y,direction_z,hit_x,hit_y,hit_z,hit_object,hit_instance_id");
        physiologyWriter = CreateWriter(prefix + (custom ? "heartrate.csv" : "physiology.csv"), "timestamp_lsl,received_local_monotonic_s,participant_id,session_id,trial_id,stream,heart_rate_bpm,rr_interval_ms,ecg_value,phase");
        eventWriter = CreateWriter(prefix + "events.csv", "timestamp_lsl,local_monotonic_s,participant_id,session_id,trial_id,event_type,object_id,metadata");
        File.WriteAllText(Path.Combine(root, prefix + "metadata.json"), JsonUtility.ToJson(new SessionMetadata {
            participant_id = participantId, session_id = sessionId, unity_version = Application.unityVersion,
            device_model = SystemInfo.deviceModel, utc_started = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            timestamp_note = "timestamp_lsl becomes valid after bridge clock synchronization; blank means unavailable, never zero-filled.",
            physiology_scope = recordPhysiologyOnlyDuringStimulus ? "fixation cross before, image, and fixation cross after each trial" : "entire session"
        }, true), Encoding.UTF8);
        recording = true;
        LogEvent("ExperimentStart", "", "unity_started");
    }

    public void SetTrial(string id, string stimulusId = "")
    {
        trialId = id ?? "";
        LogEvent("TrialStart", stimulusId, "");
    }

    public void EndTrial(string stimulusId = "") { LogEvent("TrialEnd", stimulusId, ""); trialId = ""; }

    public void OpenPhysiologyWindow()
    {
        windowStartLsl = EstimatedLslTime; windowStartMono = MonotonicSeconds;
        windowEndLsl = windowEndMono = double.PositiveInfinity;
        windowTrialId = trialId;
        stimulusStartLsl = stimulusEndLsl = stimulusStartMono = stimulusEndMono = double.NaN;
        stimulusEnded = false;
    }

    public void MarkStimulusStart() { stimulusStartLsl = EstimatedLslTime; stimulusStartMono = MonotonicSeconds; }
    public void MarkStimulusEnd() { stimulusEndLsl = EstimatedLslTime; stimulusEndMono = MonotonicSeconds; stimulusEnded = true; }

    private string PhaseFor(double lslTimestamp, double receivedAt)
    {
        bool useLsl = !double.IsNaN(stimulusStartLsl) && !double.IsNaN(lslTimestamp) &&
            (!stimulusEnded || !double.IsNaN(stimulusEndLsl));
        double time = useLsl ? lslTimestamp : receivedAt;
        double start = useLsl ? stimulusStartLsl : stimulusStartMono;
        double end = useLsl ? stimulusEndLsl : stimulusEndMono;
        if (double.IsNaN(start) || time < start) return "fixation_before";
        if (!stimulusEnded || double.IsNaN(end) || time <= end) return "image";
        return "fixation_after";
    }

    public void ClosePhysiologyWindow()
    {
        windowEndLsl = EstimatedLslTime; windowEndMono = MonotonicSeconds;
    }

    // Prefer the sensor's LSL timestamp so samples delayed by Bluetooth/Wi-Fi are still attributed to the right window.
    private bool InPhysiologyWindow(double lslTimestamp, double receivedAt)
    {
        if (!recordPhysiologyOnlyDuringStimulus) return true;
        if (double.IsNaN(windowStartMono)) return false;
        if (!double.IsNaN(windowStartLsl) && !double.IsNaN(windowEndLsl) && !double.IsNaN(lslTimestamp))
            return lslTimestamp >= windowStartLsl && lslTimestamp <= windowEndLsl;
        return receivedAt >= windowStartMono && receivedAt <= windowEndMono;
    }

    public void UpdateClockOffset(double bridgeLslAtMidpoint, double questMidpointSeconds)
    {
        double candidate = bridgeLslAtMidpoint - questMidpointSeconds;
        lslOffsetSeconds = lslClockSynchronized ? lslOffsetSeconds * 0.8 + candidate * 0.2 : candidate;
        lslClockSynchronized = true;
    }

    public void LogGaze(bool valid, string source, Vector3 origin, Vector3 direction, Vector3 hitPoint, string hitObject, int hitInstanceId)
    {
        if (!recording) return;
        Enqueue(0, Csv(EstimatedLslTime) + "," + Csv(MonotonicSeconds) + "," + Csv(participantId) + "," + Csv(sessionId) + "," + Csv(trialId) + "," + (valid ? "1" : "0") + "," + Csv(source) + "," + V(origin) + "," + V(direction) + "," + V(hitPoint) + "," + Csv(hitObject) + "," + hitInstanceId);
    }

    public void LogPhysiology(double lslTimestamp, double receivedAt, string stream, double? hr, double? rr, double? ecg)
    {
        if (!recording || !InPhysiologyWindow(lslTimestamp, receivedAt)) return;
        string sampleTrialId = recordPhysiologyOnlyDuringStimulus ? windowTrialId : trialId;
        Enqueue(1, Csv(lslTimestamp) + "," + Csv(receivedAt) + "," + Csv(participantId) + "," + Csv(sessionId) + "," + Csv(sampleTrialId) + "," + Csv(stream) + "," + Csv(hr) + "," + Csv(rr) + "," + Csv(ecg) + "," + Csv(PhaseFor(lslTimestamp, receivedAt)));
    }

    public void LogEvent(string eventType, string objectId = "", string metadata = "")
    {
        if (!recording) return;
        Enqueue(2, Csv(EstimatedLslTime) + "," + Csv(MonotonicSeconds) + "," + Csv(participantId) + "," + Csv(sessionId) + "," + Csv(trialId) + "," + Csv(eventType) + "," + Csv(objectId) + "," + Csv(metadata));
    }

    private void Update() { Drain(); }
    private void OnApplicationQuit() { CloseSession(); }
    private void OnDestroy() { CloseSession(); }

    public void CloseSession()
    {
        if (!recording) return;
        LogEvent("ExperimentEnd", "", "unity_closed");
        Drain();
        gazeWriter?.Dispose(); physiologyWriter?.Dispose(); eventWriter?.Dispose();
        gazeWriter = physiologyWriter = eventWriter = null;
        recording = false;
    }

    private StreamWriter CreateWriter(string file, string header)
    {
        StreamWriter writer = new StreamWriter(Path.Combine(SessionDirectory, file), false, new UTF8Encoding(false));
        writer.WriteLine(header); writer.Flush(); return writer;
    }
    private void Enqueue(int file, string line) { pending.Enqueue(new QueuedLine { file = file, line = line }); }
    private void Drain()
    {
        QueuedLine line;
        while (pending.TryDequeue(out line))
        {
            if (line.file == 0) gazeWriter?.WriteLine(line.line); else if (line.file == 1) physiologyWriter?.WriteLine(line.line); else eventWriter?.WriteLine(line.line);
            if (++pendingSinceFlush >= flushEveryRecords) { gazeWriter?.Flush(); physiologyWriter?.Flush(); eventWriter?.Flush(); pendingSinceFlush = 0; }
        }
    }
    private static string V(Vector3 value) => Csv(value.x) + "," + Csv(value.y) + "," + Csv(value.z);
    private static string Csv(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "" : value.ToString("R", CultureInfo.InvariantCulture);
    private static string Csv(double? value) => value.HasValue ? Csv(value.Value) : "";
    private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    private static string SafeId(string value, string fallback) { string v = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); foreach (char c in Path.GetInvalidFileNameChars()) v = v.Replace(c.ToString(), "_"); return v.Replace(" ", "_"); }
    [Serializable] private class SessionMetadata { public string participant_id, session_id, unity_version, device_model, utc_started, timestamp_note, physiology_scope; }
}
