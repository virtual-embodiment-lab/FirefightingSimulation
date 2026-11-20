using System.Collections.Generic;
using Normal.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// The incident commander's pickup truck. The rig (pickup model, head pivot and camera) is
/// authored in the Commander scene around a reference copy of the Ford F250 prefab. When a
/// scenario loads, <see cref="MonitorMode"/> hands the scene to <see cref="EnterScene"/>, which
/// parks the pickup where that scenario's F250 stands and hides the scenario's F250 on this
/// machine only (VR users still see it).
///
/// The commander faces the windshield; holding the right mouse button looks around (within
/// <see cref="maxYaw"/> / <see cref="maxPitch"/>) and the view eases back when released.
/// WASD / arrow keys move the pickup (Q / E turn it, Shift is faster, R puts it back), keeping
/// it on the ground. Every VR user's voice is played as 2D audio, so the whole conversation is
/// audible from the truck. F1 toggles the overlay.
/// </summary>
public sealed class CommanderCab : MonoBehaviour
{
    [Tooltip("Copy of the Ford F250 prefab in the Commander scene that the pickup is placed against. " +
             "Each scenario's F250 (an instance of the same prefab) marks where the commander parks. " +
             "Only marks the spot; it is hidden in Play mode.")]
    [SerializeField] private Transform referenceTruck;
    [Tooltip("Eye pivot; rotated for looking around.")]
    [SerializeField] private Transform head;
    [SerializeField] private Camera viewCamera;
    [Tooltip("Hide the scenario's F250 on the commander's machine, since the pickup takes its place.")]
    [SerializeField] private bool hideScenarioTruck = true;

    [Header("Look around (hold right mouse button)")]
    [SerializeField] private float maxYaw = 100f;
    [SerializeField] private float maxPitch = 30f;
    [Tooltip("Degrees per pixel of mouse movement.")]
    [SerializeField] private float lookSensitivity = 0.15f;
    [Tooltip("How quickly the view returns to the windshield after releasing the button.")]
    [SerializeField] private float returnSharpness = 4f;

    [Header("Moving the truck (WASD / arrows, Q / E, Shift, R)")]
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float fastMultiplier = 3f;
    [Tooltip("Degrees per second for Q / E.")]
    [SerializeField] private float turnSpeed = 45f;
    [Tooltip("Stop the truck at walls and other solid objects instead of driving through them.")]
    [SerializeField] private bool stopAtObstacles = true;

    [Header("Audio")]
    [SerializeField] private float speakingThreshold = 0.05f;

    private const float GroundProbeHeight = 1.5f;
    private const float GroundProbeDepth = 4f;
    // The obstacle box starts this far above the ground, so the road and kerbs don't stop the truck.
    private const float GroundClearance = 0.35f;
    // The obstacle box is this much smaller than the truck, so it can slide along a wall it touches.
    private const float ObstacleSkin = 0.05f;

    private readonly List<RealtimeAvatar> avatars = new();
    private readonly List<int> clientIds = new();

    private Matrix4x4 cabInTruck;
    private Mesh truckMesh;
    // How many levels the matched mesh sits below the truck's root object.
    private int truckMeshDepth;
    private RealtimeAvatarManager avatarManager;
    private Realtime realtime;
    private string scenarioName;
    private bool seatedInTruck;
    private Vector3 homePosition;
    private Quaternion homeRotation;
    private bool followsGround;
    private float rideHeight;
    private float groundHeight;
    private Vector3 bodyCentre;
    private Vector3 bodyHalfExtents;
    private float yaw;
    private float pitch;
    private bool showOverlay = true;
    private float nextAudioRefreshTime;
    private GUIStyle panelStyle;

    private void Awake()
    {
        viewCamera.gameObject.tag = "MainCamera";
        // The pickup's own collider (from the F250 prefab) would get in the way of its ground
        // probe and obstacle checks; it moves by its own box (MeasureBody), so switch it off.
        foreach (Collider own in GetComponentsInChildren<Collider>(true))
            own.enabled = false;
        if (referenceTruck != null)
        {
            cabInTruck = TruckFrame(referenceTruck).inverse * transform.localToWorldMatrix;
            MeshFilter marker = referenceTruck.GetComponentInChildren<MeshFilter>();
            truckMesh = marker.sharedMesh;
            truckMeshDepth = 0;
            for (Transform t = marker.transform; t != referenceTruck; t = t.parent)
                truckMeshDepth++;
            SetTruckVisible(referenceTruck, false);
        }
    }

    /// <summary>Parks the commander's pickup where <paramref name="scene"/>'s F250 stands.</summary>
    public void EnterScene(Scene scene, RealtimeAvatarManager manager, Pose fallback)
    {
        avatarManager = manager;
        realtime = manager.GetComponent<Realtime>();
        scenarioName = scene.name;

        Transform truck = FindTruck(scene);
        seatedInTruck = truck != null;
        if (seatedInTruck)
        {
            if (hideScenarioTruck)
                SetTruckVisible(truck, false);
            Matrix4x4 pose = TruckFrame(truck) * cabInTruck;
            transform.SetPositionAndRotation(pose.GetPosition(), pose.rotation);
        }
        else
        {
            // No truck in this scene: stand at the VR spawn point instead, facing the same way.
            Debug.LogWarning($"[Commander] No Ford F250 found in {scene.name}; placing the commander at the VR spawn point. " +
                             "Place an instance of Assets/Scenes/Commander/Ford F250/Ford F250.prefab where the commander should park.");
            Quaternion facing = Quaternion.Euler(0f, fallback.rotation.eulerAngles.y, 0f);
            Vector3 eye = fallback.position + Vector3.up * 1.6f;
            transform.SetPositionAndRotation(eye - facing * transform.InverseTransformPoint(head.position), facing);
        }

        homePosition = transform.position;
        homeRotation = transform.rotation;
        // Keep the pickup's current height above whatever ground is under it while it moves.
        followsGround = false;
        followsGround = TryGroundHeight(transform.position, transform.position.y, out groundHeight);
        rideHeight = followsGround ? transform.position.y - groundHeight : 0f;
        MeasureBody();
    }

    // The truck's box in this object's space, from its renderers, raised off the ground.
    private void MeasureBody()
    {
        bool any = false;
        Bounds local = default;
        foreach (Renderer part in GetComponentsInChildren<Renderer>())
        {
            // Local bounds, so the box fits the truck however it is turned in the world.
            Bounds own = part.localBounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = transform.InverseTransformPoint(part.transform.TransformPoint(new Vector3(
                    (corner & 1) == 0 ? own.min.x : own.max.x,
                    (corner & 2) == 0 ? own.min.y : own.max.y,
                    (corner & 4) == 0 ? own.min.z : own.max.z)));
                if (any) local.Encapsulate(point);
                else local = new Bounds(point, Vector3.zero);
                any = true;
            }
        }
        if (!any)
        {
            bodyHalfExtents = Vector3.zero;
            return;
        }

        float bottom = local.min.y + GroundClearance;
        bodyCentre = new Vector3(local.center.x, (bottom + local.max.y) * 0.5f, local.center.z);
        bodyHalfExtents = new Vector3(local.extents.x, (local.max.y - bottom) * 0.5f, local.extents.z) -
                          Vector3.one * ObstacleSkin;
    }

    private bool HasBody => stopAtObstacles && bodyHalfExtents.x > 0f && bodyHalfExtents.y > 0f && bodyHalfExtents.z > 0f;

    private bool OverlapsAt(Vector3 position, Quaternion rotation) =>
        Physics.CheckBox(position + rotation * bodyCentre, bodyHalfExtents, rotation,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

    // Only something the truck would run into blocks it. Objects it already overlaps (e.g. it
    // was parked against a fence) are reported at distance 0 and ignored, so it can always be
    // driven out.
    private bool IsBlocked(Vector3 step)
    {
        float distance = step.magnitude;
        if (distance <= 1e-5f)
            return false;
        foreach (RaycastHit hit in Physics.BoxCastAll(transform.position + transform.rotation * bodyCentre,
                     bodyHalfExtents, step / distance, transform.rotation, distance + ObstacleSkin,
                     Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.distance > 0f)
                return true;
        }
        return false;
    }

    // An upright frame at the truck's root: its position and the direction it faces, ignoring
    // any tilt, so a truck parked on a slope still seats the commander level.
    private static Matrix4x4 TruckFrame(Transform truck)
    {
        Vector3 facing = Vector3.ProjectOnPlane(truck.forward, Vector3.up).normalized;
        return Matrix4x4.TRS(truck.position, Quaternion.LookRotation(facing, Vector3.up), Vector3.one);
    }

    // The scenario's F250 is found by one of the prefab's meshes, then walked up to the truck's
    // root, so the whole truck is used for placement and hidden.
    private Transform FindTruck(Scene scene)
    {
        if (truckMesh == null)
            return null;
        foreach (MeshFilter filter in Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            // The commander's own pickup shares these meshes; never park against it.
            if (filter.gameObject.scene != scene || filter.sharedMesh != truckMesh || filter.transform.IsChildOf(transform))
                continue;
            Transform root = filter.transform;
            for (int i = 0; i < truckMeshDepth && root.parent != null; i++)
                root = root.parent;
            return root;
        }
        return null;
    }

    private static void SetTruckVisible(Transform truck, bool visible)
    {
        foreach (Renderer truckRenderer in truck.GetComponentsInChildren<Renderer>(true))
            truckRenderer.enabled = visible;
        foreach (Collider truckCollider in truck.GetComponentsInChildren<Collider>(true))
            truckCollider.enabled = visible;
    }

    private void LateUpdate()
    {
        ObservedAvatars.Collect(avatarManager, avatars, clientIds);
        HandleLook();
        if (avatarManager != null)
            HandleDriving();

        if (Time.unscaledTime >= nextAudioRefreshTime)
        {
            nextAudioRefreshTime = Time.unscaledTime + 0.5f;
            ObservedAvatars.FlattenVoices(avatars);
        }
    }

    private void HandleLook()
    {
        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.rightButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue() * lookSensitivity;
            yaw = Mathf.Clamp(yaw + delta.x, -maxYaw, maxYaw);
            pitch = Mathf.Clamp(pitch - delta.y, -maxPitch, maxPitch);
        }
        else
        {
            float t = 1f - Mathf.Exp(-returnSharpness * Time.unscaledDeltaTime);
            yaw = Mathf.Lerp(yaw, 0f, t);
            pitch = Mathf.Lerp(pitch, 0f, t);
        }
        head.localRotation = Quaternion.Euler(pitch, yaw, 0f);

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.f1Key.wasPressedThisFrame)
            showOverlay = !showOverlay;
    }

    // WASD / arrows slide the pickup forward, back and sideways relative to where it faces,
    // Q / E turn it in place. It is only moved on this machine; VR users don't see it.
    private void HandleDriving()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard.rKey.wasPressedThisFrame)
        {
            transform.SetPositionAndRotation(homePosition, homeRotation);
            if (followsGround)
                groundHeight = homePosition.y - rideHeight;
            return;
        }

        Vector2 move = Vector2.zero;
        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move.y += 1f;
        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move.y -= 1f;
        if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move.x += 1f;
        if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move.x -= 1f;
        float turn = (keyboard.eKey.isPressed ? 1f : 0f) - (keyboard.qKey.isPressed ? 1f : 0f);
        if (move == Vector2.zero && turn == 0f)
            return;

        float dt = Time.unscaledDeltaTime;
        float speed = moveSpeed * (keyboard.shiftKey.isPressed ? fastMultiplier : 1f);

        if (turn != 0f)
        {
            Quaternion turned = Quaternion.Euler(0f, turn * turnSpeed * dt, 0f) * transform.rotation;
            if (!HasBody || !OverlapsAt(transform.position, turned) || OverlapsAt(transform.position, transform.rotation))
                transform.rotation = turned;
        }

        Vector3 step = transform.rotation * new Vector3(move.x, 0f, move.y) * (speed * dt);
        if (step == Vector3.zero || (HasBody && IsBlocked(step)))
            return;

        Vector3 next = transform.position + step;
        if (followsGround && TryGroundHeight(next, groundHeight + rideHeight, out float ground))
        {
            groundHeight = ground;
            next.y = ground + rideHeight;
        }
        transform.position = next;
    }

    // Probes from a little above the last known ground, so roofs and overhangs above the
    // truck are not mistaken for the road.
    private bool TryGroundHeight(Vector3 position, float referenceHeight, out float height)
    {
        float probeTop = followsGround ? groundHeight + GroundProbeHeight : referenceHeight;
        Vector3 origin = new(position.x, probeTop, position.z);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, GroundProbeDepth + GroundProbeHeight,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            height = hit.point.y;
            return true;
        }
        height = 0f;
        return false;
    }

    private void OnGUI()
    {
        // Before a scenario is chosen the launcher menu owns the screen.
        if (!showOverlay || avatarManager == null)
            return;

        panelStyle ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true, wordWrap = true, padding = new RectOffset(10, 10, 8, 8) };

        bool connected = realtime != null && realtime.connected;
        System.Text.StringBuilder text = new();
        text.AppendLine("<b>COMMANDER VIEW</b>");
        text.AppendLine($"Scenario: <b>{scenarioName}</b>{(seatedInTruck ? string.Empty : "  <color=#ffb347>(no F250)</color>")}");
        text.AppendLine(connected ? $"Connected  ·  VR users: {avatars.Count}" : "<color=#ffb347>Connecting to room…</color>");
        for (int i = 0; i < avatars.Count; i++)
        {
            string speaking = ObservedAvatars.IsSpeaking(avatars[i], speakingThreshold) ? "  <color=#7CFC00>● speaking</color>" : string.Empty;
            text.AppendLine($"  Player {i + 1} (id {clientIds[i]}){speaking}");
        }
        text.AppendLine();
        text.Append("<size=12>WASD move truck · Q/E turn · Shift faster · R reset · Hold right mouse to look · F1 hide</size>");

        GUIContent content = new(text.ToString());
        float width = Mathf.Min(380f, Screen.width - 24f);
        GUI.Box(new Rect(12f, 12f, width, panelStyle.CalcHeight(content, width)), content, panelStyle);
    }
}
