using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace ElintriaEngine.Core
{
    // ── Enums ──────────────────────────────────────────────────────────────────
    public enum ParticleSimulationSpace { Local, World }
    public enum ParticleScalingMode { Hierarchy, Local, Shape }
    public enum ParticleRenderMode { Billboard, StretchedBillboard, HorizontalBillboard, VerticalBillboard, Mesh }
    public enum ParticleShape { Sphere, Hemisphere, Cone, Box, Circle, Edge, Point }
    public enum ParticleSortMode { None, ByDistance, OldestInFront, YoungestInFront }
    public enum ParticleStopAction { None, Disable, Destroy, Callback }
    public enum ParticleCullingMode { AlwaysSimulate, PauseAndCatchup, Pause, StopEmitting }

    // ── Gradient key ──────────────────────────────────────────────────────────
    [System.Serializable]
    public class GradientColorKey
    {
        public float Time { get; set; }
        public float R { get; set; } = 1f;
        public float G { get; set; } = 1f;
        public float B { get; set; } = 1f;
        public float A { get; set; } = 1f;

        public GradientColorKey() { }
        public GradientColorKey(float t, float r, float g, float b, float a = 1f)
        { Time = t; R = r; G = g; B = b; A = a; }
    }

    // ── Curve key ─────────────────────────────────────────────────────────────
    [System.Serializable]
    public class CurveKey
    {
        public float Time { get; set; }
        public float Value { get; set; }
        public CurveKey() { }
        public CurveKey(float t, float v) { Time = t; Value = v; }
    }

    // ── MinMaxCurve ───────────────────────────────────────────────────────────
    public class MinMaxCurve
    {
        public float Constant { get; set; }
        public float ConstantMin { get; set; }
        public float ConstantMax { get; set; } = 1f;
        public bool UseRange { get; set; } = false;
        public List<CurveKey> CurveMin { get; set; } = new() { new(0, 1), new(1, 1) };
        public List<CurveKey> CurveMax { get; set; } = new() { new(0, 1), new(1, 1) };
        public bool UseCurve { get; set; } = false;

        public float Evaluate(float t, float? rand = null)
        {
            if (UseCurve)
            {
                float lo = EvalCurve(CurveMin, t);
                float hi = EvalCurve(CurveMax, t);
                return float.IsNaN(rand ?? float.NaN) ? hi : lo + (hi - lo) * (rand!.Value);
            }
            if (UseRange)
            {
                float r = rand ?? 0.5f;
                return ConstantMin + (ConstantMax - ConstantMin) * r;
            }
            return Constant;
        }

        private static float EvalCurve(List<CurveKey> keys, float t)
        {
            if (keys.Count == 0) return 1f;
            if (keys.Count == 1) return keys[0].Value;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                var a = keys[i]; var b = keys[i + 1];
                if (t >= a.Time && t <= b.Time)
                {
                    float nt = (b.Time - a.Time) < 0.0001f ? 0f : (t - a.Time) / (b.Time - a.Time);
                    return a.Value + (b.Value - a.Value) * nt;
                }
            }
            return keys[^1].Value;
        }

        // Factory helpers
        public static MinMaxCurve ConstantCurve(float v) => new() { Constant = v };
        public static MinMaxCurve Range(float lo, float hi) => new() { ConstantMin = lo, ConstantMax = hi, UseRange = true };
        public static MinMaxCurve Curve(params (float t, float v)[] pts)
        {
            var c = new MinMaxCurve { UseCurve = true };
            foreach (var (t, v) in pts) { c.CurveMin.Add(new CurveKey(t, v)); c.CurveMax.Add(new CurveKey(t, v)); }
            return c;
        }
    }

    // ── MinMaxGradient ────────────────────────────────────────────────────────
    public class MinMaxGradient
    {
        public List<GradientColorKey> GradientMin { get; set; } = new()
            { new(0f, 1,1,1,1), new(1f, 1,1,1,1) };
        public List<GradientColorKey> GradientMax { get; set; } = new()
            { new(0f, 1,1,1,1), new(1f, 1,1,1,1) };
        public bool UseGradient { get; set; } = false;
        public float R { get; set; } = 1f;
        public float G { get; set; } = 1f;
        public float B { get; set; } = 1f;
        public float A { get; set; } = 1f;

        public (float r, float g, float b, float a) Evaluate(float t, float? rand = null)
        {
            if (!UseGradient) return (R, G, B, A);
            var lo = SampleGrad(GradientMin, t);
            var hi = SampleGrad(GradientMax, t);
            float f = rand ?? 0f;
            return (lo.r + (hi.r - lo.r) * f, lo.g + (hi.g - lo.g) * f,
                    lo.b + (hi.b - lo.b) * f, lo.a + (hi.a - lo.a) * f);
        }

        private static (float r, float g, float b, float a) SampleGrad(List<GradientColorKey> g, float t)
        {
            if (g.Count == 0) return (1, 1, 1, 1);
            if (g.Count == 1) return (g[0].R, g[0].G, g[0].B, g[0].A);
            for (int i = 0; i < g.Count - 1; i++)
            {
                var a = g[i]; var b = g[i + 1];
                if (t >= a.Time && t <= b.Time)
                {
                    float nt = (b.Time - a.Time) < 0.0001f ? 0f : (t - a.Time) / (b.Time - a.Time);
                    return (a.R + (b.R - a.R) * nt, a.G + (b.G - a.G) * nt, a.B + (b.B - a.B) * nt, a.A + (b.A - a.A) * nt);
                }
            }
            var last = g[^1]; return (last.R, last.G, last.B, last.A);
        }
    }

    // ── Burst ─────────────────────────────────────────────────────────────────
    public class ParticleBurst
    {
        public float Time { get; set; } = 0f;
        public int Count { get; set; } = 30;
        public int Cycles { get; set; } = 1;
        public float Interval { get; set; } = 0.01f;
        public float Probability { get; set; } = 1f;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ParticleSystem  —  the main component
    // ═══════════════════════════════════════════════════════════════════════════
    public class ParticleSystem : Component
    {
        // ── Main module ───────────────────────────────────────────────────────
        public float Duration { get; set; } = 5f;
        public bool Looping { get; set; } = true;
        public bool Prewarm { get; set; } = false;
        public float StartDelay { get; set; } = 0f;
        public float StartLifetime { get; set; } = 5f;
        public float StartLifetimeMax { get; set; } = 5f;
        public bool StartLifetimeRange { get; set; } = false;
        public float StartSpeed { get; set; } = 5f;
        public float StartSpeedMax { get; set; } = 5f;
        public bool StartSpeedRange { get; set; } = false;
        public float StartSize { get; set; } = 1f;
        public float StartSizeMax { get; set; } = 1f;
        public bool StartSizeRange { get; set; } = false;
        public bool StartSize3D { get; set; } = false;
        public float StartSizeX { get; set; } = 1f;
        public float StartSizeY { get; set; } = 1f;
        public float StartSizeZ { get; set; } = 1f;
        public float StartRotation { get; set; } = 0f;
        public float StartRotationMax { get; set; } = 0f;
        public bool StartRotationRange { get; set; } = false;
        public bool StartRotation3D { get; set; } = false;
        public float StartRotationX { get; set; } = 0f;
        public float StartRotationY { get; set; } = 0f;
        public float StartRotationZ { get; set; } = 0f;
        public float StartColorR { get; set; } = 1f;
        public float StartColorG { get; set; } = 1f;
        public float StartColorB { get; set; } = 1f;
        public float StartColorA { get; set; } = 1f;
        public float GravityModifier { get; set; } = 0f;
        public ParticleSimulationSpace SimulationSpace { get; set; } = ParticleSimulationSpace.Local;
        public ParticleScalingMode ScalingMode { get; set; } = ParticleScalingMode.Hierarchy;
        public bool PlayOnAwake { get; set; } = true;
        public int MaxParticles { get; set; } = 1000;
        public ParticleStopAction StopAction { get; set; } = ParticleStopAction.None;
        public ParticleCullingMode CullingMode { get; set; } = ParticleCullingMode.AlwaysSimulate;

        // ── Emission ──────────────────────────────────────────────────────────
        public bool EmissionEnabled { get; set; } = true;
        public float RateOverTime { get; set; } = 10f;
        public float RateOverDistance { get; set; } = 0f;
        public List<ParticleBurst> Bursts { get; set; } = new();

        // ── Shape ─────────────────────────────────────────────────────────────
        public bool ShapeEnabled { get; set; } = true;
        public ParticleShape Shape { get; set; } = ParticleShape.Cone;
        public float ShapeRadius { get; set; } = 1f;
        public float ShapeAngle { get; set; } = 25f;
        public float ShapeArc { get; set; } = 360f;
        public float ShapeLength { get; set; } = 5f;
        public float ShapeBoxX { get; set; } = 1f;
        public float ShapeBoxY { get; set; } = 1f;
        public float ShapeBoxZ { get; set; } = 1f;
        public bool ShapeAlignToDirection { get; set; } = false;
        public float RandomDirectionAmount { get; set; } = 0f;
        public float SphericalDirectionAmount { get; set; } = 0f;
        public float RandomPositionAmount { get; set; } = 0f;

        // ── Velocity Over Lifetime ────────────────────────────────────────────
        public bool VelocityEnabled { get; set; } = false;
        public float VelocityX { get; set; } = 0f;
        public float VelocityY { get; set; } = 0f;
        public float VelocityZ { get; set; } = 0f;
        public ParticleSimulationSpace VelocitySpace { get; set; } = ParticleSimulationSpace.Local;

        // ── Color Over Lifetime ───────────────────────────────────────────────
        public bool ColorEnabled { get; set; } = false;
        public MinMaxGradient ColorGradient { get; set; } = new();

        // ── Size Over Lifetime ────────────────────────────────────────────────
        public bool SizeEnabled { get; set; } = false;
        public MinMaxCurve SizeCurve { get; set; } = new() { Constant = 1f };

        // ── Rotation Over Lifetime ────────────────────────────────────────────
        public bool RotationEnabled { get; set; } = false;
        public float RotationSpeed { get; set; } = 45f;
        public float RotationSpeedMax { get; set; } = 45f;
        public bool RotationSpeedRange { get; set; } = false;

        // ── External Forces ───────────────────────────────────────────────────
        public bool ExtForcesEnabled { get; set; } = false;
        public float ExtForceMultiplier { get; set; } = 1f;

        // ── Noise ─────────────────────────────────────────────────────────────
        public bool NoiseEnabled { get; set; } = false;
        public float NoiseStrength { get; set; } = 1f;
        public float NoiseFrequency { get; set; } = 0.5f;
        public int NoiseOctaves { get; set; } = 1;
        public float NoiseScrollSpeed { get; set; } = 0f;
        public bool NoiseDamping { get; set; } = true;

        // ── Collision ─────────────────────────────────────────────────────────
        public bool CollisionEnabled { get; set; } = false;
        public bool CollisionWorld { get; set; } = true;  // else Planes
        public float CollisionDampen { get; set; } = 0f;
        public float CollisionBounce { get; set; } = 0f;
        public float CollisionLifetimeLoss { get; set; } = 0f;
        public float CollisionMinKillSpeed { get; set; } = 0f;
        public float CollisionRadius { get; set; } = 0.01f;
        public bool CollisionSendMsg { get; set; } = false;

        // ── Lights ────────────────────────────────────────────────────────────
        public bool LightsEnabled { get; set; } = false;
        public float LightsRatio { get; set; } = 0f;
        public bool LightsUseParticleColor { get; set; } = true;
        public float LightsRange { get; set; } = 1f;
        public float LightsIntensity { get; set; } = 1f;
        public int LightsMaxLights { get; set; } = 20;

        // ── Trails ────────────────────────────────────────────────────────────
        public bool TrailsEnabled { get; set; } = false;
        public float TrailsRatio { get; set; } = 1f;
        public float TrailsLifetime { get; set; } = 0.5f;
        public float TrailsMinVertexDistance { get; set; } = 0.1f;
        public bool TrailsWorldSpace { get; set; } = false;
        public bool TrailsDieWithParticle { get; set; } = true;
        public float TrailsWidth { get; set; } = 0.1f;
        public bool TrailsColorInherit { get; set; } = true;

        // ── Renderer ──────────────────────────────────────────────────────────
        public bool RendererEnabled { get; set; } = true;
        public ParticleRenderMode RenderMode { get; set; } = ParticleRenderMode.Billboard;
        public ParticleSortMode SortMode { get; set; } = ParticleSortMode.None;
        public float SortingFudge { get; set; } = 0f;
        public float MinParticleSize { get; set; } = 0f;
        public float MaxParticleSize { get; set; } = 0.5f;
        public string MaterialName { get; set; } = "Default-Particle";
        public bool ReceiveShadows { get; set; } = false;
        public bool CastShadows { get; set; } = false;
        public float ShadowBias { get; set; } = 0f;
        public float StretchLength { get; set; } = 2f;  // billboard stretch
        public float StretchSpeedScale { get; set; } = 0f;

        // ── Runtime state (not serialized — just live simulation) ─────────────
        [System.Text.Json.Serialization.JsonIgnore]
        public List<Particle> Particles { get; } = new();
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsPlaying { get; private set; } = false;
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsPaused { get; private set; } = false;
        [System.Text.Json.Serialization.JsonIgnore]
        public float PlaybackTime { get; private set; } = 0f;

        private float _emitAccum = 0f;
        private readonly Random _rng = new();

        // ── Playback API ──────────────────────────────────────────────────────
        public void Play() { IsPlaying = true; IsPaused = false; PlaybackTime = 0f; _emitAccum = 0f; }
        public void Stop() { IsPlaying = false; IsPaused = false; Particles.Clear(); PlaybackTime = 0f; }
        public void Pause() { IsPaused = true; }
        public void Resume() { IsPaused = false; }
        public void Simulate(float t) { if (!IsPlaying) Play(); Tick(t); }

        // ── Per-frame update ──────────────────────────────────────────────────
        public void Tick(float dt)
        {
            if (!IsPlaying || IsPaused) return;

            PlaybackTime += dt;

            // Check if duration elapsed
            if (!Looping && PlaybackTime > Duration + StartDelay)
            {
                if (Particles.Count == 0) Stop();
                return;
            }

            float simTime = PlaybackTime - StartDelay;
            if (simTime < 0f) return;

            // Emission
            if (EmissionEnabled && (Looping || simTime <= Duration))
            {
                _emitAccum += RateOverTime * dt;
                while (_emitAccum >= 1f && Particles.Count < MaxParticles)
                {
                    SpawnParticle();
                    _emitAccum -= 1f;
                }
            }

            // Update particles
            var g = new Vector3(0f, GravityModifier * -9.81f, 0f);
            for (int i = Particles.Count - 1; i >= 0; i--)
            {
                var p = Particles[i];
                p.Age += dt;
                if (p.Age >= p.Lifetime) { Particles.RemoveAt(i); continue; }

                float t01 = p.Age / p.Lifetime;

                // Velocity over lifetime
                if (VelocityEnabled)
                    p.Velocity += new Vector3(VelocityX, VelocityY, VelocityZ) * dt;

                // Noise
                if (NoiseEnabled)
                {
                    // simple turbulence approximation
                    float ns = NoiseStrength * dt;
                    float freq = NoiseFrequency;
                    p.Velocity += new Vector3(
                        (float)(Math.Sin(p.Position.X * freq + PlaybackTime * NoiseScrollSpeed) * ns),
                        (float)(Math.Cos(p.Position.Y * freq + PlaybackTime * NoiseScrollSpeed) * ns),
                        (float)(Math.Sin(p.Position.Z * freq + PlaybackTime * NoiseScrollSpeed + 7.3f) * ns));
                }

                // Gravity + integrate
                p.Velocity += g * dt;
                p.Position += p.Velocity * dt;

                // Rotation over lifetime
                if (RotationEnabled)
                    p.Rotation += p.RotationSpeed * dt;

                // Size over lifetime
                if (SizeEnabled)
                {
                    float s = SizeCurve.Evaluate(t01);
                    p.CurrentSize = p.BaseSize * s;
                }

                // Color over lifetime
                if (ColorEnabled)
                {
                    var (r, g2, b, a) = ColorGradient.Evaluate(t01);
                    p.ColorR = r; p.ColorG = g2; p.ColorB = b; p.ColorA = a;
                }
            }
        }

        private void SpawnParticle()
        {
            float r1 = (float)_rng.NextDouble();
            float r2 = (float)_rng.NextDouble();
            float r3 = (float)_rng.NextDouble();
            float r4 = (float)_rng.NextDouble();

            float lifetime = StartLifetimeRange
                ? StartLifetime + (StartLifetimeMax - StartLifetime) * r1
                : StartLifetime;
            float speed = StartSpeedRange
                ? StartSpeed + (StartSpeedMax - StartSpeed) * r2
                : StartSpeed;
            float size = StartSizeRange
                ? StartSize + (StartSizeMax - StartSize) * r3
                : StartSize;
            float rot = StartRotationRange
                ? StartRotation + (StartRotationMax - StartRotation) * r4
                : StartRotation;
            float rotSpeed = RotationSpeedRange
                ? RotationSpeed + (RotationSpeedMax - RotationSpeed) * (float)_rng.NextDouble()
                : RotationSpeed;

            var (pos, dir) = SampleShape();

            // apply random direction amount
            if (RandomDirectionAmount > 0f)
            {
                var randDir = new Vector3(
                    (float)(_rng.NextDouble() * 2 - 1),
                    (float)(_rng.NextDouble() * 2 - 1),
                    (float)(_rng.NextDouble() * 2 - 1)).Normalized();
                dir = Vector3.Normalize(Vector3.Lerp(dir, randDir, RandomDirectionAmount));
            }

            var p = new Particle
            {
                Position = pos,
                Velocity = dir * speed,
                Lifetime = lifetime,
                BaseSize = size,
                CurrentSize = size,
                Rotation = rot,
                RotationSpeed = rotSpeed,
                ColorR = StartColorR,
                ColorG = StartColorG,
                ColorB = StartColorB,
                ColorA = StartColorA,
            };

            Particles.Add(p);
        }

        private (Vector3 pos, Vector3 dir) SampleShape()
        {
            switch (Shape)
            {
                case ParticleShape.Sphere:
                    {
                        var d = RandomOnSphere();
                        return (d * ShapeRadius, d);
                    }
                case ParticleShape.Hemisphere:
                    {
                        var d = RandomOnSphere();
                        d.Y = Math.Abs(d.Y);
                        return (d * ShapeRadius, d);
                    }
                case ParticleShape.Cone:
                    {
                        // random point on disc at base, direction toward cone apex direction
                        double r = Math.Sqrt(_rng.NextDouble()) * ShapeRadius;
                        double theta = _rng.NextDouble() * (ShapeArc / 360.0) * Math.PI * 2.0;
                        float x = (float)(r * Math.Cos(theta));
                        float z = (float)(r * Math.Sin(theta));
                        float tanA = (float)Math.Tan(MathHelper.DegreesToRadians(ShapeAngle));
                        var dir = new Vector3(x * tanA, 1f, z * tanA).Normalized();
                        return (new Vector3(x, 0f, z), dir);
                    }
                case ParticleShape.Box:
                    {
                        var pos = new Vector3(
                            (float)(_rng.NextDouble() * 2 - 1) * ShapeBoxX,
                            (float)(_rng.NextDouble() * 2 - 1) * ShapeBoxY,
                            (float)(_rng.NextDouble() * 2 - 1) * ShapeBoxZ);
                        return (pos, Vector3.UnitY);
                    }
                case ParticleShape.Circle:
                    {
                        double theta = _rng.NextDouble() * (ShapeArc / 360.0) * Math.PI * 2.0;
                        double r = Math.Sqrt(_rng.NextDouble()) * ShapeRadius;
                        var pos = new Vector3((float)(r * Math.Cos(theta)), 0f, (float)(r * Math.Sin(theta)));
                        return (pos, Vector3.UnitY);
                    }
                case ParticleShape.Edge:
                    {
                        float t = (float)_rng.NextDouble() * 2f - 1f;
                        return (new Vector3(t * ShapeRadius, 0, 0), Vector3.UnitY);
                    }
                default:
                    return (Vector3.Zero, Vector3.UnitY);
            }
        }

        private Vector3 RandomOnSphere()
        {
            double theta = _rng.NextDouble() * 2.0 * Math.PI;
            double phi = Math.Acos(2.0 * _rng.NextDouble() - 1.0);
            return new Vector3(
                (float)(Math.Sin(phi) * Math.Cos(theta)),
                (float)(Math.Cos(phi)),
                (float)(Math.Sin(phi) * Math.Sin(theta)));
        }
    }

    // ── Particle instance ─────────────────────────────────────────────────────
    public class Particle
    {
        public Vector3 Position { get; set; }
        public Vector3 Velocity { get; set; }
        public float Age { get; set; }
        public float Lifetime { get; set; } = 1f;
        public float BaseSize { get; set; } = 1f;
        public float CurrentSize { get; set; } = 1f;
        public float Rotation { get; set; }
        public float RotationSpeed { get; set; }
        public float ColorR { get; set; } = 1f;
        public float ColorG { get; set; } = 1f;
        public float ColorB { get; set; } = 1f;
        public float ColorA { get; set; } = 1f;
    }
}