using UnityEngine;

namespace Nanite
{
    [System.Serializable]
    public struct NaniteVertex
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector2 uv;
    }

    [System.Serializable]
    public struct NaniteCluster
    {
        public int indexStart;
        public int indexCount;

        public Vector3 boundsCenter;   // cluster geometry bounds (frustum culling)
        public float boundsRadius;

        public float lodError;
        public float parentLodError;

        // Shared group bounding sphere for watertight LOD handoff.
        // Both child and parent in the same DAG group reference the same sphere,
        // guaranteeing identical distance → identical projected error → clean transition.
        public Vector3 lodBoundsCenter;
        public float lodBoundsRadius;
        public Vector3 parentBoundsCenter;
        public float parentBoundsRadius;

        public int lodLevel;
    }

    [CreateAssetMenu(fileName = "NewNaniteMesh", menuName = "Nanite/Nanite Mesh")]
    public class NaniteMesh : ScriptableObject
    {
        public int trianglesPerCluster;
        public int lodLevelCount;
        public NaniteVertex[] vertices;
        public int[] indices;
        public NaniteCluster[] clusters;
    }
}
