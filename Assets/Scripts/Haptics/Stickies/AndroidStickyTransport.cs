using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Firefighting.Haptics.Stickies
{
    /// <summary>
    /// Talks to feedback Stickies from the Quest itself through the Java plugin in
    /// Assets/Plugins/Android/StickiesBle.java. Asks for the Bluetooth permissions on first use
    /// (a system dialog appears in the headset once).
    /// </summary>
    public sealed class AndroidStickyTransport : IStickyTransport
    {
        private const string PluginClass = "edu.firefighting.stickies.StickiesBle";
        private const float PollInterval = 0.25f;

        private readonly List<StickyDeviceInfo> devices = new();
        private string status = "Not started";

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject plugin;
        private string[] requiredPermissions;
        private bool permissionRequested;
        private bool pluginFailed;
        private bool wantScan;
        private float nextPoll;
#endif

        public string Name => "Android BLE";
        public string Status => status;
        public IReadOnlyList<StickyDeviceInfo> Devices => devices;

        public void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            int sdk = 0;
            using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                sdk = version.GetStatic<int>("SDK_INT");

            // Android 12+ has dedicated Bluetooth permissions; older versions need location to scan.
            requiredPermissions = sdk >= 31
                ? new[] { "android.permission.BLUETOOTH_SCAN", "android.permission.BLUETOOTH_CONNECT" }
                : new[] { Permission.FineLocation };
            status = "Waiting for Bluetooth permission";
            TryCreatePlugin();
#else
            status = "Android BLE only runs on the headset. Use UdpBridge or Simulated in the Editor.";
#endif
        }

        public void Tick()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (plugin == null)
            {
                if (!pluginFailed) TryCreatePlugin();
                return;
            }

            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + PollInterval;

            try
            {
                StickyDeviceListParser.Parse(plugin.Call<string>("getDevices"), '\n', devices);
                string log = plugin.Call<string>("drainLog");
                if (!string.IsNullOrEmpty(log))
                    foreach (string line in log.Split('\n'))
                        if (line.Length > 0) Debug.Log("[Stickies/Android] " + line);
                status = plugin.Call<string>("getStatus");
            }
            catch (Exception e)
            {
                status = "Plugin error: " + e.Message;
            }
#endif
        }

        public void SetScanning(bool scanning)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            wantScan = scanning;
            if (plugin == null) return;
            Invoke(scanning ? "startScan" : "stopScan");
#endif
        }

        public void Connect(string address)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Invoke("connect", address);
#endif
        }

        public void Disconnect(string address)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Invoke("disconnect", address);
#endif
        }

        public bool Write(string address, byte[] payload)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (plugin == null) return false;
            try
            {
                // Hex keeps the JNI call free of byte[] marshalling quirks.
                return plugin.Call<bool>("write", address, StickyProtocol.ToHex(payload));
            }
            catch (Exception e)
            {
                status = "Write failed: " + e.Message;
                return false;
            }
#else
            return false;
#endif
        }

        public void Dispose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (plugin == null) return;
            Invoke("close");
            plugin.Dispose();
            plugin = null;
#endif
            devices.Clear();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void TryCreatePlugin()
        {
            bool granted = true;
            foreach (string permission in requiredPermissions)
                granted &= Permission.HasUserAuthorizedPermission(permission);

            if (!granted)
            {
                if (!permissionRequested)
                {
                    permissionRequested = true;
                    var callbacks = new PermissionCallbacks();
                    callbacks.PermissionDenied += p => status = $"Permission denied: {p}. Allow it in the headset's app settings.";
                    Permission.RequestUserPermissions(requiredPermissions, callbacks);
                }
                return;
            }

            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                plugin = new AndroidJavaObject(PluginClass, activity);
                status = plugin.Call<string>("getStatus");
                if (wantScan) Invoke("startScan");
            }
            catch (Exception e)
            {
                status = "Could not start the Stickies Android plugin: " + e.Message;
                Debug.LogError("[Stickies/Android] " + status);
                plugin = null;
                pluginFailed = true; // do not retry every frame
            }
        }

        private void Invoke(string method, params object[] args)
        {
            if (plugin == null) return;
            try
            {
                plugin.Call(method, args);
            }
            catch (Exception e)
            {
                status = $"{method} failed: {e.Message}";
            }
        }
#endif
    }
}
