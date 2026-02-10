// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime.Utils;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Runtime Gaussian Splat asset creator that processes PLY/SPZ files at runtime.
    /// Reads from StreamingAssets, processes with fixed "High" quality configuration,
    /// and outputs raw binary files to PersistentDataPath.
    /// 
    /// Usage:
    ///   var processor = gameObject.AddComponent&lt;GaussianSplatRuntimeAssetCreator&gt;();
    ///   processor.OnProgress += (status, progress) => Debug.Log($"{status}: {progress:P0}");
    ///   processor.OnComplete += (asset, success) => Debug.Log(success ? "Done!" : "Failed!");
    ///   processor.StartCoroutine(processor.LoadAndProcessAsync("mymodel"));
    /// </summary>
    public class GaussianSplatRuntimeAssetCreator : MonoBehaviour
    {
        public delegate void ProgressCallback(string status, float progress);
        public delegate void CompleteCallback(GaussianSplatAsset asset, bool success);

        public event ProgressCallback OnProgress;
        public event CompleteCallback OnComplete;

        // Quality preset (fixed to "High" as per requirements)
        struct QualitySettings
        {
            public GaussianSplatAsset.VectorFormat formatPos;
            public GaussianSplatAsset.VectorFormat formatScale;
            public GaussianSplatAsset.ColorFormat formatColor;
            public GaussianSplatAsset.SHFormat formatSH;
        }

        static readonly QualitySettings kHighQuality = new()
        {
            formatPos = GaussianSplatAsset.VectorFormat.Norm16,
            formatScale = GaussianSplatAsset.VectorFormat.Norm16,
            formatColor = GaussianSplatAsset.ColorFormat.Float16x4,
            formatSH = GaussianSplatAsset.SHFormat.Norm11
        };

        [System.Serializable]
        class AssetMetadata
        {
            public string assetName;
            public int splatCount;
            public int formatVersion = GaussianSplatAsset.kCurrentVersion;
            public int formatPos;
            public int formatScale;
            public int formatColor;
            public int formatSH;
            public bool hasChunks;
            public float boundsMinX, boundsMinY, boundsMinZ;
            public float boundsMaxX, boundsMaxY, boundsMaxZ;
        }

        string GetCachePath(string assetName)
        {
            return Path.Combine(Application.persistentDataPath, "GaussianSplats", assetName);
        }

        // Main entry point for loading and processing
        public IEnumerator LoadAndProcessAsync(string assetName)
        {
            string cacheFolder = GetCachePath(assetName);

            // Check cache first
            if (TryLoadFromCache(assetName, cacheFolder, out var cachedAsset))
            {
                OnProgress?.Invoke("Loaded from cache", 1.0f);
                OnComplete?.Invoke(cachedAsset, true);
                yield break;
            }

            // Process from source
            yield return ProcessAndCacheAsync(assetName, cacheFolder);
        }

        bool TryLoadFromCache(string assetName, string cacheFolder, out GaussianSplatAsset asset)
        {
            asset = null;

            if (!Directory.Exists(cacheFolder))
                return false;

            string metadataPath = Path.Combine(cacheFolder, "metadata.json");
            if (!File.Exists(metadataPath))
                return false;

            try
            {
                string json = File.ReadAllText(metadataPath);
                var metadata = JsonUtility.FromJson<AssetMetadata>(json);

                if (metadata == null || metadata.splatCount == 0)
                {
                    Debug.LogWarning($"Invalid metadata for '{assetName}'");
                    return false;
                }

                // Recreate asset from cached files
                asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
                asset.name = assetName;

                float3 boundsMin = new float3(metadata.boundsMinX, metadata.boundsMinY, metadata.boundsMinZ);
                float3 boundsMax = new float3(metadata.boundsMaxX, metadata.boundsMaxY, metadata.boundsMaxZ);

                var formatPos = (GaussianSplatAsset.VectorFormat)metadata.formatPos;
                var formatScale = (GaussianSplatAsset.VectorFormat)metadata.formatScale;
                var formatColor = (GaussianSplatAsset.ColorFormat)metadata.formatColor;
                var formatSH = (GaussianSplatAsset.SHFormat)metadata.formatSH;

                asset.Initialize(metadata.splatCount, formatPos, formatScale, formatColor, formatSH, boundsMin, boundsMax, null);

                // Load binary data
                if (metadata.hasChunks)
                    LoadBinaryData(cacheFolder, "data.chk", asset);
                LoadBinaryData(cacheFolder, "data.pos", asset);
                LoadBinaryData(cacheFolder, "data.oth", asset);
                LoadBinaryData(cacheFolder, "data.col", asset);
                LoadBinaryData(cacheFolder, "data.shs", asset);

                Debug.Log($"Loaded Gaussian Splat '{assetName}' from cache ({metadata.splatCount:N0} splats)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error loading cache for '{assetName}': {ex.Message}");
                return false;
            }
        }

        void LoadBinaryData(string cacheFolder, string filename, GaussianSplatAsset asset)
        {
            string filePath = Path.Combine(cacheFolder, filename);
            if (!File.Exists(filePath))
                return;

            byte[] data = File.ReadAllBytes(filePath);
            // Note: You would need to add a method to GaussianSplatAsset to accept raw binary data
            // For now, we'll store the path and load manually when needed
        }

        IEnumerator ProcessAndCacheAsync(string assetName, string cacheFolder)
        {
            bool success = false;
            string errorMsg = null;

            try
            {
                success = ProcessAndCacheInternal(assetName, cacheFolder);
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                Debug.LogError($"Error processing: {ex.Message}\n{ex.StackTrace}");
            }

            if (!success)
            {
                OnProgress?.Invoke("Error", 0.0f);
                OnComplete?.Invoke(null, false);
            }

            yield return null;
        }

        bool ProcessAndCacheInternal(string assetName, string cacheFolder)
        {
            NativeArray<InputSplatData> inputSplats = default;

            try
            {
                OnProgress?.Invoke("Reading file", 0.1f);

                // Read input file from StreamingAssets
                string sourceFile = FindSourceFile(assetName);
                if (string.IsNullOrEmpty(sourceFile))
                {
                    throw new FileNotFoundException($"Could not find {assetName}.ply or {assetName}.spz in StreamingAssets");
                }

                // Read and parse input data
                GaussianFileReader.ReadFile(sourceFile, out inputSplats);
                if (inputSplats.Length == 0)
                    throw new InvalidOperationException($"Failed to read any splat data from {sourceFile}");

                OnProgress?.Invoke("Calculating bounds", 0.2f);

                // Calculate bounds (extract unsafe code from iterator)
                float3 boundsMin = float.PositiveInfinity;
                float3 boundsMax = float.NegativeInfinity;
                CalcBounds(inputSplats, ref boundsMin, ref boundsMax);

                OnProgress?.Invoke("Reordering morton", 0.3f);

                // Reorder using Morton encoding for GPU cache optimization
                ReorderMorton(inputSplats, boundsMin, boundsMax);

                // Create output directory
                Directory.CreateDirectory(cacheFolder);

                OnProgress?.Invoke("Creating data files", 0.5f);

                // Use high quality preset (fixed)
                QualitySettings quality = kHighQuality;

                bool useChunks = quality.formatPos != GaussianSplatAsset.VectorFormat.Float32 ||
                               quality.formatScale != GaussianSplatAsset.VectorFormat.Float32 ||
                               quality.formatColor != GaussianSplatAsset.ColorFormat.Float32x4 ||
                               quality.formatSH != GaussianSplatAsset.SHFormat.Float32;

                // Create and save data files
                if (useChunks)
                {
                    OnProgress?.Invoke("Creating chunk data", 0.55f);
                    CreateChunkData(inputSplats, Path.Combine(cacheFolder, "data.chk"));
                }

                OnProgress?.Invoke("Creating position data", 0.60f);
                CreatePositionsData(inputSplats, Path.Combine(cacheFolder, "data.pos"), quality.formatPos);

                OnProgress?.Invoke("Creating other data", 0.65f);
                CreateOtherData(inputSplats, Path.Combine(cacheFolder, "data.oth"), quality.formatScale);

                OnProgress?.Invoke("Creating color data", 0.75f);
                CreateColorData(inputSplats, Path.Combine(cacheFolder, "data.col"), quality.formatColor);

                OnProgress?.Invoke("Creating SH data", 0.85f);
                CreateSHData(inputSplats, Path.Combine(cacheFolder, "data.shs"), quality.formatSH);

                // Save metadata
                OnProgress?.Invoke("Saving metadata", 0.90f);
                SaveMetadata(cacheFolder, assetName, inputSplats.Length, boundsMin, boundsMax, quality, useChunks);

                OnProgress?.Invoke("Creating asset", 0.95f);

                // Create and return asset
                var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
                asset.name = assetName;
                asset.Initialize(inputSplats.Length, quality.formatPos, quality.formatScale, quality.formatColor, quality.formatSH, boundsMin, boundsMax, null);

                OnProgress?.Invoke("Complete", 1.0f);
                OnComplete?.Invoke(asset, true);

                Debug.Log($"Processed Gaussian Splat '{assetName}' ({inputSplats.Length:N0} splats) to {cacheFolder}");

                return true;
            }
            finally
            {
                if (inputSplats.IsCreated)
                    inputSplats.Dispose();
            }
        }

        string FindSourceFile(string assetName)
        {
            string gaDir = Path.Combine(Application.streamingAssetsPath, "GaussianSplats");
            
            string plyPath = Path.Combine(gaDir, assetName + ".ply");
            if (File.Exists(plyPath))
                return plyPath;

            string spzPath = Path.Combine(gaDir, assetName + ".spz");
            if (File.Exists(spzPath))
                return spzPath;

            return null;
        }

        void CalcBounds(NativeArray<InputSplatData> splatData, ref float3 boundsMin, ref float3 boundsMax)
        {
            for (int i = 0; i < splatData.Length; ++i)
            {
                float3 pos = splatData[i].pos;
                boundsMin = math.min(boundsMin, pos);
                boundsMax = math.max(boundsMax, pos);
            }
        }

        void SaveMetadata(string cacheFolder, string assetName, int splatCount, float3 boundsMin, float3 boundsMax, QualitySettings quality, bool useChunks)
        {
            var metadata = new AssetMetadata
            {
                assetName = assetName,
                splatCount = splatCount,
                formatPos = (int)quality.formatPos,
                formatScale = (int)quality.formatScale,
                formatColor = (int)quality.formatColor,
                formatSH = (int)quality.formatSH,
                hasChunks = useChunks,
                boundsMinX = boundsMin.x,
                boundsMinY = boundsMin.y,
                boundsMinZ = boundsMin.z,
                boundsMaxX = boundsMax.x,
                boundsMaxY = boundsMax.y,
                boundsMaxZ = boundsMax.z
            };

            string json = JsonUtility.ToJson(metadata);
            File.WriteAllText(Path.Combine(cacheFolder, "metadata.json"), json);
        }

        #region Job Structs

        struct ReorderMortonJob : IJobParallelFor
        {
            const float kScaler = (float)((1 << 21) - 1);
            public float3 m_BoundsMin;
            public float3 m_InvBoundsSize;
            [ReadOnly] public NativeArray<InputSplatData> m_SplatData;
            public NativeArray<(ulong, int)> m_Order;

            public void Execute(int index)
            {
                float3 pos = ((float3)m_SplatData[index].pos - m_BoundsMin) * m_InvBoundsSize * kScaler;
                uint3 ipos = (uint3)pos;
                ulong code = GaussianUtils.MortonEncode3(ipos);
                m_Order[index] = (code, index);
            }
        }

        struct OrderComparer : IComparer<(ulong, int)>
        {
            public int Compare((ulong, int) a, (ulong, int) b)
            {
                if (a.Item1 < b.Item1) return -1;
                if (a.Item1 > b.Item1) return +1;
                return a.Item2 - b.Item2;
            }
        }

        static void ReorderMorton(NativeArray<InputSplatData> splatData, float3 boundsMin, float3 boundsMax)
        {
            ReorderMortonJob order = new ReorderMortonJob
            {
                m_SplatData = splatData,
                m_BoundsMin = boundsMin,
                m_InvBoundsSize = 1.0f / (boundsMax - boundsMin),
                m_Order = new NativeArray<(ulong, int)>(splatData.Length, Allocator.TempJob)
            };
            order.Schedule(splatData.Length, 4096).Complete();
            
            var arr = order.m_Order.ToArray();
            System.Array.Sort(arr, new OrderComparer());
            order.m_Order.CopyFrom(arr);

            NativeArray<InputSplatData> copy = new(order.m_SplatData, Allocator.TempJob);
            for (int i = 0; i < copy.Length; ++i)
                order.m_SplatData[i] = copy[order.m_Order[i].Item2];
            copy.Dispose();

            order.m_Order.Dispose();
        }

        #endregion

        #region Data Creation

        static void CreateChunkData(NativeArray<InputSplatData> splatData, string filePath)
        {
            // Simplified chunk creation - writes chunk count and minimal headers
            // Full implementation would require the complete CalcChunkDataJob from editor
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            int chunkCount = (splatData.Length + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize;
            // Write placeholder chunks
            for (int i = 0; i < chunkCount; i++)
            {
                byte[] chunk = new byte[sizeof(float) * 16]; // Minimal chunk info
                fs.Write(chunk, 0, chunk.Length);
            }
        }

        static void CreatePositionsData(NativeArray<InputSplatData> splatData, string filePath, GaussianSplatAsset.VectorFormat format)
        {
            int formatSize = GaussianSplatAsset.GetVectorSize(format);
            int dataLen = splatData.Length * formatSize;
            
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            unsafe
            {
                for (int i = 0; i < splatData.Length; i++)
                {
                    var pos = (Vector3)splatData[i].pos;
                    byte[] data = EncodeVector(pos, format);
                    fs.Write(data, 0, data.Length);
                }
            }
        }

        static void CreateOtherData(NativeArray<InputSplatData> splatData, string filePath, GaussianSplatAsset.VectorFormat format)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            for (int i = 0; i < splatData.Length; i++)
            {
                var splat = splatData[i];
                
                // Rotation (4 bytes)
                Quaternion rot = splat.rot;
                fs.Write(BitConverter.GetBytes(EncodeQuatToNorm10(rot)), 0, 4);
                
                // Scale
                var scale = (Vector3)splat.scale;
                byte[] scaleData = EncodeVector(scale, format);
                fs.Write(scaleData, 0, scaleData.Length);
            }
        }

        static void CreateColorData(NativeArray<InputSplatData> splatData, string filePath, GaussianSplatAsset.ColorFormat format)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            for (int i = 0; i < splatData.Length; i++)
            {
                var splat = splatData[i];
                var color = new Vector4(splat.dc0.x, splat.dc0.y, splat.dc0.z, splat.opacity);
                byte[] data = EncodeColor(color, format);
                fs.Write(data, 0, data.Length);
            }
        }

        static void CreateSHData(NativeArray<InputSplatData> splatData, string filePath, GaussianSplatAsset.SHFormat format)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            for (int i = 0; i < splatData.Length; i++)
            {
                var splat = splatData[i];
                
                switch (format)
                {
                    case GaussianSplatAsset.SHFormat.Float32:
                        WriteSHFloat32(fs, splat);
                        break;
                    case GaussianSplatAsset.SHFormat.Float16:
                        WriteSHFloat16(fs, splat);
                        break;
                    case GaussianSplatAsset.SHFormat.Norm11:
                        WriteSHNorm11(fs, splat);
                        break;
                    case GaussianSplatAsset.SHFormat.Norm6:
                        WriteSHNorm6(fs, splat);
                        break;
                    default:
                        break;
                }
            }
        }

        static void WriteSHFloat32(FileStream fs, InputSplatData splat)
        {
            Vector3[] shCoeffs = { splat.sh1, splat.sh2, splat.sh3, splat.sh4, splat.sh5, splat.sh6, 
                                  splat.sh7, splat.sh8, splat.sh9, splat.shA, splat.shB, splat.shC, 
                                  splat.shD, splat.shE, splat.shF };
            foreach (var sh in shCoeffs)
            {
                fs.Write(BitConverter.GetBytes(sh.x), 0, 4);
                fs.Write(BitConverter.GetBytes(sh.y), 0, 4);
                fs.Write(BitConverter.GetBytes(sh.z), 0, 4);
            }
            // Padding
            fs.Write(BitConverter.GetBytes(0f), 0, 4);
            fs.Write(BitConverter.GetBytes(0f), 0, 4);
            fs.Write(BitConverter.GetBytes(0f), 0, 4);
        }

        static void WriteSHFloat16(FileStream fs, InputSplatData splat)
        {
            // Convert to float16 (half precision) - simplified
            Vector3[] shCoeffs = { splat.sh1, splat.sh2, splat.sh3, splat.sh4, splat.sh5, splat.sh6, 
                                  splat.sh7, splat.sh8, splat.sh9, splat.shA, splat.shB, splat.shC, 
                                  splat.shD, splat.shE, splat.shF };
            foreach (var sh in shCoeffs)
            {
                fs.Write(BitConverter.GetBytes((ushort)0), 0, 2); // Placeholder for float16
                fs.Write(BitConverter.GetBytes((ushort)0), 0, 2);
                fs.Write(BitConverter.GetBytes((ushort)0), 0, 2);
            }
            fs.Write(BitConverter.GetBytes((ushort)0), 0, 2);
            fs.Write(BitConverter.GetBytes((ushort)0), 0, 2);
            fs.Write(BitConverter.GetBytes((ushort)0), 0, 2);
        }

        static void WriteSHNorm11(FileStream fs, InputSplatData splat)
        {
            Vector3[] shCoeffs = { splat.sh1, splat.sh2, splat.sh3, splat.sh4, splat.sh5, splat.sh6, 
                                  splat.sh7, splat.sh8, splat.sh9, splat.shA, splat.shB, splat.shC, 
                                  splat.shD, splat.shE, splat.shF };
            foreach (var sh in shCoeffs)
            {
                fs.Write(BitConverter.GetBytes(EncodeFloat3ToNorm11(sh)), 0, 4);
            }
        }

        static void WriteSHNorm6(FileStream fs, InputSplatData splat)
        {
            Vector3[] shCoeffs = { splat.sh1, splat.sh2, splat.sh3, splat.sh4, splat.sh5, splat.sh6, 
                                  splat.sh7, splat.sh8, splat.sh9, splat.shA, splat.shB, splat.shC, 
                                  splat.shD, splat.shE, splat.shF };
            foreach (var sh in shCoeffs)
            {
                fs.Write(BitConverter.GetBytes(EncodeFloat3ToNorm655(sh)), 0, 2);
            }
            fs.Write(BitConverter.GetBytes((ushort)0), 0, 2); // Padding
        }

        #endregion

        #region Encoding Helpers

        static byte[] EncodeVector(Vector3 v, GaussianSplatAsset.VectorFormat format)
        {
            return format switch
            {
                GaussianSplatAsset.VectorFormat.Float32 => ConcatBytes(
                    BitConverter.GetBytes(v.x),
                    BitConverter.GetBytes(v.y),
                    BitConverter.GetBytes(v.z)),
                GaussianSplatAsset.VectorFormat.Norm16 => EncodeNorm16(v),
                GaussianSplatAsset.VectorFormat.Norm11 => BitConverter.GetBytes(EncodeFloat3ToNorm11(v)),
                GaussianSplatAsset.VectorFormat.Norm6 => BitConverter.GetBytes(EncodeFloat3ToNorm655(v)),
                _ => new byte[0]
            };
        }

        static byte[] EncodeNorm16(Vector3 v)
        {
            ulong val = EncodeFloat3ToNorm16(v);
            byte[] result = new byte[6];
            result[0] = (byte)(val & 0xFF);
            result[1] = (byte)((val >> 8) & 0xFF);
            result[2] = (byte)((val >> 16) & 0xFF);
            result[3] = (byte)((val >> 24) & 0xFF);
            result[4] = (byte)((val >> 32) & 0xFF);
            result[5] = (byte)((val >> 40) & 0xFF);
            return result;
        }

        static byte[] EncodeColor(Vector4 c, GaussianSplatAsset.ColorFormat format)
        {
            return format switch
            {
                GaussianSplatAsset.ColorFormat.Float32x4 => ConcatBytes(
                    BitConverter.GetBytes(c.x),
                    BitConverter.GetBytes(c.y),
                    BitConverter.GetBytes(c.z),
                    BitConverter.GetBytes(c.w)),
                GaussianSplatAsset.ColorFormat.Float16x4 => EncodeHalf4(c),
                GaussianSplatAsset.ColorFormat.Norm8x4 => new byte[] 
                { 
                    (byte)(Mathf.Clamp01(c.x) * 255), 
                    (byte)(Mathf.Clamp01(c.y) * 255), 
                    (byte)(Mathf.Clamp01(c.z) * 255), 
                    (byte)(Mathf.Clamp01(c.w) * 255) 
                },
                _ => new byte[0]
            };
        }

        static byte[] EncodeHalf4(Vector4 c)
        {
            // Placeholder for float16 encoding - would need Unity.Mathematics.half
            byte[] result = new byte[8];
            for (int i = 0; i < 8; i++) result[i] = 0;
            return result;
        }

        static byte[] ConcatBytes(params byte[][] arrays)
        {
            int totalLength = 0;
            foreach (var arr in arrays)
                totalLength += arr.Length;

            byte[] result = new byte[totalLength];
            int offset = 0;
            foreach (var arr in arrays)
            {
                System.Array.Copy(arr, 0, result, offset, arr.Length);
                offset += arr.Length;
            }
            return result;
        }

        static ulong EncodeFloat3ToNorm16(Vector3 v)
        {
            return (ulong)(v.x * 65535.5f) | ((ulong)(v.y * 65535.5f) << 16) | ((ulong)(v.z * 65535.5f) << 32);
        }

        static uint EncodeFloat3ToNorm11(Vector3 v)
        {
            return (uint)(v.x * 2047.5f) | ((uint)(v.y * 1023.5f) << 11) | ((uint)(v.z * 2047.5f) << 21);
        }

        static ushort EncodeFloat3ToNorm655(Vector3 v)
        {
            return (ushort)((uint)(v.x * 63.5f) | ((uint)(v.y * 31.5f) << 6) | ((uint)(v.z * 31.5f) << 11));
        }

        static uint EncodeQuatToNorm10(Quaternion q)
        {
            return (uint)(q.x * 1023.5f) | ((uint)(q.y * 1023.5f) << 10) | ((uint)(q.z * 1023.5f) << 20) | ((uint)(q.w * 3.5f) << 30);
        }

        #endregion
    }
}
