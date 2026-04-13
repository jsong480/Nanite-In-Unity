using UnityEngine;

namespace Nanite
{
    /// <summary>
    /// IMGUI overlay: FPS, GPU-visible cluster/triangle counts, and comparison to LOD0 baseline / optional reference mesh.
    /// </summary>
    [ExecuteAlways]
    public class NaniteStatsOverlay : MonoBehaviour
    {
        public NaniteRenderer naniteRenderer;

        [Tooltip("Optional: MeshFilter using the original opaque mesh (same asset as build source) for raw triangle count comparison.")]
        public MeshFilter compareMeshFilter;

        [Tooltip("Show the stats box.")]
        public bool showOverlay = true;

        [Tooltip("Screen-space position of the panel (pixels, from top-left).")]
        public Vector2 panelPosition = new Vector2(12f, 12f);

        [Tooltip("Frames used to average FPS and frame time. Higher = smoother, slower to react.")]
        [Range(12, 120)]
        public int fpsSmoothingFrames = 48;

        GUIStyle _boxStyle;
        GUIStyle _labelStyle;
        float _avgFps;
        float _avgFrameMs;
        float[] _dtRing;
        int _dtRingWrite;
        int _dtRingFilled;
        float _dtRingSum;

        static Texture2D s_panelBackground;

        void OnEnable()
        {
            ResetFpsRing();
        }

        void ResetFpsRing()
        {
            _dtRing = null;
            _dtRingWrite = 0;
            _dtRingFilled = 0;
            _dtRingSum = 0f;
            _avgFps = 0f;
            _avgFrameMs = 0f;
        }

        void Update()
        {
            int cap = Mathf.Clamp(fpsSmoothingFrames, 12, 120);
            if (_dtRing == null || _dtRing.Length != cap)
            {
                ResetFpsRing();
                _dtRing = new float[cap];
            }

            float dt = Time.unscaledDeltaTime;
            if (_dtRingFilled == cap)
                _dtRingSum -= _dtRing[_dtRingWrite];
            else
                _dtRingFilled++;

            _dtRing[_dtRingWrite] = dt;
            _dtRingSum += dt;
            _dtRingWrite = (_dtRingWrite + 1) % cap;

            float avgDt = _dtRingSum / _dtRingFilled;
            _avgFrameMs = avgDt * 1000f;
            _avgFps = avgDt > 1e-6f ? 1f / avgDt : 0f;
        }

        void OnGUI()
        {
            if (!showOverlay)
                return;

            EnsureStyles();

            const float width = 380f;
            const float height = 300f;
            GUILayout.BeginArea(new Rect(panelPosition.x, panelPosition.y, width, height), _boxStyle);
            GUILayout.Label("Nanite stats", _labelStyle);

            if (naniteRenderer == null)
            {
                GUILayout.Label("Assign NaniteRenderer.", _labelStyle);
                GUILayout.EndArea();
                return;
            }

            int avgN = _dtRing != null ? _dtRing.Length : Mathf.Clamp(fpsSmoothingFrames, 12, 120);
            GUILayout.Label($"FPS: {_avgFps:0}   Frame: {_avgFrameMs:0.0} ms  ({avgN}-frame avg)", _labelStyle);
            GUILayout.Label($"Draw mode: {naniteRenderer.drawMode}", _labelStyle);
            GUILayout.Label($"Error threshold: {naniteRenderer.errorThreshold:0.##}", _labelStyle);
            GUILayout.Space(4f);

            int totalClusters = naniteRenderer.CachedTotalClusters;
            int lod0Tris = naniteRenderer.CachedLod0TriangleBaseline;
            int lod0Clusters = naniteRenderer.CachedLod0ClusterCount;

            GUILayout.Label($"Baked clusters (all LOD): {totalClusters}", _labelStyle);
            GUILayout.Label($"LOD0 baseline: {lod0Tris:N0} tris ({lod0Clusters} clusters)", _labelStyle);

            if (!Application.isPlaying)
            {
                GUILayout.Space(4f);
                GUILayout.Label("GPU counters update in Play Mode.", _labelStyle);
            }
            else
            {
                int visClusters = naniteRenderer.LastVisibleClusters;
                int visTris = naniteRenderer.LastVisibleTriangles;

                GUILayout.Space(4f);
                GUILayout.Label($"Visible clusters: {visClusters} / {totalClusters}", _labelStyle);
                GUILayout.Label($"Submitted triangles: {visTris:N0}", _labelStyle);

                if (lod0Tris > 0)
                {
                    float ratio = visTris / (float)lod0Tris;
                    float reduction = (1f - ratio) * 100f;
                    GUILayout.Label(
                        $"vs LOD0 baseline: {ratio * 100f:0.0}% tris ({reduction:0.0}% fewer)",
                        _labelStyle);
                }
            }

            if (compareMeshFilter != null && compareMeshFilter.sharedMesh != null)
            {
                Mesh m = compareMeshFilter.sharedMesh;
                int refTris = m.triangles != null ? m.triangles.Length / 3 : 0;
                GUILayout.Space(4f);
                GUILayout.Label($"Reference mesh ({m.name}): {refTris:N0} tris", _labelStyle);
                if (Application.isPlaying && lod0Tris > 0 && refTris > 0)
                {
                    GUILayout.Label(
                        $"LOD0 tris vs reference: {lod0Tris / (float)refTris * 100f:0.0}% (meshlets differ from raw mesh)",
                        _labelStyle);
                }
            }

            GUILayout.EndArea();
        }

        void EnsureStyles()
        {
            if (_boxStyle != null)
                return;

            if (s_panelBackground == null)
            {
                s_panelBackground = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                s_panelBackground.hideFlags = HideFlags.HideAndDontSave;
                var c = new Color(0f, 0f, 0f, 0.72f);
                s_panelBackground.SetPixels(new[] { c, c, c, c });
                s_panelBackground.Apply(false, true);
            }

            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 8, 8),
                normal = { background = s_panelBackground }
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                richText = true,
                normal = { textColor = Color.white }
            };
        }
    }
}
