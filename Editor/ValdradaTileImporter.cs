#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityCoder.ValdradaViewer;

namespace UnityCoder.ValdradaViewer.Editor
{
    // A scene-based, single-tile importer for Valdrada v0.6.x CTL1 files.
    // All generated meshes and materials are saved in one .asset file per import.
    public sealed class ValdradaTileImporter : EditorWindow
    {
        private const string OutputFolder = "Assets/ValdradaGenerated";
        private static string AnchorKey => "ValdradaTileImporter." + Application.dataPath + ".Anchor";
        private const int MaxSoupVertices = 30000;

        [Serializable]
        private sealed class TileHeader
        {
            public int v;
            public int tx;
            public int tz;
            public int res;
            public float[] origin;
            public TileSection[] sections;
        }

        [Serializable]
        private sealed class TileSection
        {
            public string name;
            public string type;
            public int offset;
            public int bytes;
            public int length;
        }

        private sealed class TileFile
        {
            private readonly byte[] bytes;
            private readonly int dataOffset;
            private readonly Dictionary<string, TileSection> sections = new Dictionary<string, TileSection>();
            public readonly TileHeader header;

            public TileFile(string file)
            {
                bytes = File.ReadAllBytes(file);
                if (bytes.Length < 12 || BitConverter.ToUInt32(bytes, 0) != 0x43544c31)
                    throw new InvalidDataException("This is not a Valdrada CTL1 tile. Choose a t_*.bin file, not far_*.bin.");

                uint headerLength = BitConverter.ToUInt32(bytes, 4);
                if (headerLength > bytes.Length - 8)
                    throw new InvalidDataException("Invalid tile header length.");

                header = JsonUtility.FromJson<TileHeader>(System.Text.Encoding.UTF8.GetString(bytes, 8, (int)headerLength));
                if (header == null || header.sections == null || header.origin == null || header.origin.Length != 2)
                    throw new InvalidDataException("Invalid tile header JSON.");

                dataOffset = ((8 + (int)headerLength + 3) / 4) * 4;
                foreach (TileSection section in header.sections)
                {
                    if (section.offset < 0 || section.bytes < 0 || (long)dataOffset + section.offset + section.bytes > bytes.Length)
                        throw new InvalidDataException("Invalid section bounds: " + section.name);
                    sections[section.name] = section;
                }
            }

            public byte[] Bytes(string name)
            {
                if (!sections.TryGetValue(name, out TileSection s)) return Array.Empty<byte>();
                byte[] result = new byte[s.bytes];
                Buffer.BlockCopy(bytes, dataOffset + s.offset, result, 0, s.bytes);
                return result;
            }

            public float[] Floats(string name)
            {
                if (!sections.TryGetValue(name, out TileSection s)) return Array.Empty<float>();
                if (s.type != "Float32Array" || s.bytes % 4 != 0)
                    throw new InvalidDataException("Unexpected type of " + name + ": " + s.type);
                float[] result = new float[s.bytes / 4];
                Buffer.BlockCopy(bytes, dataOffset + s.offset, result, 0, s.bytes);
                return result;
            }
        }

        private struct Hole
        {
            public int start;
            public int length;
        }

        private string sourcePath;
        private bool buildings = true;
        private bool terrain = true;
        private bool surfaces = true;
        private bool colliders;
        private bool roofs = true;
        // Valdrada uses Three.js coordinates (x east, z south). Unity is left-handed.
        // Negating Z converts handedness and preserves readable road markings.
        private bool flipZ = true;
        private int maxBuildings;
        private int buildingCount;
        private int openCourtyardCount;
        private ValdradaTileAsset currentAsset;
        private readonly Dictionary<int, Material> materialCache = new Dictionary<int, Material>();

        [MenuItem("Tools/Valdrada/Tile Importer")]
        private static void Open() => GetWindow<ValdradaTileImporter>("Valdrada Importer");

        private void OnEnable()
        {
            sourcePath = EditorPrefs.GetString("ValdradaTileImporter.SourcePath", "");
            flipZ = EditorPrefs.GetBool("ValdradaTileImporter.FlipZ", true);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Valdrada CTL1 Tile Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Select a high-detail t_X_Z.bin tile (not far_X_Z.bin). Files may remain outside Assets.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.TextField("Tile", sourcePath);
                if (GUILayout.Button("Browse", GUILayout.Width(75)))
                {
                    string directory = string.IsNullOrEmpty(sourcePath) ? "" : Path.GetDirectoryName(sourcePath);
                    string picked = EditorUtility.OpenFilePanel("Select Valdrada tile", directory, "bin");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        sourcePath = picked;
                        EditorPrefs.SetString("ValdradaTileImporter.SourcePath", sourcePath);
                    }
                }
            }
            buildings = EditorGUILayout.Toggle("Import Buildings", buildings);
            roofs = EditorGUILayout.Toggle("Simple Flat Roofs", roofs);
            terrain = EditorGUILayout.Toggle("Import Terrain", terrain);
            surfaces = EditorGUILayout.Toggle("Import Ground Surfaces", surfaces);
            colliders = EditorGUILayout.Toggle("Generate Mesh Colliders", colliders);
            maxBuildings = Mathf.Max(0, EditorGUILayout.IntField("Max Buildings (0 = All)", maxBuildings));
            bool newFlipZ = EditorGUILayout.Toggle("Convert Three.js Coordinates (Flip Z)", flipZ);
            if (newFlipZ != flipZ)
            {
                flipZ = newFlipZ;
                EditorPrefs.SetBool("ValdradaTileImporter.FlipZ", flipZ);
            }
            EditorGUILayout.HelpBox("Keep Flip Z enabled for Unity. This fixes mirrored road markings and keeps the whole tile consistent. Remove previously imported tiles and reimport after changing it.", MessageType.None);

            GUILayout.Space(8);
            EditorGUILayout.LabelField("Positioning", EditorStyles.boldLabel);
            if (EditorPrefs.HasKey(AnchorKey + "X"))
            {
                int ax = EditorPrefs.GetInt(AnchorKey + "X");
                int az = EditorPrefs.GetInt(AnchorKey + "Z");
                EditorGUILayout.LabelField("Scene origin tile", $"{ax}, {az}");
            }
            else EditorGUILayout.LabelField("Scene origin tile", "First imported tile");
            if (GUILayout.Button("Reset Scene Origin (next imported tile is at 0,0)"))
            {
                EditorPrefs.DeleteKey(AnchorKey + "X");
                EditorPrefs.DeleteKey(AnchorKey + "Z");
            }

            GUILayout.Space(10);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)))
            {
                if (GUILayout.Button("Import Tile", GUILayout.Height(34))) Import();
            }
            EditorGUILayout.HelpBox("Creates a new scene GameObject and stores meshes/materials in Assets/ValdradaGenerated/. Roof holes and advanced procedural facade details are not yet reproduced.", MessageType.None);
        }

        private void Import()
        {
            GameObject root = null;
            string assetPath = null;
            try
            {
                if (Path.GetFileName(sourcePath).StartsWith("far_", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Choose a t_*.bin tile, not a far_*.bin LOD tile.");

                TileFile tile = new TileFile(sourcePath);
                if (tile.header.res <= 0 || tile.header.res > 2048)
                    throw new InvalidDataException("Unexpected terrain grid resolution: " + tile.header.res);

                if (!AssetDatabase.IsValidFolder(OutputFolder)) AssetDatabase.CreateFolder("Assets", "ValdradaGenerated");
                string tileName = $"t_{tile.header.tx}_{tile.header.tz}";
                assetPath = AssetDatabase.GenerateUniqueAssetPath($"{OutputFolder}/{tileName}.asset");
                currentAsset = CreateInstance<ValdradaTileAsset>();
                currentAsset.name = tileName;
                currentAsset.sourceFile = sourcePath;
                currentAsset.tileX = tile.header.tx;
                currentAsset.tileZ = tile.header.tz;
                currentAsset.flippedZ = flipZ;
                AssetDatabase.CreateAsset(currentAsset, assetPath);

                int ax = EditorPrefs.GetInt(AnchorKey + "X", tile.header.tx);
                int az = EditorPrefs.GetInt(AnchorKey + "Z", tile.header.tz);
                root = new GameObject("Valdrada_" + tileName);
                Undo.RegisterCreatedObjectUndo(root, "Import Valdrada Tile");
                root.transform.position = new Vector3((tile.header.tx - ax) * 512f, 0f, (flipZ ? -1f : 1f) * (tile.header.tz - az) * 512f);

                buildingCount = 0;
                openCourtyardCount = 0;
                materialCache.Clear();

                if (terrain) ImportTerrain(tile, root.transform);
                if (surfaces) ImportSurfaces(tile, root.transform);
                if (buildings) ImportBuildings(tile, root.transform);

                currentAsset.buildingCount = buildingCount;
                EditorUtility.SetDirty(currentAsset);
                AssetDatabase.SaveAssets();
                EditorPrefs.SetInt(AnchorKey + "X", ax);
                EditorPrefs.SetInt(AnchorKey + "Z", az);
                Selection.activeGameObject = root;
                SceneView.lastActiveSceneView?.FrameSelected();
                Debug.Log($"Valdrada: imported {tileName}, {buildingCount} buildings, {openCourtyardCount} buildings with courtyard roof openings. Saved meshes/materials: {assetPath}", root);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (root != null) DestroyImmediate(root);
                if (!string.IsNullOrEmpty(assetPath)) AssetDatabase.DeleteAsset(assetPath);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                currentAsset = null;
                materialCache.Clear();
            }
        }

        private void ImportTerrain(TileFile tile, Transform parent)
        {
            float[] h = tile.Floats("terrain");
            int r = tile.header.res;
            if (h.Length != (r + 1) * (r + 1))
            {
                Debug.LogWarning("Valdrada: terrain grid not available or unexpected length.");
                return;
            }

            int side = r + 1;
            var verts = new Vector3[side * side];
            var triangles = new int[r * r * 6];
            for (int z = 0; z <= r; z++)
                for (int x = 0; x <= r; x++)
                    verts[z * side + x] = new Vector3(x * (512f / r), h[z * side + x], (flipZ ? -z : z) * (512f / r));

            int k = 0;
            for (int z = 0; z < r; z++)
            for (int x = 0; x < r; x++)
            {
                int a = z * side + x;
                int b = a + 1;
                int c = a + side;
                int d = c + 1;
                triangles[k++] = a;
                triangles[k++] = flipZ ? b : c;
                triangles[k++] = flipZ ? c : b;
                triangles[k++] = b;
                triangles[k++] = flipZ ? d : c;
                triangles[k++] = flipZ ? c : d;
            }

            Mesh mesh = MakeMesh("Terrain", verts, triangles);
            AddObject(mesh, "Terrain", parent, GetMaterial(0x77786F, "Terrain"));
        }

        private void ImportSurfaces(TileFile tile, Transform parent)
        {
            // Tile compiler already triangulates these into consecutive XYZ triples.
            string[] names = { "asphalt", "sidewalk", "curb", "paintW", "paintY", "paintG", "grass", "grassU", "path", "brick", "gutter", "busred", "warn", "warnIron", "apron", "rsteel", "rbal", "rtop", "rwall" };
            int[] colors = { 0x37393C, 0x999A96, 0x757575, 0xE6E7E5, 0xE1C74C, 0x92C18A, 0x648A4E, 0x7EA168, 0xA49982, 0x9F7461, 0x444748, 0x9C3E3B, 0xE7C16D, 0x545859, 0x8B877D, 0x697480, 0x9C9D98, 0x6C787E, 0x77787B };
            var group = new GameObject("Ground Surfaces");
            group.transform.SetParent(parent, false);

            for (int n = 0; n < names.Length; n++)
            {
                float[] data = tile.Floats(names[n]);
                if (data.Length == 0) continue;
                if (data.Length % 9 != 0)
                {
                    Debug.LogWarning($"Valdrada: skipped {names[n]} because its float count is not a multiple of 9.");
                    continue;
                }

                var mat = GetMaterial(colors[n], names[n]);
                int vertCount = data.Length / 3;
                for (int first = 0, chunk = 0; first < vertCount; first += MaxSoupVertices, chunk++)
                {
                    int count = Math.Min(MaxSoupVertices, vertCount - first);
                    Vector3[] verts = new Vector3[count];
                    int[] tris = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        int j = (first + i) * 3;
                        verts[i] = new Vector3(data[j], data[j + 1], flipZ ? -data[j + 2] : data[j + 2]);
                    }
                    for (int i = 0; i < count; i += 3)
                    {
                        tris[i] = i;
                        tris[i + 1] = flipZ ? i + 2 : i + 1;
                        tris[i + 2] = flipZ ? i + 1 : i + 2;
                    }
                    string name = names[n] + "_" + chunk;
                    AddObject(MakeMesh(name, verts, tris), name, group.transform, mat);
                }
            }
        }

        private void ImportBuildings(TileFile tile, Transform parent)
        {
            byte[] records = tile.Bytes("bldg");
            byte[] holeRecords = tile.Bytes("bholes");
            float[] xz = tile.Floats("bldgXZ");
            if (records.Length == 0 || xz.Length == 0) return;
            if (records.Length % 44 != 0 || holeRecords.Length % 12 != 0)
                throw new InvalidDataException("Building metadata has an unexpected record size.");

            var holes = new Dictionary<int, List<Hole>>();
            for (int i = 0; i < holeRecords.Length; i += 12)
            {
                int owner = checked((int)BitConverter.ToUInt32(holeRecords, i));
                var hole = new Hole { start = checked((int)BitConverter.ToUInt32(holeRecords, i + 4)), length = BitConverter.ToUInt16(holeRecords, i + 8) };
                if (!holes.TryGetValue(owner, out List<Hole> list)) holes[owner] = list = new List<Hole>();
                list.Add(hole);
            }

            var group = new GameObject("Buildings");
            group.transform.SetParent(parent, false);
            int total = records.Length / 44;
            int limit = maxBuildings > 0 ? Math.Min(maxBuildings, total) : total;

            for (int i = 0; i < limit; i++)
            {
                if ((i & 127) == 0 && EditorUtility.DisplayCancelableProgressBar("Valdrada Tile Importer", $"Building {i} / {limit}", (float)i / Math.Max(limit, 1)))
                    throw new OperationCanceledException("Import cancelled.");

                int pos = i * 44;
                int start = checked((int)BitConverter.ToUInt32(records, pos));
                int len = BitConverter.ToUInt16(records, pos + 4);
                float baseY = BitConverter.ToSingle(records, pos + 8);
                float height = BitConverter.ToSingle(records, pos + 12);
                if (len < 3 || height <= 0f || len > 10000) continue;
                if ((long)(start + len) * 2 > xz.Length) continue;

                List<Vector2> ring = ReadRing(xz, start, len);
                if (ring.Count < 3) continue;

                var vs = new List<Vector3>(ring.Count * 4);
                var ts = new List<int>(ring.Count * 9);
                AddWalls(vs, ts, ring, baseY, height, false);
                bool hasHoles = holes.TryGetValue(i, out List<Hole> buildingHoles) && buildingHoles.Count > 0;
                if (hasHoles)
                {
                    openCourtyardCount++;
                    foreach (Hole hole in buildingHoles)
                    {
                        if (hole.length >= 3 && (long)(hole.start + hole.length) * 2 <= xz.Length)
                            AddWalls(vs, ts, ReadRing(xz, hole.start, hole.length), baseY, height, true);
                    }
                }
                // Keep a courtyard open instead of mistakenly covering it with a solid roof.
                if (roofs && !hasHoles) AddRoof(vs, ts, ring, baseY + height);
                if (ts.Count == 0) continue;

                int r = records[pos + 18], g = records[pos + 19], b = records[pos + 20];
                // Quantize to 16 levels per channel to avoid hundreds of near-identical materials.
                int color = ((r / 16 * 16) << 16) | ((g / 16 * 16) << 8) | (b / 16 * 16);
                string name = "Building_" + i;
                AddObject(MakeMesh(name, vs.ToArray(), ts.ToArray()), name, group.transform, GetMaterial(color, "Building"));
                buildingCount++;
            }
        }

        private List<Vector2> ReadRing(float[] xz, int start, int len)
        {
            var ring = new List<Vector2>(len);
            for (int i = 0; i < len; i++)
            {
                float x = xz[(start + i) * 2];
                float z = xz[(start + i) * 2 + 1];
                var p = new Vector2(x, flipZ ? -z : z);
                if (ring.Count == 0 || (p - ring[ring.Count - 1]).sqrMagnitude > 0.000001f) ring.Add(p);
            }
            if (ring.Count > 2 && (ring[0] - ring[ring.Count - 1]).sqrMagnitude < 0.000001f)
                ring.RemoveAt(ring.Count - 1);
            return ring;
        }

        private static float SignedArea(List<Vector2> ring)
        {
            float a = 0f;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 u = ring[i], v = ring[(i + 1) % ring.Count];
                a += u.x * v.y - v.x * u.y;
            }
            return a * 0.5f;
        }

        private static void AddWalls(List<Vector3> vs, List<int> ts, List<Vector2> ring, float y, float h, bool inner)
        {
            if (ring.Count < 3) return;
            bool reverse = (SignedArea(ring) < 0f) != inner;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % ring.Count];
                int first = vs.Count;
                vs.Add(new Vector3(a.x, y, a.y));
                vs.Add(new Vector3(a.x, y + h, a.y));
                vs.Add(new Vector3(b.x, y + h, b.y));
                vs.Add(new Vector3(b.x, y, b.y));
                if (!reverse)
                {
                    ts.Add(first); ts.Add(first + 1); ts.Add(first + 2);
                    ts.Add(first); ts.Add(first + 2); ts.Add(first + 3);
                }
                else
                {
                    ts.Add(first); ts.Add(first + 2); ts.Add(first + 1);
                    ts.Add(first); ts.Add(first + 3); ts.Add(first + 2);
                }
            }
        }

        // Simple ear clipping for ordinary concave polygons. Does not triangulate courtyard holes.
        private static void AddRoof(List<Vector3> vs, List<int> ts, List<Vector2> ring, float y)
        {
            int n = ring.Count;
            if (n < 3) return;
            bool ccw = SignedArea(ring) > 0;
            var indices = new List<int>(n);
            for (int i = 0; i < n; i++) indices.Add(i);
            var roofTris = new List<int>();
            int guard = n * n;
            while (indices.Count > 3 && guard-- > 0)
            {
                bool foundEar = false;
                for (int j = 0; j < indices.Count; j++)
                {
                    int prev = indices[(j + indices.Count - 1) % indices.Count];
                    int cur = indices[j];
                    int next = indices[(j + 1) % indices.Count];
                    Vector2 a = ring[prev], b = ring[cur], c = ring[next];
                    float cross = Cross(a, b, c);
                    if (ccw ? cross <= 0.000001f : cross >= -0.000001f) continue;

                    bool contains = false;
                    for (int k = 0; k < indices.Count; k++)
                    {
                        int other = indices[k];
                        if (other == prev || other == cur || other == next) continue;
                        if (PointInTriangle(ring[other], a, b, c, ccw)) { contains = true; break; }
                    }
                    if (contains) continue;

                    if (ccw) { roofTris.Add(prev); roofTris.Add(next); roofTris.Add(cur); }
                    else { roofTris.Add(prev); roofTris.Add(cur); roofTris.Add(next); }
                    indices.RemoveAt(j);
                    foundEar = true;
                    break;
                }
                if (!foundEar) return; // Non-simple polygon; keep walls instead of a broken roof.
            }
            if (indices.Count == 3)
            {
                if (ccw) { roofTris.Add(indices[0]); roofTris.Add(indices[2]); roofTris.Add(indices[1]); }
                else { roofTris.Add(indices[0]); roofTris.Add(indices[1]); roofTris.Add(indices[2]); }
            }
            int offset = vs.Count;
            for (int i = 0; i < n; i++) vs.Add(new Vector3(ring[i].x, y, ring[i].y));
            foreach (int id in roofTris) ts.Add(offset + id);
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c, bool ccw)
        {
            const float e = 0.000001f;
            float ab = Cross(a, b, p), bc = Cross(b, c, p), ca = Cross(c, a, p);
            return ccw ? ab >= -e && bc >= -e && ca >= -e : ab <= e && bc <= e && ca <= e;
        }

        private static Mesh MakeMesh(string name, Vector3[] vertices, int[] triangles)
        {
            var mesh = new Mesh { name = name };
            if (vertices.Length > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void AddObject(Mesh mesh, string name, Transform parent, Material material)
        {
            AssetDatabase.AddObjectToAsset(mesh, currentAsset);
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            obj.AddComponent<MeshRenderer>().sharedMaterial = material;
            if (colliders) obj.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private Material GetMaterial(int rgb, string prefix)
        {
            if (materialCache.TryGetValue(rgb, out Material mat)) return mat;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("No compatible Lit shader was found.");
            mat = new Material(shader) { name = $"{prefix}_{rgb:X6}" };
            var color = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.12f);
            AssetDatabase.AddObjectToAsset(mat, currentAsset);
            materialCache[rgb] = mat;
            return mat;
        }
    }
}
#endif
