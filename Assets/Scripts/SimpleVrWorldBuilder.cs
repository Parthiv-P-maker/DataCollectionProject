using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

[Serializable]
public class ExperimentQuestion
{
    public string prompt = "How much did you notice?";
    public string[] options = { "Option 1", "Option 2", "Option 3", "Option 4", "Option 5" };
}

[Serializable]
public class ExperimentTrialConfig
{
    public string trialName = "Image 1";
    public Texture2D image;
    public List<ExperimentQuestion> questions = new List<ExperimentQuestion>();
}

public class SimpleVrWorldBuilder : MonoBehaviour
{
    [Header("Timing")]
    public float blankDurationSeconds = 5f;
    public float imageDurationSeconds = 10f;
    public float gazeSampleIntervalSeconds = 0.05f;

    [Header("Experiment Content")]
    [Tooltip("Show the 1-9 comfort rating at the start and end of the session. Off by default.")]
    public bool askComfortQuestions = false;

    [Header("Instructions Page")]
    [Tooltip("Shown after the System Check page, before the first image.")]
    public bool showInstructions = true;
    public string instructionsTitle = "General Instruction";
    [Tooltip("Rich text is allowed, e.g. <b>bold</b>. The font shrinks automatically to fit.")]
    [TextArea(6, 20)]
    public string instructionsText =
        "<b>Welcome to the Visual Perception Task!</b>\n\n" +
        "In this session, you will view a series of 16 360° images showcasing various street environments.\n\n" +
        "<b>Observe:</b> Each image will be displayed in the headset for 10 seconds. Please look around naturally, focusing on whichever details catch your attention.\n\n" +
        "<b>Rate:</b> Immediately following each image, you will answer 5 brief questions using a 7-point scale to share how the image made you feel.\n\n" +
        "<b>Important Guidelines for Accurate Recording:</b>\n" +
        "<b>Keep Still:</b> Try to keep your head as steady as possible throughout the task.\n" +
        "<b>Blink Normally:</b> Feel free to blink naturally. No need to hold your eyes open unnaturally.\n" +
        "<b>Take Your Time:</b> Answer the questions honestly based on your immediate impression. There are no right or wrong answers.\n\n" +
        "By pressing the start button below, you confirm that you have read the study information and voluntarily consent to participate.\n\n" +
        "When you are comfortable and ready to begin, press the START button.";
    public string instructionsButtonLabel = "START";
    public ExperimentQuestion startingQuestion = new ExperimentQuestion
    {
        prompt = "How comfortable do you feel right now?",
        options = new[] { "1 - Not comfortable", "2", "3", "4", "5", "6", "7", "8", "9 - Very comfortable" }
    };
    public ExperimentQuestion endingComfortQuestion = new ExperimentQuestion
    {
        prompt = "How comfortable do you feel now?",
        options = new[] { "1 - Not comfortable", "2", "3", "4", "5", "6", "7", "8", "9 - Very comfortable" }
    };
    public List<ExperimentTrialConfig> trials = new List<ExperimentTrialConfig>();

    [Header("Comfort Scale Artwork")]
    [Tooltip("Optional scale-strip image for the opening and closing comfort questions. Use a crop containing only the scale artwork.")]
    public Texture2D comfortScaleReferenceImage;

    [Header("Optional Image Assignments")]
    public Texture2D image1;
    public Texture2D image2;
    public Texture2D image3;

    [Header("World")]
    public Material panoramaMaterial;

    [Header("Synchronized Recording")]
    [Tooltip("Optional but recommended: writes gaze, physiology and experiment events as one session.")]
    public ExperimentRecordingSystem experimentRecorder;
    [Tooltip("Polar H10 bridge. Found automatically in the scene if left empty.")]
    public PhysiologyBridgeClient physiologyBridge;
    public string sessionId = "S001";

    private enum ExperimentState
    {
        Intro,
        Blank,
        Image,
        Questions,
        Complete,
        Setup,
        Instructions
    }

    [Serializable]
    private class GazeSample
    {
        public int trialIndex;
        public float trialTimeSeconds;
        public bool gazeValid;
        public string source;
        public Vector3 origin;
        public Vector3 direction;
        public Vector3 hitPoint;
        public float u;
        public float v;
    }

    private class TrialRecord
    {
        public readonly List<GazeSample> samples = new List<GazeSample>();
        public readonly List<string> answers = new List<string>();
    }

    private const int HeatmapWidth = 64;
    private const int HeatmapHeight = 32;
    private const int DefaultTrialCount = 3;

    private ExperimentState state;
    private int currentTrialIndex;
    private int currentQuestionIndex;
    private bool questionAnswered;
    private string participantId;
    private string startAnswer = "";
    private string finalComfortAnswer = "";
    private float imageStartTime;
    private float nextGazeSampleTime;
    private bool isRecordingGaze;
    private bool startupFailed;
    private string startupWarning = "";

    private readonly List<TrialRecord> trialRecords = new List<TrialRecord>();
    private Coroutine experimentCoroutine;
    private InputAction eyeGazePoseAction;

    private Camera xrCamera;
    private GameObject floor;
    private GameObject panoramaSphere;
    private Renderer panoramaRenderer;

    private Canvas uiCanvas;
    private GameObject uiPanel;
    private Image panelImage;
    private Text titleText;
    private Text bodyText;
    private Text statusText;
    private Text fixationText;
    private Transform contentRoot;
    private Button startButton;
    private Button nextButton;
    private readonly List<Button> desktopAnswerButtons = new List<Button>();
    private readonly List<Text> ratingTexts = new List<Text>();
    private readonly List<Text> ratingCheckmarks = new List<Text>();
    private string selectedRating = "";

    private Button readyButton;
    private Button instructionsStartButton;
    private float nextSetupRefreshTime;
    private bool editingBridgeIp;
    private float nextEyeDiagnosticLogTime;
    private string ipEntry = "";

    private string DataDirectory => Path.Combine(Application.persistentDataPath, "ExperimentData");
    private string ParticipantDirectory => Path.Combine(DataDirectory, participantId);
    private string CombinedDirectory => Path.Combine(DataDirectory, "Combined");

    private void Start()
    {
        Application.targetFrameRate = 72;
        participantId = GenerateParticipantId();

        try
        {
            EnsureConfiguration();
            xrCamera = ResolveMainCamera();
            if (experimentRecorder == null)
                experimentRecorder = GetComponent<ExperimentRecordingSystem>();
            if (experimentRecorder == null)
                experimentRecorder = gameObject.AddComponent<ExperimentRecordingSystem>();
            if (physiologyBridge == null)
                physiologyBridge = FindAnyObjectByType<PhysiologyBridgeClient>();
            if (physiologyBridge == null)
                physiologyBridge = gameObject.AddComponent<PhysiologyBridgeClient>();
            if (physiologyBridge != null && physiologyBridge.Recorder == null)
                physiologyBridge.Recorder = experimentRecorder;
            startupWarning = GetHeadsetCompatibilityWarning();

            // Create the diagnostic surface before optional world or input setup.
            CreateUserInterface();

            try
            {
                CreateWorld();
            }
            catch (Exception exception)
            {
                ShowStartupError("The experiment world could not be created.", exception);
                return;
            }

            try
            {
                eyeGazePoseAction = new InputAction("Eye Gaze Pose", InputActionType.Value, "<EyeGaze>/pose");
                eyeGazePoseAction.Enable();
            }
            catch (Exception exception)
            {
                startupWarning = AppendWarning(startupWarning,
                    "Eye tracking is unavailable on this headset. Head direction will be used instead. (" + exception.Message + ")");
                Debug.LogWarning("Eye gaze input is unavailable; using head direction fallback. " + exception, this);
            }

            ShowSetupCheck();
        }
        catch (Exception exception)
        {
            ShowStartupError("Startup failed before the experiment could begin.", exception);
        }
    }

    private string GetHeadsetCompatibilityWarning()
    {
        if (Application.platform != RuntimePlatform.Android)
            return "Desktop/editor mode: connect a supported Meta Quest headset before collecting data.";

        string model = SystemInfo.deviceModel ?? "";
        string normalizedModel = model.ToLowerInvariant().Replace(" ", "");
        bool looksLikeMetaQuest = normalizedModel.Contains("quest") || normalizedModel.Contains("oculus") ||
            normalizedModel.Contains("eureka") || normalizedModel.Contains("quest3s");

        if (looksLikeMetaQuest)
            return "Headset detected: " + model + ". Eye tracking is optional; head direction fallback is enabled.";

        if (string.IsNullOrEmpty(model))
            return "Unable to identify this headset. Supported target: Meta Quest 3S. Continue only if the XR view is working.";

        return "Unsupported or unverified headset detected: " + model + ". Supported target: Meta Quest 3S.";
    }

    private string AppendWarning(string currentWarning, string warning)
    {
        if (string.IsNullOrEmpty(currentWarning))
            return warning;
        return currentWarning + "\n" + warning;
    }

    private void ShowStartupError(string message, Exception exception)
    {
        startupFailed = true;
        state = ExperimentState.Complete;
        string details = exception == null ? "" : "\n\nTechnical details: " + exception.GetType().Name + " - " + exception.Message;
        Debug.LogError(message + details, this);

        if (uiPanel == null || titleText == null || bodyText == null || statusText == null)
            return;

        SetVisualState(true, false);
        ClearContent();
        titleText.text = "Experiment could not start";
        bodyText.text = message + details + "\n\nPlease close and relaunch the app. If this continues, send this screen to the researcher.";
        statusText.text = "Headset: " + (SystemInfo.deviceModel ?? "unknown");
    }

    private void OnGUI()
    {
        // Last-resort diagnostics if world-space UI creation itself fails.
        if (!startupFailed || uiPanel != null)
            return;

        GUI.color = Color.white;
        GUI.Label(new Rect(32f, 32f, Screen.width - 64f, Screen.height - 64f),
            "Experiment could not start.\n\nCheck the headset compatibility and XR runtime, then relaunch.\n\nHeadset: " +
            (SystemInfo.deviceModel ?? "unknown"));
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        EnsureConfiguration();
    }
#endif

    private Camera ResolveMainCamera()
    {
        Camera camera = Camera.main;
        if (camera == null)
            camera = FindAnyObjectByType<Camera>();

        if (camera == null)
        {
            GameObject cameraObject = new GameObject("Experiment Test Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 1.6f, 0f);
            camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.gray;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 1000f;
        }

        return camera;
    }

    private void Update()
    {
        HandleDesktopInput();

        if (state == ExperimentState.Setup && !editingBridgeIp && Time.unscaledTime >= nextSetupRefreshTime)
        {
            RefreshSetupCheck();
            nextSetupRefreshTime = Time.unscaledTime + 0.25f;
        }

        if (!isRecordingGaze || state != ExperimentState.Image)
            return;

        if (Time.unscaledTime >= nextGazeSampleTime)
        {
            RecordGazeSample();
            nextGazeSampleTime = Time.unscaledTime + gazeSampleIntervalSeconds;
        }
    }

    private void HandleDesktopInput()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        for (int i = 0; i < desktopAnswerButtons.Count && i < 9; i++)
        {
            if (GetNumberKey(keyboard, i + 1).wasPressedThisFrame)
            {
                desktopAnswerButtons[i].onClick.Invoke();
                return;
            }
        }

        if (state == ExperimentState.Instructions && instructionsStartButton != null && keyboard.enterKey.wasPressedThisFrame)
        {
            instructionsStartButton.onClick.Invoke();
            return;
        }

        if (state == ExperimentState.Setup && readyButton != null && keyboard.enterKey.wasPressedThisFrame)
        {
            readyButton.onClick.Invoke();
            return;
        }

        if (state == ExperimentState.Intro && startButton != null && startButton.interactable &&
            keyboard.enterKey.wasPressedThisFrame)
            startButton.onClick.Invoke();

        if (state == ExperimentState.Questions && nextButton != null && nextButton.interactable &&
            keyboard.enterKey.wasPressedThisFrame)
            nextButton.onClick.Invoke();
    }

    private KeyControl GetNumberKey(Keyboard keyboard, int number)
    {
        switch (number)
        {
            case 1: return keyboard.digit1Key;
            case 2: return keyboard.digit2Key;
            case 3: return keyboard.digit3Key;
            case 4: return keyboard.digit4Key;
            case 5: return keyboard.digit5Key;
            case 6: return keyboard.digit6Key;
            case 7: return keyboard.digit7Key;
            case 8: return keyboard.digit8Key;
            default: return keyboard.digit9Key;
        }
    }

    private void LateUpdate()
    {
        if (panoramaSphere != null && xrCamera != null)
            panoramaSphere.transform.position = xrCamera.transform.position;
    }

    private void OnDestroy()
    {
        if (eyeGazePoseAction != null)
        {
            eyeGazePoseAction.Disable();
            eyeGazePoseAction.Dispose();
        }
    }

    private void EnsureConfiguration()
    {
        NormalizeRatingOptions(startingQuestion);
        NormalizeRatingOptions(endingComfortQuestion);

        while (trials.Count < DefaultTrialCount)
        {
            int index = trials.Count + 1;
            trials.Add(new ExperimentTrialConfig
            {
                trialName = "Image " + index,
                image = GetFallbackImage(index),
                questions = CreateDefaultQuestions(index)
            });
        }

        for (int i = 0; i < trials.Count; i++)
        {
            if (trials[i].image == null)
                trials[i].image = GetFallbackImage(i + 1);
            if (trials[i].questions == null || trials[i].questions.Count == 0)
                trials[i].questions = CreateDefaultQuestions(i + 1);

        }
    }

    private void NormalizeRatingOptions(ExperimentQuestion question)
    {
        if (question == null)
            return;

        string[] normalizedOptions = new string[9];
        for (int i = 0; i < normalizedOptions.Length; i++)
        {
            if (question.options != null && i < question.options.Length && !string.IsNullOrWhiteSpace(question.options[i]))
                normalizedOptions[i] = question.options[i];
            else
                normalizedOptions[i] = (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        question.options = normalizedOptions;
    }

    private Texture2D GetFallbackImage(int imageNumber)
    {
        switch (imageNumber)
        {
            case 1: return image1;
            case 2: return image2;
            case 3: return image3;
            default: return null;
        }
    }

    private List<ExperimentQuestion> CreateDefaultQuestions(int imageNumber)
    {
        return new List<ExperimentQuestion>
        {
            new ExperimentQuestion { prompt = "How familiar was this place?", options = new[] { "Not familiar", "Slightly familiar", "Moderately familiar", "Very familiar", "Extremely familiar" } },
            new ExperimentQuestion { prompt = "How interesting was this place?", options = new[] { "Not interesting", "Slightly interesting", "Moderately interesting", "Very interesting", "Extremely interesting" } },
            new ExperimentQuestion { prompt = "How comfortable would you feel here?", options = new[] { "Not comfortable", "Slightly comfortable", "Moderately comfortable", "Very comfortable", "Extremely comfortable" } }
        };
    }

    private void CreateWorld()
    {
        floor = GameObject.Find("Floor") ?? GameObject.Find("Plane");
        if (floor == null)
        {
            floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = Vector3.one * 8f;
        }

        Renderer floorRenderer = floor.GetComponent<Renderer>();
        if (floorRenderer != null)
            floorRenderer.material.color = new Color(0.12f, 0.14f, 0.16f, 1f);

        if (FindAnyObjectByType<Light>() == null)
        {
            GameObject lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        panoramaSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        panoramaSphere.name = "Experiment Panorama Sphere";
        panoramaSphere.transform.localScale = new Vector3(-30f, 30f, 30f);

        Collider sphereCollider = panoramaSphere.GetComponent<Collider>();
        if (sphereCollider != null)
            Destroy(sphereCollider);

        panoramaRenderer = panoramaSphere.GetComponent<Renderer>();

        // A trial is viewed from inside this sphere.  Do not use the scene's
        // lit material here: on Quest, a back-face-culled lit sphere can be
        // completely invisible from its interior, which leaves only the black
        // camera clear colour during the image period.
        Shader panoramaShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
        if (panoramaShader == null)
            throw new InvalidOperationException("No compatible unlit panorama shader was found in the player build.");

        Material runtimePanoramaMaterial = new Material(panoramaShader);
        runtimePanoramaMaterial.name = "Runtime Panorama Material";
        if (runtimePanoramaMaterial.HasProperty("_Cull"))
            runtimePanoramaMaterial.SetFloat("_Cull", 0f); // Render both sides of the sphere.
        if (runtimePanoramaMaterial.HasProperty("_BaseColor"))
            runtimePanoramaMaterial.SetColor("_BaseColor", Color.white);
        if (runtimePanoramaMaterial.HasProperty("_Color"))
            runtimePanoramaMaterial.SetColor("_Color", Color.white);

        panoramaRenderer.material = runtimePanoramaMaterial;
        panoramaSphere.SetActive(false);
    }

    private void CreateUserInterface()
    {
        GameObject canvasObject = new GameObject("Experiment UI", typeof(RectTransform));
        uiCanvas = canvasObject.AddComponent<Canvas>();
        uiCanvas.renderMode = RenderMode.WorldSpace;
        uiCanvas.worldCamera = xrCamera;
        canvasObject.AddComponent<CanvasScaler>();
        canvasObject.AddComponent<GraphicRaycaster>();
        canvasObject.AddComponent<TrackedDeviceGraphicRaycaster>();

        if (xrCamera != null)
        {
            canvasObject.transform.SetParent(xrCamera.transform, false);
            canvasObject.transform.localPosition = new Vector3(0f, 0f, 1.8f);
            canvasObject.transform.localRotation = Quaternion.identity;
        }
        canvasObject.transform.localScale = Vector3.one * 0.00125f;

        uiPanel = CreateUiObject("Experiment Panel", canvasObject.transform);
        panelImage = uiPanel.AddComponent<Image>();
        panelImage.color = Color.white;
        RectTransform panelRect = uiPanel.GetComponent<RectTransform>();
        SetRect(panelRect, Vector2.zero, new Vector2(1100f, 900f));

        titleText = CreateText("Title", uiPanel.transform, 44, TextAnchor.MiddleCenter);
        SetRect(titleText.rectTransform, new Vector2(0f, 360f), new Vector2(1000f, 80f));

        bodyText = CreateText("Body", uiPanel.transform, 30, TextAnchor.MiddleCenter);
        SetRect(bodyText.rectTransform, new Vector2(0f, 245f), new Vector2(960f, 150f));

        statusText = CreateText("Status", uiPanel.transform, 22, TextAnchor.MiddleCenter);
        SetRect(statusText.rectTransform, new Vector2(0f, -395f), new Vector2(960f, 80f));

        fixationText = CreateText("Fixation Cross", uiPanel.transform, 60, TextAnchor.MiddleCenter);
        SetRect(fixationText.rectTransform, Vector2.zero, new Vector2(240f, 240f));
        fixationText.text = "x";
        fixationText.gameObject.SetActive(false);

        GameObject contentObject = CreateUiObject("Content", uiPanel.transform);
        contentRoot = contentObject.transform;
        RectTransform contentRect = contentObject.GetComponent<RectTransform>();
        SetRect(contentRect, new Vector2(0f, -75f), new Vector2(960f, 460f));
    }

    private GameObject CreateUiObject(string objectName, Transform parent)
    {
        GameObject uiObject = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer));
        uiObject.transform.SetParent(parent, false);
        return uiObject;
    }

    private Text CreateText(string objectName, Transform parent, int fontSize, TextAnchor alignment)
    {
        GameObject textObject = CreateUiObject(objectName, parent);
        Text text = textObject.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.color = Color.black;
        text.alignment = alignment;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private void SetRect(RectTransform rect, Vector2 anchoredPosition, Vector2 size)
    {
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
    }

    private string GenerateParticipantId()
    {
        try
        {
            int highest = 0;
            if (Directory.Exists(DataDirectory))
            {
                foreach (string directory in Directory.GetDirectories(DataDirectory))
                {
                    string name = Path.GetFileName(directory);
                    int number;
                    if (name.Length > 1 && name[0] == 'P' &&
                        int.TryParse(name.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                        highest = Mathf.Max(highest, number);
                }
            }
            return "P" + (highest + 1).ToString("000", CultureInfo.InvariantCulture);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not scan existing participants; using a timestamp ID. " + exception, this);
            return "P_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        }
    }

    private void ShowSetupCheck()
    {
        if (startupFailed)
            return;

        SetFixationScreen(false);
        SetQuestionPanelStyle(false);
        state = ExperimentState.Setup;
        SetVisualState(true, false);
        ClearContent();
        editingBridgeIp = false;
        titleText.text = "Participant " + participantId + " - System Check";
        readyButton = CreateButton("Ready", contentRoot, new Vector2(0f, -105f), new Vector2(500f, 70f), OnSetupReady);
        if (physiologyBridge != null)
            CreateButton("Change laptop IP", contentRoot, new Vector2(0f, -185f), new Vector2(500f, 56f), ShowIpEditor);
        RefreshSetupCheck();
    }

    private void ShowIpEditor()
    {
        editingBridgeIp = true;
        readyButton = null;
        ipEntry = physiologyBridge.LaptopIpAddress;
        BuildIpEditor();
    }

    private void BuildIpEditor()
    {
        ClearContent();
        titleText.text = "Laptop IP address";
        bodyText.text = string.IsNullOrEmpty(ipEntry) ? "(empty)" : ipEntry;
        statusText.text = "Type the address shown in the StartSession window on the laptop.";

        string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", "<" };
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            Vector2 position = new Vector2(-170f + (i % 3) * 170f, 190f - (i / 3) * 72f);
            CreateButton(key == "<" ? "Delete" : key, contentRoot, position, new Vector2(155f, 62f), () =>
            {
                if (key == "<") { if (ipEntry.Length > 0) ipEntry = ipEntry.Substring(0, ipEntry.Length - 1); }
                else if (ipEntry.Length < 15) ipEntry += key;
                bodyText.text = string.IsNullOrEmpty(ipEntry) ? "(empty)" : ipEntry;
            });
        }
        CreateButton("Save", contentRoot, new Vector2(-130f, -125f), new Vector2(250f, 62f), () =>
        {
            System.Net.IPAddress parsed;
            if (!System.Net.IPAddress.TryParse(ipEntry, out parsed) || ipEntry.Split('.').Length != 4)
            {
                statusText.text = "That is not a valid address. Example: 192.168.1.106";
                return;
            }
            physiologyBridge.LaptopIpAddress = ipEntry;
            ShowSetupCheck();
        });
        CreateButton("Cancel", contentRoot, new Vector2(130f, -125f), new Vector2(250f, 62f), ShowSetupCheck);
    }

    private void RefreshSetupCheck()
    {
        const string ok = "<color=#0A7D2C>OK</color>";
        string heartLine;
        if (physiologyBridge == null)
            heartLine = "Heart rate: <color=#B00020>Polar bridge not in scene</color>";
        else if (physiologyBridge.LastPhysiologyReceivedTime >= 0f &&
                 Time.unscaledTime - physiologyBridge.LastPhysiologyReceivedTime < 5f &&
                 !double.IsNaN(physiologyBridge.LastHeartRateBpm))
            heartLine = physiologyBridge.LastHeartRateBpm < 30
                ? "Heart rate: <color=#B00020>NO SKIN CONTACT</color> (wet the strap and tighten it)"
                : "Heart rate: " + ok + " - " + physiologyBridge.LastHeartRateBpm.ToString("0", CultureInfo.InvariantCulture) + " bpm";
        else
            heartLine = "Heart rate: <color=#B00020>NO DATA</color> (" + physiologyBridge.Status + ")";

        string clockLine = physiologyBridge != null && physiologyBridge.IsClockSynchronized
            ? "Clock sync: " + ok
            : "Clock sync: <color=#B00020>waiting</color>";

        string eyeLine;
        Vector3 unusedPosition; Quaternion unusedRotation; string eyeDiagnostic;
        bool eyeOk = TryReadEyeGaze(out unusedPosition, out unusedRotation, out eyeDiagnostic);
        if (eyeGazePoseAction == null || !eyeGazePoseAction.enabled)
            eyeLine = "Eye tracking: <color=#B00020>NOT AVAILABLE</color> (head direction will be used)";
        else if (eyeOk)
            eyeLine = "Eye tracking: " + ok;
        else
            eyeLine = "Eye tracking: <color=#B00020>NOT TRACKED</color> (" + eyeDiagnostic + ")";
        if (Time.unscaledTime >= nextEyeDiagnosticLogTime)
        {
            Debug.Log("[EyeDiag] ok=" + eyeOk + " " + eyeDiagnostic, this);
            nextEyeDiagnosticLogTime = Time.unscaledTime + 1f;
        }

        string ipLine = physiologyBridge != null ? "\nLaptop IP: " + physiologyBridge.LaptopIpAddress : "";
        bodyText.text = heartLine + "\n" + clockLine + "\n" + eyeLine + ipLine;
        bool allOk = !heartLine.Contains("B00020") && !clockLine.Contains("B00020") && !eyeLine.Contains("B00020");
        statusText.text = (allOk
            ? "All checks passed. Press Ready to continue. Nothing on this page is saved."
            : "Do NOT press Ready yet: fix the items in red first (see the instructions sheet).") +
            (string.IsNullOrEmpty(startupWarning) || startupWarning.StartsWith("Headset detected") ? "" : "\n" + startupWarning);
    }

    private void OnSetupReady()
    {
        readyButton = null;
        if (showInstructions)
            ShowInstructions();
        else
            BeginAfterInstructions();
    }

    private void ShowInstructions()
    {
        SetFixationScreen(false);
        SetQuestionPanelStyle(false);
        state = ExperimentState.Instructions;
        SetVisualState(true, false);
        ClearContent();
        titleText.text = instructionsTitle;
        bodyText.text = "";
        statusText.text = "";

        Text instructions = CreateText("Instructions Text", contentRoot, 30, TextAnchor.UpperLeft);
        instructions.text = instructionsText;
        instructions.supportRichText = true;
        instructions.verticalOverflow = VerticalWrapMode.Truncate;
        instructions.resizeTextForBestFit = true;
        instructions.resizeTextMinSize = 16;
        instructions.resizeTextMaxSize = 30;
        SetRect(instructions.rectTransform, new Vector2(0f, 110f), new Vector2(1000f, 520f));

        instructionsStartButton = CreateButton(instructionsButtonLabel, contentRoot, new Vector2(0f, -215f),
            new Vector2(500f, 70f), () =>
            {
                instructionsStartButton = null;
                BeginAfterInstructions();
            });
    }

    private void BeginAfterInstructions()
    {
        if (askComfortQuestions)
            ShowIntro();
        else
            StartExperiment();
    }

    private void ExitApplication()
    {
        experimentRecorder?.CloseSession();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void ShowIntro()
    {
        if (startupFailed)
            return;

        SetFixationScreen(false);
        state = ExperimentState.Intro;
        SetVisualState(true, false);
        ShowComfortQuestion(startingQuestion, true, "Start Experiment", StartExperiment);
        statusText.text = startupWarning;
    }

    private void StartExperiment()
    {
        if (experimentCoroutine != null || (askComfortQuestions && string.IsNullOrEmpty(startAnswer)))
            return;

        finalComfortAnswer = "";
        trialRecords.Clear();
        for (int i = 0; i < trials.Count; i++)
            trialRecords.Add(new TrialRecord());

        experimentRecorder?.BeginSession(participantId, sessionId, ParticipantDirectory);
        if (askComfortQuestions)
            experimentRecorder?.LogEvent("StartingComfortAnswered", "", startAnswer);

        experimentCoroutine = StartCoroutine(RunExperiment());
    }

    private IEnumerator RunExperiment()
    {
        for (currentTrialIndex = 0; currentTrialIndex < trials.Count; currentTrialIndex++)
        {
            experimentRecorder?.SetTrial((currentTrialIndex + 1).ToString(CultureInfo.InvariantCulture), trials[currentTrialIndex].trialName);
            experimentRecorder?.OpenPhysiologyWindow();
            yield return RunFixationPeriod("before_image");
            yield return RunImagePeriod(currentTrialIndex);
            yield return RunFixationPeriod("after_image");
            experimentRecorder?.ClosePhysiologyWindow();
            yield return RunQuestionPeriod(currentTrialIndex);
        }

        if (askComfortQuestions)
            yield return RunFinalComfortQuestion();
        SaveExperimentData();
        experimentRecorder?.LogEvent("ExperimentComplete", "", "legacy_gaze_export_saved");
        experimentRecorder?.CloseSession();
        SetFixationScreen(false);
        SetQuestionPanelStyle(false);
        state = ExperimentState.Complete;
        SetVisualState(true, false);
        ClearContent();
        titleText.text = "Experiment Complete";
        bodyText.text = "Thank you. Your gaze data and responses have been saved.";
        statusText.text = "Files saved in: " + ParticipantDirectory;
        CreateButton("Exit", contentRoot, new Vector2(0f, -120f), new Vector2(500f, 70f), ExitApplication);
        experimentCoroutine = null;
    }

    private IEnumerator RunFixationPeriod(string marker = "")
    {
        state = ExperimentState.Blank;
        SetQuestionPanelStyle(false);
        SetVisualState(true, false);
        SetFixationScreen(true);
        experimentRecorder?.LogEvent("FixationShown", "", marker);
        yield return new WaitForSecondsRealtime(blankDurationSeconds);
        experimentRecorder?.LogEvent("FixationHidden", "", marker);
    }

    private IEnumerator RunImagePeriod(int trialIndex)
    {
        state = ExperimentState.Image;
        SetFixationScreen(false);
        Texture2D image = trials[trialIndex].image;
        if (image != null)
        {
            Material material = panoramaRenderer.material;
            material.mainTexture = image;
            if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", image);
            if (material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", image);
        }
        else
        {
            Debug.LogWarning("No equirectangular image assigned for " + trials[trialIndex].trialName + ".", this);
        }

        SetVisualState(false, true);
        experimentRecorder?.MarkStimulusStart();
        experimentRecorder?.LogEvent("StimulusShown", trials[trialIndex].trialName, "image_start_marker");
        imageStartTime = Time.unscaledTime;
        nextGazeSampleTime = imageStartTime;
        isRecordingGaze = true;
        yield return new WaitForSecondsRealtime(imageDurationSeconds);
        isRecordingGaze = false;
        experimentRecorder?.MarkStimulusEnd();
        experimentRecorder?.LogEvent("StimulusHidden", trials[trialIndex].trialName, "image_end_marker");
        SetVisualState(false, false);
    }

    private IEnumerator RunQuestionPeriod(int trialIndex)
    {
        state = ExperimentState.Questions;
        SetVisualState(true, false);
        TrialRecord record = trialRecords[trialIndex];
        currentQuestionIndex = 0;
        questionAnswered = false;
        selectedRating = "";
        ClearContent();
        ShowCurrentQuestion(trialIndex);

        while (!questionAnswered)
            yield return null;

        experimentRecorder?.EndTrial(trials[trialIndex].trialName);
    }

    private IEnumerator RunFinalComfortQuestion()
    {
        state = ExperimentState.Questions;
        SetVisualState(true, false);
        questionAnswered = false;
        ShowComfortQuestion(endingComfortQuestion, false, "Finish Study", () => questionAnswered = true);

        while (!questionAnswered)
            yield return null;
    }

    private void ShowCurrentQuestion(int trialIndex)
    {
        SetFixationScreen(false);
        SetQuestionPanelStyle(false);
        ExperimentTrialConfig trial = trials[trialIndex];
        ExperimentQuestion question = trial.questions[currentQuestionIndex];
        titleText.text = trial.trialName + " - Question " + (currentQuestionIndex + 1) + "/" + trial.questions.Count;
        bodyText.text = question.prompt;
        statusText.text = "Select an answer, then press Next.";
        ClearContent();
        selectedRating = "";

        float startY = 150f;
        for (int i = 0; i < question.options.Length; i++)
        {
            string option = question.options[i];
            Button answerButton = CreateButton(option, contentRoot, new Vector2(0f, startY - i * 70f), new Vector2(860f, 58f), () =>
            {
                selectedRating = option;
                if (nextButton != null)
                    nextButton.interactable = true;
                statusText.text = "Answer selected. Press Next to continue.";
            });
            desktopAnswerButtons.Add(answerButton);
        }

        float nextY = startY - question.options.Length * 70f - 25f;
        nextButton = CreateButton(currentQuestionIndex == trial.questions.Count - 1 ? "Next Image" : "Next", contentRoot,
            new Vector2(0f, nextY), new Vector2(500f, 70f), () => OnQuestionAnswer(trialIndex, selectedRating));
        nextButton.interactable = false;
    }

    private void ShowComfortQuestion(ExperimentQuestion comfortQuestion, bool isStartingQuestion, string buttonLabel,
        UnityEngine.Events.UnityAction continueAction)
    {
        SetFixationScreen(false);
        SetQuestionPanelStyle(true);
        titleText.text = comfortQuestion.prompt;
        bodyText.text = "";
        statusText.text = "";
        ClearContent();
        selectedRating = "";

        CreateScaleEndpointLabel(comfortQuestion.options[0], contentRoot, new Vector2(-405f, 165f), TextAnchor.MiddleLeft);
        CreateScaleEndpointLabel(comfortQuestion.options[comfortQuestion.options.Length - 1], contentRoot, new Vector2(405f, 165f), TextAnchor.MiddleRight);
        bool showFallbackScale = CreateComfortScaleArtwork(contentRoot);

        float scaleWidth = 760f;
        Button continueButton = null;
        for (int i = 0; i < comfortQuestion.options.Length; i++)
        {
            string option = comfortQuestion.options[i];
            int optionIndex = i;
            float pointPosition = comfortQuestion.options.Length == 1
                ? 0f
                : -scaleWidth * 0.5f + scaleWidth * optionIndex / (comfortQuestion.options.Length - 1);
            Button answerButton = CreateHorizontalRatingButton((optionIndex + 1).ToString(CultureInfo.InvariantCulture), option,
                contentRoot, new Vector2(pointPosition, 45f), showFallbackScale, answer =>
            {
                selectedRating = answer;
                for (int textIndex = 0; textIndex < ratingTexts.Count; textIndex++)
                    ratingTexts[textIndex].color = textIndex == optionIndex ? new Color(0.05f, 0.3f, 0.75f, 1f) : Color.black;
                for (int checkmarkIndex = 0; checkmarkIndex < ratingCheckmarks.Count; checkmarkIndex++)
                    ratingCheckmarks[checkmarkIndex].gameObject.SetActive(checkmarkIndex == optionIndex);
                if (isStartingQuestion)
                    startAnswer = answer;
                else
                    finalComfortAnswer = answer;
                if (continueButton != null)
                    continueButton.interactable = true;
                statusText.text = "Rating selected.";
            });
            desktopAnswerButtons.Add(answerButton);
        }

        continueButton = CreateButton(buttonLabel, contentRoot, new Vector2(0f, -190f), new Vector2(860f, 62f), continueAction);
        continueButton.interactable = false;
        if (isStartingQuestion)
            startButton = continueButton;
        else
            nextButton = continueButton;
    }

    private void SetQuestionPanelStyle(bool isQuestionScreen)
    {
        if (panelImage != null)
            panelImage.color = isQuestionScreen ? new Color(0.76f, 0.76f, 0.76f, 1f) : Color.white;
    }

    private void CreateScaleEndpointLabel(string label, Transform parent, Vector2 position, TextAnchor alignment)
    {
        Text endpoint = CreateText("Scale Endpoint", parent, 24, alignment);
        endpoint.text = label;
        endpoint.fontStyle = FontStyle.Bold;
        SetRect(endpoint.rectTransform, position, new Vector2(300f, 48f));
    }

    private void OnQuestionAnswer(int trialIndex, string answer)
    {
        TrialRecord record = trialRecords[trialIndex];
        record.answers.Add(answer);
        experimentRecorder?.LogEvent("UserInteraction", trials[trialIndex].trialName, "question=" + (currentQuestionIndex + 1) + ";answer=" + answer);
        currentQuestionIndex++;

        if (currentQuestionIndex >= trials[trialIndex].questions.Count)
        {
            questionAnswered = true;
            return;
        }

        ShowCurrentQuestion(trialIndex);
    }

    private Button CreateRatingButton(string label, Transform parent, float y, UnityEngine.Events.UnityAction<string> callback)
    {
        GameObject buttonObject = CreateUiObject("Rating - " + label, parent);
        Image image = buttonObject.AddComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0f);
        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.colors = new ColorBlock
        {
            normalColor = new Color(1f, 1f, 1f, 0f),
            highlightedColor = new Color(0.9f, 0.95f, 1f, 0.5f),
            pressedColor = new Color(0.8f, 0.9f, 1f, 0.7f),
            selectedColor = new Color(0.9f, 0.95f, 1f, 0.5f),
            disabledColor = new Color(1f, 1f, 1f, 0f),
            colorMultiplier = 1f,
            fadeDuration = 0.1f
        };
        button.onClick.AddListener(() => callback(label));
        SetRect(buttonObject.GetComponent<RectTransform>(), new Vector2(0f, y), new Vector2(860f, 40f));

        Text text = CreateText("Bullet Label", buttonObject.transform, 28, TextAnchor.MiddleLeft);
        text.text = "• " + label;
        SetRect(text.rectTransform, new Vector2(-330f, 0f), new Vector2(640f, 40f));
        ratingTexts.Add(text);
        return button;
    }

    private bool CreateComfortScaleArtwork(Transform parent)
    {
        if (comfortScaleReferenceImage != null)
        {
            GameObject artworkObject = CreateUiObject("Comfort Scale Artwork", parent);
            RawImage artwork = artworkObject.AddComponent<RawImage>();
            artwork.texture = comfortScaleReferenceImage;
            artwork.color = Color.white;
            SetRect(artworkObject.GetComponent<RectTransform>(), new Vector2(0f, 45f), new Vector2(860f, 100f));
            return false;
        }

        GameObject railObject = CreateUiObject("Rating Scale Rail", parent);
        Image rail = railObject.AddComponent<Image>();
        rail.color = Color.white;
        SetRect(railObject.GetComponent<RectTransform>(), new Vector2(0f, 45f), new Vector2(860f, 100f));
        return true;
    }

    private Button CreateHorizontalRatingButton(string displayLabel, string answer, Transform parent, Vector2 position,
        bool showScaleVisual, UnityEngine.Events.UnityAction<string> callback)
    {
        GameObject buttonObject = CreateUiObject("Rating Point - " + displayLabel, parent);
        Image target = buttonObject.AddComponent<Image>();
        target.color = new Color(1f, 1f, 1f, 0f);
        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = target;
        button.colors = new ColorBlock
        {
            normalColor = new Color(1f, 1f, 1f, 0f),
            highlightedColor = new Color(0.9f, 0.95f, 1f, 0.4f),
            pressedColor = new Color(0.8f, 0.9f, 1f, 0.65f),
            selectedColor = new Color(0.9f, 0.95f, 1f, 0.4f),
            disabledColor = new Color(1f, 1f, 1f, 0f),
            colorMultiplier = 1f,
            fadeDuration = 0.1f
        };
        button.onClick.AddListener(() => callback(answer));
        SetRect(buttonObject.GetComponent<RectTransform>(), position, new Vector2(74f, 170f));

        if (showScaleVisual)
        {
            GameObject tileObject = CreateUiObject("Rating Tile", buttonObject.transform);
            Image tile = tileObject.AddComponent<Image>();
            tile.color = Color.white;
            Outline tileOutline = tileObject.AddComponent<Outline>();
            tileOutline.effectColor = Color.black;
            tileOutline.effectDistance = new Vector2(2f, -2f);
            SetRect(tileObject.GetComponent<RectTransform>(), new Vector2(0f, 12f), new Vector2(66f, 56f));

            Text marker = CreateText("Scale Dot", tileObject.transform, 34, TextAnchor.MiddleCenter);
            marker.text = "●";
            SetRect(marker.rectTransform, Vector2.zero, new Vector2(52f, 46f));

            Text text = CreateText("Scale Label", buttonObject.transform, 24, TextAnchor.MiddleCenter);
            text.text = displayLabel;
            text.fontStyle = FontStyle.Bold;
            SetRect(text.rectTransform, new Vector2(0f, -28f), new Vector2(60f, 32f));
            ratingTexts.Add(text);
        }

        GameObject checkboxObject = CreateUiObject("Checkbox", buttonObject.transform);
        Image checkbox = checkboxObject.AddComponent<Image>();
        checkbox.color = Color.white;
        Outline checkboxOutline = checkboxObject.AddComponent<Outline>();
        checkboxOutline.effectColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        checkboxOutline.effectDistance = new Vector2(1f, -1f);
        SetRect(checkboxObject.GetComponent<RectTransform>(), new Vector2(0f, -76f), new Vector2(20f, 20f));

        Text checkmark = CreateText("Checkmark", checkboxObject.transform, 20, TextAnchor.MiddleCenter);
        checkmark.text = "x";
        SetRect(checkmark.rectTransform, Vector2.zero, new Vector2(20f, 20f));
        checkmark.gameObject.SetActive(false);
        ratingCheckmarks.Add(checkmark);
        return button;
    }

    private Button CreateButton(string label, Transform parent, Vector2 position, Vector2 size, UnityEngine.Events.UnityAction callback)
    {
        GameObject buttonObject = CreateUiObject("Button - " + label, parent);
        Image image = buttonObject.AddComponent<Image>();
        image.color = Color.white;
        Outline outline = buttonObject.AddComponent<Outline>();
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(2f, -2f);
        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.colors = new ColorBlock
        {
            normalColor = Color.white,
            highlightedColor = new Color(0.9f, 0.9f, 0.9f, 1f),
            pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f),
            selectedColor = new Color(0.9f, 0.9f, 0.9f, 1f),
            disabledColor = new Color(0.8f, 0.8f, 0.8f, 1f),
            colorMultiplier = 1f,
            fadeDuration = 0.1f
        };
        button.onClick.AddListener(callback);

        RectTransform rect = buttonObject.GetComponent<RectTransform>();
        SetRect(rect, position, size);

        Text text = CreateText("Label", buttonObject.transform, 28, TextAnchor.MiddleCenter);
        text.text = label;
        SetRect(text.rectTransform, Vector2.zero, size - new Vector2(24f, 8f));
        return button;
    }

    private void ClearContent()
    {
        if (contentRoot == null)
            return;

        for (int i = contentRoot.childCount - 1; i >= 0; i--)
            Destroy(contentRoot.GetChild(i).gameObject);
        desktopAnswerButtons.Clear();
        ratingTexts.Clear();
        ratingCheckmarks.Clear();
        startButton = null;
        nextButton = null;
    }

    private void SetFixationScreen(bool showFixation)
    {
        if (fixationText != null)
            fixationText.gameObject.SetActive(showFixation);
        if (titleText != null)
            titleText.gameObject.SetActive(!showFixation);
        if (bodyText != null)
            bodyText.gameObject.SetActive(!showFixation);
        if (statusText != null)
            statusText.gameObject.SetActive(!showFixation);

        if (showFixation)
            ClearContent();
    }

    private void SetVisualState(bool showUi, bool showPanorama)
    {
        if (xrCamera != null)
        {
            xrCamera.clearFlags = CameraClearFlags.SolidColor;
            xrCamera.backgroundColor = showUi ? Color.white : Color.black;
        }
        if (uiPanel != null)
            uiPanel.SetActive(showUi);
        if (panoramaSphere != null)
            panoramaSphere.SetActive(showPanorama);
        if (floor != null)
        {
            Renderer floorRenderer = floor.GetComponent<Renderer>();
            if (floorRenderer != null)
                floorRenderer.enabled = false;
        }
    }

    // Meta's runtime can deliver a valid gaze rotation without setting isTracked, so accept either signal.
    private bool TryReadEyeGaze(out Vector3 position, out Quaternion rotation, out string diagnostic)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (eyeGazePoseAction == null || !eyeGazePoseAction.enabled)
        {
            diagnostic = "no eye action";
            return false;
        }
        if (eyeGazePoseAction.controls.Count == 0)
        {
            diagnostic = "eye device not found";
            return false;
        }

        PoseState pose = eyeGazePoseAction.ReadValue<PoseState>();
        bool rotationValid = (pose.trackingState & UnityEngine.XR.InputTrackingState.Rotation) != 0;
        diagnostic = "tracked=" + pose.isTracked + " state=" + pose.trackingState;
        if (!pose.isTracked && !rotationValid)
            return false;

        position = pose.position;
        rotation = pose.rotation;
        return true;
    }

    private void RecordGazeSample()
    {
        if (xrCamera == null || panoramaSphere == null)
            return;

        Vector3 origin = xrCamera.transform.position;
        Vector3 direction = xrCamera.transform.forward;
        string source = "head_fallback";
        bool gazeValid = false;

        Vector3 gazePosition; Quaternion gazeRotation; string unusedDiagnostic;
        if (TryReadEyeGaze(out gazePosition, out gazeRotation, out unusedDiagnostic))
        {
            origin = gazePosition;
            direction = gazeRotation * Vector3.forward;
            source = "eye_gaze";
            gazeValid = true;
        }

        direction.Normalize();
        Vector3 sphereCenter = panoramaSphere.transform.position;
        Vector3 localDirection = Quaternion.Inverse(panoramaSphere.transform.rotation) * direction;
        float u = Mathf.Repeat(0.5f + Mathf.Atan2(localDirection.x, localDirection.z) / (2f * Mathf.PI), 1f);
        float v = Mathf.Clamp01(0.5f - Mathf.Asin(Mathf.Clamp(localDirection.y, -1f, 1f)) / Mathf.PI);
        Vector3 hitPoint = sphereCenter + direction * 15f;
        string hitObject = panoramaSphere.name;
        int hitInstanceId = panoramaSphere.GetInstanceID();
        RaycastHit raycastHit;
        if (Physics.Raycast(origin, direction, out raycastHit, 100f, ~0, QueryTriggerInteraction.Ignore))
        {
            hitPoint = raycastHit.point;
            hitObject = raycastHit.collider.gameObject.name;
            hitInstanceId = raycastHit.collider.gameObject.GetInstanceID();
        }

        trialRecords[currentTrialIndex].samples.Add(new GazeSample
        {
            trialIndex = currentTrialIndex + 1,
            trialTimeSeconds = Time.unscaledTime - imageStartTime,
            gazeValid = gazeValid,
            source = source,
            origin = origin,
            direction = direction,
            hitPoint = hitPoint,
            u = u,
            v = v
        });
        experimentRecorder?.LogGaze(gazeValid, source, origin, direction, hitPoint, hitObject, hitInstanceId);
    }

    private void SaveExperimentData()
    {
        Directory.CreateDirectory(ParticipantDirectory);
        Directory.CreateDirectory(CombinedDirectory);
        string gazePath = Path.Combine(ParticipantDirectory, participantId + "_gaze.csv");
        StringBuilder csv = new StringBuilder();
        int questionColumnCount = GetMaxQuestionCount();
        csv.AppendLine(BuildGazeCsvHeader(questionColumnCount));

        for (int i = 0; i < trialRecords.Count; i++)
        {
            TrialRecord record = trialRecords[i];
            foreach (GazeSample sample in record.samples)
            {
                csv.Append(Csv(participantId)).Append(',');
                csv.Append(Csv(startAnswer)).Append(',');
                csv.Append(Csv(finalComfortAnswer)).Append(',');
                csv.Append(sample.trialIndex).Append(',');
                csv.Append(Csv(trials[i].trialName)).Append(',');
                csv.Append(F(sample.trialTimeSeconds)).Append(',');
                csv.Append(sample.gazeValid ? "1" : "0").Append(',');
                csv.Append(Csv(sample.source)).Append(',');
                csv.Append(F(sample.origin.x)).Append(',').Append(F(sample.origin.y)).Append(',').Append(F(sample.origin.z)).Append(',');
                csv.Append(F(sample.direction.x)).Append(',').Append(F(sample.direction.y)).Append(',').Append(F(sample.direction.z)).Append(',');
                csv.Append(F(sample.hitPoint.x)).Append(',').Append(F(sample.hitPoint.y)).Append(',').Append(F(sample.hitPoint.z)).Append(',');
                csv.Append(F(sample.u)).Append(',').Append(F(sample.v));
                for (int answerIndex = 0; answerIndex < questionColumnCount; answerIndex++)
                {
                    csv.Append(',');
                    if (answerIndex < record.answers.Count)
                        csv.Append(Csv(record.answers[answerIndex]));
                }
                csv.AppendLine();
            }
        }

        File.WriteAllText(gazePath, csv.ToString(), Encoding.UTF8);
        AppendToCombinedCsv(csv.ToString());
        SaveHeatmaps();
        File.WriteAllText(Path.Combine(ParticipantDirectory, participantId + "_responses.csv"), BuildResponsesCsv(), Encoding.UTF8);
        Debug.Log("Experiment data saved to " + ParticipantDirectory, this);
    }

    private void AppendToCombinedCsv(string participantCsv)
    {
        string path = Path.Combine(CombinedDirectory, "combined_gaze.csv");
        bool needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        int headerEnd = participantCsv.IndexOf('\n');
        string rows = headerEnd >= 0 ? participantCsv.Substring(headerEnd + 1) : "";
        using (StreamWriter writer = new StreamWriter(path, true, Encoding.UTF8))
        {
            if (needsHeader)
                writer.WriteLine(BuildGazeCsvHeader(GetMaxQuestionCount()));
            writer.Write(rows);
        }
    }

    private int GetMaxQuestionCount()
    {
        int maxQuestionCount = 0;
        for (int i = 0; i < trials.Count; i++)
        {
            if (trials[i].questions != null)
                maxQuestionCount = Mathf.Max(maxQuestionCount, trials[i].questions.Count);
        }
        return maxQuestionCount;
    }

    private string BuildGazeCsvHeader(int questionColumnCount)
    {
        StringBuilder header = new StringBuilder("participant_id,starting_comfort_answer,ending_comfort_answer,trial_index,image_name,trial_time_seconds,gaze_valid,gaze_source,origin_x,origin_y,origin_z,direction_x,direction_y,direction_z,hit_x,hit_y,hit_z,pano_u,pano_v");
        for (int questionIndex = 0; questionIndex < questionColumnCount; questionIndex++)
            header.Append(",question_").Append(questionIndex + 1);
        return header.ToString();
    }

    private string BuildResponsesCsv()
    {
        StringBuilder csv = new StringBuilder("participant_id,starting_comfort_answer,ending_comfort_answer,trial_index,image_name,question_index,answer\n");
        for (int i = 0; i < trialRecords.Count; i++)
        {
            for (int q = 0; q < trialRecords[i].answers.Count; q++)
            {
                csv.Append(Csv(participantId)).Append(',').Append(Csv(startAnswer)).Append(',').Append(Csv(finalComfortAnswer)).Append(',').Append(i + 1).Append(',');
                csv.Append(Csv(trials[i].trialName)).Append(',').Append(q + 1).Append(',').Append(Csv(trialRecords[i].answers[q])).AppendLine();
            }
        }
        return csv.ToString();
    }

    private void SaveHeatmaps()
    {
        for (int trialIndex = 0; trialIndex < trialRecords.Count; trialIndex++)
        {
            int[] participantCounts = BuildHeatmapCounts(trialRecords[trialIndex].samples);
            string participantBase = Path.Combine(ParticipantDirectory, participantId + "_image_" + (trialIndex + 1) + "_heatmap");
            WriteHeatmapCsv(participantBase + ".csv", participantCounts);
            WriteHeatmapPng(participantBase + ".png", participantCounts);

            string combinedBase = Path.Combine(CombinedDirectory, "combined_image_" + (trialIndex + 1) + "_heatmap");
            int[] combinedCounts = File.Exists(combinedBase + ".csv") ? ReadHeatmapCsv(combinedBase + ".csv") : new int[HeatmapWidth * HeatmapHeight];
            for (int i = 0; i < combinedCounts.Length; i++)
                combinedCounts[i] += participantCounts[i];
            WriteHeatmapCsv(combinedBase + ".csv", combinedCounts);
            WriteHeatmapPng(combinedBase + ".png", combinedCounts);
        }
    }

    private int[] BuildHeatmapCounts(List<GazeSample> samples)
    {
        int[] counts = new int[HeatmapWidth * HeatmapHeight];
        foreach (GazeSample sample in samples)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(sample.u * HeatmapWidth), 0, HeatmapWidth - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(sample.v * HeatmapHeight), 0, HeatmapHeight - 1);
            counts[y * HeatmapWidth + x]++;
        }
        return counts;
    }

    private void WriteHeatmapCsv(string path, int[] counts)
    {
        StringBuilder csv = new StringBuilder("u_index,v_index,count\n");
        for (int y = 0; y < HeatmapHeight; y++)
            for (int x = 0; x < HeatmapWidth; x++)
                csv.Append(x).Append(',').Append(y).Append(',').Append(counts[y * HeatmapWidth + x]).AppendLine();
        File.WriteAllText(path, csv.ToString(), Encoding.UTF8);
    }

    private int[] ReadHeatmapCsv(string path)
    {
        int[] counts = new int[HeatmapWidth * HeatmapHeight];
        string[] lines = File.ReadAllLines(path);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] parts = lines[i].Split(',');
            if (parts.Length != 3) continue;
            int x, y, count;
            if (int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y) && int.TryParse(parts[2], out count) && x >= 0 && x < HeatmapWidth && y >= 0 && y < HeatmapHeight)
                counts[y * HeatmapWidth + x] = count;
        }
        return counts;
    }

    private void WriteHeatmapPng(string path, int[] counts)
    {
        const int outputWidth = 256;
        const int outputHeight = 128;
        int maxCount = 1;
        foreach (int count in counts)
            maxCount = Mathf.Max(maxCount, count);

        Texture2D texture = new Texture2D(outputWidth, outputHeight, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[outputWidth * outputHeight];
        for (int y = 0; y < outputHeight; y++)
        {
            for (int x = 0; x < outputWidth; x++)
            {
                int gridX = x * HeatmapWidth / outputWidth;
                int gridY = y * HeatmapHeight / outputHeight;
                float intensity = counts[gridY * HeatmapWidth + gridX] / (float)maxCount;
                pixels[y * outputWidth + x] = Color.Lerp(new Color(0f, 0f, 0f, 0f), new Color(1f, 0.05f, 0f, 0.9f), intensity);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        Destroy(texture);
    }

    private string F(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private string Csv(string value)
    {
        if (value == null) return "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
