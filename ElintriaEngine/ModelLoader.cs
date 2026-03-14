using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ElintriaEngine.Rendering.Scene
{
    public static class ModelLoader
    {
        public static (float[] Vertices, uint[] Indices, string Error) Load(string filePath)
        {
            if (!File.Exists(filePath))
                return (Empty, EmptyI, $"File not found: {filePath}");

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            try
            {
                return ext switch
                {
                    ".obj" => ParseObj(filePath),
                    ".fbx" => ParseFbx(filePath),
                    _ => (Empty, EmptyI, $"Unsupported format: {ext}"),
                };
            }
            catch (Exception ex)
            {
                return (Empty, EmptyI, $"Exception: {ex.Message}");
            }
        }

        private static readonly float[] Empty = Array.Empty<float>();
        private static readonly uint[] EmptyI = Array.Empty<uint>();

        // ═════════════════════════════════════════════════════════════════════
        //  OBJ
        // ═════════════════════════════════════════════════════════════════════
        private static (float[] V, uint[] I, string Err) ParseObj(string path)
        {
            var pos = new List<float3>();
            var norm = new List<float3>();
            var uv = new List<float2>();
            var tris = new List<fv3>();

            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var t = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
                switch (t[0])
                {
                    case "v" when t.Length >= 4: pos.Add(new float3(F(t[1]), F(t[2]), F(t[3]))); break;
                    case "vn" when t.Length >= 4: norm.Add(new float3(F(t[1]), F(t[2]), F(t[3]))); break;
                    case "vt" when t.Length >= 3: uv.Add(new float2(F(t[1]), F(t[2]))); break;
                    case "f" when t.Length >= 4:
                        var c = new List<(int p, int n, int u)>();
                        for (int i = 1; i < t.Length; i++) c.Add(ObjFV(t[i]));
                        for (int i = 1; i < c.Count - 1; i++)
                            tris.Add(new fv3(c[0], c[i], c[i + 1]));
                        break;
                }
            }
            return Assemble(tris, pos, norm, uv);
        }

        private static (int p, int n, int u) ObjFV(string s)
        {
            var p = s.Split('/');
            int v = p.Length > 0 && p[0].Length > 0 ? int.Parse(p[0]) : 0;
            int t = p.Length > 1 && p[1].Length > 0 ? int.Parse(p[1]) : 0;
            int n = p.Length > 2 && p[2].Length > 0 ? int.Parse(p[2]) : 0;
            return (v, n, t);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  FBX dispatcher
        // ═════════════════════════════════════════════════════════════════════
        private static readonly byte[] BinMagic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  ");

        private static (float[] V, uint[] I, string Err) ParseFbx(string path)
        {
            using var fs = File.OpenRead(path);
            var hdr = new byte[20];
            if (fs.Read(hdr, 0, 20) == 20)
            {
                bool isBinary = true;
                for (int i = 0; i < BinMagic.Length; i++)
                    if (hdr[i] != BinMagic[i]) { isBinary = false; break; }
                if (isBinary) return ParseBinaryFbx(path);
            }
            return ParseAsciiFbx(path);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Binary FBX
        // ═════════════════════════════════════════════════════════════════════
        private static (float[] V, uint[] I, string Err) ParseBinaryFbx(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);

            // FBX binary layout:
            //   0–20 : "Kaydara FBX Binary  \0"  (21 bytes)
            //   21   : 0x1A
            //   22   : 0x00
            //   23–26: version uint32 (LE)
            //   27+  : node records
            r.BaseStream.Seek(23, SeekOrigin.Begin);
            uint version = r.ReadUInt32();    // now at position 27
            bool is64 = version >= 7500;

            Console.WriteLine($"[FBX] Binary version {version}, 64-bit={is64}");

            var root = new FNode("root");
            ReadChildren(r, root, r.BaseStream.Length - (is64 ? 25L : 13L), is64);

            // Find all Geometry nodes anywhere in the tree
            var geoms = new List<FNode>();
            FindNodes(root, "Geometry", geoms);

            if (geoms.Count == 0)
                return (Empty, EmptyI, "No Geometry node found in FBX.");

            // Merge all geometries into one mesh
            var allPos = new List<float3>();
            var allNorm = new List<float3>();
            var allUV = new List<float2>();
            var allTris = new List<fv3>();

            foreach (var g in geoms)
                MergeGeom(g, allPos, allNorm, allUV, allTris);

            if (allTris.Count == 0)
                return (Empty, EmptyI, "Geometry nodes contained no triangles.");

            return Assemble(allTris, allPos, allNorm, allUV);
        }

        private static void MergeGeom(FNode geom,
            List<float3> allPos, List<float3> allNorm,
            List<float2> allUV, List<fv3> allTris)
        {
            int posBase = allPos.Count;
            int normBase = allNorm.Count;
            int uvBase = allUV.Count;

            double[]? vPos = null;
            int[]? polyIdx = null;
            double[]? normals = null;
            double[]? uvData = null;
            int[]? uvIndex = null;
            string normMap = "ByPolygonVertex", normRef = "Direct";
            string uvMap = "ByPolygonVertex", uvRef = "IndexToDirect";

            foreach (var c in geom.Children)
            {
                switch (c.Name)
                {
                    case "Vertices":
                        vPos = GetDoubles(c);
                        break;
                    case "PolygonVertexIndex":
                        polyIdx = GetInts(c);
                        break;
                    case "LayerElementNormal":
                        foreach (var n in c.Children)
                        {
                            if (n.Name == "Normals") normals = GetDoubles(n);
                            if (n.Name == "MappingInformationType") normMap = GetStr(n) ?? normMap;
                            if (n.Name == "ReferenceInformationType") normRef = GetStr(n) ?? normRef;
                        }
                        break;
                    case "LayerElementUV":
                        foreach (var u in c.Children)
                        {
                            if (u.Name == "UV") uvData = GetDoubles(u);
                            if (u.Name == "UVIndex") uvIndex = GetInts(u);
                            if (u.Name == "MappingInformationType") uvMap = GetStr(u) ?? uvMap;
                            if (u.Name == "ReferenceInformationType") uvRef = GetStr(u) ?? uvRef;
                        }
                        break;
                }
            }

            if (vPos == null || polyIdx == null) return;

            // Populate local lists (0-based)
            var lPos = new List<float3>();
            var lNorm = new List<float3>();
            var lUV = new List<float2>();

            for (int i = 0; i + 2 < vPos.Length; i += 3)
                lPos.Add(new float3((float)vPos[i], (float)vPos[i + 1], (float)vPos[i + 2]));

            if (normals != null)
                for (int i = 0; i + 2 < normals.Length; i += 3)
                    lNorm.Add(new float3((float)normals[i], (float)normals[i + 1], (float)normals[i + 2]));

            if (uvData != null)
                for (int i = 0; i + 1 < uvData.Length; i += 2)
                    lUV.Add(new float2((float)uvData[i], (float)uvData[i + 1]));

            // Triangulate — track per-face-vertex cursor for normals/UVs
            int fvCursor = 0;   // running ByPolygonVertex index
            var polyBuf = new List<int>();

            for (int i = 0; i < polyIdx.Length; i++)
            {
                int raw = polyIdx[i];
                bool end = raw < 0;
                int vi = end ? ~raw : raw;   // 0-based vertex index
                polyBuf.Add(vi);

                if (end)
                {
                    int polyStart = fvCursor;   // first ByPolygonVertex index for this polygon
                    int polyCount = polyBuf.Count;

                    // Fan-triangulate
                    for (int j = 1; j < polyCount - 1; j++)
                    {
                        int ni0 = LayerIdx(0, polyStart, normMap, normRef, null, lNorm.Count);
                        int ni1 = LayerIdx(j, polyStart, normMap, normRef, null, lNorm.Count);
                        int ni2 = LayerIdx(j + 1, polyStart, normMap, normRef, null, lNorm.Count);

                        int ui0 = LayerIdx(0, polyStart, uvMap, uvRef, uvIndex, lUV.Count);
                        int ui1 = LayerIdx(j, polyStart, uvMap, uvRef, uvIndex, lUV.Count);
                        int ui2 = LayerIdx(j + 1, polyStart, uvMap, uvRef, uvIndex, lUV.Count);

                        // +1 converts to 1-based for Assemble; also offset by base counts
                        allTris.Add(new fv3(
                            (posBase + polyBuf[0] + 1, normBase + ni0 + 1, uvBase + ui0 + 1),
                            (posBase + polyBuf[j] + 1, normBase + ni1 + 1, uvBase + ui1 + 1),
                            (posBase + polyBuf[j + 1] + 1, normBase + ni2 + 1, uvBase + ui2 + 1)));
                    }

                    fvCursor += polyCount;
                    polyBuf.Clear();
                }
            }

            // Append local lists to shared lists
            allPos.AddRange(lPos);
            allNorm.AddRange(lNorm);
            allUV.AddRange(lUV);
        }

        // Layer mapping: returns a 0-based index into the normal/UV array
        private static int LayerIdx(int polyLocal, int polyStart,
                                     string mapping, string reference,
                                     int[]? indexArr, int arrayLen)
        {
            if (mapping == "AllSame") return 0;

            int fvi = polyStart + polyLocal;   // absolute per-face-vertex index
            if (reference == "Direct")
                return Math.Clamp(fvi, 0, Math.Max(0, arrayLen - 1));
            if (reference == "IndexToDirect" && indexArr != null
                && fvi >= 0 && fvi < indexArr.Length)
                return Math.Clamp(indexArr[fvi], 0, Math.Max(0, arrayLen - 1));
            return 0;
        }

        // ── FBX node reading ─────────────────────────────────────────────────
        private static void ReadChildren(BinaryReader r, FNode parent, long end, bool is64)
        {
            while (r.BaseStream.Position < end)
            {
                var n = ReadNode(r, is64);
                if (n == null) break;
                parent.Children.Add(n);
            }
        }

        private static FNode? ReadNode(BinaryReader r, bool is64)
        {
            long endOff = is64 ? (long)r.ReadUInt64() : r.ReadUInt32();
            long numProp = is64 ? (long)r.ReadUInt64() : r.ReadUInt32();
            long propLen = is64 ? (long)r.ReadUInt64() : r.ReadUInt32();
            int nameLen = r.ReadByte();
            string name = nameLen > 0 ? Encoding.UTF8.GetString(r.ReadBytes(nameLen)) : "";

            if (endOff == 0) return null;

            var node = new FNode(name);
            long pStart = r.BaseStream.Position;

            for (long p = 0; p < numProp; p++)
            {
                if (r.BaseStream.Position >= pStart + propLen) break;
                node.Props.Add(ReadProp(r));
            }
            r.BaseStream.Position = pStart + propLen;

            long sentinel = is64 ? 25 : 13;
            if (endOff - sentinel > r.BaseStream.Position)
                ReadChildren(r, node, endOff - sentinel, is64);

            r.BaseStream.Position = endOff;
            return node;
        }

        private static object? ReadProp(BinaryReader r)
        {
            char t = (char)r.ReadByte();
            return t switch
            {
                'Y' => (object?)r.ReadInt16(),
                'C' => r.ReadByte() != 0,
                'I' => r.ReadInt32(),
                'F' => r.ReadSingle(),
                'D' => r.ReadDouble(),
                'L' => r.ReadInt64(),
                'S' => Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())),
                'R' => r.ReadBytes(r.ReadInt32()),
                'f' => ReadArr(r, 4, b => BitConverter.ToSingle(b, 0)),
                'd' => ReadArr(r, 8, b => BitConverter.ToDouble(b, 0)),
                'i' => ReadArr(r, 4, b => BitConverter.ToInt32(b, 0)),
                'l' => ReadArr(r, 8, b => BitConverter.ToInt64(b, 0)),
                'b' => ReadArr(r, 1, b => b[0] != 0),
                _ => null,
            };
        }

        private static T[] ReadArr<T>(BinaryReader r, int stride, Func<byte[], T> conv)
        {
            uint count = r.ReadUInt32(), enc = r.ReadUInt32(), clen = r.ReadUInt32();
            byte[] raw = Decompress(r, enc, clen, count * (uint)stride);
            var res = new T[count];
            var tmp = new byte[stride];
            for (int i = 0; i < count; i++)
            {
                Array.Copy(raw, i * stride, tmp, 0, stride);
                res[i] = conv(tmp);
            }
            return res;
        }

        private static byte[] Decompress(BinaryReader r, uint enc, uint clen, uint uclen)
        {
            byte[] data = r.ReadBytes((int)clen);
            if (enc == 0) return data;
            // zlib: skip 2-byte header
            using var ms = new MemoryStream(data, 2, data.Length - 2);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            var buf = new byte[uclen];
            int got = 0;
            while (got < buf.Length)
            {
                int n = ds.Read(buf, got, buf.Length - got);
                if (n == 0) break;
                got += n;
            }
            return buf;
        }

        // ── Helpers to extract typed data from an FNode's first property ──────
        private static double[]? GetDoubles(FNode n)
        {
            if (n.Props.Count == 0) return null;
            var p = n.Props[0];
            if (p is double[] dd) return dd;
            if (p is float[] ff) { var d = new double[ff.Length]; for (int i = 0; i < ff.Length; i++) d[i] = ff[i]; return d; }
            return null;
        }

        private static int[]? GetInts(FNode n)
        {
            if (n.Props.Count == 0) return null;
            var p = n.Props[0];
            if (p is int[] ii) return ii;
            if (p is long[] ll) { var i2 = new int[ll.Length]; for (int i = 0; i < ll.Length; i++) i2[i] = (int)ll[i]; return i2; }
            return null;
        }

        private static string? GetStr(FNode n)
            => n.Props.Count > 0 && n.Props[0] is string s ? s.Trim() : null;

        private static void FindNodes(FNode root, string name, List<FNode> result)
        {
            foreach (var c in root.Children)
            {
                if (c.Name == name) result.Add(c);
                FindNodes(c, name, result);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ASCII FBX
        // ═════════════════════════════════════════════════════════════════════
        private static (float[] V, uint[] I, string Err) ParseAsciiFbx(string path)
        {
            var lines = File.ReadAllLines(path);
            var vArr = ExtractDblArr(lines, "Vertices");
            var piArr = ExtractIntArr(lines, "PolygonVertexIndex");
            var nArr = ExtractDblArr(lines, "Normals");

            if (vArr == null || vArr.Length < 3 || piArr == null)
                return (Empty, EmptyI, "Could not read ASCII FBX geometry.");

            var pos = new List<float3>();
            var norm = new List<float3>();
            var uv = new List<float2>();
            var tris = new List<fv3>();

            for (int i = 0; i + 2 < vArr.Length; i += 3)
                pos.Add(new float3((float)vArr[i], (float)vArr[i + 1], (float)vArr[i + 2]));
            if (nArr != null)
                for (int i = 0; i + 2 < nArr.Length; i += 3)
                    norm.Add(new float3((float)nArr[i], (float)nArr[i + 1], (float)nArr[i + 2]));

            var poly = new List<int>();
            int fvc = 0;
            for (int i = 0; i < piArr.Length; i++)
            {
                bool end = piArr[i] < 0;
                poly.Add(end ? ~piArr[i] : piArr[i]);
                if (end)
                {
                    for (int j = 1; j < poly.Count - 1; j++)
                        tris.Add(new fv3(
                            (poly[0] + 1, fvc + 1, 1),
                            (poly[j] + 1, fvc + j + 1, 1),
                            (poly[j + 1] + 1, fvc + j + 2, 1)));
                    fvc += poly.Count;
                    poly.Clear();
                }
            }
            return Assemble(tris, pos, norm, uv);
        }

        private static double[]? ExtractDblArr(string[] lines, string key)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (!t.StartsWith(key + ":")) continue;
                var sb = new StringBuilder();
                int c = t.IndexOf(':');
                if (c >= 0) sb.Append(t[(c + 1)..]);
                for (int j = i + 1; j < lines.Length && j < i + 30; j++)
                {
                    var nl = lines[j].Trim();
                    if (nl.StartsWith("a:")) { sb.Append(nl[2..]); break; }
                    if (nl.StartsWith("{") || nl.StartsWith("}")) break;
                    sb.Append(','); sb.Append(nl);
                }
                var parts = sb.ToString().Split(new[] { ',', '\t', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var res = new List<double>();
                foreach (var p in parts)
                    if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        res.Add(v);
                return res.ToArray();
            }
            return null;
        }

        private static int[]? ExtractIntArr(string[] lines, string key)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (!t.StartsWith(key + ":")) continue;
                var sb = new StringBuilder();
                int c = t.IndexOf(':');
                if (c >= 0) sb.Append(t[(c + 1)..]);
                for (int j = i + 1; j < lines.Length && j < i + 30; j++)
                {
                    var nl = lines[j].Trim();
                    if (nl.StartsWith("a:")) { sb.Append(nl[2..]); break; }
                    if (nl.StartsWith("{") || nl.StartsWith("}")) break;
                    sb.Append(','); sb.Append(nl);
                }
                var parts = sb.ToString().Split(new[] { ',', '\t', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var res = new List<int>();
                foreach (var p in parts)
                    if (int.TryParse(p, out int v)) res.Add(v);
                return res.ToArray();
            }
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Assemble: dedup vertices, build index buffer
        // ═════════════════════════════════════════════════════════════════════
        private static (float[] V, uint[] I, string Err) Assemble(
            List<fv3> tris,
            List<float3> pos, List<float3> norm, List<float2> uv)
        {
            if (tris.Count == 0) return (Empty, EmptyI, "No triangles.");

            bool hasN = norm.Count > 0;
            bool hasU = uv.Count > 0;

            var verts = new List<float>(tris.Count * 24);
            var indices = new List<uint>(tris.Count * 3);
            var dedup = new Dictionary<(int, int, int), uint>();

            for (int ti = 0; ti < tris.Count; ti++)
            {
                var tri = tris[ti];

                // Flat normal fallback for this triangle
                float3 flat = new float3(0, 1, 0);
                if (!hasN)
                {
                    int p0 = tri.A.p - 1, p1 = tri.B.p - 1, p2 = tri.C.p - 1;
                    if (p0 >= 0 && p0 < pos.Count && p1 >= 0 && p1 < pos.Count && p2 >= 0 && p2 < pos.Count)
                    {
                        var v0 = pos[p0]; var v1 = pos[p1]; var v2 = pos[p2];
                        float ax = v1.x - v0.x, ay = v1.y - v0.y, az = v1.z - v0.z;
                        float bx = v2.x - v0.x, by = v2.y - v0.y, bz = v2.z - v0.z;
                        float cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
                        float len = MathF.Sqrt(cx * cx + cy * cy + cz * cz);
                        if (len > 1e-6f) flat = new float3(cx / len, cy / len, cz / len);
                    }
                }

                foreach (var fv in new[] { tri.A, tri.B, tri.C })
                {
                    var key = hasN ? (fv.p, fv.n, fv.u) : (fv.p, -1, fv.u);
                    if (dedup.TryGetValue(key, out uint ex)) { indices.Add(ex); continue; }

                    uint idx = (uint)(verts.Count / 8);
                    dedup[key] = idx;
                    indices.Add(idx);

                    int pi = fv.p - 1;
                    if (pi >= 0 && pi < pos.Count) { verts.Add(pos[pi].x); verts.Add(pos[pi].y); verts.Add(pos[pi].z); }
                    else { verts.Add(0); verts.Add(0); verts.Add(0); }

                    int ni = fv.n - 1;
                    if (hasN && ni >= 0 && ni < norm.Count) { verts.Add(norm[ni].x); verts.Add(norm[ni].y); verts.Add(norm[ni].z); }
                    else { verts.Add(flat.x); verts.Add(flat.y); verts.Add(flat.z); }

                    int ui = fv.u - 1;
                    if (hasU && ui >= 0 && ui < uv.Count) { verts.Add(uv[ui].x); verts.Add(1f - uv[ui].y); }
                    else { verts.Add(0); verts.Add(0); }
                }
            }

            Console.WriteLine($"[ModelLoader] {tris.Count} tris, {verts.Count / 8} verts, {indices.Count} indices");
            return (verts.ToArray(), indices.ToArray(), "");
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        // ── Tiny value types ──────────────────────────────────────────────────
        private   struct float3 { public float x, y, z; public float3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
        private   struct float2 { public float x, y; public float2(float x, float y) { this.x = x; this.y = y; } }
        private   struct fv3
        {
            public (int p, int n, int u) A, B, C;
            public fv3((int p, int n, int u) a, (int p, int n, int u) b, (int p, int n, int u) c) { A = a; B = b; C = c; }
        }

        // ── FBX node ──────────────────────────────────────────────────────────
        private class FNode
        {
            public string Name { get; }
            public List<object?> Props { get; } = new();
            public List<FNode> Children { get; } = new();
            public FNode(string n) => Name = n;
        }
    }
}