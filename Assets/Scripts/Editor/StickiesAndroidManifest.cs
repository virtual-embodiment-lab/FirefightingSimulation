using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

/// <summary>
/// Adds the Bluetooth permissions the Sensorimotor Stickies plugin
/// (Assets/Plugins/Android/StickiesBle.java) needs to the generated Android manifest, so the
/// project keeps using Unity's default manifest. Nothing is requested at launch: the app only
/// asks for them when WallProximityHaptics starts with the Android BLE transport.
/// </summary>
public sealed class StickiesAndroidManifest : IPostGenerateGradleAndroidProject
{
    private const string AndroidNs = "http://schemas.android.com/apk/res/android";

    public int callbackOrder => 100;

    public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
    {
        string manifestPath = Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            Debug.LogWarning($"[Stickies] Android manifest not found at {manifestPath}; Bluetooth permissions not added.");
            return;
        }

        var doc = new XmlDocument();
        doc.Load(manifestPath);
        XmlElement manifest = doc.DocumentElement;
        if (manifest == null) return;

        // Android 12+ (API 31+). Stickies are not used to derive location.
        AddPermission(doc, manifest, "android.permission.BLUETOOTH_SCAN", null, "neverForLocation");
        AddPermission(doc, manifest, "android.permission.BLUETOOTH_CONNECT", null, null);
        // Android 11 and older.
        AddPermission(doc, manifest, "android.permission.BLUETOOTH", "30", null);
        AddPermission(doc, manifest, "android.permission.BLUETOOTH_ADMIN", "30", null);
        AddPermission(doc, manifest, "android.permission.ACCESS_FINE_LOCATION", "30", null);
        AddFeature(doc, manifest, "android.hardware.bluetooth_le");

        doc.Save(manifestPath);
    }

    private static void AddPermission(XmlDocument doc, XmlElement manifest, string name, string maxSdk, string flags)
    {
        foreach (XmlElement existing in manifest.GetElementsByTagName("uses-permission"))
            if (existing.GetAttribute("name", AndroidNs) == name) return;

        XmlElement element = doc.CreateElement("uses-permission");
        SetAndroid(doc, element, "name", name);
        if (maxSdk != null) SetAndroid(doc, element, "maxSdkVersion", maxSdk);
        if (flags != null) SetAndroid(doc, element, "usesPermissionFlags", flags);
        manifest.PrependChild(element);
    }

    /// <summary>Writes android:attr with the explicit prefix so the saved XML stays readable by aapt.</summary>
    private static void SetAndroid(XmlDocument doc, XmlElement element, string localName, string value)
    {
        XmlAttribute attribute = doc.CreateAttribute("android", localName, AndroidNs);
        attribute.Value = value;
        element.Attributes.Append(attribute);
    }

    private static void AddFeature(XmlDocument doc, XmlElement manifest, string name)
    {
        foreach (XmlElement existing in manifest.GetElementsByTagName("uses-feature"))
            if (existing.GetAttribute("name", AndroidNs) == name) return;

        XmlElement element = doc.CreateElement("uses-feature");
        SetAndroid(doc, element, "name", name);
        SetAndroid(doc, element, "required", "false"); // the app still installs without BLE
        manifest.PrependChild(element);
    }
}
