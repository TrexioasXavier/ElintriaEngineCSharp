using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK.Windowing.Common;
using ElintriaEngine.Core;

namespace ElintriaEngine.UI.Panels
{
    /// <summary>
    /// Draws the full Unity-style Particle System inspector inside the InspectorPanel.
    /// Call DrawParticleSystem(r, cr, ps, ref y) from InspectorPanel.DrawComponent.
    /// All sections are collapsible. Sliders, toggles, dropdowns, and range fields
    /// all write back directly to the ParticleSystem component.
    /// </summary>
    public class ParticleSystemInspector
    {
        // ── Colors ────────────────────────────────────────────────────────────
        private static readonly Color CBg = Color.FromArgb(255, 36, 38, 46);
        private static readonly Color CBgAlt = Color.FromArgb(255, 32, 34, 42);
        private static readonly Color CHead = Color.FromArgb(255, 28, 30, 40);
        private static readonly Color CHeadSel = Color.FromArgb(255, 40, 60, 100);
        private static readonly Color CAccent = Color.FromArgb(255, 60, 130, 255);
        private static readonly Color CText = Color.FromArgb(255, 210, 215, 225);
        private static readonly Color CTextDim = Color.FromArgb(255, 130, 140, 155);
        private static readonly Color CBorder = Color.FromArgb(255, 48, 52, 64);
        private static readonly Color CTogOn = Color.FromArgb(255, 45, 140, 70);
        private static readonly Color CTogOff = Color.FromArgb(255, 55, 55, 68);
        private static readonly Color CPlayGrn = Color.FromArgb(255, 40, 160, 70);
        private static readonly Color CPauseYel = Color.FromArgb(255, 200, 160, 40);
        private static readonly Color CStopRed = Color.FromArgb(255, 180, 50, 50);

        // ── Section open/closed state (keyed by "psHash_SectionName") ─────────
        private readonly Dictionary<string, bool> _open = new();

        // ── Slider drag state ─────────────────────────────────────────────────
        private string? _dragId;
        private RectangleF _dragTrack;
        private float _dragMin, _dragMax;
        private bool _dragIsInt;

        // ── Stored track rects for drag detection (rebuilt each frame) ────────
        private readonly Dictionary<string, (RectangleF Track, float Min, float Max, bool IsInt)> _tracks = new();

        private const float RowH = 22f;
        private const float HeadH = 22f;
        private const float LabelW = 0.44f;  // fraction of content width for label column

        private PointF _mouse;

        // ─────────────────────────────────────────────────────────────────────
        public void SetMouse(PointF p) => _mouse = p;

        // ── Drag start (call from InspectorPanel.OnMouseDown) ─────────────────
        public bool TryStartDrag(PointF pos)
        {
            foreach (var kv in _tracks)
            {
                if (kv.Value.Track.Contains(pos))
                {
                    _dragId = kv.Key;
                    _dragTrack = kv.Value.Track;
                    _dragMin = kv.Value.Min;
                    _dragMax = kv.Value.Max;
                    _dragIsInt = kv.Value.IsInt;
                    return true;
                }
            }
            return false;
        }

        public bool IsDragging => _dragId != null;

        public void UpdateDrag(float mouseX, ParticleSystem ps)
        {
            if (_dragId == null) return;
            float t = Math.Clamp((mouseX - _dragTrack.X) / _dragTrack.Width, 0f, 1f);
            float v = _dragMin + (_dragMax - _dragMin) * t;
            ApplyDragValue(_dragId, _dragIsInt ? MathF.Round(v) : v, ps);
        }

        public void EndDrag() => _dragId = null;

        // ── Section header toggle ─────────────────────────────────────────────
        public bool TryToggleSection(PointF pos, IEditorRenderer r,
                                     RectangleF cr, ParticleSystem ps, ref float y)
        {
            // We just check the stored section header rects registered during last render
            foreach (var kv in _sectionHeaderRects)
            {
                if (kv.Value.Contains(pos))
                {
                    _open[kv.Key] = !IsOpen(kv.Key);
                    return true;
                }
            }
            return false;
        }

        // ── Playback button clicks ─────────────────────────────────────────────
        public bool TryHandlePlaybackClick(PointF pos, ParticleSystem ps)
        {
            foreach (var kv in _playbackRects)
            {
                if (kv.Value.Contains(pos))
                {
                    switch (kv.Key)
                    {
                        case "play": if (ps.IsPlaying) ps.Stop(); else ps.Play(); break;
                        case "pause": if (ps.IsPaused) ps.Resume(); else ps.Pause(); break;
                        case "stop": ps.Stop(); break;
                        case "restart": ps.Stop(); ps.Play(); break;
                    }
                    return true;
                }
            }
            return false;
        }

        private readonly Dictionary<string, RectangleF> _sectionHeaderRects = new();
        private readonly Dictionary<string, RectangleF> _playbackRects = new();

        // ══════════════════════════════════════════════════════════════════════
        //  Main entry — called from InspectorPanel
        // ══════════════════════════════════════════════════════════════════════
        public void Draw(IEditorRenderer r, RectangleF cr, ParticleSystem ps, ref float y,
                         ref float contentHeight)
        {
            float ch = contentHeight;
            float _y = y;

            _tracks.Clear();
            _sectionHeaderRects.Clear();
            _playbackRects.Clear();

            string pfx = "ps" + ps.GetHashCode();

            // ── Playback toolbar ──────────────────────────────────────────────
            DrawPlaybackBar(r, cr, ps, ref _y, ref contentHeight, pfx);

            // ── Main module (always visible) ──────────────────────────────────
            DrawSection(r, cr, "Main", pfx, ref _y, ref contentHeight, () =>
            {
                FloatRow(r, cr, "Duration", pfx + "dur", ps.Duration, 0.1f, 60f, ref _y, ref ch, v => ps.Duration = v);
                BoolRow(r, cr, "Looping", pfx + "loop", ps.Looping, ref _y, ref ch, v => ps.Looping = v);
                BoolRow(r, cr, "Prewarm", pfx + "prw", ps.Prewarm, ref _y, ref ch, v => ps.Prewarm = v);
                FloatRow(r, cr, "Start Delay", pfx + "sdly", ps.StartDelay, 0f, 10f, ref _y, ref ch, v => ps.StartDelay = v);
                RangeRow(r, cr, "Start Lifetime", pfx + "slt", ps.StartLifetime, ps.StartLifetimeMax, ps.StartLifetimeRange,
                    0.01f, 100f, ref _y, ref ch,
                    (lo, hi, use) => { ps.StartLifetime = lo; ps.StartLifetimeMax = hi; ps.StartLifetimeRange = use; });
                RangeRow(r, cr, "Start Speed", pfx + "ssp", ps.StartSpeed, ps.StartSpeedMax, ps.StartSpeedRange,
                    0f, 50f, ref _y, ref ch,
                    (lo, hi, use) => { ps.StartSpeed = lo; ps.StartSpeedMax = hi; ps.StartSpeedRange = use; });
                RangeRow(r, cr, "Start Size", pfx + "ssz", ps.StartSize, ps.StartSizeMax, ps.StartSizeRange,
                    0f, 10f, ref _y, ref ch,
                    (lo, hi, use) => { ps.StartSize = lo; ps.StartSizeMax = hi; ps.StartSizeRange = use; });
                RangeRow(r, cr, "Start Rotation", pfx + "srt", ps.StartRotation, ps.StartRotationMax, ps.StartRotationRange,
                    -360f, 360f, ref _y, ref ch,
                    (lo, hi, use) => { ps.StartRotation = lo; ps.StartRotationMax = hi; ps.StartRotationRange = use; });
                ColorRow(r, cr, "Start Color", pfx + "sco",
                    ps.StartColorR, ps.StartColorG, ps.StartColorB, ps.StartColorA,
                    ref _y, ref ch,
                    (rv, gv, bv, av) => { ps.StartColorR = rv; ps.StartColorG = gv; ps.StartColorB = bv; ps.StartColorA = av; });
                FloatRow(r, cr, "Gravity Modifier", pfx + "grv", ps.GravityModifier, -5f, 5f, ref _y, ref ch, v => ps.GravityModifier = v);
                EnumRow(r, cr, "Simulation Space", pfx + "sims", ps.SimulationSpace.ToString(),
                    new[] { "Local", "World" }, ref _y, ref ch, v => ps.SimulationSpace = Enum.Parse<ParticleSimulationSpace>(v));
                BoolRow(r, cr, "Play On Awake", pfx + "poa", ps.PlayOnAwake, ref _y, ref ch, v => ps.PlayOnAwake = v);
                IntRow(r, cr, "Max Particles", pfx + "maxp", ps.MaxParticles, 1, 100000, ref _y, ref ch, v => ps.MaxParticles = v);
            });

            // ── Emission ──────────────────────────────────────────────────────
            DrawModuleSection(r, cr, "Emission", pfx, ref _y, ref contentHeight, ps.EmissionEnabled,
                v => ps.EmissionEnabled = v, () =>
                {
                    FloatRow(r, cr, "Rate Over Time", pfx + "rot", ps.RateOverTime, 0f, 1000f, ref _y, ref ch, v => ps.RateOverTime = v);
                    FloatRow(r, cr, "Rate Over Distance", pfx + "rod", ps.RateOverDistance, 0f, 100f, ref _y, ref ch, v => ps.RateOverDistance = v);
                    // Bursts summary
                    LabelRow(r, cr, $"Bursts: {ps.Bursts.Count}", ref _y, ref ch);
                });

            // ── Shape ─────────────────────────────────────────────────────────
            DrawModuleSection(r, cr, "Shape", pfx, ref _y, ref contentHeight, ps.ShapeEnabled,
                v => ps.ShapeEnabled = v, () =>
                {
                    EnumRow(r, cr, "Shape", pfx + "shp", ps.Shape.ToString(),
                        new[] { "Sphere", "Hemisphere", "Cone", "Box", "Circle", "Edge", "Point" },
                        ref _y, ref ch, v => ps.Shape = Enum.Parse<ParticleShape>(v));

                    FloatRow(r, cr, "Radius", pfx + "shpR", ps.ShapeRadius, 0.01f, 20f, ref _y, ref ch, v => ps.ShapeRadius = v);

                    if (ps.Shape == ParticleShape.Cone)
                    {
                        FloatRow(r, cr, "Angle", pfx + "shpA", ps.ShapeAngle, 0f, 90f, ref _y, ref ch, v => ps.ShapeAngle = v);
                        FloatRow(r, cr, "Arc", pfx + "shpAr", ps.ShapeArc, 1f, 360f, ref _y, ref ch, v => ps.ShapeArc = v);
                    }
                    if (ps.Shape == ParticleShape.Box)
                    {
                        FloatRow(r, cr, "Box X", pfx + "bx", ps.ShapeBoxX, 0.01f, 20f, ref _y, ref ch, v => ps.ShapeBoxX = v);
                        FloatRow(r, cr, "Box Y", pfx + "by", ps.ShapeBoxY, 0.01f, 20f, ref _y, ref ch, v => ps.ShapeBoxY = v);
                        FloatRow(r, cr, "Box Z", pfx + "bz", ps.ShapeBoxZ, 0.01f, 20f, ref _y, ref ch, v => ps.ShapeBoxZ = v);
                    }
                    FloatRow(r, cr, "Random Direction", pfx + "shpRd", ps.RandomDirectionAmount, 0f, 1f, ref _y, ref ch, v => ps.RandomDirectionAmount = v);
                    FloatRow(r, cr, "Random Position", pfx + "shpRp", ps.RandomPositionAmount, 0f, 1f, ref _y, ref ch, v => ps.RandomPositionAmount = v);
                    BoolRow(r, cr, "Align to Direction", pfx + "shpAl", ps.ShapeAlignToDirection, ref _y, ref ch, v => ps.ShapeAlignToDirection = v);
                });

            // ── Velocity over Lifetime ─────────────────────────────────────────
            DrawModuleSection(r, cr, "Velocity over Lifetime", pfx, ref _y, ref contentHeight, ps.VelocityEnabled,
                v => ps.VelocityEnabled = v, () =>
                {
                    FloatRow(r, cr, "X", pfx + "vlx", ps.VelocityX, -20f, 20f, ref _y, ref ch, v => ps.VelocityX = v);
                    FloatRow(r, cr, "Y", pfx + "vly", ps.VelocityY, -20f, 20f, ref _y, ref ch, v => ps.VelocityY = v);
                    FloatRow(r, cr, "Z", pfx + "vlz", ps.VelocityZ, -20f, 20f, ref _y, ref ch, v => ps.VelocityZ = v);
                    EnumRow(r, cr, "Space", pfx + "vls", ps.VelocitySpace.ToString(),
                        new[] { "Local", "World" }, ref _y, ref ch, v => ps.VelocitySpace = Enum.Parse<ParticleSimulationSpace>(v));
                });

            // ── Color over Lifetime ────────────────────────────────────────────
            DrawModuleSection(r, cr, "Color over Lifetime", pfx, ref _y, ref contentHeight, ps.ColorEnabled,
                v => ps.ColorEnabled = v, () =>
                {
                    // Gradient preview (simplified as a solid bar with start/end colors from keys)
                    DrawGradientPreview(r, cr, ps.ColorGradient, ref _y, ref ch);
                });

            // ── Size over Lifetime ─────────────────────────────────────────────
            DrawModuleSection(r, cr, "Size over Lifetime", pfx, ref _y, ref contentHeight, ps.SizeEnabled,
                v => ps.SizeEnabled = v, () =>
                {
                    // Curve preview (constant for now)
                    FloatRow(r, cr, "Size Multiplier", pfx + "szc", ps.SizeCurve.Constant, 0f, 5f, ref _y, ref ch,
                        v => ps.SizeCurve.Constant = v);
                });

            // ── Rotation over Lifetime ─────────────────────────────────────────
            DrawModuleSection(r, cr, "Rotation over Lifetime", pfx, ref _y, ref contentHeight, ps.RotationEnabled,
                v => ps.RotationEnabled = v, () =>
                {
                    RangeRow(r, cr, "Angular Velocity", pfx + "rols", ps.RotationSpeed, ps.RotationSpeedMax, ps.RotationSpeedRange,
                        -720f, 720f, ref _y, ref ch,
                        (lo, hi, use) => { ps.RotationSpeed = lo; ps.RotationSpeedMax = hi; ps.RotationSpeedRange = use; });
                });

            // ── Noise ──────────────────────────────────────────────────────────
            DrawModuleSection(r, cr, "Noise", pfx, ref _y, ref contentHeight, ps.NoiseEnabled,
                v => ps.NoiseEnabled = v, () =>
                {
                    FloatRow(r, cr, "Strength", pfx + "nstr", ps.NoiseStrength, 0f, 5f, ref _y, ref ch, v => ps.NoiseStrength = v);
                    FloatRow(r, cr, "Frequency", pfx + "nfrq", ps.NoiseFrequency, 0f, 10f, ref _y, ref ch, v => ps.NoiseFrequency = v);
                    IntRow(r, cr, "Octaves", pfx + "noct", ps.NoiseOctaves, 1, 8, ref _y, ref ch, v => ps.NoiseOctaves = v);
                    FloatRow(r, cr, "Scroll Speed", pfx + "nscr", ps.NoiseScrollSpeed, 0f, 5f, ref _y, ref ch, v => ps.NoiseScrollSpeed = v);
                    BoolRow(r, cr, "Damping", pfx + "ndmp", ps.NoiseDamping, ref _y, ref ch, v => ps.NoiseDamping = v);
                });

            // ── Collision ──────────────────────────────────────────────────────
            DrawModuleSection(r, cr, "Collision", pfx, ref _y, ref contentHeight, ps.CollisionEnabled,
                v => ps.CollisionEnabled = v, () =>
                {
                    FloatRow(r, cr, "Dampen", pfx + "cdam", ps.CollisionDampen, 0f, 1f, ref _y, ref ch, v => ps.CollisionDampen = v);
                    FloatRow(r, cr, "Bounce", pfx + "cbou", ps.CollisionBounce, 0f, 1f, ref _y, ref ch, v => ps.CollisionBounce = v);
                    FloatRow(r, cr, "Lifetime Loss", pfx + "cll", ps.CollisionLifetimeLoss, 0f, 1f, ref _y, ref ch, v => ps.CollisionLifetimeLoss = v);
                    FloatRow(r, cr, "Radius Scale", pfx + "crs", ps.CollisionRadius, 0f, 1f, ref _y, ref ch, v => ps.CollisionRadius = v);
                    BoolRow(r, cr, "Send Messages", pfx + "csm", ps.CollisionSendMsg, ref _y, ref ch, v => ps.CollisionSendMsg = v);
                });

            // ── Trails ────────────────────────────────────────────────────────
            DrawModuleSection(r, cr, "Trails", pfx, ref _y, ref contentHeight, ps.TrailsEnabled,
                v => ps.TrailsEnabled = v, () =>
                {
                    FloatRow(r, cr, "Ratio", pfx + "trat", ps.TrailsRatio, 0f, 1f, ref _y, ref ch, v => ps.TrailsRatio = v);
                    FloatRow(r, cr, "Lifetime", pfx + "tlt", ps.TrailsLifetime, 0f, 5f, ref _y, ref ch, v => ps.TrailsLifetime = v);
                    FloatRow(r, cr, "Min Vertex Dist", pfx + "tmvd", ps.TrailsMinVertexDistance, 0.01f, 2f, ref _y, ref ch, v => ps.TrailsMinVertexDistance = v);
                    FloatRow(r, cr, "Width", pfx + "twid", ps.TrailsWidth, 0f, 2f, ref _y, ref ch, v => ps.TrailsWidth = v);
                    BoolRow(r, cr, "World Space", pfx + "tws", ps.TrailsWorldSpace, ref _y, ref ch, v => ps.TrailsWorldSpace = v);
                    BoolRow(r, cr, "Die With Particle", pfx + "tdwp", ps.TrailsDieWithParticle, ref _y, ref ch, v => ps.TrailsDieWithParticle = v);
                    BoolRow(r, cr, "Inherit Color", pfx + "tic", ps.TrailsColorInherit, ref _y, ref ch, v => ps.TrailsColorInherit = v);
                });

            // ── Renderer ──────────────────────────────────────────────────────
            DrawModuleSection(r, cr, "Renderer", pfx, ref _y, ref contentHeight, ps.RendererEnabled,
                v => ps.RendererEnabled = v, () =>
                {
                    EnumRow(r, cr, "Render Mode", pfx + "rmd", ps.RenderMode.ToString(),
                        new[] { "Billboard", "StretchedBillboard", "HorizontalBillboard", "VerticalBillboard", "Mesh" },
                        ref _y, ref ch, v => ps.RenderMode = Enum.Parse<ParticleRenderMode>(v));
                    EnumRow(r, cr, "Sort Mode", pfx + "rsrt", ps.SortMode.ToString(),
                        new[] { "None", "ByDistance", "OldestInFront", "YoungestInFront" },
                        ref _y, ref ch, v => ps.SortMode = Enum.Parse<ParticleSortMode>(v));
                    FloatRow(r, cr, "Min Particle Size", pfx + "rmn", ps.MinParticleSize, 0f, 1f, ref _y, ref ch, v => ps.MinParticleSize = v);
                    FloatRow(r, cr, "Max Particle Size", pfx + "rmx", ps.MaxParticleSize, 0f, 1f, ref _y, ref ch, v => ps.MaxParticleSize = v);
                    BoolRow(r, cr, "Receive Shadows", pfx + "rrs", ps.ReceiveShadows, ref _y, ref ch, v => ps.ReceiveShadows = v);
                    BoolRow(r, cr, "Cast Shadows", pfx + "rcs", ps.CastShadows, ref _y, ref ch, v => ps.CastShadows = v);
                    if (ps.RenderMode == ParticleRenderMode.StretchedBillboard)
                    {
                        FloatRow(r, cr, "Stretch Length", pfx + "rsl", ps.StretchLength, 0f, 10f, ref _y, ref ch, v => ps.StretchLength = v);
                        FloatRow(r, cr, "Speed Scale", pfx + "rss", ps.StretchSpeedScale, 0f, 1f, ref _y, ref ch, v => ps.StretchSpeedScale = v);
                    }
                });

            // ── Particle count status bar ──────────────────────────────────────
            var statusR = new RectangleF(cr.X, y, cr.Width, 20f);
            r.FillRect(statusR, Color.FromArgb(255, 24, 26, 34));
            r.DrawText($"  Particles: {ps.Particles.Count} / {ps.MaxParticles}   Time: {ps.PlaybackTime:F1}s",
                new PointF(cr.X + 10f, y + 4f), CTextDim, 9f);
            y += 22f; contentHeight += 22f;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Playback bar
        // ══════════════════════════════════════════════════════════════════════
        private void DrawPlaybackBar(IEditorRenderer r, RectangleF cr, ParticleSystem ps,
                                     ref float y, ref float ch, string pfx)
        {
            float barH = 30f;
            var bar = new RectangleF(cr.X, y, cr.Width, barH);
            r.FillRect(bar, Color.FromArgb(255, 26, 28, 38));
            r.DrawLine(new PointF(bar.X, bar.Bottom), new PointF(bar.Right, bar.Bottom), CBorder);

            float btnW = 64f, gap = 4f;
            float bx = cr.X + 8f;
            float by = y + 5f;

            var playBtn = new RectangleF(bx, by, btnW, 20f); bx += btnW + gap;
            var pauseBtn = new RectangleF(bx, by, btnW, 20f); bx += btnW + gap;
            var stopBtn = new RectangleF(bx, by, btnW, 20f); bx += btnW + gap;
            var restartBtn = new RectangleF(bx, by, btnW, 20f);

            DrawPlayBtn(r, playBtn, ps.IsPlaying ? "■ Stop" : "▶ Play", ps.IsPlaying ? CStopRed : CPlayGrn);
            DrawPlayBtn(r, pauseBtn, ps.IsPaused ? "▶ Resume" : "⏸ Pause", CPauseYel);
            DrawPlayBtn(r, stopBtn, "⏹ Reset", Color.FromArgb(255, 55, 60, 75));
            DrawPlayBtn(r, restartBtn, "↺ Restart", Color.FromArgb(255, 55, 60, 75));

            _playbackRects["play"] = playBtn;
            _playbackRects["pause"] = pauseBtn;
            _playbackRects["stop"] = stopBtn;
            _playbackRects["restart"] = restartBtn;

            y += barH + 2f; ch += barH + 2f;
        }

        private void DrawPlayBtn(IEditorRenderer r, RectangleF btn, string label, Color col)
        {
            bool hov = btn.Contains(_mouse);
            r.FillRect(btn, hov ? Color.FromArgb(col.A, Math.Min(col.R + 30, 255), Math.Min(col.G + 30, 255), Math.Min(col.B + 30, 255)) : col);
            r.DrawRect(btn, CBorder);
            r.DrawText(label, new PointF(btn.X + 4f, btn.Y + 5f), Color.White, 8f);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Section header with collapse arrow
        // ══════════════════════════════════════════════════════════════════════
        private void DrawSection(IEditorRenderer r, RectangleF cr, string name, string pfx,
                                 ref float y, ref float ch, Action drawBody)
        {
            string key = pfx + "_" + name;
            bool open = IsOpen(key, true);

            var hdr = new RectangleF(cr.X, y, cr.Width, HeadH);
            r.FillRect(hdr, open ? CHeadSel : CHead);
            r.DrawLine(new PointF(hdr.X, hdr.Y), new PointF(hdr.Right, hdr.Y), CBorder);
            r.DrawText(open ? "▼" : "▶", new PointF(hdr.X + 6f, hdr.Y + 5f), CAccent, 9f);
            r.DrawText(name, new PointF(hdr.X + 22f, hdr.Y + 5f), CText, 10f);
            _sectionHeaderRects[key] = hdr;

            y += HeadH; ch += HeadH;

            if (open) drawBody();
        }

        // ── Module section (has enable checkbox on header) ─────────────────────
        private void DrawModuleSection(IEditorRenderer r, RectangleF cr, string name, string pfx,
                                       ref float y, ref float ch, bool enabled, Action<bool> setEnabled,
                                       Action drawBody)
        {
            string key = pfx + "_" + name;
            bool open = IsOpen(key, false);

            var hdr = new RectangleF(cr.X, y, cr.Width, HeadH);
            r.FillRect(hdr, open ? CHeadSel : CHead);
            r.DrawLine(new PointF(hdr.X, hdr.Y), new PointF(hdr.Right, hdr.Y), CBorder);

            // Collapse arrow
            r.DrawText(open ? "▼" : "▶", new PointF(hdr.X + 6f, hdr.Y + 5f), CAccent, 9f);
            r.DrawText(name, new PointF(hdr.X + 22f, hdr.Y + 5f), open ? CText : CTextDim, 10f);

            // Module enable toggle (top-right of header)
            var tog = new RectangleF(hdr.Right - 38f, hdr.Y + 3f, 32f, 16f);
            r.FillRect(tog, enabled ? CTogOn : CTogOff);
            r.DrawRect(tog, CBorder);
            r.FillRect(new RectangleF(enabled ? tog.Right - 14f : tog.X + 2f, tog.Y + 2f, 12f, 12f), Color.White);

            // Store toggle rect under a dedicated key so OnMouseDown can hit it
            _sectionHeaderRects[key + "_tog"] = tog;
            // Store the setEnabled callback keyed by this id (simple trick: store in dict)
            _moduleEnableCallbacks[key + "_tog"] = setEnabled;

            _sectionHeaderRects[key] = hdr;
            y += HeadH; ch += HeadH;

            if (open && enabled) drawBody();
        }

        private readonly Dictionary<string, Action<bool>> _moduleEnableCallbacks = new();

        public bool TryHandleModuleToggle(PointF pos)
        {
            foreach (var kv in _moduleEnableCallbacks)
            {
                if (_sectionHeaderRects.TryGetValue(kv.Key, out var rect) && rect.Contains(pos))
                {
                    // Read the current toggle state from the key in _sectionHeaderRects
                    // We can infer it by checking if open — but we stored the callback.
                    // For enable toggle we just call with negation. The callback knows the current value.
                    // We'll just toggle by calling with true, caller handles negation via lambda.
                    // Better: store current value per key.
                    if (_moduleCurrentEnabled.TryGetValue(kv.Key, out bool cur))
                        kv.Value(!cur);
                    return true;
                }
            }
            return false;
        }

        // Store current enabled value per key for toggle
        private readonly Dictionary<string, bool> _moduleCurrentEnabled = new();

        // ══════════════════════════════════════════════════════════════════════
        //  Row renderers
        // ══════════════════════════════════════════════════════════════════════
        private int _rowIdx = 0;

        private RectangleF RowBg(IEditorRenderer r, RectangleF cr, float y)
        {
            var rr = new RectangleF(cr.X, y, cr.Width, RowH);
            r.FillRect(rr, _rowIdx++ % 2 == 0 ? CBg : CBgAlt);
            return rr;
        }

        private void FloatRow(IEditorRenderer r, RectangleF cr, string label, string id,
                              float value, float min, float max,
                              ref float y, ref float ch, Action<float> set)
        {
            var rr = RowBg(r, cr, y);
            DrawLabel(r, rr, label, cr);

            float valBoxW = 42f;
            float valBoxX = rr.Right - valBoxW - 4f;
            float trackX = rr.X + rr.Width * LabelW + 4f;
            float trackW = Math.Max(valBoxX - trackX - 4f, 20f);
            var track = new RectangleF(trackX, y + 7f, trackW, 8f);

            float t = Math.Clamp((value - min) / (max - min), 0f, 1f);
            bool drag = _dragId == id;

            r.FillRect(track, Color.FromArgb(255, 20, 22, 30));
            r.DrawRect(track, drag ? CAccent : CBorder);
            r.FillRect(new RectangleF(track.X, track.Y, track.Width * t, track.Height), CAccent);
            r.FillRect(new RectangleF(track.X + track.Width * t - 4f, track.Y - 2f, 8f, 12f), Color.White);

            var vb = new RectangleF(valBoxX, y + 2f, valBoxW, 18f);
            r.FillRect(vb, Color.FromArgb(255, 20, 22, 30));
            r.DrawRect(vb, CBorder);
            r.DrawText(value.ToString("F2"), new PointF(vb.X + 3f, vb.Y + 3f), CText, 8f);

            _tracks[id] = (track, min, max, false);
            // Apply live drag
            if (drag) set(value);

            y += RowH; ch += RowH;
        }

        private void IntRow(IEditorRenderer r, RectangleF cr, string label, string id,
                            int value, int min, int max,
                            ref float y, ref float ch, Action<int> set)
        {
            var rr = RowBg(r, cr, y);
            DrawLabel(r, rr, label, cr);

            float valBoxW = 42f;
            float valBoxX = rr.Right - valBoxW - 4f;
            float trackX = rr.X + rr.Width * LabelW + 4f;
            float trackW = Math.Max(valBoxX - trackX - 4f, 20f);
            var track = new RectangleF(trackX, y + 7f, trackW, 8f);

            float t = Math.Clamp((float)(value - min) / (max - min), 0f, 1f);
            r.FillRect(track, Color.FromArgb(255, 20, 22, 30));
            r.DrawRect(track, _dragId == id ? CAccent : CBorder);
            r.FillRect(new RectangleF(track.X, track.Y, track.Width * t, track.Height), CAccent);
            r.FillRect(new RectangleF(track.X + track.Width * t - 4f, track.Y - 2f, 8f, 12f), Color.White);

            var vb = new RectangleF(valBoxX, y + 2f, valBoxW, 18f);
            r.FillRect(vb, Color.FromArgb(255, 20, 22, 30));
            r.DrawRect(vb, CBorder);
            r.DrawText(value.ToString(), new PointF(vb.X + 3f, vb.Y + 3f), CText, 8f);

            _tracks[id] = (track, min, max, true);

            y += RowH; ch += RowH;
        }

        private void BoolRow(IEditorRenderer r, RectangleF cr, string label, string id,
                             bool value, ref float y, ref float ch, Action<bool> set)
        {
            var rr = RowBg(r, cr, y);
            DrawLabel(r, rr, label, cr);

            var tog = new RectangleF(rr.Right - 40f, y + 3f, 32f, 16f);
            r.FillRect(tog, value ? CTogOn : CTogOff);
            r.DrawRect(tog, CBorder);
            r.FillRect(new RectangleF(value ? tog.Right - 14f : tog.X + 2f, tog.Y + 2f, 12f, 12f), Color.White);

            // Store for click detection
            _boolRects[id] = tog;
            _boolCallbacks[id] = () => set(!value);

            y += RowH; ch += RowH;
        }

        private readonly Dictionary<string, RectangleF> _boolRects = new();
        private readonly Dictionary<string, Action> _boolCallbacks = new();

        private void RangeRow(IEditorRenderer r, RectangleF cr, string label, string id,
                              float lo, float hi, bool useRange, float min, float max,
                              ref float y, ref float ch, Action<float, float, bool> set)
        {
            var rr = RowBg(r, cr, y);
            DrawLabel(r, rr, label, cr);

            // Range toggle button
            float rangeTogW = 18f;
            var rangeTog = new RectangleF(rr.Right - rangeTogW - 2f, y + 3f, rangeTogW, 16f);
            r.FillRect(rangeTog, useRange ? CAccent : Color.FromArgb(255, 50, 55, 70));
            r.DrawRect(rangeTog, CBorder);
            r.DrawText("↕", new PointF(rangeTog.X + 4f, rangeTog.Y + 3f), Color.White, 8f);

            if (useRange)
            {
                // Two sliders stacked
                float hw = (rr.Width * (1f - LabelW) - rangeTogW - 50f) / 2f - 4f;
                float tx1 = rr.X + rr.Width * LabelW + 4f;
                var t1 = new RectangleF(tx1, y + 7f, hw, 8f);
                var t2 = new RectangleF(tx1 + hw + 4f, y + 7f, hw, 8f);

                DrawMiniTrack(r, t1, lo, min, max, id + "_lo");
                DrawMiniTrack(r, t2, hi, min, max, id + "_hi");

                // Value labels
                r.DrawText(lo.ToString("F1"), new PointF(t1.X - 1f, y + 14f), CTextDim, 7f);
                r.DrawText(hi.ToString("F1"), new PointF(t2.X - 1f, y + 14f), CTextDim, 7f);
            }
            else
            {
                float valBoxW = 42f;
                float valBoxX = rangeTog.X - valBoxW - 6f;
                float trackX = rr.X + rr.Width * LabelW + 4f;
                float trackW = Math.Max(valBoxX - trackX - 4f, 20f);
                var track = new RectangleF(trackX, y + 7f, trackW, 8f);
                DrawMiniTrack(r, track, lo, min, max, id);

                var vb = new RectangleF(valBoxX, y + 2f, valBoxW, 18f);
                r.FillRect(vb, Color.FromArgb(255, 20, 22, 30));
                r.DrawRect(vb, CBorder);
                r.DrawText(lo.ToString("F2"), new PointF(vb.X + 3f, vb.Y + 3f), CText, 8f);
            }

            // Store range toggle rect
            _rangeTogRects[id] = rangeTog;
            _rangeTogCallbacks[id] = () => set(lo, hi, !useRange);

            y += RowH; ch += RowH;
        }

        private readonly Dictionary<string, RectangleF> _rangeTogRects = new();
        private readonly Dictionary<string, Action> _rangeTogCallbacks = new();

        private void DrawMiniTrack(IEditorRenderer r, RectangleF track, float val, float min, float max, string id)
        {
            bool drag = _dragId == id;
            float t = Math.Clamp((val - min) / (max - min), 0f, 1f);
            r.FillRect(track, Color.FromArgb(255, 20, 22, 30));
            r.DrawRect(track, drag ? CAccent : CBorder);
            r.FillRect(new RectangleF(track.X, track.Y, track.Width * t, track.Height), CAccent);
            r.FillRect(new RectangleF(track.X + track.Width * t - 3f, track.Y - 2f, 6f, 12f), Color.White);
            _tracks[id] = (track, min, max, false);
        }

        private void EnumRow(IEditorRenderer r, RectangleF cr, string label, string id,
                             string value, string[] opts, ref float y, ref float ch, Action<string> set)
        {
            var rr = RowBg(r, cr, y);
            DrawLabel(r, rr, label, cr);

            float ddW = Math.Min(140f, rr.Width * 0.50f);
            var dd = new RectangleF(rr.Right - ddW - 4f, y + 2f, ddW, 18f);
            bool open = _openEnumId == id;
            r.FillRect(dd, open ? Color.FromArgb(255, 50, 80, 130) : Color.FromArgb(255, 36, 40, 54));
            r.DrawRect(dd, open ? CAccent : CBorder);
            string disp = value.Length > 16 ? value[..13] + "…" : value;
            r.DrawText(disp, new PointF(dd.X + 4f, dd.Y + 3f), CText, 8f);
            r.DrawText("▾", new PointF(dd.Right - 12f, dd.Y + 3f), CTextDim, 8f);

            _enumRects[id] = dd;
            _enumOpts[id] = opts;
            _enumCallbacks[id] = set;

            if (open) DrawEnumPopup(r, cr, id, dd, opts, value);

            y += RowH; ch += RowH;
        }

        private string? _openEnumId;
        private readonly Dictionary<string, RectangleF> _enumRects = new();
        private readonly Dictionary<string, string[]> _enumOpts = new();
        private readonly Dictionary<string, Action<string>> _enumCallbacks = new();

        private void DrawEnumPopup(IEditorRenderer r, RectangleF cr, string id, RectangleF origin,
                                   string[] opts, string current)
        {
            float itemH = 18f;
            float ph = opts.Length * itemH + 4f;
            float px = origin.X;
            float py = origin.Bottom + 1f;
            if (py + ph > cr.Y + cr.Height) py = origin.Y - ph - 1f;
            var pop = new RectangleF(px, py, Math.Max(origin.Width, 130f), ph);
            r.FillRect(pop, Color.FromArgb(255, 28, 32, 44));
            r.DrawRect(pop, CAccent, 1.5f);
            for (int i = 0; i < opts.Length; i++)
            {
                var ir = new RectangleF(pop.X, pop.Y + 2f + i * itemH, pop.Width, itemH);
                bool hov = ir.Contains(_mouse);
                bool sel = opts[i] == current;
                if (hov || sel) r.FillRect(ir, sel
                    ? Color.FromArgb(255, 50, 100, 200) : Color.FromArgb(255, 42, 48, 64));
                r.DrawText(opts[i], new PointF(ir.X + 6f, ir.Y + 3f), hov || sel ? CText : CTextDim, 8f);
            }
        }

        private void ColorRow(IEditorRenderer r, RectangleF cr, string label, string id,
                              float rv, float gv, float bv, float av,
                              ref float y, ref float ch,
                              Action<float, float, float, float> set)
        {
            var rr = RowBg(r, cr, y);
            DrawLabel(r, rr, label, cr);

            var sw = new RectangleF(rr.Right - 60f, y + 3f, 56f, 16f);
            r.FillRect(sw, Color.FromArgb(
                (int)(av * 255), (int)(rv * 255), (int)(gv * 255), (int)(bv * 255)));
            r.DrawRect(sw, CBorder);
            r.DrawText($"A:{(int)(av * 255)}", new PointF(sw.X - 32f, y + 5f), CTextDim, 7f);

            y += RowH; ch += RowH;
        }

        private void DrawGradientPreview(IEditorRenderer r, RectangleF cr, MinMaxGradient grad,
                                         ref float y, ref float ch)
        {
            var rr = RowBg(r, cr, y);
            r.DrawText("Gradient", new PointF(rr.X + 10f, y + 5f), CTextDim, 9f);

            var bar = new RectangleF(rr.X + rr.Width * (float)LabelW, y + 3f,
                rr.Width * (1f - (float)LabelW) - 8f, 16f);
            // Draw gradient segments
            int segs = 16;
            float sw = bar.Width / segs;
            for (int i = 0; i < segs; i++)
            {
                float t = (i + 0.5f) / segs;
                var (rv, gv, bv, av) = grad.Evaluate(t);
                r.FillRect(new RectangleF(bar.X + i * sw, bar.Y, sw + 1f, bar.Height),
                    Color.FromArgb((int)(av * 255), (int)(rv * 255), (int)(gv * 255), (int)(bv * 255)));
            }
            r.DrawRect(bar, CBorder);

            y += RowH; ch += RowH;
        }

        private void LabelRow(IEditorRenderer r, RectangleF cr, string text, ref float y, ref float ch)
        {
            var rr = RowBg(r, cr, y);
            r.DrawText(text, new PointF(rr.X + 10f, y + 5f), CTextDim, 9f);
            y += RowH; ch += RowH;
        }

        private void DrawLabel(IEditorRenderer r, RectangleF rr, string label, RectangleF cr)
        {
            // Clip label to its column
            float maxW = cr.Width * LabelW - 8f;
            r.DrawText(label, new PointF(rr.X + 10f, rr.Y + 5f), CText, 9f);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Handle clicks — call from InspectorPanel.OnMouseDown
        // ══════════════════════════════════════════════════════════════════════
        public bool HandleClick(PointF pos, ParticleSystem ps)
        {
            // Enum dropdowns — open or select option
            if (_openEnumId != null)
            {
                if (_enumOpts.TryGetValue(_openEnumId, out var opts) &&
                    _enumRects.TryGetValue(_openEnumId, out var origin))
                {
                    float itemH = 18f;
                    float ph = opts.Length * itemH + 4f;
                    float py = origin.Bottom + 1f;
                    var pop = new RectangleF(origin.X, py, Math.Max(origin.Width, 130f), ph);
                    if (pop.Contains(pos))
                    {
                        int i = (int)((pos.Y - pop.Y - 2f) / itemH);
                        if (i >= 0 && i < opts.Length)
                        {
                            if (_enumCallbacks.TryGetValue(_openEnumId, out var cb))
                                cb(opts[i]);
                        }
                        _openEnumId = null;
                        return true;
                    }
                }
                _openEnumId = null;
            }

            // Playback buttons
            if (TryHandlePlaybackClick(pos, ps)) return true;

            // Section headers
            foreach (var kv in _sectionHeaderRects)
            {
                if (!kv.Key.EndsWith("_tog") && kv.Value.Contains(pos))
                {
                    _open[kv.Key] = !IsOpen(kv.Key);
                    return true;
                }
            }

            // Module enable toggles
            if (TryHandleModuleToggle(pos)) return true;

            // Bool toggles
            foreach (var kv in _boolRects)
            {
                if (kv.Value.Contains(pos) && _boolCallbacks.TryGetValue(kv.Key, out var cb))
                { cb(); return true; }
            }

            // Range toggles
            foreach (var kv in _rangeTogRects)
            {
                if (kv.Value.Contains(pos) && _rangeTogCallbacks.TryGetValue(kv.Key, out var cb))
                { cb(); return true; }
            }

            // Enum dropdowns — open
            foreach (var kv in _enumRects)
            {
                if (kv.Value.Contains(pos)) { _openEnumId = kv.Key; return true; }
            }

            // Slider drag start
            if (TryStartDrag(pos)) return true;

            return false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Apply drag values back to ParticleSystem
        // ══════════════════════════════════════════════════════════════════════
        private void ApplyDragValue(string id, float v, ParticleSystem ps)
        {
            int iv = (int)v;
            // Trim id prefix (first char group before known suffix)
            // We rely on unique id suffixes defined above
            if (id.EndsWith("dur")) ps.Duration = v;
            else if (id.EndsWith("sdly")) ps.StartDelay = v;
            else if (id.EndsWith("slt")) ps.StartLifetime = v;
            else if (id.EndsWith("slt_lo")) ps.StartLifetime = v;
            else if (id.EndsWith("slt_hi")) ps.StartLifetimeMax = v;
            else if (id.EndsWith("ssp")) ps.StartSpeed = v;
            else if (id.EndsWith("ssp_lo")) ps.StartSpeed = v;
            else if (id.EndsWith("ssp_hi")) ps.StartSpeedMax = v;
            else if (id.EndsWith("ssz")) ps.StartSize = v;
            else if (id.EndsWith("ssz_lo")) ps.StartSize = v;
            else if (id.EndsWith("ssz_hi")) ps.StartSizeMax = v;
            else if (id.EndsWith("srt")) ps.StartRotation = v;
            else if (id.EndsWith("srt_lo")) ps.StartRotation = v;
            else if (id.EndsWith("srt_hi")) ps.StartRotationMax = v;
            else if (id.EndsWith("grv")) ps.GravityModifier = v;
            else if (id.EndsWith("maxp")) ps.MaxParticles = iv;
            else if (id.EndsWith("rot")) ps.RateOverTime = v;
            else if (id.EndsWith("rod")) ps.RateOverDistance = v;
            else if (id.EndsWith("shpR")) ps.ShapeRadius = v;
            else if (id.EndsWith("shpA")) ps.ShapeAngle = v;
            else if (id.EndsWith("shpAr")) ps.ShapeArc = v;
            else if (id.EndsWith("bx")) ps.ShapeBoxX = v;
            else if (id.EndsWith("by")) ps.ShapeBoxY = v;
            else if (id.EndsWith("bz")) ps.ShapeBoxZ = v;
            else if (id.EndsWith("shpRd")) ps.RandomDirectionAmount = v;
            else if (id.EndsWith("shpRp")) ps.RandomPositionAmount = v;
            else if (id.EndsWith("vlx")) ps.VelocityX = v;
            else if (id.EndsWith("vly")) ps.VelocityY = v;
            else if (id.EndsWith("vlz")) ps.VelocityZ = v;
            else if (id.EndsWith("szc")) ps.SizeCurve.Constant = v;
            else if (id.EndsWith("rols") || id.EndsWith("rols_lo")) ps.RotationSpeed = v;
            else if (id.EndsWith("rols_hi")) ps.RotationSpeedMax = v;
            else if (id.EndsWith("nstr")) ps.NoiseStrength = v;
            else if (id.EndsWith("nfrq")) ps.NoiseFrequency = v;
            else if (id.EndsWith("noct")) ps.NoiseOctaves = iv;
            else if (id.EndsWith("nscr")) ps.NoiseScrollSpeed = v;
            else if (id.EndsWith("cdam")) ps.CollisionDampen = v;
            else if (id.EndsWith("cbou")) ps.CollisionBounce = v;
            else if (id.EndsWith("cll")) ps.CollisionLifetimeLoss = v;
            else if (id.EndsWith("crs")) ps.CollisionRadius = v;
            else if (id.EndsWith("trat")) ps.TrailsRatio = v;
            else if (id.EndsWith("tlt")) ps.TrailsLifetime = v;
            else if (id.EndsWith("tmvd")) ps.TrailsMinVertexDistance = v;
            else if (id.EndsWith("twid")) ps.TrailsWidth = v;
            else if (id.EndsWith("rsl")) ps.StretchLength = v;
            else if (id.EndsWith("rss")) ps.StretchSpeedScale = v;
            else if (id.EndsWith("rmn")) ps.MinParticleSize = v;
            else if (id.EndsWith("rmx")) ps.MaxParticleSize = v;
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private bool IsOpen(string key, bool defaultOpen = false)
        {
            if (_open.TryGetValue(key, out bool v)) return v;
            _open[key] = defaultOpen;
            return defaultOpen;
        }
    }
}