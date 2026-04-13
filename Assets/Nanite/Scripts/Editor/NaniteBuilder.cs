using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using Nanite;

namespace Nanite.Editor
{
    public class NaniteBuilder : EditorWindow
    {
        public Mesh sourceMesh;
        public int trianglesPerCluster = 128;
        [Range(0.2f, 0.95f)]
        public float simplifyRatio = 0.5f;

        [MenuItem("Nanite/Builder")]
        public static void ShowWindow()
        {
            GetWindow<NaniteBuilder>("Nanite Builder");
        }

        private void OnGUI()
        {
            GUILayout.Label("Build Nanite Mesh (DAG LOD Preprocessing)", EditorStyles.boldLabel);
            
            sourceMesh = (Mesh)EditorGUILayout.ObjectField("Source Mesh", sourceMesh, typeof(Mesh), false);
            trianglesPerCluster = EditorGUILayout.IntSlider("Triangles Per Cluster", trianglesPerCluster, 64, 256);
            simplifyRatio = EditorGUILayout.Slider("Simplify Ratio / LOD", simplifyRatio, 0.2f, 0.95f);

            if (GUILayout.Button("Build Nanite Asset"))
            {
                if (sourceMesh != null)
                {
                    BuildNaniteMesh(sourceMesh, trianglesPerCluster);
                }
                else
                {
                    Debug.LogWarning("Please assign a source mesh.");
                }
            }
        }

        class BuildCluster
        {
            public int[] indices;
            public Vector3 boundsCenter;
            public float boundsRadius;
            public float lodError;
            public float parentLodError;
            public Vector3 lodBoundsCenter;
            public float lodBoundsRadius;
            public Vector3 parentBoundsCenter;
            public float parentBoundsRadius;
            public int lodLevel;
        }

        static (Vector3 center, float radius) ComputeGroupBounds(List<BuildCluster> group)
        {
            Vector3 center = Vector3.zero;
            foreach (var c in group) center += c.boundsCenter;
            center /= group.Count;

            float radius = 0f;
            foreach (var c in group)
            {
                float d = Vector3.Distance(center, c.boundsCenter) + c.boundsRadius;
                radius = Mathf.Max(radius, d);
            }
            return (center, Mathf.Max(radius, 0.0001f));
        }

        private void BuildNaniteMesh(Mesh mesh, int trisPerCluster)
        {
            if (mesh == null)
            {
                Debug.LogWarning("Source mesh is null.");
                return;
            }

            if (!mesh.isReadable)
            {
                Debug.LogError("Source mesh is not readable. Enable Read/Write in import settings first.");
                return;
            }

            int[] meshTriangles = mesh.triangles;
            if (meshTriangles == null || meshTriangles.Length < 3 || meshTriangles.Length % 3 != 0)
            {
                Debug.LogError("Source mesh triangles are invalid.");
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("Save Nanite Mesh", mesh.name + "_Nanite", "asset", "Save Nanite Mesh Asset");
            if (string.IsNullOrEmpty(path)) return;

            NaniteMesh naniteMesh = ScriptableObject.CreateInstance<NaniteMesh>();
            naniteMesh.trianglesPerCluster = trisPerCluster; 

            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uvs = mesh.uv;

            bool hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasUVs = uvs != null && uvs.Length == vertices.Length;

            naniteMesh.vertices = new NaniteVertex[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                naniteMesh.vertices[i] = new NaniteVertex
                {
                    position = vertices[i],
                    normal = hasNormals ? normals[i] : Vector3.up,
                    uv = hasUVs ? uvs[i] : Vector2.zero
                };
            }

            int[] currentIndices = meshTriangles;
            uint maxVertices = 128; 
            uint maxTriangles = (uint)trisPerCluster;
            
            float meshDiagonal = mesh.bounds.size.magnitude;
            float baseErrorIncrement = Mathf.Max(meshDiagonal * 0.005f, 0.0001f);

            List<BuildCluster> allClusters = new List<BuildCluster>();
            List<string> lodLevelInfo = new List<string>();
            
            // --- LOD 0 ---
            float currentError = 0.0f;
            List<BuildCluster> currentLODClusters = ClusterizeIndices(currentIndices, vertices, maxVertices, maxTriangles, currentError, 0);
            allClusters.AddRange(currentLODClusters);
            lodLevelInfo.Add($"  LOD0: {currentLODClusters.Count} clusters, error=0");

            // --- Higher LOD levels (DAG) ---
            int maxLODLevels = 30; 
            int totalLodLevels = 1;
            for (int lod = 1; lod <= maxLODLevels; lod++)
            {
                if (currentLODClusters.Count <= 1) break;

                List<BuildCluster> nextLODClusters = new List<BuildCluster>();
                
                List<List<BuildCluster>> groups = BuildSpatialGroups(currentLODClusters);

                if (EditorUtility.DisplayCancelableProgressBar("Building Nanite DAG", $"Simplifying LOD {lod} (Groups: {groups.Count})", (float)lod / maxLODLevels))
                {
                    EditorUtility.ClearProgressBar();
                    DestroyImmediate(naniteMesh);
                    Debug.LogWarning("Nanite build canceled by user.");
                    return;
                }

                // Pre-compute group bounding spheres (thread-safe, read-only after this point)
                var groupBoundsArray = new (Vector3 center, float radius)[groups.Count];
                for (int i = 0; i < groups.Count; i++)
                    groupBoundsArray[i] = ComputeGroupBounds(groups[i]);

                BuildCluster[][] newClustersArray = new BuildCluster[groups.Count][];
                Vector3[] threadSafeVertices = vertices;
                float capturedCurrentError = currentError;
                float capturedBaseError = baseErrorIncrement;
                
                int lockBorderFailedGroups = 0;
                int relaxedSucceededGroups = 0;
                int totalSimplifiedGroups = 0;
                int capturedLod = lod;

                try
                {
                    System.Threading.Tasks.Parallel.For(0, groups.Count, i =>
                    {
                        List<BuildCluster> group = groups[i];
                        var (gbCenter, gbRadius) = groupBoundsArray[i];

                        List<int> groupIndices = new List<int>();
                        foreach (var c in group) groupIndices.AddRange(c.indices);

                        uint targetCount = (uint)(groupIndices.Count * simplifyRatio);
                        targetCount = targetCount - (targetCount % 3);

                        if (targetCount < 3)
                        {
                            foreach (var c in group)
                            {
                                c.parentLodError = 9999999f;
                                c.parentBoundsCenter = gbCenter;
                                c.parentBoundsRadius = gbRadius;
                            }
                            newClustersArray[i] = new BuildCluster[0];
                            return;
                        }

                        int[] simplifiedIndices = new int[groupIndices.Count];
                        float resultError = 0;
                        UIntPtr simpCount = MeshOptimizer.Simplify(
                            simplifiedIndices,
                            groupIndices.ToArray(),
                            threadSafeVertices,
                            targetCount,
                            MeshOptimizer.meshopt_SimplifyLockBorder,
                            out resultError);

                        float minNextError = (capturedCurrentError < 0.0001f)
                            ? capturedBaseError
                            : capturedCurrentError * 1.8f;
                        float groupError = Mathf.Max(resultError, minNextError);

                        int finalSimpCount = (int)simpCount.ToUInt32();
                        bool lockBorderFailed = finalSimpCount == 0 || finalSimpCount == groupIndices.Count;

                        if (lockBorderFailed)
                        {
                            float relaxedError = 0;
                            UIntPtr relaxedCount = MeshOptimizer.Simplify(
                                simplifiedIndices,
                                groupIndices.ToArray(),
                                threadSafeVertices,
                                targetCount,
                                0u,
                                out relaxedError);

                            int relaxedFinalCount = (int)relaxedCount.ToUInt32();
                            if (relaxedFinalCount > 0 && relaxedFinalCount < groupIndices.Count)
                            {
                                resultError = relaxedError;
                                finalSimpCount = relaxedFinalCount;
                                groupError = Mathf.Max(resultError, minNextError);
                                System.Threading.Interlocked.Increment(ref relaxedSucceededGroups);
                            }
                        }

                        if (finalSimpCount == 0 || finalSimpCount == groupIndices.Count)
                        {
                            if (lockBorderFailed)
                                System.Threading.Interlocked.Increment(ref lockBorderFailedGroups);
                            foreach (var c in group)
                            {
                                c.parentLodError = 9999999f;
                                c.parentBoundsCenter = gbCenter;
                                c.parentBoundsRadius = gbRadius;
                            }
                            newClustersArray[i] = new BuildCluster[0];
                            return;
                        }

                        System.Threading.Interlocked.Increment(ref totalSimplifiedGroups);

                        int[] actualSimpIndices = new int[finalSimpCount];
                        Array.Copy(simplifiedIndices, actualSimpIndices, finalSimpCount);

                        // KEY FIX: child.parentBounds = group bounds, child.parentLodError = groupError
                        foreach (var c in group)
                        {
                            c.parentLodError = groupError;
                            c.parentBoundsCenter = gbCenter;
                            c.parentBoundsRadius = gbRadius;
                        }

                        List<BuildCluster> newClusters = ClusterizeIndices(
                            actualSimpIndices, threadSafeVertices,
                            maxVertices, maxTriangles, groupError, capturedLod);

                        // KEY FIX: parent.lodBounds = same group bounds → guarantees clean handoff
                        foreach (var nc in newClusters)
                        {
                            nc.lodBoundsCenter = gbCenter;
                            nc.lodBoundsRadius = gbRadius;
                        }

                        newClustersArray[i] = newClusters.ToArray();
                    });
                }
                catch (Exception e)
                {
                    EditorUtility.ClearProgressBar();
                    DestroyImmediate(naniteMesh);
                    Debug.LogError($"Nanite build failed while simplifying LOD {lod}: {e.Message}");
                    return;
                }

                foreach (var arr in newClustersArray)
                {
                    nextLODClusters.AddRange(arr);
                }

                Debug.Log($"[NaniteBuilder] LOD{lod}: groups={groups.Count}, simplified={totalSimplifiedGroups}, lockBorderFailed={lockBorderFailedGroups}, relaxedSucceeded={relaxedSucceededGroups}, nextClusters={nextLODClusters.Count}");

                if (nextLODClusters.Count >= currentLODClusters.Count)
                {
                    Debug.LogWarning($"[NaniteBuilder] LOD{lod} failed to reduce cluster count (Current: {currentLODClusters.Count}, Next: {nextLODClusters.Count}). Stopping DAG build.");
                    allClusters.AddRange(nextLODClusters);
                    currentLODClusters = nextLODClusters;
                    totalLodLevels++;
                    break;
                }

                allClusters.AddRange(nextLODClusters);
                currentLODClusters = nextLODClusters;
                totalLodLevels++;
                if (nextLODClusters.Count > 0)
                {
                    float minErr = float.MaxValue, maxErr = 0;
                    foreach (var c in nextLODClusters)
                    {
                        maxErr = Mathf.Max(maxErr, c.lodError);
                        minErr = Mathf.Min(minErr, c.lodError);
                    }
                    currentError = maxErr;
                    lodLevelInfo.Add($"  LOD{lod}: {nextLODClusters.Count} clusters, error=[{minErr:F6}, {maxErr:F6}]");
                }
                else
                    break;
            }

            EditorUtility.ClearProgressBar();

            foreach (var c in currentLODClusters)
            {
                c.parentLodError = 9999999f; 
            }

            // Flatten into final arrays
            List<int> finalIndicesList = new List<int>();
            List<NaniteCluster> finalClustersList = new List<NaniteCluster>();

            foreach (var bc in allClusters)
            {
                NaniteCluster nc = new NaniteCluster();
                nc.indexStart = finalIndicesList.Count;
                nc.indexCount = bc.indices.Length;
                nc.boundsCenter = bc.boundsCenter;
                nc.boundsRadius = bc.boundsRadius;
                nc.lodError = bc.lodError;
                nc.parentLodError = bc.parentLodError;
                nc.lodBoundsCenter = bc.lodBoundsCenter;
                nc.lodBoundsRadius = bc.lodBoundsRadius;
                nc.parentBoundsCenter = bc.parentBoundsCenter;
                nc.parentBoundsRadius = bc.parentBoundsRadius;
                nc.lodLevel = bc.lodLevel;

                finalIndicesList.AddRange(bc.indices);
                finalClustersList.Add(nc);
            }

            naniteMesh.indices = finalIndicesList.ToArray();
            naniteMesh.clusters = finalClustersList.ToArray();
            naniteMesh.lodLevelCount = totalLodLevels;

            AssetDatabase.CreateAsset(naniteMesh, path);
            AssetDatabase.SaveAssets();

            Resources.UnloadUnusedAssets();
            GC.Collect();

            string lodSummary = string.Join("\n", lodLevelInfo);
            Debug.Log($"<color=green>Nanite DAG Built!</color> Mesh diagonal={meshDiagonal:F4}, Levels={totalLodLevels}, Total Clusters={naniteMesh.clusters.Length}\n{lodSummary}");
        }

        private List<BuildCluster> ClusterizeIndices(int[] indices, Vector3[] vertices, uint maxVertices, uint maxTriangles, float error, int lodLevel)
        {
            List<BuildCluster> result = new List<BuildCluster>();
            if (indices.Length == 0) return result;

            UIntPtr maxMeshletsPtr = MeshOptimizer.meshopt_buildMeshletsBound((UIntPtr)indices.Length, (UIntPtr)maxVertices, (UIntPtr)maxTriangles);
            int maxMeshlets = (int)maxMeshletsPtr.ToUInt64();

            meshopt_Meshlet[] meshlets = new meshopt_Meshlet[maxMeshlets];
            uint[] meshlet_vertices = new uint[maxMeshlets * (int)maxVertices];
            byte[] meshlet_triangles = new byte[maxMeshlets * (int)maxTriangles * 3];

            UIntPtr meshletCountPtr = MeshOptimizer.BuildMeshlets(meshlets, meshlet_vertices, meshlet_triangles, indices, vertices, maxVertices, maxTriangles, 0.0f);
            int meshletCount = (int)meshletCountPtr.ToUInt64();

            for (int m = 0; m < meshletCount; m++)
            {
                meshopt_Meshlet meshlet = meshlets[m];
                int[] clusterIndices = new int[meshlet.triangle_count * 3];
                
                Vector3 minBounds = Vector3.one * float.MaxValue;
                Vector3 maxBounds = Vector3.one * float.MinValue;

                for (int i = 0; i < clusterIndices.Length; i++)
                {
                    byte local_idx = meshlet_triangles[meshlet.triangle_offset + i];
                    uint global_idx = meshlet_vertices[meshlet.vertex_offset + local_idx];
                    clusterIndices[i] = (int)global_idx;

                    Vector3 v = vertices[global_idx];
                    minBounds = Vector3.Min(minBounds, v);
                    maxBounds = Vector3.Max(maxBounds, v);
                }

                Vector3 center = (minBounds + maxBounds) * 0.5f;
                float radius = Vector3.Distance(center, maxBounds); 

                result.Add(new BuildCluster
                {
                    indices = clusterIndices,
                    boundsCenter = center,
                    boundsRadius = radius,
                    lodError = error,
                    parentLodError = 0,
                    lodBoundsCenter = center,
                    lodBoundsRadius = radius,
                    parentBoundsCenter = center,
                    parentBoundsRadius = radius,
                    lodLevel = lodLevel
                });
            }
            return result;
        }

        private static List<List<BuildCluster>> BuildSpatialGroups(List<BuildCluster> currentLODClusters)
        {
            List<List<BuildCluster>> groups = new List<List<BuildCluster>>();
            bool[] isGrouped = new bool[currentLODClusters.Count];
            int groupedCount = 0;
            int seedIdx = 0;

            while (groupedCount < currentLODClusters.Count)
            {
                while (seedIdx < currentLODClusters.Count && isGrouped[seedIdx])
                    seedIdx++;

                if (seedIdx >= currentLODClusters.Count)
                    break;

                BuildCluster seed = currentLODClusters[seedIdx];
                isGrouped[seedIdx] = true;
                groupedCount++;

                List<BuildCluster> group = new List<BuildCluster> { seed };

                while (group.Count < 4 && groupedCount < currentLODClusters.Count)
                {
                    float minD = float.MaxValue;
                    int bestIdx = -1;

                    for (int i = 0; i < currentLODClusters.Count; i++)
                    {
                        if (isGrouped[i]) continue;

                        float d = (seed.boundsCenter - currentLODClusters[i].boundsCenter).sqrMagnitude;
                        if (d < minD)
                        {
                            minD = d;
                            bestIdx = i;
                        }
                    }

                    if (bestIdx == -1) break;

                    isGrouped[bestIdx] = true;
                    groupedCount++;
                    group.Add(currentLODClusters[bestIdx]);
                }

                groups.Add(group);
            }

            return groups;
        }
    }
}
