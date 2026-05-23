using HKX2;
using SoulsFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace hkx_exporter
{
    class Program
    {
        record Tri(Vector3 A, Vector3 B, Vector3 C, int BodyIndex, uint Filter);
        record CollisionInstance(string Name, string ModelName, Vector3 Position, Vector3 RotationDeg, byte HitFilterID);
        record InstanceStat(
            string SourceHkxFile,
            string InstanceName,
            string ModelName,
            byte MsbHitFilterId,
            string MsbHitFilterType,
            string OutObjFile,
            long OutObjSizeBytes);
        record FileStat(string File, int Bodies, List<string> Errors);

        static int Main(string[] args)
        {
            try
            {
                var hkxDir = GetArg(args, "--hkx-dir") ?? @"D:\github\darksoulsiii-practice-tool\ds3map\m40_00_00_00\collision_export_h40\binder_unpack\m40_00_00_00";
                var msbPath = GetArg(args, "--msb") ?? @"D:\github\darksoulsiii-practice-tool\ds3map\mapstudio\m40_00_00_00.msb.dcx";
                var outObjDir = GetArg(args, "--out-obj-dir") ?? @"D:\github\darksoulsiii-practice-tool\map-work\walk-surface-m40\collision_h40_world_objs";
                var outJson = GetArg(args, "--out-json") ?? @"D:\github\darksoulsiii-practice-tool\map-work\walk-surface-m40\collision_h40_world.json";
                var repoRoot = GetArg(args, "--repo-root") ?? @"D:\github\darksoulsiii-practice-tool";

                Directory.CreateDirectory(outObjDir);
                Directory.CreateDirectory(Path.GetDirectoryName(outJson)!);

                var instances = LoadCollisionInstances(msbPath);
                var instancesByModel = instances
                    .GroupBy(x => x.ModelName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                var hkxFiles = Directory.GetFiles(hkxDir, "*.hkx", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToList();
                var instanceStats = new List<InstanceStat>();
                var fileStats = new List<FileStat>();

                foreach (var hkx in hkxFiles)
                {
                    var fileTris = new List<Tri>();
                    var errors = new List<string>();
                    int bodies = 0;
                    try
                    {
                        var root = LoadRoot(hkx);
                        if (root.m_namedVariants == null || root.m_namedVariants.Count == 0)
                        {
                            errors.Add("no namedVariants");
                        }
                        else
                        {
                            var scene = root.m_namedVariants[0].m_variant as hknpPhysicsSceneData;
                            if (scene == null || scene.m_systemDatas == null || scene.m_systemDatas.Count == 0)
                            {
                                errors.Add("no physics scene/systemDatas");
                            }
                            else
                            {
                                var bodyInfos = scene.m_systemDatas[0].m_bodyCinfos;
                                for (int bi = 0; bi < bodyInfos.Count; bi++)
                                {
                                    var body = bodyInfos[bi];
                                    var shape = body.m_shape;
                                    if (shape == null) { continue; }
                                    bodies++;
                                    try
                                    {
                                        if (shape is fsnpCustomParamCompressedMeshShape fsnp)
                                        {
                                            DecodeCompressedMesh(fsnp.m_data, body, bi, body.m_collisionFilterInfo, fileTris);
                                        }
                                        else if (shape is hknpCompressedMeshShape cmesh)
                                        {
                                            DecodeCompressedMesh(cmesh.m_data, body, bi, body.m_collisionFilterInfo, fileTris);
                                        }
                                        else if (shape is hknpConvexPolytopeShape convex)
                                        {
                                            DecodeConvexPolytope(convex, body, bi, body.m_collisionFilterInfo, fileTris);
                                        }
                                        else
                                        {
                                            errors.Add($"body[{bi}] unsupported shape: {shape.GetType().Name}");
                                        }
                                    }
                                    catch (Exception exb)
                                    {
                                        errors.Add($"body[{bi}] {shape.GetType().Name}: {exb.GetType().Name}: {exb.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"file parse failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    var modelName = HkxFileToModelName(hkx);
                    int outTriCount = 0;
                    int outObjCount = 0;
                    if (instancesByModel.TryGetValue(modelName, out var modelInstances) && modelInstances.Count > 0)
                    {
                        for (int ii = 0; ii < modelInstances.Count; ii++)
                        {
                            var inst = modelInstances[ii];
                            var triInst = ApplyPartTransform(fileTris, inst.Position, inst.RotationDeg);
                            outTriCount += triInst.Count;
                            var safeName = SanitizeName(inst.Name);
                            var outObjFile = Path.Combine(
                                outObjDir,
                                $"{Path.GetFileNameWithoutExtension(hkx)}__inst_{ii:D3}__{safeName}.obj");
                            WriteObj(outObjFile, triInst, inst.HitFilterID);
                            var outObjSizeBytes = new FileInfo(outObjFile).Length;
                            instanceStats.Add(new InstanceStat(
                                Path.GetFileName(hkx),
                                inst.Name,
                                inst.ModelName,
                                inst.HitFilterID,
                                ResolveDs3HitFilterType(inst.HitFilterID),
                                ToRepoRelativeWebPath(outObjFile, repoRoot),
                                outObjSizeBytes));
                            outObjCount++;
                        }
                    }
                    else
                    {
                        var outObjFile = Path.Combine(outObjDir, Path.GetFileNameWithoutExtension(hkx) + ".obj");
                        WriteObj(outObjFile, fileTris, 0xFF);
                        var outObjSizeBytes = new FileInfo(outObjFile).Length;
                        instanceStats.Add(new InstanceStat(
                            Path.GetFileName(hkx),
                            Path.GetFileNameWithoutExtension(hkx),
                            modelName,
                            0xFF,
                            ResolveDs3HitFilterType(0xFF),
                            ToRepoRelativeWebPath(outObjFile, repoRoot),
                            outObjSizeBytes));
                        outTriCount = fileTris.Count;
                        outObjCount = 1;
                    }
                    fileStats.Add(new FileStat(Path.GetFileName(hkx), bodies, errors));
                    Console.WriteLine($"{Path.GetFileName(hkx)} bodies={bodies} inst={outObjCount} errs={errors.Count}");
                }

                WriteJson(outJson, hkxFiles, fileStats, instanceStats, repoRoot);

                var failFiles = fileStats.Where(f => f.Errors.Count > 0).ToList();
                Console.WriteLine($"HKX files: {hkxFiles.Count}, fail files: {failFiles.Count}");
                foreach (var f in failFiles.Take(20))
                {
                    Console.WriteLine($"FAIL {f.File}: {string.Join(" || ", f.Errors.Take(2))}");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        static string HkxFileToModelName(string hkxPath)
        {
            var stem = Path.GetFileNameWithoutExtension(hkxPath);
            var us = stem.LastIndexOf('_');
            if (us < 0 || us + 1 >= stem.Length) return stem;
            var suffix = stem.Substring(us + 1);
            return "h" + suffix;
        }

        static string SanitizeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "unnamed";
            var bad = Path.GetInvalidFileNameChars();
            var outChars = raw.Select(c => bad.Contains(c) ? '_' : c).ToArray();
            return new string(outChars);
        }

        static List<CollisionInstance> LoadCollisionInstances(string msbPath)
        {
            if (!File.Exists(msbPath))
            {
                Console.WriteLine($"WARN msb not found: {msbPath}");
                return new List<CollisionInstance>();
            }

            var msb = MSB3.Read(msbPath);
            var list = new List<CollisionInstance>();
            foreach (var part in msb.Parts.Collisions)
            {
                list.Add(new CollisionInstance(
                    part.Name ?? "",
                    part.ModelName ?? "",
                    part.Position,
                    part.Rotation,
                    part.HitFilterID));
            }
            Console.WriteLine($"MSB collisions: {list.Count}");
            return list;
        }

        static Quaternion EulerDegToQuatYZX(Vector3 deg)
        {
            var rad = new Vector3(
                MathF.PI / 180.0f * deg.X,
                MathF.PI / 180.0f * deg.Y,
                MathF.PI / 180.0f * deg.Z);
            var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, rad.Y);
            var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rad.Z);
            var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rad.X);
            return qy * qz * qx;
        }

        static List<Tri> ApplyPartTransform(List<Tri> src, Vector3 pos, Vector3 rotDeg)
        {
            var q = EulerDegToQuatYZX(rotDeg);
            var outTris = new List<Tri>(src.Count);
            foreach (var t in src)
            {
                outTris.Add(new Tri(
                    Vector3.Transform(t.A, q) + pos,
                    Vector3.Transform(t.B, q) + pos,
                    Vector3.Transform(t.C, q) + pos,
                    t.BodyIndex,
                    t.Filter));
            }
            return outTris;
        }

        static hkRootLevelContainer LoadRoot(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var br = new BinaryReaderEx(false, bytes);
            var root = new PackFileDeserializer().Deserialize(br) as hkRootLevelContainer;
            if (root == null) throw new Exception("root is not hkRootLevelContainer");
            return root;
        }

        static void DecodeCompressedMesh(hknpCompressedMeshShapeData data, hknpBodyCinfo body, int bodyIndex, uint filter, List<Tri> outTris)
        {
            foreach (var section in data.m_meshTree.m_sections)
            {
                var primitiveCount = (int)(section.m_primitives.m_data & 0xFF);
                var primitiveBase = (int)(section.m_primitives.m_data >> 8);
                var sharedVerticesLength = (int)(section.m_sharedVertices.m_data & 0xFF);
                var sharedVerticesIndex = (int)(section.m_sharedVertices.m_data >> 8);
                var smallOffset = new Vector3(section.m_codecParms_0, section.m_codecParms_1, section.m_codecParms_2);
                var smallScale = new Vector3(section.m_codecParms_3, section.m_codecParms_4, section.m_codecParms_5);

                for (int i = 0; i < primitiveCount; i++)
                {
                    var tri = data.m_meshTree.m_primitives[primitiveBase + i];
                    if (tri.m_indices_0 == 0xDE && tri.m_indices_1 == 0xAD && tri.m_indices_2 == 0xDE && tri.m_indices_3 == 0xAD)
                        continue;

                    var v0 = DecodeVertex(data, section, tri.m_indices_0, sharedVerticesLength, sharedVerticesIndex, smallScale, smallOffset);
                    var v1 = DecodeVertex(data, section, tri.m_indices_1, sharedVerticesLength, sharedVerticesIndex, smallScale, smallOffset);
                    var v2 = DecodeVertex(data, section, tri.m_indices_2, sharedVerticesLength, sharedVerticesIndex, smallScale, smallOffset);
                    v0 = Transform(v0, body);
                    v1 = Transform(v1, body);
                    v2 = Transform(v2, body);
                    outTris.Add(new Tri(v0, v1, v2, bodyIndex, filter));

                    if (tri.m_indices_2 != tri.m_indices_3)
                    {
                        var v3 = DecodeVertex(data, section, tri.m_indices_3, sharedVerticesLength, sharedVerticesIndex, smallScale, smallOffset);
                        v3 = Transform(v3, body);
                        outTris.Add(new Tri(v0, v2, v3, bodyIndex, filter));
                    }
                }
            }
        }

        static Vector3 DecodeVertex(
            hknpCompressedMeshShapeData data,
            hkcdStaticMeshTreeBaseSection section,
            byte idx,
            int sharedVerticesLength,
            int sharedVerticesIndex,
            Vector3 smallScale,
            Vector3 smallOffset)
        {
            if (idx < sharedVerticesLength)
            {
                var index = (ushort)(idx + section.m_firstPackedVertex);
                return data.DecompressPackedVertex(data.m_meshTree.m_packedVertices[index], smallScale, smallOffset);
            }
            else
            {
                var index = data.m_meshTree.m_sharedVerticesIndex[idx + sharedVerticesIndex - sharedVerticesLength];
                return data.DecompressSharedVertex(
                    data.m_meshTree.m_sharedVertices[index],
                    data.m_meshTree.m_domain.m_min,
                    data.m_meshTree.m_domain.m_max);
            }
        }

        static void DecodeConvexPolytope(hknpConvexPolytopeShape shape, hknpBodyCinfo body, int bodyIndex, uint filter, List<Tri> outTris)
        {
            if (shape.m_vertices == null || shape.m_faces == null || shape.m_indices == null) return;
            var verts = shape.m_vertices.Select(v => Transform(new Vector3(v.X, v.Y, v.Z), body)).ToList();
            if (verts.Count == 0) return;
            foreach (var face in shape.m_faces)
            {
                int start = face.m_firstIndex;
                int count = face.m_numIndices;
                if (count < 3) continue;
                int i0 = shape.m_indices[start];
                for (int i = 1; i + 1 < count; i++)
                {
                    int i1 = shape.m_indices[start + i];
                    int i2 = shape.m_indices[start + i + 1];
                    if (i0 >= verts.Count || i1 >= verts.Count || i2 >= verts.Count) continue;
                    outTris.Add(new Tri(verts[i0], verts[i1], verts[i2], bodyIndex, filter));
                }
            }
        }

        static Vector3 Transform(Vector3 v, hknpBodyCinfo body)
        {
            var p = new Vector3(body.m_position.X, body.m_position.Y, body.m_position.Z);
            return Vector3.Transform(v, body.m_orientation) + p;
        }

        static void WriteObj(string path, List<Tri> tris, byte msbHitFilterId)
        {
            using var sw = new StreamWriter(path, false, Encoding.UTF8);
            int nextIdx = 1;
            int lastBody = -1;
            uint lastFilter = 0;
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
                if (t.BodyIndex != lastBody || t.Filter != lastFilter)
                {
                    sw.WriteLine($"o body_{t.BodyIndex}_hkx_{t.Filter:X8}_msb_{msbHitFilterId:X2}");
                    sw.WriteLine($"g body_{t.BodyIndex}_hkx_{t.Filter:X8}_msb_{msbHitFilterId:X2}");
                    lastBody = t.BodyIndex;
                    lastFilter = t.Filter;
                }
                int ia = GetOrCreateVertexIndex(t.A);
                int ib = GetOrCreateVertexIndex(t.B);
                int ic = GetOrCreateVertexIndex(t.C);
                sw.WriteLine($"f {ia} {ib} {ic}");
            }
        }

        static void WriteJson(string path, List<string> files, List<FileStat> fileStats, List<InstanceStat> instanceStats, string repoRoot)
        {
            var msbHitFilterStats = instanceStats
                .GroupBy(i => i.MsbHitFilterId)
                .Select(g => new {
                    raw = (int)g.Key,
                    hex = $"0x{g.Key:X2}",
                    count = g.Count()
                })
                .OrderBy(x => x.raw)
                .ToList();

            var payload = new {
                map_id = "m40_00_00_00",
                repo_root_abs = Path.GetFullPath(repoRoot),
                source_hkx_files = files.Select(f => ToRepoRelativeWebPath(f, repoRoot)).ToList(),
                files = fileStats,
                instances = instanceStats,
                msb_hit_filter_stats = msbHitFilterStats
            };
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, opts));
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
                throw new InvalidOperationException($"Output file is outside repo root: {absFile}");
            }
            return "/" + rel;
        }

        static string ResolveDs3HitFilterType(byte id)
        {
            return id switch
            {
                0 => "Standard: No High Collision - No Foot IK",
                1 => "Standard: No High Collision",
                2 => "Standard: No High Collision",
                3 => "Standard: No High Collision",
                4 => "Standard: No High Collision",
                5 => "Standard: No High Collision",
                6 => "Standard: No High Collision",
                7 => "Standard: No High Collision",
                8 => "Collide with all characters",
                9 => "Collide with camera only",
                11 => "Collide with non-player characters only",
                13 => "Trigger fall death camera in collision",
                14 => "Trigger fall death camera in collision",
                15 => "Trigger instant death on collision",
                16 => "Type 16",
                17 => "Type 17",
                19 => "Collide with non-player characters only",
                20 => "Type 20",
                21 => "Slide movement",
                22 => "Block all fall damage",
                23 => "Type 23",
                24 => "Type 24",
                29 => "Type 29",
                _ => $"Unknown ({id})"
            };
        }
    }
}

