using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Core;

namespace ElintriaEngine.UI.Panels
{
    /// <summary>
    /// Edit → Project Settings – per-project game settings.
    /// Fully interactive: toggles, sliders, dropdowns and text fields all write back
    /// to ProjectSettings and auto-save on close.
    ///
    /// Sections (mirroring Unity):
    ///   Player     – identity, icon, splash
    ///   Display    – resolution, fullscreen, vsync
    ///   Graphics   – AA, shadows, post-processing, fog, GI
    ///   Physics    – gravity, broadphase, material defaults
    ///   Audio      – volumes, speaker mode, doppler
    ///   Time       – time-scale, particle delta
    ///   Input      – mouse sensitivity, controller dead zone
    ///   Scripting  – backend, API compat
    /// </summary>
    public class ProjectSettingsWindow : Panel
    {
        // ── Data ──────────────────────────────────────────────────────────────
        private ProjectSettings? _settings;
        private string _projectRoot = "";

        // ── Sections ──────────────────────────────────────────────────────────
        private enum Section { Player, Display, Graphics, Physics, Audio, Time, Input, Scripting }
        private Section _section = Section.Player;

        // ── Layout ────────────────────────────────────────────────────────────
        private const float SideW = 165f;
        private const float TitleH = 32f;
        private const float FootH = 38f;
        private const float RowH = 28f;

        private float _scroll;
        private float _contentH;
        private PointF _mouse;

        // ── Interactive element registry ──────────────────────────────────────
        // Built each frame during draw; consumed by OnMouseDown / drag.
        private enum CtrlType { Toggle, Slider, Dropdown, TextField }
        private record Ctrl(CtrlType Type, string Id, RectangleF Bounds, string? DdKey = null);
        private readonly List<Ctrl> _ctrls = new();

        // ── Dropdown state ────────────────────────────────────────────────────
        private string? _openDd;
        private string[]? _openDdOptions;
        private RectangleF _openDdOrigin;

        // ── Slider drag state ─────────────────────────────────────────────────
        private string? _draggingSlider;
        private RectangleF _dragTrack;

        // ── Text field editing ────────────────────────────────────────────────
        private string? _focusedField;
        private string _fieldText = "";

        // ── Colours ───────────────────────────────────────────────────────────
        private static readonly Color CBg = Color.FromArgb(255, 30, 32, 38);
        private static readonly Color CSide = Color.FromArgb(255, 24, 26, 32);
        private static readonly Color CSideHov = Color.FromArgb(255, 38, 42, 56);
        private static readonly Color CSideSel = Color.FromArgb(255, 50, 100, 200);
        private static readonly Color CHead = Color.FromArgb(255, 22, 24, 30);
        private static readonly Color CRow = Color.FromArgb(255, 36, 38, 46);
        private static readonly Color CRowAlt = Color.FromArgb(255, 32, 34, 42);
        private static readonly Color CAccent = Color.FromArgb(255, 60, 130, 255);
        private static readonly Color CText = Color.FromArgb(255, 210, 215, 225);
        private static readonly Color CTextDim = Color.FromArgb(255, 130, 140, 155);
        private static readonly Color CBorder = Color.FromArgb(255, 48, 52, 64);
        private static readonly Color CYellow = Color.FromArgb(255, 220, 185, 60);
        private static readonly Color CTogOn = Color.FromArgb(255, 45, 140, 70);
        private static readonly Color CTogOff = Color.FromArgb(255, 55, 55, 68);

        private int _rowIdx;

        public ProjectSettingsWindow(RectangleF bounds) : base("Project Settings", bounds)
        { IsVisible = false; }

        public void Load(string projectRoot)
        {
            _projectRoot = projectRoot;
            _settings = ProjectSettings.LoadForProject(projectRoot);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Render
        // ══════════════════════════════════════════════════════════════════════
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible || _settings == null) return;
            _ctrls.Clear();

            // Window frame
            r.FillRect(Bounds, CBg);
            r.DrawRect(Bounds, CBorder, 2f);

            // Title bar
            var tb = new RectangleF(Bounds.X, Bounds.Y, Bounds.Width, TitleH);
            r.FillRect(tb, CHead);
            r.DrawText("Project Settings", new PointF(Bounds.X + 12f, Bounds.Y + 9f), CText, 13f);
            r.DrawText(_settings.ProductName, new PointF(Bounds.X + 155f, Bounds.Y + 10f), CTextDim, 10f);
            var closeBtn = new RectangleF(Bounds.Right - 28f, Bounds.Y + 6f, 22f, 20f);
            bool chov = closeBtn.Contains(_mouse);
            r.FillRect(closeBtn, chov ? Color.FromArgb(255, 180, 50, 50) : Color.FromArgb(255, 70, 35, 35));
            r.DrawText("✕", new PointF(closeBtn.X + 5f, closeBtn.Y + 4f), Color.White, 9f);
            r.DrawLine(new PointF(Bounds.X, Bounds.Y + TitleH), new PointF(Bounds.Right, Bounds.Y + TitleH), CBorder);

            DrawSide(r);

            var content = new RectangleF(Bounds.X + SideW, Bounds.Y + TitleH,
                                          Bounds.Width - SideW, Bounds.Height - TitleH - FootH);
            r.FillRect(content, CBg);
            r.PushClip(content);
            DrawContent(r, content);
            r.PopClip();

            // Footer
            var foot = new RectangleF(Bounds.X, Bounds.Bottom - FootH, Bounds.Width, FootH);
            r.FillRect(foot, CHead);
            r.DrawLine(new PointF(foot.X, foot.Y), new PointF(foot.Right, foot.Y), CBorder);
            r.DrawText("Changes are saved automatically when you close this window.",
                new PointF(foot.X + 12f, foot.Y + 11f), CTextDim, 8f);
            var applyBtn = new RectangleF(foot.Right - 104f, foot.Y + 7f, 92f, 24f);
            bool ahov = applyBtn.Contains(_mouse);
            r.FillRect(applyBtn, ahov ? Color.FromArgb(255, 80, 160, 255) : CAccent);
            r.DrawText("Apply & Close", new PointF(applyBtn.X + 6f, applyBtn.Y + 7f), Color.White, 9f);

            // Dropdown popup drawn last (on top of everything — outside clip)
            if (_openDd != null) DrawDropdownPopup(r, content);
        }

        // ── Side nav ──────────────────────────────────────────────────────────
        private void DrawSide(IEditorRenderer r)
        {
            var sideR = new RectangleF(Bounds.X, Bounds.Y + TitleH, SideW, Bounds.Height - TitleH);
            r.FillRect(sideR, CSide);
            r.DrawLine(new PointF(sideR.Right, sideR.Y), new PointF(sideR.Right, sideR.Bottom), CBorder);

            var items = new[]
            {
                (Section.Player,    "👤  Player"),
                (Section.Display,   "🖥  Display"),
                (Section.Graphics,  "✨  Graphics"),
                (Section.Physics,   "⚙  Physics"),
                (Section.Audio,     "🔊  Audio"),
                (Section.Time,      "⏱  Time"),
                (Section.Input,     "🎮  Input"),
                (Section.Scripting, "📜  Scripting"),
            };

            float y = sideR.Y + 10f;
            foreach (var (sec, lbl) in items)
            {
                bool sel = _section == sec;
                bool hov = !sel && new RectangleF(sideR.X, y, SideW - 1f, 28f).Contains(_mouse);
                r.FillRect(new RectangleF(sideR.X, y, SideW - 1f, 28f),
                    sel ? CSideSel : hov ? CSideHov : Color.Transparent);
                if (sel) r.FillRect(new RectangleF(sideR.X, y, 3f, 28f), CAccent);
                r.DrawText(lbl, new PointF(sideR.X + 14f, y + 7f),
                    sel ? Color.White : CTextDim, 10f);
                y += 30f;
            }
        }

        // ── Content dispatch ──────────────────────────────────────────────────
        private void DrawContent(IEditorRenderer r, RectangleF area)
        {
            if (_settings == null) return;
            _rowIdx = 0;
            float y = area.Y + 10f - _scroll;
            switch (_section)
            {
                case Section.Player: DrawPlayer(r, area, ref y); break;
                case Section.Display: DrawDisplay(r, area, ref y); break;
                case Section.Graphics: DrawGraphics(r, area, ref y); break;
                case Section.Physics: DrawPhysics(r, area, ref y); break;
                case Section.Audio: DrawAudio(r, area, ref y); break;
                case Section.Time: DrawTime(r, area, ref y); break;
                case Section.Input: DrawInput(r, area, ref y); break;
                case Section.Scripting: DrawScripting(r, area, ref y); break;
            }
            _contentH = y - area.Y + _scroll;
        }

        // ── Player ────────────────────────────────────────────────────────────
        private void DrawPlayer(IEditorRenderer r, RectangleF a, ref float y)
        {
            Header(r, a, ref y, "Identity");
            TextField(r, a, ref y, "Product Name", "player.name", _settings!.ProductName);
            TextField(r, a, ref y, "Company Name", "player.company", _settings.CompanyName);
            TextField(r, a, ref y, "Version", "player.ver", _settings.Version);
            TextField(r, a, ref y, "Bundle ID", "player.bundle", _settings.BundleId);
            TextField(r, a, ref y, "Copyright", "player.copy", _settings.Copyright);

            Header(r, a, ref y, "Assets");
            TextField(r, a, ref y, "Icon Path", "player.icon", _settings.IconPath, hint: "relative path to .png");
            TextField(r, a, ref y, "Splash Path", "player.splash", _settings.SplashPath, hint: "relative path to .png");
            TextAreaField(r, a, ref y, "Description", "player.desc", _settings.Description);
        }

        // ── Display ───────────────────────────────────────────────────────────
        private void DrawDisplay(IEditorRenderer r, RectangleF a, ref float y)
        {
            Header(r, a, ref y, "Default Resolution");
            SliderInt(r, a, ref y, "Width", "disp.w", _settings!.DefaultWidth, 320, 7680);
            SliderInt(r, a, ref y, "Height", "disp.h", _settings.DefaultHeight, 240, 4320);
            SliderInt(r, a, ref y, "Min Width", "disp.mw", _settings.MinWidth, 320, 3840);
            SliderInt(r, a, ref y, "Min Height", "disp.mh", _settings.MinHeight, 240, 2160);
            Toggle(r, a, ref y, "Allow Resizing", "disp.resize", _settings.AllowResizing);

            Header(r, a, ref y, "Window Mode");
            Dropdown(r, a, ref y, "Fullscreen", "disp.fs", _settings.Fullscreen.ToString(),
                new[] { "Windowed", "FullscreenWindow", "ExclusiveFullscreen" });
            Dropdown(r, a, ref y, "V-Sync", "disp.vsync", _settings.VSync.ToString(),
                new[] { "Off", "On", "AdaptiveHalf" });
            SliderInt(r, a, ref y, "Target Frame Rate (−1=unlimited)", "disp.fps",
                _settings.TargetFrameRate, -1, 360);
        }

        // ── Graphics ──────────────────────────────────────────────────────────
        private void DrawGraphics(IEditorRenderer r, RectangleF a, ref float y)
        {
            Header(r, a, ref y, "Anti-Aliasing");
            Dropdown(r, a, ref y, "Mode", "gfx.aa", _settings!.AntiAliasing.ToString(),
                new[] { "None", "MSAA2x", "MSAA4x", "MSAA8x", "FXAA", "TAA" });

            Header(r, a, ref y, "Shadows");
            Dropdown(r, a, ref y, "Shadow Quality", "gfx.shadq", _settings.Shadows.ToString(),
                new[] { "Disabled", "Low", "Medium", "High", "VeryHigh" });
            Dropdown(r, a, ref y, "Shadow Resolution", "gfx.shadr", _settings.ShadowResolution.ToString(),
                new[] { "R256", "R512", "R1024", "R2048", "R4096" });
            SliderFloat(r, a, ref y, "Shadow Distance", "gfx.shadd", _settings.ShadowDistance, 1f, 500f);
            Toggle(r, a, ref y, "Soft Shadows", "gfx.softshadow", _settings.SoftShadows);

            Header(r, a, ref y, "Textures");
            Dropdown(r, a, ref y, "Texture Quality", "gfx.texq", _settings.TextureQuality.ToString(),
                new[] { "Full", "Half", "Quarter", "Eighth" });
            Toggle(r, a, ref y, "Anisotropic Filtering", "gfx.aniso", _settings.Anisotropic);
            SliderInt(r, a, ref y, "Anisotropic Level", "gfx.anisolv", _settings.AnisotropicLevel, 1, 16);

            Header(r, a, ref y, "Colour");
            Dropdown(r, a, ref y, "Colour Space", "gfx.cs", _settings.ColorSpace.ToString(),
                new[] { "Linear", "Gamma" });
            Toggle(r, a, ref y, "HDR", "gfx.hdr", _settings.HDR);

            Header(r, a, ref y, "Post-Processing");
            Toggle(r, a, ref y, "SSAO", "gfx.ssao", _settings.SSAO);
            Toggle(r, a, ref y, "Bloom", "gfx.bloom", _settings.Bloom);
            if (_settings.Bloom)
            {
                SliderFloat(r, a, ref y, "  Bloom Threshold", "gfx.bloomt", _settings.BloomThreshold, 0f, 5f);
                SliderFloat(r, a, ref y, "  Bloom Intensity", "gfx.bloomi", _settings.BloomIntensity, 0f, 5f);
            }
            Toggle(r, a, ref y, "Motion Blur", "gfx.mb", _settings.MotionBlur);
            if (_settings.MotionBlur)
                SliderFloat(r, a, ref y, "  Shutter Angle", "gfx.mba", _settings.MotionBlurShutter, 0f, 360f);
            Toggle(r, a, ref y, "Depth of Field", "gfx.dof", _settings.DepthOfField);
            if (_settings.DepthOfField)
            {
                SliderFloat(r, a, ref y, "  Focal Length", "gfx.dofl", _settings.DOFFocalLength, 1f, 300f);
                SliderFloat(r, a, ref y, "  Aperture", "gfx.dofa", _settings.DOFAperture, 1f, 22f);
            }

            Header(r, a, ref y, "Fog");
            Toggle(r, a, ref y, "Enable Fog", "gfx.fog", _settings.Fog);
            if (_settings.Fog)
            {
                SliderFloat(r, a, ref y, "Fog Start", "gfx.fogs", _settings.FogStart, 0f, 500f);
                SliderFloat(r, a, ref y, "Fog End", "gfx.foge", _settings.FogEnd, 1f, 2000f);
                Color3Swatches(r, a, ref y, "Fog Colour", _settings.FogR, _settings.FogG, _settings.FogB);
            }

            Header(r, a, ref y, "Ambient Light");
            SliderFloat(r, a, ref y, "Intensity", "gfx.ambi", _settings.AmbientIntensity, 0f, 2f);
            Color3Swatches(r, a, ref y, "Colour",
                _settings.AmbientR, _settings.AmbientG, _settings.AmbientB);

            Header(r, a, ref y, "Global Illumination");
            Toggle(r, a, ref y, "Realtime GI", "gfx.rgi", _settings.RealtimeGI);
            Toggle(r, a, ref y, "Baked GI", "gfx.bgi", _settings.BakedGI);
        }

        // ── Physics ───────────────────────────────────────────────────────────
        private void DrawPhysics(IEditorRenderer r, RectangleF a, ref float y)
        {
            Header(r, a, ref y, "Gravity");
            SliderFloat(r, a, ref y, "Gravity X", "phy.gx", _settings!.GravityX, -25f, 25f);
            SliderFloat(r, a, ref y, "Gravity Y", "phy.gy", _settings.GravityY, -25f, 25f);
            SliderFloat(r, a, ref y, "Gravity Z", "phy.gz", _settings.GravityZ, -25f, 25f);

            Header(r, a, ref y, "Simulation");
            SliderFloat(r, a, ref y, "Fixed Timestep", "phy.fdt", _settings.FixedTimestep, 0.005f, 0.1f);
            SliderFloat(r, a, ref y, "Max Timestep", "phy.mdt", _settings.MaxTimestep, 0.01f, 1f);
            SliderInt(r, a, ref y, "Solver Iterations", "phy.si", _settings.SolverIterations, 1, 20);
            Toggle(r, a, ref y, "Auto Simulation", "phy.auto", _settings.AutoSimulation);
            Dropdown(r, a, ref y, "Broadphase", "phy.bp", _settings.Broadphase.ToString(),
                new[] { "SweepAndPrune", "MultiBoxPruning", "AutomaticBoxPruning" });

            Header(r, a, ref y, "Default Material");
            SliderFloat(r, a, ref y, "Friction", "phy.fri", _settings.DefaultFriction, 0f, 1f);
            SliderFloat(r, a, ref y, "Bounciness", "phy.bou", _settings.DefaultBounciness, 0f, 1f);
            SliderFloat(r, a, ref y, "Sleep Threshold", "phy.slp", _settings.SleepThreshold, 0f, 0.1f);
        }

        // ── Audio ─────────────────────────────────────────────────────────────
        private void DrawAudio(IEditorRenderer r, RectangleF a, ref float y)
        {
            Header(r, a, ref y, "Volume");
            SliderFloat(r, a, ref y, "Master Volume", "aud.mv", _settings!.MasterVolume, 0f, 1f);
            SliderFloat(r, a, ref y, "Music Volume", "aud.mu", _settings.MusicVolume, 0f, 1f);
            SliderFloat(r, a, ref y, "SFX Volume", "aud.sf", _settings.SFXVolume, 0f, 1f);

            Header(r, a, ref y, "Output");
            Dropdown(r, a, ref y, "Speaker Mode", "aud.spk", _settings.SpeakerMode.ToString(),
                new[] { "Stereo", "Mono", "Quad", "Surround5point1", "Surround7point1" });
            SliderInt(r, a, ref y, "Sample Rate", "aud.sr", _settings.SampleRate, 8000, 96000);

            Header(r, a, ref y, "3D Audio");
            SliderFloat(r, a, ref y, "Doppler Factor", "aud.dop", _settings.DopplerFactor, 0f, 5f);
            Toggle(r, a, ref y, "Spatial Blend 3D", "aud.3d", _settings.SpatialBlend3D);
        }

        // ── Time ─────────────────────────────────────────────────────────────
        private void DrawTime(IEditorRenderer r, RectangleF a, ref float y)
        {
            Header(r, a, ref y, "Time");
            SliderFloat(r, a, ref y, "Time Scale", "tim.ts", _settings!.TimeScale, 0.01f, 10f);
            SliderFloat(r, a, ref y, "Max Particle Delta Time", "tim.pdt", _settings.MaxParticleDeltaTime, 0.001f, 0.1f);
        }

        // ── Input ─────────────────────────────────────────────────────────────
        private void DrawInput(IEditorRenderer r, RectangleF a, ref float y)
        {
            Header(r, a, ref y, "Input");
            SliderFloat(r, a, ref y, "Mouse Sensitivity", "inp.ms", _settings!.MouseSensitivity, 0.1f, 5f);
            SliderFloat(r, a, ref y, "Controller Deadzone", "inp.dz", _settings.ControllerDeadzone, 0f, 0.5f);
            Toggle(r, a, ref y, "Invert Mouse Y", "inp.inv", _settings.InvertMouseY);
        }

        // ── Scripting ─────────────────────────────────────────────────────────
        private void DrawScripting(IEditorRenderer r, RectangleF a, ref float y)
        {
            Header(r, a, ref y, "Scripting");
            TextField(r, a, ref y, "Backend", "scr.be", _settings!.ScriptingBackend);
            TextField(r, a, ref y, "API Compatibility", "scr.api", _settings.ApiCompatibility);

            y += 12f;
            InfoBox(r, a, ref y, "Changing these settings requires a project recompile.", CYellow);
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Field Renderers — each appends a Ctrl entry for click detection
        // ════════════════════════════════════════════════════════════════════════
        private RectangleF RowBg(IEditorRenderer r, RectangleF a, float y, float h = RowH)
        {
            var rr = new RectangleF(a.X, y, a.Width, h);
            r.FillRect(rr, _rowIdx++ % 2 == 0 ? CRow : CRowAlt);
            return rr;
        }

        private void Header(IEditorRenderer r, RectangleF a, ref float y, string title)
        {
            _rowIdx = 0;
            y += 6f;
            r.FillRect(new RectangleF(a.X, y, a.Width, 22f), Color.FromArgb(255, 26, 28, 38));
            r.DrawText(title, new PointF(a.X + 10f, y + 4f), CAccent, 10f);
            y += 22f;
        }

        private void Toggle(IEditorRenderer r, RectangleF a, ref float y,
                            string label, string id, bool value)
        {
            var rr = RowBg(r, a, y);
            // Label takes up left 55%, controls right-aligned
            float ctrlX = rr.Right - 80f;
            r.DrawText(label, new PointF(rr.X + 14f, y + 7f), CText, 10f);

            var tog = new RectangleF(rr.Right - 44f, y + 6f, 36f, 16f);
            r.FillRect(tog, value ? CTogOn : CTogOff);
            r.DrawRect(tog, CBorder);
            r.FillRect(new RectangleF(value ? tog.Right - 14f : tog.X + 2f, tog.Y + 2f, 12f, 12f),
                Color.White);
            r.DrawText(value ? "ON" : "OFF",
                new PointF(rr.Right - 56f, y + 8f), CTextDim, 8f);

            _ctrls.Add(new Ctrl(CtrlType.Toggle, id, rr));
            y += RowH;
        }

        private void SliderFloat(IEditorRenderer r, RectangleF a, ref float y,
                                  string label, string id, float value, float min, float max)
        {
            var rr = RowBg(r, a, y);
            r.DrawText(label, new PointF(rr.X + 14f, y + 7f), CText, 10f);

            // Value box: fixed 44px at far right
            // Track: fills from 40% of row width to value box
            float valBoxW = 44f;
            float valBoxX = rr.Right - valBoxW - 4f;
            float trackEnd = valBoxX - 4f;
            float trackX = Math.Max(rr.X + rr.Width * 0.42f, rr.X + 90f);
            float trackW = Math.Max(trackEnd - trackX, 20f);
            var track = new RectangleF(trackX, y + 10f, trackW, 8f);

            r.FillRect(track, Color.FromArgb(255, 22, 24, 30));
            r.DrawRect(track, _draggingSlider == id ? CAccent : CBorder);
            float t = Math.Clamp((value - min) / (max - min), 0f, 1f);
            r.FillRect(new RectangleF(track.X, track.Y, track.Width * t, track.Height), CAccent);
            r.FillRect(new RectangleF(track.X + track.Width * t - 4f, track.Y - 2f, 8f, 12f), Color.White);

            var valLabel = new RectangleF(valBoxX, y + 4f, valBoxW, 20f);
            r.FillRect(valLabel, Color.FromArgb(255, 22, 24, 30));
            r.DrawRect(valLabel, CBorder);
            r.DrawText(value.ToString("F2"), new PointF(valLabel.X + 3f, valLabel.Y + 4f), CText, 9f);

            _ctrls.Add(new Ctrl(CtrlType.Slider, id, track));
            y += RowH;
        }

        private void SliderInt(IEditorRenderer r, RectangleF a, ref float y,
                                string label, string id, int value, int min, int max)
        {
            var rr = RowBg(r, a, y);
            r.DrawText(label, new PointF(rr.X + 14f, y + 7f), CText, 10f);

            float valBoxW = 44f;
            float valBoxX = rr.Right - valBoxW - 4f;
            float trackEnd = valBoxX - 4f;
            float trackX = Math.Max(rr.X + rr.Width * 0.42f, rr.X + 90f);
            float trackW = Math.Max(trackEnd - trackX, 20f);
            var track = new RectangleF(trackX, y + 10f, trackW, 8f);

            r.FillRect(track, Color.FromArgb(255, 22, 24, 30));
            r.DrawRect(track, _draggingSlider == id ? CAccent : CBorder);
            float t = Math.Clamp((float)(value - min) / (max - min), 0f, 1f);
            r.FillRect(new RectangleF(track.X, track.Y, track.Width * t, track.Height), CAccent);
            r.FillRect(new RectangleF(track.X + track.Width * t - 4f, track.Y - 2f, 8f, 12f), Color.White);

            var valLabel = new RectangleF(valBoxX, y + 4f, valBoxW, 20f);
            r.FillRect(valLabel, Color.FromArgb(255, 22, 24, 30));
            r.DrawRect(valLabel, CBorder);
            r.DrawText(value.ToString(), new PointF(valLabel.X + 3f, valLabel.Y + 4f), CText, 9f);

            _ctrls.Add(new Ctrl(CtrlType.Slider, id, track));
            y += RowH;
        }

        private void Dropdown(IEditorRenderer r, RectangleF a, ref float y,
                              string label, string id, string value, string[] options)
        {
            var rr = RowBg(r, a, y);
            r.DrawText(label, new PointF(rr.X + 14f, y + 7f), CText, 10f);

            bool open = _openDd == id;
            float ddW = Math.Min(160f, rr.Width * 0.44f);
            var dd = new RectangleF(rr.Right - ddW - 4f, y + 4f, ddW, 20f);
            bool hov = dd.Contains(_mouse) && !open;
            r.FillRect(dd, open ? Color.FromArgb(255, 50, 80, 130)
                : hov ? Color.FromArgb(255, 45, 55, 75) : Color.FromArgb(255, 36, 40, 54));
            r.DrawRect(dd, open ? CAccent : CBorder);
            // Clip the value text to the dropdown box
            string disp = value.Length > 18 ? value[..15] + "…" : value;
            r.DrawText(disp, new PointF(dd.X + 6f, dd.Y + 4f), CText, 9f);
            r.DrawText("▾", new PointF(dd.Right - 14f, dd.Y + 4f), CTextDim, 9f);

            if (open) { _openDdOptions = options; _openDdOrigin = dd; }

            _ctrls.Add(new Ctrl(CtrlType.Dropdown, id, dd, id));
            y += RowH;
        }

        private void TextField(IEditorRenderer r, RectangleF a, ref float y,
                               string label, string id, string value, string? hint = null)
        {
            var rr = RowBg(r, a, y);
            r.DrawText(label, new PointF(rr.X + 14f, y + 7f), CText, 10f);

            bool focused = _focusedField == id;
            string display = focused ? _fieldText + "|"
                : (string.IsNullOrEmpty(value) ? (hint ?? "") : value);
            Color textColor = (string.IsNullOrEmpty(value) && !focused) ? CTextDim : CText;

            float tfW = Math.Min(200f, rr.Width * 0.52f);
            var tf = new RectangleF(rr.Right - tfW - 4f, y + 4f, tfW, 20f);
            r.FillRect(tf, focused ? Color.FromArgb(255, 18, 20, 26) : Color.FromArgb(255, 22, 24, 30));
            r.DrawRect(tf, focused ? CAccent : CBorder);
            int maxChars = Math.Max(8, (int)(tfW / 7f));
            string clip = display.Length > maxChars ? display[^maxChars..] : display;
            r.DrawText(clip, new PointF(tf.X + 4f, tf.Y + 4f), textColor, 9f);

            _ctrls.Add(new Ctrl(CtrlType.TextField, id, rr));
            y += RowH;
        }

        private void TextAreaField(IEditorRenderer r, RectangleF a, ref float y,
                                   string label, string id, string value)
        {
            float h = 60f;
            var rr = new RectangleF(a.X, y, a.Width, h);
            r.FillRect(rr, _rowIdx++ % 2 == 0 ? CRow : CRowAlt);
            r.DrawText(label, new PointF(rr.X + 14f, y + 6f), CText, 10f);
            var tf = new RectangleF(rr.X + 10f, y + 26f, rr.Width - 20f, 26f);
            bool focused = _focusedField == id;
            r.FillRect(tf, focused ? Color.FromArgb(255, 18, 20, 26) : Color.FromArgb(255, 22, 24, 30));
            r.DrawRect(tf, focused ? CAccent : CBorder);
            string display = focused ? _fieldText + "|" : value;
            r.DrawText(display.Length > 60 ? display[..57] + "…" : display,
                new PointF(tf.X + 4f, tf.Y + 5f), CText, 9f);
            _ctrls.Add(new Ctrl(CtrlType.TextField, id, rr));
            y += h;
        }

        private void Color3Swatches(IEditorRenderer r, RectangleF a, ref float y,
                                    string label, float rv, float gv, float bv)
        {
            var rr = RowBg(r, a, y);
            r.DrawText(label, new PointF(rr.X + 14f, y + 7f), CText, 10f);
            int ri = (int)(rv * 255), gi = (int)(gv * 255), bi = (int)(bv * 255);
            var sw = new RectangleF(rr.Right - 52f, y + 4f, 40f, 20f);
            r.FillRect(sw, Color.FromArgb(255, Math.Clamp(ri, 0, 255), Math.Clamp(gi, 0, 255), Math.Clamp(bi, 0, 255)));
            r.DrawRect(sw, CBorder);
            r.DrawText($"R:{ri}  G:{gi}  B:{bi}", new PointF(rr.Right - 140f, y + 8f), CTextDim, 8f);
            y += RowH;
        }

        private void InfoBox(IEditorRenderer r, RectangleF a, ref float y, string msg, Color c)
        {
            var box = new RectangleF(a.X + 8f, y, a.Width - 16f, 30f);
            r.FillRect(box, Color.FromArgb(40, c.R, c.G, c.B));
            r.DrawRect(box, Color.FromArgb(120, c.R, c.G, c.B));
            r.DrawText("⚠  " + msg, new PointF(box.X + 8f, box.Y + 8f), c, 9f);
            y += 34f;
        }

        // ── Dropdown popup ────────────────────────────────────────────────────
        private void DrawDropdownPopup(IEditorRenderer r, RectangleF area)
        {
            if (_openDdOptions == null) return;
            float itemH = 22f;
            float ph = _openDdOptions.Length * itemH + 4f;
            var pop = new RectangleF(
                _openDdOrigin.X, _openDdOrigin.Bottom + 2f,
                Math.Max(_openDdOrigin.Width, 160f), ph);

            // Ensure popup doesn't go below window
            if (pop.Bottom > Bounds.Bottom - FootH - 4f)
                pop = new RectangleF(pop.X, _openDdOrigin.Y - ph - 2f, pop.Width, pop.Height);

            r.FillRect(pop, Color.FromArgb(255, 30, 34, 44));
            r.DrawRect(pop, CAccent, 1.5f);

            for (int i = 0; i < _openDdOptions.Length; i++)
            {
                var ir = new RectangleF(pop.X, pop.Y + 2f + i * itemH, pop.Width, itemH);
                bool hov = ir.Contains(_mouse);
                if (hov) r.FillRect(ir, Color.FromArgb(255, 50, 80, 130));
                r.DrawText(_openDdOptions[i], new PointF(ir.X + 8f, ir.Y + 4f), CText, 9f);
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Mouse
        // ════════════════════════════════════════════════════════════════════════
        public override void OnMouseMove(PointF pos)
        {
            _mouse = pos;

            // Live slider drag
            if (_draggingSlider != null)
            {
                float t = Math.Clamp((pos.X - _dragTrack.X) / _dragTrack.Width, 0f, 1f);
                ApplySlider(_draggingSlider, t);
            }
        }

        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (!IsVisible || _settings == null) return;
            _mouse = pos;

            // Dropdown popup click
            if (_openDd != null && _openDdOptions != null)
            {
                float itemH = 22f;
                float ph = _openDdOptions.Length * itemH + 4f;
                var pop = new RectangleF(_openDdOrigin.X, _openDdOrigin.Bottom + 2f,
                                              Math.Max(_openDdOrigin.Width, 160f), ph);
                if (pop.Bottom > Bounds.Bottom - FootH - 4f)
                    pop = new RectangleF(pop.X, _openDdOrigin.Y - ph - 2f, pop.Width, pop.Height);

                for (int i = 0; i < _openDdOptions.Length; i++)
                {
                    var ir = new RectangleF(pop.X, pop.Y + 2f + i * itemH, pop.Width, itemH);
                    if (ir.Contains(pos))
                    {
                        ApplyDropdown(_openDd, _openDdOptions[i]);
                        _openDd = null;
                        _settings.Save();
                        return;
                    }
                }
                _openDd = null;
                return;
            }

            // Title bar close button
            if (new RectangleF(Bounds.Right - 28f, Bounds.Y + 6f, 22f, 20f).Contains(pos))
            { _settings.Save(); IsVisible = false; return; }

            // Footer apply button
            var applyBtn = new RectangleF(Bounds.Right - 104f, Bounds.Bottom - FootH + 7f, 92f, 24f);
            if (applyBtn.Contains(pos)) { _settings.Save(); IsVisible = false; return; }

            // Side nav
            var sideR = new RectangleF(Bounds.X, Bounds.Y + TitleH, SideW, Bounds.Height - TitleH);
            if (sideR.Contains(pos))
            {
                var secs = new[] { Section.Player, Section.Display, Section.Graphics,
                    Section.Physics, Section.Audio, Section.Time, Section.Input, Section.Scripting };
                float sy = sideR.Y + 10f;
                foreach (var sec in secs)
                {
                    if (new RectangleF(sideR.X, sy, SideW - 1f, 28f).Contains(pos))
                    { _section = sec; _scroll = 0; _focusedField = null; return; }
                    sy += 30f;
                }
                return;
            }

            // Defocus text field if clicking elsewhere
            _focusedField = null;

            // Hit-test against registered controls
            foreach (var ctrl in _ctrls)
            {
                if (!ctrl.Bounds.Contains(pos)) continue;

                switch (ctrl.Type)
                {
                    case CtrlType.Toggle:
                        ApplyToggle(ctrl.Id);
                        _settings.Save();
                        break;
                    case CtrlType.Slider:
                        float t = Math.Clamp((pos.X - ctrl.Bounds.X) / ctrl.Bounds.Width, 0f, 1f);
                        _draggingSlider = ctrl.Id;
                        _dragTrack = ctrl.Bounds;
                        ApplySlider(ctrl.Id, t);
                        break;
                    case CtrlType.Dropdown:
                        _openDd = _openDd == ctrl.Id ? null : ctrl.Id;
                        break;
                    case CtrlType.TextField:
                        _focusedField = ctrl.Id;
                        _fieldText = GetFieldValue(ctrl.Id);
                        break;
                }
                return;
            }
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            if (_draggingSlider != null)
            {
                _settings?.Save();
                _draggingSlider = null;
            }
        }

        public override void OnMouseScroll(float delta)
        {
            if (!IsVisible) return;
            _scroll = Math.Clamp(_scroll - delta * 30f, 0f, Math.Max(0f, _contentH - 400f));
        }

        // ── Keyboard — text field editing ─────────────────────────────────────
        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (!IsVisible || _focusedField == null) return;
            if (e.Key == Keys.Escape) { _focusedField = null; return; }
            if (e.Key == Keys.Enter) { CommitTextField(); return; }
            if (e.Key == Keys.Backspace && _fieldText.Length > 0)
                _fieldText = _fieldText[..^1];
        }

        public override void OnTextInput(TextInputEventArgs e)
        {
            if (!IsVisible || _focusedField == null) return;
            _fieldText += e.AsString;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  Apply helpers — map control ID back to settings field
        // ════════════════════════════════════════════════════════════════════════
        private void ApplyToggle(string id)
        {
            if (_settings == null) return;
            switch (id)
            {
                case "disp.resize": _settings.AllowResizing = !_settings.AllowResizing; break;
                case "gfx.softshadow": _settings.SoftShadows = !_settings.SoftShadows; break;
                case "gfx.aniso": _settings.Anisotropic = !_settings.Anisotropic; break;
                case "gfx.hdr": _settings.HDR = !_settings.HDR; break;
                case "gfx.ssao": _settings.SSAO = !_settings.SSAO; break;
                case "gfx.bloom": _settings.Bloom = !_settings.Bloom; break;
                case "gfx.mb": _settings.MotionBlur = !_settings.MotionBlur; break;
                case "gfx.dof": _settings.DepthOfField = !_settings.DepthOfField; break;
                case "gfx.fog": _settings.Fog = !_settings.Fog; break;
                case "gfx.rgi": _settings.RealtimeGI = !_settings.RealtimeGI; break;
                case "gfx.bgi": _settings.BakedGI = !_settings.BakedGI; break;
                case "phy.auto": _settings.AutoSimulation = !_settings.AutoSimulation; break;
                case "aud.3d": _settings.SpatialBlend3D = !_settings.SpatialBlend3D; break;
                case "inp.inv": _settings.InvertMouseY = !_settings.InvertMouseY; break;
            }
        }

        private void ApplySlider(string id, float t)
        {
            if (_settings == null) return;
            switch (id)
            {
                case "disp.w": _settings.DefaultWidth = (int)Lerp(320, 7680, t); break;
                case "disp.h": _settings.DefaultHeight = (int)Lerp(240, 4320, t); break;
                case "disp.mw": _settings.MinWidth = (int)Lerp(320, 3840, t); break;
                case "disp.mh": _settings.MinHeight = (int)Lerp(240, 2160, t); break;
                case "disp.fps": _settings.TargetFrameRate = (int)Lerp(-1, 360, t); break;
                case "gfx.shadd": _settings.ShadowDistance = Lerp(1f, 500f, t); break;
                case "gfx.anisolv": _settings.AnisotropicLevel = (int)Lerp(1, 16, t); break;
                case "gfx.bloomt": _settings.BloomThreshold = Lerp(0f, 5f, t); break;
                case "gfx.bloomi": _settings.BloomIntensity = Lerp(0f, 5f, t); break;
                case "gfx.mba": _settings.MotionBlurShutter = Lerp(0f, 360f, t); break;
                case "gfx.dofl": _settings.DOFFocalLength = Lerp(1f, 300f, t); break;
                case "gfx.dofa": _settings.DOFAperture = Lerp(1f, 22f, t); break;
                case "gfx.fogs": _settings.FogStart = Lerp(0f, 500f, t); break;
                case "gfx.foge": _settings.FogEnd = Lerp(1f, 2000f, t); break;
                case "gfx.ambi": _settings.AmbientIntensity = Lerp(0f, 2f, t); break;
                case "phy.gx": _settings.GravityX = Lerp(-25f, 25f, t); break;
                case "phy.gy": _settings.GravityY = Lerp(-25f, 25f, t); break;
                case "phy.gz": _settings.GravityZ = Lerp(-25f, 25f, t); break;
                case "phy.fdt": _settings.FixedTimestep = Lerp(0.005f, 0.1f, t); break;
                case "phy.mdt": _settings.MaxTimestep = Lerp(0.01f, 1f, t); break;
                case "phy.si": _settings.SolverIterations = (int)Lerp(1, 20, t); break;
                case "phy.fri": _settings.DefaultFriction = Lerp(0f, 1f, t); break;
                case "phy.bou": _settings.DefaultBounciness = Lerp(0f, 1f, t); break;
                case "phy.slp": _settings.SleepThreshold = Lerp(0f, 0.1f, t); break;
                case "aud.mv": _settings.MasterVolume = Lerp(0f, 1f, t); break;
                case "aud.mu": _settings.MusicVolume = Lerp(0f, 1f, t); break;
                case "aud.sf": _settings.SFXVolume = Lerp(0f, 1f, t); break;
                case "aud.sr": _settings.SampleRate = (int)Lerp(8000, 96000, t); break;
                case "aud.dop": _settings.DopplerFactor = Lerp(0f, 5f, t); break;
                case "tim.ts": _settings.TimeScale = Lerp(0.01f, 10f, t); break;
                case "tim.pdt": _settings.MaxParticleDeltaTime = Lerp(0.001f, 0.1f, t); break;
                case "inp.ms": _settings.MouseSensitivity = Lerp(0.1f, 5f, t); break;
                case "inp.dz": _settings.ControllerDeadzone = Lerp(0f, 0.5f, t); break;
            }
        }

        private void ApplyDropdown(string id, string value)
        {
            if (_settings == null) return;
            switch (id)
            {
                case "disp.fs": _settings.Fullscreen = Enum.Parse<FullscreenMode>(value); break;
                case "disp.vsync": _settings.VSync = Enum.Parse<ElintriaEngine.Core.VSyncMode>(value); break;
                case "gfx.aa": _settings.AntiAliasing = Enum.Parse<AntiAliasMode>(value); break;
                case "gfx.shadq": _settings.Shadows = Enum.Parse<ShadowQuality>(value); break;
                case "gfx.shadr": _settings.ShadowResolution = Enum.Parse<ShadowResolution>(value); break;
                case "gfx.texq": _settings.TextureQuality = Enum.Parse<TextureQuality>(value); break;
                case "gfx.cs": _settings.ColorSpace = Enum.Parse<ColorSpace>(value); break;
                case "phy.bp": _settings.Broadphase = Enum.Parse<PhysicsBroadphase>(value); break;
                case "aud.spk": _settings.SpeakerMode = Enum.Parse<SpeakerMode>(value); break;
            }
        }

        private string GetFieldValue(string id)
        {
            if (_settings == null) return "";
            return id switch
            {
                "player.name" => _settings.ProductName,
                "player.company" => _settings.CompanyName,
                "player.ver" => _settings.Version,
                "player.bundle" => _settings.BundleId,
                "player.copy" => _settings.Copyright,
                "player.icon" => _settings.IconPath,
                "player.splash" => _settings.SplashPath,
                "player.desc" => _settings.Description,
                "scr.be" => _settings.ScriptingBackend,
                "scr.api" => _settings.ApiCompatibility,
                _ => ""
            };
        }

        private void CommitTextField()
        {
            if (_settings == null || _focusedField == null) return;
            string v = _fieldText.Trim();
            switch (_focusedField)
            {
                case "player.name": _settings.ProductName = v; break;
                case "player.company": _settings.CompanyName = v; break;
                case "player.ver": _settings.Version = v; break;
                case "player.bundle": _settings.BundleId = v; break;
                case "player.copy": _settings.Copyright = v; break;
                case "player.icon": _settings.IconPath = v; break;
                case "player.splash": _settings.SplashPath = v; break;
                case "player.desc": _settings.Description = v; break;
                case "scr.be": _settings.ScriptingBackend = v; break;
                case "scr.api": _settings.ApiCompatibility = v; break;
            }
            _settings.Save();
            _focusedField = null;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // ── Drag tracking ──────────────────────────────────────────────────────
        // OnMouseUp is not called by base.Panel so we override it here
        // (EditorLayout calls p.OnMouseUp on all panels)
    }
}