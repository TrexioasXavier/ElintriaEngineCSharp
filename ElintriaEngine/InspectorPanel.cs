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
        private bool _dropHighlight;

        // Fields rendered this frame (rebuilt each render)
        private readonly List<FieldRecord> _fields = new();

        // Track which script types were visible last frame.
        // If the type set changes (new compile), Inspect() is called automatically.
        private readonly Dictionary<string, bool> _knownScriptTypes = new();

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
        private static readonly string[] AllComponents =
        {
            "MeshFilter","MeshRenderer","Camera","Light","Rigidbody",
            "BoxCollider","SphereCollider","AudioSource","AudioListener",
            "CanvasComponent","CanvasRenderer","ImageComponent","ButtonComponent",
            "TextComponent","SliderComponent"
        };

        private const float LW = 118f;   // label column width
        private const float FH = 20f;    // field row height
        private const float SH = 22f;    // section header height
        private const float PAD = 6f;

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

        public void SetDropHighlight(bool on) => _dropHighlight = on;

        // ── Render ─────────────────────────────────────────────────────────────
        public override void OnRender(IEditorRenderer r)
        {
            if (!IsVisible) return;
            DrawHeader(r);

            var cr = ContentRect;
            r.PushClip(cr);
            r.FillRect(cr, ColBg);

            _fields.Clear();
            float y = cr.Y - ScrollOffset;

            if (_target == null)
            {
                r.DrawText("Nothing selected.", new PointF(cr.X + 10, cr.Y + 12), ColTextDim, 11f);
                if (_dropHighlight) DrawDropOverlay(r, cr);
                r.PopClip(); DrawScrollBar(r); return;
            }

            DrawObjectHeader(r, cr, ref y);
            DrawSeparator(r, cr, ref y);
            DrawTransform(r, cr, ref y);
            DrawSeparator(r, cr, ref y);

            foreach (var comp in new List<Component>(_target.Components))
                DrawComponent(r, cr, comp, ref y);

            DrawAddComponentButton(r, cr, ref y);

            ContentHeight = (y + ScrollOffset) - cr.Y + 10f;

            if (_dropHighlight) DrawDropOverlay(r, cr);
            r.PopClip();
            DrawScrollBar(r);

            // Add component popup drawn OUTSIDE clip so it's not cut off
            if (_showAddComp) DrawAddCompPopup(r);
        }

        // ── Object header ──────────────────────────────────────────────────────
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

            // Name
            string nm = _editId == "__name__" ? _editBuf + "|" : _target.Name;
            r.DrawText(nm, new PointF(cr.X + PAD + 20f, y + 7f), ColText, 12f);

            r.DrawText("Tag: " + _target.Tag, new PointF(cr.Right - 108f, y + 3f), ColTextDim, 9f);
            r.DrawText("Layer: " + _target.Layer, new PointF(cr.Right - 108f, y + 14f), ColTextDim, 9f);

            y += 28f; ContentHeight += 28f;
        }

        // ── Transform ─────────────────────────────────────────────────────────
        private void DrawTransform(IEditorRenderer r, RectangleF cr, ref float y)
        {
            if (_target == null) return;
            DrawSectionHeader(r, cr, "Transform", null, ref y);

            var t = _target.Transform;
            DrawVec3Field(r, cr, "Position", t.LocalPosition, ref y, "t_pos", v => t.LocalPosition = v);
            DrawVec3Field(r, cr, "Rotation", t.LocalEulerAngles, ref y, "t_rot", v => t.LocalEulerAngles = v);
            DrawVec3Field(r, cr, "Scale", t.LocalScale, ref y, "t_scl", v => t.LocalScale = v);
        }

        // ── Component section ──────────────────────────────────────────────────
        private void DrawComponent(IEditorRenderer r, RectangleF cr, Component comp, ref float y)
        {
            string cid = "c" + comp.GetHashCode();

            // ── DynamicScript placeholder — shows fields from compiled type ─────────
            if (comp is Core.DynamicScript ds)
            {
                DrawSectionHeader(r, cr, ds.ScriptTypeName, () => _target?.RemoveComponent(comp), ref y);
                DrawBoolField(r, cr, "Enabled", comp.Enabled, ref y, cid + "_en", v => comp.Enabled = v);

                var realType = Core.ComponentRegistry.TryGetType(ds.ScriptTypeName);

                // Track whether the type was available last frame.
                // If this changes (newly compiled) we re-scroll to top so new fields are visible.
                bool wasKnown = _knownScriptTypes.TryGetValue(ds.ScriptTypeName, out bool prev) && prev;
                bool isKnown = realType != null;
                _knownScriptTypes[ds.ScriptTypeName] = isKnown;
                if (isKnown && !wasKnown)
                    ScrollOffset = 0;   // jump to top so new fields are visible

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

                        // Capture loop variable so each closure has its own copy
                        var fieldInfo = fi;
                        var fieldName = fi.Name;
                        Type fieldType = fi.FieldType;

                        // Read from FieldValues dict (editor storage), coerce to the right type
                        ds.FieldValues.TryGetValue(fieldName, out var stored);
                        object? display = CoerceToType(stored, fieldType) ?? GetDefault(fieldType);

                        DrawAnyField(r, cr, fieldName, display, fieldType,
                            cid + "_ds_" + fieldName, ref y,
                            newVal =>
                            {
                                // Store in FieldValues — copied to the real instance on play
                                ds.FieldValues[fieldName] = newVal;
                            });
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
                    // Script not yet compiled — show a hint
                    float lx = cr.X + PAD;
                    r.FillRect(new RectangleF(lx, y, cr.Width - PAD * 2, 34f),
                        Color.FromArgb(30, 200, 160, 40));
                    r.DrawText("⚙  Script not compiled yet.", new PointF(lx + 6f, y + 4f),
                        Color.FromArgb(255, 190, 150, 40), 9f);
                    r.DrawText("Save your script file to trigger auto-compile.",
                        new PointF(lx + 6f, y + 17f), Color.FromArgb(255, 130, 130, 130), 8f);
                    y += 40f; ContentHeight += 40f;
                }

                r.DrawLine(new PointF(cr.X, y), new PointF(cr.Right, y), Color.FromArgb(255, 50, 50, 50));
                y += 3f; ContentHeight += 3f;
                return;
            }

            // ── Normal (compiled) component ───────────────────────────────────
            DrawSectionHeader(r, cr, comp.GetType().Name, () => _target?.RemoveComponent(comp), ref y);

            // Enabled toggle
            DrawBoolField(r, cr, "Enabled", comp.Enabled, ref y, cid + "_en", v => comp.Enabled = v);

            // Reflected public fields – FlattenHierarchy ensures fields declared in
            // base user classes (not just the immediate concrete type) are included.
            // We skip fields declared on Component itself (Enabled, GameObject) because
            // they are already drawn above or are infrastructure-only.
            var componentBaseFields = new System.Collections.Generic.HashSet<string>(
                typeof(Component).GetFields(BindingFlags.Public | BindingFlags.Instance |
                                            BindingFlags.FlattenHierarchy)
                    .Select(f => f.Name));

            foreach (var fi in comp.GetType().GetFields(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (componentBaseFields.Contains(fi.Name)) continue;
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

        // ── Add Component button ───────────────────────────────────────────────
        private void DrawAddComponentButton(IEditorRenderer r, RectangleF cr, ref float y)
        {
            float by = y + 6f;
            var btn = new RectangleF(cr.X + PAD, by, cr.Width - PAD * 2, 22f);
            _addCompRect = btn;
            r.FillRect(btn, Color.FromArgb(255, 46, 46, 46));
            r.DrawRect(btn, Color.FromArgb(255, 70, 70, 70));
            r.DrawText("+ Add Component", new PointF(btn.X + btn.Width / 2f - 50f, btn.Y + 5f), ColText, 11f);
            y += 32f; ContentHeight += 32f;
        }

        // ── Add Component popup ────────────────────────────────────────────────
        private void DrawAddCompPopup(IEditorRenderer r)
        {
            float popW = 200f, popH = 220f;
            float px = _addCompRect.X;
            float py = _addCompRect.Bottom;
            var pop = new RectangleF(px, py, popW, popH);

            r.FillRect(new RectangleF(px + 3, py + 3, popW, popH), Color.FromArgb(70, 0, 0, 0));
            r.FillRect(pop, Color.FromArgb(252, 34, 34, 34));
            r.DrawRect(pop, Color.FromArgb(255, 65, 65, 65));

            // Search bar
            var searchRect = new RectangleF(px + 4, py + 4, popW - 8, 20f);
            r.FillRect(searchRect, Color.FromArgb(255, 28, 28, 28));
            r.DrawRect(searchRect, ColAccent);
            r.DrawText(_addCompFilter + "|", new PointF(searchRect.X + 4, searchRect.Y + 4), ColText, 10f);

            // Filter matches
            _compMatches.Clear();
            foreach (var name in AllComponents)
                if (_addCompFilter.Length == 0 || name.ToLower().Contains(_addCompFilter.ToLower()))
                    _compMatches.Add(name);

            float iy = py + 28f;
            foreach (var nm in _compMatches)
            {
                if (iy > pop.Bottom - 4f) break;
                var row = new RectangleF(px + 2, iy, popW - 4, 18f);
                // Hover handled in OnMouseMove
                r.DrawText(nm, new PointF(px + 8f, iy + 3f), ColText, 10f);
                iy += 18f;
            }
        }

        // ── Drop overlay ──────────────────────────────────────────────────────
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

            // Fallback – display as read-only text
            DrawLabelRow(r, cr, label, value?.ToString() ?? "null", ref y);
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

        private void DrawColorField(IEditorRenderer r, RectangleF cr, string label,
            Color4 value, ref float y, string id)
        {
            DrawLabel(r, cr, label, y);
            var fr = new RectangleF(cr.X + LW, y + 2f, cr.Width - LW - PAD, FH - 4f);
            r.FillRect(fr, Color.FromArgb(
                (int)(value.A * 255), (int)(value.R * 255),
                (int)(value.G * 255), (int)(value.B * 255)));
            r.DrawRect(fr, ColBorder);
            y += FH; ContentHeight += FH;
        }

        private void DrawLabelRow(IEditorRenderer r, RectangleF cr, string label,
            string display, ref float y)
        {
            DrawLabel(r, cr, label, y);
            r.DrawText(display, new PointF(cr.X + LW + 4f, y + 4f), ColTextDim, 10f);
            y += FH; ContentHeight += FH;
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private void DrawSectionHeader(IEditorRenderer r, RectangleF cr,
            string title, Action? onRemove, ref float y)
        {
            var row = new RectangleF(cr.X, y, cr.Width, SH);
            r.FillRect(row, Color.FromArgb(255, 46, 46, 52));
            r.DrawText("v " + title, new PointF(cr.X + PAD, y + 5f),
                Color.FromArgb(255, 195, 210, 255), 11f);

            if (onRemove != null)
            {
                var rm = new RectangleF(cr.Right - 20f, y + 3f, 16f, 16f);
                r.FillRect(rm, Color.FromArgb(255, 130, 38, 38));
                r.DrawRect(rm, ColBorder);
                r.DrawText("X", new PointF(rm.X + 3f, rm.Y + 3f), Color.White, 9f);
                // Register as clickable via field record with bool toggle
                _fields.Add(new FieldRecord(rm, "rm_" + title + y.GetHashCode(),
                    true, typeof(bool), _ => onRemove()));
            }

            y += SH; ContentHeight += SH;
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
            if (!IsVisible || !Bounds.Contains(pos)) return;
            IsFocused = true;

            // Add component popup
            if (_showAddComp)
            {
                float popW = 200f, popH = 220f;
                var pop = new RectangleF(_addCompRect.X, _addCompRect.Bottom, popW, popH);
                if (pop.Contains(pos))
                {
                    HandleAddCompClick(pos, pop);
                    return;
                }
                _showAddComp = false;
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
                        // Toggle bool
                        bool cur = field.Value is bool b && b;
                        field.Setter(!cur);
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
            }

            base.OnMouseDown(e, pos);
        }

        public override void OnMouseUp(MouseButtonEventArgs e, PointF pos)
        {
            _dragId = null; _dragSetter = null;
            base.OnMouseUp(e, pos);
        }

        public override void OnMouseMove(PointF pos)
        {
            base.OnMouseMove(pos);
            // Float field drag
            if (_dragId != null && _dragSetter != null)
            {
                float delta = (pos.X - _dragStartX) * 0.02f;
                _dragSetter(_dragStartVal + delta);
            }
        }

        public override void OnKeyDown(KeyboardKeyEventArgs e)
        {
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
            if (_showAddComp) { _addCompFilter += e.AsString; return; }
            if (_editId != null) _editBuf += e.AsString;
        }

        private void StartEdit(string id, string initial, Action<string> commit)
        { _editId = id; _editBuf = initial; _editCommit = commit; }

        private void CommitEdit() { _editCommit?.Invoke(_editBuf); _editId = null; }

        // ── Add Component popup click ─────────────────────────────────────────
        private void HandleAddCompClick(PointF pos, RectangleF pop)
        {
            float iy = pop.Y + 28f;
            foreach (var nm in _compMatches)
            {
                if (iy > pop.Bottom - 4f) break;
                var row = new RectangleF(pop.X + 2, iy, pop.Width - 4, 18f);
                if (row.Contains(pos))
                {
                    _target?.AddComponentByName(nm);
                    _showAddComp = false;
                    return;
                }
                iy += 18f;
            }
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