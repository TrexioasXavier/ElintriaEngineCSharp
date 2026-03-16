using System;
using System.Collections.Generic;
using System.Drawing;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using ElintriaEngine.Core;

namespace ElintriaEngine.Rendering
{
    /// <summary>
    /// Draws scene-space gizmo overlays and transform handles via raw GL lines.
    /// All coordinates passed to Render() must be UI-space (top-left origin).
    /// WorldToScreen() converts world positions to UI-space screen coords — matching
    /// mouse coordinates from the windowing system so hit-testing works correctly.
    ///
    /// This renderer also draws all pending <see cref="PhysicsDebug"/> rays/lines
    /// each frame, exactly like Unity's Gizmos system does for Debug.DrawRay.
    /// </summary>
    public class GizmoRenderer : IDisposable
    {
        // ── Visibility toggles ────────────────────────────────────────────────
        public bool ShowAll { get; set; } = true;
        public bool ShowCameras { get; set; } = true;
        public bool ShowLights { get; set; } = true;
        public bool ShowColliders { get; set; } = true;
        public bool ShowAudio { get; set; } = true;
        public bool ShowTransforms { get; set; } = true;

        // ── Transform tool ────────────────────────────────────────────────────
        public enum TransformTool { None, Move, Rotate }
        public TransformTool ActiveTool { get; set; } = TransformTool.Move;
        public GameObject? HandleTarget { get; set; }

        // ── Collider edit mode ────────────────────────────────────────────────
        /// <summary>When true, face-drag handles are drawn instead of transform handles.</summary>
        public bool ColliderEditMode { get; set; } = false;

        // Collider handle: each face of a box/sphere/capsule gets a dot handle.
        // Axis: 0=+X 1=-X 2=+Y 3=-Y 4=+Z 5=-Z  (for sphere/capsule: 0=+R)
        public struct ColliderHandle { public Vector2 ScreenPos; public int Axis; }
        public readonly List<ColliderHandle> ColliderHandles = new();

        // ── Axis handles (screen-space tips, rebuilt each Render call) ────────
        public struct AxisHandle { public Vector2 ScreenTip; public int Axis; public float ShaftLength; }
        public readonly List<AxisHandle> LastHandles = new();

        // ── GL state ──────────────────────────────────────────────────────────
        private ElintriaEngine.Rendering.Scene.SceneShader? _shader;
        private int _vao, _vbo;
        private bool _ready;

        // ── Colours ───────────────────────────────────────────────────────────
        private static readonly Vector4 CX = new(0.95f, 0.25f, 0.25f, 1f);
        private static readonly Vector4 CY = new(0.25f, 0.90f, 0.25f, 1f);
        private static readonly Vector4 CZ = new(0.25f, 0.50f, 0.95f, 1f);
        private static readonly Vector4 CCam = new(1.00f, 0.80f, 0.10f, 1f);
        private static readonly Vector4 CDL = new(1.00f, 0.95f, 0.50f, 1f);
        private static readonly Vector4 CSL = new(1.00f, 0.65f, 0.20f, 1f);
        private static readonly Vector4 CCo = new(0.25f, 0.85f, 0.85f, 1f);
        private static readonly Vector4 CCoEdit = new(0.30f, 1.00f, 0.40f, 1f);
        private static readonly Vector4 CAu = new(0.55f, 0.55f, 1.00f, 1f);

        // ── Init ──────────────────────────────────────────────────────────────
        public void Init()
        {
            if (_ready) return;
            const string vert = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uVP;
void main(){ gl_Position = uVP * vec4(aPos,1.0); }";
            const string frag = @"#version 330 core
uniform vec4 uColor;
out vec4 FragColor;
void main(){ FragColor = uColor; }";
            _shader = ElintriaEngine.Rendering.Scene.SceneShader.Compile(vert, frag);

            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, 4096 * sizeof(float), IntPtr.Zero,
                          BufferUsageHint.DynamicDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
            GL.BindVertexArray(0);
            _ready = true;
        }

        // ── Main render ───────────────────────────────────────────────────────
        public void Render(Matrix4 view, Matrix4 proj, Vector3 camPos,
                           ElintriaEngine.Core.Scene? scene, RectangleF viewport)
        {
            if (!_ready) Init();
            if (!ShowAll || scene == null) return;

            LastHandles.Clear();
            var vpMat = view * proj;

            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            DrawDebugLines(view, proj, vpMat, depthPass: false);
            GL.Enable(EnableCap.DepthTest);
            DrawDebugLines(view, proj, vpMat, depthPass: true);
            GL.Disable(EnableCap.DepthTest);

            foreach (var go in scene.All())
            {
                if (!go.ActiveSelf) continue;

                if (ShowCameras && go.GetComponent<Camera>() != null)
                    DrawCameraGizmo(vpMat, view, proj, go, camPos, viewport);

                if (ShowLights)
                {
                    if (go.GetComponent<DirectionalLight>() != null)
                        DrawDirectionalLightGizmo(vpMat, go, camPos, viewport);
                    if (go.GetComponent<SpotLight>() != null)
                        DrawSpotLightGizmo(vpMat, go, camPos, viewport);
                }

                if (ShowColliders)
                {
                    if (go.GetComponent<BoxCollider>() is BoxCollider bc)
                    {
                        var tint = (ColliderEditMode && HandleTarget == go) ? CCoEdit : CCo;
                        DrawBoxWire(vpMat, go, bc.Center, bc.Size, tint);
                    }
                    if (go.GetComponent<SphereCollider>() is SphereCollider sc)
                    {
                        var tint = (ColliderEditMode && HandleTarget == go) ? CCoEdit : CCo;
                        // FIX: run center through the full LocalMatrix so the gizmo
                        // follows the GO's position, rotation, and scale.
                        var wc = Tr(go.Transform.LocalMatrix, sc.Center);
                        float r = sc.Radius * MaxScale(go);
                        DrawCircleRing(vpMat, wc, r, tint);
                        DrawCircleRingAxis(vpMat, wc, r, tint, 0);
                        DrawCircleRingAxis(vpMat, wc, r, tint, 2);
                    }
                    if (go.GetComponent<CapsuleCollider>() is CapsuleCollider cap)
                    {
                        var tint = (ColliderEditMode && HandleTarget == go) ? CCoEdit : CCo;
                        DrawCapsuleWire(vpMat, go, cap, tint);
                    }
                    if (go.GetComponent<BoxCollider2D>() is BoxCollider2D bc2)
                    {
                        var s = new Vector3(bc2.Width, bc2.Height, 0.02f);
                        DrawBoxWire(vpMat, go, bc2.Offset, s, CCo);
                    }
                    if (go.GetComponent<CircleCollider2D>() is CircleCollider2D cc2)
                    {
                        // FIX: same as sphere — use matrix instead of raw LocalPosition
                        var wc = Tr(go.Transform.LocalMatrix, cc2.Offset);
                        DrawCircleRing(vpMat, wc, cc2.Radius, CCo);
                    }
                    if (go.GetComponent<MeshCollider>() is MeshCollider mc)
                        DrawBoxWire(vpMat, go, Vector3.Zero, Vector3.One, Color4TintedCCo(mc.IsTrigger));
                }

                if (ShowAudio && go.GetComponent<AudioSource>() != null)
                    DrawCrossIcon(vpMat, go.Transform.LocalPosition, CAu, camPos);
            }

            ColliderHandles.Clear();
            if (ShowTransforms && HandleTarget != null)
            {
                if (ColliderEditMode)
                    DrawColliderEditHandles(vpMat, view, proj, HandleTarget, viewport);
                else if (ActiveTool == TransformTool.Move)
                    DrawMoveHandles(vpMat, view, proj, camPos, viewport);
                else if (ActiveTool == TransformTool.Rotate)
                    DrawRotateHandles(vpMat, view, proj, camPos, viewport);
            }

            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);

            PhysicsDebug.FlushSingleFrame();
        }

        // ── PhysicsDebug line rendering ───────────────────────────────────────
        private void DrawDebugLines(Matrix4 view, Matrix4 proj, Matrix4 vpMat, bool depthPass)
        {
            var lines = PhysicsDebug.Lines;
            if (lines.Count == 0) return;
            foreach (var line in lines)
            {
                if (line.DepthTest != depthPass) continue;
                Lines(vpMat, line.Color, 1.5f, line.Start, line.End);
            }
        }

        // ── Camera — frustum wireframe ────────────────────────────────────────
        private void DrawCameraGizmo(Matrix4 vpMat, Matrix4 view, Matrix4 proj,
            GameObject go, Vector3 camPos, RectangleF viewport)
        {
            var cam = go.GetComponent<Camera>()!;
            float fov = MathHelper.DegreesToRadians(cam.FieldOfView);
            float asp = viewport.Width / Math.Max(viewport.Height, 1f);
            float near = cam.NearClip;
            float far = Math.Min(cam.FarClip, 10f);

            float hn = MathF.Tan(fov * .5f) * near, wn = hn * asp;
            float hf = MathF.Tan(fov * .5f) * far, wf = hf * asp;
            var m = go.Transform.LocalMatrix;

            Vector3[] p =
            {
                Tr(m, new(-wn,-hn,-near)), Tr(m, new(wn,-hn,-near)),
                Tr(m, new( wn, hn,-near)), Tr(m, new(-wn, hn,-near)),
                Tr(m, new(-wf,-hf,-far)),  Tr(m, new(wf,-hf,-far)),
                Tr(m, new( wf, hf,-far)),  Tr(m, new(-wf, hf,-far)),
            };
            Lines(vpMat, CCam, 1.5f,
                p[0], p[1], p[1], p[2], p[2], p[3], p[3], p[0],
                p[4], p[5], p[5], p[6], p[6], p[7], p[7], p[4],
                p[0], p[4], p[1], p[5], p[2], p[6], p[3], p[7]);

            DrawCrossIcon(vpMat, go.Transform.LocalPosition, CCam, camPos);
        }

        // ── Directional light — sun disc + radial rays ────────────────────────
        private void DrawDirectionalLightGizmo(Matrix4 vpMat, GameObject go,
                                               Vector3 camPos, RectangleF viewport)
        {
            var dl = go.GetComponent<DirectionalLight>()!;
            var dir = dl.Direction.Normalized();
            var pos = go.Transform.LocalPosition;

            int n = 12; float r = 0.55f;
            var perp = Vector3.Cross(dir, Vector3.UnitY);
            if (perp.LengthSquared < 0.01f) perp = Vector3.Cross(dir, Vector3.UnitX);
            perp.Normalize();
            var perp2 = Vector3.Cross(dir, perp).Normalized();

            var ring = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * MathF.PI * 2f / n;
                ring[i] = pos + (perp * MathF.Cos(a) + perp2 * MathF.Sin(a)) * r;
            }
            for (int i = 0; i < n; i++)
                Lines(vpMat, CDL, 2f, ring[i], ring[(i + 1) % n]);

            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI * 2f / 8;
                var off = (perp * MathF.Cos(a) + perp2 * MathF.Sin(a)) * r;
                Lines(vpMat, CDL, 1.5f, pos + off, pos + off + dir * 1.4f);
            }
            DrawCrossIcon(vpMat, pos, CDL, camPos);
        }

        // ── Spotlight — cone outline with inner ring + shaft ──────────────────
        private void DrawSpotLightGizmo(Matrix4 vpMat, GameObject go,
                                        Vector3 camPos, RectangleF viewport)
        {
            var sl = go.GetComponent<SpotLight>()!;
            var dir = sl.Direction.Normalized();
            var pos = sl.Position;
            float coneR = sl.Range * MathF.Tan(MathHelper.DegreesToRadians(sl.SpotAngle));
            var tip = pos + dir * sl.Range;

            var perp = Vector3.Cross(dir, Vector3.UnitY);
            if (perp.LengthSquared < 0.01f) perp = Vector3.Cross(dir, Vector3.UnitX);
            perp.Normalize();
            var perp2 = Vector3.Cross(dir, perp).Normalized();

            int n = 20;
            var ring = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * MathF.PI * 2f / n;
                ring[i] = tip + (perp * MathF.Cos(a) + perp2 * MathF.Sin(a)) * coneR;
            }
            for (int i = 0; i < n; i++)
                Lines(vpMat, CSL, 1.5f, ring[i], ring[(i + 1) % n]);

            Lines(vpMat, CSL, 2f,
                pos, ring[0], pos, ring[n / 4], pos, ring[n / 2], pos, ring[3 * n / 4]);

            float innerR = sl.Range * 0.4f * MathF.Tan(MathHelper.DegreesToRadians(sl.SpotAngle));
            var innerTip = pos + dir * sl.Range * 0.4f;
            int ni = 12;
            var iRing = new Vector3[ni];
            for (int i = 0; i < ni; i++)
            {
                float a = i * MathF.PI * 2f / ni;
                iRing[i] = innerTip + (perp * MathF.Cos(a) + perp2 * MathF.Sin(a)) * innerR;
            }
            for (int i = 0; i < ni; i++)
                Lines(vpMat, new Vector4(CSL.X, CSL.Y, CSL.Z, 0.4f), 1f,
                    iRing[i], iRing[(i + 1) % ni]);

            DrawCrossIcon(vpMat, pos, CSL, camPos);
        }

        // ── Box collider wireframe ────────────────────────────────────────────
        // Uses Tr(LocalMatrix, ...) so the box always follows the GO correctly.
        private void DrawBoxWire(Matrix4 vpMat, GameObject go,
                                 Vector3 center, Vector3 size, Vector4 color)
        {
            var m = go.Transform.LocalMatrix;
            var h = size * .5f;
            var c = new Vector3[8];
            int i = 0;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                        c[i++] = Tr(m, center + new Vector3(sx * h.X, sy * h.Y, sz * h.Z));
            Lines(vpMat, color, 1f,
                c[0], c[1], c[0], c[2], c[0], c[4],
                c[7], c[6], c[7], c[5], c[7], c[3],
                c[1], c[3], c[1], c[5],
                c[2], c[6], c[2], c[3]);
            Lines(vpMat, color, 1f,
                c[0], c[1], c[1], c[3], c[3], c[2], c[2], c[0],
                c[4], c[5], c[5], c[7], c[7], c[6], c[6], c[4],
                c[0], c[4], c[1], c[5], c[2], c[6], c[3], c[7]);
        }

        // ── Circle ring helpers ───────────────────────────────────────────────
        // These receive an already world-transformed center — callers must run
        // the local center through Tr(LocalMatrix, ...) before passing it in.
        private void DrawCircleRing(Matrix4 vpMat, Vector3 center, float radius, Vector4 color)
        {
            int n = 20;
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * MathF.PI * 2f / n;
                pts[i] = center + new Vector3(MathF.Cos(a) * radius, 0, MathF.Sin(a) * radius);
            }
            for (int i = 0; i < n; i++)
                Lines(vpMat, color, 1f, pts[i], pts[(i + 1) % n]);
        }

        private void DrawCircleRingAxis(Matrix4 vpMat, Vector3 center, float radius,
                                        Vector4 color, int axis)
        {
            int n = 20;
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * MathF.PI * 2f / n;
                pts[i] = axis == 2
                    ? center + new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0)
                    : center + new Vector3(0, MathF.Cos(a) * radius, MathF.Sin(a) * radius);
            }
            for (int i = 0; i < n; i++)
                Lines(vpMat, color, 1f, pts[i], pts[(i + 1) % n]);
        }

        // ── Capsule wireframe ─────────────────────────────────────────────────
        private void DrawCapsuleWire(Matrix4 vpMat, GameObject go,
                                     CapsuleCollider cap, Vector4 color)
        {
            // FIX: transform center through the full LocalMatrix so the capsule
            // gizmo follows position, rotation, and scale.
            var m = go.Transform.LocalMatrix;
            var pos = Tr(m, cap.Center);
            var scale = go.Transform.LocalScale;
            float r = cap.Radius * Math.Max(scale.X, scale.Z);
            float h = Math.Max(0f, cap.Height * 0.5f * scale.Y - r);

            DrawCircleRing(vpMat, pos + Vector3.UnitY * h, r, color);
            DrawCircleRing(vpMat, pos - Vector3.UnitY * h, r, color);
            Lines(vpMat, color, 1f,
                pos + new Vector3(r, h, 0), pos + new Vector3(r, -h, 0),
                pos + new Vector3(-r, h, 0), pos + new Vector3(-r, -h, 0),
                pos + new Vector3(0, h, r), pos + new Vector3(0, -h, r),
                pos + new Vector3(0, h, -r), pos + new Vector3(0, -h, -r));
        }

        // ── Collider face-drag edit handles ───────────────────────────────────
        private void DrawColliderEditHandles(Matrix4 vpMat, Matrix4 view, Matrix4 proj,
                                             GameObject go, RectangleF viewport)
        {
            ColliderHandles.Clear();
            var m = go.Transform.LocalMatrix;

            if (go.GetComponent<BoxCollider>() is BoxCollider bc)
            {
                // FIX: center and all face handle positions go through LocalMatrix
                var c = Tr(m, bc.Center);
                var faces = new (Vector3 fp, int axis)[]
                {
                    (Tr(m, bc.Center + Vector3.UnitX * (bc.Size.X * 0.5f)), 0),
                    (Tr(m, bc.Center - Vector3.UnitX * (bc.Size.X * 0.5f)), 1),
                    (Tr(m, bc.Center + Vector3.UnitY * (bc.Size.Y * 0.5f)), 2),
                    (Tr(m, bc.Center - Vector3.UnitY * (bc.Size.Y * 0.5f)), 3),
                    (Tr(m, bc.Center + Vector3.UnitZ * (bc.Size.Z * 0.5f)), 4),
                    (Tr(m, bc.Center - Vector3.UnitZ * (bc.Size.Z * 0.5f)), 5),
                };
                foreach (var (fp, ax) in faces)
                {
                    Lines(vpMat, CCoEdit, 2f, c, fp);
                    ColliderHandles.Add(new ColliderHandle
                    {
                        ScreenPos = WorldToScreen(fp, vpMat, proj, viewport),
                        Axis = ax
                    });
                    DrawDotHandle(vpMat, fp, CCoEdit, viewport);
                }
                DrawBoxWire(vpMat, go, bc.Center, bc.Size, CCoEdit);
            }
            else if (go.GetComponent<SphereCollider>() is SphereCollider sc)
            {
                // FIX: center through matrix
                var c = Tr(m, sc.Center);
                float r = sc.Radius * MaxScale(go);
                var axes = new (Vector3 dir, int ax)[]
                {
                    ( Vector3.UnitX, 0), (-Vector3.UnitX, 1),
                    ( Vector3.UnitY, 2), (-Vector3.UnitY, 3),
                    ( Vector3.UnitZ, 4), (-Vector3.UnitZ, 5),
                };
                foreach (var (dir, ax) in axes)
                {
                    var fp = c + dir * r;
                    Lines(vpMat, CCoEdit, 1f, c, fp);
                    ColliderHandles.Add(new ColliderHandle
                    {
                        ScreenPos = WorldToScreen(fp, vpMat, proj, viewport),
                        Axis = ax
                    });
                    DrawDotHandle(vpMat, fp, CCoEdit, viewport);
                }
                DrawCircleRing(vpMat, c, r, CCoEdit);
                DrawCircleRingAxis(vpMat, c, r, CCoEdit, 2);
            }
            else if (go.GetComponent<CapsuleCollider>() is CapsuleCollider cap)
            {
                // FIX: center through matrix, scale applied properly
                var c = Tr(m, cap.Center);
                var scale = go.Transform.LocalScale;
                float r = cap.Radius * Math.Max(scale.X, scale.Z);
                float h = cap.Height * 0.5f * scale.Y;
                var handles = new (Vector3 p, int ax)[]
                {
                    (c + Vector3.UnitX * r, 0),
                    (c + Vector3.UnitY * h, 2),
                    (c - Vector3.UnitY * h, 3),
                };
                foreach (var (fp, ax) in handles)
                {
                    Lines(vpMat, CCoEdit, 1f, c, fp);
                    ColliderHandles.Add(new ColliderHandle
                    {
                        ScreenPos = WorldToScreen(fp, vpMat, proj, viewport),
                        Axis = ax
                    });
                    DrawDotHandle(vpMat, fp, CCoEdit, viewport);
                }
                DrawCapsuleWire(vpMat, go, cap, CCoEdit);
            }
        }

        private void DrawDotHandle(Matrix4 vpMat, Vector3 worldPos,
                                   Vector4 color, RectangleF viewport)
        {
            float s = 0.06f;
            Lines(vpMat, color, 3f,
                worldPos - Vector3.UnitX * s, worldPos + Vector3.UnitX * s,
                worldPos - Vector3.UnitY * s, worldPos + Vector3.UnitY * s,
                worldPos - Vector3.UnitZ * s, worldPos + Vector3.UnitZ * s);
        }

        private static Vector4 Color4TintedCCo(bool trigger) =>
            trigger ? new Vector4(0.8f, 0.4f, 0.1f, 1f) : new Vector4(0.25f, 0.85f, 0.85f, 1f);

        // ── Small cross icon at a world position ──────────────────────────────
        private void DrawCrossIcon(Matrix4 vpMat, Vector3 pos, Vector4 color, Vector3 camPos)
        {
            float s = Math.Clamp((camPos - pos).Length * 0.06f, 0.06f, 1.2f);
            Lines(vpMat, color, 2f,
                pos - Vector3.UnitX * s, pos + Vector3.UnitX * s,
                pos - Vector3.UnitY * s, pos + Vector3.UnitY * s,
                pos - Vector3.UnitZ * s, pos + Vector3.UnitZ * s);
        }

        // ── Move handles — coloured arrows + XZ square ────────────────────────
        private void DrawMoveHandles(Matrix4 vpMat, Matrix4 view, Matrix4 proj,
                                     Vector3 camPos, RectangleF viewport)
        {
            if (HandleTarget == null) return;
            var pos = HandleTarget.Transform.LocalPosition;
            float dist = Math.Max((camPos - pos).Length, 0.1f);
            float scale = dist * 0.20f;

            LastHandles.Clear();

            var axDefs = new (Vector3 dir, Vector4 col, int axis)[]
            {
                (Vector3.UnitX, CX, 0),
                (Vector3.UnitY, CY, 1),
                (Vector3.UnitZ, CZ, 2),
            };

            foreach (var (dir, col, axis) in axDefs)
            {
                var tip = pos + dir * scale;
                var back = dir * (scale * 0.20f);

                Lines(vpMat, col, 3f, pos, tip);

                var perp = Vector3.Cross(dir, Vector3.UnitY);
                if (perp.LengthSquared < 0.001f) perp = Vector3.Cross(dir, Vector3.UnitZ);
                perp.Normalize(); perp *= scale * 0.055f;
                var q90 = Quaternion.FromAxisAngle(dir, MathF.PI * 0.5f);
                var p2 = Vector3.Transform(perp, q90);
                Lines(vpMat, col, 3f,
                    tip, tip - back + perp,
                    tip, tip - back - perp,
                    tip, tip - back + p2,
                    tip, tip - back - p2);

                var tipScr = WorldToScreen(tip, view, proj, viewport);
                var midScr = WorldToScreen(pos + dir * scale * 0.5f, view, proj, viewport);
                float shaftLen = Vector2.Distance(tipScr, WorldToScreen(pos, view, proj, viewport));
                LastHandles.Add(new AxisHandle { ScreenTip = tipScr, Axis = axis, ShaftLength = shaftLen });
                LastHandles.Add(new AxisHandle { ScreenTip = midScr, Axis = axis, ShaftLength = shaftLen });
            }

            float ps = scale * 0.22f;
            Lines(vpMat, new Vector4(1f, 1f, 0f, .8f), 1.5f,
                pos + Vector3.UnitX * ps * 0.4f + Vector3.UnitZ * ps * 0.4f,
                pos + Vector3.UnitX * ps + Vector3.UnitZ * ps * 0.4f,
                pos + Vector3.UnitX * ps + Vector3.UnitZ * ps,
                pos + Vector3.UnitX * ps * 0.4f + Vector3.UnitZ * ps,
                pos + Vector3.UnitX * ps * 0.4f + Vector3.UnitZ * ps * 0.4f);
            LastHandles.Add(new AxisHandle
            {
                ScreenTip = WorldToScreen(
                    pos + (Vector3.UnitX + Vector3.UnitZ).Normalized() * ps * 0.7f,
                    view, proj, viewport),
                Axis = 3
            });
        }

        // ── Rotate handles — three rings ──────────────────────────────────────
        private void DrawRotateHandles(Matrix4 vpMat, Matrix4 view, Matrix4 proj,
                                       Vector3 camPos, RectangleF viewport)
        {
            if (HandleTarget == null) return;
            var pos = HandleTarget.Transform.LocalPosition;
            float dist = Math.Max((camPos - pos).Length, 0.1f);
            float r = dist * 0.20f;
            int n = 48;

            LastHandles.Clear();

            void Ring(Vector3 a1, Vector3 a2, Vector4 col, int axis)
            {
                var pts = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    float ang = i * MathF.PI * 2f / n;
                    pts[i] = pos + a1 * MathF.Cos(ang) * r + a2 * MathF.Sin(ang) * r;
                }
                for (int i = 0; i < n; i++)
                    Lines(vpMat, col, 2.5f, pts[i], pts[(i + 1) % n]);

                LastHandles.Add(new AxisHandle { ScreenTip = WorldToScreen(pos + a1 * r, view, proj, viewport), Axis = axis });
                LastHandles.Add(new AxisHandle { ScreenTip = WorldToScreen(pos - a1 * r, view, proj, viewport), Axis = axis });
                LastHandles.Add(new AxisHandle { ScreenTip = WorldToScreen(pos + a2 * r, view, proj, viewport), Axis = axis });
            }

            Ring(Vector3.UnitY, Vector3.UnitZ, CX, 0);
            Ring(Vector3.UnitX, Vector3.UnitZ, CY, 1);
            Ring(Vector3.UnitX, Vector3.UnitY, CZ, 2);
        }

        // ── GL primitives ─────────────────────────────────────────────────────
        private void Lines(Matrix4 vpMat, Vector4 color, float width, params Vector3[] pts)
        {
            if (pts.Length < 2 || _shader == null || pts.Length % 2 != 0) return;

            var data = new float[pts.Length * 3];
            for (int i = 0; i < pts.Length; i++)
            { data[i * 3] = pts[i].X; data[i * 3 + 1] = pts[i].Y; data[i * 3 + 2] = pts[i].Z; }

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float),
                          data, BufferUsageHint.DynamicDraw);

            _shader.Use();
            GL.UniformMatrix4(GL.GetUniformLocation(_shader.Program, "uVP"), false, ref vpMat);
            GL.Uniform4(GL.GetUniformLocation(_shader.Program, "uColor"),
                        color.X, color.Y, color.Z, color.W);
            GL.LineWidth(Math.Clamp(width, 1f, 8f));
            GL.DrawArrays(PrimitiveType.Lines, 0, pts.Length);
            GL.BindVertexArray(0);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Transforms a local-space point into world space via the GO's LocalMatrix.</summary>
        private static Vector3 Tr(Matrix4 m, Vector3 v)
            => (m * new Vector4(v, 1f)).Xyz;

        /// <summary>Returns the largest scale component (used for uniform sphere radius scaling).</summary>
        private static float MaxScale(GameObject go)
        {
            var s = go.Transform.LocalScale;
            return Math.Max(s.X, Math.Max(s.Y, s.Z));
        }

        /// <summary>
        /// Converts a world-space point to UI-space screen coordinates (top-left origin).
        /// The <paramref name="viewport"/> must be in UI space (same origin as mouse events).
        /// </summary>
        public static Vector2 WorldToScreen(Vector3 world, Matrix4 view, Matrix4 proj, RectangleF viewport)
        {
            var clip = new Vector4(world, 1f) * (view * proj);
            if (MathF.Abs(clip.W) < 1e-5f) return new Vector2(-99999, -99999);
            var ndc = clip.Xyz / clip.W;
            return new Vector2(
                viewport.X + (ndc.X * 0.5f + 0.5f) * viewport.Width,
                viewport.Y + (1f - (ndc.Y * 0.5f + 0.5f)) * viewport.Height);
        }

        public void Dispose()
        {
            if (_vao != 0) GL.DeleteVertexArray(_vao);
            if (_vbo != 0) GL.DeleteBuffer(_vbo);
            _shader?.Dispose();
        }
    }
}