using HKX2;
using SoulsFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace navmesh_exporter
{
    class Program
    {
        record Tri(Vector3 A, Vector3 B, Vector3 C, int FaceIndex);
        record MeshStat(string File, int Faces, int Edges, int Vertices, int Triangles, List<string> Errors, string OutObjFile, long OutObjSizeBytes);
        record NavmeshTransform(Vector3 Position, Vector3 RotationRad, Vector3 Scale);

        static int Main(string[] args)
        {
            try
            {
                var nvmhktbnd = GetArg(args, "--nvmhktbnd")
                    ?? @"E:\DARK SOULS III\Game\map\m40_00_00_00\m40_00_00_00.nvmhktbnd.dcx";
                var outObjDir = GetArg(args, "--out-obj-dir")
                    ?? @"D:\github\darksoulsiii-practice-tool\map-work\walk-surface-m40\navmesh_objs";
                var outJson = GetArg(args, "--out-json")
                    ?? @"D:\github\darksoulsiii-practice-tool\map-work\walk-surface-m40\navmesh_summary.json";
                var repoRoot = GetArg(args, "--repo-root")
                    ?? @"D:\github\darksoulsiii-practice-tool";

                Directory.CreateDirectory(outObjDir);
                Directory.CreateDirectory(Path.GetDirectoryName(outJson)!);

                var bnd = BND4.Read(nvmhktbnd);
                var nvaPath = ResolveNvaPath(nvmhktbnd);
                var transformsByModelId = LoadNavmeshTransforms(nvaPath);
                var fallbackTransform = GetSingleFallbackTransform(transformsByModelId);
                var stats = new List<MeshStat>();
                foreach (var file in bnd.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var errors = new List<string>();
                    int faceCount = 0;
                    int edgeCount = 0;
                    int vertCount = 0;
                    var tris = new List<Tri>();
                    try
                    {
                        var br = new BinaryReaderEx(false, file.Bytes);
                        var root = new PackFileDeserializer().Deserialize(br) as hkRootLevelContainer;
                        if (root == null || root.m_namedVariants == null || root.m_namedVariants.Count == 0)
                        {
                            errors.Add("invalid hkRootLevelContainer or empty namedVariants");
                        }
                        else
                        {
                            var nav = root.m_namedVariants
                                .Select(v => v.m_variant)
                                .OfType<hkaiNavMesh>()
                                .FirstOrDefault();
                            if (nav == null)
                            {
                                errors.Add("no hkaiNavMesh variant");
                            }
                            else
                            {
                                faceCount = nav.m_faces?.Count ?? 0;
                                edgeCount = nav.m_edges?.Count ?? 0;
                                vertCount = nav.m_vertices?.Count ?? 0;
                                tris = BuildTriangles(nav, errors);
                                var modelId = ParseModelId(file.Name);
                                if (modelId.HasValue && transformsByModelId.TryGetValue(modelId.Value, out var tf))
                                {
                                    tris = ApplyTransform(tris, tf);
                                }
                                else if (fallbackTransform != null)
                                {
                                    tris = ApplyTransform(tris, fallbackTransform);
                                }
                                else
                                {
                                    errors.Add("no NVA transform match; exported in local space");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{ex.GetType().Name}: {ex.Message}");
                    }

                    var stem = Path.GetFileNameWithoutExtension(file.Name);
                    var outObjPath = Path.Combine(outObjDir, $"{stem}.obj");
                    WriteObj(outObjPath, tris);
                    var outObjSizeBytes = new FileInfo(outObjPath).Length;

                    stats.Add(new MeshStat(
                        Path.GetFileName(file.Name),
                        faceCount,
                        edgeCount,
                        vertCount,
                        tris.Count,
                        errors,
                        ToRepoRelativeWebPath(outObjPath, repoRoot),
                        outObjSizeBytes));
                    Console.WriteLine($"{Path.GetFileName(file.Name)} faces={faceCount} tris={tris.Count} errs={errors.Count}");
                }

                var payload = new
                {
                    repo_root_abs = Path.GetFullPath(repoRoot),
                    source = ToRepoRelativeWebPath(nvmhktbnd, repoRoot),
                    files = stats.Count,
                    fail_files = stats.Count(s => s.Errors.Count > 0),
                    navmeshes = stats.Select(s => new
                    {
                        name = s.File,
                        path = s.OutObjFile,
                        obj_size_bytes = s.OutObjSizeBytes
                    }).ToList(),
                    meshes = stats
                };
                File.WriteAllText(outJson, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"done files={stats.Count} fail={stats.Count(s => s.Errors.Count > 0)}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        static List<Tri> BuildTriangles(hkaiNavMesh nav, List<string> errors)
        {
            var outTris = new List<Tri>();
            if (nav.m_faces == null || nav.m_edges == null || nav.m_vertices == null)
                return outTris;

            for (int fi = 0; fi < nav.m_faces.Count; fi++)
            {
                var f = nav.m_faces[fi];
                if (f.m_numEdges < 3) continue;
                if (f.m_startEdgeIndex < 0 || f.m_startEdgeIndex + f.m_numEdges > nav.m_edges.Count)
                {
                    errors.Add($"face[{fi}] edge range out of bounds");
                    continue;
                }

                var poly = new List<int>(f.m_numEdges);
                for (int ei = 0; ei < f.m_numEdges; ei++)
                {
                    var edge = nav.m_edges[f.m_startEdgeIndex + ei];
                    poly.Add(edge.m_a);
                }

                var dedup = new List<int>(poly.Count);
                int last = int.MinValue;
                foreach (var v in poly)
                {
                    if (v != last) dedup.Add(v);
                    last = v;
                }
                if (dedup.Count >= 2 && dedup[0] == dedup[dedup.Count - 1])
                    dedup.RemoveAt(dedup.Count - 1);

                if (dedup.Count < 3) continue;
                for (int i = 0; i < dedup.Count; i++)
                {
                    if (dedup[i] < 0 || dedup[i] >= nav.m_vertices.Count)
                    {
                        errors.Add($"face[{fi}] vertex index out of bounds");
                        dedup.Clear();
                        break;
                    }
                }
                if (dedup.Count < 3) continue;

                var a = ToV3(nav.m_vertices[dedup[0]]);
                for (int i = 1; i + 1 < dedup.Count; i++)
                {
                    var b = ToV3(nav.m_vertices[dedup[i]]);
                    var c = ToV3(nav.m_vertices[dedup[i + 1]]);
                    outTris.Add(new Tri(a, b, c, fi));
                }
            }
            return outTris;
        }

        static Vector3 ToV3(Vector4 v) => new Vector3(v.X, v.Y, v.Z);

        static Dictionary<int, NavmeshTransform> LoadNavmeshTransforms(string nvaPath)
        {
            var map = new Dictionary<int, NavmeshTransform>();
            if (!File.Exists(nvaPath))
            {
                Console.WriteLine($"WARN nva not found: {nvaPath}");
                return map;
            }

            var nva = NVA.Read(nvaPath);
            foreach (var nav in nva.Navmeshes)
            {
                map[nav.ModelID] = new NavmeshTransform(nav.Position, nav.Rotation, nav.Scale);
            }
            Console.WriteLine($"NVA navmesh transforms: {map.Count}");
            return map;
        }

        static string ResolveNvaPath(string nvmhktbndPath)
        {
            var dir = Path.GetDirectoryName(nvmhktbndPath) ?? "";
            var name = Path.GetFileName(nvmhktbndPath);
            var marker = ".nvmhktbnd.dcx";
            if (name.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var mapId = name.Substring(0, name.Length - marker.Length);
                return Path.Combine(dir, mapId + ".nva.dcx");
            }
            return Path.ChangeExtension(nvmhktbndPath, ".nva.dcx");
        }

        static int? ParseModelId(string hkxName)
        {
            var stem = Path.GetFileNameWithoutExtension(hkxName);
            var us = stem.LastIndexOf('_');
            if (us < 0 || us + 1 >= stem.Length) return null;
            if (int.TryParse(stem.Substring(us + 1), out int id)) return id;
            return null;
        }

        static Quaternion EulerRadToQuatYZX(Vector3 rad)
        {
            var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, rad.Y);
            var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rad.Z);
            var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rad.X);
            return qy * qz * qx;
        }

        static Vector3 TransformPoint(Vector3 p, NavmeshTransform tf, Quaternion q)
        {
            var s = new Vector3(p.X * tf.Scale.X, p.Y * tf.Scale.Y, p.Z * tf.Scale.Z);
            return Vector3.Transform(s, q) + tf.Position;
        }

        static List<Tri> ApplyTransform(List<Tri> src, NavmeshTransform tf)
        {
            var q = EulerRadToQuatYZX(tf.RotationRad);
            var outTris = new List<Tri>(src.Count);
            foreach (var t in src)
            {
                outTris.Add(new Tri(
                    TransformPoint(t.A, tf, q),
                    TransformPoint(t.B, tf, q),
                    TransformPoint(t.C, tf, q),
                    t.FaceIndex));
            }
            return outTris;
        }

        static NavmeshTransform GetSingleFallbackTransform(Dictionary<int, NavmeshTransform> map)
        {
            if (map.Count == 0) return null;
            var groups = map.Values
                .GroupBy(v => $"{v.Position.X:F6},{v.Position.Y:F6},{v.Position.Z:F6}|{v.RotationRad.X:F6},{v.RotationRad.Y:F6},{v.RotationRad.Z:F6}|{v.Scale.X:F6},{v.Scale.Y:F6},{v.Scale.Z:F6}")
                .ToList();
            if (groups.Count == 1) return groups[0].First();
            return null;
        }

        static void WriteObj(string path, List<Tri> tris)
        {
            using var sw = new StreamWriter(path, false, Encoding.UTF8);
            int nextIdx = 1;
            int lastFace = -1;
            var vertexToIndex = new Dictionary<(float X, float Y, float Z), int>();

            int GetOrCreateVertexIndex(Vector3 v)
            {
                var key = (v.X, v.Y, v.Z);
                if (vertexToIndex.TryGetValue(key, out var found))
                    return found;
                int created = nextIdx++;
                vertexToIndex[key] = created;
                sw.WriteLine($"v {v.X} {v.Y} {v.Z}");
                return created;
            }

            foreach (var t in tris)
            {
                if (t.FaceIndex != lastFace)
                {
                    sw.WriteLine($"o face_{t.FaceIndex:D5}");
                    sw.WriteLine($"g face_{t.FaceIndex:D5}");
                    lastFace = t.FaceIndex;
                }
                int ia = GetOrCreateVertexIndex(t.A);
                int ib = GetOrCreateVertexIndex(t.B);
                int ic = GetOrCreateVertexIndex(t.C);
                sw.WriteLine($"f {ia} {ib} {ic}");
            }
        }

        static string? GetArg(string[] args, string key)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == key) return args[i + 1];
            }
            return null;
        }

        static string ToRepoRelativeWebPath(string filePath, string repoRoot)
        {
            var absFile = Path.GetFullPath(filePath);
            var absRoot = Path.GetFullPath(repoRoot);
            var rel = Path.GetRelativePath(absRoot, absFile).Replace('\\', '/');
            if (rel.StartsWith("../", StringComparison.Ordinal) || rel == "..")
            {
                return absFile.Replace('\\', '/');
            }
            return "/" + rel;
        }
    }
}
