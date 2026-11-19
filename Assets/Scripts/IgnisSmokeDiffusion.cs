using System.Collections.Generic;
using Ignis;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;

/// <summary>
/// Keeps Ignis drawing the existing fire, and releases the plugin's OnlySmoke
/// blocks from each burning object. Blocks drift through open indoor air:
/// above a floor, below the ceiling, and clear of walls. An open doorway counts
/// as open air, so smoke can leave the fire's room and keep spreading.
/// </summary>
/// <remarks>
/// A scene turns this on by putting the component on its FlameEngine object.
/// It is not added automatically, so scenes with their own smoke keep it.
/// With Fire Sources left empty, every fire that starts burning releases smoke.
///
/// Ignis smoke particles are large camera-facing quads, so a block that stops
/// at a wall still draws smoke on the far side of it. With Contain Smoke on,
/// blocks play Only_smoke_contained, whose shader draws each particle only
/// inside the room that holds its center; a lingering particle is drawn only
/// inside the room it was released in. The rooms come from probing the
/// building's own walls, ceilings and floors, so any structure works without
/// setup. With Contain Flames also on, objects that burn with the Wild flames
/// play Flame_box_wild_contained, whose flames and embers are clipped to the
/// room of the object that is burning. Those spawn Flame Particles of the
/// plugin's flames, each Flame Brightness times as bright, which looks the same
/// for much less overdraw up close.
///
/// The first volume of a fire is its plume: it stays over the fire and keeps
/// churning, so the source of the smoke looks aggressive. It plays
/// Only_smoke_plume, which holds 250 particles instead of 1000, each made more
/// opaque by Plume Opacity, because up close every plume particle covers most
/// of the view and it is the most expensive smoke to draw. The other volumes
/// play Only_smoke_lingering: each volume heads for the nearest patch of air
/// that does not hold enough smoke yet, following the open floor around
/// furniture and through doorways, holds over it while it leaves particles
/// there, and moves on once the patch is full. The particles stay for minutes
/// instead of being replaced every few seconds. Each room fills in rings
/// outward from where its smoke came in, the fire or a doorway, the same in
/// every open direction. The
/// smoke fills every room it reaches evenly, from the ceiling down as the
/// volumes bank down, and the particle count is bounded by the size of the
/// building rather than by how long the fire burns.
///
/// The open air is found on a grid of cells. A doorway is often narrower than
/// two cells, so a cell whose center sits by a door jamb is moved into the
/// opening, and cells only join where a volume can pass between them. The
/// smoke so reaches every room on the fire's floor through its doorways but
/// never through a wall.
///
/// The smoke has an edge that moves out from the fire along those cells at
/// Front Speed, and smoke is only left behind it. It so spreads with a
/// direction: across the burning room, out through its doorways, down each
/// hallway as a line, and into every room the line passes. Over each patch the
/// smoke layer starts at the ceiling when the edge arrives and deepens from
/// there. Outside the burning room the edge, the volumes and the deepening all
/// move at Other Room Speed, and each patch fills only Other Room Smoke of
/// what it would hold, so the smoke thins and slows as it spreads. Spreading
/// Volumes of the volumes follow the edge out of the burning room as soon as it
/// leaves, riding the top of the smoke layer, while the rest fill the burning
/// room and then deepen the smoke behind the edge. Volumes line up with each
/// doorway and pass through the middle of it, and the air by a doorway holds
/// Doorway Smoke, so the smoke funnels through the openings and thins out in
/// the rooms beyond.
///
/// The burning room's windows are its flow path out. Once the smoke layer
/// over a window has banked down past its top, smoke pours out of the window's
/// upper part, deeper and stronger as the layer deepens, and turns upward
/// outside (Window_smoke_vent). Only the burning room's windows vent; the
/// rooms the smoke spreads to keep theirs shut.
/// </remarks>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-50)]
public class IgnisSmokeDiffusion : MonoBehaviour
{
    const float FinalVisibleAlpha = 0.001f;
    const float CastSkin = 0.02f;
    const float CellSize = 0.6f;
    const float MaxRoomRadius = 22f;
    const int MaxOpenCells = 2400;
    const string ContainedSmokeResource = "Smoke/Only_smoke_contained";
    const string LingeringSmokeResource = "Smoke/Only_smoke_lingering";
    // Exposed properties of Only_smoke_lingering, see ContainedSmokeBuilder.
    const string SpawnRateProperty = "SmokeSpawnRate";
    const string LifetimeProperty = "SmokeLifetime";
    const string CeilingProperty = "SmokeCeiling";
    const string PlumeSmokeResource = "Smoke/Only_smoke_plume";
    // A lingering particle is released at least this far under the ceiling
    // above its volume, so it stays inside the room for its whole life.
    const float LingerCeilingGap = 0.3f;
    // Lingering smoke is counted in patches of air this wide and this tall.
    const float FillCellSize = 1.2f;
    const float FillBandHeight = 0.8f;
    // Seconds between updates of the field that steers volumes to unfilled air.
    const float FillFieldSeconds = 0.25f;
    // Volumes head for air the edge of the smoke reaches within this many
    // seconds, so they wait at the edge instead of wandering behind it.
    const float FrontLeadSeconds = 4f;
    // Lingering volumes drift as spheres this share of Body Radius: the size
    // the open-air grid checks doorways with, so they fit through every
    // doorway the grid joins rooms by instead of catching on the frame.
    const float LingeringBodyShare = 0.5f;
    // Seconds a volume heading somewhere stays down at cruise height after it
    // runs into a wall, so it passes under a door header into the next room.
    const float DuckSeconds = 2f;
    // Spreading volumes keep to this top share of the smoke layer.
    const float SpreadingLayerDepth = 0.2f;
    // A volume heading somewhere looks this many cells ahead for the farthest
    // one it can reach in a straight line, and looks again this often.
    const int AimCells = 8;
    const float AimSeconds = 0.25f;
    // A room fills in rings this wide, measured along the way the smoke
    // travels, outward from where its smoke came in.
    const float RingMeters = 1.5f;
    // A volume slows down within this distance of the air it is filling and
    // holds there until it is full, then moves on to the next.
    const float HoldEaseMeters = 0.3f;
    // Longest a volume holds on one cell before it gives up and wanders.
    const float HoldSeconds = 2f;
    // Fill keys carry the height band times this plus the room's slot.
    const int RoomKeyStride = 128;
    const string ContainedFlameResource = "Fire/Flame_box_wild_contained";
    // Exposed properties of Flame_box_wild_contained, see ContainedSmokeBuilder.
    const string FlameShareProperty = "FlameParticleShare";
    const string FlameBrightnessProperty = "FlameBrightness";
    // Room boxes are measured this far under the ceiling, above door headers,
    // so rays from a room do not leave it through a doorway.
    const float RoomProbeBelowCeiling = 0.25f;
    const float RoomProbeRange = 40f;
    const float MinRoomHalfSize = 0.25f;
    const string WindowVentResource = "Smoke/Window_smoke_vent";
    // A window vents through its upper part only, at most this share of its
    // height: below the neutral plane, fresh air flows in instead.
    const float VentHeightShare = 0.5f;
    // The strip smoke leaves a window from: this share of the window's width,
    // never thinner than VentMinStrip, VentStripDepth deep, just outside the glass.
    const float VentWidthShare = 0.9f;
    const float VentMinStrip = 0.1f;
    const float VentStripDepth = 0.12f;
    // How far either side of a window is checked for the room it belongs to.
    const float VentSideProbe = 0.4f;
    // The vent graph keeps only smoke released inside its Spread sphere, which
    // Ignis uses for the part of an object the fire has reached. Left at its
    // default, around the world origin, it drops every particle. It is
    // centered on the strip, this much wider than the strip's farthest corner.
    const float VentSpreadMargin = 0.5f;
    // RoomGeneration puts each room's furniture and windows under a GameObject
    // named Room_<n> [type].
    const string GeneratedRoomPrefix = "Room_";
    // Seconds a window takes to vent at the strength the smoke layer calls for.
    const float VentRampSeconds = 2f;
    const float VentTurbulence = 0.65f;
    // The vent effect thins out beyond the first distance and is not drawn
    // beyond the second, in meters from the camera.
    const float VentThinDistance = 15f;
    const float VentCullDistance = 60f;
    // Exposed properties of Window_smoke_vent. WindowSmokePlumeController
    // drives the same graph and explains each one.
    static readonly int VentBoxCenterId = Shader.PropertyToID("Box_center");
    static readonly int VentBoxSizeId = Shader.PropertyToID("Box_size");
    static readonly int VentRotationId = Shader.PropertyToID("Rotation");
    static readonly int VentSpreadCenterId = Shader.PropertyToID("Spread_center");
    static readonly int VentSpreadRadiusId = Shader.PropertyToID("Spread_radius");
    static readonly int VentSmokeRateId = Shader.PropertyToID("SmokeVFXMultiplier");
    static readonly int VentAlphaId = Shader.PropertyToID("SmokeAlpha");
    static readonly int VentParticleSizeId = Shader.PropertyToID("SmokeParticleSize");
    static readonly int VentColorId = Shader.PropertyToID("SmokeColor");
    static readonly int VentLaunchId = Shader.PropertyToID("WindForce");
    static readonly int VentLaunchScaleId = Shader.PropertyToID("WindMultiplier");
    static readonly int VentDragId = Shader.PropertyToID("FlameSpeed");
    static readonly int VentTurbulenceId = Shader.PropertyToID("FlameLiveliness");
    static readonly int VentTurbulenceSpeedId = Shader.PropertyToID("FlameLivelinessSpeed");
    static readonly int VentLifetimeId = Shader.PropertyToID("FlameLength");
    static readonly int VentThinDistanceId = Shader.PropertyToID("LODMaxDist");
    static readonly int VentCullDistanceId = Shader.PropertyToID("CullingDist");
    static readonly int VentParticleShareId = Shader.PropertyToID("FireParticleMultiplier");
    static readonly int VentFlamesId = Shader.PropertyToID("FireVFXMultiplier");
    static readonly int VentEmbersId = Shader.PropertyToID("EmbersVFXMultiplier");
    static readonly int VentEmberBurstsId = Shader.PropertyToID("EmbersBurstVFXMultiplier");

    static readonly Vector2Int[] Neighbors =
    {
        new Vector2Int(1, 0),
        new Vector2Int(-1, 0),
        new Vector2Int(0, 1),
        new Vector2Int(0, -1)
    };

    [Header("Fire Sources")]
    [Tooltip("Fires that release drifting smoke while they are burning. An empty list uses a FlammableObject on this GameObject, or every burning fire in the scene when this object has none.")]
    public FlammableObject[] fireSources;

    [Tooltip("Optional scene cube already set up as Ignis OnlySmoke. Its color, alpha, particle size and multiplier are copied. Leave empty to use the values below.")]
    public FlammableObject smokeTemplate;

    [Tooltip("While this component owns a burning fire, Ignis keeps the flames and the drifting blocks carry the smoke.")]
    public bool muteEmbeddedSmoke = true;

    [Header("Flame Spread And Smoke")]
    [Tooltip("Extra drift speed, in meters per second, added for each meter per second of the fire's crawl speed.")]
    [Min(0f)]
    public float flameSpeedToSmoke = 1f;

    [Tooltip("Horizontal drift speed while a fire is burning, including a point fire whose crawl speed is zero.")]
    [Min(0f)]
    public float baseSmokeSpeed = 1.05f;

    [Tooltip("Upward speed mixed into every smoke volume. The plugin particles also rise on their own.")]
    [Min(0f)]
    public float riseSpeed = 0.55f;

    [Tooltip("How quickly a volume changes its horizontal direction, in degrees per second.")]
    [Min(0f)]
    public float wanderDegreesPerSecond = 16f;

    [Tooltip("Seconds of burning after which a fire's smoke volumes reach their full plugin alpha.")]
    [Min(0.01f)]
    public float fullStrengthSeconds = 8f;

    [Tooltip("Flame spread distance, in meters, that also counts as a fully established smoke output.")]
    [Min(0.01f)]
    public float fullSpreadMeters = 3f;

    [Range(0f, 1f)]
    [Tooltip("Fraction of the plugin alpha used the moment a volume appears.")]
    public float minimumSmokeStrength = 0.4f;

    [Header("Drifting Smoke Volumes")]
    [Tooltip("Seconds between new smoke volumes while the fire is burning.")]
    [Min(0.05f)]
    public float spawnInterval = 1f;

    [Tooltip("Highest number of live Ignis smoke volumes one fire may release.")]
    [Min(1)]
    public int maxVolumesPerFire = 36;

    [Tooltip("Highest number of live Ignis smoke volumes in the scene.")]
    [Min(1)]
    public int maxLiveVolumes = 72;

    [Tooltip("Height above the floor where a volume is released.")]
    public float heightOffset = 1.25f;

    [Tooltip("Sphere radius used to stop a volume on a collider. Triggers are ignored. Lingering volumes use half of it, the size doorways are checked with, so they fit through.")]
    [Min(0.05f)]
    public float bodyRadius = 0.3f;

    [Tooltip("Extra space kept between the top of a smoke volume and the ceiling, so Ignis particles stay inside the room.")]
    [Min(0f)]
    public float particleCeilingMargin = 0.45f;

    [Tooltip("Colliders that stop free drift. Smoke keeps the part of its speed that slides along the surface.")]
    public LayerMask obstacleMask = ~0;

    [Tooltip("Seconds a new volume takes to fade up to the current fire output.")]
    [Min(0f)]
    public float growthSeconds = 2.5f;

    [Tooltip("Seconds a volume takes to disappear after its fire goes out.")]
    [Min(0f)]
    public float fadeSeconds = 8f;

    [Tooltip("Extra fade delay, in seconds, given to the volume that has traveled farthest.")]
    [Min(0f)]
    public float fadeDistanceDelay = 1.5f;

    [Header("Plume And Lingering Smoke")]
    [Tooltip("Keeps the first smoke volume of each fire anchored over the fire, constantly churning and rising, so the source looks aggressive. Needs Contain Smoke.")]
    public bool keepPlumeAtFire = true;

    [Tooltip("How opaque each plume particle is, as a multiple of its usual opacity. The plume effect holds 250 particles instead of 1000, so each must be more opaque for it to look as thick. Costs no extra GPU time.")]
    [Range(1f, 6f)]
    public float plumeOpacity = 2.5f;

    [Tooltip("Smoke effect for the plume over the fire. Leave empty to use Resources/Smoke/Only_smoke_plume.")]
    public GameObject plumeSmokePrefab;

    [Tooltip("Size of the plume's particles, as a multiple of Smoke Particle Size. Up close every plume particle covers much of the view, so the GPU time grows with the square of this. Smaller reads as a narrower, more defined column. QuestPerformanceSettings lowers it on Quest. (The plume always runs full: even a fifth of its smoke rate still fills its 250 particles, so size is the lever.)")]
    [Range(0.2f, 1f)]
    public float plumeParticleSize = 1f;

    [Tooltip("The drifting volumes leave smoke behind them that lingers where it was put, instead of churning out new smoke all the time. A volume only adds smoke where the air does not hold enough yet, so the rooms fill evenly. Needs Contain Smoke. Off, every volume churns like the plume.")]
    public bool lingeringSmoke = true;

    [Tooltip("Smoke particles left in each 1.2 m patch of air, per 0.8 m of height. Volumes stop adding smoke to a patch once it holds this many. Higher fills more thickly and costs more GPU time.")]
    [Min(1)]
    public int particlesPerCell = 3;

    [Tooltip("Particles a second a drifting volume releases while it is in air that needs more smoke.")]
    [Min(0.1f)]
    public float releaseRate = 10f;

    [Tooltip("Most particles one drifting volume can leave. The lingering effect holds at most 128.")]
    [Range(1, 128)]
    public int particlesPerVolume = 128;

    [Tooltip("How opaque each lingering particle is, as a multiple of a churning particle at its peak. A patch of air holds a few lingering particles where churning volumes kept hundreds passing through, so each must be more opaque for the smoke to look as thick. Costs no extra GPU time.")]
    [Range(1f, 8f)]
    public float lingeringOpacity = 3f;

    [Tooltip("Seconds a released particle lives. Keep it longer than the fire burns, so the smoke stays until the fire is out and its volume fades.")]
    [Min(1f)]
    public float lingerSeconds = 600f;

    [Tooltip("Smoke effect for the drifting volumes. Leave empty to use Resources/Smoke/Only_smoke_lingering.")]
    public GameObject lingeringSmokePrefab;

    [Header("Spreading Through The House")]
    [Tooltip("How fast the edge of the lingering smoke moves out from the fire through the burning room, in meters per second. It follows the open floor, so outside the burning room it runs down a hallway as a line and turns into each room it passes. Smoke is only left behind the edge.")]
    [Min(0.05f)]
    public float frontSpeed = 0.75f;

    [Tooltip("How thick the lingering smoke gets in rooms other than the burning one, as a share of Particles Per Cell. Smoke thins out as it spreads away from the fire.")]
    [Range(0.1f, 1f)]
    public float otherRoomSmoke = 0.25f;

    [Tooltip("How thick the lingering smoke gets right by a doorway outside the burning room, on both sides of it, as a share of Particles Per Cell. Smoke is drawn to openings and pours through them, so it gathers thicker there and thins as it spreads into the room beyond.")]
    [Range(0.1f, 1f)]
    public float doorwaySmoke = 0.6f;

    [Tooltip("How fast smoke moves through rooms other than the burning one, as a share of its speed in the burning room: the drifting volumes, the edge of the smoke and how quickly the smoke layer deepens there. The smoke creeps through the rest of the house.")]
    [Range(0.1f, 1f)]
    public float otherRoomSpeed = 0.6f;

    [Tooltip("Share of the drifting volumes that carry smoke on to the rest of the house as soon as the edge of the smoke leaves the burning room. They ride the top of the smoke layer, like the hot layer under the ceiling, and keep up with the edge. The others fill the burning room first, then deepen the smoke elsewhere. Every volume heads for the nearest air that needs smoke, so without this the smoke only leaves the burning room once it is full. Higher spreads through the house sooner and fills the burning room more slowly.")]
    [Range(0f, 1f)]
    public float spreadingVolumes = 0.5f;

    [Header("Banking Down")]
    [Tooltip("Height above the floor that the lowest smoke volumes settle to once the room has fully banked down. Their Ignis particles reach a little below this. Lower leaves less clear air at the floor.")]
    [Min(0f)]
    public float lowestSmokeHeight = 1.0f;

    [Tooltip("Seconds of burning for the smoke layer to grow from just under the ceiling down to Lowest Smoke Height.")]
    [Min(0.01f)]
    public float bankDownSeconds = 60f;

    [Tooltip("Speed, in meters per second, at which a volume sinks when the growing layer moves its place below it.")]
    [Min(0f)]
    public float sinkSpeed = 0.15f;

    [Header("Venting Through Windows")]
    [Tooltip("Pushes smoke out of the windows of each burning room once its smoke layer has banked down past the top of the window, the way hot smoke finds its way out of the room on fire. The rooms the smoke spreads to do not vent. Needs Contain Smoke.")]
    public bool ventThroughWindows = true;

    [Tooltip("Objects whose name starts with this are windows. Generated houses name theirs Window(Clone).")]
    public string windowName = "Window";

    [Tooltip("How far, in meters, the smoke layer reaches down past the top of a window when the window vents at full strength. Smoke leaves through the window's upper part only, at most its top half.")]
    [Min(0.05f)]
    public float ventFullDepth = 0.6f;

    [Tooltip("How much smoke a window releases at full strength, as Ignis's smoke multiplier.")]
    [Min(0f)]
    public float ventSmokeRate = 2.5f;

    [Tooltip("How opaque the smoke leaving a window is, as Ignis's smoke alpha.")]
    [Min(0f)]
    public float ventOpacity = 2f;

    [Tooltip("Size of the smoke particles leaving a window, as Ignis's particle size.")]
    [Min(0.01f)]
    public float ventParticleSize = 1.1f;

    [Tooltip("How much lighter the smoke outside is than the smoke in the room, as a multiple of the Plugin Smoke Appearance color. Above 1 for daylight on the smoke.")]
    [Min(0f)]
    public float ventBrightness = 1f;

    [Tooltip("Speed, in meters per second, at which smoke leaves the window before it turns upward.")]
    [Min(0f)]
    public float ventOutwardSpeed = 1.8f;

    [Tooltip("How quickly smoke that left a window turns upward. Higher turns it up closer to the wall.")]
    [Min(0.05f)]
    public float ventRise = 1.1f;

    [Tooltip("Longest a particle that left a window lives, in seconds. Most live shorter.")]
    [Min(0.1f)]
    public float ventLifetime = 6f;

    [Tooltip("Smoke effect for the windows. Leave empty to use Resources/Smoke/Window_smoke_vent.")]
    public VisualEffectAsset windowVentEffect;

    [Header("Wall Containment")]
    [Tooltip("Draws each smoke particle only inside the room it belongs to, so smoke never shows through a wall, ceiling or floor. The smoke's size, motion and spread are unchanged.")]
    public bool containSmoke = true;

    [Tooltip("Smoke effect whose shader clips particles to their room. Leave empty to use Resources/Smoke/Only_smoke_contained.")]
    public GameObject containedSmokePrefab;

    [Tooltip("Draws flames and embers only inside the room of the object that is burning, so fire never shows through a wall. Uses the rooms measured for Contain Smoke; a fire outside them is drawn as usual. Applies to every object that uses the plugin's Wild flames.")]
    public bool containFlames = true;

    [Tooltip("Flame effect whose shader clips flames and embers to the burning object's room. Leave empty to use Resources/Fire/Flame_box_wild_contained.")]
    public GameObject containedFlamePrefab;

    [Tooltip("Share of the Wild flame particles each burning object spawns. Up close the flames cover most of the view and cost several milliseconds a frame. 0.6 with Flame Brightness 1.25 looks the same. Needs Contain Flames.")]
    [Range(0.1f, 1f)]
    public float flameParticles = 0.6f;

    [Tooltip("How bright each Wild flame particle is, as a multiple of the plugin's, so fewer flames look as bright. Too high turns the orange edges yellow. Needs Contain Flames.")]
    [Range(1f, 3f)]
    public float flameBrightness = 1.25f;

    [Tooltip("Layers of the walls, ceilings and floors that bound a room. Generated buildings use Default. Keep furniture and players off these layers so they do not split a room.")]
    public LayerMask roomBoundaryMask = 1;

    [Tooltip("Width, in meters, over which smoke fades out at a wall. Smoke reaches the inside face of the wall at full strength and fades out inside the wall itself, so keep this below the wall thickness.")]
    [Min(0.01f)]
    public float wallFadeWidth = 0.08f;

    [Header("Plugin Smoke Appearance")]
    [Tooltip("Ignis multiplies this by Smoke Color Intensity. The product is the color sent to OnlySmoke.")]
    public Color smokeColor = new Color(0.02f, 0.02f, 0.02f, 1f);

    [Min(0f)]
    [Tooltip("Ignis multiplies this into the smoke color. Higher is lighter; kept moderate so the cloud stays dark but not solid black.")]
    public float smokeColorIntensity = 20f;

    [Min(0f)]
    public float smokeAlpha = 0.7f;

    [Min(0f)]
    public float smokeVFXMultiplier = 4f;

    [Min(0f)]
    public float smokeParticleSize = 4f;

    [Tooltip("World size of a generated OnlySmoke volume. Ignis emits through this whole box.")]
    public Vector3 generatedBlockScale = new Vector3(1.2f, 0.7f, 1.2f);

    readonly List<FireFront> fronts = new List<FireFront>();
    readonly RaycastHit[] boundaryHits = new RaycastHit[16];
    readonly RaycastHit[] castHits = new RaycastHit[32];
    Transform blockHolder;
    bool warnedAboutMissingEngine;
    bool loadedContainedSmokePrefab;
    bool loadedLingeringSmokePrefab;
    bool loadedPlumeSmokePrefab;
    bool loadedWindowVentEffect;
    float nextDiscoveryTime;

    class FireFront
    {
        public FlammableObject fire;
        public NetworkedFireState lifecycle;
        public RoomAir room;
        public Vector3 origin;
        public bool originLocked;
        public bool burning;
        public bool embeddedMuted;
        public float savedSmokeAlpha;
        public float savedSmokeMultiplier;
        public float burnSeconds;
        public float spawnCredit;
        public float flameOutput;
        public float farthestDistance;
        public float nextMapTime;
        public readonly List<SmokeVolume> volumes = new List<SmokeVolume>();
        // Lingering particles left so far in each patch of air, keyed by PatchKey.
        public readonly Dictionary<Vector3Int, float> smokeFill = new Dictionary<Vector3Int, float>();
        // Per height band: steps from each open cell to the nearest cell whose
        // patch still needs smoke, and when that field was last worked out.
        // Keyed by FieldKey: one field per band for all air, one for the air
        // outside the burning room. Also how many cells each field starts from.
        public readonly Dictionary<int, int[]> fillField = new Dictionary<int, int[]>();
        public readonly Dictionary<int, float> fillFieldTime = new Dictionary<int, float>();
        public readonly Dictionary<int, int> fillFieldSeeds = new Dictionary<int, int>();
        // Per room, while a field is worked out: nearest air needing smoke.
        public readonly float[] fillRing = new float[SmokeRoomClipVolumes.MaxRooms + 2];
        // Lingering volumes released so far, to spread Spreading Volumes evenly.
        public int lingeringCount;
        public int[] fillQueue;
        // The burning room's windows that vent its smoke, found for ventRoom.
        public readonly List<WindowVent> vents = new List<WindowVent>();
        public RoomAir ventRoom;
    }

    /// <summary>A window of the burning room, and the smoke leaving through it.</summary>
    class WindowVent
    {
        public Transform window;
        public VisualEffect effect;
        // The open cell just inside the window, whose smoke layer it vents.
        public int cell;
        // Level and pointing out of the house, and its heading in degrees.
        public Vector3 outward;
        public float yaw;
        // Middle of the window's outer face.
        public Vector3 face;
        public float width;
        public float height;
        public float top;
        // How far the smoke layer reaches down the window, and how strongly it vents, 0 to 1.
        public float depth;
        public float strength;
    }

    class SmokeVolume
    {
        public FlammableObject smoke;
        public float headingRadians;
        // 0 keeps the volume at the top of the smoke layer, 1 at its bottom.
        public float layerDepth;
        public float distance;
        public float age;
        public float alpha;
        public float fadeStartAlpha;
        public float authoredAlpha;
        public float authoredColorIntensity;
        public float fadeDelay;
        public bool ignited;
        public int spawnFrame;
        public VolumePhase phase;
        // The plume stays over the fire and keeps churning.
        public bool plume;
        // A lingering volume leaves particles behind it, up to Particles Per Volume.
        public bool lingers;
        public float released;
        // A stuck volume wanders instead of steering until this time.
        public float wanderUntil;
        // Heads for air outside the burning room first (see Spreading Volumes).
        public bool spreads;
        // Stays down at cruise height until this time (see DuckSeconds).
        public float duckUntil;
        // The cell it heads for, and until when (see AimAhead).
        public int aimCell = -1;
        public float aimUntil;
        // The cell it is holding over while it fills, and since when.
        public int holdCell = -1;
        public float holdSince;
    }

    /// <summary>
    /// Open cells of the floor the fire is on, in meters, keyed from the floor
    /// point under the fire. They reach from the burning room through doorways
    /// into the rest of the house.
    /// </summary>
    class RoomAir
    {
        public Collider floor;
        public Collider ceiling;
        public float floorY;
        public float ceilingY;
        public Vector3 origin;
        public readonly HashSet<Vector2Int> open = new HashSet<Vector2Int>();
        // Where in each open cell a volume fits: usually its center. The grid
        // can put a doorway cell's center next to a door jamb, so its point is
        // moved into the opening.
        public readonly Dictionary<Vector2Int, Vector3> points = new Dictionary<Vector2Int, Vector3>();
        // Bit k set: a volume can pass from the cell to its neighbor k.
        public readonly Dictionary<Vector2Int, int> passable = new Dictionary<Vector2Int, int>();
        // The open cells as arrays, for the fill field: each cell's index and
        // point, its four neighbors a volume can reach (-1 where none) and the
        // fill patch it lies in.
        public Vector2Int[] cells;
        public Vector3[] cellPoints;
        public int[] links;
        public int[] patchX;
        public int[] patchZ;
        public readonly Dictionary<Vector2Int, int> cellIndex = new Dictionary<Vector2Int, int>();
        // The room (clip box group) each cell is in, and the burning room's.
        // Without contained smoke there are no rooms and everything counts as
        // the burning room.
        public int[] cellGroup;
        public int fireGroup = -1;
        // The rooms' clip boxes, to tell which room a point is in.
        public List<SmokeRoomClipVolumes.Room> boxes;
        // Cells by an opening between two rooms, on either side of it.
        public bool[] doorway;
        // How far the smoke travels from the fire to each cell along the open
        // floor, in meters, with each meter outside the burning room counted
        // as 1 / Other Room Speed meters because smoke moves slower there.
        // Divided by Front Speed it is when the edge of the smoke gets there.
        public float[] spreadMeters;
    }

    enum VolumePhase
    {
        Growing,
        Holding,
        Fading
    }

    void Awake()
    {
        if (fireSources == null || fireSources.Length == 0)
        {
            FlammableObject self = GetComponent<FlammableObject>();
            if (self != null)
                fireSources = new[] { self };
        }

        blockHolder = transform.Find("Ignis Smoke Blocks");
        if (blockHolder == null)
        {
            var holderObject = new GameObject("Ignis Smoke Blocks");
            blockHolder = holderObject.transform;
            blockHolder.SetParent(transform, false);
        }

        SmokeRoomClipVolumes.EdgeFade = wallFadeWidth;

        if (containSmoke && containFlames)
            UseContainedFlames();
    }

    /// <summary>
    /// Gives every object that burns with the plugin's Wild flames the
    /// contained copy instead. Its flames and embers are clipped to the room of
    /// the burning object, so they never show through a wall. Runs before any
    /// object catches fire on Start.
    /// </summary>
    void UseContainedFlames()
    {
        FlameEngine engine = FlameEngine.instance;
        if (engine == null)
            return;

        if (containedFlamePrefab == null)
        {
            containedFlamePrefab = Resources.Load<GameObject>(ContainedFlameResource);
            if (containedFlamePrefab == null)
            {
                Debug.LogWarning("IgnisSmokeDiffusion could not find Resources/" + ContainedFlameResource + ", so flames can show through walls. Build it from Tools > Smoke Containment > Build Contained Flames.", this);
                return;
            }
        }

        GameObject wild = engine.GetFireVFX(FlameEngine.FireVFXVariant.Wild);
        FlammableObject[] objects = Object.FindObjectsByType<FlammableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < objects.Length; i++)
        {
            FlammableObject flammable = objects[i];
            if (flammable.fireVFXPrefabOverride == null &&
                engine.GetFireVFX(flammable.overrideVFXVariant, flammable.overrideFireVFX) == wild)
                flammable.fireVFXPrefabOverride = containedFlamePrefab;
        }
    }

    void OnValidate()
    {
        SmokeRoomClipVolumes.EdgeFade = wallFadeWidth;
    }

    void OnDisable()
    {
        ClearFronts();
    }

    void Update()
    {
        if (FlameEngine.instance == null)
        {
            if (!warnedAboutMissingEngine)
            {
                Debug.LogWarning("IgnisSmokeDiffusion needs a FlameEngine in the scene before it can spawn OnlySmoke volumes.", this);
                warnedAboutMissingEngine = true;
            }
            return;
        }

        EnsureFronts();
        for (int i = fronts.Count - 1; i >= 0; i--)
        {
            if (fronts[i].fire == null)
            {
                ReleaseFront(fronts[i]);
                fronts.RemoveAt(i);
                continue;
            }

            AdvanceFront(fronts[i]);
            UpdateVents(fronts[i]);
            if (!fronts[i].burning && fronts[i].volumes.Count == 0 && VentsQuiet(fronts[i]))
            {
                RestoreEmbeddedSmoke(fronts[i]);
                ReleaseVents(fronts[i]);
                fronts.RemoveAt(i);
            }
        }
    }

    void LateUpdate()
    {
        if (FlameEngine.instance == null)
            return;

        for (int i = 0; i < fronts.Count; i++)
            DriftVolumes(fronts[i]);
    }

    void EnsureFronts()
    {
        if (fireSources != null && fireSources.Length > 0)
        {
            for (int i = 0; i < fireSources.Length; i++)
                Track(fireSources[i]);
            return;
        }

        if (Time.unscaledTime < nextDiscoveryTime)
            return;

        nextDiscoveryTime = Time.unscaledTime + 0.5f;
        FlammableObject[] fires = Object.FindObjectsByType<FlammableObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < fires.Length; i++)
        {
            FlammableObject fire = fires[i];
            if (!IsSceneFire(fire) || !BurningNow(fire))
                continue;

            Track(fire);
        }
    }

    void Track(FlammableObject fire)
    {
        if (fire == null || HasFront(fire) || !IsSceneFire(fire))
            return;

        fronts.Add(new FireFront
        {
            fire = fire,
            lifecycle = fire.GetComponent<NetworkedFireState>()
        });
    }

    bool IsSceneFire(FlammableObject fire)
    {
        if (fire == null)
            return false;

        if (blockHolder != null && fire.transform.IsChildOf(blockHolder))
            return false;

        if (!fire.enableFlameVFX)
            return false;

        if (fire.overrideFireVFX && fire.overrideVFXVariant == FlameEngine.FireVFXVariant.OnlySmoke)
            return false;

        return true;
    }

    bool HasFront(FlammableObject fire)
    {
        for (int i = 0; i < fronts.Count; i++)
        {
            if (fronts[i].fire == fire)
                return true;
        }

        return false;
    }

    void AdvanceFront(FireFront front)
    {
        bool burning = IsBurning(front);
        if (burning && !front.burning)
            BeginBurn(front);
        else if (!burning && front.burning)
            BeginFade(front);

        front.burning = burning;
        if (burning)
        {
            if (muteEmbeddedSmoke)
                MuteEmbeddedSmoke(front);

            // Only the contained Wild flames have these; other flames ignore them.
            front.fire.SetFireVFXFloat(FlameShareProperty, flameParticles);
            front.fire.SetFireVFXFloat(FlameBrightnessProperty, flameBrightness);

            if (front.room == null && Time.unscaledTime >= front.nextMapTime)
            {
                front.room = BuildRoom(front);
                front.nextMapTime = Time.unscaledTime + 0.5f;
            }

            float delta = Time.deltaTime;
            front.burnSeconds += delta;
            front.flameOutput = FlameOutput(front);
            if (front.volumes.Count >= maxVolumesPerFire || LiveVolumeCount() >= maxLiveVolumes)
                front.spawnCredit = 0f;
            else
                front.spawnCredit += delta;

            while (front.spawnCredit >= spawnInterval && front.volumes.Count < maxVolumesPerFire && LiveVolumeCount() < maxLiveVolumes)
            {
                front.spawnCredit -= spawnInterval;
                SpawnVolume(front);
            }
        }

        UpdateVolumes(front);
    }

    int LiveVolumeCount()
    {
        int count = 0;
        for (int i = 0; i < fronts.Count; i++)
            count += fronts[i].volumes.Count;
        return count;
    }

    void BeginBurn(FireFront front)
    {
        if (!front.originLocked || front.volumes.Count == 0)
        {
            Vector3 origin = front.fire.onFire ? front.fire.GetFireOrigin() : front.fire.transform.position;
            front.origin = origin;
            front.originLocked = true;
            front.burnSeconds = 0f;
            front.spawnCredit = spawnInterval;
            front.farthestDistance = 0f;
            front.room = BuildRoom(front);
            front.nextMapTime = Time.unscaledTime + 0.5f;
        }

        // Volumes that already faded out took their smoke with them, so the
        // count starts over. A patch whose smoke survived may get a little more.
        front.smokeFill.Clear();
        for (int i = 0; i < front.volumes.Count; i++)
        {
            if (front.volumes[i].phase == VolumePhase.Fading)
                front.volumes[i].phase = VolumePhase.Holding;
        }
    }

    void BeginFade(FireFront front)
    {
        float span = Mathf.Max(0.5f, front.farthestDistance);
        for (int i = 0; i < front.volumes.Count; i++)
        {
            SmokeVolume volume = front.volumes[i];
            volume.phase = VolumePhase.Fading;
            volume.age = 0f;
            volume.fadeStartAlpha = volume.alpha;
            volume.fadeDelay = span > 0.0001f
                ? (volume.distance / span) * fadeDistanceDelay
                : 0f;
        }
    }

    float FlameOutput(FireFront front)
    {
        float byTime = Mathf.Clamp01(front.burnSeconds / fullStrengthSeconds);
        float bySpread = Mathf.Clamp01(front.fire.fireSpread / fullSpreadMeters);
        return Mathf.Max(byTime, bySpread);
    }

    float CurrentDriftSpeed(FireFront front)
    {
        float crawl = Mathf.Max(0f, front.fire.fireCrawlSpeed);
        return baseSmokeSpeed + crawl * flameSpeedToSmoke;
    }

    void SpawnVolume(FireFront front)
    {
        Vector3 position = SpawnPoint(front);
        FlammableObject smoke = smokeTemplate != null
            ? Instantiate(smokeTemplate, position, Quaternion.identity, blockHolder)
            : CreateSmokeBlock(position);

        bool plume = keepPlumeAtFire && containSmoke && !HasPlume(front);
        bool lingers = !plume && lingeringSmoke && containSmoke && LingeringSmokePrefab() != null;

        smoke.name = "IgnisSmoke_" + front.fire.name + "_" + front.volumes.Count + (plume ? "_Plume" : "");
        smoke.gameObject.SetActive(true);
        PrepareSmokeObject(smoke, plume, lingers);
        // Ignis passes this to the effect when the volume ignites, after this.
        if (plume)
            smoke.smokeParticleSize *= plumeParticleSize;

        float authoredAlpha = Mathf.Max(0f, smoke.smokeAlpha);
        float authoredIntensity = Mathf.Max(0f, smoke.smokeColorIntensity);
        smoke.smokeAlpha = 0f;
        bool spreads = lingers && TakesSpreadingTurn(front);

        front.volumes.Add(new SmokeVolume
        {
            smoke = smoke,
            headingRadians = Random.Range(0f, Mathf.PI * 2f),
            // Spreading volumes ride the top of the smoke layer, the way the hot
            // layer under the ceiling runs ahead through a house. Where the edge
            // has just arrived that is all the smoke there is, so they keep up
            // with it, and the other volumes deepen the layer behind them.
            layerDepth = spreads ? Random.value * SpreadingLayerDepth : Random.value,
            authoredAlpha = authoredAlpha,
            authoredColorIntensity = authoredIntensity,
            spawnFrame = Time.frameCount,
            phase = VolumePhase.Growing,
            plume = plume,
            lingers = lingers,
            spreads = spreads
        });
    }

    /// <summary>
    /// Changes Plume Particle Size, for the plumes already burning too. Their
    /// particles take the new size as they are replaced, within seconds.
    /// </summary>
    public void SetPlumeParticleSize(float particleSize)
    {
        particleSize = Mathf.Clamp(particleSize, 0.2f, 1f);
        for (int f = 0; f < fronts.Count; f++)
        {
            List<SmokeVolume> volumes = fronts[f].volumes;
            for (int i = 0; i < volumes.Count; i++)
            {
                FlammableObject smoke = volumes[i].smoke;
                if (!volumes[i].plume || smoke == null)
                    continue;

                smoke.smokeParticleSize = smoke.smokeParticleSize / plumeParticleSize * particleSize;
                smoke.SetFireVFXFloat("SmokeParticleSize", smoke.smokeParticleSize);
            }
        }

        plumeParticleSize = particleSize;
    }

    /// <summary>Whether the next lingering volume is one of the Spreading Volumes, so they come evenly spaced.</summary>
    bool TakesSpreadingTurn(FireFront front)
    {
        int index = front.lingeringCount++;
        return Mathf.FloorToInt((index + 1) * spreadingVolumes) > Mathf.FloorToInt(index * spreadingVolumes);
    }

    static bool HasPlume(FireFront front)
    {
        for (int i = 0; i < front.volumes.Count; i++)
        {
            if (front.volumes[i].plume && front.volumes[i].smoke != null)
                return true;
        }

        return false;
    }

    Vector3 SpawnPoint(FireFront front)
    {
        RoomAir room = front.room;
        if (room == null || room.open.Count == 0)
        {
            Vector3 fallback = front.origin + Vector3.up * heightOffset;
            fallback += Random.insideUnitSphere * 0.15f;
            fallback.y = front.origin.y + heightOffset;
            return fallback;
        }

        Vector2Int cell = ToCell(room, front.origin);
        if (!room.open.Contains(cell))
            cell = NearestOpenCell(room, cell);

        Vector3 position = PointOf(room, cell);
        position.y = CruiseHeight(room);
        position.x += Random.Range(-0.08f, 0.08f);
        position.z += Random.Range(-0.08f, 0.08f);
        return position;
    }

    FlammableObject CreateSmokeBlock(Vector3 position)
    {
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.transform.SetParent(blockHolder, false);
        block.transform.position = position;
        block.transform.localScale = generatedBlockScale;

        FlammableObject smoke = block.AddComponent<FlammableObject>();
        smoke.smokeColor = smokeColor;
        smoke.smokeColorIntensity = smokeColorIntensity;
        smoke.smokeAlpha = smokeAlpha;
        smoke.smokeVFXMultiplier = smokeVFXMultiplier;
        smoke.smokeParticleSize = smokeParticleSize;
        return smoke;
    }

    /// <summary>
    /// Makes the object a smoke emitter only. It must not crawl, play fire audio,
    /// or ignite whatever it overlaps. Its collider stays a trigger so the drift
    /// cast, not a rigid body, decides when the volume hits the room.
    /// </summary>
    void PrepareSmokeObject(FlammableObject smoke, bool plume, bool lingers)
    {
        smoke.overrideFireVFX = true;
        smoke.overrideVFXVariant = FlameEngine.FireVFXVariant.OnlySmoke;
        GameObject plumePrefab = plume ? PlumeSmokePrefab() : null;
        smoke.fireVFXPrefabOverride = !containSmoke ? null
            : lingers ? LingeringSmokePrefab()
            : plumePrefab != null ? plumePrefab
            : ContainedSmokePrefab();
        smoke.enableFlameVFX = false;
        smoke.embersVFXMultiplier = 0f;
        smoke.embersBurstVFXMultiplier = 0f;
        smoke.embersParticleSize = 0f;
        smoke.useFireSFX = false;
        smoke.enableMaterialAnimation = false;
        smoke.setThisOnFireOnStart = false;
        smoke.isEffectOnForever = true;
        smoke.burnOutStart_s = float.MaxValue;
        smoke.burnOutLength_s = float.MaxValue;
        smoke.fireCrawlSpeed = 0f;
        smoke.flameEnvironmentalSpeed = 0.25f;
        smoke.affectedByWind = false;
        smoke.maxSpread = 0.1f;
        smoke.spreadLayerMask = 0;
        smoke.ignitionTime = 0f;
        smoke.onFire = false;
        smoke.ResetObj();

        Renderer[] renderers = smoke.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            renderers[i].enabled = false;

        BoxCollider[] boxes = smoke.GetComponentsInChildren<BoxCollider>(true);
        for (int i = 0; i < boxes.Length; i++)
            boxes[i].isTrigger = true;

        if (boxes.Length > 0)
        {
            smoke.flammableColliders = new List<BoxCollider> { boxes[0] };
            if (lingers)
                NarrowRelease(boxes[0]);
        }
    }

    /// <summary>
    /// Ignis releases smoke anywhere in the volume's box, which is wider than
    /// the gap the drift keeps between a volume and a wall. A lingering particle
    /// stays in the room it was released in, so one released past the wall
    /// would show on the far side. The box is narrowed to the volume's own
    /// radius across, half the gap, so every particle starts in its room.
    /// </summary>
    void NarrowRelease(BoxCollider box)
    {
        float width = bodyRadius * LingeringBodyShare;
        Vector3 scale = box.transform.lossyScale;
        Vector3 size = box.size;
        if (Mathf.Abs(scale.x) > 0.0001f)
            size.x = Mathf.Min(size.x, width / Mathf.Abs(scale.x));
        if (Mathf.Abs(scale.z) > 0.0001f)
            size.z = Mathf.Min(size.z, width / Mathf.Abs(scale.z));
        box.size = size;
    }

    void DriftVolumes(FireFront front)
    {
        float delta = Time.deltaTime;
        float driftSpeed = front.burning ? CurrentDriftSpeed(front) : CurrentDriftSpeed(front) * 0.35f;
        float turn = wanderDegreesPerSecond * Mathf.Deg2Rad * delta;

        for (int i = 0; i < front.volumes.Count; i++)
        {
            SmokeVolume volume = front.volumes[i];
            if (volume.smoke == null || volume.plume)
                continue;

            Vector3 from = volume.smoke.transform.position;
            Vector3 toward = Vector3.zero;
            bool steering = volume.lingers && TowardNeededSmoke(front, volume, from, out toward);
            if (steering)
                volume.headingRadians = Mathf.Atan2(toward.z, toward.x);
            else
                volume.headingRadians += Mathf.Lerp(-turn, turn, Random.value);

            // Smoke creeps once it has left the burning room.
            int cell = CellAt(front.room, from);
            float speed = AwayFromFire(front.room, cell) ? driftSpeed * otherRoomSpeed : driftSpeed;

            // Eases to a stop over the air it is filling.
            float reach = toward.magnitude;
            if (steering && reach < HoldEaseMeters)
                speed *= reach / HoldEaseMeters;
            Vector3 heading = new Vector3(Mathf.Cos(volume.headingRadians), 0f, Mathf.Sin(volume.headingRadians));
            float vertical = front.room != null && Time.time < volume.duckUntil && delta > 0f
                ? Mathf.Clamp((CruiseHeight(front.room) - from.y) / delta, -riseSpeed, riseSpeed)
                : VerticalSpeed(front, volume, from.y, cell, delta);
            Vector3 desired = heading * speed + Vector3.up * vertical;
            float radius = volume.lingers ? bodyRadius * LingeringBodyShare : bodyRadius;
            Vector3 position = MoveUntilBlocked(from, desired * delta, front.fire.transform, radius, out bool hitWall);

            // The open air was joined at cruise height, under the door headers.
            // A volume high in the smoke layer runs into the wall over a
            // doorway, so it dips to that height to pass and rises again after.
            if (steering && hitWall && front.room != null && from.y > CruiseHeight(front.room) + 0.1f)
                volume.duckUntil = Time.time + DuckSeconds;

            position = StayInOpenAir(front.room, from, position);
            position = KeepUnderCeiling(position);
            if (speed > 0.05f && (new Vector3(position.x - from.x, 0f, position.z - from.z)).sqrMagnitude < 0.0000004f)
            {
                volume.headingRadians += Mathf.PI * 0.5f;
                volume.wanderUntil = Time.time + 1f;
            }

            volume.smoke.transform.position = position;
            volume.distance = Vector3.Distance(position, front.origin);
            if (volume.distance > front.farthestDistance)
                front.farthestDistance = volume.distance;
        }
    }

    /// <summary>
    /// Upward speed of a volume. Early in the fire every volume rises to just
    /// under the ceiling. As the fire burns, the bottom of the smoke layer comes
    /// down to Lowest Smoke Height over Bank Down Seconds, counted over each
    /// cell from when the edge of the smoke reached it. Each volume keeps its
    /// own depth inside the layer over its cell, so the layer stays filled from
    /// ceiling to bottom. A volume sinks slowly when its place moves below it,
    /// and rises when it drifts on to where the layer is still thin.
    /// </summary>
    float VerticalSpeed(FireFront front, SmokeVolume volume, float y, int cell, float delta)
    {
        RoomAir room = front.room;
        if (room == null || delta <= 0f)
            return riseSpeed;

        return Mathf.Clamp((LayerHeight(front, volume, cell) - y) / delta, -sinkSpeed, riseSpeed);
    }

    /// <summary>Height a volume keeps over a cell: its own depth inside the smoke layer there.</summary>
    float LayerHeight(FireFront front, SmokeVolume volume, int cell)
    {
        float top = front.room.ceilingY - VolumeTopClearance();
        return Mathf.Lerp(top, LayerBottom(front, cell), volume.layerDepth);
    }

    /// <summary>
    /// Moves freely through open space. A collider removes only the speed aimed
    /// into it, so the volume slides along a wall or ceiling instead of passing through.
    /// The burning object's own colliders never block its smoke: a stove's spray
    /// Hitbox can reach the release height, and a volume that starts inside it
    /// would be blocked in every direction.
    /// </summary>
    Vector3 MoveUntilBlocked(Vector3 position, Vector3 displacement, Transform source, float radius, out bool hitWall)
    {
        position = Step(position, displacement, source, radius, out Vector3 hitNormal, out bool hit);
        hitWall = hit && Mathf.Abs(hitNormal.y) < 0.5f;
        if (!hit)
            return position;

        Vector3 slide = Vector3.ProjectOnPlane(displacement, hitNormal);
        if (slide.sqrMagnitude <= 0.0000001f)
            return position;

        return Step(position, slide, source, radius, out _, out _);
    }

    Vector3 Step(Vector3 position, Vector3 displacement, Transform source, float radius, out Vector3 hitNormal, out bool hit)
    {
        hitNormal = Vector3.up;
        hit = false;
        float distance = displacement.magnitude;
        if (distance <= 0.0001f)
            return position;

        Vector3 direction = displacement / distance;
        if (Cast(position, direction, distance + CastSkin, source, radius, out RaycastHit castHit))
        {
            hit = true;
            hitNormal = castHit.normal;
            float travel = Mathf.Max(0f, castHit.distance - CastSkin);
            return position + direction * Mathf.Min(travel, distance);
        }

        return position + displacement;
    }

    bool Cast(Vector3 position, Vector3 direction, float distance, Transform source, float radius, out RaycastHit closest)
    {
        closest = default;
        RaycastHit[] hits = castHits;
        int count = Physics.SphereCastNonAlloc(position, radius, direction, hits, distance, obstacleMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            if (IgnoreSolid(hits[i].collider) || hits[i].distance >= best)
                continue;

            if (source != null && hits[i].collider.transform.IsChildOf(source))
                continue;

            best = hits[i].distance;
            closest = hits[i];
            found = true;
        }

        return found;
    }

    void UpdateVolumes(FireFront front)
    {
        for (int i = front.volumes.Count - 1; i >= 0; i--)
        {
            SmokeVolume volume = front.volumes[i];
            if (volume.smoke == null)
            {
                front.volumes.RemoveAt(i);
                continue;
            }

            if (!volume.ignited)
            {
                if (volume.phase == VolumePhase.Fading)
                {
                    ReleaseVolume(volume);
                    front.volumes.RemoveAt(i);
                    continue;
                }

                if (volume.spawnFrame != Time.frameCount)
                    volume.ignited = TryIgnite(volume);
                continue;
            }

            volume.age += Time.deltaTime;
            if (volume.lingers)
                UpdateRelease(front, volume);

            float target = TargetAlpha(front, volume);
            if (volume.phase == VolumePhase.Growing)
            {
                float progress = growthSeconds <= 0f ? 1f : Mathf.Clamp01(volume.age / growthSeconds);
                volume.alpha = Mathf.Lerp(volume.authoredAlpha * minimumSmokeStrength, target, Mathf.SmoothStep(0f, 1f, progress));
                if (progress >= 1f)
                {
                    volume.phase = VolumePhase.Holding;
                    volume.age = 0f;
                }
            }
            else if (volume.phase == VolumePhase.Holding)
            {
                float rise = volume.authoredAlpha / Mathf.Max(0.05f, growthSeconds);
                volume.alpha = Mathf.MoveTowards(volume.alpha, target, rise * Time.deltaTime);
            }
            else
            {
                float elapsed = volume.age - volume.fadeDelay;
                if (elapsed <= 0f)
                {
                    volume.alpha = volume.fadeStartAlpha;
                }
                else
                {
                    float progress = fadeSeconds <= 0f ? 1f : Mathf.Clamp01(elapsed / fadeSeconds);
                    volume.alpha = DispersingAlpha(volume.fadeStartAlpha, progress);
                    if (progress >= 1f)
                    {
                        ReleaseVolume(volume);
                        front.volumes.RemoveAt(i);
                        continue;
                    }
                }
            }

            ApplyAlpha(volume);
        }

        if (front.volumes.Count == 0 && !front.burning)
            ResetFront(front);
    }

    /// <summary>
    /// A lingering volume releases particles only while it is in a patch of air
    /// that needs smoke: behind the edge of the smoke, inside the smoke layer,
    /// and holding fewer than its share of Particles Per Cell. They stay where
    /// they were put for Linger Seconds, so as the volumes wander and bank down
    /// they fill every patch they reach once, the middle of a room as much as
    /// its walls.
    /// </summary>
    void UpdateRelease(FireFront front, SmokeVolume volume)
    {
        float rate = 0f;
        if (volume.phase != VolumePhase.Fading && volume.released < particlesPerVolume)
        {
            Vector3 position = volume.smoke.transform.position;
            int cell = CellAt(front.room, position);
            int band = FillBand(position.y);
            Vector3Int key = cell >= 0 ? PatchKey(front.room, cell, band) : FillKey(position);
            front.smokeFill.TryGetValue(key, out float filled);
            bool needed = cell >= 0 ? NeedsSmoke(front, cell, band, filled, 0f) : filled < particlesPerCell;
            if (needed)
            {
                rate = releaseRate;
                float step = rate * Time.deltaTime;
                front.smokeFill[key] = filled + step;
                volume.released += step;

                // The measured room height stands in where no ceiling is
                // overhead, such as when the ceilings are hidden.
                float ceiling = Mathf.Min(CeilingAbove(position), front.room != null ? front.room.ceilingY : float.PositiveInfinity);
                if (!float.IsPositiveInfinity(ceiling))
                    volume.smoke.SetFireVFXFloat(CeilingProperty, ceiling - LingerCeilingGap);
            }
        }

        volume.smoke.SetFireVFXFloat(SpawnRateProperty, rate);
    }

    static Vector3Int FillKey(Vector3 position)
    {
        return new Vector3Int(
            Mathf.FloorToInt(position.x / FillCellSize),
            FillBand(position.y),
            Mathf.FloorToInt(position.z / FillCellSize));
    }

    /// <summary>
    /// The fill key of a cell's patch at a height band, kept apart per room:
    /// a patch that straddles a wall is counted once for each room, so smoke
    /// on one side of the wall does not count as smoke on the other.
    /// </summary>
    static Vector3Int PatchKey(RoomAir room, int cell, int band)
    {
        return new Vector3Int(room.patchX[cell], band * RoomKeyStride + RingIndex(room, cell), room.patchZ[cell]);
    }

    static int FillBand(float height)
    {
        return Mathf.FloorToInt(height / FillBandHeight);
    }

    /// <summary>
    /// Direction from a lingering volume toward the nearest air at its height
    /// that still needs smoke, following open cells around furniture and
    /// through doorways. One of the Spreading Volumes heads for such air
    /// outside the burning room while there is any. False inside such air,
    /// when none is left within reach, or when the volume has no particles
    /// left to give, so it wanders.
    /// </summary>
    bool TowardNeededSmoke(FireFront front, SmokeVolume volume, Vector3 position, out Vector3 direction)
    {
        direction = Vector3.zero;
        RoomAir room = front.room;
        if (room == null || room.cells == null || room.cells.Length == 0)
            return false;
        if (volume.phase == VolumePhase.Fading || volume.released >= particlesPerVolume || Time.time < volume.wanderUntil)
            return false;
        if (!room.cellIndex.TryGetValue(ToCell(room, position), out int at))
            return false;

        // Steer by the band the volume keeps to in the layer, not the one it
        // passes through while it rises, sinks or dips under a door header,
        // or it would turn back for air at that height mid-way.
        float top = room.ceilingY - VolumeTopClearance();
        int band = FillBand(Mathf.Min(LayerHeight(front, volume, at), top - 0.05f));
        int[] field = volume.spreads ? FillField(front, band, true) : null;
        if (field == null)
            field = FillField(front, band, false);
        int steps = field[at];
        if (steps == int.MaxValue)
            return false;

        // Over air that needs smoke: hold there until it is full, instead of
        // wandering off and laying smoke along the first wall it reaches.
        if (steps == 0)
        {
            if (volume.holdCell != at)
            {
                volume.holdCell = at;
                volume.holdSince = Time.time;
            }
            else if (Time.time - volume.holdSince > HoldSeconds)
            {
                volume.holdCell = -1;
                volume.wanderUntil = Time.time + 1f;
                return false;
            }

            direction = room.cellPoints[at] - position;
            direction.y = 0f;
            return true;
        }

        volume.holdCell = -1;
        int next = -1;
        for (int k = 0; k < Neighbors.Length; k++)
        {
            int neighbor = room.links[at * Neighbors.Length + k];
            if (neighbor >= 0 && field[neighbor] < steps)
            {
                steps = field[neighbor];
                next = neighbor;
            }
        }

        if (next < 0)
            return false;

        direction = room.cellPoints[AimAhead(front, volume, position, at, next, field)] - position;
        direction.y = 0f;
        return direction.sqrMagnitude > 0.000001f;
    }

    /// <summary>
    /// The farthest cell down the field, up to AimCells on, that a volume can
    /// reach in a straight clear line. Heading for it lines the volume up with
    /// a doorway from across the room, so it passes through the middle of the
    /// opening instead of cutting the corner, catching the frame and sliding
    /// along the wall beside it. Worked out again every AimSeconds.
    /// </summary>
    int AimAhead(FireFront front, SmokeVolume volume, Vector3 position, int at, int next, int[] field)
    {
        RoomAir room = front.room;
        int count = room.cells.Length;
        if (volume.aimCell >= 0 && volume.aimCell < count && Time.time < volume.aimUntil && field[volume.aimCell] < field[at])
            return volume.aimCell;

        // The cells were joined at cruise height, under the door headers.
        float cruise = CruiseHeight(room);
        float radius = bodyRadius * LingeringBodyShare;
        Vector3 from = new Vector3(position.x, cruise, position.z);
        int aim = next;
        int cell = next;
        for (int look = 0; look < AimCells; look++)
        {
            int ahead = -1;
            int steps = field[cell];
            for (int k = 0; k < Neighbors.Length; k++)
            {
                int neighbor = room.links[cell * Neighbors.Length + k];
                if (neighbor >= 0 && field[neighbor] < steps)
                {
                    steps = field[neighbor];
                    ahead = neighbor;
                }
            }

            if (ahead < 0)
                break;

            Vector3 to = room.cellPoints[ahead];
            to.y = cruise;
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance > 0.001f && Cast(from, delta / distance, distance, front.fire.transform, radius, out _))
                break;

            aim = ahead;
            cell = ahead;
        }

        volume.aimCell = aim;
        volume.aimUntil = Time.time + AimSeconds;
        return aim;
    }

    /// <summary>
    /// Steps from every open cell to the nearest one whose patch at this height
    /// band needs smoke, refreshed every few frames. Air just past the edge of
    /// the smoke counts too, so idle volumes gather at the edge and fill it as
    /// it moves on. In each room only the air nearest where the smoke came in,
    /// within RingMeters of it along the way the smoke travels, counts, so a
    /// room fills in rings outward from the fire or doorway, the same in every
    /// open direction, instead of wherever the volumes happen to wander. With
    /// awayOnly, only air outside the burning room counts, and the result is
    /// null while none of it needs smoke.
    /// </summary>
    int[] FillField(FireFront front, int band, bool awayOnly)
    {
        RoomAir room = front.room;
        int count = room.cells.Length;
        int key = band * 2 + (awayOnly ? 1 : 0);
        if (front.fillField.TryGetValue(key, out int[] field) && field.Length == count
            && front.fillFieldTime.TryGetValue(key, out float built) && Time.time - built < FillFieldSeconds)
            return awayOnly && front.fillFieldSeeds.TryGetValue(key, out int known) && known == 0 ? null : field;

        if (field == null || field.Length != count)
        {
            field = new int[count];
            front.fillField[key] = field;
        }
        if (front.fillQueue == null || front.fillQueue.Length != count)
            front.fillQueue = new int[count];

        // Air that needs smoke, and in each room how far the nearest of it is
        // from the fire along the way the smoke travels.
        float[] ring = front.fillRing;
        for (int g = 0; g < ring.Length; g++)
            ring[g] = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            front.smokeFill.TryGetValue(PatchKey(room, i, band), out float filled);
            if ((!awayOnly || AwayFromFire(room, i)) && NeedsSmoke(front, i, band, filled, FrontLeadSeconds))
            {
                field[i] = 0;
                int g = RingIndex(room, i);
                ring[g] = Mathf.Min(ring[g], SpreadMetersAt(room, i));
            }
            else
            {
                field[i] = int.MaxValue;
            }
        }

        // Of that, only the ring nearest where the smoke came into each room.
        int[] queue = front.fillQueue;
        int head = 0;
        int tail = 0;
        for (int i = 0; i < count; i++)
        {
            if (field[i] != 0)
                continue;

            if (SpreadMetersAt(room, i) > ring[RingIndex(room, i)] + RingMeters)
                field[i] = int.MaxValue;
            else
                queue[tail++] = i;
        }

        int seeds = tail;
        while (head < tail)
        {
            int cell = queue[head++];
            int steps = field[cell] + 1;
            for (int k = 0; k < Neighbors.Length; k++)
            {
                int neighbor = room.links[cell * Neighbors.Length + k];
                if (neighbor < 0 || field[neighbor] <= steps)
                    continue;

                field[neighbor] = steps;
                queue[tail++] = neighbor;
            }
        }

        front.fillFieldTime[key] = Time.time;
        front.fillFieldSeeds[key] = seeds;
        return awayOnly && seeds == 0 ? null : field;
    }

    /// <summary>
    /// Lays out the open cells as arrays for the fill field. Neighbors are
    /// linked only where a volume can pass between them, so the field leads
    /// through doorways and never through a wall.
    /// </summary>
    static void IndexCells(RoomAir room)
    {
        int count = room.open.Count;
        room.cells = new Vector2Int[count];
        room.cellPoints = new Vector3[count];
        room.links = new int[count * Neighbors.Length];
        room.patchX = new int[count];
        room.patchZ = new int[count];
        room.cellIndex.Clear();

        int n = 0;
        foreach (Vector2Int cell in room.open)
        {
            room.cells[n] = cell;
            room.cellIndex[cell] = n;
            n++;
        }

        for (int i = 0; i < count; i++)
        {
            Vector3 world = PointOf(room, room.cells[i]);
            room.cellPoints[i] = world;
            room.patchX[i] = Mathf.FloorToInt(world.x / FillCellSize);
            room.patchZ[i] = Mathf.FloorToInt(world.z / FillCellSize);
            room.passable.TryGetValue(room.cells[i], out int mask);
            for (int k = 0; k < Neighbors.Length; k++)
            {
                bool linked = (mask & (1 << k)) != 0 && room.cellIndex.TryGetValue(room.cells[i] + Neighbors[k], out int j);
                room.links[i * Neighbors.Length + k] = linked ? room.cellIndex[room.cells[i] + Neighbors[k]] : -1;
            }
        }
    }

    /// <summary>The point in an open cell where a volume fits.</summary>
    static Vector3 PointOf(RoomAir room, Vector2Int cell)
    {
        return room.points.TryGetValue(cell, out Vector3 point) ? point : CellCenter(room, cell);
    }

    float TargetAlpha(FireFront front, SmokeVolume volume)
    {
        float strength = Mathf.Lerp(minimumSmokeStrength, 1f, front.flameOutput);
        return volume.authoredAlpha * strength;
    }

    bool TryIgnite(SmokeVolume volume)
    {
        FlameEngine engine = FlameEngine.instance;
        if (engine != null && engine.FlameCount() >= engine.maxFlamesInWorld)
            return false;

        volume.smoke.smokeAlpha = volume.alpha;
        volume.smoke.SetOnFireFromCenter();
        if (!volume.smoke.onFire)
            return false;

        if (volume.lingers)
        {
            volume.smoke.SetFireVFXFloat(LifetimeProperty, lingerSeconds);
            volume.smoke.SetFireVFXFloat(SpawnRateProperty, 0f);
        }

        ApplyAlpha(volume);
        return true;
    }

    void ApplyAlpha(SmokeVolume volume)
    {
        float opacity = volume.plume ? (plumeSmokePrefab != null ? plumeOpacity : 1f)
            : volume.lingers ? lingeringOpacity : 1f;
        volume.smoke.smokeAlpha = Mathf.Max(0f, volume.alpha) * opacity;
        volume.smoke.smokeColorIntensity = volume.authoredColorIntensity;
        volume.smoke.RefreshSmokeAppearance();
    }

    static float DispersingAlpha(float startingAlpha, float progress)
    {
        progress = Mathf.Clamp01(progress);
        if (progress >= 1f || startingAlpha <= 0f)
            return 0f;

        if (startingAlpha <= FinalVisibleAlpha)
            return Mathf.Lerp(startingAlpha, 0f, progress);

        return startingAlpha * Mathf.Pow(FinalVisibleAlpha / startingAlpha, progress);
    }

    bool IsBurning(FireFront front)
    {
        return BurningNow(front.fire, front.lifecycle);
    }

    static bool BurningNow(FlammableObject fire)
    {
        if (fire == null)
            return false;

        return BurningNow(fire, fire.GetComponent<NetworkedFireState>());
    }

    static bool BurningNow(FlammableObject fire, NetworkedFireState lifecycle)
    {
        if (fire == null)
            return false;

        if (lifecycle != null && lifecycle.isActiveAndEnabled)
        {
            if (!lifecycle.TryGetLifecycleSnapshot(out NetworkedFireState.FireLifecycleSnapshot state))
                return false;

            return state.IsBurning && !state.IsExtinguished && !state.IsBurnedOut;
        }

        return fire.onFire && !fire.IsExtinguished() && !fire.hasBurnedOut();
    }

    void MuteEmbeddedSmoke(FireFront front)
    {
        FlammableObject fire = front.fire;
        if (fire == null)
            return;

        if (!front.embeddedMuted)
        {
            front.savedSmokeAlpha = fire.smokeAlpha;
            front.savedSmokeMultiplier = fire.smokeVFXMultiplier;
            front.embeddedMuted = true;
        }

        fire.smokeAlpha = 0f;
        fire.smokeVFXMultiplier = 0f;
    }

    void RestoreEmbeddedSmoke(FireFront front)
    {
        if (!front.embeddedMuted || front.fire == null)
            return;

        front.fire.smokeAlpha = front.savedSmokeAlpha;
        front.fire.smokeVFXMultiplier = front.savedSmokeMultiplier;
        front.embeddedMuted = false;
    }

    RoomAir BuildRoom(FireFront front)
    {
        Vector3 rough = front.originLocked ? front.origin : front.fire.transform.position;
        if (!TryFindShell(rough, out Collider floor, out Collider ceiling, out float floorY, out float ceilingY))
            return null;

        var room = new RoomAir
        {
            floor = floor,
            ceiling = ceiling,
            floorY = floorY,
            ceilingY = ceilingY,
            origin = new Vector3(rough.x, floorY, rough.z)
        };

        var probes = new Dictionary<Vector2Int, Vector3?>();
        var edges = new Dictionary<Vector3Int, bool>();
        Vector2Int start = Vector2Int.zero;
        if (Probe(room, probes, start) == null)
        {
            bool seeded = false;
            for (int radius = 1; radius <= 4 && !seeded; radius++)
            {
                for (int z = -radius; z <= radius && !seeded; z++)
                {
                    for (int x = -radius; x <= radius; x++)
                    {
                        if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) != radius)
                            continue;

                        Vector2Int candidate = new Vector2Int(x, z);
                        if (Probe(room, probes, candidate) == null)
                            continue;

                        start = candidate;
                        seeded = true;
                    }
                }
            }

            if (!seeded)
                return null;
        }

        // Spread from cell to cell wherever a volume can pass between their
        // points, so the air reaches through doorways but not through walls.
        var queue = new Queue<Vector2Int>();
        queue.Enqueue(start);
        room.open.Add(start);
        room.points[start] = probes[start].Value;
        int examined = 0;
        while (queue.Count > 0 && room.open.Count < MaxOpenCells)
        {
            Vector2Int cell = queue.Dequeue();
            if (++examined > MaxOpenCells * 4)
                break;

            for (int i = 0; i < Neighbors.Length; i++)
            {
                Vector2Int next = cell + Neighbors[i];
                bool known = room.open.Contains(next);
                if (!known)
                {
                    Vector3 world = CellCenter(room, next);
                    float dx = world.x - room.origin.x;
                    float dz = world.z - room.origin.z;
                    if (dx * dx + dz * dz > MaxRoomRadius * MaxRoomRadius)
                        continue;

                    if (Probe(room, probes, next) == null)
                        continue;
                }

                Vector3 point = known ? room.points[next] : probes[next].Value;
                if (!EdgePassable(room, edges, cell, i, room.points[cell], point))
                    continue;

                room.passable.TryGetValue(cell, out int mask);
                room.passable[cell] = mask | (1 << i);
                room.passable.TryGetValue(next, out int nextMask);
                room.passable[next] = nextMask | (1 << (i ^ 1));
                if (known)
                    continue;

                room.open.Add(next);
                room.points[next] = point;
                queue.Enqueue(next);
            }
        }

        if (room.open.Count == 0)
            return null;

        IndexCells(room);
        if (containSmoke)
        {
            List<SmokeRoomClipVolumes.Room> boxes = MeasureRooms(room);
            SmokeRoomClipVolumes.SetRooms(front, boxes);
            AssignRoomGroups(room, boxes);
            room.boxes = boxes;
        }
        MapSpread(room);
        return room;
    }

    /// <summary>
    /// How far smoke travels from the fire to every open cell, along the cells
    /// a volume can pass between, so the path runs through doorways and down
    /// hallways. Steps outside the burning room count longer, because smoke
    /// moves slower there. Steps go diagonally too where both ways around the
    /// corner are open, so in open space the distance is close to a straight
    /// line and the edge of the smoke grows round, not as a diamond.
    /// </summary>
    void MapSpread(RoomAir room)
    {
        const float Diagonal = 1.41421356f;
        int count = room.cells.Length;
        room.spreadMeters = new float[count];
        for (int i = 0; i < count; i++)
            room.spreadMeters[i] = float.PositiveInfinity;

        Vector2Int fireCell = ToCell(room, room.origin);
        if (!room.cellIndex.TryGetValue(fireCell, out int start)
            && !room.cellIndex.TryGetValue(NearestOpenCell(room, fireCell), out start))
            return;

        // Steps cost several lengths, so a queue that revisits a cell
        // whenever a shorter way to it turns up finds every shortest path.
        float awayStep = CellSize / Mathf.Max(0.05f, otherRoomSpeed);
        var queue = new Queue<int>();
        room.spreadMeters[start] = 0f;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            for (int k = 0; k < Neighbors.Length; k++)
                Relax(room, queue, cell, room.links[cell * Neighbors.Length + k], 1f, awayStep);

            // Neighbors 0 and 1 are +x and -x, 2 and 3 are +z and -z.
            for (int x = 0; x < 2; x++)
            {
                for (int z = 2; z < 4; z++)
                {
                    int viaX = room.links[cell * Neighbors.Length + x];
                    int viaZ = room.links[cell * Neighbors.Length + z];
                    if (viaX < 0 || viaZ < 0)
                        continue;

                    int corner = room.links[viaX * Neighbors.Length + z];
                    if (corner >= 0 && corner == room.links[viaZ * Neighbors.Length + x])
                        Relax(room, queue, cell, corner, Diagonal, awayStep);
                }
            }
        }
    }

    void Relax(RoomAir room, Queue<int> queue, int cell, int neighbor, float length, float awayStep)
    {
        if (neighbor < 0)
            return;

        float meters = room.spreadMeters[cell] + length * (AwayFromFire(room, neighbor) ? awayStep : CellSize);
        if (meters >= room.spreadMeters[neighbor] - 0.001f)
            return;

        room.spreadMeters[neighbor] = meters;
        queue.Enqueue(neighbor);
    }

    /// <summary>Seconds after the fire starts that the edge of its smoke reaches a cell.</summary>
    float ArrivalSeconds(RoomAir room, int cell)
    {
        if (room == null || room.spreadMeters == null || cell < 0)
            return 0f;

        return room.spreadMeters[cell] / Mathf.Max(0.05f, frontSpeed);
    }

    /// <summary>
    /// How far the smoke layer over a cell has banked down, 0 to 1. It starts
    /// when the edge of the smoke gets there, and away from the burning room
    /// it deepens Other Room Speed times as fast.
    /// </summary>
    float Banked(FireFront front, int cell)
    {
        float since = front.burnSeconds - ArrivalSeconds(front.room, cell);
        float seconds = AwayFromFire(front.room, cell) ? bankDownSeconds / Mathf.Max(0.05f, otherRoomSpeed) : bankDownSeconds;
        return Mathf.Clamp01(since / seconds);
    }

    /// <summary>Height of the bottom of the smoke layer over a cell.</summary>
    float LayerBottom(FireFront front, int cell)
    {
        RoomAir room = front.room;
        float top = room.ceilingY - VolumeTopClearance();
        float lowest = Mathf.Min(top, room.floorY + lowestSmokeHeight);
        return Mathf.Lerp(top, lowest, Banked(front, cell));
    }

    /// <summary>
    /// Whether a patch of air at a cell and height band should get smoke now:
    /// it holds less than it should, the edge of the smoke has reached the cell
    /// (or will within Lead seconds), and the band reaches into the smoke layer.
    /// </summary>
    bool NeedsSmoke(FireFront front, int cell, int band, float filled, float lead)
    {
        RoomAir room = front.room;
        if (filled >= FillTarget(room, cell))
            return false;
        if (ArrivalSeconds(room, cell) > front.burnSeconds + lead)
            return false;

        return (band + 1) * FillBandHeight > LayerBottom(front, cell);
    }

    /// <summary>The cell's open point, worked out once per cell, or null where no volume fits.</summary>
    Vector3? Probe(RoomAir room, Dictionary<Vector2Int, Vector3?> probes, Vector2Int cell)
    {
        if (probes.TryGetValue(cell, out Vector3? known))
            return known;

        Vector3? point = TryOpenPoint(room, cell, out Vector3 found) ? found : (Vector3?)null;
        probes[cell] = point;
        return point;
    }

    /// <summary>Whether a volume can pass between two neighboring cells, tested once per pair.</summary>
    bool EdgePassable(RoomAir room, Dictionary<Vector3Int, bool> edges, Vector2Int cell, int direction, Vector3 from, Vector3 to)
    {
        // Neighbors are +x, -x, +z, -z: key each pair by its lower cell and axis.
        Vector2Int low = (direction & 1) == 0 ? cell : cell + Neighbors[direction];
        var key = new Vector3Int(low.x, low.y, direction >> 1);
        if (edges.TryGetValue(key, out bool passable))
            return passable;

        passable = Passable(room, from, to);
        edges[key] = passable;
        return passable;
    }

    /// <summary>
    /// Which room each open cell is in, from the clip boxes, and which room is
    /// burning. A cell belongs to the box that holds it in the floor plan, or
    /// the nearest one, the same rule the smoke shader uses.
    /// </summary>
    static void AssignRoomGroups(RoomAir room, List<SmokeRoomClipVolumes.Room> boxes)
    {
        int count = room.cells.Length;
        room.cellGroup = new int[count];
        for (int i = 0; i < count; i++)
            room.cellGroup[i] = GroupAt(boxes, room.cellPoints[i]);
        room.fireGroup = GroupAt(boxes, room.origin);

        // Doorways: cells joined to a cell of another room, and the cells next
        // to them, so each opening has a patch of air on either side.
        var opening = new bool[count];
        for (int i = 0; i < count; i++)
        {
            for (int k = 0; k < Neighbors.Length; k++)
            {
                int j = room.links[i * Neighbors.Length + k];
                if (j >= 0 && room.cellGroup[j] != room.cellGroup[i])
                    opening[i] = true;
            }
        }

        room.doorway = new bool[count];
        for (int i = 0; i < count; i++)
        {
            if (!opening[i])
                continue;

            room.doorway[i] = true;
            for (int k = 0; k < Neighbors.Length; k++)
            {
                int j = room.links[i * Neighbors.Length + k];
                if (j >= 0)
                    room.doorway[j] = true;
            }
        }
    }

    static int GroupAt(List<SmokeRoomClipVolumes.Room> boxes, Vector3 point)
    {
        int group = -1;
        float best = float.MinValue;
        for (int i = 0; i < boxes.Count; i++)
        {
            SmokeRoomClipVolumes.Room box = boxes[i];
            Vector3 forward = new Vector3(-box.right.z, 0f, box.right.x);
            Vector3 offset = point - box.center;
            float depth = Mathf.Min(
                box.halfSize.x - Mathf.Abs(Vector3.Dot(offset, box.right)),
                box.halfSize.z - Mathf.Abs(Vector3.Dot(offset, forward)));
            if (depth <= best)
                continue;

            best = depth;
            group = box.group;
        }

        return group;
    }

    /// <summary>Slot in FireFront.fillRing of the room a cell is in.</summary>
    static int RingIndex(RoomAir room, int cell)
    {
        int group = room.cellGroup != null ? room.cellGroup[cell] : -1;
        return Mathf.Clamp(group + 1, 0, SmokeRoomClipVolumes.MaxRooms + 1);
    }

    static float SpreadMetersAt(RoomAir room, int cell)
    {
        return room.spreadMeters != null ? room.spreadMeters[cell] : 0f;
    }

    /// <summary>Whether a cell lies in a room other than the burning one.</summary>
    static bool AwayFromFire(RoomAir room, int cell)
    {
        return room != null && room.cellGroup != null && room.fireGroup >= 0 && cell >= 0 && room.cellGroup[cell] != room.fireGroup;
    }

    /// <summary>
    /// Particles a patch of air holds when full: fewer away from the burning
    /// room, but more by a doorway, where smoke pours through the opening.
    /// </summary>
    float FillTarget(RoomAir room, int cell)
    {
        if (!AwayFromFire(room, cell))
            return particlesPerCell;

        bool doorway = room.doorway != null && room.doorway[cell];
        return particlesPerCell * (doorway ? Mathf.Max(doorwaySmoke, otherRoomSmoke) : otherRoomSmoke);
    }

    static int CellAt(RoomAir room, Vector3 position)
    {
        return room != null && room.cellIndex.TryGetValue(ToCell(room, position), out int cell) ? cell : -1;
    }

    /// <summary>
    /// Finds the burning room's windows once its air is measured: windows on
    /// the fire's floor whose inside is in the burning room and whose outside
    /// is in no room, so outside the house. Each gets a vent effect, quiet
    /// until the smoke layer reaches it. Windows of other rooms do not vent.
    /// Rooms joined by an opening with no door header share their air, so they
    /// count as one burning room; in a generated house, a window must also be
    /// in the same generated room as the burning object.
    /// </summary>
    void FindVents(FireFront front)
    {
        ReleaseVents(front);
        RoomAir room = front.room;
        front.ventRoom = room;
        if (!ventThroughWindows || room == null || room.boxes == null || room.fireGroup < 0 || string.IsNullOrEmpty(windowName))
            return;

        VisualEffectAsset effect = WindowVentEffect();
        if (effect == null)
            return;

        Transform fireRoom = GeneratedRoom(front.fire.transform);
        MeshFilter[] meshes = Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < meshes.Length; i++)
        {
            MeshFilter mesh = meshes[i];
            if (mesh.sharedMesh == null || !mesh.name.StartsWith(windowName, System.StringComparison.Ordinal))
                continue;

            Transform windowRoom = GeneratedRoom(mesh.transform);
            if (fireRoom != null && windowRoom != null && windowRoom != fireRoom)
                continue;

            if (TryMeasureWindow(room, mesh, out WindowVent vent))
            {
                vent.effect = CreateVentEffect(effect);
                front.vents.Add(vent);
            }
        }
    }

    /// <summary>
    /// A window's size, and which way is out, from its mesh. Its thinnest
    /// level side runs through the wall; out is the side in no room.
    /// </summary>
    bool TryMeasureWindow(RoomAir room, MeshFilter mesh, out WindowVent vent)
    {
        vent = null;
        Transform window = mesh.transform;
        Bounds bounds = mesh.sharedMesh.bounds;
        Vector3 center = window.TransformPoint(bounds.center);
        if (center.y <= room.floorY || center.y >= room.ceilingY)
            return false;

        Vector3 sideX = window.TransformVector(new Vector3(bounds.size.x, 0f, 0f));
        Vector3 sideZ = window.TransformVector(new Vector3(0f, 0f, bounds.size.z));
        bool wideX = sideX.sqrMagnitude >= sideZ.sqrMagnitude;
        Vector3 across = wideX ? sideX : sideZ;
        Vector3 through = wideX ? sideZ : sideX;
        float thickness = through.magnitude;
        through.y = 0f;
        if (through.sqrMagnitude < 0.000001f)
            return false;
        through.Normalize();

        float reach = thickness * 0.5f + VentSideProbe;
        int inside = RoomOf(room.boxes, center - through * reach);
        int outside = RoomOf(room.boxes, center + through * reach);
        if (outside == room.fireGroup && inside < 0)
            through = -through;
        else if (inside != room.fireGroup || outside >= 0)
            return false;

        int cell = NearestCellIn(room, center - through * reach, room.fireGroup);
        if (cell < 0)
            return false;

        float height = window.TransformVector(new Vector3(0f, bounds.size.y, 0f)).magnitude;
        vent = new WindowVent
        {
            window = window,
            cell = cell,
            outward = through,
            yaw = Quaternion.LookRotation(through).eulerAngles.y,
            face = center + through * (thickness * 0.5f),
            width = across.magnitude,
            height = height,
            top = center.y + height * 0.5f
        };
        return true;
    }

    /// <summary>The generated room an object is in, or null outside a generated house.</summary>
    static Transform GeneratedRoom(Transform transform)
    {
        for (; transform != null; transform = transform.parent)
        {
            if (transform.name.StartsWith(GeneratedRoomPrefix, System.StringComparison.Ordinal))
                return transform;
        }

        return null;
    }

    /// <summary>The room a point is in, or -1 when it is in none, such as outside the house.</summary>
    static int RoomOf(List<SmokeRoomClipVolumes.Room> boxes, Vector3 point)
    {
        for (int i = 0; i < boxes.Count; i++)
        {
            SmokeRoomClipVolumes.Room box = boxes[i];
            Vector3 forward = new Vector3(-box.right.z, 0f, box.right.x);
            Vector3 offset = point - box.center;
            if (Mathf.Abs(Vector3.Dot(offset, box.right)) < box.halfSize.x &&
                Mathf.Abs(Vector3.Dot(offset, forward)) < box.halfSize.z)
                return box.group;
        }

        return -1;
    }

    /// <summary>The open cell of a room nearest a point, across the floor, or -1 when the room has none.</summary>
    static int NearestCellIn(RoomAir room, Vector3 point, int group)
    {
        int nearest = -1;
        float nearestDistance = float.MaxValue;
        for (int i = 0; i < room.cells.Length; i++)
        {
            if (room.cellGroup[i] != group)
                continue;

            Vector3 offset = room.cellPoints[i] - point;
            offset.y = 0f;
            if (offset.sqrMagnitude >= nearestDistance)
                continue;

            nearestDistance = offset.sqrMagnitude;
            nearest = i;
        }

        return nearest;
    }

    /// <summary>
    /// A vent effect at the world origin with no rotation, the way Ignis places
    /// its own effects, so the graph's local space is world space.
    /// </summary>
    VisualEffect CreateVentEffect(VisualEffectAsset asset)
    {
        var holder = new GameObject("Window Smoke Vent");
        holder.transform.SetParent(blockHolder, false);
        holder.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        VisualEffect effect = holder.AddComponent<VisualEffect>();
        effect.visualEffectAsset = asset;
        return effect;
    }

    /// <summary>
    /// Vents the burning room's smoke out of its windows. A window starts once
    /// the edge of the smoke has reached it and the layer has banked down past
    /// its top. Smoke leaves through the part of the window the layer covers,
    /// at most its top half, and more strongly as the layer deepens and the
    /// fire grows. When the fire goes out, the windows stop over Fade Seconds.
    /// </summary>
    void UpdateVents(FireFront front)
    {
        if (front.room != null && front.room != front.ventRoom)
            FindVents(front);

        for (int i = front.vents.Count - 1; i >= 0; i--)
        {
            WindowVent vent = front.vents[i];
            if (vent.window == null || vent.effect == null)
            {
                ReleaseVent(vent);
                front.vents.RemoveAt(i);
                continue;
            }

            float target = front.burning && ventThroughWindows ? VentStrength(front, vent) : 0f;
            float seconds = target > vent.strength ? VentRampSeconds : fadeSeconds;
            vent.strength = Mathf.MoveTowards(vent.strength, target, Time.deltaTime / Mathf.Max(0.01f, seconds));
            ApplyVent(vent);
        }
    }

    float VentStrength(FireFront front, WindowVent vent)
    {
        RoomAir room = front.room;
        if (room == null || room != front.ventRoom || ArrivalSeconds(room, vent.cell) > front.burnSeconds)
            return 0f;

        float depth = Mathf.Min(vent.top - LayerBottom(front, vent.cell), vent.height * VentHeightShare);
        if (depth <= 0f)
            return 0f;

        vent.depth = depth;
        return Mathf.Clamp01(depth / ventFullDepth) * Mathf.Lerp(minimumSmokeStrength, 1f, front.flameOutput);
    }

    /// <summary>
    /// Puts the vent's thin strip just outside the window, across its top and
    /// as deep as the smoke layer reaches down it, and launches the smoke
    /// straight out of it. The effect simulates in world space, so moving the
    /// strip leaves the smoke already released where it is.
    /// </summary>
    void ApplyVent(WindowVent vent)
    {
        float strip = Mathf.Max(VentMinStrip, vent.depth);
        Vector3 center = vent.face + vent.outward * (VentStripDepth * 0.5f);
        center.y = vent.top - strip * 0.5f;
        var size = new Vector3(vent.width * VentWidthShare, strip, VentStripDepth);

        VisualEffect effect = vent.effect;
        Color color = smokeColor * (smokeColorIntensity * ventBrightness);
        SetVent(effect, VentBoxCenterId, center);
        SetVent(effect, VentBoxSizeId, size);
        SetVent(effect, VentRotationId, new Vector3(0f, vent.yaw, 0f));
        SetVent(effect, VentSpreadCenterId, center);
        SetVent(effect, VentSpreadRadiusId, size.magnitude * 0.5f + VentSpreadMargin);
        SetVent(effect, VentLaunchId, vent.outward * ventOutwardSpeed);
        SetVent(effect, VentLaunchScaleId, 1f);
        SetVent(effect, VentDragId, ventRise);
        SetVent(effect, VentTurbulenceId, VentTurbulence);
        SetVent(effect, VentTurbulenceSpeedId, VentTurbulence);
        // The graph's smoke lives 2 to 6 seconds times this.
        SetVent(effect, VentLifetimeId, ventLifetime / 6f);
        SetVent(effect, VentParticleSizeId, ventParticleSize);
        SetVent(effect, VentThinDistanceId, VentThinDistance);
        SetVent(effect, VentCullDistanceId, VentCullDistance);
        // The plugin's particle multiplier also gates its smoke, so it stays at
        // one, and the flames and embers the graph also holds stay off.
        SetVent(effect, VentParticleShareId, 1f);
        SetVent(effect, VentFlamesId, 0f);
        SetVent(effect, VentEmbersId, 0f);
        SetVent(effect, VentEmberBurstsId, 0f);
        if (effect.HasVector4(VentColorId))
            effect.SetVector4(VentColorId, new Vector4(color.r, color.g, color.b, 1f));
        SetVent(effect, VentSmokeRateId, ventSmokeRate * vent.strength);
        SetVent(effect, VentAlphaId, ventOpacity * vent.strength);
    }

    static void SetVent(VisualEffect effect, int property, float value)
    {
        if (effect.HasFloat(property))
            effect.SetFloat(property, value);
    }

    static void SetVent(VisualEffect effect, int property, Vector3 value)
    {
        if (effect.HasVector3(property))
            effect.SetVector3(property, value);
    }

    static bool VentsQuiet(FireFront front)
    {
        for (int i = 0; i < front.vents.Count; i++)
        {
            if (front.vents[i].strength > 0f)
                return false;
        }

        return true;
    }

    static void ReleaseVent(WindowVent vent)
    {
        if (vent.effect != null)
            Destroy(vent.effect.gameObject);
        vent.effect = null;
    }

    void ReleaseVents(FireFront front)
    {
        for (int i = 0; i < front.vents.Count; i++)
            ReleaseVent(front.vents[i]);

        front.vents.Clear();
        front.ventRoom = null;
    }

    VisualEffectAsset WindowVentEffect()
    {
        if (windowVentEffect == null && !loadedWindowVentEffect)
        {
            loadedWindowVentEffect = true;
            windowVentEffect = Resources.Load<VisualEffectAsset>(WindowVentResource);
            if (windowVentEffect == null)
                Debug.LogWarning("IgnisSmokeDiffusion could not find Resources/" + WindowVentResource + ", so no smoke vents out of the windows. Build it from Tools > Smoke Containment.", this);
        }

        return windowVentEffect;
    }

    GameObject ContainedSmokePrefab()
    {
        if (containedSmokePrefab == null && !loadedContainedSmokePrefab)
        {
            loadedContainedSmokePrefab = true;
            containedSmokePrefab = Resources.Load<GameObject>(ContainedSmokeResource);
            if (containedSmokePrefab == null)
                Debug.LogWarning("IgnisSmokeDiffusion could not find Resources/" + ContainedSmokeResource + ", so its smoke can show through walls. Build it from Tools > Smoke Containment.", this);
        }

        return containedSmokePrefab;
    }

    GameObject LingeringSmokePrefab()
    {
        if (lingeringSmokePrefab == null && !loadedLingeringSmokePrefab)
        {
            loadedLingeringSmokePrefab = true;
            lingeringSmokePrefab = Resources.Load<GameObject>(LingeringSmokeResource);
            if (lingeringSmokePrefab == null)
                Debug.LogWarning("IgnisSmokeDiffusion could not find Resources/" + LingeringSmokeResource + ", so every volume keeps churning. Build it from Tools > Smoke Containment.", this);
        }

        return lingeringSmokePrefab;
    }

    GameObject PlumeSmokePrefab()
    {
        if (plumeSmokePrefab == null && !loadedPlumeSmokePrefab)
        {
            loadedPlumeSmokePrefab = true;
            plumeSmokePrefab = Resources.Load<GameObject>(PlumeSmokeResource);
            if (plumeSmokePrefab == null)
                Debug.LogWarning("IgnisSmokeDiffusion could not find Resources/" + PlumeSmokeResource + ", so the plume plays the full 1000-particle smoke. Build it from Tools > Smoke Containment.", this);
        }

        return plumeSmokePrefab;
    }

    /// <summary>
    /// Boxes of the rooms the smoke can reach, for clipping. The air the smoke
    /// spreads through is sampled on the building's own grid just under the
    /// ceiling, where door headers close off doorways, so the cells split into
    /// rooms. Each room is covered with rectangles whose sides reach out to the
    /// nearest wall, and all rectangles of a room share its group, so the shader
    /// treats an L-shaped room as one space.
    /// </summary>
    List<SmokeRoomClipVolumes.Room> MeasureRooms(RoomAir room)
    {
        float probeY = Mathf.Max(room.floorY + 0.5f, room.ceilingY - RoomProbeBelowCeiling);
        Vector3 origin = new Vector3(room.origin.x, probeY, room.origin.z);
        Vector3 right = BuildingRight(origin);
        Vector3 forward = new Vector3(-right.z, 0f, right.x);
        var grid = new RoomGrid { origin = origin, right = right, forward = forward };

        // Cells under the ceiling, inside the air the smoke can reach.
        var visited = new HashSet<Vector2Int>();
        foreach (Vector2Int open in room.open)
        {
            Vector3 offset = CellCenter(room, open) - room.origin;
            var cell = new Vector2Int(
                Mathf.RoundToInt(Vector3.Dot(offset, right) / CellSize),
                Mathf.RoundToInt(Vector3.Dot(offset, forward) / CellSize));
            if (!visited.Add(cell))
                continue;

            // Above a doorway the cell sits inside the wall over it.
            if (!Physics.CheckSphere(grid.Point(cell), 0.1f, roomBoundaryMask, QueryTriggerInteraction.Ignore))
                grid.air.Add(cell);
        }

        // Rooms: cells joined without a wall between them.
        int roomCount = 0;
        var queue = new Queue<Vector2Int>();
        foreach (Vector2Int start in grid.air)
        {
            if (grid.roomOf.ContainsKey(start))
                continue;

            grid.roomOf[start] = roomCount;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                for (int i = 0; i < Neighbors.Length; i++)
                {
                    Vector2Int next = cell + Neighbors[i];
                    if (!grid.air.Contains(next) || grid.roomOf.ContainsKey(next) || !Joined(grid, cell, next))
                        continue;

                    grid.roomOf[next] = roomCount;
                    queue.Enqueue(next);
                }
            }

            roomCount++;
        }

        // Rectangles of each room, grown row by row.
        var cells = new List<Vector2Int>(grid.air);
        cells.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
        var used = new HashSet<Vector2Int>();
        var boxes = new List<SmokeRoomClipVolumes.Room>();
        foreach (Vector2Int start in cells)
        {
            if (used.Contains(start))
                continue;

            int group = grid.roomOf[start];
            int maxX = start.x;
            while (Free(grid, used, group, new Vector2Int(maxX + 1, start.y), new Vector2Int(maxX, start.y)))
                maxX++;

            int maxY = start.y;
            while (RowFree(grid, used, group, start.x, maxX, maxY + 1))
                maxY++;

            for (int y = start.y; y <= maxY; y++)
            {
                for (int x = start.x; x <= maxX; x++)
                    used.Add(new Vector2Int(x, y));
            }

            boxes.Add(RectangleBox(grid, room, start.x, maxX, start.y, maxY, group));
            if (boxes.Count >= SmokeRoomClipVolumes.MaxRooms)
                break;
        }

        return boxes;
    }

    class RoomGrid
    {
        public Vector3 origin;
        public Vector3 right;
        public Vector3 forward;
        public readonly HashSet<Vector2Int> air = new HashSet<Vector2Int>();
        public readonly Dictionary<Vector2Int, int> roomOf = new Dictionary<Vector2Int, int>();

        public Vector3 Point(Vector2Int cell)
        {
            return origin + right * (cell.x * CellSize) + forward * (cell.y * CellSize);
        }
    }

    bool Joined(RoomGrid grid, Vector2Int a, Vector2Int b)
    {
        return !Physics.Linecast(grid.Point(a), grid.Point(b), roomBoundaryMask, QueryTriggerInteraction.Ignore);
    }

    bool Free(RoomGrid grid, HashSet<Vector2Int> used, int group, Vector2Int cell, Vector2Int from)
    {
        return grid.roomOf.TryGetValue(cell, out int cellGroup) && cellGroup == group &&
               !used.Contains(cell) && Joined(grid, from, cell);
    }

    bool RowFree(RoomGrid grid, HashSet<Vector2Int> used, int group, int minX, int maxX, int y)
    {
        for (int x = minX; x <= maxX; x++)
        {
            if (!Free(grid, used, group, new Vector2Int(x, y), new Vector2Int(x, y - 1)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Box over a rectangle of cells. Each side reaches out to the nearest wall
    /// found from any cell along it, but no further than one and a half cells,
    /// so the box stays inside the room at an inside corner. A side facing more
    /// of the same room overlaps the next rectangle, which leaves no seam.
    /// </summary>
    SmokeRoomClipVolumes.Room RectangleBox(RoomGrid grid, RoomAir room, int minX, int maxX, int minY, int maxY, int group)
    {
        float reach = CellSize * 1.5f;
        float east = reach, west = reach, north = reach, south = reach;
        float top = float.MaxValue;
        for (int y = minY; y <= maxY; y++)
        {
            east = Mathf.Min(east, SideReach(grid.Point(new Vector2Int(maxX, y)), grid.right, reach));
            west = Mathf.Min(west, SideReach(grid.Point(new Vector2Int(minX, y)), -grid.right, reach));
        }

        for (int x = minX; x <= maxX; x++)
        {
            north = Mathf.Min(north, SideReach(grid.Point(new Vector2Int(x, maxY)), grid.forward, reach));
            south = Mathf.Min(south, SideReach(grid.Point(new Vector2Int(x, minY)), -grid.forward, reach));
        }

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector3 point = grid.Point(new Vector2Int(x, y));
                if (BoundaryDistance(point, Vector3.up, out float up))
                    top = Mathf.Min(top, point.y + up);
            }
        }

        if (top == float.MaxValue)
            top = room.ceilingY;
        float bottom = room.floorY;

        float lowX = minX * CellSize - west;
        float highX = maxX * CellSize + east;
        float lowZ = minY * CellSize - south;
        float highZ = maxY * CellSize + north;
        Vector3 center = grid.origin + grid.right * ((lowX + highX) * 0.5f) + grid.forward * ((lowZ + highZ) * 0.5f);
        center.y = (top + bottom) * 0.5f;

        // Grown by the fade width, so smoke reaches the wall at full strength
        // and fades out inside it.
        var halfSize = new Vector3((highX - lowX) * 0.5f, (top - bottom) * 0.5f, (highZ - lowZ) * 0.5f);
        return new SmokeRoomClipVolumes.Room
        {
            center = center,
            halfSize = halfSize + Vector3.one * wallFadeWidth,
            right = grid.right,
            group = group
        };
    }

    float SideReach(Vector3 point, Vector3 direction, float reach)
    {
        return BoundaryDistance(point, direction, out float distance) ? Mathf.Min(distance, reach) : reach;
    }

    /// <summary>
    /// The building's x axis in the floor plan, from the first wall found around
    /// a point. Generated houses are usually aligned with the world, but a turned
    /// one is measured along its own walls.
    /// </summary>
    Vector3 BuildingRight(Vector3 point)
    {
        Vector3[] directions = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
        for (int i = 0; i < directions.Length; i++)
        {
            if (!BoundaryHit(point, directions[i], out RaycastHit hit) || Mathf.Abs(hit.normal.y) > 0.3f)
                continue;

            float angle = Mathf.Repeat(Mathf.Atan2(hit.normal.z, hit.normal.x), Mathf.PI * 0.5f);
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        }

        return Vector3.right;
    }

    bool BoundaryDistance(Vector3 origin, Vector3 direction, out float distance)
    {
        bool found = BoundaryHit(origin, direction, out RaycastHit hit);
        distance = found ? hit.distance : 0f;
        return found;
    }

    bool BoundaryHit(Vector3 origin, Vector3 direction, out RaycastHit closest)
    {
        closest = default;
        int count = Physics.RaycastNonAlloc(origin, direction, boundaryHits, RoomProbeRange, roomBoundaryMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Collider collider = boundaryHits[i].collider;
            if (IgnoreSolid(collider) || collider is CharacterController || boundaryHits[i].distance >= best)
                continue;

            best = boundaryHits[i].distance;
            closest = boundaryHits[i];
        }

        return best < float.MaxValue;
    }

    bool TryFindShell(Vector3 rough, out Collider floor, out Collider ceiling, out float floorY, out float ceilingY)
    {
        floor = null;
        ceiling = null;
        floorY = rough.y;
        ceilingY = rough.y + 2.4f;

        RaycastHit[] down = Physics.RaycastAll(rough + Vector3.up * 0.75f, Vector3.down, 8f, obstacleMask, QueryTriggerInteraction.Ignore);
        RaycastHit floorHit = default;
        float floorScore = float.MinValue;
        bool foundFloor = false;
        for (int i = 0; i < down.Length; i++)
        {
            if (SkipShell(down[i].collider))
                continue;

            float score = ShellScore(down[i], true);
            if (score <= floorScore)
                continue;

            floorScore = score;
            floorHit = down[i];
            foundFloor = true;
        }

        if (!foundFloor)
            return false;

        floor = floorHit.collider;
        floorY = floorHit.point.y;

        RaycastHit[] up = Physics.RaycastAll(floorHit.point + Vector3.up * 0.15f, Vector3.up, 12f, obstacleMask, QueryTriggerInteraction.Ignore);
        RaycastHit ceilingHit = default;
        float ceilingScore = float.MinValue;
        bool foundCeiling = false;
        for (int i = 0; i < up.Length; i++)
        {
            if (SkipShell(up[i].collider) || up[i].collider == floor)
                continue;

            float score = ShellScore(up[i], false);
            if (score <= ceilingScore)
                continue;

            ceilingScore = score;
            ceilingHit = up[i];
            foundCeiling = true;
        }

        if (foundCeiling)
        {
            ceiling = ceilingHit.collider;
            ceilingY = ceilingHit.point.y;
        }

        // Rooms are told apart just under the ceiling, above the door headers.
        // With the ceilings hidden for a cutaway view, or only a roof far
        // overhead, a guessed height can fall below the door headers and join
        // every room through its doorways, so the walls give the height instead.
        if ((!foundCeiling || ceilingY - floorY > 4f) && TryFindWallTop(floorHit.point, floorY, out float wallTop)
            && (!foundCeiling || wallTop < ceilingY))
            ceilingY = wallTop;

        if (ceilingY - floorY < 0.8f)
            ceilingY = floorY + 2.4f;

        return true;
    }

    /// <summary>
    /// Height of the walls around a floor point: the highest height at which
    /// rays in most directions still meet the walls they meet at head height.
    /// False when too few walls are found around the point.
    /// </summary>
    bool TryFindWallTop(Vector3 floorPoint, float floorY, out float top)
    {
        const int Directions = 8;
        const float Step = 0.1f;
        top = 0f;
        var directions = new Vector3[Directions];
        var reach = new float[Directions];
        int walls = 0;
        Vector3 head = new Vector3(floorPoint.x, floorY + 1.2f, floorPoint.z);
        for (int i = 0; i < Directions; i++)
        {
            float angle = i * Mathf.PI * 2f / Directions;
            directions[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            bool wall = BoundaryHit(head, directions[i], out RaycastHit hit) && Mathf.Abs(hit.normal.y) < 0.3f;
            reach[i] = wall ? hit.distance : -1f;
            if (wall)
                walls++;
        }

        if (walls < 3)
            return false;

        bool found = false;
        for (float y = floorY + 1.8f; y <= floorY + 8f; y += Step)
        {
            Vector3 point = new Vector3(floorPoint.x, y, floorPoint.z);
            int still = 0;
            for (int i = 0; i < Directions; i++)
            {
                if (reach[i] >= 0f && BoundaryHit(point, directions[i], out RaycastHit hit) && Mathf.Abs(hit.distance - reach[i]) < 0.3f)
                    still++;
            }

            if (still * 2 >= walls)
            {
                top = y;
                found = true;
            }
            else if (found)
            {
                break;
            }
        }

        return found;
    }

    static float ShellScore(RaycastHit hit, bool floor)
    {
        string name = hit.collider.name;
        bool named = floor
            ? name.IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) >= 0
            : name.IndexOf("ceiling", System.StringComparison.OrdinalIgnoreCase) >= 0;
        if (named)
            return 100000f - hit.distance;

        float facing = floor ? hit.normal.y : -hit.normal.y;
        if (facing < 0.65f)
            return float.MinValue;

        Vector3 size = hit.collider.bounds.size;
        return size.x * size.z - hit.distance;
    }

    static bool SkipShell(Collider collider)
    {
        if (collider == null || collider.isTrigger)
            return true;

        return collider.name == "Hitbox";
    }

    /// <summary>
    /// Where in a cell a volume fits: over an interior floor, with nothing
    /// solid around it at cruise height. The center is tried first. Failing
    /// that, points up to half a cell away are tried with the volume's full
    /// radius, so a doorway still opens when the grid puts a cell's center
    /// right next to a door jamb, and the point lands where a volume fits
    /// through.
    /// </summary>
    bool TryOpenPoint(RoomAir room, Vector2Int cell, out Vector3 point)
    {
        Vector3 center = CellCenter(room, cell);
        point = center;
        if (OpenAt(room, center, bodyRadius * 0.75f))
            return true;

        for (int i = 0; i < NudgeOffsets.Length; i++)
        {
            Vector3 candidate = center + new Vector3(NudgeOffsets[i].x, 0f, NudgeOffsets[i].y);
            if (!OpenAt(room, candidate, bodyRadius))
                continue;

            point = candidate;
            return true;
        }

        return false;
    }

    // Offsets tried inside a blocked cell, nearest first, a quarter cell apart.
    static readonly Vector2[] NudgeOffsets = BuildNudgeOffsets();

    static Vector2[] BuildNudgeOffsets()
    {
        var offsets = new List<Vector2>();
        float step = CellSize * 0.25f;
        for (int z = -2; z <= 2; z++)
        {
            for (int x = -2; x <= 2; x++)
            {
                if (x != 0 || z != 0)
                    offsets.Add(new Vector2(x * step, z * step));
            }
        }

        offsets.Sort((a, b) => a.sqrMagnitude.CompareTo(b.sqrMagnitude));
        return offsets.ToArray();
    }

    bool OpenAt(RoomAir room, Vector3 point, float radius)
    {
        float high = room.ceilingY - 0.05f;
        float drop = high - room.floorY;
        if (drop < bodyRadius)
            return false;

        Collider[] overlaps = Physics.OverlapSphere(new Vector3(point.x, CruiseHeight(room), point.z), radius, obstacleMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < overlaps.Length; i++)
        {
            if (Blocks(room, overlaps[i]))
                return false;
        }

        RaycastHit[] hits = Physics.RaycastAll(new Vector3(point.x, high, point.z), Vector3.down, drop + 3f, obstacleMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider hit = hits[i].collider;
            if (hit == null || SkipShell(hit))
                continue;
            if (hit.name.IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a volume can pass in a straight line between two points at
    /// cruise height. A thin sweep finds the wall between two cells on either
    /// side of it, and leaves a doorway open.
    /// </summary>
    bool Passable(RoomAir room, Vector3 from, Vector3 to)
    {
        float y = CruiseHeight(room);
        from.y = y;
        to.y = y;
        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (distance < 0.0001f)
            return true;

        RaycastHit[] hits = Physics.SphereCastAll(from, bodyRadius * 0.5f, delta / distance, distance, obstacleMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            if (Blocks(room, hits[i].collider))
                return false;
        }

        return true;
    }

    /// <summary>Colliders that keep smoke out: anything solid but the floor and ceiling.</summary>
    bool Blocks(RoomAir room, Collider collider)
    {
        if (collider == null)
            return false;
        if (collider == room.floor || collider == room.ceiling)
            return false;
        if (collider.name.IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (collider.name.IndexOf("ceiling", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        return !IgnoreSolid(collider);
    }

    float CruiseHeight(RoomAir room)
    {
        float low = room.floorY + bodyRadius;
        float high = room.ceilingY - bodyRadius;
        if (high < low)
            return (room.floorY + room.ceilingY) * 0.5f;

        return Mathf.Clamp(room.floorY + heightOffset, low, high);
    }

    Vector3 StayInOpenAir(RoomAir room, Vector3 from, Vector3 to)
    {
        if (room == null || room.open.Count == 0)
            return to;

        to = ClampHeight(room, to);
        if (IsInsideOpen(room, to))
            return to;

        Vector3 alongX = ClampHeight(room, new Vector3(to.x, to.y, from.z));
        if (IsInsideOpen(room, alongX))
            return alongX;

        Vector3 alongZ = ClampHeight(room, new Vector3(from.x, to.y, to.z));
        if (IsInsideOpen(room, alongZ))
            return alongZ;

        Vector3 vertical = ClampHeight(room, new Vector3(from.x, to.y, from.z));
        if (IsInsideOpen(room, vertical))
            return vertical;

        return from;
    }

    Vector3 ClampHeight(RoomAir room, Vector3 position)
    {
        float low = room.floorY + bodyRadius * 0.5f;
        float high = room.ceilingY - VolumeTopClearance();
        if (high < low)
            position.y = (room.floorY + room.ceilingY) * 0.5f;
        else
            position.y = Mathf.Clamp(position.y, low, high);
        return position;
    }

    bool IsInsideOpen(RoomAir room, Vector3 position)
    {
        if (position.y < room.floorY || position.y > room.ceilingY)
            return false;

        return room.open.Contains(ToCell(room, position));
    }

    Vector2Int NearestOpenCell(RoomAir room, Vector2Int around)
    {
        Vector2Int best = around;
        float bestDistance = float.MaxValue;
        foreach (Vector2Int cell in room.open)
        {
            float distance = (cell - around).sqrMagnitude;
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = cell;
        }

        return best;
    }

    static Vector2Int ToCell(RoomAir room, Vector3 world)
    {
        return new Vector2Int(
            Mathf.RoundToInt((world.x - room.origin.x) / CellSize),
            Mathf.RoundToInt((world.z - room.origin.z) / CellSize));
    }

    static Vector3 CellCenter(RoomAir room, Vector2Int cell)
    {
        return new Vector3(room.origin.x + cell.x * CellSize, room.floorY, room.origin.z + cell.y * CellSize);
    }

    float VolumeTopClearance()
    {
        float scaleY = blockHolder != null ? Mathf.Abs(blockHolder.lossyScale.y) : 1f;
        return generatedBlockScale.y * 0.5f * scaleY + particleCeilingMargin;
    }

    /// <summary>
    /// Pulls a volume down so its top, including the Ignis particles rising out of the box, stays under the nearest ceiling.
    /// </summary>
    Vector3 KeepUnderCeiling(Vector3 position)
    {
        float underside = CeilingAbove(position);
        if (float.IsPositiveInfinity(underside))
            return position;

        float limit = underside - VolumeTopClearance();
        if (position.y > limit)
            position.y = limit;
        return position;
    }

    /// <summary>Height of the lowest ceiling or overhang above a point, or infinity when there is none.</summary>
    float CeilingAbove(Vector3 position)
    {
        RaycastHit[] hits = Physics.RaycastAll(position, Vector3.up, 8f, obstacleMask, QueryTriggerInteraction.Ignore);
        float underside = float.PositiveInfinity;
        for (int i = 0; i < hits.Length; i++)
        {
            if (IgnoreSolid(hits[i].collider) || hits[i].normal.y > -0.35f)
                continue;

            if (hits[i].point.y < underside)
                underside = hits[i].point.y;
        }

        return underside;
    }

    bool IgnoreSolid(Collider collider)
    {
        if (collider == null || collider.isTrigger)
            return true;

        if (blockHolder != null && collider.transform.IsChildOf(blockHolder))
            return true;

        if (collider.name != "Hitbox")
            return false;

        Vector3 size = collider.bounds.size;
        return size.x > 2f && size.y > 2f && size.z > 2f;
    }

    void ReleaseVolume(SmokeVolume volume)
    {
        if (volume.smoke == null)
            return;

        if (volume.smoke.onFire)
            volume.smoke.ResetObj();

        Destroy(volume.smoke.gameObject);
        volume.smoke = null;
    }

    void ResetFront(FireFront front)
    {
        front.originLocked = false;
        front.burnSeconds = 0f;
        front.spawnCredit = 0f;
        front.flameOutput = 0f;
        front.farthestDistance = 0f;
        front.room = null;
        front.smokeFill.Clear();
        front.fillField.Clear();
        front.fillFieldTime.Clear();
        front.fillFieldSeeds.Clear();
        front.lingeringCount = 0;
        SmokeRoomClipVolumes.Remove(front);
    }

    void ReleaseFront(FireFront front)
    {
        for (int i = 0; i < front.volumes.Count; i++)
            ReleaseVolume(front.volumes[i]);

        front.volumes.Clear();
        RestoreEmbeddedSmoke(front);
        ReleaseVents(front);
        ResetFront(front);
    }

    void ClearFronts()
    {
        for (int i = 0; i < fronts.Count; i++)
            ReleaseFront(fronts[i]);

        fronts.Clear();
    }

    void OnDrawGizmosSelected()
    {
        for (int i = 0; i < fronts.Count; i++)
        {
            RoomAir room = fronts[i].room;
            if (room == null)
                continue;

            Gizmos.color = new Color(0.4f, 0.85f, 1f, 0.35f);
            foreach (Vector2Int cell in room.open)
            {
                Vector3 center = PointOf(room, cell);
                center.y = CruiseHeight(room);
                Gizmos.DrawWireCube(center, new Vector3(CellSize * 0.85f, 0.05f, CellSize * 0.85f));
            }
        }
    }
}
