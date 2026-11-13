// Minimal stand-ins for the packages the real project uses, so the Stickies code compiles in an
// empty project. Only the members WallProximityHaptics touches are present.
using UnityEngine;

public static class MonitorMode { public static bool IsActive => false; }

namespace Unity.XR.CoreUtils
{
    public class XROrigin : MonoBehaviour { public Camera Camera; }
}

namespace Normal.Realtime
{
    public class RealtimeAvatar : MonoBehaviour { }
    public class RealtimeAvatarManager : MonoBehaviour { public RealtimeAvatar localAvatar; }
}

namespace RootMotion.FinalIK
{
    public class VRIK : MonoBehaviour
    {
        public References references = new References();

        [System.Serializable]
        public class References
        {
            public Transform root, pelvis, spine, chest, neck, head;
            public Transform leftShoulder, leftUpperArm, leftForearm, leftHand;
            public Transform rightShoulder, rightUpperArm, rightForearm, rightHand;
            public Transform leftThigh, leftCalf, leftFoot, leftToes;
            public Transform rightThigh, rightCalf, rightFoot, rightToes;
        }
    }
}
