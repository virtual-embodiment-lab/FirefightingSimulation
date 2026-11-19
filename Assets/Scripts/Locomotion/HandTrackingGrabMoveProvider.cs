using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;

/// <summary>
/// Hand-tracking equivalent of the XR Interaction Toolkit Grab Move Provider.
/// A grab (a full fist, see <see cref="HandGrip.IsFullFist"/>)
/// anchors the hand in the world while the XR Origin moves opposite the hand motion.
/// A grab keeps the direction of its first fore-aft stroke until the hand opens.
///
/// Only one hand drives at a time. The first hand to grab keeps control until it opens;
/// the other hand is ignored meanwhile. If the other hand is still grabbing when the first
/// lets go, it takes over from where it is, so crawling hand over hand never jumps.
///
/// Adapted from feature/grab-to-move for low-posture locomotion. The companion
/// walking-stick component owns posture selection; its move provider supplies gravity.
/// Settings are editable once this component is on the Walking Stick object in the scene;
/// otherwise the walking stick adds it at runtime with these defaults.
/// </summary>
[AddComponentMenu("XR/Locomotion/Hand Tracking Grab Move Provider", 11)]
public sealed class HandTrackingGrabMoveProvider : ConstrainedMoveProvider
{
    const int k_NoHand = 0;
    const int k_LeftHand = 1;
    const int k_RightHand = 2;

    [Header("Speed")]
    [SerializeField]
    [Tooltip("Crawl speed: how far the player moves per meter the grabbing hand is pulled. Higher is faster.")]
    [Min(0f)]
    float m_MoveFactor = 3.5f;

    [SerializeField]
    [Tooltip("Maximum player movement that can be applied in one frame, in meters. Set to zero for no limit.")]
    [Min(0f)]
    float m_MaxMovementPerFrame = 0.4f;

    [Header("Feel")]
    [SerializeField]
    [Tooltip("Tracking movement smaller than this distance is ignored to prevent hand jitter from moving the player.")]
    [Min(0f)]
    float m_HandMovementDeadzone = 0.005f;

    [SerializeField]
    [Tooltip("Smooths the tracked hand position over this many seconds. Set to zero to disable smoothing.")]
    [Min(0f)]
    float m_HandPositionSmoothing = 0.02f;

    [SerializeField]
    [Tooltip("Ignore movement that would move the player backward relative to the headset. Disable if intentional backward locomotion is needed.")]
    bool m_PreventBackwardMovement = false;

    [SerializeField]
    [Tooltip("Allows this provider to be disabled without removing the hand-tracking setup.")]
    bool m_CanMove = true;

    readonly List<XRHandSubsystem> m_HandSubsystems = new List<XRHandSubsystem>();
    readonly List<XRBaseInteractor> m_HandInteractors = new List<XRBaseInteractor>();

    XRHandSubsystem m_HandSubsystem;
    Transform m_InteractorSearchRoot;
    bool m_LeftGrabbing;
    bool m_RightGrabbing;
    int m_ActiveHand;
    bool m_HasPreviousPosition;
    int m_PreviousActiveHand;
    Vector3 m_GrabStartPosition;
    Vector3 m_GrabForward;
    float m_GrabDirection;
    Vector3 m_PreviousMovePosition;
    Vector3 m_SmoothedHandPosition;
    HandWalkingStickLocomotion m_WalkingStick;

    protected override void Awake()
    {
        base.Awake();
        m_WalkingStick = GetComponent<HandWalkingStickLocomotion>();
        useGravity = false;
        enableFreeYMovement = false;
    }

    void OnDisable() => ResetMovementState();

    // The continuous provider already handles gravity. Do not start another locomotion
    // event for an idle grab, or let it contribute a second gravity step.
    protected override void MoveRig(Vector3 translationInWorldSpace)
    {
        if (translationInWorldSpace.sqrMagnitude > Mathf.Epsilon)
            base.MoveRig(translationInWorldSpace);
    }

    /// <summary>
    /// The ratio of actual movement distance to hand movement distance.
    /// </summary>
    public float moveFactor
    {
        get => m_MoveFactor;
        set => m_MoveFactor = Mathf.Max(0f, value);
    }

    /// <summary>
    /// Whether this provider is allowed to move the XR Origin.
    /// </summary>
    public bool canMove
    {
        get => m_CanMove;
        set => m_CanMove = value;
    }

    void OnValidate()
    {
        m_MoveFactor = Mathf.Max(0f, m_MoveFactor);
        m_HandMovementDeadzone = Mathf.Max(0f, m_HandMovementDeadzone);
        m_HandPositionSmoothing = Mathf.Max(0f, m_HandPositionSmoothing);
        m_MaxMovementPerFrame = Mathf.Max(0f, m_MaxMovementPerFrame);
    }

    protected override Vector3 ComputeDesiredMove(out bool attemptingMove)
    {
        attemptingMove = false;

        var xrOriginComponent = mediator != null ? mediator.xrOrigin : null;
        var xrOrigin = xrOriginComponent?.Origin;
        if (!m_CanMove || m_WalkingStick == null || !m_WalkingStick.isActiveAndEnabled || !m_WalkingStick.IsCrawling ||
            xrOrigin == null || !TryGetRunningHandSubsystem(out var subsystem))
        {
            ResetMovementState();
            return Vector3.zero;
        }

        RefreshHandInteractors(xrOrigin.transform);

        var leftGrabbing = TryGetAvailableGrabbingHandPose(
            subsystem.leftHand,
            InteractorHandedness.Left,
            ref m_LeftGrabbing,
            out var leftPosition);
        var rightGrabbing = TryGetAvailableGrabbingHandPose(
            subsystem.rightHand,
            InteractorHandedness.Right,
            ref m_RightGrabbing,
            out var rightPosition);

        // One hand at a time: the hand in control keeps it until it lets go. Only then can
        // the other hand (if it is grabbing) take over; both hands never drive together.
        if ((m_ActiveHand == k_LeftHand && !leftGrabbing) || (m_ActiveHand == k_RightHand && !rightGrabbing))
            m_ActiveHand = k_NoHand;
        if (m_ActiveHand == k_NoHand)
            m_ActiveHand = leftGrabbing ? k_LeftHand : rightGrabbing ? k_RightHand : k_NoHand;

        if (m_ActiveHand == k_NoHand)
        {
            ResetMovementState();
            return Vector3.zero;
        }

        var currentHandPosition = m_ActiveHand == k_LeftHand ? leftPosition : rightPosition;
        return ComputePull(currentHandPosition, m_ActiveHand, xrOrigin.transform, Time.deltaTime, out attemptingMove);
    }

    Vector3 ComputePull(Vector3 currentHandPosition, int activeHand, Transform origin, float deltaTime, out bool attemptingMove)
    {
        attemptingMove = false;
        // Re-anchor when a grab begins or when control passes to the other hand,
        // so the hand taking over starts from where it is instead of jumping.
        if (!m_HasPreviousPosition || activeHand != m_PreviousActiveHand)
        {
            m_HasPreviousPosition = true;
            m_PreviousActiveHand = activeHand;
            m_GrabStartPosition = currentHandPosition;
            var camera = mediator.xrOrigin.Camera;
            var facing = camera != null ? camera.transform : origin;
            m_GrabForward = Vector3.ProjectOnPlane(facing.forward, Vector3.up);
            // When the player looks straight down, the head's right axis still gives its heading.
            if (m_GrabForward.sqrMagnitude < 0.01f)
                m_GrabForward = Vector3.Cross(facing.right, Vector3.up);
            m_GrabForward.Normalize();
            m_GrabDirection = 0f;
            m_PreviousMovePosition = Vector3.zero;
            m_SmoothedHandPosition = currentHandPosition;
            return Vector3.zero;
        }

        // Filter the hand pose before taking a delta. This keeps small tracking noise from
        // becoming locomotion while still allowing a deliberate pull to accumulate smoothly.
        if (m_HandPositionSmoothing > 0f)
        {
            var smoothing = 1f - Mathf.Exp(-deltaTime / m_HandPositionSmoothing);
            currentHandPosition = Vector3.Lerp(m_SmoothedHandPosition, currentHandPosition, smoothing);
        }
        m_SmoothedHandPosition = currentHandPosition;

        // Transform the offset, so moving the rig itself does not feed back into the pull.
        var space = mediator.xrOrigin.CameraFloorOffsetObject != null
            ? mediator.xrOrigin.CameraFloorOffsetObject.transform : origin;
        var handOffset = m_GrabStartPosition - currentHandPosition;
        handOffset.y = 0f;
        var movePosition = Vector3.ProjectOnPlane(space.TransformVector(handOffset), Vector3.up);
        var moveDelta = movePosition - m_PreviousMovePosition;
        float forwardDelta = Vector3.Dot(moveDelta, m_GrabForward);

        if (m_GrabDirection == 0f && Mathf.Abs(forwardDelta) > m_HandMovementDeadzone &&
            (!m_PreventBackwardMovement || forwardDelta > 0f))
        {
            m_GrabDirection = Mathf.Sign(forwardDelta);
        }

        // Keep the farthest point reached along the chosen direction. Returning the hand
        // cannot reverse the rig or add extra travel on the way back to that point.
        if (forwardDelta * m_GrabDirection <= 0f || (m_PreventBackwardMovement && forwardDelta < 0f))
            moveDelta -= m_GrabForward * forwardDelta;

        // Accumulate small pulls instead of discarding them every frame.
        if (moveDelta.sqrMagnitude <= m_HandMovementDeadzone * m_HandMovementDeadzone)
            return Vector3.zero;

        m_PreviousMovePosition += moveDelta;
        var move = moveDelta * m_MoveFactor;
        if (m_MaxMovementPerFrame > 0f)
            move = Vector3.ClampMagnitude(move, m_MaxMovementPerFrame);

        attemptingMove = move.sqrMagnitude > Mathf.Epsilon;
        return move;
    }

    bool TryGetRunningHandSubsystem(out XRHandSubsystem subsystem)
    {
        if (m_HandSubsystem != null && m_HandSubsystem.running)
        {
            subsystem = m_HandSubsystem;
            return true;
        }

        m_HandSubsystems.Clear();
        SubsystemManager.GetSubsystems(m_HandSubsystems);
        for (var index = 0; index < m_HandSubsystems.Count; ++index)
        {
            if (!m_HandSubsystems[index].running)
                continue;

            m_HandSubsystem = m_HandSubsystems[index];
            subsystem = m_HandSubsystem;
            return true;
        }

        m_HandSubsystem = null;
        subsystem = null;
        return false;
    }

    void RefreshHandInteractors(Transform xrOrigin)
    {
        if (xrOrigin == m_InteractorSearchRoot && m_HandInteractors.Count > 0)
            return;

        m_InteractorSearchRoot = xrOrigin;
        m_HandInteractors.Clear();
        m_HandInteractors.AddRange(xrOrigin.GetComponentsInChildren<XRBaseInteractor>(true));
    }

    bool TryGetAvailableGrabbingHandPose(
        XRHand hand,
        InteractorHandedness handedness,
        ref bool wasGrabbing,
        out Vector3 handPosition)
    {
        var handSide = handedness == InteractorHandedness.Left ? Handedness.Left : Handedness.Right;
        if (HandHasSelection(handedness) || HandSprayGesture.IsSprayingWith(handSide))
        {
            // A hand holding an object or spraying must not also act as a locomotion grab.
            // Clear the anchor so releasing the object cannot reuse a stale pull position.
            wasGrabbing = false;
            handPosition = default;
            return false;
        }

        return TryGetGrabbingHandPose(hand, ref wasGrabbing, out handPosition);
    }

    bool HandHasSelection(InteractorHandedness handedness)
    {
        for (var index = 0; index < m_HandInteractors.Count; ++index)
        {
            var interactor = m_HandInteractors[index];
            if (interactor.handedness == handedness && interactor.hasSelection)
                return true;
        }

        return false;
    }

    static bool TryGetGrabbingHandPose(XRHand hand, ref bool wasGrabbing, out Vector3 handPosition)
    {
        handPosition = default;
        if (!hand.isTracked)
        {
            wasGrabbing = false;
            return false;
        }

        wasGrabbing = HandGrip.IsFullFist(hand, wasGrabbing);
        if (!wasGrabbing)
            return false;

        // The palm moves steadily with the whole fist; fall back to the wrist.
        if (hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palmPose))
        {
            handPosition = palmPose.position;
            return true;
        }

        if (hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out var wristPose))
        {
            handPosition = wristPose.position;
            return true;
        }

        wasGrabbing = false;
        return false;
    }

    internal void ResetMovementState()
    {
        m_LeftGrabbing = false;
        m_RightGrabbing = false;
        m_ActiveHand = k_NoHand;
        m_HasPreviousPosition = false;
        m_PreviousActiveHand = k_NoHand;
        m_GrabStartPosition = default;
        m_GrabForward = default;
        m_GrabDirection = 0f;
        m_PreviousMovePosition = default;
        m_SmoothedHandPosition = default;
    }
}
