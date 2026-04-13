using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Nanite.Editor
{
    [StructLayout(LayoutKind.Sequential)]
    public struct meshopt_Meshlet
    {
        public uint vertex_offset;
        public uint triangle_offset;
        public uint vertex_count;
        public uint triangle_count;
    }

    public static class MeshOptimizer
    {
        // 库名称，Unity 会自动在 Plugins 文件夹下寻找对应的 DLL (meshoptimizer.dll)
        const string LibName = "meshoptimizer";

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr meshopt_buildMeshletsBound(
            UIntPtr index_count, 
            UIntPtr max_vertices, 
            UIntPtr max_triangles);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr meshopt_buildMeshlets(
            IntPtr destination,
            IntPtr meshlet_vertices,
            IntPtr meshlet_triangles,
            IntPtr indices,
            UIntPtr index_count,
            IntPtr vertex_positions,
            UIntPtr vertex_count,
            UIntPtr vertex_positions_stride,
            UIntPtr max_vertices,
            UIntPtr max_triangles,
            float cone_weight);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr meshopt_simplify(
            IntPtr destination,
            IntPtr indices,
            UIntPtr index_count,
            IntPtr vertex_positions,
            UIntPtr vertex_count,
            UIntPtr vertex_positions_stride,
            UIntPtr target_index_count,
            float target_error,
            uint options,
            out float result_error);

        // 简化时锁定边界顶点不被移动或移除，这是避免 LOD 产生裂缝的核心！
        public const uint meshopt_SimplifyLockBorder = 1 << 0;

        public static UIntPtr Simplify(
            int[] destination,
            int[] indices,
            Vector3[] vertex_positions,
            uint target_index_count,
            uint options,
            out float result_error)
        {
            GCHandle destHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);
            GCHandle idxHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
            GCHandle vpHandle = GCHandle.Alloc(vertex_positions, GCHandleType.Pinned);

            try
            {
                return meshopt_simplify(
                    destHandle.AddrOfPinnedObject(),
                    idxHandle.AddrOfPinnedObject(),
                    (UIntPtr)indices.Length,
                    vpHandle.AddrOfPinnedObject(),
                    (UIntPtr)vertex_positions.Length,
                    (UIntPtr)12, // sizeof(Vector3)
                    (UIntPtr)target_index_count,
                    float.MaxValue, // 不限制 target_error，强制简化到指定的索引数量
                    options,
                    out result_error);
            }
            finally
            {
                destHandle.Free();
                idxHandle.Free();
                vpHandle.Free();
            }
        }

        // 为了避免在项目里必须开启 Unsafe Code，我们使用 GCHandle 将数组固定在内存中传递给 C++
        public static UIntPtr BuildMeshlets(
            meshopt_Meshlet[] destination,
            uint[] meshlet_vertices,
            byte[] meshlet_triangles,
            int[] indices,
            Vector3[] vertex_positions,
            uint max_vertices,
            uint max_triangles,
            float cone_weight)
        {
            GCHandle destHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);
            GCHandle mvHandle = GCHandle.Alloc(meshlet_vertices, GCHandleType.Pinned);
            GCHandle mtHandle = GCHandle.Alloc(meshlet_triangles, GCHandleType.Pinned);
            GCHandle idxHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
            GCHandle vpHandle = GCHandle.Alloc(vertex_positions, GCHandleType.Pinned);

            try
            {
                return meshopt_buildMeshlets(
                    destHandle.AddrOfPinnedObject(),
                    mvHandle.AddrOfPinnedObject(),
                    mtHandle.AddrOfPinnedObject(),
                    idxHandle.AddrOfPinnedObject(),
                    (UIntPtr)indices.Length,
                    vpHandle.AddrOfPinnedObject(),
                    (UIntPtr)vertex_positions.Length,
                    (UIntPtr)12, // sizeof(Vector3) = 3个float * 4字节
                    (UIntPtr)max_vertices,
                    (UIntPtr)max_triangles,
                    cone_weight);
            }
            finally
            {
                destHandle.Free();
                mvHandle.Free();
                mtHandle.Free();
                idxHandle.Free();
                vpHandle.Free();
            }
        }
    }
}
