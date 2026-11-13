using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Firefighting.Haptics.Stickies;
using RootMotion.FinalIK;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Runs <see cref="WallProximityHaptics"/> end to end without hardware: simulated Stickies, real
/// Unity physics, and a simulated clock. A throwaway avatar and walls are built 5 km above the
/// open scene (so its geometry cannot interfere), walked through a series of situations, and
/// every Motor Command the component sends is checked. Nothing is saved to the scene.
///
/// From the Editor: Tools > Stickies > Run Self-Test (no hardware).
/// Headless (Unity closed on this project):
///   Unity.exe -batchmode -nographics -projectPath &lt;project&gt; -executeMethod StickiesSelfTest.RunBatch -logFile &lt;log&gt;
/// The process exits with code 0 when every check passes, 1 otherwise.
/// </summary>
public static class StickiesSelfTest
{
    [MenuItem("Tools/Stickies/Run Self-Test (no hardware)")]
    private static void RunFromMenu()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[Stickies] Leave Play mode before running the self-test.");
            return;
        }
        bool passed = Run(out string report);
        if (passed) Debug.Log(report);
        else Debug.LogError(report);
        EditorUtility.DisplayDialog("Stickies self-test",
            passed ? "All checks passed. Details are in the Console." : "Some checks failed. Details are in the Console.", "OK");
    }

    public static void RunBatch()
    {
        bool passed = false;
        try
        {
            passed = Run(out string report);
            Debug.Log(report);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
        EditorApplication.Exit(passed ? 0 : 1);
    }

    public static bool Run(out string report)
    {
        var harness = new Harness();
        try
        {
            harness.RunAll();
        }
        catch (Exception e)
        {
            harness.Fail("Self-test crashed", e.ToString());
        }
        finally
        {
            harness.Dispose();
        }
        report = harness.Report;
        return harness.Failures == 0;
    }

    private sealed class Harness : IDisposable
    {
        private const int Chest = 0, Back = 1, Left = 2, Right = 3;
        private const float Step = 1f / 30f;
        private static readonly Vector3 Origin = new Vector3(0f, 5000f, 0f);

        private readonly StringBuilder lines = new();
        private readonly List<(float time, int slot, byte[] payload)> sent = new();
        private readonly GameObject root;
        private readonly WallProximityHaptics haptics;
        private readonly Transform player, head, chest, leftArm, rightArm;
        private readonly Transform wallAhead, wallLeft, wardrobe;
        private float clock;
        private int checks;

        public int Failures { get; private set; }
        public string Report => $"[Stickies] Self-test: {checks - Failures}/{checks} checks passed\n{lines}";

        public Harness()
        {
            root = new GameObject("[Stickies Self-Test]");
            root.transform.position = Origin;

            player = Child(root.transform, "Player", Vector3.zero);
            chest = Child(player, "Chest", new Vector3(0f, 1.3f, 0f));
            leftArm = Child(player, "LeftUpperArm", new Vector3(-0.2f, 1.4f, 0f));
            rightArm = Child(player, "RightUpperArm", new Vector3(0.2f, 1.4f, 0f));
            head = Child(player, "Head", new Vector3(0f, 1.65f, 0f));
            VRIK vrik = player.gameObject.AddComponent<VRIK>();
            vrik.references.root = player;
            vrik.references.chest = chest;
            vrik.references.leftUpperArm = leftArm;
            vrik.references.rightUpperArm = rightArm;
            vrik.references.head = head;

            Transform walls = Child(root.transform, "Walls", Vector3.zero);
            wallAhead = Box(walls, "Wall A", new Vector3(6f, 3f, 0.2f));
            wallLeft = Box(walls, "Wall B", new Vector3(0.2f, 3f, 6f));
            wardrobe = Box(root.transform, "Wardrobe", new Vector3(1f, 2f, 0.2f)); // furniture, not a wall

            var host = new GameObject("Haptics");
            host.transform.SetParent(root.transform, false);
            haptics = host.AddComponent<WallProximityHaptics>();
            Configure(so =>
            {
                so.FindProperty("transport").enumValueIndex = (int)StickyTransportMode.Simulated;
                so.FindProperty("identifyOnConnect").boolValue = false;
                so.FindProperty("keepAcrossScenes").boolValue = false;
                so.FindProperty("logZoneChanges").boolValue = false;
                so.FindProperty("headOverride").objectReferenceValue = head;
            });
            haptics.CommandSent += (slot, payload) =>
            {
                int index = 0;
                while (index < haptics.Slots.Count && haptics.Slots[index] != slot) index++;
                sent.Add((clock, index, payload));
            };

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideFlags.HideAndDontSave;
            HideAll();
        }

        public void RunAll()
        {
            ProximityZone touching = haptics.Zones[0];
            ProximityZone near = haptics.Zones[1];
            byte[] nearBurst = StickyProtocol.Play(0, near.pulseMs, near.burstPulses, near.gapMs, near.intensity);
            byte[] touchBurst = StickyProtocol.Play(0, touching.pulseMs, touching.burstPulses, touching.gapMs, touching.intensity);

            // Pairing
            Advance(1f);
            Check("All four slots pair with simulated Stickies",
                haptics.Slots.All(s => s.connection == "Ready"),
                string.Join(", ", haptics.Slots.Select(s => s.connection)));

            // Alert On Approach (default)
            Settle();
            Advance(3f);
            Check("No buzz when no wall is nearby", sent.Count == 0, Describe());

            PlaceAhead(1.0f);
            Advance(3f);
            Check("No buzz for a wall 1.0 m ahead", sent.Count == 0, Describe());

            PlaceAhead(0.5f);
            Advance(0.2f);
            Check("One gentle buzz on the chest when a wall comes within 0.5 m",
                Plays(Chest) == 1 && SameBytes(LastPlay(Chest), nearBurst), Describe());
            Check("Only the chest buzzes for a wall straight ahead", sent.All(s => s.slot == Chest), Describe());

            int before = sent.Count;
            Advance(5f);
            Check("Silent while standing next to the wall (5 s)", sent.Count == before, Describe());

            before = sent.Count;
            for (int i = 0; i < 8; i++)
            {
                PlaceAhead(i % 2 == 0 ? 0.58f : 0.63f);
                Advance(0.3f);
            }
            Check("Swaying across the zone edge does not re-buzz", sent.Count == before, Describe());

            PlaceAhead(0.2f);
            Advance(0.2f);
            Check("Three strong pulses when touching the wall (0.2 m)",
                Plays(Chest) == 2 && SameBytes(LastPlay(Chest), touchBurst), Describe());

            before = Plays(Chest);
            Advance(3f);
            Check("Silent while touching (3 s)", Plays(Chest) == before, Describe());

            HideAll();
            before = sent.Count;
            Advance(2f);
            Check("Silent when moving away", sent.Count == before, Describe());

            PlaceAhead(0.5f);
            Advance(0.2f);
            Check("Buzzes again on the next approach", Plays(Chest) == 3, Describe());

            // Direction
            Settle();
            PlaceLeft(0.4f);
            Advance(0.5f);
            Check("Wall on the left buzzes only the left-arm Sticky",
                Plays(Left) == 1 && sent.All(s => s.slot == Left), Describe());

            Settle();
            head.localRotation = Quaternion.Euler(0f, -90f, 0f); // looking left, body still forward
            PlaceAhead(0.5f);
            Advance(0.5f);
            Check("Cues follow the body, not the head (head turned 90 degrees)",
                Plays(Chest) == 1 && Plays(Left) == 0, Describe());
            head.localRotation = Quaternion.identity;

            Settle();
            player.localRotation = Quaternion.Euler(0f, -90f, 0f); // body turned to face world -X
            wallAhead.gameObject.SetActive(true);
            wallAhead.SetPositionAndRotation(chest.position + Vector3.forward * (0.7f + 0.1f), Quaternion.identity);
            Advance(0.5f);
            Check("Turning the body hands the cue to another Sticky (wall now on the right)",
                Plays(Right) == 1 && Plays(Chest) == 0, Describe());
            player.localRotation = Quaternion.identity;

            // What counts as a wall
            Settle();
            wardrobe.gameObject.SetActive(true);
            wardrobe.SetPositionAndRotation(chest.position + player.forward * (0.3f + 0.1f), player.rotation);
            Advance(1f);
            Check("Tall furniture alone does not buzz", sent.Count == 0, Describe());
            PlaceAhead(0.55f);
            Advance(0.5f);
            Check("A wall behind furniture is still felt", Plays(Chest) == 1, Describe());

            // Options
            Settle();
            Configure(so => so.FindProperty("remindEverySeconds").floatValue = 2f);
            PlaceAhead(0.5f);
            Advance(5f);
            Check("Remind Every 2 s repeats the buzz while still near (3 in 5 s)", Plays(Chest) == 3, Describe());
            Configure(so => so.FindProperty("remindEverySeconds").floatValue = 0f);

            Settle();
            Configure(so => so.FindProperty("feedbackStyle").enumValueIndex = (int)StickyFeedbackStyle.ContinuousProximity);
            PlaceAhead(0.5f);
            Advance(3.5f);
            int renewals = Plays(Chest);
            HideAll();
            Advance(0.5f);
            Check("Continuous Proximity keeps pulsing near a wall, then sends Stop",
                renewals >= 3 && Stops(Chest) == 1, $"plays={renewals}; {Describe()}");
            Configure(so => so.FindProperty("feedbackStyle").enumValueIndex = (int)StickyFeedbackStyle.AlertOnApproach);

            Settle();
            PlaceAhead(0.5f);
            haptics.HapticsEnabled = false;
            Advance(1f);
            Check("Haptics Enabled = false keeps every Sticky silent", Plays(Chest) == 0, Describe());
            haptics.HapticsEnabled = true;
        }

        public void Fail(string name, string detail)
        {
            checks++;
            Failures++;
            lines.AppendLine($"FAIL  {name}\n      -> {detail}");
        }

        public void Dispose()
        {
            if (haptics != null) haptics.Shutdown();
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
        }

        // -------------------------------------------------------------- helpers

        private void Check(string name, bool ok, string detail)
        {
            checks++;
            if (ok)
            {
                lines.AppendLine($"PASS  {name}");
                return;
            }
            Failures++;
            lines.AppendLine($"FAIL  {name}\n      -> {detail}");
        }

        private void Advance(float seconds)
        {
            float end = clock + seconds;
            while (clock < end)
            {
                Physics.SyncTransforms();
                haptics.Tick(clock);
                clock += Step;
            }
        }

        /// <summary>Clears every wall, lets the component return to "no wall", forgets commands.</summary>
        private void Settle()
        {
            HideAll();
            Advance(1.5f);
            sent.Clear();
        }

        private void HideAll()
        {
            wallAhead.gameObject.SetActive(false);
            wallLeft.gameObject.SetActive(false);
            wardrobe.gameObject.SetActive(false);
        }

        /// <summary>Wall face <paramref name="gap"/> metres in front of the chest bone.</summary>
        private void PlaceAhead(float gap)
        {
            wallAhead.gameObject.SetActive(true);
            wallAhead.SetPositionAndRotation(chest.position + player.forward * (gap + 0.1f), player.rotation);
        }

        /// <summary>Wall face <paramref name="gap"/> metres left of the left upper arm.</summary>
        private void PlaceLeft(float gap)
        {
            wallLeft.gameObject.SetActive(true);
            wallLeft.SetPositionAndRotation(leftArm.position - player.right * (gap + 0.1f), player.rotation);
        }

        private void Configure(Action<SerializedObject> edit)
        {
            var so = new SerializedObject(haptics);
            edit(so);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private int Plays(int slot) => sent.Count(s => s.slot == slot && s.payload[0] == 0x01);
        private int Stops(int slot) => sent.Count(s => s.slot == slot && s.payload[0] == 0x00);
        private byte[] LastPlay(int slot) => sent.LastOrDefault(s => s.slot == slot && s.payload[0] == 0x01).payload;

        private static bool SameBytes(byte[] a, byte[] b) => a != null && b != null && a.SequenceEqual(b);

        private string Describe()
        {
            string commands = sent.Count == 0
                ? "no commands"
                : string.Join(", ", sent.Select(s => $"t={s.time:0.00} {haptics.Slots[s.slot].label} {StickyProtocol.ToHex(s.payload)}"));
            string distances = string.Join(", ", haptics.Slots.Select(s =>
                $"{s.label.Split(' ')[0]}={(float.IsInfinity(s.wallDistance) ? "-" : s.wallDistance.ToString("0.00"))}/{s.zone}"));
            return $"{commands} | {distances}";
        }

        private static Transform Child(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        private static Transform Box(Transform parent, string name, Vector3 size)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localScale = size;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<MeshRenderer>());
            return go.transform;
        }
    }
}
