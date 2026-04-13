using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nanite
{
    public enum NaniteDrawMode
    {
        DebugClusters,
        Shaded
    }

    [ExecuteAlways]
    public class NaniteRenderer : MonoBehaviour
    {
        public NaniteMesh naniteMesh;

        [Header("Rendering")]
        public NaniteDrawMode drawMode = NaniteDrawMode.DebugClusters;
        [Tooltip("Per-cluster visualization — shader Nanite/DebugClusters")]
        public Material material;
        [Tooltip("Normal-looking mesh — shader Nanite/Shaded (same GPU buffer path)")]
        public Material shadedMaterial;
        [Tooltip("Press in Play Mode to flip between Debug and Shaded (KeyCode.None disables)")]
        public KeyCode toggleDrawModeKey = KeyCode.F3;

        public ComputeShader cullingCompute;
        public Camera cullingCamera;

        [Header("LOD Settings")]
        [Range(1f, 500f)]
        public float errorThreshold = 1.0f;

        [Header("Performance")]
        [Tooltip("When the mesh world AABB is outside the culling camera frustum, skip GPU cluster culling and draw calls for this object. " +
                 "Without this, every cluster thread still runs each frame even if no triangles are rendered.")]
        public bool skipGpuWhenOutsideFrustum = true;

        private GraphicsBuffer vertexBuffer;
        private GraphicsBuffer indexBuffer;
        private GraphicsBuffer clusterBuffer;
        private GraphicsBuffer visibleClusterBuffer;
        private GraphicsBuffer visibleTriangleSumBuffer;
        private GraphicsBuffer argsBuffer;

        private NaniteMesh currentMesh;
        private int currentVertexCount;
        private int currentIndexCount;
        private int currentClusterCount;
        private MaterialPropertyBlock materialPropertyBlock;
        private readonly Plane[] frustumPlaneCache = new Plane[6];
        private readonly Vector4[] frustumPlanes = new Vector4[6];
        private int cullingKernel = -1;
        private Bounds localMeshBounds;
        private uint _indirectIndexCountPerInstance;
        private uint[] _argsScratch;

        private static readonly uint[] s_zeroUInt1 = { 0u };

        /// <summary>Last GPU readback: number of visible clusters this frame (async, typically 1+ frames behind).</summary>
        public int LastVisibleClusters { get; private set; }

        /// <summary>Last GPU readback: sum of triangles in visible clusters (async).</summary>
        public int LastVisibleTriangles { get; private set; }

        /// <summary>Total clusters in the baked asset (all LOD levels).</summary>
        public int CachedTotalClusters { get; private set; }

        /// <summary>Triangles if every LOD0 cluster were drawn (high-detail baseline for reduction %).</summary>
        public int CachedLod0TriangleBaseline { get; private set; }

        /// <summary>LOD0 cluster count.</summary>
        public int CachedLod0ClusterCount { get; private set; }

        void OnEnable()
        {
            materialPropertyBlock ??= new MaterialPropertyBlock();
            CheckAndInitBuffers();
        }

        void OnDisable()
        {
            ReleaseBuffers();
        }

        void CheckAndInitBuffers()
        {
            if (naniteMesh == null ||
                naniteMesh.vertices == null || naniteMesh.vertices.Length == 0 ||
                naniteMesh.indices == null || naniteMesh.indices.Length == 0 ||
                naniteMesh.clusters == null || naniteMesh.clusters.Length == 0)
            {
                ReleaseBuffers();
                return;
            }

            bool needsReinit = currentMesh != naniteMesh ||
                               currentVertexCount != naniteMesh.vertices.Length ||
                               currentIndexCount != naniteMesh.indices.Length ||
                               currentClusterCount != naniteMesh.clusters.Length ||
                               vertexBuffer == null || !vertexBuffer.IsValid() ||
                               indexBuffer == null || !indexBuffer.IsValid() ||
                               clusterBuffer == null || !clusterBuffer.IsValid() ||
                               visibleClusterBuffer == null || !visibleClusterBuffer.IsValid() ||
                               visibleTriangleSumBuffer == null || !visibleTriangleSumBuffer.IsValid() ||
                               argsBuffer == null || !argsBuffer.IsValid();

            if (!needsReinit) return;

            ReleaseBuffers();
            InitializeBuffers();
            currentMesh = naniteMesh;
            currentVertexCount = naniteMesh.vertices.Length;
            currentIndexCount = naniteMesh.indices.Length;
            currentClusterCount = naniteMesh.clusters.Length;
        }

        void InitializeBuffers()
        {
            if (cullingCompute != null && cullingKernel < 0)
            {
                if (cullingCompute.HasKernel("CSMain"))
                {
                    cullingKernel = cullingCompute.FindKernel("CSMain");
                }
            }

            if (cullingKernel < 0)
            {
                Debug.LogError("[Nanite] Could not find 'CSMain' kernel in culling compute shader.");
                return;
            }

            vertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, naniteMesh.vertices.Length, Marshal.SizeOf<NaniteVertex>());
            vertexBuffer.SetData(naniteMesh.vertices);

            indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, naniteMesh.indices.Length, sizeof(int));
            indexBuffer.SetData(naniteMesh.indices);

            clusterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, naniteMesh.clusters.Length, Marshal.SizeOf<NaniteCluster>());
            clusterBuffer.SetData(naniteMesh.clusters);

            visibleClusterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Append, naniteMesh.clusters.Length, sizeof(uint));

            visibleTriangleSumBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

            argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, 5 * sizeof(uint));
            uint[] args = new uint[5];
            int maxClusterIndexCount = 0;
            for (int i = 0; i < naniteMesh.clusters.Length; i++)
                maxClusterIndexCount = Mathf.Max(maxClusterIndexCount, naniteMesh.clusters[i].indexCount);

            args[0] = (uint)Mathf.Max(3, maxClusterIndexCount);
            args[1] = 0;
            args[2] = 0;
            args[3] = 0;
            args[4] = 0;
            argsBuffer.SetData(args);
            _indirectIndexCountPerInstance = args[0];

            localMeshBounds = new Bounds(naniteMesh.vertices[0].position, Vector3.zero);
            for (int i = 1; i < naniteMesh.vertices.Length; i++)
                localMeshBounds.Encapsulate(naniteMesh.vertices[i].position);

            RebuildCachedBaselines();
        }

        void RebuildCachedBaselines()
        {
            CachedTotalClusters = naniteMesh.clusters.Length;
            CachedLod0TriangleBaseline = 0;
            CachedLod0ClusterCount = 0;
            for (int i = 0; i < naniteMesh.clusters.Length; i++)
            {
                NaniteCluster c = naniteMesh.clusters[i];
                if (c.lodLevel != 0)
                    continue;
                CachedLod0ClusterCount++;
                CachedLod0TriangleBaseline += Mathf.Max(0, c.indexCount) / 3;
            }
        }

        void ReleaseBuffers()
        {
            vertexBuffer?.Release(); vertexBuffer = null;
            indexBuffer?.Release(); indexBuffer = null;
            clusterBuffer?.Release(); clusterBuffer = null;
            visibleClusterBuffer?.Release(); visibleClusterBuffer = null;
            visibleTriangleSumBuffer?.Release(); visibleTriangleSumBuffer = null;
            argsBuffer?.Release(); argsBuffer = null;

            currentMesh = null;
            currentVertexCount = 0;
            currentIndexCount = 0;
            currentClusterCount = 0;
            cullingKernel = -1;
            _indirectIndexCountPerInstance = 0;
        }

        void WriteIndirectArgsZeroInstances()
        {
            if (_argsScratch == null || _argsScratch.Length != 5)
                _argsScratch = new uint[5];

            _argsScratch[0] = _indirectIndexCountPerInstance;
            _argsScratch[1] = 0;
            _argsScratch[2] = 0;
            _argsScratch[3] = 0;
            _argsScratch[4] = 0;
            argsBuffer.SetData(_argsScratch);
        }

        void UpdateFrustumPlanes(Camera cam)
        {
            GeometryUtility.CalculateFrustumPlanes(cam, frustumPlaneCache);
            for (int i = 0; i < 6; i++)
            {
                frustumPlanes[i] = new Vector4(
                    frustumPlaneCache[i].normal.x,
                    frustumPlaneCache[i].normal.y,
                    frustumPlaneCache[i].normal.z,
                    frustumPlaneCache[i].distance);
            }
        }

        Camera ResolveCamera()
        {
            if (cullingCamera != null) return cullingCamera;
            if (Camera.main != null) return Camera.main;
            return null;
        }

        Bounds GetWorldBounds()
        {
            // Tight world-axis AABB for rotated/scaled transforms (8 corners of local bounds).
            Vector3 c = localMeshBounds.center;
            Vector3 e = localMeshBounds.extents;
            Bounds w = new Bounds(transform.TransformPoint(new Vector3(c.x - e.x, c.y - e.y, c.z - e.z)), Vector3.zero);
            w.Encapsulate(transform.TransformPoint(new Vector3(c.x + e.x, c.y - e.y, c.z - e.z)));
            w.Encapsulate(transform.TransformPoint(new Vector3(c.x - e.x, c.y + e.y, c.z - e.z)));
            w.Encapsulate(transform.TransformPoint(new Vector3(c.x + e.x, c.y + e.y, c.z - e.z)));
            w.Encapsulate(transform.TransformPoint(new Vector3(c.x - e.x, c.y - e.y, c.z + e.z)));
            w.Encapsulate(transform.TransformPoint(new Vector3(c.x + e.x, c.y - e.y, c.z + e.z)));
            w.Encapsulate(transform.TransformPoint(new Vector3(c.x - e.x, c.y + e.y, c.z + e.z)));
            w.Encapsulate(transform.TransformPoint(new Vector3(c.x + e.x, c.y + e.y, c.z + e.z)));
            w.Expand(0.05f);
            return w;
        }

        Material ResolveDrawMaterial()
        {
            if (drawMode == NaniteDrawMode.Shaded && shadedMaterial != null)
                return shadedMaterial;
            return material;
        }

        static Vector3 ResolveMainLightDirectionWS()
        {
            Light sun = RenderSettings.sun;
            if (sun != null && sun.isActiveAndEnabled && sun.type == LightType.Directional)
                return (-sun.transform.forward).normalized;

            Light[] lights = FindObjectsOfType<Light>();
            for (int i = 0; i < lights.Length; i++)
            {
                Light l = lights[i];
                if (l == null || !l.isActiveAndEnabled || l.type != LightType.Directional)
                    continue;
                return (-l.transform.forward).normalized;
            }

            return new Vector3(0.35f, 1f, 0.25f).normalized;
        }

        void Update()
        {
            if (toggleDrawModeKey != KeyCode.None && Application.isPlaying && Input.GetKeyDown(toggleDrawModeKey))
            {
                drawMode = drawMode == NaniteDrawMode.DebugClusters
                    ? NaniteDrawMode.Shaded
                    : NaniteDrawMode.DebugClusters;
            }

            Material drawMaterial = ResolveDrawMaterial();
            if (naniteMesh == null || drawMaterial == null || cullingCompute == null)
            {
                ReleaseBuffers();
                return;
            }

            CheckAndInitBuffers();

            if (argsBuffer == null || !argsBuffer.IsValid() ||
                clusterBuffer == null || !clusterBuffer.IsValid() ||
                visibleClusterBuffer == null || !visibleClusterBuffer.IsValid() ||
                visibleTriangleSumBuffer == null || !visibleTriangleSumBuffer.IsValid() ||
                vertexBuffer == null || !vertexBuffer.IsValid() ||
                indexBuffer == null || !indexBuffer.IsValid())
                return;

            Camera cam = ResolveCamera();
            if (cam == null) return;

            int kernel = cullingKernel;
            if (kernel < 0)
            {
                if (cullingCompute.HasKernel("CSMain"))
                {
                    kernel = cullingCompute.FindKernel("CSMain");
                    cullingKernel = kernel;
                }
            }

            if (kernel < 0) return;

            UpdateFrustumPlanes(cam);

            if (skipGpuWhenOutsideFrustum)
            {
                Bounds worldBounds = GetWorldBounds();
                if (!GeometryUtility.TestPlanesAABB(frustumPlaneCache, worldBounds))
                {
                    WriteIndirectArgsZeroInstances();
                    if (Application.isPlaying)
                    {
                        LastVisibleClusters = 0;
                        LastVisibleTriangles = 0;
                    }

                    return;
                }
            }

            cullingCompute.SetVectorArray("_FrustumPlanes", frustumPlanes);
            cullingCompute.SetMatrix("_NaniteObjectToWorld", transform.localToWorldMatrix);
            cullingCompute.SetVector("_CameraPositionWS", cam.transform.position);
            cullingCompute.SetFloat("_ErrorThreshold", errorThreshold);
            cullingCompute.SetInt("_IsOrthographic", cam.orthographic ? 1 : 0);

            float screenFactor = cam.orthographic
                ? cam.pixelHeight / (2.0f * Mathf.Max(cam.orthographicSize, 0.0001f))
                : cam.pixelHeight / (2.0f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));

            cullingCompute.SetFloat("_ScreenFactor", screenFactor);
            cullingCompute.SetInt("_ClusterCount", naniteMesh.clusters.Length);
            cullingCompute.SetBuffer(kernel, "_ClusterBuffer", clusterBuffer);
            cullingCompute.SetBuffer(kernel, "_VisibleClusterBuffer", visibleClusterBuffer);
            cullingCompute.SetBuffer(kernel, "_VisibleTriangleSum", visibleTriangleSumBuffer);

            int threadGroups = Mathf.CeilToInt(naniteMesh.clusters.Length / 64f);

            if (kernel >= 0 && threadGroups > 0)
            {
                visibleClusterBuffer.SetCounterValue(0);
                visibleTriangleSumBuffer.SetData(s_zeroUInt1);
                cullingCompute.Dispatch(kernel, threadGroups, 1, 1);
                GraphicsBuffer.CopyCount(visibleClusterBuffer, argsBuffer, 4);

                if (Application.isPlaying && SystemInfo.supportsAsyncGPUReadback)
                {
                    // GraphicsBuffer overload: Request(src, sizeInBytes, srcOffsetBytes, callback) — not (offset, size).
                    const int argsByteCount = 5 * sizeof(uint);
                    AsyncGPUReadback.Request(argsBuffer, argsByteCount, 0, OnArgsReadback);
                    AsyncGPUReadback.Request(visibleTriangleSumBuffer, OnTriangleSumReadback);
                }
            }

            RenderParams rp = new RenderParams(drawMaterial)
            {
                worldBounds = GetWorldBounds(),
                matProps = materialPropertyBlock
            };

            materialPropertyBlock.SetMatrix("_NaniteObjectToWorld", transform.localToWorldMatrix);
            materialPropertyBlock.SetVector("_NaniteLightDirWS", ResolveMainLightDirectionWS());
            materialPropertyBlock.SetBuffer("_VertexBuffer", vertexBuffer);
            materialPropertyBlock.SetBuffer("_IndexBuffer", indexBuffer);
            materialPropertyBlock.SetBuffer("_ClusterBuffer", clusterBuffer);
            materialPropertyBlock.SetBuffer("_VisibleClusterBuffer", visibleClusterBuffer);

            Graphics.RenderPrimitivesIndirect(rp, MeshTopology.Triangles, argsBuffer, 1);
        }

        void OnArgsReadback(AsyncGPUReadbackRequest req)
        {
            if (req.hasError)
                return;
            using NativeArray<uint> data = req.GetData<uint>();
            if (data.Length > 1)
                LastVisibleClusters = (int)data[1];
        }

        void OnTriangleSumReadback(AsyncGPUReadbackRequest req)
        {
            if (req.hasError)
                return;
            using NativeArray<uint> data = req.GetData<uint>();
            if (data.Length > 0)
                LastVisibleTriangles = (int)data[0];
        }
    }
}
