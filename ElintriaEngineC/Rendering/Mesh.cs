using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Collections.Generic;

namespace Elintria.Engine.Rendering
{
    /// <summary>
    /// Vertex layout: Position, Normal, UV, Tangent, Color.
    /// All fields optional — normals/UVs default to zero if not supplied.
    /// </summary>
    public struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;
        public Vector3 Tangent;
        public Vector4 Color;   // vertex color (default white)

        public static readonly int Stride = System.Runtime.InteropServices.Marshal.SizeOf<Vertex>();

        public Vertex(Vector3 position)
        {
            Position = position;
            Normal = Vector3.UnitY;
            UV = Vector2.Zero;
            Tangent = Vector3.UnitX;
            Color = Vector4.One;
        }

        public Vertex(Vector3 position, Vector3 normal, Vector2 uv)
        {
            Position = position;
            Normal = normal;
            UV = uv;
            Tangent = Vector3.UnitX;
            Color = Vector4.One;
        }

        public Vertex(Vector3 position, Vector3 normal, Vector2 uv,
                      Vector3 tangent, Vector4 color)
        {
            Position = position;
            Normal = normal;
            UV = uv;
            Tangent = tangent;
            Color = color;
        }
    }

    /// <summary>
    /// CPU + GPU mesh. Owns a VAO/VBO/EBO.
    ///
    /// Usage:
    ///   var mesh = new Mesh("MyMesh");
    ///   mesh.SetVertices(verts);
    ///   mesh.SetIndices(indices);
    ///   mesh.Upload();           // send to GPU
    ///   mesh.Draw();             // GL draw call
    ///
    /// Primitive factories:
    ///   Mesh.CreateQuad()
    ///   Mesh.CreateCube()
    ///   Mesh.CreateSphere(rings, slices)
    ///   Mesh.CreatePlane(subdivisions)
    /// </summary>
    public class Mesh : System.IDisposable
    {
        // ------------------------------------------------------------------
        // Identity / metadata
        // ------------------------------------------------------------------
        public string Name { get; set; }
        public Bounds Bounds { get; private set; }

        // ------------------------------------------------------------------
        // CPU data
        // ------------------------------------------------------------------
        public Vertex[] Vertices { get; private set; } = System.Array.Empty<Vertex>();
        public uint[] Indices { get; private set; } = System.Array.Empty<uint>();

        public int VertexCount => Vertices.Length;
        public int IndexCount => Indices.Length;
        public int TriangleCount => Indices.Length / 3;

        // ------------------------------------------------------------------
        // GPU handles
        // ------------------------------------------------------------------
        private int _vao = -1, _vbo = -1, _ebo = -1;
        private bool _uploaded = false;
        private bool _dirty = true;

        public PrimitiveType DrawMode { get; set; } = PrimitiveType.Triangles;

        // ------------------------------------------------------------------
        // Constructor
        // ------------------------------------------------------------------
        public Mesh(string name = "Mesh")
        {
            Name = name;
        }

        // ------------------------------------------------------------------
        // Data setters (mark dirty so Upload() re-sends on next call)
        // ------------------------------------------------------------------
        public void SetVertices(Vertex[] vertices)
        {
            Vertices = vertices;
            _dirty = true;
            RecalculateBounds();
        }

        public void SetVertices(IList<Vertex> vertices)
            => SetVertices(System.Linq.Enumerable.ToArray(vertices));

        public void SetIndices(uint[] indices)
        {
            Indices = indices;
            _dirty = true;
        }

        public void SetIndices(IList<uint> indices)
            => SetIndices(System.Linq.Enumerable.ToArray(indices));

        /// <summary>
        /// Recalculate per-vertex normals from triangle face normals.
        /// Call after SetVertices + SetIndices, before Upload.
        /// </summary>
        public void RecalculateNormals()
        {
            var normals = new Vector3[Vertices.Length];
            for (int i = 0; i < Indices.Length; i += 3)
            {
                uint ia = Indices[i], ib = Indices[i + 1], ic = Indices[i + 2];
                Vector3 edge1 = Vertices[ib].Position - Vertices[ia].Position;
                Vector3 edge2 = Vertices[ic].Position - Vertices[ia].Position;
                Vector3 n = Vector3.Normalize(Vector3.Cross(edge1, edge2));
                normals[ia] += n; normals[ib] += n; normals[ic] += n;
            }
            for (int i = 0; i < Vertices.Length; i++)
            {
                var v = Vertices[i];
                v.Normal = Vector3.Normalize(normals[i]);
                Vertices[i] = v;
            }
            _dirty = true;
        }

        /// <summary>
        /// Recalculate tangents (needed for normal mapping).
        /// Requires valid UVs and normals.
        /// </summary>
        public void RecalculateTangents()
        {
            var tangents = new Vector3[Vertices.Length];
            for (int i = 0; i < Indices.Length; i += 3)
            {
                uint ia = Indices[i], ib = Indices[i + 1], ic = Indices[i + 2];
                Vector3 e1 = Vertices[ib].Position - Vertices[ia].Position;
                Vector3 e2 = Vertices[ic].Position - Vertices[ia].Position;
                Vector2 du1 = Vertices[ib].UV - Vertices[ia].UV;
                Vector2 du2 = Vertices[ic].UV - Vertices[ia].UV;
                float f = 1f / (du1.X * du2.Y - du2.X * du1.Y + 1e-8f);
                Vector3 t = f * (du2.Y * e1 - du1.Y * e2);
                tangents[ia] += t; tangents[ib] += t; tangents[ic] += t;
            }
            for (int i = 0; i < Vertices.Length; i++)
            {
                var v = Vertices[i];
                v.Tangent = Vector3.Normalize(tangents[i]);
                Vertices[i] = v;
            }
            _dirty = true;
        }

        // ------------------------------------------------------------------
        // GPU upload
        // ------------------------------------------------------------------
        /// <summary>
        /// Upload (or re-upload) vertex/index data to the GPU.
        /// Safe to call multiple times — only re-sends when dirty.
        /// </summary>
        public void Upload()
        {
            if (!_dirty && _uploaded) return;

            if (_vao == -1)
            {
                _vao = GL.GenVertexArray();
                _vbo = GL.GenBuffer();
                _ebo = GL.GenBuffer();
            }

            GL.BindVertexArray(_vao);

            // VBO
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer,
                          Vertices.Length * Vertex.Stride,
                          Vertices,
                          BufferUsageHint.StaticDraw);

            // EBO
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer,
                          Indices.Length * sizeof(uint),
                          Indices,
                          BufferUsageHint.StaticDraw);

            int stride = Vertex.Stride;
            int off = 0;

            // location 0: Position (vec3)
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, off);
            off += 12;

            // location 1: Normal (vec3)
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, off);
            off += 12;

            // location 2: UV (vec2)
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, off);
            off += 8;

            // location 3: Tangent (vec3)
            GL.EnableVertexAttribArray(3);
            GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, off);
            off += 12;

            // location 4: Color (vec4)
            GL.EnableVertexAttribArray(4);
            GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, off);

            GL.BindVertexArray(0);

            _uploaded = true;
            _dirty = false;
        }

        // ------------------------------------------------------------------
        // Draw
        // ------------------------------------------------------------------
        public void Draw()
        {
            if (_dirty) Upload();
            if (!_uploaded || _vao == -1) return;

            GL.BindVertexArray(_vao);
            if (Indices.Length > 0)
                GL.DrawElements(DrawMode, Indices.Length, DrawElementsType.UnsignedInt, 0);
            else
                GL.DrawArrays(DrawMode, 0, Vertices.Length);
            GL.BindVertexArray(0);
        }

        // ------------------------------------------------------------------
        // Bounds
        // ------------------------------------------------------------------
        private void RecalculateBounds()
        {
            if (Vertices.Length == 0) { Bounds = default; return; }
            Vector3 min = Vertices[0].Position, max = min;
            foreach (var v in Vertices)
            {
                min = Vector3.ComponentMin(min, v.Position);
                max = Vector3.ComponentMax(max, v.Position);
            }
            Bounds = new Bounds((min + max) * 0.5f, max - min);
        }

        // ------------------------------------------------------------------
        // Primitive factories
        // ------------------------------------------------------------------
        public static Mesh CreateQuad(float width = 1f, float height = 1f)
        {
            float hw = width * 0.5f, hh = height * 0.5f;
            var m = new Mesh("Quad");
            m.SetVertices(new[]
            {
                new Vertex(new Vector3(-hw, -hh, 0), Vector3.UnitZ, new Vector2(0,0)),
                new Vertex(new Vector3( hw, -hh, 0), Vector3.UnitZ, new Vector2(1,0)),
                new Vertex(new Vector3( hw,  hh, 0), Vector3.UnitZ, new Vector2(1,1)),
                new Vertex(new Vector3(-hw,  hh, 0), Vector3.UnitZ, new Vector2(0,1)),
            });
            m.SetIndices(new uint[] { 0, 1, 2, 0, 2, 3 });
            m.Upload();
            return m;
        }

        public static Mesh CreateCube(float size = 1f)
        {
            float h = size * 0.5f;
            // 24 unique verts (4 per face) so normals are hard-edged
            var verts = new List<Vertex>();
            var idxs = new List<uint>();

            void AddFace(Vector3 n, Vector3 up)
            {
                Vector3 right = Vector3.Cross(n, up);
                Vector3 tl = (-right + up) * h, tr = (right + up) * h;
                Vector3 bl = (-right - up) * h, br = (right - up) * h;
                uint b = (uint)verts.Count;
                verts.Add(new Vertex(bl + n * h, n, new Vector2(0, 0)));
                verts.Add(new Vertex(br + n * h, n, new Vector2(1, 0)));
                verts.Add(new Vertex(tr + n * h, n, new Vector2(1, 1)));
                verts.Add(new Vertex(tl + n * h, n, new Vector2(0, 1)));
                idxs.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }

            AddFace(Vector3.UnitZ, Vector3.UnitY);
            AddFace(-Vector3.UnitZ, Vector3.UnitY);
            AddFace(Vector3.UnitX, Vector3.UnitY);
            AddFace(-Vector3.UnitX, Vector3.UnitY);
            AddFace(Vector3.UnitY, Vector3.UnitZ);
            AddFace(-Vector3.UnitY, -Vector3.UnitZ);

            var m = new Mesh("Cube");
            m.SetVertices(verts);
            m.SetIndices(idxs);
            m.RecalculateTangents();
            m.Upload();
            return m;
        }

        public static Mesh CreateSphere(int rings = 16, int slices = 16, float radius = 0.5f)
        {
            var verts = new List<Vertex>();
            var idxs = new List<uint>();

            for (int r = 0; r <= rings; r++)
            {
                float phi = MathF.PI * r / rings;
                for (int s = 0; s <= slices; s++)
                {
                    float theta = 2f * MathF.PI * s / slices;
                    Vector3 n = new Vector3(
                        MathF.Sin(phi) * MathF.Cos(theta),
                        MathF.Cos(phi),
                        MathF.Sin(phi) * MathF.Sin(theta));
                    verts.Add(new Vertex(n * radius, n,
                        new Vector2((float)s / slices, (float)r / rings)));
                }
            }

            for (int r = 0; r < rings; r++)
                for (int s = 0; s < slices; s++)
                {
                    uint a = (uint)(r * (slices + 1) + s);
                    uint b = a + 1;
                    uint c = (uint)((r + 1) * (slices + 1) + s);
                    uint d = c + 1;
                    idxs.AddRange(new[] { a, c, b, b, c, d });
                }

            var m = new Mesh("Sphere");
            m.SetVertices(verts);
            m.SetIndices(idxs);
            m.RecalculateTangents();
            m.Upload();
            return m;
        }

        public static Mesh CreatePlane(int subdivisions = 1, float size = 1f)
        {
            int n = subdivisions + 1;
            float step = size / subdivisions;
            float half = size * 0.5f;
            var verts = new List<Vertex>();
            var idxs = new List<uint>();

            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    float px = -half + x * step;
                    float pz = -half + z * step;
                    verts.Add(new Vertex(
                        new Vector3(px, 0, pz), Vector3.UnitY,
                        new Vector2((float)x / subdivisions, (float)z / subdivisions)));
                }

            for (int z = 0; z < subdivisions; z++)
                for (int x = 0; x < subdivisions; x++)
                {
                    uint a = (uint)(z * n + x);
                    uint b = a + 1;
                    uint c = (uint)((z + 1) * n + x);
                    uint d = c + 1;
                    idxs.AddRange(new[] { a, c, b, b, c, d });
                }

            var m = new Mesh("Plane");
            m.SetVertices(verts);
            m.SetIndices(idxs);
            m.RecalculateTangents();
            m.Upload();
            return m;
        }

        // ------------------------------------------------------------------
        // IDisposable
        // ------------------------------------------------------------------
        public void Dispose()
        {
            if (_vao != -1) { GL.DeleteVertexArray(_vao); _vao = -1; }
            if (_vbo != -1) { GL.DeleteBuffer(_vbo); _vbo = -1; }
            if (_ebo != -1) { GL.DeleteBuffer(_ebo); _ebo = -1; }
            _uploaded = false;
        }
    }

    /// <summary>Axis-aligned bounding box.</summary>
    public struct Bounds
    {
        public Vector3 Center;
        public Vector3 Size;
        public Vector3 Min => Center - Size * 0.5f;
        public Vector3 Max => Center + Size * 0.5f;

        public Bounds(Vector3 center, Vector3 size) { Center = center; Size = size; }

        public bool Contains(Vector3 point)
            => point.X >= Min.X && point.X <= Max.X
            && point.Y >= Min.Y && point.Y <= Max.Y
            && point.Z >= Min.Z && point.Z <= Max.Z;

        public bool Intersects(Bounds other)
            => !(other.Min.X > Max.X || other.Max.X < Min.X
              || other.Min.Y > Max.Y || other.Max.Y < Min.Y
              || other.Min.Z > Max.Z || other.Max.Z < Min.Z);
    }
}