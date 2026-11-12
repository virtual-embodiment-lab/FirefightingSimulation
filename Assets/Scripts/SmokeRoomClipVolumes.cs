using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Room boxes that contained smoke is clipped to. Every particle of the
/// Only_smoke_contained effect is drawn only inside the room that holds its
/// center (see SmokeRoomClip.hlsl), so smoke never shows through a wall,
/// ceiling or floor. Owners such as a fire's smoke front register the rooms
/// they spread through and remove them when they are done.
/// </summary>
public static class SmokeRoomClipVolumes
{
    public const int MaxRooms = 64;

    /// <summary>
    /// One upright box turned about the vertical axis. A room that is not a
    /// rectangle is several boxes with the same group; smoke may fill any of them.
    /// </summary>
    public struct Room
    {
        public Vector3 center;
        public Vector3 halfSize;
        /// <summary>Unit vector along the box's local x axis, in the floor plane.</summary>
        public Vector3 right;
        /// <summary>Boxes of the same owner with the same group form one room.</summary>
        public int group;
    }

    static readonly int CentersId = Shader.PropertyToID("_SmokeRoomCenters");
    static readonly int ExtentsId = Shader.PropertyToID("_SmokeRoomExtents");
    static readonly int GroupsId = Shader.PropertyToID("_SmokeRoomGroups");
    static readonly int CountId = Shader.PropertyToID("_SmokeRoomCount");
    static readonly int FadeId = Shader.PropertyToID("_SmokeRoomFade");

    static readonly Dictionary<object, List<Room>> roomsByOwner = new Dictionary<object, List<Room>>();
    static readonly List<Room> uploadOrder = new List<Room>();
    static readonly Vector4[] centers = new Vector4[MaxRooms];
    static readonly Vector4[] extents = new Vector4[MaxRooms];
    static readonly Vector4[] groups = new Vector4[MaxRooms];
    static float edgeFade = 0.08f;
    static bool warnedFull;

    /// <summary>
    /// Width, in meters, over which smoke fades out at a room's edge. Rooms are
    /// grown by the same amount, so smoke reaches the inside face of a wall at
    /// full strength and fades out inside the wall itself.
    /// </summary>
    public static float EdgeFade
    {
        get => edgeFade;
        set
        {
            edgeFade = Mathf.Max(0.001f, value);
            Upload();
        }
    }

    public static void SetRooms(object owner, List<Room> rooms)
    {
        roomsByOwner[owner] = new List<Room>(rooms);
        Upload();
    }

    public static void Remove(object owner)
    {
        if (roomsByOwner.Remove(owner))
            Upload();
    }

    public static void Clear()
    {
        roomsByOwner.Clear();
        Upload();
    }

    /// <summary>
    /// Sends the boxes to the shader, each room's boxes next to each other. For
    /// every box, _SmokeRoomGroups holds the index of its room's first box and
    /// how many boxes that room has.
    /// </summary>
    static void Upload()
    {
        uploadOrder.Clear();
        foreach (List<Room> rooms in roomsByOwner.Values)
        {
            int ownerStart = uploadOrder.Count;
            uploadOrder.AddRange(rooms);
            uploadOrder.Sort(ownerStart, rooms.Count, Comparer<Room>.Create((a, b) => a.group.CompareTo(b.group)));
            for (int i = ownerStart; i < uploadOrder.Count; i++)
            {
                // Groups are only unique within one owner.
                Room room = uploadOrder[i];
                room.group += ownerStart * 1000;
                uploadOrder[i] = room;
            }
        }

        int count = Mathf.Min(uploadOrder.Count, MaxRooms);
        if (uploadOrder.Count > MaxRooms && !warnedFull)
        {
            Debug.LogWarning("SmokeRoomClipVolumes holds at most " + MaxRooms + " room boxes. Smoke beyond them is clipped to the nearest listed room.");
            warnedFull = true;
        }

        int start = 0;
        while (start < count)
        {
            int end = start + 1;
            while (end < count && uploadOrder[end].group == uploadOrder[start].group)
                end++;

            for (int i = start; i < end; i++)
            {
                Room room = uploadOrder[i];
                centers[i] = new Vector4(room.center.x, room.center.y, room.center.z, room.right.x);
                extents[i] = new Vector4(room.halfSize.x, room.halfSize.y, room.halfSize.z, room.right.z);
                groups[i] = new Vector4(start, end - start, 0f, 0f);
            }

            start = end;
        }

        Shader.SetGlobalVectorArray(CentersId, centers);
        Shader.SetGlobalVectorArray(ExtentsId, extents);
        Shader.SetGlobalVectorArray(GroupsId, groups);
        Shader.SetGlobalFloat(CountId, count);
        Shader.SetGlobalFloat(FadeId, edgeFade);
    }
}
