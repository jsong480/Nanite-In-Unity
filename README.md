# Nanite-Style Cluster LOD (Unity)

A small Unity prototype inspired by Nanite-style rendering: offline **meshlet clustering** and multi-level **LOD DAG** (via [meshoptimizer](https://github.com/zeux/meshoptimizer)), runtime **compute frustum culling** + **screen-space error LOD**, **append buffer** + **indexed indirect** draw, and **`GraphicsBuffer`** vertex/index/cluster path.

**Unity:** 2022.3 LTS (see `ProjectSettings/ProjectVersion.txt`).

**Demo video:** [YouTube — project walkthrough / demo](https://youtu.be/Gwo7bt9FCT0)

## Requirements

- **Windows:** `Assets/Plugins/meshoptimizer.dll` is used by the editor build pipeline (`MeshOptimizerInterop`). Keep this file under version control when cloning.
- **Other platforms:** Build meshoptimizer as a native plugin for your target, place it under `Assets/Plugins/`, and match the P/Invoke entry names used in `Assets/Nanite/Scripts/Editor/MeshOptimizerInterop.cs`.

## Quick start

1. Open the project in Unity **2022.3.x**.
2. Provide a readable mesh (enable **Read/Write** on the mesh import settings).
3. Menu: **Nanite → Builder** → choose source mesh → **Build Nanite Asset** to generate a `NaniteMesh` asset.
4. Assign that asset on a `NaniteRenderer` in the scene, hook up **Culling Compute**, materials, and camera as in `Assets/Scenes/SampleScene.unity` (or `Assets/Nanite/Prefabs`).

## Project layout

| Path | Role |
|------|------|
| `Assets/Nanite/Scripts/Editor/NaniteBuilder.cs` | Offline DAG build (clusters, simplification, bounds). |
| `Assets/Nanite/Scripts/Runtime/NaniteRenderer.cs` | Compute dispatch, buffers, indirect draw. |
| `Assets/Nanite/Shaders/NaniteCulling.compute` | Frustum cull + LOD selection. |
| `Assets/Nanite/Shaders/NaniteShaded.shader` / `NaniteDebug.shader` | Shaded vs cluster debug. |

## License

Project code: add your license here. **meshoptimizer** is MIT — see upstream [meshoptimizer](https://github.com/zeux/meshoptimizer).
