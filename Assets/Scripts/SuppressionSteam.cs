using System.Collections.Generic;
using Ignis;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Renders the spray impact shared by networked and local fires.</summary>
[DisallowMultipleComponent]
public sealed class SuppressionSteam : MonoBehaviour
{
    private const float SecondsBetweenPuffs = 0.06f;
    private static readonly int RoomBoxId = Shader.PropertyToID("_SteamRoomBox");
    private static readonly List<SuppressionSteam> enabledEmitters = new();
    private readonly System.Random _puffRandom = new System.Random();

    internal static IReadOnlyList<SuppressionSteam> EnabledEmitters => enabledEmitters;

    private ParticleSystem _particles;
    private ParticleSystemRenderer _renderer;
    private Material _material;
    private MaterialPropertyBlock _properties;
    private Vector3 _fireOrigin;
    private int _roomBox = int.MinValue;
    private float _nextPuffTime;
    private Vector3 _lastPuffPosition;
    private float _lastPuffTime = float.NegativeInfinity;

    public static void EmitAt(FlammableObject fire, Vector3 point)
    {
        if (!fire.TryGetComponent<SuppressionSteam>(out var steam))
            steam = fire.gameObject.AddComponent<SuppressionSteam>();

        steam.Emit(point, fire.GetFireOrigin());
    }

    public static void Clear(FlammableObject fire)
    {
        if (fire != null && fire.TryGetComponent<SuppressionSteam>(out var steam))
            steam.ClearParticles();
    }

    /// <summary>Provides the last emission point even after the visible particles have faded.</summary>
    internal bool TryGetRecentImpact(float earliestPuffTime, out Vector3 position)
    {
        position = _lastPuffPosition;
        return _lastPuffTime >= earliestPuffTime;
    }

    private void Awake()
    {
        Shader shader = Resources.Load<Shader>("Smoke/SuppressionSteam");
        if (shader == null)
        {
            Debug.LogWarning("Could not load the suppression steam shader.", this);
            return;
        }

        var emitter = new GameObject("Suppression Steam");
        emitter.transform.SetParent(transform, false);
        _particles = emitter.AddComponent<ParticleSystem>();
        _particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = _particles.main;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Shape;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        main.maxParticles = 32;

        var emission = _particles.emission;
        emission.enabled = false;
        var shape = _particles.shape;
        shape.enabled = false;

        var color = _particles.colorOverLifetime;
        color.enabled = true;
        color.color = new Gradient
        {
            colorKeys = new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            alphaKeys = new[] { new GradientAlphaKey(0.45f, 0f), new GradientAlphaKey(0.75f, 0.18f), new GradientAlphaKey(0f, 1f) }
        };

        var size = _particles.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.45f, 1f, 1.6f));

        _material = new Material(shader);
        _properties = new MaterialPropertyBlock();
        _renderer = emitter.GetComponent<ParticleSystemRenderer>();
        _renderer.sharedMaterial = _material;
        _renderer.renderMode = ParticleSystemRenderMode.VerticalBillboard;
        _renderer.allowRoll = false;
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
    }

    private void Emit(Vector3 point, Vector3 fireOrigin)
    {
        float now = Time.time;
        if (!isActiveAndEnabled || _particles == null || now < _nextPuffTime)
            return;

        _nextPuffTime = now + SecondsBetweenPuffs;
        _fireOrigin = fireOrigin;
        UpdateRoom();
        if (!_particles.isPlaying)
            _particles.Play(false);

        // Give the contact a small lift so the first puff is visible above the surface.
        _lastPuffPosition = point + Vector3.up * 0.07f;
        _lastPuffTime = now;
        _particles.Emit(new ParticleSystem.EmitParams
        {
            position = _lastPuffPosition,
            velocity = new Vector3(Vary(-0.15f, 0.15f), Vary(0.45f, 0.85f), Vary(-0.15f, 0.15f)),
            startSize = Vary(0.28f, 0.48f),
            startLifetime = Vary(0.9f, 1.55f),
            rotation = Vary(0f, 360f),
            startColor = Color.white
        }, 1);
    }

    private float Vary(float min, float max) => Mathf.Lerp(min, max, (float)_puffRandom.NextDouble());

    private void LateUpdate()
    {
        if (_particles == null || !_particles.isPlaying)
            return;

        if (_particles.particleCount > 0)
            UpdateRoom();
        else
            _particles.Stop(false, ParticleSystemStopBehavior.StopEmitting);
    }

    private void UpdateRoom()
    {
        int box = SmokeRoomClipVolumes.FindContainingBox(_fireOrigin);
        if (box == _roomBox)
            return;

        _roomBox = box;
        _properties.SetFloat(RoomBoxId, box);
        _renderer.SetPropertyBlock(_properties);
    }

    private void ClearParticles()
    {
        _nextPuffTime = 0f;
        _lastPuffTime = float.NegativeInfinity;
        if (_particles != null)
            _particles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void OnEnable() => enabledEmitters.Add(this);

    private void OnDisable()
    {
        enabledEmitters.Remove(this);
        ClearParticles();
    }

    private void OnDestroy()
    {
        if (_particles != null)
            Destroy(_particles.gameObject);
        if (_material != null)
            Destroy(_material);
    }
}
