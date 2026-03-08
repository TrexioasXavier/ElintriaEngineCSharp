using Elintria.Engine.Rendering;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Elintria.Engine.Assets
{
    public static class ObjLoader
    {
        public static Rendering.Mesh Load(string path)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();

            var vertexData = new List<Vertex>();
            var indices = new List<uint>();

            var uniqueVertices = new Dictionary<string, uint>();

            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                switch (parts[0])
                {
                    case "v":
                        positions.Add(ParseVec3(parts));
                        break;

                    case "vn":
                        normals.Add(ParseVec3(parts));
                        break;

                    case "vt":
                        uvs.Add(ParseVec2(parts));
                        break;

                    case "f":
                        for (int i = 1; i <= 3; i++)
                        {
                            if (!uniqueVertices.TryGetValue(parts[i], out uint index))
                            {
                                var comps = parts[i].Split('/');

                                var pos = positions[int.Parse(comps[0]) - 1];
                                var uv = uvs.Count > 0 ? uvs[int.Parse(comps[1]) - 1] : Vector2.Zero;
                                var norm = normals.Count > 0 ? normals[int.Parse(comps[2]) - 1] : Vector3.UnitY;
                                 
                                vertexData.Add(new Vertex(pos, norm, uv));
                                 
                                //vertexData.Add(1.0f - uv.Y); // flip V

                                index = (uint)(vertexData.Count / 8 - 1);
                                uniqueVertices.Add(parts[i], index);
                            }

                            indices.Add(index);
                        }
                        break;
                }
            } 

            Rendering.Mesh _mesh = new Rendering.Mesh();
            _mesh.SetVertices(vertexData.ToArray());
            _mesh.SetIndices(indices.ToArray());



            return _mesh;


        }

        private static Vector3 ParseVec3(string[] parts)
        {
            return new Vector3(
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture),
                float.Parse(parts[3], CultureInfo.InvariantCulture)
            );
        }

        private static Vector2 ParseVec2(string[] parts)
        {
            return new Vector2(
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture)
            );
        }
    }
}
