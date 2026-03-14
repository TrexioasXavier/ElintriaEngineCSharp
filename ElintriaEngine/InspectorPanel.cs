using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ElintriaEngine.Core;

namespace ElintriaEngine.UI.Panels
{
    // ── Describes one rendered field row so we can hit-test it ────────────────
    internal record FieldRecord(
        RectangleF Bounds,
        string Id,
        object? Value,
        Type FieldType,
        Action<object?> Setter);

    public class InspectorPanel : Panel
    {
        private GameObject? _target;

        /// <summary>Set by EditorLayout so the inspector can toggle collider-edit mode.</summary>
        public SceneViewPanel? SceneView { get; set; }

        /// <summary>Set by EditorLayout so the picker can enumerate scene GameObjects.</summary>
        public Core.Scene? Scene { get; set; }

        /// <summary>Set by EditorLayout so the picker can scan prefab files.</summary>
        public string ProjectRoot { get; set; } = "";

        /// <summary>The actual Assets folder (may differ from ProjectRoot/Assets on some setups).</summary>
        public string AssetsRoot { get; set; } = "";

        private bool _dropHighlight;

        // Fields rendered this frame (rebuilt each render)
        private readonly List<FieldRecord> _fields = new();

        // Track the last assembly we saw so we detect each new compile automatically.
        private System.Reflection.Assembly? _lastUserAssembly;

        // Active text edit
        private string? _editId;
        private string _editBuf = "";
        private Action<string>? _editCommit;

        // Float drag
        private string? _dragId;
        private float _dragStartX;
        private float _dragStartVal;
        private Action<float>? _dragSetter;

        // Add Component popup
        private bool _showAddComp;
        private string _addCompFilter = "";
        private RectangleF _addCompRect;
        private List<string> _compMatches = new();

        // ── Component collapse state ──────────────────────────────────────────
        // Key = component type name + instance hash; true = expanded (default)
        private readonly Dictionary<string, bool> _compExpanded = new();

        // ── Inspector component drag (drag a component row → drop on ref field) ──
        public Component? ActiveDragComponent { get; private set; }
        private Component? _compDragCandidate;
        private PointF _compDragStart;
        private bool _compDragging;
        private PointF _mouse;

        // ── Color picker state ────────────────────────────────────────────────
        private string? _colorPickerId;
        private float _cpR, _cpG, _cpB;
        private Action<float, float, float>? _cpApply;

        // ── GameObject / Component drag-drop target ───────────────────────────
        // Set by EditorLayout when a Hierarchy GO drag hovers over the inspector.
        // Key = field id, Value = pending drop GO.
        private string? _pendingDropFieldId;
        private GameObject? _pendingDropGO;
        public void AcceptGODrop(string fieldId, GameObject go)
        { _pendingDropFieldId = fieldId; _pendingDropGO = go; }

        /// <summary>
        /// Called by EditorLayout when a .prefab file is dropped onto a ref field.
        /// Loads the prefab, extracts the required component (or GO), and assigns it.
        /// If the field needs a Component, the prefab GO is also added to the scene so
        /// the component lives inside a real scene object.
        /// </summary>
        public void AcceptPrefabDrop(string fieldId, string prefabPath)
        {
            foreach (var f in _fields)
            {
                if (f.Id != fieldId) continue;

                var prefabGO = Core.SceneSerializer.LoadPrefab(prefabPath);
                if (prefabGO == null) break;

                if (f.FieldType == typeof(Core.GameObject))
                {
                    // Add the prefab to the scene and assign the GO
                    Scene?.AddGameObject(prefabGO);
                    f.Setter(prefabGO);
                }
                else if (typeof(Core.Component).IsAssignableFrom(f.FieldType))
                {
                    var comp = prefabGO.GetComponentByType(f.FieldType);
                    if (comp != null)
                    {
                        // Prefab must live in the scene for the component ref to be valid
                        Scene?.AddGameObject(prefabGO);
                        f.Setter(comp);
                    }
                    else
                    {
                        Console.WriteLine(
                            $"[Inspector] Prefab '{prefabGO.Name}' has no {f.FieldType.Name} component.");
                    }
                }
                break;
            }
        }

        // Called once per frame by EditorLayout so inspector can apply queued drops
        public void FlushGODrops()
        {
            if (_pendingDropFieldId == null || _pendingDropGO == null) return;
            foreach (var f in _fields)
            {
                if (f.Id != _pendingDropFieldId) continue;

                if (f.FieldType == typeof(Core.GameObject))
                {
                    // Field expects a GameObject — pass directly
                    f.Setter(_pendingDropGO);
                }
                else if (typeof(Core.Component).IsAssignableFrom(f.FieldType))
                {
                    // Field expects a specific component type — extract it from the GO
                    var comp = _pendingDropGO.GetComponentByType(f.FieldType);
                    if (comp != null)
                        f.Setter(comp);
                    else
                    {
                        // GO doesn't have that component — show nothing silently
                        // (could show error toast in future)
                        Console.WriteLine(
                            $"[Inspector] Dropped GO '{_pendingDropGO.Name}' has no {f.FieldType.Name} component.");
                    }
                }
                break;
            }
            _pendingDropFieldId = null; _pendingDropGO = null;
        }

        // Highlight field id when hovering a GO drag over it
        public string? HoveredDropFieldId { get; set; }
        private static readonly string[] AllComponents =
        {
            "MeshFilter","MeshRenderer",
            "Camera",
            "DirectionalLight","SpotLight",
            "Rigidbody","Rigidbody3D",
            "BoxCollider","SphereCollider","CapsuleCollider","MeshCollider",
            "BoxCollider2D","CircleCollider2D",
            "AudioSource","AudioListener",
            "ParticleSystem",
            "CanvasComponent","CanvasRenderer","ImageComponent","ButtonComponent",
            "TextComponent","SliderComponent"
        };

        private const float LW = 118f;   // label column width
        private const float FH = 20f;    // field row height
        private const float SH = 22f;    // section header height
        private const float PAD = 6f;

        private static readonly Color ColLabel = Color.FromArgb(255, 170, 170, 180);

        // ── One-shot click registrations (cleared + rebuilt each frame) ───────
        private readonly Dictionary<string, (RectangleF Rect, Action Action)> _clickMap = new();
        private void RegisterClick(RectangleF rect, string id, Action action)
            => _clickMap[id] = (rect, action);

        // ── Particle System inspector sub-panel ───────────────────────────────
        private readonly ParticleSystemInspector _psInspector = new();

        public InspectorPanel(RectangleF bounds) : base("Inspector", bounds)
        { MinWidth = 210f; MinHeight = 200f; }

        public void Inspect(GameObject? go)
        {
            _target = go;
            _editId = null;
            _editBuf = "";
            _showAddComp = false;
            _fields.Clear();
            ScrollOffset = 0;
        }

        /// <summary>
        /// Re-inspect the CURRENT target (whatever is already shown).
        /// Call this after a script recompile so new fields appear immediately
        /// without needing the user to re-click the GameObject.
        /// </summary>
        public void ForceRefresh() => Inspect(_target);

        public void SetDropHighlight(bool on) => _dropHighlight = on;

        // ── Render ─────────────────────────────────────────────────────────────
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;
            _clickMap.Clear();   // rebuilt each frame
            DrawHeader(r);

            // ── Auto-detect script recompile ──────────────────────────────────
            // If the user-assembly reference changed since last frame, a new compile
            // just landed. Reset scroll so newly added fields are visible at top.
            var currentAsm = Core.ComponentRegistry.UserAssembly;
            if (currentAsm != null && currentAsm != _lastUserAssembly)
            {
                _lastUserAssembly = currentAsm;
                ScrollOffset = 0;
                // Keep _editId so the user doesn't lose a half-typed value;
                // the field might still exist in the new type.
            }

            var cr = ContentRect;

            // ── Measure header + transform height first (no clip, dry run) ────
            // We pin the AddComponent bar right after the transform section.
            // Draw header/transform into a temp y to know where to place the bar.
            const float AddCompBarH = 36f;

            // Pass 1 — draw header + transform inside full content clip
            r.PushClip(cr);
            r.FillRect(cr, ColBg);
            _fields.Clear();

            if (_target == null)
            {
                r.DrawText("Nothing selected.", new PointF(cr.X + 10, cr.Y + 12), ColTextDim, 11f);
                if (_dropHighlight) DrawDropOverlay(r, cr);
                r.PopClip(); DrawScrollBar(r); return;
            }

            // Draw header (no scroll — always anchored at top)
            float headerY = cr.Y;
            DrawObjectHeader(r, cr, ref headerY);
            DrawSeparator(r, cr, ref headerY);
            DrawTransform(r, cr, ref headerY);
            DrawSeparator(r, cr, ref headerY);
            float fixedTopH = headerY - cr.Y; // height consumed by header+transform
            r.PopClip();

            // ── Add Component bar — pinned immediately below header/transform ──
            var addBarRect = new RectangleF(cr.X, cr.Y + fixedTopH, cr.Width, AddCompBarH);
            r.FillRect(addBarRect, Color.FromArgb(255, 34, 34, 38));
            r.DrawLine(new PointF(addBarRect.X, addBarRect.Bottom),
                       new PointF(addBarRect.Right, addBarRect.Bottom),
                       Color.FromArgb(255, 55, 55, 62));
            DrawAddComponentButton(r, addBarRect);

            // ── Scrollable components region — sits below the bar ─────────────
            float compTop = cr.Y + fixedTopH + AddCompBarH;
            var scrollCr = new RectangleF(cr.X, compTop, cr.Width, cr.Bottom - compTop);

            r.PushClip(scrollCr);
            r.FillRect(scrollCr, ColBg);

            float y = scrollCr.Y - ScrollOffset;

            foreach (var comp in new List<Component>(_target.Components))
                DrawComponent(r, scrollCr, comp, ref y);

            ContentHeight = (y + ScrollOffset) - scrollCr.Y + 10f;

            if (_dropHighlight) DrawDropOverlay(r, scrollCr);
            r.PopClip();
            DrawScrollBar(r);

            // Add component popup drawn OUTSIDE clip so it's not cut off
            if (_showAddComp) DrawAddCompPopup(r);
        }

        // ── Object header ──────────────────────────────────────────────────────
        private bool _showTagDropdown = false;
        private bool _showLayerDropdown = false;
        private int _tagDropHov = -1;
        private int _layerDropHov = -1;
        private RectangleF _tagBtnRect, _layerBtnRect;
        private RectangleF _tagDropRect, _layerDropRect;

        private void DrawObjectHeader(IEditorRenderer r, RectangleF cr, ref float y)
        {
            if (_target == null) return;
            var row = new RectangleF(cr.X, y, cr.Width, 26f);
            r.FillRect(row, Color.FromArgb(255, 42, 42, 42));

            // Active checkbox
            var cb = new RectangleF(cr.X + PAD, y + 6f, 14f, 14f);
            r.FillRect(cb, _target.ActiveSelf ? Color.FromArgb(255, 55, 155, 55) : Color.FromArgb(255, 52, 52, 52));
            r.DrawRect(cb, ColBorder);
            if (_target.ActiveSelf) r.DrawText("ok", new PointF(cb.X + 1f, cb.Y + 2f), Color.White, 8f);

            // Name field
            string nm = _editId == "__name__" ? _editBuf + "|" : _target.Name;
            r.DrawText(nm, new PointF(cr.X + PAD + 20f, y + 7f), ColText, 12f);

            // Tag dropdown button
            float btnW = 90f, btnH = 11f;
            _tagBtnRect = new RectangleF(cr.Right - btnW - 4f, y + 2f, btnW, btnH);
            _layerBtnRect = new RectangleF(cr.Right - btnW - 4f, y + 14f, btnW, btnH);
            bool tagHov = _tagBtnRect.Contains(_mouse);
            bool layerHov = _layerBtnRect.Contains(_mouse);

            r.FillRect(_tagBtnRect, tagHov ? ColAccent : Color.FromArgb(255, 50, 50, 58));
            r.FillRect(_layerBtnRect, layerHov ? ColAccent : Color.FromArgb(255, 50, 50, 58));
            r.DrawRect(_tagBtnRect, Color.FromArgb(255, 60, 60, 70));
            r.DrawRect(_layerBtnRect, Color.FromArgb(255, 60, 60, 70));

            r.DrawText("Tag: " + Truncate(_target.Tag, 8),
                new PointF(_tagBtnRect.X + 3f, _tagBtnRect.Y + 1f), ColTextDim, 8f);
            r.DrawText("Layer: " + Truncate(_target.Layer, 6),
                new PointF(_layerBtnRect.X + 3f, _layerBtnRect.Y + 1f), ColTextDim, 8f);

            y += 28f; ContentHeight += 28f;

            // Tag dropdown overlay
            if (_showTagDropdown)
            {
                var tl = Core.TagsAndLayers.Instance;
                float dh = tl.Tags.Count * 18f + 4f;
                _tagDropRect = new RectangleF(_tagBtnRect.X, _tagBtnRect.Bottom, btnW + 20f, dh);
                r.FillRect(_tagDropRect, Color.FromArgb(255, 38, 38, 44));
                r.DrawRect(_tagDropRect, ColAccent);
                for (int i = 0; i < tl.Tags.Count; i++)
                {
                    var ir = new RectangleF(_tagDropRect.X, _tagDropRect.Y + 2f + i * 18f, _tagDropRect.Width, 17f);
                    bool sel = tl.Tags[i] == _target.Tag;
                    bool hov = i == _tagDropHov;
                    if (sel) r.FillRect(ir, ColSelected);
                    else if (hov) r.FillRect(ir, Color.FromArgb(60, 255, 255, 255));
                    r.DrawText(tl.Tags[i], new PointF(ir.X + 4f, ir.Y + 3f),
                        sel ? Color.White : ColText, 9f);
                }
                y += dh; ContentHeight += dh;
            }

            // Layer dropdown overlay
            if (_showLayerDropdown)
            {
                var tl = Core.TagsAndLayers.Instance;
                float dh = tl.Layers.Count * 18f + 4f;
                _layerDropRect = new RectangleF(_layerBtnRect.X, _layerBtnRect.Bottom, btnW + 20f, dh);
                r.FillRect(_layerDropRect, Color.FromArgb(255, 38, 38, 44));
                r.DrawRect(_layerDropRect, ColAccent);
                for (int i = 0; i < tl.Layers.Count; i++)
                {
                    var ir = new RectangleF(_layerDropRect.X, _layerDropRect.Y + 2f + i * 18f, _layerDropRect.Width, 17f);
                    bool sel = tl.Layers[i] == _target.Layer;
                    bool hov = i == _layerDropHov;
                    if (sel) r.FillRect(ir, ColSelected);
                    else if (hov) r.FillRect(ir, Color.FromArgb(60, 255, 255, 255));
                    r.DrawText(tl.Layers[i], new PointF(ir.X + 4f, ir.Y + 3f),
                        sel ? Color.White : ColText, 9f);
                }
                y += dh; ContentHeight += dh;
            }
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";

        // Store mouse position for header hover effects 

        // ── Transform ─────────────────────────────────────────────────────────
        private void DrawTransform(IEditorRenderer r, RectangleF cr, ref float y)
        {
            if (_target == null) return;
            bool expanded = DrawSectionHeader(r, cr, "Transform", null, ref y);
            if (!expanded) return;

            var t = _target.Transform;
            DrawVec3Field(r, cr, "Position", t.LocalPosition, ref y, "t_pos", v => t.LocalPosition = v);
            DrawVec3Field(r, cr, "Rotation", t.LocalEulerAngles, ref y, "t_rot", v => t.LocalEulerAngles = v);
            DrawVec3Field(r, cr, "Scale", t.LocalScale, ref y, "t_scl", v => t.LocalScale = v);
        }

        // ── Component section ──────────────────────────────────────────────────
        private void DrawComponent(IEditorRenderer r, RectangleF cr, Component comp, ref float y)
        {
            string cid = "c" + comp.GetHashCode();

            // ── DynamicScript placeholder — shows editable fields from compiled type ──
            if (comp is Core.DynamicScript ds)
            {
                bool dsExpanded = DrawSectionHeader(r, cr, ds.ScriptTypeName, () => _target?.RemoveComponent(comp), ref y, comp);
                if (!dsExpanded)
                {
                    r.DrawLine(new PointF(cr.X, y), new PointF(cr.Right, y), Color.FromArgb(255, 50, 50, 50));
                    y += 3f; ContentHeight += 3f;
                    return;
                }
                DrawBoolField(r, cr, "Enabled", comp.Enabled, ref y, cid + "_en", v => comp.Enabled = v);

                // Always look up via the latest UserAssembly first (bypasses stale _map entries),
                // then fall back to the registry dict.
                Type? realType = GetLatestScriptType(ds.ScriptTypeName);

                if (realType != null)
                {
                    var skipNames = new HashSet<string>(
                        typeof(Core.Component)
                            .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                            .Select(f => f.Name));

                    bool hasFields = false;
                    foreach (var fi in realType.GetFields(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
                    {
                        if (skipNames.Contains(fi.Name)) continue;
                        hasFields = true;

                        var capturedFi = fi;
                        var fieldName = fi.Name;
                        Type fieldType = fi.FieldType;

                        ds.FieldValues.TryGetValue(fieldName, out var stored);
                        object? display = CoerceToType(stored, fieldType) ?? GetDefault(fieldType);

                        DrawAnyField(r, cr, fieldName, display, fieldType,
                            cid + "_ds_" + fieldName, ref y,
                            newVal => ds.FieldValues[fieldName] = newVal);
                    }

                    if (!hasFields)
                    {
                        r.DrawText("No public fields.",
                            new PointF(cr.X + PAD, y + 3f), Color.FromArgb(255, 120, 120, 120), 9f);
                        y += 18f; ContentHeight += 18f;
                    }
                }
                else
                {
                    float lx = cr.X + PAD;
                    r.FillRect(new RectangleF(lx, y, cr.Width - PAD * 2, 34f),
                        Color.FromArgb(30, 200, 160, 40));
                    r.DrawText("⚙  Script not compiled yet.", new PointF(lx + 6f, y + 4f),
                        Color.FromArgb(255, 190, 150, 40), 9f);
                    r.DrawText("Save your script file — it will compile automatically.",
                        new PointF(lx + 6f, y + 17f), Color.FromArgb(255, 130, 130, 130), 8f);
                    y += 40f; ContentHeight += 40f;
                }

                r.DrawLine(new PointF(cr.X, y), new PointF(cr.Right, y), Color.FromArgb(255, 50, 50, 50));
                y += 3f; ContentHeight += 3f;
                return;
            }

            // ── ParticleSystem — custom inspector ─────────────────────────────
            if (comp is Core.ParticleSystem ps)
            {
                _psInspector.SetMouse(_mouse);
                bool psExpanded = DrawSectionHeader(r, cr, "Particle System", () => _target?.RemoveComponent(comp), ref y, comp);
                r.DrawLine(new PointF(cr.X, y), new PointF(cr.Right, y), Color.FromArgb(255, 50, 50, 50));
                y += 3f; ContentHeight += 3f;
                if (!psExpanded) return;
                _psInspector.Draw(r, cr, ps, ref y, ref ContentHeight);
                return;
            }

            // ── Normal (compiled) component ───────────────────────────────────
            bool compExpanded = DrawSectionHeader(r, cr, comp.GetType().Name, () => _target?.RemoveComponent(comp), ref y, comp);

            // Enabled toggle (always visible even when collapsed — as a quick toggle)
            if (!compExpanded)
            {
                // Draw just the separator when collapsed
                r.DrawLine(new PointF(cr.X, y), new PointF(cr.Right, y), Color.FromArgb(255, 50, 50, 50));
                y += 3f; ContentHeight += 3f;
                return;
            }

            DrawBoolField(r, cr, "Enabled", comp.Enabled, ref y, cid + "_en", v => comp.Enabled = v);

            // ── Collider — "Edit Collider" button ─────────────────────────────
            bool isCollider = comp is Core.BoxCollider
                           || comp is Core.SphereCollider
                           || comp is Core.CapsuleCollider
                           || comp is Core.BoxCollider2D
                           || comp is Core.CircleCollider2D;
            if (isCollider && SceneView != null)
            {
                bool editing = SceneView.ColliderEditMode;
                float bw = cr.Width - PAD * 2;
                var btn = new RectangleF(cr.X + PAD, y + 2f, bw, 20f);
                r.FillRect(btn, editing
                    ? Color.FromArgb(255, 50, 140, 60)
                    : Color.FromArgb(255, 44, 44, 50));
                r.DrawRect(btn, editing
                    ? Color.FromArgb(255, 80, 200, 90)
                    : Color.FromArgb(255, 80, 80, 90));
                r.DrawText(editing ? "✓  Editing Collider  (click to exit)" : "Edit Collider",
                    new PointF(btn.X + 8f, btn.Y + 4f),
                    editing ? Color.FromArgb(255, 140, 255, 150) : Color.FromArgb(255, 200, 200, 210),
                    9f);
                RegisterClick(btn, cid + "_coledit", () => SceneView.SetColliderEditMode(!editing));
                y += 26f; ContentHeight += 26f;
            }

            // ── Rigidbody3D — freeze-axes panel ───────────────────────────────
            if (comp is Core.Rigidbody3D rb3d)
            {
                // Freeze Position row
                r.DrawText("Freeze Position", new PointF(cr.X + PAD, y + 3f), ColLabel, 9f);
                float cx = cr.X + LW;
                DrawInlineBool(r, cr, "X", rb3d.FreezePositionX, ref cx, y, cid + "_fpx", v => rb3d.FreezePositionX = v);
                DrawInlineBool(r, cr, "Y", rb3d.FreezePositionY, ref cx, y, cid + "_fpy", v => rb3d.FreezePositionY = v);
                DrawInlineBool(r, cr, "Z", rb3d.FreezePositionZ, ref cx, y, cid + "_fpz", v => rb3d.FreezePositionZ = v);
                y += FH; ContentHeight += FH;
                // Freeze Rotation row
                cx = cr.X + LW;
                r.DrawText("Freeze Rotation", new PointF(cr.X + PAD, y + 3f), ColLabel, 9f);
                DrawInlineBool(r, cr, "X", rb3d.FreezeRotationX, ref cx, y, cid + "_frx", v => rb3d.FreezeRotationX = v);
                DrawInlineBool(r, cr, "Y", rb3d.FreezeRotationY, ref cx, y, cid + "_fry", v => rb3d.FreezeRotationY = v);
                DrawInlineBool(r, cr, "Z", rb3d.FreezeRotationZ, ref cx, y, cid + "_frz", v => rb3d.FreezeRotationZ = v);
                y += FH; ContentHeight += FH;
            }


            // they are already drawn above or are infrastructure-only.
            var componentBaseFields = new System.Collections.Generic.HashSet<string>(
                typeof(Component).GetFields(BindingFlags.Public | BindingFlags.Instance |
                                            BindingFlags.FlattenHierarchy)
                    .Select(f => f.Name));

            // Pre-scan for Color R/G/B triplet pattern
            var allFields = comp.GetType()
                .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                .Where(f => !componentBaseFields.Contains(f.Name))
                .ToList();

            var skipRGB = new System.Collections.Generic.HashSet<string>();
            for (int fi2 = 0; fi2 < allFields.Count - 2; fi2++)
            {
                var fR = allFields[fi2];
                var fG = allFields[fi2 + 1];
                var fB = allFields[fi2 + 2];
                if (fR.FieldType == typeof(float) && fG.FieldType == typeof(float) &&
                    fB.FieldType == typeof(float) &&
                    fR.Name == "ColorR" && fG.Name == "ColorG" && fB.Name == "ColorB")
                {
                    skipRGB.Add(fR.Name); skipRGB.Add(fG.Name); skipRGB.Add(fB.Name);
                    // Draw as a Color4 swatch + picker
                    float rV = (float)(fR.GetValue(comp) ?? 0f);
                    float gV = (float)(fG.GetValue(comp) ?? 0f);
                    float bV = (float)(fB.GetValue(comp) ?? 0f);
                    var c4v = new Color4(rV, gV, bV, 1f);
                    DrawColorField(r, cr, "Color", c4v, ref y, cid + "_rgb");
                    // Wire picker apply back to the three fields
                    if (_colorPickerId == cid + "_rgb" && _cpApply == null)
                    {
                        _cpR = rV; _cpG = gV; _cpB = bV;
                        _cpApply = (rv, gv, bv) =>
                        {
                            fR.SetValue(comp, rv);
                            fG.SetValue(comp, gv);
                            fB.SetValue(comp, bv);
                        };
                    }
                }
            }

            foreach (var fi in allFields)
            {
                if (skipRGB.Contains(fi.Name)) continue;
                DrawAnyField(r, cr, fi.Name, fi.GetValue(comp), fi.FieldType,
                    cid + "_f_" + fi.Name, ref y, nv => fi.SetValue(comp, nv));
            }

            // Reflected public r/w properties – search all levels of the hierarchy
            // (not just DeclaredOnly) so properties from intermediate base classes appear.
            var seenProps = new System.Collections.Generic.HashSet<string>();
            var componentBaseProps = new System.Collections.Generic.HashSet<string>(
                typeof(Component).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name));

            foreach (var pi in comp.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || !pi.CanWrite) continue;
                if (componentBaseProps.Contains(pi.Name)) continue;  // skip Enabled etc.
                if (!seenProps.Add(pi.Name)) continue;               // skip duplicates
                DrawAnyField(r, cr, pi.Name, pi.GetValue(comp), pi.PropertyType,
                    cid + "_p_" + pi.Name, ref y, nv => pi.SetValue(comp, nv));
            }

            r.DrawLine(new PointF(cr.X, y), new PointF(cr.Right, y),
                Color.FromArgb(255, 50, 50, 50));
            y += 3f; ContentHeight += 3f;
        }

        // ── Add Component button — pinned bar ─────────────────────────────────
        private void DrawAddComponentButton(IEditorRenderer r, RectangleF barRect)
        {
            float by = barRect.Y + 7f;
            var btn = new RectangleF(barRect.X + PAD, by, barRect.Width - PAD * 2, 22f);
            _addCompRect = btn;
            r.FillRect(btn, Color.FromArgb(255, 52, 52, 58));
            r.DrawRect(btn, Color.FromArgb(255, 75, 75, 85));
            r.DrawText("+ Add Component",
                new PointF(btn.X + btn.Width / 2f - 50f, btn.Y + 5f), ColText, 11f);
        }

        // ── Add Component popup ────────────────────────────────────────────────
        private int _addCompHoverIndex = -1;

        // ── Object-ref picker popup ───────────────────────────────────────────
        // Opened when the user clicks a ref-field slot (GO or Component)
        private bool _showRefPicker;
        private string _refPickerFilter = "";
        private Type? _refPickerType;     // required field type
        private Action<object?>? _refPickerSetter;
        private int _refPickerHover = -1;

        // Entries built each time the picker opens (scene GOs + prefab files)
        private readonly List<RefPickerEntry> _refPickerEntries = new();

        private class RefPickerEntry
        {
            public string Label { get; init; } = "";
            public string SubLabel { get; init; } = "";  // e.g. "(Prefab)" or GO path
            public bool IsPrefab { get; init; }
            public string PrefabPath { get; init; } = "";
            public Core.GameObject? GO { get; init; }
            public Core.Component? Component { get; init; }
        }

        // Component categories for grouping in the popup
        private static readonly (string Category, string[] Names)[] CompCategories =
        {
            ("Mesh",      new[]{ "MeshFilter","MeshRenderer" }),
            ("Camera",    new[]{ "Camera" }),
            ("Lighting",  new[]{ "DirectionalLight","SpotLight" }),
            ("Physics",   new[]{ "Rigidbody","Rigidbody3D","BoxCollider","SphereCollider",
                                 "CapsuleCollider","MeshCollider","BoxCollider2D","CircleCollider2D" }),
            ("Audio",     new[]{ "AudioSource","AudioListener" }),
            ("Effects",   new[]{ "ParticleSystem" }),
            ("UI",        new[]{ "CanvasComponent","CanvasRenderer","ImageComponent",
                                 "ButtonComponent","TextComponent","SliderComponent" }),
        };

        private void DrawAddCompPopup(IEditorRenderer r)
        {
            bool searching = _addCompFilter.Length > 0;

            // Build flat match list
            _compMatches.Clear();
            foreach (var name in AllComponents)
                if (!searching || name.ToLower().Contains(_addCompFilter.ToLower()))
                    _compMatches.Add(name);

            // Calculate height: search bar + items (+ category headers when not searching)
            float itemH = 20f;
            float catH = 16f;
            float totalItems = _compMatches.Count * itemH;
            float totalCats = searching ? 0 : CompCategories.Length * catH;
            float popH = Math.Min(32f + totalItems + totalCats, 320f);
            float popW = Math.Max(Bounds.Width - PAD * 2, 220f);
            float px = Bounds.X + PAD;

            float pyDown = _addCompRect.Bottom + 2f;
            float py = (pyDown + popH <= Bounds.Bottom) ? pyDown
                           : _addCompRect.Y - popH - 2f;
            py = Math.Max(Bounds.Y + 2f, Math.Min(py, Bounds.Bottom - popH - 2f));

            var pop = new RectangleF(px, py, popW, popH);
            r.FillRect(new RectangleF(px + 3, py + 3, popW, popH), Color.FromArgb(60, 0, 0, 0));
            r.FillRect(pop, Color.FromArgb(248, 32, 32, 36));
            r.DrawRect(pop, Color.FromArgb(255, 80, 80, 95), 1f);

            // Search bar
            var searchRect = new RectangleF(px + 4, py + 4, popW - 8, 22f);
            r.FillRect(searchRect, Color.FromArgb(255, 22, 22, 26));
            r.DrawRect(searchRect, _addCompFilter.Length > 0 ? ColAccent : Color.FromArgb(255, 65, 65, 78));
            r.DrawText(
                _addCompFilter.Length == 0 ? "Search components…" : _addCompFilter + "|",
                new PointF(searchRect.X + 6f, searchRect.Y + 5f),
                _addCompFilter.Length == 0 ? ColTextDim : ColText, 10f);

            r.PushClip(new RectangleF(px, py + 30f, popW, popH - 30f));

            float iy = py + 30f;
            int flatIdx = 0;

            if (searching)
            {
                // Flat filtered list
                foreach (var nm in _compMatches)
                {
                    if (iy + itemH > pop.Bottom) break;
                    var row = new RectangleF(px + 2, iy, popW - 4, itemH);
                    if (flatIdx == _addCompHoverIndex)
                        r.FillRect(row, Color.FromArgb(255, 55, 95, 185));
                    r.DrawText(nm, new PointF(row.X + 10f, iy + 4f), ColText, 10f);
                    iy += itemH; flatIdx++;
                }
            }
            else
            {
                // Grouped by category
                foreach (var (cat, names) in CompCategories)
                {
                    // Only show categories with at least one matching component
                    var visible = new List<string>();
                    foreach (var n in names)
                        if (_compMatches.Contains(n)) visible.Add(n);
                    if (visible.Count == 0) continue;

                    // Category header
                    if (iy + catH <= pop.Bottom)
                    {
                        r.FillRect(new RectangleF(px, iy, popW, catH),
                            Color.FromArgb(255, 44, 44, 52));
                        r.DrawText(cat.ToUpper(), new PointF(px + 6f, iy + 3f),
                            Color.FromArgb(255, 120, 145, 200), 8f);
                        iy += catH;
                    }

                    foreach (var nm in visible)
                    {
                        if (iy + itemH > pop.Bottom) break;
                        var row = new RectangleF(px + 2, iy, popW - 4, itemH);
                        if (flatIdx == _addCompHoverIndex)
                            r.FillRect(row, Color.FromArgb(255, 55, 95, 185));
                        r.DrawText(nm, new PointF(row.X + 14f, iy + 4f), ColText, 10f);
                        iy += itemH; flatIdx++;
                    }
                }
            }

            r.PopClip();
        }

        // ── Drop overlay ──────────────────────────────────────────────────────
        // ── Ref-field picker popup ────────────────────────────────────────────
        // Track which field id has the picker open so we can highlight it correctly
        private string _refPickerFieldId = "";

        private void OpenRefPicker(Type fieldType, Action<object?> setter, string fieldId = "")
        {
            _refPickerType = fieldType;
            _refPickerSetter = setter;
            _refPickerFilter = "";
            _refPickerHover = -1;
            _refPickerFieldId = fieldId;
            _showRefPicker = true;
            BuildRefPickerEntries();
        }

        private void BuildRefPickerEntries()
        {
            _refPickerEntries.Clear();
            if (_refPickerType == null) return;

            bool wantsGO = _refPickerType == typeof(Core.GameObject);
            bool wantsComp = !wantsGO && typeof(Core.Component).IsAssignableFrom(_refPickerType);

            // ── Scene GameObjects ─────────────────────────────────────────────
            if (Scene != null)
            {
                foreach (var go in Scene.All())
                {
                    if (wantsGO)
                    {
                        _refPickerEntries.Add(new RefPickerEntry
                        {
                            Label = go.Name,
                            SubLabel = "(Scene)",
                            GO = go,
                        });
                    }
                    else if (wantsComp)
                    {
                        var comp = go.GetComponentByType(_refPickerType);
                        if (comp != null)
                            _refPickerEntries.Add(new RefPickerEntry
                            {
                                Label = go.Name,
                                SubLabel = $"({_refPickerType.Name})",
                                GO = go,
                                Component = comp,
                            });
                    }
                }
            }

            // ── Prefab files — scan all candidate directories ─────────────────
            // Build a de-duplicated set of directories to search.
            var scanDirs = new System.Collections.Generic.HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            // 1. Explicit AssetsRoot (most reliable — set from Project panel root)
            if (!string.IsNullOrEmpty(AssetsRoot) && System.IO.Directory.Exists(AssetsRoot))
                scanDirs.Add(AssetsRoot);

            // 2. ProjectRoot/Assets fallback
            if (!string.IsNullOrEmpty(ProjectRoot))
            {
                string pa = System.IO.Path.Combine(ProjectRoot, "Assets");
                if (System.IO.Directory.Exists(pa)) scanDirs.Add(pa);
                // 3. ProjectRoot itself in case no Assets subfolder exists
                if (System.IO.Directory.Exists(ProjectRoot)) scanDirs.Add(ProjectRoot);
            }

            foreach (var dir in scanDirs)
            {
                try
                {
                    foreach (var file in System.IO.Directory.GetFiles(
                        dir, "*.prefab", System.IO.SearchOption.AllDirectories))
                    {
                        // Skip duplicates (same file found via two scan dirs)
                        if (_refPickerEntries.Any(e => e.IsPrefab &&
                                string.Equals(e.PrefabPath, file,
                                    StringComparison.OrdinalIgnoreCase)))
                            continue;

                        Core.GameObject? prefabGO = null;
                        try { prefabGO = Core.SceneSerializer.LoadPrefab(file); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Picker] Failed to load prefab '{file}': {ex.Message}");
                            continue;
                        }
                        if (prefabGO == null) continue;

                        if (wantsGO)
                        {
                            _refPickerEntries.Add(new RefPickerEntry
                            {
                                Label = prefabGO.Name,
                                SubLabel = "(Prefab)",
                                IsPrefab = true,
                                PrefabPath = file,
                                GO = prefabGO,
                            });
                        }
                        else if (wantsComp)
                        {
                            // Check the root GO and all its children
                            var comp = FindComponentInHierarchy(prefabGO, _refPickerType);
                            if (comp != null)
                                _refPickerEntries.Add(new RefPickerEntry
                                {
                                    Label = prefabGO.Name,
                                    SubLabel = $"(Prefab • {_refPickerType.Name})",
                                    IsPrefab = true,
                                    PrefabPath = file,
                                    GO = comp.GameObject ?? prefabGO,
                                    Component = comp,
                                });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Picker] Prefab scan error in '{dir}': {ex.Message}");
                }
            }

            Console.WriteLine($"[Picker] Built {_refPickerEntries.Count} entries " +
                              $"(type={_refPickerType.Name}, scanDirs={scanDirs.Count})");
        }

        /// <summary>Searches a GO and all its descendants for a component of the given type.</summary>
        private static Core.Component? FindComponentInHierarchy(Core.GameObject go, Type t)
        {
            var c = go.GetComponentByType(t);
            if (c != null) return c;
            foreach (var child in go.Children)
            {
                c = FindComponentInHierarchy(child, t);
                if (c != null) return c;
            }
            return null;
        }

        private IEnumerable<RefPickerEntry> FilteredPickerEntries()
        {
            if (_refPickerFilter.Length == 0)
            {
                foreach (var e in _refPickerEntries) yield return e;
                yield break;
            }
            string f = _refPickerFilter.ToLower();
            foreach (var e in _refPickerEntries)
                if (e.Label.ToLower().Contains(f) || e.SubLabel.ToLower().Contains(f))
                    yield return e;
        }

        // Called by EditorLayout from the top render layer (above dock overlay)
        public void DrawRefPickerIfOpen(IEditorRenderer r)
        {
            if (!_showRefPicker || _refPickerType == null) return;

            float popW = Math.Max(Bounds.Width - 8f, 240f);
            float itemH = 22f;
            var filtered = FilteredPickerEntries().ToList();
            float popH = Math.Min(34f + filtered.Count * itemH, 340f);

            float px = Bounds.X + 4f;
            float py = Math.Max(Bounds.Y + 4f,
                        Math.Min(Bounds.Bottom - popH - 4f, Bounds.Y + Bounds.Height * 0.3f));

            var pop = new RectangleF(px, py, popW, popH);

            // Dim background
            r.FillRect(new RectangleF(px + 3, py + 3, popW, popH), Color.FromArgb(60, 0, 0, 0));
            r.FillRect(pop, Color.FromArgb(250, 28, 30, 36));
            r.DrawRect(pop, Color.FromArgb(255, 85, 130, 85), 1f);

            // Header
            string typeName = _refPickerType == typeof(Core.GameObject) ? "GameObject" : _refPickerType.Name;
            r.DrawText($"Select {typeName}", new PointF(px + 6f, py + 4f),
                Color.FromArgb(255, 160, 200, 160), 9f);

            // Search bar
            var search = new RectangleF(px + 4, py + 18f, popW - 8, 20f);
            r.FillRect(search, Color.FromArgb(255, 22, 22, 26));
            r.DrawRect(search, _refPickerFilter.Length > 0 ? ColAccent : ColBorder);
            r.DrawText(_refPickerFilter.Length == 0 ? "Search…" : _refPickerFilter + "|",
                new PointF(search.X + 5f, search.Y + 4f),
                _refPickerFilter.Length == 0 ? ColTextDim : ColText, 9f);

            // Items
            r.PushClip(new RectangleF(px, py + 40f, popW, popH - 40f));
            float iy = py + 40f;
            int idx = 0;
            foreach (var entry in filtered)
            {
                if (iy + itemH > pop.Bottom) break;
                var row = new RectangleF(px, iy, popW, itemH);
                if (idx == _refPickerHover)
                    r.FillRect(row, Color.FromArgb(255, 45, 95, 55));
                else if ((idx & 1) == 1)
                    r.FillRect(row, Color.FromArgb(12, 255, 255, 255));

                // Icon badge
                Color badge = entry.IsPrefab
                    ? Color.FromArgb(255, 30, 90, 140)
                    : Color.FromArgb(255, 38, 80, 58);
                r.FillRect(new RectangleF(px + 3f, iy + 3f, 6f, itemH - 6f), badge);

                r.DrawText(entry.Label, new PointF(px + 14f, iy + 5f), ColText, 10f);
                r.DrawText(entry.SubLabel,
                    new PointF(px + popW - entry.SubLabel.Length * 5.5f - 6f, iy + 6f),
                    Color.FromArgb(180, 130, 155, 130), 8f);
                iy += itemH; idx++;
            }
            if (filtered.Count == 0)
                r.DrawText("No matching objects found.",
                    new PointF(px + 10f, py + 46f), ColTextDim, 9f);
            r.PopClip();
        }

        private void HandleRefPickerClick(PointF pos)
        {
            float popW = Math.Max(Bounds.Width - 8f, 240f);
            float itemH = 22f;
            var filtered = FilteredPickerEntries().ToList();
            float popH = Math.Min(34f + filtered.Count * itemH, 340f);
            float px = Bounds.X + 4f;
            float py = Math.Max(Bounds.Y + 4f,
                         Math.Min(Bounds.Bottom - popH - 4f, Bounds.Y + Bounds.Height * 0.3f));
            var pop = new RectangleF(px, py, popW, popH);

            if (!pop.Contains(pos)) { _showRefPicker = false; return; }

            // Search bar click — absorb, keyboard handles input
            if (new RectangleF(px + 4, py + 18f, popW - 8, 20f).Contains(pos)) return;

            float iy = py + 40f;
            foreach (var entry in filtered)
            {
                if (iy + itemH > pop.Bottom) break;
                if (new RectangleF(px, iy, popW, itemH).Contains(pos))
                {
                    // Resolve the value to assign
                    object? resolved = null;
                    if (_refPickerType == typeof(Core.GameObject))
                        resolved = entry.GO;
                    else if (entry.Component != null)
                    {
                        // If prefab, the component is on an instantiated temporary GO —
                        // for scene entries, use the live component directly.
                        resolved = entry.IsPrefab
                            ? InstantiatePrefabAndGetComp(entry)
                            : entry.Component;
                    }
                    if (resolved != null) _refPickerSetter?.Invoke(resolved);
                    _showRefPicker = false;
                    return;
                }
                iy += itemH;
            }
        }

        private object? InstantiatePrefabAndGetComp(RefPickerEntry entry)
        {
            // Instantiate the prefab into the scene so the component lives there
            if (Scene == null || string.IsNullOrEmpty(entry.PrefabPath)) return null;
            var go = Core.SceneSerializer.LoadPrefab(entry.PrefabPath);
            if (go == null) return null;
            Scene.AddGameObject(go);
            return _refPickerType == typeof(Core.GameObject)
                ? (object)go
                : go.GetComponentByType(_refPickerType!);
        }

        private void UpdateRefPickerHover(PointF pos)
        {
            if (!_showRefPicker) return;
            float popW = Math.Max(Bounds.Width - 8f, 240f);
            float itemH = 22f;
            var filtered = FilteredPickerEntries().ToList();
            float popH = Math.Min(34f + filtered.Count * itemH, 340f);
            float px = Bounds.X + 4f;
            float py = Math.Max(Bounds.Y + 4f,
                          Math.Min(Bounds.Bottom - popH - 4f, Bounds.Y + Bounds.Height * 0.3f));
            float iy = py + 40f;
            _refPickerHover = -1;
            int idx = 0;
            foreach (var entry in filtered)
            {
                if (iy + itemH > py + popH) break;
                if (new RectangleF(px, iy, popW, itemH).Contains(pos))
                { _refPickerHover = idx; break; }
                iy += itemH; idx++;
            }
        }

        private void DrawDropOverlay(IEditorRenderer r, RectangleF cr)
        {
            r.DrawRect(cr, Color.FromArgb(210, 55, 195, 55), 2f);
            r.DrawText("Drop script to add Component",
                new PointF(cr.X + cr.Width / 2f - 92f, cr.Y + cr.Height / 2f),
                Color.FromArgb(255, 55, 195, 55), 12f);
        }

        // ── Generic field dispatcher ───────────────────────────────────────────
        /// Returns the default value for a type (0, false, "", Vector3.Zero etc.)
        private static object? GetDefault(Type t)
        {
            if (t == typeof(string)) return "";
            if (t == typeof(bool)) return false;
            if (t == typeof(float)) return 0f;
            if (t == typeof(double)) return 0.0;
            if (t == typeof(int)) return 0;
            if (t == typeof(OpenTK.Mathematics.Vector2)) return OpenTK.Mathematics.Vector2.Zero;
            if (t == typeof(OpenTK.Mathematics.Vector3)) return OpenTK.Mathematics.Vector3.Zero;
            if (t == typeof(OpenTK.Mathematics.Vector4)) return OpenTK.Mathematics.Vector4.Zero;
            if (t.IsValueType) return Activator.CreateInstance(t);
            return null;
        }

        /// Look up the compiled type for a script name.
        /// Prefers the latest UserAssembly (guaranteed fresh from last compile)
        /// over the registry dict, which may hold a stale type from an older context.
        private static Type? GetLatestScriptType(string typeName)
        {
            // 1. Scan the freshest user assembly directly
            var asm = Core.ComponentRegistry.UserAssembly;
            if (asm != null)
            {
                try
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        if (t.IsAbstract) continue;
                        if (t.Name != typeName && t.FullName != typeName) continue;
                        if (typeof(Core.Component).IsAssignableFrom(t)) return t;
                    }
                }
                catch { }
            }

            // 2. Fall back to registry dict (catches built-in + first-compile types)
            return Core.ComponentRegistry.TryGetType(typeName);
        }

        /// Ensures a stored value is the right boxed type for DrawAnyField's pattern matches.
        /// e.g. a stored boxed int won't match "value is float fv", so we convert it.
        private static object? CoerceToType(object? value, Type target)
        {
            if (value == null) return null;
            if (target.IsInstanceOfType(value)) return value;
            try { return Convert.ChangeType(value, target); } catch { return null; }
        }

        private void DrawAnyField(IEditorRenderer r, RectangleF cr, string label,
            object? value, Type type, string id, ref float y, Action<object?> setter)
        {
            if (type == typeof(bool) && value is bool bv)
            { DrawBoolField(r, cr, label, bv, ref y, id, v => setter(v)); return; }

            if (type == typeof(Vector3) && value is Vector3 v3)
            { DrawVec3Field(r, cr, label, v3, ref y, id, v => setter(v)); return; }

            if (type == typeof(float) && value is float fv)
            { DrawFloatField(r, cr, label, fv, ref y, id, v => setter(v)); return; }

            if (type == typeof(int) && value is int iv)
            { DrawIntField(r, cr, label, iv, ref y, id, v => setter(v)); return; }

            if (type == typeof(Color4))
            { DrawColorField(r, cr, label, value is Color4 c4 ? c4 : Color4.White, ref y, id); return; }

            if (type == typeof(string))
            { DrawStringField(r, cr, label, value as string ?? "", ref y, id, v => setter(v)); return; }

            // ── GameObject reference field ─────────────────────────────────────
            if (type == typeof(Core.GameObject))
            { DrawObjectRefField(r, cr, label, value as Core.GameObject, ref y, id, v => setter(v), typeof(Core.GameObject)); return; }

            // ── Component reference field ─────────────────────────────────────
            if (typeof(Core.Component).IsAssignableFrom(type))
            { DrawObjectRefField(r, cr, label, value as Core.Component, ref y, id, v => setter(v), type); return; }

            // Fallback – display as read-only text
            DrawLabelRow(r, cr, label, value?.ToString() ?? "null", ref y);
        }

        private void DrawObjectRefField(IEditorRenderer r, RectangleF cr, string label,
            object? value, ref float y, string id, Action<object?> setter, Type fieldType)
        {
            DrawLabel(r, cr, label, y);
            float fw = cr.Width - LW - PAD;

            // Right edge: small ⊙ picker button
            const float BtnW = 18f;
            var fr = new RectangleF(cr.X + LW, y + 2f, fw - BtnW - 1f, FH - 4f);
            var btn = new RectangleF(fr.Right + 1f, y + 2f, BtnW, FH - 4f);

            bool isHovering = HoveredDropFieldId == id;
            bool isPickerOpen = _showRefPicker && _refPickerFieldId == id;
            Color bg = isHovering ? Color.FromArgb(255, 40, 80, 140)
                     : isPickerOpen ? Color.FromArgb(255, 35, 68, 35)
                     : Color.FromArgb(255, 38, 38, 44);
            r.FillRect(fr, bg);
            r.DrawRect(fr, isHovering ? Color.FromArgb(255, 80, 160, 255)
                         : isPickerOpen ? Color.FromArgb(255, 80, 200, 100)
                         : ColBorder);

            r.FillRect(btn, Color.FromArgb(255, 48, 52, 62));
            r.DrawRect(btn, ColBorder);
            r.DrawText("⊙", new PointF(btn.X + 3f, btn.Y + 2f),
                Color.FromArgb(200, 160, 195, 255), 9f);

            string typeName = fieldType == typeof(Core.GameObject) ? "GameObject" : fieldType.Name;
            string display = value == null
                ? $"None ({typeName})"
                : value is Core.GameObject gv ? gv.Name
                : value is Core.Component cv ? $"{cv.GameObject?.Name ?? "?"} ({cv.GetType().Name})"
                : value.ToString() ?? "?";
            r.DrawText(display, new PointF(fr.X + 4f, fr.Y + 3f),
                value == null ? Color.FromArgb(160, 140, 140, 150)
                              : Color.FromArgb(255, 200, 220, 255), 9f);

            // Both field and button open the picker — pass id so isPickerOpen works
            string capturedId = id;
            Action openPicker = () => OpenRefPicker(fieldType, setter, capturedId);
            RegisterClick(fr, "pickfield_" + id, openPicker);
            RegisterClick(btn, "pickbtn_" + id, openPicker);

            _fields.Add(new FieldRecord(fr, id, value, fieldType, nv => setter(nv)));
            y += FH; ContentHeight += FH;
        }

        // ── Individual field drawers ───────────────────────────────────────────
        private void DrawVec3Field(IEditorRenderer r, RectangleF cr, string label,
            Vector3 v, ref float y, string idPfx, Action<Vector3> setter)
        {
            DrawLabel(r, cr, label, y);
            float fw = (cr.Width - LW - PAD) / 3f;

            string[] axes = { "X", "Y", "Z" };
            float[] vals = { v.X, v.Y, v.Z };
            Color[] axCol = {
                Color.FromArgb(255, 185, 48, 48),
                Color.FromArgb(255, 48, 165, 48),
                Color.FromArgb(255, 48, 105, 215)};

            for (int i = 0; i < 3; i++)
            {
                string eid = idPfx + axes[i];
                float fx = cr.X + LW + i * fw;
                var fr = new RectangleF(fx, y + 1f, fw - 2f, FH - 2f);
                bool ed = _editId == eid;
                bool drg = _dragId == eid;

                r.FillRect(fr, ed || drg ? Color.FromArgb(255, 36, 56, 90) : Color.FromArgb(255, 34, 34, 34));
                r.DrawRect(fr, ed ? ColAccent : drg ? Color.FromArgb(255, 80, 140, 80) : ColBorder);
                r.FillRect(new RectangleF(fx, y + 1f, 10f, FH - 2f), axCol[i]);
                r.DrawText(axes[i], new PointF(fx + 1f, y + 4f), Color.White, 8f);

                string disp = ed ? _editBuf + "|"
                            : drg ? $"{vals[i]:F3}*"
                            : $"{vals[i]:F3}";
                r.DrawText(disp, new PointF(fx + 12f, y + 4f), ColText, 9f);

                // Capture loop index in a local — without this every closure shares
                // the same 'i' variable which equals 3 after the loop, meaning every
                // axis setter always hits the Z branch (the reported X→Z bug).
                int ci = i;
                float[] snap = { v.X, v.Y, v.Z };  // snapshot current values
                _fields.Add(new FieldRecord(fr, eid, vals[ci], typeof(float), obj =>
                {
                    float nv = obj is float f ? f : snap[ci];
                    setter(ci == 0 ? new Vector3(nv, snap[1], snap[2])
                         : ci == 1 ? new Vector3(snap[0], nv, snap[2])
                                   : new Vector3(snap[0], snap[1], nv));
                }));
            }
            y += FH; ContentHeight += FH;
        }

        private void DrawFloatField(IEditorRenderer r, RectangleF cr, string label,
            float value, ref float y, string id, Action<float> setter)
        {
            DrawLabel(r, cr, label, y);
            bool ed = _editId == id;
            bool drg = _dragId == id;
            var fr = new RectangleF(cr.X + LW, y + 1f, cr.Width - LW - PAD, FH - 2f);

            r.FillRect(fr, ed || drg ? Color.FromArgb(255, 36, 56, 90) : Color.FromArgb(255, 34, 34, 34));
            r.DrawRect(fr, ed ? ColAccent : ColBorder);
            string disp = ed ? _editBuf + "|" : $"{value:F3}";
            r.DrawText(disp, new PointF(fr.X + 4f, y + 4f), ColText, 10f);

            _fields.Add(new FieldRecord(fr, id, value, typeof(float),
                obj => setter(obj is float f ? f : value)));
            y += FH; ContentHeight += FH;
        }

        private void DrawIntField(IEditorRenderer r, RectangleF cr, string label,
            int value, ref float y, string id, Action<int> setter)
        {
            DrawLabel(r, cr, label, y);
            bool ed = _editId == id;
            var fr = new RectangleF(cr.X + LW, y + 1f, cr.Width - LW - PAD, FH - 2f);

            r.FillRect(fr, ed ? Color.FromArgb(255, 36, 56, 90) : Color.FromArgb(255, 34, 34, 34));
            r.DrawRect(fr, ed ? ColAccent : ColBorder);
            string disp = ed ? _editBuf + "|" : value.ToString();
            r.DrawText(disp, new PointF(fr.X + 4f, y + 4f), ColText, 10f);

            _fields.Add(new FieldRecord(fr, id, value, typeof(int),
                obj => setter(obj is int i ? i : value)));
            y += FH; ContentHeight += FH;
        }

        private void DrawStringField(IEditorRenderer r, RectangleF cr, string label,
            string value, ref float y, string id, Action<string> setter)
        {
            DrawLabel(r, cr, label, y);
            bool ed = _editId == id;
            var fr = new RectangleF(cr.X + LW, y + 1f, cr.Width - LW - PAD, FH - 2f);

            r.FillRect(fr, ed ? Color.FromArgb(255, 36, 56, 90) : Color.FromArgb(255, 34, 34, 34));
            r.DrawRect(fr, ed ? ColAccent : ColBorder);
            string disp = ed ? _editBuf + "|" : value;
            r.DrawText(disp, new PointF(fr.X + 4f, y + 4f), ColText, 10f);

            _fields.Add(new FieldRecord(fr, id, value, typeof(string),
                obj => setter(obj as string ?? value)));
            y += FH; ContentHeight += FH;
        }

        private void DrawBoolField(IEditorRenderer r, RectangleF cr, string label,
            bool value, ref float y, string id, Action<bool> setter)
        {
            DrawLabel(r, cr, label, y);
            var cb = new RectangleF(cr.X + LW + 2f, y + 3f, 14f, 14f);
            r.FillRect(cb, value ? Color.FromArgb(255, 55, 155, 55) : Color.FromArgb(255, 48, 48, 48));
            r.DrawRect(cb, ColBorder);
            if (value) r.DrawText("ok", new PointF(cb.X + 1f, cb.Y + 2f), Color.White, 8f);

            _fields.Add(new FieldRecord(cb, id, value, typeof(bool),
                obj => setter(obj is bool b && b)));
            y += FH; ContentHeight += FH;
        }

        /// <summary>Compact labelled checkbox drawn inline (no newline advance).</summary>
        private void DrawInlineBool(IEditorRenderer r, RectangleF cr, string label,
            bool value, ref float cx, float y, string id, Action<bool> setter)
        {
            float lw = label.Length * 5.5f + 4f;
            r.DrawText(label, new PointF(cx + 2f, y + 3f), ColLabel, 8f);
            cx += lw;
            var cb = new RectangleF(cx, y + 3f, 13f, 13f);
            r.FillRect(cb, value ? Color.FromArgb(255, 55, 120, 200) : Color.FromArgb(255, 44, 44, 44));
            r.DrawRect(cb, ColBorder);
            if (value) r.DrawText("✓", new PointF(cb.X + 1f, cb.Y + 1f), Color.White, 8f);
            _fields.Add(new FieldRecord(cb, id, value, typeof(bool),
                obj => setter(obj is bool b && b)));
            cx += 18f;
        }

        private void DrawColorField(IEditorRenderer r, RectangleF cr, string label,
            Color4 value, ref float y, string id)
        {
            DrawLabel(r, cr, label, y);
            var swatch = new RectangleF(cr.X + LW, y + 2f, cr.Width - LW - PAD, FH - 4f);
            r.FillRect(swatch, Color.FromArgb(
                (int)(value.A * 255), (int)(value.R * 255),
                (int)(value.G * 255), (int)(value.B * 255)));
            r.DrawRect(swatch, ColBorder);
            // Register as clickable
            _fields.Add(new FieldRecord(swatch, "color_swatch_" + id,
                value, typeof(Color4), _ => { }));
            y += FH; ContentHeight += FH;
        }

        // Full RGB + hue-bar color picker drawn inline below the swatch
        private void DrawColorPicker(IEditorRenderer r, RectangleF cr, ref float y)
        {
            float ph = 160f;
            var bg = new RectangleF(cr.X + 2f, y, cr.Width - 4f, ph);
            r.FillRect(bg, Color.FromArgb(255, 30, 30, 35));
            r.DrawRect(bg, ColBorder);

            float px = bg.X + 8f;
            float pw = bg.Width - 16f;
            float iy = bg.Y + 8f;

            // ── Hue bar (22 segments) ─────────────────────────────────────────
            int hsegs = 22;
            float sw = pw / hsegs;
            for (int i = 0; i < hsegs; i++)
            {
                float hue = (float)i / hsegs;
                HsvToRgb(hue, 1f, 1f, out float hr, out float hg, out float hb);
                var seg = new RectangleF(px + i * sw, iy, sw + 0.5f, 16f);
                r.FillRect(seg, Color.FromArgb(255, (int)(hr * 255), (int)(hg * 255), (int)(hb * 255)));

                // Register hue click
                _fields.Add(new FieldRecord(seg, $"cp_hue_{i}_{_colorPickerId}",
                    hue, typeof(float), hv =>
                    {
                        // Apply selected hue while keeping current S/V from current RGB
                        RgbToHsv(_cpR, _cpG, _cpB, out float _, out float s, out float v);
                        HsvToRgb((float)hv, s, v, out float nr, out float ng, out float nb);
                        _cpR = nr; _cpG = ng; _cpB = nb;
                        _cpApply?.Invoke(_cpR, _cpG, _cpB);
                    }));
            }
            // Marker on current hue
            RgbToHsv(_cpR, _cpG, _cpB, out float curH, out float _, out float _2);
            float mx = px + curH * pw;
            r.DrawLine(new PointF(mx, iy), new PointF(mx, iy + 16f),
                       Color.White, 2f);
            iy += 22f;

            // ── S/V gradient (5×5 grid approximation) ─────────────────────────
            RgbToHsv(_cpR, _cpG, _cpB, out float curH2, out float curS, out float curV);
            int svN = 5;
            float svW = pw / svN, svH2 = 32f / svN;
            for (int si = 0; si < svN; si++)
                for (int vi = 0; vi < svN; vi++)
                {
                    float s = 1f - (float)si / (svN - 1);
                    float v = (float)vi / (svN - 1);
                    HsvToRgb(curH2, s, v, out float cr2, out float cg2, out float cb2);
                    var cell = new RectangleF(px + vi * svW, iy + si * svH2, svW + 0.5f, svH2 + 0.5f);
                    r.FillRect(cell, Color.FromArgb(255, (int)(cr2 * 255), (int)(cg2 * 255), (int)(cb2 * 255)));
                    float fs = s, fv = v;
                    _fields.Add(new FieldRecord(cell, $"cp_sv_{si}_{vi}_{_colorPickerId}",
                        0f, typeof(float), _ =>
                        {
                            HsvToRgb(curH2, fs, fv, out float nr, out float ng, out float nb);
                            _cpR = nr; _cpG = ng; _cpB = nb;
                            _cpApply?.Invoke(_cpR, _cpG, _cpB);
                        }));
                }
            iy += 34f + 2f;

            // ── R / G / B sliders ─────────────────────────────────────────────
            DrawColorSlider(r, px, pw, ref iy, "R", _cpR, Color.FromArgb(255, 220, 60, 60),
                v => { _cpR = v; _cpApply?.Invoke(_cpR, _cpG, _cpB); }, $"cp_r_{_colorPickerId}");
            DrawColorSlider(r, px, pw, ref iy, "G", _cpG, Color.FromArgb(255, 60, 200, 80),
                v => { _cpG = v; _cpApply?.Invoke(_cpR, _cpG, _cpB); }, $"cp_g_{_colorPickerId}");
            DrawColorSlider(r, px, pw, ref iy, "B", _cpB, Color.FromArgb(255, 80, 130, 255),
                v => { _cpB = v; _cpApply?.Invoke(_cpR, _cpG, _cpB); }, $"cp_b_{_colorPickerId}");

            // ── Preview swatch ────────────────────────────────────────────────
            var prev = new RectangleF(px, iy, pw, 14f);
            r.FillRect(prev, Color.FromArgb(255, (int)(_cpR * 255), (int)(_cpG * 255), (int)(_cpB * 255)));
            r.DrawRect(prev, ColBorder);

            y += ph; ContentHeight += ph;
        }

        private void DrawColorSlider(IEditorRenderer r, float x, float w, ref float y,
            string label, float val, Color trackColor, Action<float> setter, string id)
        {
            r.DrawText(label, new PointF(x, y + 2f), Color.FromArgb(200, 200, 200, 200), 9f);
            var track = new RectangleF(x + 14f, y + 2f, w - 14f, 11f);
            r.FillRect(track, Color.FromArgb(255, 45, 45, 50));
            r.FillRect(new RectangleF(track.X, track.Y, track.Width * val, track.Height), trackColor);
            r.DrawRect(track, ColBorder);
            // Thumb
            float tx = track.X + track.Width * val - 3f;
            r.FillRect(new RectangleF(tx, track.Y - 1f, 6f, track.Height + 2f), Color.White);
            // Register drag
            _fields.Add(new FieldRecord(track, id, val, typeof(float),
                _ => { })); // drag handled via _dragSetter in float path
            // We piggyback on the float drag system by adding a proper record:
            _fields[_fields.Count - 1] = new FieldRecord(track, id, val, typeof(float),
                nv => setter(Math.Clamp((float)(nv is float fv2 ? fv2 : 0f), 0f, 1f)));
            y += 14f;
        }

        private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
        {
            if (s == 0) { r = g = b = v; return; }
            float h6 = h * 6f;
            int i = (int)h6 % 6;
            float f = h6 - MathF.Floor(h6);
            float p = v * (1 - s), q = v * (1 - s * f), t2 = v * (1 - s * (1 - f));
            switch (i)
            {
                case 0: r = v; g = t2; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t2; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t2; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
        }
        private static void RgbToHsv(float r, float g, float b,
            out float h, out float s, out float v)
        {
            float max = MathF.Max(r, MathF.Max(g, b));
            float min = MathF.Min(r, MathF.Min(g, b));
            v = max; float d = max - min;
            s = max == 0 ? 0 : d / max;
            if (d == 0) { h = 0; return; }
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6f;
        }

        private void DrawLabelRow(IEditorRenderer r, RectangleF cr, string label,
            string display, ref float y)
        {
            DrawLabel(r, cr, label, y);
            r.DrawText(display, new PointF(cr.X + LW + 4f, y + 4f), ColTextDim, 10f);
            y += FH; ContentHeight += FH;
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        /// <summary>
        /// Draws a component section header with collapse toggle, drag handle, and optional remove button.
        /// Returns true if the section is expanded (content should be drawn).
        /// </summary>
        private bool DrawSectionHeader(IEditorRenderer r, RectangleF cr,
            string title, Action? onRemove, ref float y, Component? comp = null)
        {
            string colKey = title + (comp?.GetHashCode().ToString() ?? "");
            if (!_compExpanded.ContainsKey(colKey)) _compExpanded[colKey] = true;
            bool expanded = _compExpanded[colKey];

            var row = new RectangleF(cr.X, y, cr.Width, SH);

            // Background — slightly lighter when hovered for drag affordance
            bool headerHovered = row.Contains(_mouse);
            r.FillRect(row, headerHovered && comp != null
                ? Color.FromArgb(255, 58, 58, 68)
                : Color.FromArgb(255, 46, 46, 52));

            // Collapse arrow
            r.DrawText(expanded ? "▼" : "▶",
                new PointF(cr.X + PAD, y + 5f),
                Color.FromArgb(255, 130, 150, 200), 9f);

            // Title
            r.DrawText(title, new PointF(cr.X + PAD + 14f, y + 5f),
                Color.FromArgb(255, 195, 210, 255), 11f);

            // Component drag handle (≡) — only for removable components
            if (comp != null)
            {
                var drag = new RectangleF(cr.X + 2f, y + 4f, 12f, SH - 8f);
                r.DrawText("≡", new PointF(drag.X + 1f, drag.Y + 1f),
                    Color.FromArgb(120, 200, 200, 220), 9f);

                // Store candidate for drag start (checked in OnMouseDown)
                _fields.Add(new FieldRecord(drag, "drag_" + colKey, comp,
                    comp.GetType(), _ => { }));
            }

            // Remove button (X)
            if (onRemove != null)
            {
                var rm = new RectangleF(cr.Right - 20f, y + 3f, 16f, 16f);
                r.FillRect(rm, Color.FromArgb(255, 130, 38, 38));
                r.DrawRect(rm, ColBorder);
                r.DrawText("X", new PointF(rm.X + 3f, rm.Y + 3f), Color.White, 9f);
                _fields.Add(new FieldRecord(rm, "rm_" + colKey,
                    true, typeof(bool), _ => onRemove()));
            }

            // Clicking the header row (not the X, not the drag handle) toggles collapse
            RegisterClick(new RectangleF(cr.X + 16f, y, cr.Width - 36f, SH),
                "col_" + colKey, () => _compExpanded[colKey] = !expanded);

            y += SH; ContentHeight += SH;
            return expanded;
        }

        private static void DrawSeparator(IEditorRenderer r, RectangleF cr, ref float y)
        {
            r.DrawLine(new PointF(cr.X, y + 1), new PointF(cr.Right, y + 1),
                Color.FromArgb(255, 50, 50, 50));
            y += 3f;
        }

        private static void DrawLabel(IEditorRenderer r, RectangleF cr, string label, float y)
        {
            r.DrawText(label, new PointF(cr.X + PAD, y + 4f),
                Color.FromArgb(255, 175, 175, 175), 10f);
        }

        // ── Input ──────────────────────────────────────────────────────────────
        public override void OnMouseDown(MouseButtonEventArgs e, PointF pos)
        {
            if (!IsVisible || !Bounds.Contains(pos))
            {
                if (_showRefPicker) _showRefPicker = false;
                return;
            }
            IsFocused = true;

            // ── Ref-picker popup — handle first ───────────────────────────────
            if (_showRefPicker && e.Button == MouseButton.Left)
            {
                HandleRefPickerClick(pos);
                return;
            }

            // ── Drop ActiveDragComponent onto a ref field in this panel ────────
            if (e.Button == MouseButton.Left && ActiveDragComponent != null)
            {
                foreach (var f in _fields)
                {
                    if (!f.Bounds.Contains(pos)) continue;
                    if (!typeof(Core.Component).IsAssignableFrom(f.FieldType)) continue;
                    if (f.FieldType.IsAssignableFrom(ActiveDragComponent.GetType()))
                        f.Setter(ActiveDragComponent);
                    ActiveDragComponent = null; _compDragging = false; _compDragCandidate = null;
                    return;
                }
            }

            // ── Detect drag-handle click on a component header ─────────────────
            if (e.Button == MouseButton.Left)
            {
                foreach (var f in _fields)
                {
                    if (f.Id.StartsWith("drag_") && f.Bounds.Contains(pos) && f.Value is Core.Component dc)
                    {
                        _compDragCandidate = dc;
                        _compDragStart = pos;
                        _compDragging = false;
                        ActiveDragComponent = null;
                        return;
                    }
                }
            }

            // ── Registered one-shot click areas (Edit Collider button, etc.) ──
            if (e.Button == MouseButton.Left)
            {
                foreach (var kv in _clickMap)
                {
                    if (kv.Value.Rect.Contains(pos))
                    { kv.Value.Action(); return; }
                }
            }

            // ── Tag dropdown selection ─────────────────────────────────────────
            if (_showTagDropdown && _target != null)
            {
                var tl = Core.TagsAndLayers.Instance;
                for (int i = 0; i < tl.Tags.Count; i++)
                {
                    var ir = new RectangleF(_tagDropRect.X, _tagDropRect.Y + 2f + i * 18f,
                                            _tagDropRect.Width, 17f);
                    if (ir.Contains(pos)) { _target.Tag = tl.Tags[i]; _showTagDropdown = false; return; }
                }
                _showTagDropdown = false; return;
            }
            // ── Layer dropdown selection ───────────────────────────────────────
            if (_showLayerDropdown && _target != null)
            {
                var tl = Core.TagsAndLayers.Instance;
                for (int i = 0; i < tl.Layers.Count; i++)
                {
                    var ir = new RectangleF(_layerDropRect.X, _layerDropRect.Y + 2f + i * 18f,
                                            _layerDropRect.Width, 17f);
                    if (ir.Contains(pos)) { _target.Layer = tl.Layers[i]; _showLayerDropdown = false; return; }
                }
                _showLayerDropdown = false; return;
            }
            // Toggle tag/layer dropdowns via header buttons
            if (_target != null && e.Button == MouseButton.Left)
            {
                if (_tagBtnRect.Contains(pos))
                { _showTagDropdown = !_showTagDropdown; _showLayerDropdown = false; return; }
                if (_layerBtnRect.Contains(pos))
                { _showLayerDropdown = !_showLayerDropdown; _showTagDropdown = false; return; }
            }

            // Add component popup
            if (_showAddComp)
            {
                _compMatches.Clear();
                foreach (var name in AllComponents)
                    if (_addCompFilter.Length == 0 || name.ToLower().Contains(_addCompFilter.ToLower()))
                        _compMatches.Add(name);
                bool searching = _addCompFilter.Length > 0;
                float itemH = 20f, catH = 16f;
                float popW = Math.Max(Bounds.Width - PAD * 2, 220f);
                float popH = Math.Min(32f + _compMatches.Count * itemH +
                    (searching ? 0 : CompCategories.Length * catH), 320f);
                float ppx = Bounds.X + PAD;
                float pyD = _addCompRect.Bottom + 2f;
                float ppy = (pyD + popH <= Bounds.Bottom) ? pyD : _addCompRect.Y - popH - 2f;
                ppy = Math.Max(Bounds.Y + 2f, Math.Min(ppy, Bounds.Bottom - popH - 2f));
                var pop = new RectangleF(ppx, ppy, popW, popH);
                if (pop.Contains(pos))
                {
                    // Search bar click — just let keyboard input handle it
                    var searchRect = new RectangleF(ppx + 4, ppy + 4, popW - 8, 22f);
                    if (!searchRect.Contains(pos))
                        HandleAddCompClick(pos, pop);
                    return;
                }
                _showAddComp = false;
                _addCompFilter = "";
                return;
            }

            // Commit any active text edit
            if (_editId != null && e.Button == MouseButton.Left)
            {
                bool clickedSameField = false;
                foreach (var f in _fields)
                    if (f.Id == _editId && f.Bounds.Contains(pos)) { clickedSameField = true; break; }
                if (!clickedSameField) { CommitEdit(); }
            }

            if (e.Button == MouseButton.Left)
            {
                // Check field records
                foreach (var field in _fields)
                {
                    if (!field.Bounds.Contains(pos)) continue;

                    if (field.FieldType == typeof(bool))
                    {
                        bool cur = field.Value is bool b && b;
                        field.Setter(!cur);
                        return;
                    }

                    // Color swatch click — open / close inline picker
                    if (field.Id.StartsWith("color_swatch_"))
                    {
                        string gid = field.Id["color_swatch_".Length..];
                        if (_colorPickerId == gid)
                        { _colorPickerId = null; return; } // close

                        if (field.Value is Color4 c4)
                        {
                            _colorPickerId = gid;
                            _cpR = c4.R; _cpG = c4.G; _cpB = c4.B;
                            // The setter stored in the color field record doesn't carry the RGB—
                            // we need to find the original field in the component to write back.
                            // We stash _cpApply via the field record setter chain.
                            _cpApply = (r2, g2, b2) => field.Setter(new Color4(r2, g2, b2, 1f));
                        }
                        return;
                    }

                    if (field.FieldType == typeof(float) || field.Id.Contains("_f_") || field.Id.Contains("t_"))
                    {
                        // Start drag for float
                        if (_editId == field.Id)
                        {
                            // Already editing text – don't switch to drag
                        }
                        else
                        {
                            _dragId = field.Id;
                            _dragStartX = pos.X;
                            _dragStartVal = field.Value is float fv ? fv : 0f;
                            _dragSetter = v => field.Setter(v);
                        }
                        return;
                    }

                    if (field.FieldType == typeof(string))
                    {
                        StartEdit(field.Id, field.Value as string ?? "",
                            s => field.Setter(s));
                        return;
                    }

                    if (field.FieldType == typeof(int))
                    {
                        StartEdit(field.Id, field.Value?.ToString() ?? "0",
                            s => { if (int.TryParse(s, out int iv)) field.Setter(iv); });
                        return;
                    }
                    return;
                }

                // Check Add Component button
                if (_addCompRect.Contains(pos))
                {
                    _showAddComp = !_showAddComp;
                    _addCompFilter = "";
                    return;
                }

                // Particle System inspector interactions
                if (_target != null)
                {
                    var ps = _target.GetComponent<Core.ParticleSystem>();
                    if (ps != null && _psInspector.HandleClick(pos, ps)) return;
                }
            }

            base.OnMouseDown(e, pos);
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            _dragId = null; _dragSetter = null;
            _compDragCandidate = null;
            if (_compDragging) { _compDragging = false; ActiveDragComponent = null; }
            _psInspector.EndDrag();
            base.OnMouseUp(e, pos);
        }

        public override void OnMouseMove(PointF pos)
        {
            _mouse = pos;
            base.OnMouseMove(pos);

            // Component header drag — start once threshold exceeded
            if (_compDragCandidate != null && !_compDragging)
            {
                float d = MathF.Sqrt(MathF.Pow(pos.X - _compDragStart.X, 2) +
                                     MathF.Pow(pos.Y - _compDragStart.Y, 2));
                if (d > 8f) { _compDragging = true; ActiveDragComponent = _compDragCandidate; }
            }
            UpdateRefPickerHover(pos);
            // Float field drag
            if (_dragId != null && _dragSetter != null)
            {
                float delta = (pos.X - _dragStartX) * 0.02f;
                _dragSetter(_dragStartVal + delta);
            }
            // Particle system slider drag
            if (_psInspector.IsDragging && _target != null)
            {
                var ps = _target.GetComponent<Core.ParticleSystem>();
                if (ps != null) _psInspector.UpdateDrag(pos.X, ps);
            }
            // Update tag/layer dropdown hover indices
            if (_showTagDropdown && _target != null)
            {
                var tl = Core.TagsAndLayers.Instance;
                _tagDropHov = -1;
                for (int i = 0; i < tl.Tags.Count; i++)
                {
                    var ir = new RectangleF(_tagDropRect.X, _tagDropRect.Y + 2f + i * 18f,
                                            _tagDropRect.Width, 17f);
                    if (ir.Contains(pos)) { _tagDropHov = i; break; }
                }
            }
            if (_showLayerDropdown && _target != null)
            {
                var tl = Core.TagsAndLayers.Instance;
                _layerDropHov = -1;
                for (int i = 0; i < tl.Layers.Count; i++)
                {
                    var ir = new RectangleF(_layerDropRect.X, _layerDropRect.Y + 2f + i * 18f,
                                            _layerDropRect.Width, 17f);
                    if (ir.Contains(pos)) { _layerDropHov = i; break; }
                }
            }

            // AddComponent popup hover tracking
            if (_showAddComp)
            {
                _addCompHoverIndex = -1;
                bool searching = _addCompFilter.Length > 0;
                float itemH = 20f, catH = 16f;
                // Reconstruct popup Y (same logic as DrawAddCompPopup)
                float popW = Math.Max(Bounds.Width - PAD * 2, 220f);
                float popH = Math.Min(32f + _compMatches.Count * itemH +
                    (searching ? 0 : CompCategories.Length * catH), 320f);
                float ppx = Bounds.X + PAD;
                float pyD = _addCompRect.Bottom + 2f;
                float ppy = (pyD + popH <= Bounds.Bottom) ? pyD : _addCompRect.Y - popH - 2f;
                ppy = Math.Max(Bounds.Y + 2f, Math.Min(ppy, Bounds.Bottom - popH - 2f));
                float iy = ppy + 30f;
                int idx = 0;
                if (searching)
                {
                    foreach (var nm in _compMatches)
                    {
                        var row = new RectangleF(ppx + 2, iy, popW - 4, itemH);
                        if (row.Contains(pos)) { _addCompHoverIndex = idx; break; }
                        iy += itemH; idx++;
                    }
                }
                else
                {
                    foreach (var (_, names) in CompCategories)
                    {
                        var visible = new List<string>();
                        foreach (var n in names)
                            if (_compMatches.Contains(n)) visible.Add(n);
                        if (visible.Count == 0) continue;
                        iy += catH;
                        foreach (var nm in visible)
                        {
                            var row = new RectangleF(ppx + 2, iy, popW - 4, itemH);
                            if (row.Contains(pos)) { _addCompHoverIndex = idx; break; }
                            iy += itemH; idx++;
                        }
                    }
                }
            }
        }

        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            if (_showRefPicker)
            {
                if (e.Key == Keys.Escape) { _showRefPicker = false; return; }
                if (e.Key == Keys.Backspace && _refPickerFilter.Length > 0)
                { _refPickerFilter = _refPickerFilter[..^1]; return; }
                return;
            }
            if (_showAddComp)
            {
                if (e.Key == Keys.Escape) { _showAddComp = false; return; }
                if (e.Key == Keys.Backspace && _addCompFilter.Length > 0)
                { _addCompFilter = _addCompFilter[..^1]; return; }
                return;
            }

            if (_editId == null) return;
            switch (e.Key)
            {
                case Keys.Enter: CommitEdit(); break;
                case Keys.Escape: _editId = null; break;
                case Keys.Backspace when _editBuf.Length > 0:
                    _editBuf = _editBuf[..^1]; break;
            }
        }

        public override void OnTextInput(TextInputEventArgs e)
        {
            if (_showRefPicker) { _refPickerFilter += e.AsString; return; }
            if (_showAddComp) { _addCompFilter += e.AsString; return; }
            if (_editId != null) _editBuf += e.AsString;
        }

        private void StartEdit(string id, string initial, Action<string> commit)
        { _editId = id; _editBuf = initial; _editCommit = commit; }

        private void CommitEdit() { _editCommit?.Invoke(_editBuf); _editId = null; }

        // ── Add Component popup click ─────────────────────────────────────────
        private void HandleAddCompClick(PointF pos, RectangleF pop)
        {
            bool searching = _addCompFilter.Length > 0;
            float itemH = 20f, catH = 16f;
            float iy = pop.Y + 30f;

            if (searching)
            {
                foreach (var nm in _compMatches)
                {
                    if (iy + itemH > pop.Bottom) break;
                    var row = new RectangleF(pop.X + 2, iy, pop.Width - 4, itemH);
                    if (row.Contains(pos)) { _target?.AddComponentByName(nm); _showAddComp = false; return; }
                    iy += itemH;
                }
            }
            else
            {
                foreach (var (_, names) in CompCategories)
                {
                    var visible = new List<string>();
                    foreach (var n in names)
                        if (_compMatches.Contains(n)) visible.Add(n);
                    if (visible.Count == 0) continue;
                    iy += catH; // skip category header
                    foreach (var nm in visible)
                    {
                        if (iy + itemH > pop.Bottom) break;
                        var row = new RectangleF(pop.X + 2, iy, pop.Width - 4, itemH);
                        if (row.Contains(pos)) { _target?.AddComponentByName(nm); _showAddComp = false; return; }
                        iy += itemH;
                    }
                }
            }
        }

        // ── GameObject / object-ref drop helpers ─────────────────────────────
        /// Returns the field id of the first object-ref field (typeof GameObject or Component)
        /// that contains the given screen position, or null if none.
        public string? GetObjectRefFieldAt(PointF pos)
        {
            foreach (var f in _fields)
            {
                if (f.FieldType != typeof(Core.GameObject) &&
                    !typeof(Core.Component).IsAssignableFrom(f.FieldType)) continue;
                if (f.Bounds.Contains(pos)) return f.Id;
            }
            return null;
        }

        // ── Script drop ───────────────────────────────────────────────────────
        public void AcceptScriptDrop(string scriptPath, string projectRoot)
        {
            if (_target == null) return;
            string typeName = System.IO.Path.GetFileNameWithoutExtension(scriptPath);

            // Try built-in registry first
            var comp = _target.AddComponentByName(typeName);
            if (comp == null)
            {
                // Add a DynamicScript placeholder so the user can see it attached
                var ds = new ElintriaEngine.Core.DynamicScript { ScriptTypeName = typeName };
                ds.GameObject = _target;
                _target.Components.Add(ds);
            }
            // Re-inspect to refresh the inspector
            Inspect(_target);
        }
    }
}