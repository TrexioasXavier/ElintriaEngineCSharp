using Elintria.Editor;
using Elintria.Engine;
using Elintria.Engine.Rendering;
using OpenTK.Mathematics;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Elintria
{
    // =========================================================================
    // InspectorPanel
    // =========================================================================
    /// <summary>
    /// Unity-style inspector:
    ///   • Object header — active toggle + editable name
    ///   • Transform — Position / Rotation / Scale (live-editable Vec3 rows)
    ///   • Per-component section:
    ///       - Enabled toggle
    ///       - Type name
    ///       - Remove [×] button
    ///       - ALL public instance fields and read/write properties shown as
    ///         editable InputFields; updated live back into the component each frame
    ///   • "Add Component" button → dropdown of all Component subclasses
    /// </summary>
    public class InspectorPanel : Panel
    {
        // ------------------------------------------------------------------
        // Colours
        // ------------------------------------------------------------------
        static readonly Color C_Bg = Color.FromArgb(220, 20, 20, 30);
        static readonly Color C_Header = Color.FromArgb(255, 38, 38, 55);
        static readonly Color C_Section = Color.FromArgb(255, 30, 30, 46);
        static readonly Color C_CompHdr = Color.FromArgb(255, 34, 40, 58);
        static readonly Color C_RowA = Color.FromArgb(80, 22, 22, 34);
        static readonly Color C_RowB = Color.FromArgb(80, 26, 26, 40);
        static readonly Color C_Sep = Color.FromArgb(80, 130, 130, 160);
        static readonly Color C_Text = Color.FromArgb(255, 215, 215, 225);
        static readonly Color C_Dim = Color.FromArgb(190, 145, 145, 160);
        static readonly Color C_Green = Color.FromArgb(255, 75, 185, 95);
        static readonly Color C_Red = Color.FromArgb(255, 190, 60, 60);
        static readonly Color C_Blue = Color.FromArgb(255, 65, 110, 195);
        static readonly Color C_BlueHov = Color.FromArgb(255, 85, 135, 220);

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------
        private readonly BitmapFont _font;
        private GameObject _selected;
        private Panel _body;
        private Panel _dropdown;
        private string _compSearch = "";

        // Drag-and-drop drop zone state
        private bool _dropHighlight;   // true while a valid drag is hovering over us

        // Transform field cache
        private InputField _posX, _posY, _posZ;
        private InputField _rotX, _rotY, _rotZ;
        private InputField _scaX, _scaY, _scaZ;
        private InputField _nameField;

        // Per-component public-field cache: comp → list of (fieldOrPropName, InputField)
        private readonly Dictionary<Component, List<(string member, InputField field)>>
            _compFields = new();

        // Component type lists — refreshed on each dropdown open so runtime-compiled scripts appear
        private static List<System.Type> _engineTypes;   // Elintria.Engine.*
        private static List<System.Type> _scriptTypes;   // user scripts (data/Scripts)
        private static List<System.Type> _knownTypes;    // combined (kept for legacy paths)

        // ------------------------------------------------------------------
        public InspectorPanel(BitmapFont font)
        {
            _font = font;
            BackgroundColor = C_Bg;
            RefreshComponentTypes();
            Rebuild();
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------
        public void Select(GameObject go)
        {
            if (_selected == go) return;
            _selected = go;
            CloseDropdown();
            Rebuild();
        }

        public override void Draw()
        {
            base.Draw();
            // Show drop highlight when a draggable asset hovers over the Inspector
            if (DragDropService.ShowGhost && IsPointInside(GetMousePosition()))
            {
                var abs = GetAbsolutePosition();
                UIRenderer.DrawRectOutline(abs.X + 1, abs.Y + 1, Size.X - 2, Size.Y - 2,
                    System.Drawing.Color.FromArgb(200, 90, 160, 255), 2f);
                // Drop hint label at top
                _font?.DrawText("Drop to add component", abs.X + 8, abs.Y + 4,
                    System.Drawing.Color.FromArgb(200, 90, 160, 255));
            }
        }

        public override void Update(float dt)
        {
            base.Update(dt);
            PushAllToComponents();
        }

        // ------------------------------------------------------------------
        // Rebuild
        // ------------------------------------------------------------------
        private void Rebuild()
        {
            if (_body != null) RemoveChild(_body);
            if (_dropdown != null) RemoveChild(_dropdown);
            _body = _dropdown = null;
            _posX = _posY = _posZ = null;
            _rotX = _rotY = _rotZ = null;
            _scaX = _scaY = _scaZ = null;
            _nameField = null;
            _compFields.Clear();

            _body = new Panel
            {
                Position = Vector2.Zero,
                Size = Size,
                BackgroundColor = Color.Transparent
            };
            AddChild(_body);

            float y = 0f;

            if (_selected == null)
            {
                y = EmptyHint(y);
            }
            else
            {
                y = Header(y);
                y = HRule(y);
                y = TransformSection(y);
                y = HRule(y);
                y = ComponentsSection(y);
                y = HRule(y);
                y = AddCompButton(y);
            }

            _body.Size = new Vector2(Size.X, y + 8f);
        }

        // ------------------------------------------------------------------
        // Empty state
        // ------------------------------------------------------------------
        private float EmptyHint(float y)
        {
            y += 24f;
            _body.AddChild(Lbl("No object selected", 0, y, Size.X, 18, C_Dim, halign: "c")); y += 22f;
            _body.AddChild(Lbl("Click an object in the viewport", 0, y, Size.X, 16, C_Dim, halign: "c")); y += 20f;
            _body.AddChild(Lbl("or the hierarchy list.", 0, y, Size.X, 16, C_Dim, halign: "c")); y += 20f;
            return y + 16f;
        }

        // ------------------------------------------------------------------
        // Header
        // ------------------------------------------------------------------
        private float Header(float y)
        {
            _body.AddChild(Rect(0, y, Size.X, 36, C_Header));

            var toggle = Btn(_selected.ActiveSelf ? "●" : "○",
                6, y + 8, 20, 20,
                _selected.ActiveSelf ? C_Green : C_Dim,
                Color.FromArgb(140, 40, 40, 60), Color.FromArgb(200, 55, 55, 80));
            toggle.OnClick += () => { _selected.SetActive(!_selected.ActiveSelf); Rebuild(); };
            _body.AddChild(toggle);

            _nameField = Field(_selected.Name, 30, y + 7, Size.X - 38, 22);
            _body.AddChild(_nameField);

            return y + 38f;
        }

        // ------------------------------------------------------------------
        // Transform section
        // ------------------------------------------------------------------
        private float TransformSection(float y)
        {
            y = SectionHdr("Transform", y);
            var p = _selected.Transform.LocalPosition;
            var r = _selected.Transform.EulerAngles;
            var s = _selected.Transform.LocalScale;
            (_posX, _posY, _posZ) = Vec3Row("Position", p.X, p.Y, p.Z, ref y);
            (_rotX, _rotY, _rotZ) = Vec3Row("Rotation", r.X, r.Y, r.Z, ref y);
            (_scaX, _scaY, _scaZ) = Vec3Row("Scale", s.X, s.Y, s.Z, ref y);
            return y;
        }

        // ------------------------------------------------------------------
        // Components section — each comp gets a header + public fields
        // ------------------------------------------------------------------
        private float ComponentsSection(float y)
        {
            y = SectionHdr("Components", y);

            var comps = _selected.GetComponents<Component>().ToList();
            if (comps.Count == 0)
            {
                _body.AddChild(Lbl("  (none)", 4, y, Size.X, 18, C_Dim));
                return y + 24f;
            }

            foreach (var comp in comps)
            {
                // ── Component header row ──────────────────────────────
                _body.AddChild(Rect(0, y, Size.X, 26, C_CompHdr));

                var captComp = comp;

                // Enabled toggle
                var enBtn = Btn(comp.Enabled ? "●" : "○",
                    4, y + 4, 18, 18,
                    comp.Enabled ? C_Green : C_Dim,
                    Color.Transparent, Color.FromArgb(80, 60, 60, 90));
                enBtn.OnClick += () =>
                {
                    captComp.Enabled = !captComp.Enabled;
                    if (captComp.Enabled) captComp.OnEnable(); else captComp.OnDisable();
                    Rebuild();
                };
                _body.AddChild(enBtn);

                _body.AddChild(Lbl(comp.GetType().Name, 26, y + 5, Size.X - 54, 16, C_Text));

                // Remove [×]
                var rmBtn = Btn("×", Size.X - 26, y + 3, 20, 20,
                    C_Red, Color.FromArgb(140, 55, 22, 22), Color.FromArgb(200, 90, 35, 35));
                rmBtn.OnClick += () =>
                {
                    typeof(GameObject)
                        .GetMethod(nameof(GameObject.RemoveComponent))!
                        .MakeGenericMethod(captComp.GetType())
                        .Invoke(_selected, null);
                    Rebuild();
                };
                _body.AddChild(rmBtn);

                y += 28f;

                // ── Public fields / properties ────────────────────────
                var members = GetPublicMembers(comp);
                if (members.Count > 0)
                {
                    var fieldCache = new List<(string, InputField)>();
                    int row = 0;
                    foreach (var (name, value) in members)
                    {
                        float rowH = 22f;
                        float lblW = 90f;
                        _body.AddChild(Rect(0, y, Size.X, rowH,
                            row % 2 == 0 ? C_RowA : C_RowB));

                        _body.AddChild(Lbl(name, 6, y + 3, lblW, 16, C_Dim));

                        var fld = Field(value, lblW + 10, y + 2, Size.X - lblW - 16, 18);
                        _body.AddChild(fld);
                        fieldCache.Add((name, fld));

                        y += rowH + 1f;
                        row++;
                    }
                    _compFields[comp] = fieldCache;
                }

                y += 4f;  // gap between components
            }

            return y;
        }

        // ------------------------------------------------------------------
        // Add Component button
        // ------------------------------------------------------------------
        private float AddCompButton(float y)
        {
            var btn = new Button
            {
                Position = new Vector2(8, y),
                Size = new Vector2(Size.X - 16, 28),
                Label = "+ Add Component",
                Font = _font,
                LabelColor = Color.White,
                BackgroundColor = C_Blue,
                HoverColor = C_BlueHov,
                PressedColor = Color.FromArgb(255, 45, 78, 150)
            };
            btn.OnClick += ToggleDropdown;
            _body.AddChild(btn);
            return y + 32f;
        }

        private void ToggleDropdown()
        {
            if (_dropdown != null) { CloseDropdown(); return; }
            _compSearch = "";
            RebuildDropdown();
        }

        /// <summary>Builds (or rebuilds after search text changes) the Add Component popup.</summary>
        private void RebuildDropdown()
        {
            if (_dropdown != null) { RemoveChild(_dropdown); _dropdown = null; }

            RefreshComponentTypes();

            const float searchH = 22f;
            const float catH = 18f;
            const float itemH = 22f;
            float popW = Size.X - 16f;

            // Filter by search
            string q = _compSearch.ToLowerInvariant();
            var filtered_engine = string.IsNullOrEmpty(q)
                ? _engineTypes
                : _engineTypes.Where(t => t.Name.ToLowerInvariant().Contains(q)).ToList();
            var filtered_script = string.IsNullOrEmpty(q)
                ? _scriptTypes
                : _scriptTypes.Where(t => t.Name.ToLowerInvariant().Contains(q)).ToList();

            // Calculate height
            float h = searchH + 4f;
            if (filtered_engine.Count > 0) h += catH + filtered_engine.Count * itemH + 4f;
            if (filtered_script.Count > 0) h += catH + filtered_script.Count * itemH + 4f;
            if (filtered_engine.Count == 0 && filtered_script.Count == 0) h += itemH;
            h = MathF.Min(h, 340f);

            _dropdown = new Panel
            {
                Position = new Vector2(8, _body.Size.Y + 2f),
                Size = new Vector2(popW, h),
                BackgroundColor = Color.FromArgb(250, 22, 22, 35)
            };

            float y = 2f;

            // ── Search box ───────────────────────────────────────────────
            var searchField = new InputField
            {
                Position = new Vector2(4, y),
                Size = new Vector2(popW - 8, searchH - 2),
                Text = _compSearch,
                Font = _font,
                BackgroundColor = Color.FromArgb(200, 16, 16, 26),
                /*FocusedBorderColor = Color.FromArgb(255, 75, 115, 200),
                UnfocusedBorderColor = Color.FromArgb(80, 80, 80, 100),*/
                MaxLength = 40,
                PlaceholderText = "Search components..."
            };
            // Rebuild dropdown on text change
            string prevText = _compSearch;
            searchField.OnTextChanged += text =>
            {
                _compSearch = text;
                RebuildDropdown();
            };
            _dropdown.AddChild(searchField);
            y += searchH + 2f;

            // ── Helper to add a section ──────────────────────────────────
            void AddSection(string title, List<System.Type> types)
            {
                if (types.Count == 0) return;

                // Category header
                _dropdown.AddChild(new Panel
                {
                    Position = new Vector2(0, y),
                    Size = new Vector2(popW, catH),
                    BackgroundColor = Color.FromArgb(200, 30, 30, 50)
                });
                _dropdown.AddChild(new Text
                {
                    Position = new Vector2(6, y + 2),
                    Size = new Vector2(popW - 12, catH - 4),
                    Content = title,
                    Font = _font,
                    TextColor = Color.FromArgb(200, 120, 160, 220),
                    BackgroundColor = Color.Transparent
                });
                y += catH;

                foreach (var type in types)
                {
                    var captType = type;
                    var item = new Button
                    {
                        Position = new Vector2(4, y),
                        Size = new Vector2(popW - 8, itemH - 2),
                        Label = type.Name,
                        Font = _font,
                        LabelColor = C_Text,
                        BackgroundColor = Color.Transparent,
                        HoverColor = Color.FromArgb(180, 50, 68, 110),
                        PressedColor = C_Blue
                    };
                    item.OnClick += () =>
                    {
                        typeof(GameObject)
                            .GetMethod(nameof(GameObject.AddComponent))!
                            .MakeGenericMethod(captType)
                            .Invoke(_selected, null);
                        CloseDropdown();
                        Rebuild();
                    };
                    _dropdown.AddChild(item);
                    y += itemH;
                }
                y += 4f;
            }

            if (filtered_engine.Count == 0 && filtered_script.Count == 0)
            {
                _dropdown.AddChild(new Text
                {
                    Position = new Vector2(6, y),
                    Size = new Vector2(popW - 12, itemH),
                    Content = "No components found",
                    Font = _font,
                    TextColor = C_Dim,
                    BackgroundColor = Color.Transparent
                });
            }
            else
            {
                AddSection("Engine Components", filtered_engine);
                AddSection("Scripts", filtered_script);
            }

            AddChild(_dropdown);
        }

        private void CloseDropdown()
        {
            if (_dropdown == null) return;
            RemoveChild(_dropdown);
            _dropdown = null;
        }

        // ------------------------------------------------------------------
        // ------------------------------------------------------------------
        // Drag-and-drop — accept files dropped from ProjectPanel
        // ------------------------------------------------------------------
        public override bool HandleMouseUp(OpenTK.Windowing.Common.MouseButtonEventArgs e)
        {
            if (e.Button == OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left
                && DragDropService.IsDragging)
            {
                // Use actual mouse position (GhostPos lags by one frame on fast drags)
                var mp = GetMousePosition();
                if (IsPointInside(mp))
                {
                    var payload = DragDropService.TryDrop();
                    if (payload != null && _selected != null)
                        HandleAssetDrop(payload);
                    return true;
                }
            }
            return base.HandleMouseUp(e);
        }

        private void HandleAssetDrop(DragDropPayload payload)
        {
            switch (payload.AssetType)
            {
                case DragDropAssetType.Script:
                    // Find the Component type whose name matches the file stem
                    var compType = FindComponentType(payload.FileStem);
                    if (compType != null)
                    {
                        typeof(Elintria.Engine.GameObject)
                            .GetMethod(nameof(Elintria.Engine.GameObject.AddComponent))!
                            .MakeGenericMethod(compType)
                            .Invoke(_selected, null);
                        Console.WriteLine($"[Inspector] Added component: {compType.Name}");
                        Rebuild();
                    }
                    else
                    {
                        Console.WriteLine($"[Inspector] No compiled type found for '{payload.FileStem}'. " +
                                          "Build the project first so the script is compiled.");
                    }
                    break;

                case DragDropAssetType.Material:
                    // Drop .mat onto MeshRenderer — note materials are engine objects,
                    // future version can deserialise from file; for now just log
                    Console.WriteLine($"[Inspector] Material drop: {payload.FileName} (set via code for now)");
                    break;

                case DragDropAssetType.Texture:
                    Console.WriteLine($"[Inspector] Texture drop: {payload.FileName}");
                    // Load texture and assign to first MeshRenderer material
                    var mr = _selected.GetComponent<Elintria.Engine.MeshRenderer>();
                    if (mr?.Material != null)
                    {
                        var tex = Elintria.Engine.Rendering.Texture.Load(payload.FilePath);
                        if (tex != null) mr.Material.MainTexture = tex;
                        Console.WriteLine($"[Inspector] Texture assigned to MeshRenderer.Material");
                    }
                    break;

                case DragDropAssetType.Mesh:
                    Console.WriteLine($"[Inspector] Mesh drop: {payload.FileName} (.obj import not yet implemented)");
                    break;

                case DragDropAssetType.Shader:
                    Console.WriteLine($"[Inspector] Shader drop: {payload.FileName}");
                    break;

                default:
                    Console.WriteLine($"[Inspector] Dropped: {payload.FileName} (type: {payload.AssetType})");
                    break;
            }
        }

        private static System.Type FindComponentType(string typeName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t.IsAbstract) continue;
                    if (!typeof(Elintria.Engine.Component).IsAssignableFrom(t)) continue;
                    if (string.Equals(t.Name, typeName,
                            System.StringComparison.OrdinalIgnoreCase)) return t;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Live update — push all field values back to their components
        // ------------------------------------------------------------------
        private void PushAllToComponents()
        {
            if (_selected == null) return;
            var t = _selected.Transform;

            if (_nameField != null && _nameField.Text != _selected.Name)
                _selected.Name = _nameField.Text;

            if (_posX != null)
                t.LocalPosition = new Vector3(Pf(_posX, t.LocalPosition.X),
                                              Pf(_posY, t.LocalPosition.Y),
                                              Pf(_posZ, t.LocalPosition.Z));
            if (_rotX != null)
                t.EulerAngles = new Vector3(Pf(_rotX, t.EulerAngles.X),
                                            Pf(_rotY, t.EulerAngles.Y),
                                            Pf(_rotZ, t.EulerAngles.Z));
            if (_scaX != null)
                t.LocalScale = new Vector3(Pf(_scaX, t.LocalScale.X),
                                           Pf(_scaY, t.LocalScale.Y),
                                           Pf(_scaZ, t.LocalScale.Z));

            // Push per-component public fields
            foreach (var (comp, entries) in _compFields)
            {
                foreach (var (memberName, inputField) in entries)
                {
                    if (inputField.IsFocused) continue; // don't interrupt typing
                    WriteToMember(comp, memberName, inputField.Text);
                }
            }
        }

        // ------------------------------------------------------------------
        // Reflection: get public instance members of a component
        // ------------------------------------------------------------------
        private static List<(string name, string value)> GetPublicMembers(Component comp)
        {
            var result = new List<(string, string)>();
            var type = comp.GetType();
            if (type == typeof(Component)) return result;

            // Only the type's OWN declared members (not inherited engine members)
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance
                                        | BindingFlags.DeclaredOnly);
            foreach (var fi in fields)
            {
                if (fi.IsSpecialName) continue;
                if (!IsEditableType(fi.FieldType)) continue;
                result.Add((fi.Name, ValStr(fi.GetValue(comp))));
            }

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance
                                           | BindingFlags.DeclaredOnly);
            foreach (var pi in props)
            {
                if (!pi.CanRead || !pi.CanWrite) continue;
                if (pi.GetIndexParameters().Length > 0) continue;
                if (!IsEditableType(pi.PropertyType)) continue;
                result.Add((pi.Name, ValStr(pi.GetValue(comp))));
            }

            return result;
        }

        private static bool IsEditableType(System.Type t)
            => t == typeof(float) || t == typeof(double) || t == typeof(int)
            || t == typeof(bool) || t == typeof(string)
            || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4);

        private static void WriteToMember(Component comp, string name, string text)
        {
            var type = comp.GetType();
            var ic = CultureInfo.InvariantCulture;

            // Try field
            var fi = type.GetField(name, BindingFlags.Public | BindingFlags.Instance
                                         | BindingFlags.DeclaredOnly);
            if (fi != null)
            {
                var val = ParseValue(text, fi.FieldType, ic);
                if (val != null) fi.SetValue(comp, val);
                return;
            }

            // Try property
            var pi = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance
                                            | BindingFlags.DeclaredOnly);
            if (pi != null && pi.CanWrite)
            {
                var val = ParseValue(text, pi.PropertyType, ic);
                if (val != null) pi.SetValue(comp, val);
            }
        }

        private static object ParseValue(string s, System.Type t, CultureInfo ic)
        {
            try
            {
                if (t == typeof(float)) return float.Parse(s, ic);
                if (t == typeof(double)) return double.Parse(s, ic);
                if (t == typeof(int)) return int.Parse(s, ic);
                if (t == typeof(bool)) return bool.Parse(s);
                if (t == typeof(string)) return s;
                if (t == typeof(Vector2)) { var p = P(s, 2, ic); return new Vector2(p[0], p[1]); }
                if (t == typeof(Vector3)) { var p = P(s, 3, ic); return new Vector3(p[0], p[1], p[2]); }
                if (t == typeof(Vector4)) { var p = P(s, 4, ic); return new Vector4(p[0], p[1], p[2], p[3]); }
            }
            catch { }
            return null;
        }

        private static float[] P(string s, int n, CultureInfo ic)
            => s.Split(',').Select(x => float.Parse(x.Trim(), ic)).Take(n).ToArray();

        private static string ValStr(object v)
        {
            if (v == null) return "";
            var ic = CultureInfo.InvariantCulture;
            return v switch
            {
                float f => f.ToString("G6", ic),
                double d => d.ToString("G9", ic),
                Vector2 v2 => $"{v2.X.ToString("G4", ic)},{v2.Y.ToString("G4", ic)}",
                Vector3 v3 => $"{v3.X.ToString("G4", ic)},{v3.Y.ToString("G4", ic)},{v3.Z.ToString("G4", ic)}",
                Vector4 v4 => $"{v4.X.ToString("G4", ic)},{v4.Y.ToString("G4", ic)},{v4.Z.ToString("G4", ic)},{v4.W.ToString("G4", ic)}",
                _ => v.ToString() ?? ""
            };
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        static float Pf(InputField f, float fallback)
            => f == null ? fallback
             : float.TryParse(f.Text, NumberStyles.Float,
                              CultureInfo.InvariantCulture, out float v) ? v : fallback;

        private float SectionHdr(string title, float y)
        {
            _body.AddChild(Rect(0, y, Size.X, 20, C_Section));
            _body.AddChild(Lbl(title, 8, y + 2, Size.X - 16, 16, C_Dim));
            return y + 22f;
        }

        private float HRule(float y)
        {
            _body.AddChild(Rect(6, y + 1, Size.X - 12, 1, C_Sep));
            return y + 4f;
        }

        private (InputField, InputField, InputField) Vec3Row(
            string label, float vx, float vy, float vz, ref float y)
        {
            float rowH = 24f;
            float lblW = 58f;
            float avail = Size.X - lblW - 10f;
            float fw = (avail - 4f) / 3f;

            _body.AddChild(Rect(0, y, Size.X, rowH, Color.FromArgb(50, 20, 20, 32)));
            _body.AddChild(Lbl(label, 6, y + 4, lblW, 16, C_Dim));

            float fx = lblW + 6f;
            var ix = Field(Ff(vx), fx, y + 2, fw, rowH - 4f);
            var iy = Field(Ff(vy), fx + fw + 2f, y + 2, fw, rowH - 4f);
            var iz = Field(Ff(vz), fx + fw * 2 + 4f, y + 2, fw, rowH - 4f);
            _body.AddChild(ix); _body.AddChild(iy); _body.AddChild(iz);

            y += rowH + 2f;
            return (ix, iy, iz);
        }

        static string Ff(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

        private InputField Field(string text, float x, float y, float w, float h) =>
            new InputField
            {
                Position = new Vector2(x, y),
                Size = new Vector2(w, h),
                Text = text,
                Font = _font,
                BackgroundColor = Color.FromArgb(200, 16, 16, 26),
                /*FocusedBorderColor = Color.FromArgb(255, 75, 115, 200),
                UnfocusedBorderColor = Color.FromArgb(80, 80, 80, 100),*/
                MaxLength = 32
            };

        private Text Lbl(string content, float x, float y, float w, float h,
                         Color color, string halign = "l")
        {
            var ha = halign == "c" ? Text.HAlign.Center : Text.HAlign.Left;
            return new Text
            {
                Position = new Vector2(x, y),
                Size = new Vector2(w, h),
                Content = content,
                Font = _font,
                TextColor = color,
                HorizontalAlign = ha,
                VerticalAlign = Text.VAlign.Middle,
                BackgroundColor = Color.Transparent
            };
        }

        private static Panel Rect(float x, float y, float w, float h, Color col) =>
            new Panel
            {
                Position = new Vector2(x, y),
                Size = new Vector2(w, h),
                BackgroundColor = col
            };

        private Button Btn(string label, float x, float y, float w, float h,
                           Color labelCol, Color bg, Color hover) =>
            new Button
            {
                Position = new Vector2(x, y),
                Size = new Vector2(w, h),
                Label = label,
                Font = _font,
                LabelColor = labelCol,
                BackgroundColor = bg,
                HoverColor = hover,
                PressedColor = Color.FromArgb(255, 60, 60, 90)
            };

        /// <summary>
        /// Scans all loaded assemblies for concrete Component subclasses and
        /// buckets them into engine types vs user scripts.
        /// Called every time the dropdown opens so newly compiled scripts appear.
        /// </summary>
        private static void RefreshComponentTypes()
        {
            var engine = new List<System.Type>();
            var scripts = new List<System.Type>();

            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    if (t.IsAbstract || t.IsInterface) continue;
                    if (!typeof(Component).IsAssignableFrom(t)) continue;
                    if (t == typeof(Component)) continue;
                    if (t.GetConstructor(System.Type.EmptyTypes) == null) continue;

                    // Bucket: Elintria.Engine(.Rendering) = engine, everything else = script
                    string ns = t.Namespace ?? "";
                    bool isEngine = ns.StartsWith("Elintria.Engine",
                                        System.StringComparison.OrdinalIgnoreCase)
                                 || ns.StartsWith("Elintria.Editor",
                                        System.StringComparison.OrdinalIgnoreCase);

                    if (isEngine) engine.Add(t);
                    else scripts.Add(t);
                }
            }

            static int Cmp(System.Type a, System.Type b) =>
                string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase);

            engine.Sort(Cmp);
            scripts.Sort(Cmp);

            _engineTypes = engine;
            _scriptTypes = scripts;
            _knownTypes = new List<System.Type>(engine.Concat(scripts));
        }
    }
}