using Normal.Realtime;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// A hinged door that swings open or shut when the local player's hand touches it.
/// RoomGeneration puts this on the front door's hinge pivot. When connected, the open/closed state is
/// shared through Normcore so every player sees the same door; offline it just toggles locally.
/// A touch is measured against the door's mesh, not a collider: HazardTagging adds, resizes and removes
/// colliders on the house's objects, and the door has to keep working through all of it.
/// </summary>
public class TouchDoor : RealtimeComponent<DoorStateModel>
{
    [Tooltip("The door's mesh. A hand within Touch Distance of it counts as a touch. RoomGeneration sets it; if empty, the first mesh under the hinge is used.")]
    public MeshFilter doorPanel;
    [Tooltip("How far the door swings open around its hinge, in degrees. Positive swings it into the house.")]
    public float openAngle = 90f;
    [Tooltip("How fast the door swings, in degrees per second.")]
    public float swingSpeed = 120f;
    [Tooltip("How close (in meters) a hand has to get to the door to count as touching it.")]
    public float touchDistance = 0.1f;
    [Tooltip("Seconds after a toggle before another touch counts, so one push doesn't flip the door straight back.")]
    public float touchCooldown = 1f;

    private bool isOpen;
    private Quaternion closedRotation;
    private XRBaseInteractor[] interactors = new XRBaseInteractor[0];
    private float nextInteractorSearchTime;
    private float nextTouchTime;
    private bool wasTouching;

    private void Awake()
    {
        closedRotation = transform.localRotation;

        if (doorPanel == null || doorPanel.sharedMesh == null)
            doorPanel = FindDoorPanel();
        if (doorPanel == null)
            Debug.LogWarning($"TouchDoor on {name} has no door mesh under it, so it can't be touched open.", this);
    }

    private MeshFilter FindDoorPanel()
    {
        foreach (MeshFilter meshFilter in GetComponentsInChildren<MeshFilter>(true))
        {
            if (meshFilter.sharedMesh != null)
                return meshFilter;
        }
        return null;
    }

    private void Update()
    {
        if (TouchStarted())
            SetOpen(!isOpen);

        Quaternion target = isOpen ? closedRotation * Quaternion.Euler(0f, openAngle, 0f) : closedRotation;
        transform.localRotation = Quaternion.RotateTowards(transform.localRotation, target, swingSpeed * Time.deltaTime);
    }

    private bool TouchStarted()
    {
        if (doorPanel == null || !doorPanel.gameObject.activeInHierarchy) return false;

        // The player rig can spawn after the house (e.g. once the network connects), so look for hands every couple of seconds
        if (Time.time >= nextInteractorSearchTime)
        {
            interactors = FindObjectsByType<XRBaseInteractor>(FindObjectsSortMode.None);
            nextInteractorSearchTime = Time.time + 2f;
        }

        bool touching = false;
        foreach (XRBaseInteractor interactor in interactors)
        {
            if (!IsLocalHand(interactor)) continue;

            if (IsNearDoor(interactor.transform.position))
            {
                touching = true;
                break;
            }
        }

        // One toggle per touch: the hand has to leave the door (and the cooldown pass) before it can toggle it again
        bool started = touching && !wasTouching && Time.time >= nextTouchTime;
        wasTouching = touching;
        if (started) nextTouchTime = Time.time + touchCooldown;
        return started;
    }

    // Whether a point is within touchDistance of the door's mesh box. The box is clamped in the mesh's own
    // space and the distance measured in the world, so it follows the door as it swings and stays exact
    // for the door's non-uniform scale
    private bool IsNearDoor(Vector3 handPosition)
    {
        Transform panel = doorPanel.transform;
        Vector3 closest = doorPanel.sharedMesh.bounds.ClosestPoint(panel.InverseTransformPoint(handPosition));
        return (panel.TransformPoint(closest) - handPosition).sqrMagnitude <= touchDistance * touchDistance;
    }

    // Hand interactors on this player's own rig. Sockets (holsters, racks) aren't hands, and remote players'
    // copies are skipped so each touch is only counted by the client whose player made it
    private static bool IsLocalHand(XRBaseInteractor interactor)
    {
        if (interactor == null || !interactor.isActiveAndEnabled || interactor is XRSocketInteractor) return false;

        RealtimeView view = interactor.GetComponentInParent<RealtimeView>();
        return view == null || view.realtime == null || !view.realtime.connected || view.isOwnedLocallyInHierarchy;
    }

    private void SetOpen(bool open)
    {
        isOpen = open;

        // Online, write it to the shared model so everyone else's door follows
        if (model != null && realtime != null && realtime.connected)
            model.isOpen = open;
    }

    protected override void OnRealtimeModelReplaced(DoorStateModel previousModel, DoorStateModel currentModel)
    {
        if (previousModel != null)
            previousModel.isOpenDidChange -= OnIsOpenDidChange;

        if (currentModel != null)
        {
            // The first client in the room seeds the shared state with how its door currently is
            if (currentModel.isFreshModel)
                currentModel.isOpen = isOpen;

            isOpen = currentModel.isOpen;
            currentModel.isOpenDidChange += OnIsOpenDidChange;
        }
    }

    private void OnIsOpenDidChange(DoorStateModel changedModel, bool value)
    {
        isOpen = value;
    }
}
