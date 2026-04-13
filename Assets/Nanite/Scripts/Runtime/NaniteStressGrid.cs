using System.Collections.Generic;
using UnityEngine;

namespace Nanite
{
    /// <summary>
    /// Spawns many <see cref="NaniteRenderer"/> instances in a grid so GPU/CPU cost scales and LOD changes with distance are visible in FPS.
    /// Single meshes are often too light to move uncapped FPS (e.g. 900+).
    /// </summary>
    public class NaniteStressGrid : MonoBehaviour
    {
        [Tooltip("Object with NaniteRenderer + materials/compute assigned. Should not include another NaniteStressGrid.")]
        public GameObject prototype;

        [Tooltip("Instances along X.")]
        [Range(1, 64)]
        public int columns = 12;

        [Tooltip("Instances along Z.")]
        [Range(1, 64)]
        public int rows = 12;

        [Tooltip("Spacing between instance centers (local space).")]
        public float spacing = 4f;

        [Tooltip("Hide the prototype after spawning so only the grid draws.")]
        public bool deactivatePrototypeAfterSpawn = true;

        [Tooltip("Small random Y rotation per instance so lighting isn’t perfectly uniform.")]
        public bool randomYaw;

        readonly List<GameObject> _spawned = new List<GameObject>();

        void Start()
        {
            if (!Application.isPlaying)
                return;

            Rebuild();
        }

        [ContextMenu("Rebuild Grid (Play Mode only)")]
        public void Rebuild()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[NaniteStressGrid] Rebuild only runs in Play Mode.");
                return;
            }

            ClearSpawned();

            if (prototype == null)
            {
                Debug.LogWarning("[NaniteStressGrid] Assign a prototype GameObject with NaniteRenderer.");
                return;
            }

            if (prototype.GetComponent<NaniteRenderer>() == null)
            {
                Debug.LogWarning("[NaniteStressGrid] Prototype needs a NaniteRenderer.");
                return;
            }

            float ox = -(columns - 1) * spacing * 0.5f;
            float oz = -(rows - 1) * spacing * 0.5f;

            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < columns; x++)
                {
                    GameObject inst = Instantiate(prototype, transform);
                    inst.transform.localPosition = new Vector3(ox + x * spacing, 0f, oz + z * spacing);
                    inst.transform.localRotation = Quaternion.identity;
                    inst.transform.localScale = Vector3.one;
                    if (randomYaw)
                        inst.transform.Rotate(0f, Random.Range(0f, 360f), 0f);

                    inst.name = $"{prototype.name}_{x}_{z}";
                    StripNestedStressGrids(inst);
                    _spawned.Add(inst);
                }
            }

            if (deactivatePrototypeAfterSpawn)
                prototype.SetActive(false);

            int total = columns * rows;
            Debug.Log($"[NaniteStressGrid] Spawned {total} instances ({columns}×{rows}, spacing {spacing}).");
        }

        static void StripNestedStressGrids(GameObject root)
        {
            var stresses = root.GetComponentsInChildren<NaniteStressGrid>(true);
            for (int i = 0; i < stresses.Length; i++)
                Destroy(stresses[i]);
        }

        void ClearSpawned()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                    Destroy(_spawned[i]);
            }

            _spawned.Clear();

            if (prototype != null)
                prototype.SetActive(true);
        }

        void OnDestroy()
        {
            if (Application.isPlaying)
                ClearSpawned();
        }
    }
}
