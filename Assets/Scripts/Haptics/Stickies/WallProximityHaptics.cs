using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Normal.Realtime;
using RootMotion.FinalIK;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;

// The Editor self-test (Tools > Stickies > Run Self-Test) steps the component with its own clock.
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]

namespace Firefighting.Haptics.Stickies
{
    public enum StickyTransportMode
    {
        [Tooltip("Android BLE on the headset, PC bridge everywhere else.")]
        Auto,
        AndroidBle,
        PcBridge,
        Simulated,
    }

    public enum StickyFeedbackStyle
    {
        [Tooltip("A short buzz when a wall comes within a zone, then silence until the participant moves away and back.")]
        AlertOnApproach,
        [Tooltip("Keeps pulsing for as long as a wall is within a zone, like a parking sensor.")]
        ContinuousProximity,
    }

    /// <summary>Where a Sticky is worn. Maps to a bone of the local VRIK avatar.</summary>
    public enum StickyBodySite
    {
        Chest, UpperBack, Pelvis, Head,
        LeftShoulder, LeftUpperArm, LeftForearm, LeftHand,
        RightShoulder, RightUpperArm, RightForearm, RightHand,
        LeftThigh, LeftCalf, RightThigh, RightCalf,
        CustomTransform,
    }

    /// <summary>Which way a Sticky "looks" for walls, relative to where the body faces.</summary>
    public enum WallSenseDirection { Forward, Back, Left, Right, AllAround, CustomYaw }

    /// <summary>One physical feedback Sticky worn by the participant.</summary>
    [Serializable]
    public class StickySlot
    {
        public string label = "Sticky";
        public bool enabled = true;

        [Tooltip("Part of the Sticky's BLE name or address (case-insensitive). Leave empty to take the next feedback Sticky that is switched on, in slot order.")]
        public string deviceFilter = "";

        [Tooltip("Body site the Sticky is taped to. Wall distance is measured from this bone of the local avatar.")]
        public StickyBodySite bodySite = StickyBodySite.Chest;
        [Tooltip("Used when Body Site is Custom Transform.")]
        public Transform customAnchor;

        [Tooltip("Which way this Sticky senses walls, relative to the body's facing (not the head's).")]
        public WallSenseDirection direction = WallSenseDirection.Forward;
        [Range(-180f, 180f), Tooltip("Degrees clockwise from body forward, for Custom Yaw.")]
        public float customYaw;
        [Range(0f, 90f), Tooltip("Half-width of the fan of rays around the sensing direction.")]
        public float fanHalfAngle = 35f;

        [Range(0, 254), Tooltip("Motor on the feedback Sticky. 0 for single-motor boards.")]
        public int motorId;
        [Range(0f, 1f), Tooltip("Per-participant comfort scaling for this site.")]
        public float intensityScale = 1f;

        [Header("Live (read only)")]
        public string assignedDevice = "";
        public string connection = "";
        public float wallDistance = float.PositiveInfinity;
        public string zone = "";
    }

    /// <summary>
    /// A distance band and the vibration it plays. Bands are sorted nearest first; beyond the
    /// last band the Sticky is silent.
    /// </summary>
    [Serializable]
    public class ProximityZone
    {
        public string label = "Zone";
        [Min(0.05f), Tooltip("Applies when the wall is closer than this (metres from the body site).")]
        public float maxDistance = 0.6f;
        [Range(1, 2000)] public int pulseMs = 120;
        [Range(0, 2000), Tooltip("Silence between pulses. In Continuous Proximity, 0 = continuous vibration.")]
        public int gapMs = 120;
        [Range(1, 10), Tooltip("Alert On Approach: pulses played once when this zone is entered.")]
        public int burstPulses = 1;
        [Range(0, 255)] public int intensity = 180;
    }

    /// <summary>
    /// Wall-proximity feedback through Sensorimotor Stickies (https://stickies.catherineyu.com).
    ///
    /// Each feedback Sticky taped to the participant is tied to a body site of their avatar and a
    /// direction relative to their body (front, back, left, right, or all around). Every sensing
    /// tick a small fan of rays is cast from that bone; the nearest wall hit picks a proximity
    /// zone. With Alert On Approach (the default) entering a closer zone plays one short burst and
    /// the Sticky then stays quiet, so a participant walking a corridor is not buzzed constantly.
    /// Continuous Proximity keeps pulsing while a wall is near instead.
    ///
    /// The firmware plays a whole pulse train from one Motor Command, so this writes rarely, and a
    /// dropped link never leaves a motor buzzing for longer than one burst or Pattern Window.
    ///
    /// Put it on any GameObject in the scene (Tools > Stickies > Add Wall Proximity Haptics). It
    /// finds the local XR Origin and Normcore avatar by itself and does nothing on monitor or
    /// commander clients.
    /// </summary>
    [AddComponentMenu("Firefighting/Stickies Wall Proximity Haptics")]
    [DefaultExecutionOrder(10050)] // after VRIK and BodyAnchor, so bones are this frame's pose
    public sealed class WallProximityHaptics : MonoBehaviour
    {
        private const float RigSearchInterval = 1f;
        private const float ReconnectDelay = 3f;
        private const float ScanToggleCooldown = 6f; // Android blocks more than 5 scan starts per 30 s
        private const float ContinuousLead = 0.06f;
        private const int MaxRaysPerSlot = 24;

        [Header("Connection")]
        [SerializeField] private StickyTransportMode transport = StickyTransportMode.Auto;
        [SerializeField, Tooltip("PC running Tools/StickiesBridge. 127.0.0.1 in the Editor; the PC's LAN IP from a standalone Quest.")]
        private string bridgeHost = "127.0.0.1";
        [SerializeField] private int bridgePort = 47800;
        [SerializeField, Tooltip("Keep Bluetooth links when scenes change. Only works on its own root GameObject.")]
        private bool keepAcrossScenes = true;
        [SerializeField, Tooltip("When a Sticky connects it pulses (slot number) times, so you can check it is on the right body site.")]
        private bool identifyOnConnect = true;

        [Header("Stickies on the body")]
        [SerializeField] private List<StickySlot> slots = DefaultSlots();
        [SerializeField, Tooltip("Optional. Headset transform to use instead of finding the XR Origin camera (desktop rigs, tests).")]
        private Transform headOverride;

        [Header("What counts as a wall")]
        [SerializeField, Tooltip("A collider counts when it or a parent has one of these names. The house generator combines walls into objects named Walls.")]
        private string[] wallObjectNames = { "Walls" };
        [SerializeField] private bool doorsCountAsWalls = true;
        [SerializeField] private string[] doorObjectNames = { "Door", "Door(Clone)", "Door_Interior", "Door_Interior(Clone)" };
        [SerializeField, Tooltip("Anything on these layers also counts as a wall.")]
        private LayerMask extraWallLayers;
        [SerializeField, Tooltip("Fallback for scenes without named walls: any static, roughly vertical surface counts (includes furniture sides).")]
        private bool anyVerticalStaticSurface;

        [Header("Sensing")]
        [SerializeField, Min(0.5f)] private float senseRange = 2f;
        [SerializeField, Range(1, 9)] private int raysPerFan = 5;
        [SerializeField, Range(4, MaxRaysPerSlot)] private int raysAllAround = 12;
        [SerializeField, Range(5f, 90f)] private float senseRateHz = 30f;

        [Header("Feedback")]
        [SerializeField] private bool hapticsEnabled = true;
        [SerializeField] private StickyFeedbackStyle feedbackStyle = StickyFeedbackStyle.AlertOnApproach;
        [SerializeField, Min(0f), Tooltip("Alert On Approach: repeat the burst every this many seconds while the wall stays in the zone. 0 = only once per approach.")]
        private float remindEverySeconds;
        [SerializeField, Range(0f, 1f)] private float masterIntensity = 1f;
        [SerializeField] private List<ProximityZone> zones = DefaultZones();
        [SerializeField, Min(0f), Tooltip("Extra distance needed to drop to a farther zone, to stop flicker at a boundary.")]
        private float hysteresis = 0.05f;
        [SerializeField, Range(0.3f, 3f), Tooltip("Continuous Proximity: how long each pattern sent to a Sticky lasts before it is renewed.")]
        private float patternWindowSeconds = 1f;

        [Header("Debug")]
        [SerializeField] private bool logZoneChanges = true;
        [SerializeField] private bool logSimulatedWrites;
        [SerializeField] private bool drawRaysInSceneView = true;
        [SerializeField, Tooltip("Live transport status (read only).")]
        private string transportStatus = "";

        /// <summary>Raised when a slot enters a new zone. Zone index is -1 when no wall is in range.</summary>
        public event Action<StickySlot, int, float> ZoneChanged;

        /// <summary>Raised for every Motor Command accepted by the transport (raw BLE payload).</summary>
        public event Action<StickySlot, byte[]> CommandSent;

        public static WallProximityHaptics Instance { get; private set; }
        public IReadOnlyList<StickySlot> Slots => slots;
        public IReadOnlyList<ProximityZone> Zones => zones;
        public string TransportStatus => transportStatus;

        public bool HapticsEnabled
        {
            get => hapticsEnabled;
            set
            {
                if (hapticsEnabled == value) return;
                hapticsEnabled = value;
                if (!value) StopAll();
            }
        }

        private sealed class SlotRuntime
        {
            public string address;
            public bool ready;
            public int zone = -1;
            public float patternEndsAt;
            public float nextReminderAt;
            public float identifyEndsAt;
            public float lastConnectAttempt = float.NegativeInfinity;
            public float distance = float.PositiveInfinity;
            public Vector3 origin;
            public readonly Vector3[] rayEnds = new Vector3[MaxRaysPerSlot];
            public readonly bool[] rayHits = new bool[MaxRaysPerSlot];
            public int rayCount;
        }

        private IStickyTransport link;
        private readonly List<SlotRuntime> runtime = new();
        private readonly RaycastHit[] hits = new RaycastHit[32];
        private readonly Dictionary<Collider, bool> wallCache = new();

        private XROrigin rig;
        private Transform head;
        private VRIK body;
        private float now;
        private float nextRigSearch;
        private float nextSense;
        private bool scanning;
        private float lastScanToggle = float.NegativeInfinity;

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (keepAcrossScenes)
            {
                if (Instance != null && Instance != this)
                {
                    Debug.Log("[Stickies] A Wall Proximity Haptics instance already holds the Stickies; removing the scene copy.", this);
                    enabled = false; // never open a second Bluetooth link, even for one frame
                    Destroy(this);
                    return;
                }

                // Only carry a GameObject of our own across scenes, never a rig or manager it shares.
                bool dedicated = transform.parent == null && transform.childCount == 0 &&
                                 GetComponents<Component>().Length == 2;
                if (dedicated) DontDestroyOnLoad(gameObject);
                else Debug.LogWarning("[Stickies] Keep Across Scenes needs its own empty root GameObject (Tools > Stickies > Add Wall Proximity Haptics); links will drop on scene change.", this);
            }
            Instance = this;
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SyncRuntime();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Shutdown();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void OnApplicationPause(bool paused)
        {
            // Headset taken off or app backgrounded: never leave a participant buzzing.
            if (paused) StopAll();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            wallCache.Clear();
            rig = null;
            head = null;
            body = null;
            nextRigSearch = 0f;
        }

        private void OnValidate()
        {
            zones.Sort((a, b) => a.maxDistance.CompareTo(b.maxDistance));
            float farthest = zones.Count > 0 ? zones[zones.Count - 1].maxDistance : 0f;
            senseRange = Mathf.Max(senseRange, farthest + hysteresis + 0.1f);
        }

        private void LateUpdate() => Tick(Time.unscaledTime);

        /// <summary>One update at clock time <paramref name="time"/> (seconds). LateUpdate passes
        /// the real clock; the Editor self-test passes simulated time.</summary>
        internal void Tick(float time)
        {
            now = time;

            // Observer clients (desktop monitor, commander) have no body to put Stickies on.
            if (MonitorMode.IsActive) return;

            if (link == null) StartTransport();
            link.Tick();
            transportStatus = link.Status;

            SyncRuntime();
            ManageConnections();

            if (now >= nextRigSearch && (head == null || body == null))
            {
                nextRigSearch = now + RigSearchInterval;
                FindRig();
            }

            if (now >= nextSense)
            {
                nextSense = now + 1f / senseRateHz;
                Sense();
            }

            for (int i = 0; i < slots.Count; i++)
                Drive(i);
        }

        /// <summary>Silences every Sticky and closes the Bluetooth link.</summary>
        internal void Shutdown()
        {
            StopAll();
            link?.Dispose();
            link = null;
            scanning = false;
            foreach (SlotRuntime state in runtime)
            {
                state.ready = false;
                state.zone = -1;
            }
        }

        // ------------------------------------------------------------------ transport and pairing

        private void StartTransport()
        {
            StickyTransportMode mode = transport;
            if (mode == StickyTransportMode.Auto)
                mode = Application.platform == RuntimePlatform.Android && !Application.isEditor
                    ? StickyTransportMode.AndroidBle
                    : StickyTransportMode.PcBridge;

            link = mode switch
            {
                StickyTransportMode.AndroidBle => new AndroidStickyTransport(),
                StickyTransportMode.PcBridge => new UdpBridgeStickyTransport(bridgeHost, bridgePort),
                _ => new SimulatedStickyTransport(slots.Count, logSimulatedWrites),
            };
            link.Start();
            Debug.Log($"[Stickies] Wall proximity haptics using {link.Name}: {link.Status}", this);
        }

        private void SyncRuntime()
        {
            while (runtime.Count < slots.Count) runtime.Add(new SlotRuntime());
            while (runtime.Count > slots.Count) runtime.RemoveAt(runtime.Count - 1);
        }

        private void ManageConnections()
        {
            IReadOnlyList<StickyDeviceInfo> devices = link.Devices;

            // Slots with a filter claim their Sticky first; then empty slots take the rest in
            // the order the Stickies were found (switch them on one at a time, in slot order).
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    StickySlot slot = slots[i];
                    bool filtered = !string.IsNullOrWhiteSpace(slot.deviceFilter);
                    if (!slot.enabled || runtime[i].address != null || filtered != (pass == 0)) continue;

                    foreach (StickyDeviceInfo device in devices)
                    {
                        if (IsAssigned(device.Address)) continue;
                        if (filtered ? !Matches(slot.deviceFilter, device) : IsReservedByFilter(device, i)) continue;
                        runtime[i].address = device.Address;
                        Debug.Log($"[Stickies] '{slot.label}' -> {device.Name} ({device.Address})", this);
                        break;
                    }
                }
            }

            bool anyConnecting = false;
            foreach (StickyDeviceInfo device in devices)
                anyConnecting |= device.State == StickyConnectionState.Connecting;

            bool needScan = false;
            for (int i = 0; i < slots.Count; i++)
            {
                StickySlot slot = slots[i];
                SlotRuntime state = runtime[i];
                if (!slot.enabled)
                {
                    slot.connection = "Disabled";
                    continue;
                }
                if (state.address == null)
                {
                    slot.assignedDevice = "";
                    slot.connection = "Waiting for a feedback Sticky";
                    needScan = true;
                    continue;
                }

                StickyDeviceInfo info = default;
                bool known = false;
                foreach (StickyDeviceInfo device in devices)
                {
                    if (device.Address != state.address) continue;
                    info = device;
                    known = true;
                    break;
                }

                StickyConnectionState connection = known ? info.State : StickyConnectionState.Disconnected;
                slot.assignedDevice = known ? $"{info.Name} ({info.Address})" : state.address;
                slot.connection = connection.ToString();

                if (connection == StickyConnectionState.Ready)
                {
                    if (!state.ready)
                    {
                        state.ready = true;
                        state.zone = -1;
                        if (identifyOnConnect) Identify(i);
                    }
                    continue;
                }

                if (state.ready)
                {
                    state.ready = false;
                    state.zone = -1;
                    slot.zone = "";
                    Debug.LogWarning($"[Stickies] '{slot.label}' lost its Sticky; reconnecting.", this);
                }

                needScan = true;
                if (connection != StickyConnectionState.Connecting && !anyConnecting &&
                    now - state.lastConnectAttempt > ReconnectDelay)
                {
                    // One connection attempt at a time, as the Stickies docs recommend.
                    state.lastConnectAttempt = now;
                    anyConnecting = true;
                    link.Connect(state.address);
                }
            }

            if (needScan != scanning && now - lastScanToggle > ScanToggleCooldown)
            {
                scanning = needScan;
                lastScanToggle = now;
                link.SetScanning(needScan);
            }
        }

        private bool IsAssigned(string address)
        {
            foreach (SlotRuntime state in runtime)
                if (state.address == address) return true;
            return false;
        }

        private bool IsReservedByFilter(StickyDeviceInfo device, int exceptSlot)
        {
            for (int i = 0; i < slots.Count; i++)
                if (i != exceptSlot && slots[i].enabled && !string.IsNullOrWhiteSpace(slots[i].deviceFilter) &&
                    Matches(slots[i].deviceFilter, device))
                    return true;
            return false;
        }

        private static bool Matches(string filter, StickyDeviceInfo device)
        {
            filter = filter.Trim();
            return (device.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (device.Address ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------ body and walls

        private void FindRig()
        {
            if (headOverride != null)
            {
                head = headOverride;
            }
            else if (head == null)
            {
                foreach (XROrigin origin in FindObjectsByType<XROrigin>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (origin.Camera == null || !origin.Camera.isActiveAndEnabled) continue;
                    rig = origin;
                    head = origin.Camera.transform;
                    // Prefer the walking rig (the one with a CharacterController).
                    if (origin.GetComponentInChildren<CharacterController>() != null) break;
                }
            }

            if (body == null)
            {
                foreach (RealtimeAvatarManager manager in FindObjectsByType<RealtimeAvatarManager>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (manager.localAvatar == null) continue;
                    body = manager.localAvatar.GetComponentInChildren<VRIK>(true);
                    if (body != null) break;
                }
                if (body == null && rig != null)
                    body = rig.GetComponentInChildren<VRIK>(true);
                if (body == null && headOverride != null)
                    body = headOverride.root.GetComponentInChildren<VRIK>(true);
            }
        }

        private bool TryGetBodyForward(out Vector3 forward)
        {
            forward = Vector3.zero;
            Transform reference = body != null && body.references.root != null ? body.references.root : head;
            if (reference == null) return false;

            forward = Vector3.ProjectOnPlane(reference.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) // looking straight down
                forward = Vector3.ProjectOnPlane(reference.up, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) return false;
            forward.Normalize();
            return true;
        }

        private Vector3 SitePosition(StickySlot slot, Vector3 forward)
        {
            if (slot.bodySite == StickyBodySite.CustomTransform && slot.customAnchor != null)
                return slot.customAnchor.position;

            Transform bone = Bone(slot.bodySite);
            if (bone != null) return bone.position;

            // No avatar yet: estimate from the headset.
            Vector3 headPosition = head.position;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            return slot.bodySite switch
            {
                StickyBodySite.Head => headPosition,
                StickyBodySite.Pelvis => headPosition + Vector3.down * 0.7f,
                StickyBodySite.LeftShoulder or StickyBodySite.LeftUpperArm or StickyBodySite.LeftForearm or StickyBodySite.LeftHand
                    => headPosition + Vector3.down * 0.4f - right * 0.2f,
                StickyBodySite.RightShoulder or StickyBodySite.RightUpperArm or StickyBodySite.RightForearm or StickyBodySite.RightHand
                    => headPosition + Vector3.down * 0.4f + right * 0.2f,
                StickyBodySite.LeftThigh => headPosition + Vector3.down * 0.95f - right * 0.1f,
                StickyBodySite.RightThigh => headPosition + Vector3.down * 0.95f + right * 0.1f,
                StickyBodySite.LeftCalf => headPosition + Vector3.down * 1.3f - right * 0.1f,
                StickyBodySite.RightCalf => headPosition + Vector3.down * 1.3f + right * 0.1f,
                _ => headPosition + Vector3.down * 0.35f,
            };
        }

        private Transform Bone(StickyBodySite site)
        {
            if (body == null) return null;
            VRIK.References r = body.references;
            return site switch
            {
                StickyBodySite.Chest or StickyBodySite.UpperBack => r.chest != null ? r.chest : r.spine,
                StickyBodySite.Pelvis => r.pelvis,
                StickyBodySite.Head => r.head,
                StickyBodySite.LeftShoulder => r.leftShoulder != null ? r.leftShoulder : r.leftUpperArm,
                StickyBodySite.LeftUpperArm => r.leftUpperArm,
                StickyBodySite.LeftForearm => r.leftForearm,
                StickyBodySite.LeftHand => r.leftHand,
                StickyBodySite.RightShoulder => r.rightShoulder != null ? r.rightShoulder : r.rightUpperArm,
                StickyBodySite.RightUpperArm => r.rightUpperArm,
                StickyBodySite.RightForearm => r.rightForearm,
                StickyBodySite.RightHand => r.rightHand,
                StickyBodySite.LeftThigh => r.leftThigh,
                StickyBodySite.LeftCalf => r.leftCalf,
                StickyBodySite.RightThigh => r.rightThigh,
                StickyBodySite.RightCalf => r.rightCalf,
                _ => null,
            };
        }

        private void Sense()
        {
            Vector3 forward = default;
            bool hasBody = head != null && TryGetBodyForward(out forward);

            for (int i = 0; i < slots.Count; i++)
            {
                StickySlot slot = slots[i];
                SlotRuntime state = runtime[i];
                state.distance = float.PositiveInfinity;
                state.rayCount = 0;
                if (!slot.enabled || !hasBody)
                {
                    slot.wallDistance = state.distance;
                    continue;
                }

                state.origin = SitePosition(slot, forward);
                if (slot.direction == WallSenseDirection.AllAround)
                {
                    int count = Mathf.Clamp(raysAllAround, 4, MaxRaysPerSlot);
                    for (int k = 0; k < count; k++)
                        CastRay(state, Quaternion.AngleAxis(360f * k / count, Vector3.up) * forward);
                }
                else
                {
                    float center = slot.direction switch
                    {
                        WallSenseDirection.Back => 180f,
                        WallSenseDirection.Left => -90f,
                        WallSenseDirection.Right => 90f,
                        WallSenseDirection.CustomYaw => slot.customYaw,
                        _ => 0f,
                    };
                    int count = Mathf.Clamp(raysPerFan, 1, MaxRaysPerSlot);
                    for (int k = 0; k < count; k++)
                    {
                        float offset = count == 1 ? 0f : Mathf.Lerp(-slot.fanHalfAngle, slot.fanHalfAngle, k / (count - 1f));
                        CastRay(state, Quaternion.AngleAxis(center + offset, Vector3.up) * forward);
                    }
                }
                slot.wallDistance = state.distance;
            }
        }

        private void CastRay(SlotRuntime state, Vector3 direction)
        {
            int mask = Physics.DefaultRaycastLayers | extraWallLayers.value;
            int count = Physics.RaycastNonAlloc(state.origin, direction, hits, senseRange, mask, QueryTriggerInteraction.Ignore);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.distance < nearest && IsWall(hit))
                    nearest = hit.distance;
            }

            int index = state.rayCount++;
            bool hitWall = nearest < float.PositiveInfinity;
            state.rayHits[index] = hitWall;
            state.rayEnds[index] = state.origin + direction * (hitWall ? nearest : senseRange);
            state.distance = Mathf.Min(state.distance, nearest);
        }

        private bool IsWall(RaycastHit hit)
        {
            Collider collider = hit.collider;
            if (collider == null) return false;
            if ((extraWallLayers.value & (1 << collider.gameObject.layer)) != 0) return true;

            if (!wallCache.TryGetValue(collider, out bool named))
            {
                named = HasWallName(collider.transform);
                wallCache[collider] = named;
            }
            if (named) return true;

            if (!anyVerticalStaticSurface || Mathf.Abs(hit.normal.y) > 0.5f) return false;
            if (rig != null && collider.transform.IsChildOf(rig.transform)) return false;
            if (body != null && collider.transform.IsChildOf(body.transform.root)) return false;
            Rigidbody attached = collider.attachedRigidbody;
            return attached == null || attached.isKinematic;
        }

        private bool HasWallName(Transform t)
        {
            for (Transform current = t; current != null; current = current.parent)
            {
                string name = current.name;
                if (Array.IndexOf(wallObjectNames, name) >= 0) return true;
                if (doorsCountAsWalls && Array.IndexOf(doorObjectNames, name) >= 0) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ haptics

        private void Drive(int i)
        {
            StickySlot slot = slots[i];
            SlotRuntime state = runtime[i];
            if (!slot.enabled || !state.ready || now < state.identifyEndsAt) return;

            int zone = hapticsEnabled ? Classify(state.distance, state.zone) : -1;
            bool alert = feedbackStyle == StickyFeedbackStyle.AlertOnApproach;

            if (zone != state.zone)
            {
                // Lower index = nearer zone; -1 = no wall in range.
                bool nearer = zone >= 0 && (state.zone < 0 || zone < state.zone);
                state.zone = zone;
                slot.zone = zone >= 0 ? zones[zone].label : "None";

                if (alert)
                {
                    // Buzz only on the way in. Moving away (or staying put) is silent.
                    if (nearer)
                    {
                        PlayBurst(i, zones[zone]);
                        state.nextReminderAt = now + remindEverySeconds;
                    }
                }
                else if (zone >= 0)
                {
                    SendPattern(i, zones[zone]);
                }
                else
                {
                    Send(i, StickyProtocol.Stop((byte)slot.motorId));
                    state.patternEndsAt = 0f;
                }

                if (logZoneChanges)
                    Debug.Log($"[Stickies] {slot.label}: {slot.zone} ({(float.IsInfinity(state.distance) ? "no wall" : state.distance.ToString("0.00") + " m")})", this);
                ZoneChanged?.Invoke(slot, zone, state.distance);
                return;
            }

            if (zone < 0) return;

            if (alert)
            {
                if (remindEverySeconds > 0f && now >= state.nextReminderAt)
                {
                    PlayBurst(i, zones[zone]);
                    state.nextReminderAt = now + remindEverySeconds;
                }
                return;
            }

            float lead = zones[zone].gapMs == 0 ? ContinuousLead : 0f;
            if (now >= state.patternEndsAt - lead)
                SendPattern(i, zones[zone]);
        }

        /// <summary>Nearest zone containing the distance, holding the current zone until the wall is
        /// <see cref="hysteresis"/> past its edge.</summary>
        private int Classify(float distance, int current)
        {
            int raw = -1;
            for (int z = 0; z < zones.Count; z++)
            {
                if (distance > zones[z].maxDistance) continue;
                raw = z;
                break;
            }

            bool movingAway = current >= 0 && current < zones.Count && (raw < 0 || raw > current);
            if (movingAway && distance <= zones[current].maxDistance + hysteresis)
                return current;
            return raw;
        }

        private int Intensity(int i, ProximityZone zone) =>
            Mathf.RoundToInt(zone.intensity * masterIntensity * slots[i].intensityScale);

        /// <summary>Alert On Approach: one short pulse train, played once.</summary>
        private void PlayBurst(int i, ProximityZone zone)
        {
            Send(i, StickyProtocol.Play((byte)slots[i].motorId, zone.pulseMs, zone.burstPulses, zone.gapMs, Intensity(i, zone)));
        }

        /// <summary>Continuous Proximity: about one Pattern Window of pulses, renewed while in the zone.</summary>
        private void SendPattern(int i, ProximityZone zone)
        {
            SlotRuntime state = runtime[i];
            int periodMs = Mathf.Max(1, zone.pulseMs + zone.gapMs);
            int count = Mathf.Clamp(Mathf.RoundToInt(patternWindowSeconds * 1000f / periodMs), 1, 255);

            byte[] command = StickyProtocol.Play((byte)slots[i].motorId, zone.pulseMs, count, zone.gapMs, Intensity(i, zone));
            state.patternEndsAt = Send(i, command)
                ? now + count * periodMs / 1000f
                : now + 0.2f; // not accepted; retry shortly
        }

        private bool Send(int i, byte[] payload)
        {
            if (link == null || !link.Write(runtime[i].address, payload)) return false;
            CommandSent?.Invoke(slots[i], payload);
            return true;
        }

        private void Identify(int i)
        {
            StickySlot slot = slots[i];
            SlotRuntime state = runtime[i];
            int pulses = Mathf.Clamp(i + 1, 1, 10);
            const int pulseMs = 150, gapMs = 200;
            int intensity = Mathf.RoundToInt(200 * slot.intensityScale);
            if (Send(i, StickyProtocol.Play((byte)slot.motorId, pulseMs, pulses, gapMs, intensity)))
            {
                state.identifyEndsAt = now + pulses * (pulseMs + gapMs) / 1000f + 0.3f;
                state.zone = -1;
                Debug.Log($"[Stickies] '{slot.label}' connected; it should pulse {pulses} time(s).", this);
            }
        }

        private void StopAll()
        {
            if (link == null) return;
            for (int i = 0; i < slots.Count && i < runtime.Count; i++)
            {
                SlotRuntime state = runtime[i];
                if (!state.ready) continue;
                Send(i, StickyProtocol.Stop((byte)slots[i].motorId));
                state.zone = -1;
                state.patternEndsAt = 0f;
                slots[i].zone = "None";
            }
        }

        // ------------------------------------------------------------------ inspector tools

        [ContextMenu("Stickies/Pulse every connected Sticky (identify)")]
        private void IdentifyAll()
        {
            if (link == null) return;
            for (int i = 0; i < slots.Count && i < runtime.Count; i++)
                if (runtime[i].ready) Identify(i);
        }

        [ContextMenu("Stickies/Forget assignments (re-pair)")]
        private void ForgetAssignments()
        {
            StopAll();
            foreach (SlotRuntime state in runtime)
            {
                if (state.address != null) link?.Disconnect(state.address);
                state.address = null;
                state.ready = false;
                state.zone = -1;
            }
            Debug.Log("[Stickies] Assignments cleared. Switch Stickies on one at a time, in slot order.", this);
        }

        private static List<StickySlot> DefaultSlots() => new()
        {
            new StickySlot { label = "Chest (walls ahead)", bodySite = StickyBodySite.Chest, direction = WallSenseDirection.Forward, fanHalfAngle = 35f },
            new StickySlot { label = "Upper back (walls behind)", bodySite = StickyBodySite.UpperBack, direction = WallSenseDirection.Back, fanHalfAngle = 35f },
            new StickySlot { label = "Left upper arm (walls left)", bodySite = StickyBodySite.LeftUpperArm, direction = WallSenseDirection.Left, fanHalfAngle = 50f },
            new StickySlot { label = "Right upper arm (walls right)", bodySite = StickyBodySite.RightUpperArm, direction = WallSenseDirection.Right, fanHalfAngle = 50f },
        };

        /// <summary>Two cues: a gentle single buzz when a wall is within reach, three quick strong
        /// pulses at contact. Nothing farther than 0.6 m, so walking a corridor stays quiet.</summary>
        private static List<ProximityZone> DefaultZones() => new()
        {
            new ProximityZone { label = "Touching", maxDistance = 0.3f, pulseMs = 70, gapMs = 70, burstPulses = 3, intensity = 255 },
            new ProximityZone { label = "Near", maxDistance = 0.6f, pulseMs = 150, gapMs = 450, burstPulses = 1, intensity = 170 },
        };

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!drawRaysInSceneView || !Application.isPlaying) return;
            for (int i = 0; i < slots.Count && i < runtime.Count; i++)
            {
                SlotRuntime state = runtime[i];
                if (state.rayCount == 0) continue;
                Color color = state.zone switch
                {
                    0 => Color.red,
                    1 => new Color(1f, 0.6f, 0f),
                    -1 => new Color(0.3f, 0.9f, 0.4f),
                    _ => Color.yellow,
                };
                for (int k = 0; k < state.rayCount; k++)
                {
                    Gizmos.color = state.rayHits[k] ? color : new Color(color.r, color.g, color.b, 0.25f);
                    Gizmos.DrawLine(state.origin, state.rayEnds[k]);
                    if (state.rayHits[k]) Gizmos.DrawWireSphere(state.rayEnds[k], 0.03f);
                }
                UnityEditor.Handles.Label(state.origin + Vector3.up * 0.1f,
                    $"{slots[i].label}\n{(float.IsInfinity(state.distance) ? "-" : state.distance.ToString("0.00") + " m")} {slots[i].zone}");
            }
        }
#endif
    }
}
