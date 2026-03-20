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
    ///
    /// KEY RULE — world-space point from a local offset:
    ///
    ///   worldPoint = LocalPosition + Quaternion.Rotate(localOffset * LocalScale)
    ///
    /// We do NOT use Tr(LocalMatrix, point) for collider centers because
    /// OpenTK's LocalMatrix = CreateScale * CreateRotation * CreateTranslation
    /// causes the translation to be multiplied by scale, giving a wrong result.
    ///
    /// Use the helper:
    ///   LocalToWorld(go, localOffset)
    ///     = go.LocalPosition + RotateByGO(go, localOffset * go.LocalScale)
    ///
    /// Camera frustum corners use LocalPosition + Rotate(corner) — same rule.
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
        public bool ColliderEditMode { get; set; } = false;

        public struct ColliderHandle { public Vector2 ScreenPos; public int Axis; }
        public readonly List<ColliderHandle> ColliderHandles = new();

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

            DrawDebugLines(vpMat, depthPass: false);
            GL.Enable(EnableCap.DepthTest);
            DrawDebugLines(vpMat, depthPass: true);
            GL.Disable(EnableCap.DepthTest);

            foreach (var go in scene.All())
            {
                if (!go.ActiveSelf) continue;

                if (ShowCameras && go.GetComponent<Camera>() != null)
                    DrawCameraGizmo(vpMat, go, camPos, viewport);

                if (ShowLights)
                {
                    if (go.GetComponent<DirectionalLight>() != null)
                        DrawDirectionalLightGizmo(vpMat, go, camPos);
                    if (go.GetComponent<SpotLight>() != null)
                        DrawSpotLightGizmo(vpMat, go, camPos);
                }

                if (ShowColliders)
                {
                    if (go.GetComponent<BoxCollider>() is BoxCollider bc)
                    {
                        var tint = (ColliderEditMode && HandleTarget == go) ? CCoEdit : CCo;
                        DrawBoxCollider(vpMat, go, bc, tint);
                    }
                    if (go.GetComponent<SphereCollider>() is SphereCollider sc)
                    {
                        var tint = (ColliderEditMode && HandleTarget == go) ? CCoEdit : CCo;
                        DrawSphereCollider(vpMat, go, sc, tint);
                    }
                    if (go.GetComponent<CapsuleCollider>() is CapsuleCollider cap)
                    {
                        var tint = (ColliderEditMode && HandleTarget == go) ? CCoEdit : CCo;
                        DrawCapsuleCollider(vpMat, go, cap, tint);
                    }
                    if (go.GetComponent<BoxCollider2D>() is BoxCollider2D bc2)
                        DrawBoxCollider2D(vpMat, go, bc2, CCo);
                    if (go.GetComponent<CircleCollider2D>() is CircleCollider2D cc2)
                        DrawCircleCollider2D(vpMat, go, cc2, CCo);
                    if (go.GetComponent<MeshCollider>() is MeshCollider mc)
                        DrawBoxCollider(vpMat, go,
                            new BoxCollider { Center = Vector3.Zero, Size = Vector3.One },
                            Color4TintedCCo(mc.IsTrigger));
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

        // ── PhysicsDebug lines ────────────────────────────────────────────────
        private void DrawDebugLines(Matrix4 vpMat, bool depthPass)
        {
            var lines = PhysicsDebug.Lines;
            if (lines.Count == 0) return;
            foreach (var line in lines)
            {
                if (line.DepthTest != depthPass) continue;
                Lines(vpMat, line.Color, 1.5f, line.Start, line.End);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  World-space transform helpers
        //
        //  LocalToWorld(go, localOffset):
        //    Converts a LOCAL-SPACE point (expressed relative to the GO's pivot)
        //    into WORLD-SPACE, correctly applying position + rotation + scale.
        //
        //    Formula:  worldPos = go.LocalPosition + Rotate(localOffset * go.LocalScale)
        //
        //    Do NOT use Tr(LocalMatrix, point) — OpenTK's LocalMatrix =
        //    Scale*Rotate*Translate causes the translation to be pre-multiplied
        //    by scale, producing wrong world positions.
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts a local-space offset into world space using
        /// position + rotation(offset * scale).
        /// This is the correct formula for placing collider gizmos in world space.
        /// </summary>
        private static Vector3 LocalToWorld(GameObject go, Vector3 localOffset)
        {
            var t = go.Transform;
            var scaled = localOffset * t.LocalScale;
            var rotated = Vector3.Transform(scaled, GetRotation(t));
            return t.LocalPosition + rotated;
        }

        /// <summary>
        /// Returns the world-space rotation quaternion from the transform's Euler angles.
        /// </summary>
        private static Quaternion GetRotation(Transform t)
            => Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(t.LocalEulerAngles.X),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Y),
                MathHelper.DegreesToRadians(t.LocalEulerAngles.Z));

        /// <summary>
        /// Rotates a direction vector by the GO's rotation only (no scale, no translation).
        /// Used for axis directions on collider handles.
        /// </summary>
        private static Vector3 RotateDir(GameObject go, Vector3 dir)
            => Vector3.Transform(dir, GetRotation(go.Transform));

        // ═════════════════════════════════════════════════════════════════════
        //  Collider gizmo drawing
        //  All use LocalToWorld so the gizmo always sits exactly on the GO.
        // ═════════════════════════════════════════════════════════════════════

        // ── BoxCollider ───────────────────────────────────────────────────────
        private void DrawBoxCollider(Matrix4 vpMat, GameObject go,
                                     BoxCollider bc, Vector4 color)
        {
            var t = go.Transform;
            var scale = t.LocalScale;
            var rot = GetRotation(t);
            var worldCenter = LocalToWorld(go, bc.Center);
            var half = bc.Size * 0.5f * scale;

            // Eight corners in local space relative to collider center, then rotate
            var c = new Vector3[8];
            int i = 0;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        var local = new Vector3(sx * half.X, sy * half.Y, sz * half.Z);
                        c[i++] = worldCenter + Vector3.Transform(local, rot);
                    }

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

        // ── SphereCollider ────────────────────────────────────────────────────
        private void DrawSphereCollider(Matrix4 vpMat, GameObject go,
                                        SphereCollider sc, Vector4 color)
        {
            var worldCenter = LocalToWorld(go, sc.Center);
            float r = sc.Radius * MaxScale(go);
            DrawCircleRing(vpMat, worldCenter, r, color);
            DrawCircleRingAxis(vpMat, worldCenter, r, color, 0);
            DrawCircleRingAxis(vpMat, worldCenter, r, color, 2);
        }

        // ── CapsuleCollider ───────────────────────────────────────────────────
        private void DrawCapsuleCollider(Matrix4 vpMat, GameObject go,
                                         CapsuleCollider cap, Vector4 color)
        {
            var t = go.Transform;
            var scale = t.LocalScale;
            var worldCenter = LocalToWorld(go, cap.Center);
            float r = cap.Radius * Math.Max(scale.X, scale.Z);
            float h = Math.Max(0f, cap.Height * 0.5f * scale.Y - r);

            DrawCircleRing(vpMat, worldCenter + Vector3.UnitY * h, r, color);
            DrawCircleRing(vpMat, worldCenter - Vector3.UnitY * h, r, color);
            Lines(vpMat, color, 1f,
                worldCenter + new Vector3(r, h, 0), worldCenter + new Vector3(r, -h, 0),
                worldCenter + new Vector3(-r, h, 0), worldCenter + new Vector3(-r, -h, 0),
                worldCenter + new Vector3(0, h, r), worldCenter + new Vector3(0, -h, r),
                worldCenter + new Vector3(0, h, -r), worldCenter + new Vector3(0, -h, -r));
        }

        // ── BoxCollider2D ─────────────────────────────────────────────────────
        private void DrawBoxCollider2D(Matrix4 vpMat, GameObject go,
                                       BoxCollider2D bc2, Vector4 color)
        {
            var fakeBox = new BoxCollider
            {
                Center = bc2.Offset,
                Size = new Vector3(bc2.Width, bc2.Height, 0.02f)
            };
            DrawBoxCollider(vpMat, go, fakeBox, color);
        }

        // ── CircleCollider2D ──────────────────────────────────────────────────
        private void DrawCircleCollider2D(Matrix4 vpMat, GameObject go,
                                          CircleCollider2D cc2, Vector4 color)
        {
            var worldCenter = LocalToWorld(go, cc2.Offset);
            DrawCircleRing(vpMat, worldCenter, cc2.Radius, color);
        }

        // ── Circle ring helpers ───────────────────────────────────────────────
        // These receive an already world-transformed center.
        private void DrawCircleRing(Matrix4 vpMat, Vector3 center, float radius, Vector4 color)
        {
            int n = 24;
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
            int n = 24;
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

        // ── Camera frustum wireframe ──────────────────────────────────────────
        private void DrawCameraGizmo(Matrix4 vpMat, GameObject go,
                                     Vector3 camPos, RectangleF viewport)
        {
            var cam = go.GetComponent<Camera>()!;
            float fov = MathHelper.DegreesToRadians(cam.FieldOfView);
            float asp = viewport.Width / Math.Max(viewport.Height, 1f);
            float near = cam.NearClip;
            float far = Math.Min(cam.FarClip, 10f);

            float hn = MathF.Tan(fov * .5f) * near, wn = hn * asp;
            float hf = MathF.Tan(fov * .5f) * far, wf = hf * asp;

            // Frustum corners are in view space (no object scale applied).
            // Use LocalPosition + Rotate(corner) — NOT LocalMatrix, which would
            // multiply the translation by scale and misplace the gizmo.
            var pos = go.Transform.LocalPosition;
            var rot = GetRotation(go.Transform);
            Vector3 W(Vector3 v) => pos + Vector3.Transform(v, rot);

            Vector3[] p =
            {
                W(new(-wn,-hn,-near)), W(new(wn,-hn,-near)),
                W(new( wn, hn,-near)), W(new(-wn, hn,-near)),
                W(new(-wf,-hf,-far)),  W(new(wf,-hf,-far)),
                W(new( wf, hf,-far)),  W(new(-wf, hf,-far)),
            };
            Lines(vpMat, CCam, 1.5f,
                p[0], p[1], p[1], p[2], p[2], p[3], p[3], p[0],
                p[4], p[5], p[5], p[6], p[6], p[7], p[7], p[4],
                p[0], p[4], p[1], p[5], p[2], p[6], p[3], p[7]);
            DrawCrossIcon(vpMat, pos, CCam, camPos);
        }

        // ── Directional light ─────────────────────────────────────────────────
        private void DrawDirectionalLightGizmo(Matrix4 vpMat, GameObject go, Vector3 camPos)
        {
            var dl = go.GetComponent<DirectionalLight>()!;
            var dir = dl.Direction.Normalized();
            var pos = go.Transform.LocalPosition;

            // Use the light's actual color for the gizmo tint
            var lightCol = new Vector4(dl.ColorR, dl.ColorG, dl.ColorB, 1f);

            // Build perpendicular axes for the ring
            var perp = Vector3.Cross(dir, Vector3.UnitY);
            if (perp.LengthSquared < 0.01f) perp = Vector3.Cross(dir, Vector3.UnitX);
            perp.Normalize();
            var perp2 = Vector3.Cross(dir, perp).Normalized();

            // Draw a ring at the light origin perpendicular to its direction
            int n = 16; float r = 0.5f;
            var ring = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * MathF.PI * 2f / n;
                ring[i] = pos + (perp * MathF.Cos(a) + perp2 * MathF.Sin(a)) * r;
            }
            for (int i = 0; i < n; i++)
                Lines(vpMat, lightCol, 2f, ring[i], ring[(i + 1) % n]);

            // Draw 8 parallel rays from the ring — these show the light direction clearly
            float rayLen = 1.6f;
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI * 2f / 8;
                var off = (perp * MathF.Cos(a) + perp2 * MathF.Sin(a)) * r;
                var start = pos + off;
                var end = start + dir * rayLen;
                Lines(vpMat, lightCol, 1.5f, start, end);
                // Small arrowhead at end of each ray
                var side = off.Normalized() * 0.07f;
                Lines(vpMat, lightCol, 1.5f,
                    end, end - dir * 0.15f + side,
                    end, end - dir * 0.15f - side);
            }

            DrawCrossIcon(vpMat, pos, lightCol, camPos);
        }

        // ── Spotlight ─────────────────────────────────────────────────────────
        private void DrawSpotLightGizmo(Matrix4 vpMat, GameObject go, Vector3 camPos)
        {
            var sl = go.GetComponent<SpotLight>()!;
            var dir = sl.Direction.Normalized();
            var pos = sl.Position;

            // Use the light's actual color for the gizmo
            var lightCol = new Vector4(sl.ColorR, sl.ColorG, sl.ColorB, 1f);
            var lightColFade = new Vector4(sl.ColorR, sl.ColorG, sl.ColorB, 0.35f);

            // Perpendicular axes for drawing circles
            var perp = Vector3.Cross(dir, Vector3.UnitY);
            if (perp.LengthSquared < 0.01f) perp = Vector3.Cross(dir, Vector3.UnitX);
            perp.Normalize();
            var perp2 = Vector3.Cross(dir, perp).Normalized();

            // Outer cone tip ring at full range
            float outerR = sl.Range * MathF.Tan(MathHelper.DegreesToRadians(sl.SpotAngle));
            var coneBase = pos + dir * sl.Range;

            int n = 24;
            var outerRing = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * MathF.PI * 2f / n;
                outerRing[i] = coneBase + (perp * MathF.Cos(a) + perp2 * MathF.Sin(a)) * outerR;
            }
            for (int i = 0; i < n; i++)
                Lines(vpMat, lightCol, 1.5f, outerRing[i], outerRing[(i + 1) % n]);

            // Four lines from apex to outer ring edge (the cone silhouette)
            Lines(vpMat, lightCol, 2f,
                pos, outerRing[0],
                pos, outerRing[n / 4],
                pos, outerRing[n / 2],
                pos, outerRing[3 * n / 4]);

            // Inner cone ring — shows the hard/soft blend edge
            // Inner angle = SpotAngle * (1 - BlendFraction)
            float innerAngle = sl.SpotAngle * (1f - Math.Clamp(sl.BlendFraction, 0f, 0.99f));
            float innerR = sl.Range * MathF.Tan(MathHelper.DegreesToRadians(innerAngle));
            var innerBase = pos + dir * sl.Range;
            int ni = 24;
            var innerRing = new Vector3[ni];
            for (int i = 0; i < ni; i++)
            {
                float a = i * MathF.PI * 2f / ni;
                innerRing[i] = innerBase + (perp * MathF.Cos(a) + perp2 * MathF.Sin(a)) * innerR;
            }
            for (int i = 0; i < ni; i++)
                Lines(vpMat, lightColFade, 1f, innerRing[i], innerRing[(i + 1) % ni]);

            // Center axis line from apex to base (shows direction clearly)
            Lines(vpMat, lightCol, 1.5f, pos, coneBase);

            DrawCrossIcon(vpMat, pos, lightCol, camPos);
        }

        // ── Collider face-drag edit handles ───────────────────────────────────
        private void DrawColliderEditHandles(Matrix4 vpMat, Matrix4 view, Matrix4 proj,
                                             GameObject go, RectangleF viewport)
        {
            ColliderHandles.Clear();

            if (go.GetComponent<BoxCollider>() is BoxCollider bc)
            {
                var worldCenter = LocalToWorld(go, bc.Center);
                var scale = go.Transform.LocalScale;
                var faces = new (Vector3 fp, int axis)[]
                {
                    (LocalToWorld(go, bc.Center + Vector3.UnitX * (bc.Size.X * 0.5f)), 0),
                    (LocalToWorld(go, bc.Center - Vector3.UnitX * (bc.Size.X * 0.5f)), 1),
                    (LocalToWorld(go, bc.Center + Vector3.UnitY * (bc.Size.Y * 0.5f)), 2),
                    (LocalToWorld(go, bc.Center - Vector3.UnitY * (bc.Size.Y * 0.5f)), 3),
                    (LocalToWorld(go, bc.Center + Vector3.UnitZ * (bc.Size.Z * 0.5f)), 4),
                    (LocalToWorld(go, bc.Center - Vector3.UnitZ * (bc.Size.Z * 0.5f)), 5),
                };
                foreach (var (fp, ax) in faces)
                {
                    Lines(vpMat, CCoEdit, 2f, worldCenter, fp);
                    ColliderHandles.Add(new ColliderHandle
                    {
                        ScreenPos = WorldToScreen(fp, view, proj, viewport),
                        Axis = ax
                    });
                    DrawDotHandle(vpMat, fp);
                }
                DrawBoxCollider(vpMat, go, bc, CCoEdit);
            }
            else if (go.GetComponent<SphereCollider>() is SphereCollider sc)
            {
                var worldCenter = LocalToWorld(go, sc.Center);
                float r = sc.Radius * MaxScale(go);
                var axes = new (Vector3 dir, int ax)[]
                {
                    ( Vector3.UnitX, 0), (-Vector3.UnitX, 1),
                    ( Vector3.UnitY, 2), (-Vector3.UnitY, 3),
                    ( Vector3.UnitZ, 4), (-Vector3.UnitZ, 5),
                };
                foreach (var (dir, ax) in axes)
                {
                    var fp = worldCenter + dir * r;
                    Lines(vpMat, CCoEdit, 1f, worldCenter, fp);
                    ColliderHandles.Add(new ColliderHandle
                    {
                        ScreenPos = WorldToScreen(fp, view, proj, viewport),
                        Axis = ax
                    });
                    DrawDotHandle(vpMat, fp);
                }
                DrawCircleRing(vpMat, worldCenter, r, CCoEdit);
                DrawCircleRingAxis(vpMat, worldCenter, r, CCoEdit, 2);
            }
            else if (go.GetComponent<CapsuleCollider>() is CapsuleCollider cap)
            {
                var scale = go.Transform.LocalScale;
                var worldCenter = LocalToWorld(go, cap.Center);
                float r = cap.Radius * Math.Max(scale.X, scale.Z);
                float h = cap.Height * 0.5f * scale.Y;
                var handles = new (Vector3 p, int ax)[]
                {
                    (worldCenter + Vector3.UnitX * r, 0),
                    (worldCenter + Vector3.UnitY * h, 2),
                    (worldCenter - Vector3.UnitY * h, 3),
                };
                foreach (var (fp, ax) in handles)
                {
                    Lines(vpMat, CCoEdit, 1f, worldCenter, fp);
                    ColliderHandles.Add(new ColliderHandle
                    {
                        ScreenPos = WorldToScreen(fp, view, proj, viewport),
                        Axis = ax
                    });
                    DrawDotHandle(vpMat, fp);
                }
                DrawCapsuleCollider(vpMat, go, cap, CCoEdit);
            }
        }

        private void DrawDotHandle(Matrix4 vpMat, Vector3 worldPos)
        {
            float s = 0.06f;
            Lines(vpMat, CCoEdit, 3f,
                worldPos - Vector3.UnitX * s, worldPos + Vector3.UnitX * s,
                worldPos - Vector3.UnitY * s, worldPos + Vector3.UnitY * s,
                worldPos - Vector3.UnitZ * s, worldPos + Vector3.UnitZ * s);
        }

        private static Vector4 Color4TintedCCo(bool trigger) =>
            trigger ? new Vector4(0.8f, 0.4f, 0.1f, 1f) : new Vector4(0.25f, 0.85f, 0.85f, 1f);

        // ── Cross icon ────────────────────────────────────────────────────────
        private void DrawCrossIcon(Matrix4 vpMat, Vector3 pos, Vector4 color, Vector3 camPos)
        {
            float s = Math.Clamp((camPos - pos).Length * 0.06f, 0.06f, 1.2f);
            Lines(vpMat, color, 2f,
                pos - Vector3.UnitX * s, pos + Vector3.UnitX * s,
                pos - Vector3.UnitY * s, pos + Vector3.UnitY * s,
                pos - Vector3.UnitZ * s, pos + Vector3.UnitZ * s);
        }

        // ── Move handles ──────────────────────────────────────────────────────
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
                ScreenTip = WorldToScreen(pos + (Vector3.UnitX + Vector3.UnitZ).Normalized() * ps * 0.7f,
                    view, proj, viewport),
                Axis = 3
            });
        }

        // ── Rotate handles ────────────────────────────────────────────────────
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

        /// <summary>
        /// Transforms a local-space point through a 4x4 matrix (column-vector convention).
        /// Only used for camera frustum corners which are expressed in camera local space.
        /// Do NOT use this for collider centers — use LocalToWorld() instead.
        /// </summary>
        private static Vector3 TrMatrix(Matrix4 m, Vector3 v)
            => (m * new Vector4(v, 1f)).Xyz;

        private static float MaxScale(GameObject go)
        {
            var s = go.Transform.LocalScale;
            return Math.Max(s.X, Math.Max(s.Y, s.Z));
        }

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