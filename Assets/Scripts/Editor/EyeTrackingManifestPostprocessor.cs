#if UNITY_EDITOR && UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;

/// <summary>Adds the Quest Pro eye-tracking permission to the Android manifest at build time.</summary>
public class EyeTrackingManifestPostprocessor : IPostGenerateGradleAndroidProject
{
    private const string AndroidNs = "http://schemas.android.com/apk/res/android";
    public int callbackOrder => 10;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath)) return;

        XmlDocument doc = new XmlDocument();
        doc.Load(manifestPath);
        XmlElement root = doc.DocumentElement;

        AddIfMissing(doc, root, "uses-permission", "com.oculus.permission.EYE_TRACKING", null);
        AddIfMissing(doc, root, "uses-feature", "oculus.software.eye_tracking", "false");
        doc.Save(manifestPath);
    }

    private static void AddIfMissing(XmlDocument doc, XmlElement root, string tag, string name, string required)
    {
        foreach (XmlNode node in root.GetElementsByTagName(tag))
            if (node.Attributes?["name", AndroidNs]?.Value == name) return;

        XmlElement element = doc.CreateElement(tag);
        element.SetAttribute("name", AndroidNs, name);
        if (required != null) element.SetAttribute("required", AndroidNs, required);
        root.AppendChild(element);
    }
}
#endif
