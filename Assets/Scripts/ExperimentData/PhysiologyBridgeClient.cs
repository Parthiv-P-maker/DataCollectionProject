using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>Quest-side TCP client for physiology_lsl_bridge.py. It preserves source LSL timestamps.</summary>
public sealed class PhysiologyBridgeClient : MonoBehaviour
{
    [SerializeField] private ExperimentRecordingSystem recorder;
    [SerializeField] private string laptopIpAddress = "192.168.1.10";
    [SerializeField] private int port = 8765;
    [SerializeField] private float syncIntervalSeconds = 2f;
    private readonly ConcurrentQueue<BridgeMessage> incoming = new ConcurrentQueue<BridgeMessage>();
    private Thread worker; private volatile bool running; private float nextSync; private TcpClient client; private readonly object sendLock = new object();
    [Serializable] private class BridgeMessage { public string type, stream; public double timestamp_lsl, received_lsl, sent_lsl, quest_sent_monotonic; public double heart_rate_bpm, rr_interval_ms, ecg_value; public bool has_hr, has_rr, has_ecg; }

    public string Status { get; private set; } = "Bridge not connected";
    public ExperimentRecordingSystem Recorder { get => recorder; set => recorder = value; }
    public double LastHeartRateBpm { get; private set; } = double.NaN;
    public float LastPhysiologyReceivedTime { get; private set; } = -1f;
    public bool IsClockSynchronized => recorder != null && recorder.IsLslClockSynchronized;
    private const string IpPrefKey = "physiology_bridge_ip";

    /// <summary>Laptop address. Changes are remembered between launches and applied immediately.</summary>
    public string LaptopIpAddress
    {
        get => laptopIpAddress;
        set
        {
            laptopIpAddress = (value ?? "").Trim();
            PlayerPrefs.SetString(IpPrefKey, laptopIpAddress); PlayerPrefs.Save();
            try { client?.Close(); } catch { }
        }
    }

    private void Awake()
    {
        if (recorder == null) recorder = GetComponent<ExperimentRecordingSystem>();
        string saved = PlayerPrefs.GetString(IpPrefKey, "");
        if (!string.IsNullOrWhiteSpace(saved)) laptopIpAddress = saved;
    }
    private void OnEnable() { running = true; worker = new Thread(NetworkLoop) { IsBackground = true, Name = "Physiology bridge" }; worker.Start(); }
    private void OnDisable() { running = false; try { client?.Close(); } catch { } if (worker != null && worker.IsAlive) worker.Join(500); }
    private void Update()
    {
        if (client != null && client.Connected && Time.unscaledTime >= nextSync) { Send(new BridgeMessage { type = "sync_ping", quest_sent_monotonic = recorder != null ? recorder.MonotonicSeconds : Time.realtimeSinceStartupAsDouble }); nextSync = Time.unscaledTime + syncIntervalSeconds; }
        while (incoming.TryDequeue(out BridgeMessage message))
        {
            if (message.type == "sync_reply" && recorder != null)
            {
                double questNow = recorder.MonotonicSeconds;
                double questMidpoint = (message.quest_sent_monotonic + questNow) * .5;
                recorder.UpdateClockOffset((message.received_lsl + message.sent_lsl) * .5, questMidpoint);
                Status = "Bridge connected; LSL clock synchronized";
            }
            else if (message.type == "physiology")
            {
                LastPhysiologyReceivedTime = Time.unscaledTime;
                if (message.has_hr) LastHeartRateBpm = message.heart_rate_bpm;
                if (recorder != null) recorder.LogPhysiology(message.timestamp_lsl, recorder.MonotonicSeconds, message.stream, message.has_hr ? message.heart_rate_bpm : (double?)null, message.has_rr ? message.rr_interval_ms : (double?)null, message.has_ecg ? message.ecg_value : (double?)null);
            }
        }
    }
    private void NetworkLoop()
    {
        while (running)
        {
            try
            {
                client = new TcpClient();
                if (!client.ConnectAsync(laptopIpAddress, port).Wait(3000)) throw new TimeoutException("no answer from " + laptopIpAddress);
                Status = "Bridge connected; synchronizing clock";
                using (NetworkStream stream = client.GetStream()) using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
                    while (running && client.Connected) { string line = reader.ReadLine(); if (line == null) break; BridgeMessage message = JsonUtility.FromJson<BridgeMessage>(line); if (message != null) incoming.Enqueue(message); }
            }
            catch (Exception e) { Status = "Bridge unavailable: " + e.GetBaseException().Message; }
            finally { try { client?.Close(); } catch { } client = null; }
            Thread.Sleep(1000);
        }
    }
    private void Send(BridgeMessage message)
    {
        try { lock (sendLock) { if (client == null || !client.Connected) return; byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(message) + "\n"); client.GetStream().Write(bytes, 0, bytes.Length); } } catch { }
    }
}
