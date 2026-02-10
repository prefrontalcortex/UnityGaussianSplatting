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
using UnityEngine.Networking;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Runtime Gaussian Splat asset creator that processes PLY/SPZ files at runtime.
    /// Supports multiple file sources with independent module integration.
    /// 
    /// File Organization:
    /// - StreamingAssets/GaussianSplats/: Manually added source files (PLY/SPZ)
    /// - PersistentDataPath/GaussianSplats/sources/: Additional source files (for Marble AI module, etc)
    /// - PersistentDataPath/GaussianSplats/{assetName}/: Processed cache for each asset
    /// 
    /// Integration Pattern:
    /// Other modules (e.g., Marble AI) can register their own source paths without tight coupling:
    ///   gaussianSplatCreator.RegisterSourcePath("/path/to/marble/ai/models");
    /// 
    /// Usage:
    ///   var processor = gameObject.AddComponent&lt;GaussianSplatRuntimeAssetCreator&gt;();
    ///   processor.OnProgress += (status, progress) => Debug.Log($"{status}: {progress:P0}");
    ///   processor.OnComplete += (asset, success) => Debug.Log(success ? "Done!" : "Failed!");
    ///   
    ///   // Load from StreamingAssets or source paths
    ///   processor.StartCoroutine(processor.LoadAndProcessAsync("mymodel"));
    ///   
    ///   // Register custom path from another module (Marble AI, etc)
    ///   processor.RegisterSourcePath("/path/to/ai/module/models");
    ///   
    ///   // List all available assets
    ///   var assets = processor.ListAvailableAssets();
    ///   
    ///   // Process a file from custom path
    ///   processor.StartCoroutine(processor.ProcessFileAsync("mymodel", "/custom/path/mymodel.ply"));
    /// </summary>
    // Simple wrapper for binary data at runtime (mimics TextAsset)
    public class BinaryDataAsset : ScriptableObject
    {
        [SerializeField] byte[] m_Data;
        public byte[] bytes => m_Data;
        public int dataSize => m_Data?.Length ?? 0;
        
        public void SetBytes(byte[] data) => m_Data = data;
        
        // Mimic TextAsset.GetData<T>() API for compatibility with renderer
        public T[] GetData<T>() where T : unmanaged
        {
            if (m_Data == null || m_Data.Length == 0)
                return new T[0];
            
            unsafe
            {
                int elementSize = sizeof(T);
                int elementCount = m_Data.Length / elementSize;
                T[] result = new T[elementCount];
                
                fixed (byte* srcPtr = m_Data)
                fixed (T* dstPtr = result)
                {
                    Buffer.MemoryCopy(srcPtr, dstPtr, m_Data.Length, m_Data.Length);
                }
                
                return result;
            }
        }
    }

    public class GaussianSplatRuntimeAssetCreator : MonoBehaviour
    {
        public delegate void ProgressCallback(string status, float progress);
        public delegate void CompleteCallback(GaussianSplatAsset asset, bool success);

        public event ProgressCallback OnProgress;
        public event CompleteCallback OnComplete;

        // Quality presets
        public enum QualityPreset
        {
            VeryHigh,
            High,
            Medium,
            Low
        }

        struct QualitySettings
        {
            public GaussianSplatAsset.VectorFormat formatPos;
            public GaussianSplatAsset.VectorFormat formatScale;
            public GaussianSplatAsset.ColorFormat formatColor;
            public GaussianSplatAsset.SHFormat formatSH;
        }

        static readonly QualitySettings kVeryHighQuality = new()
        {
            formatPos = GaussianSplatAsset.VectorFormat.Float32,
            formatScale = GaussianSplatAsset.VectorFormat.Float32,
            formatColor = GaussianSplatAsset.ColorFormat.Float32x4,
            formatSH = GaussianSplatAsset.SHFormat.Float32
        };

        static readonly QualitySettings kHighQuality = new()
        {
            formatPos = GaussianSplatAsset.VectorFormat.Norm16,
            formatScale = GaussianSplatAsset.VectorFormat.Norm16,
            formatColor = GaussianSplatAsset.ColorFormat.Float16x4,
            formatSH = GaussianSplatAsset.SHFormat.Norm11
        };

        static readonly QualitySettings kMediumQuality = new()
        {
            formatPos = GaussianSplatAsset.VectorFormat.Norm11,
            formatScale = GaussianSplatAsset.VectorFormat.Norm11,
            formatColor = GaussianSplatAsset.ColorFormat.Norm8x4,
            formatSH = GaussianSplatAsset.SHFormat.Norm6
        };

        static readonly QualitySettings kLowQuality = new()
        {
            formatPos = GaussianSplatAsset.VectorFormat.Norm6,
            formatScale = GaussianSplatAsset.VectorFormat.Norm6,
            formatColor = GaussianSplatAsset.ColorFormat.Norm8x4,
            formatSH = GaussianSplatAsset.SHFormat.Norm6
        };

        [SerializeField] QualityPreset m_QualityPreset = QualityPreset.High;

        public enum AssetSource
        {
            StreamingAssets,
            Downloaded,
            Manual
        }

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
            public int source = (int)AssetSource.StreamingAssets;
            public string sourceFilename;
            public long sourceFileSize;
            public string sourceUrl;
            public long createdTimestamp;
        }

        public struct AssetInfo
        {
            public string name;
            public AssetSource source;
            public string sourceFile;
            public long fileSize;
            public bool isCached;
        }

        QualitySettings GetQualitySettings()
        {
            return m_QualityPreset switch
            {
                QualityPreset.VeryHigh => kVeryHighQuality,
                QualityPreset.High => kHighQuality,
                QualityPreset.Medium => kMediumQuality,
                QualityPreset.Low => kLowQuality,
                _ => kHighQuality
            };
        }

        /// <summary>Get or set the quality preset for asset processing</summary>
        public QualityPreset QualityLevel
        {
            get => m_QualityPreset;
            set => m_QualityPreset = value;
        }

        /// <summary>List all available Gaussian Splat assets from all sources</summary>
        public List<AssetInfo> ListAvailableAssets()
        {
            var assets = new Dictionary<string, AssetInfo>();

            // Discover StreamingAssets
            string streamingPath = Path.Combine(Application.streamingAssetsPath, "GaussianSplats");
            if (Directory.Exists(streamingPath))
            {
                foreach (var file in Directory.GetFiles(streamingPath, "*.ply").Concat(Directory.GetFiles(streamingPath, "*.spz")))
                {
                    string assetName = Path.GetFileNameWithoutExtension(file);
                    if (!assets.ContainsKey(assetName))
                    {
                        assets[assetName] = new AssetInfo
                        {
                            name = assetName,
                            source = AssetSource.StreamingAssets,
                            sourceFile = file,
                            fileSize = new FileInfo(file).Length,
                            isCached = IsCached(assetName)
                        };
                    }
                }
            }

            // Discover files from other sources (custom paths, Marble AI module, etc)
            string otherSourcesPath = GetOtherSourcesPath();
            if (Directory.Exists(otherSourcesPath))
            {
                foreach (var file in Directory.GetFiles(otherSourcesPath, "*.ply").Concat(Directory.GetFiles(otherSourcesPath, "*.spz")))
                {
                    string assetName = Path.GetFileNameWithoutExtension(file);
                    if (!assets.ContainsKey(assetName))  // StreamingAssets takes priority
                    {
                        assets[assetName] = new AssetInfo
                        {
                            name = assetName,
                            source = AssetSource.Manual,
                            sourceFile = file,
                            fileSize = new FileInfo(file).Length,
                            isCached = IsCached(assetName)
                        };
                    }
                }
            }

            // Check custom registered source paths
            foreach (var customPath in m_CustomSourcePaths)
            {
                if (Directory.Exists(customPath))
                {
                    foreach (var file in Directory.GetFiles(customPath, "*.ply").Concat(Directory.GetFiles(customPath, "*.spz")))
                    {
                        string assetName = Path.GetFileNameWithoutExtension(file);
                        if (!assets.ContainsKey(assetName))  // StreamingAssets takes priority
                        {
                            assets[assetName] = new AssetInfo
                            {
                                name = assetName,
                                source = AssetSource.Manual,
                                sourceFile = file,
                                fileSize = new FileInfo(file).Length,
                                isCached = IsCached(assetName)
                            };
                        }
                    }
                }
            }

            return assets.Values.ToList();
        }

        /// <summary>Get all available source paths for file discovery</summary>
        public List<string> GetSourcePaths()
        {
            var paths = new List<string>
            {
                Path.Combine(Application.streamingAssetsPath, "GaussianSplats"),
                GetOtherSourcesPath()
            };
            paths.AddRange(m_CustomSourcePaths);
            return paths;
        }

        /// <summary>Register a custom source path for file discovery (e.g., from Marble AI module)</summary>
        public void RegisterSourcePath(string path)
        {
            if (!Directory.Exists(path))
            {
                Debug.LogWarning($"Source path does not exist: {path}");
                return;
            }
            
            if (!m_CustomSourcePaths.Contains(path))
            {
                m_CustomSourcePaths.Add(path);
                Debug.Log($"Registered source path: {path}");
            }
        }

        static List<string> m_CustomSourcePaths = new();

        /// <summary>Process a file from a custom path (e.g., from another module)</summary>
        public IEnumerator ProcessFileAsync(string assetName, string filePath)
        {
            if (!File.Exists(filePath))
            {
                Debug.LogError($"File not found: {filePath}");
                OnComplete?.Invoke(null, false);
                yield break;
            }

            string cacheFolder = GetCachePath(assetName);
            bool success = ProcessAndCacheInternal(assetName, cacheFolder, filePath);

            if (success)
            {
                OnProgress?.Invoke("Complete", 1.0f);
                OnComplete?.Invoke(LoadCachedAsset(assetName, cacheFolder), true);
            }
            else
            {
                OnComplete?.Invoke(null, false);
            }
            
            yield break;
        }

        /// <summary>Delete an asset and all associated files</summary>
        public bool DeleteAsset(string assetName, bool deleteSource = false)
        {
            try
            {
                // Delete cache
                string cacheFolder = GetCachePath(assetName);
                if (Directory.Exists(cacheFolder))
                {
                    Directory.Delete(cacheFolder, true);
                    Debug.Log($"Deleted cache for {assetName}");
                }

                // Optionally delete source file
                if (deleteSource)
                {
                    string sourcePath = GetOtherSourcesPath();
                    foreach (var ext in new[] { ".ply", ".spz" })
                    {
                        string file = Path.Combine(sourcePath, assetName + ext);
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                            Debug.Log($"Deleted source file {file}");
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error deleting {assetName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Get metadata for a specific asset</summary>
        public AssetInfo? GetAssetInfo(string assetName)
        {
            var allAssets = ListAvailableAssets();
            return allAssets.FirstOrDefault(a => a.name == assetName);
        }

        string GetOtherSourcesPath()
        {
            return Path.Combine(Application.persistentDataPath, "GaussianSplats", "sources");
        }

        string GetCachePath(string assetName)
        {
            return Path.Combine(Application.persistentDataPath, "GaussianSplats", assetName);
        }

        bool IsCached(string assetName)
        {
            string cacheFolder = GetCachePath(assetName);
            return File.Exists(Path.Combine(cacheFolder, "metadata.json"));
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

                // Load binary data from disk
                LoadBinaryData(cacheFolder, asset);

                Debug.Log($"Loaded Gaussian Splat '{assetName}' from cache ({metadata.splatCount:N0} splats)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error loading cache for '{assetName}': {ex.Message}");
                return false;
            }
        }

        GaussianSplatAsset LoadCachedAsset(string assetName, string cacheFolder)
        {
            return TryLoadFromCache(assetName, cacheFolder, out var asset) ? asset : null;
        }

        void LoadBinaryData(string cacheFolder, GaussianSplatAsset asset)
        {
            try
            {
                // Load all binary data files from disk as BinaryDataAssets
                UnityEngine.Object posData = LoadBinaryDataAsset(Path.Combine(cacheFolder, "data.pos"));
                UnityEngine.Object otherData = LoadBinaryDataAsset(Path.Combine(cacheFolder, "data.oth"));
                UnityEngine.Object colorData = LoadBinaryDataAsset(Path.Combine(cacheFolder, "data.col"));
                UnityEngine.Object shData = LoadBinaryDataAsset(Path.Combine(cacheFolder, "data.shs"));
                UnityEngine.Object chunkData = null;
                
                // Chunk data is optional
                string chunkPath = Path.Combine(cacheFolder, "data.chk");
                if (File.Exists(chunkPath))
                    chunkData = LoadBinaryDataAsset(chunkPath);

                // Set the data on the asset
                asset.SetAssetFiles(chunkData, posData, otherData, colorData, shData);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error loading binary data: {ex.Message}");
            }
        }

        BinaryDataAsset LoadBinaryDataAsset(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            byte[] data = File.ReadAllBytes(filePath);
            BinaryDataAsset asset = ScriptableObject.CreateInstance<BinaryDataAsset>();
            asset.SetBytes(data);
            return asset;
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

        bool ProcessAndCacheInternal(string assetName, string cacheFolder, string sourceFile = null)
        {
            NativeArray<InputSplatData> inputSplats = default;

            try
            {
                OnProgress?.Invoke("Reading file", 0.1f);

                // Find or use provided source file
                string sourceFilePath = sourceFile;
                if (string.IsNullOrEmpty(sourceFilePath))
                {
                    sourceFilePath = FindSourceFile(assetName);
                }

                if (string.IsNullOrEmpty(sourceFilePath))
                {
                    throw new FileNotFoundException($"Could not find {assetName}.ply or {assetName}.spz in StreamingAssets or Downloads");
                }

                // Read and parse input data
                GaussianFileReader.ReadFile(sourceFilePath, out inputSplats);
                if (inputSplats.Length == 0)
                    throw new InvalidOperationException($"Failed to read any splat data from {sourceFilePath}");

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

                // Use selected quality preset
                QualitySettings quality = GetQualitySettings();

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
                FileInfo sourceFileInfo = new FileInfo(sourceFilePath);
                SaveMetadata(cacheFolder, assetName, inputSplats.Length, boundsMin, boundsMax, quality, useChunks, 
                    GetAssetSource(sourceFilePath), sourceFileInfo.Name, sourceFileInfo.Length);

                OnProgress?.Invoke("Creating asset", 0.95f);

                // Create and return asset
                var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
                asset.name = assetName;
                asset.Initialize(inputSplats.Length, quality.formatPos, quality.formatScale, quality.formatColor, quality.formatSH, boundsMin, boundsMax, null);

                // Load the binary data files that were just created
                LoadBinaryData(cacheFolder, asset);

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
            // Check StreamingAssets first
            string gaDir = Path.Combine(Application.streamingAssetsPath, "GaussianSplats");
            
            string plyPath = Path.Combine(gaDir, assetName + ".ply");
            if (File.Exists(plyPath))
                return plyPath;

            string spzPath = Path.Combine(gaDir, assetName + ".spz");
            if (File.Exists(spzPath))
                return spzPath;

            // Check other sources folder
            string otherSourcesDir = GetOtherSourcesPath();
            plyPath = Path.Combine(otherSourcesDir, assetName + ".ply");
            if (File.Exists(plyPath))
                return plyPath;

            spzPath = Path.Combine(otherSourcesDir, assetName + ".spz");
            if (File.Exists(spzPath))
                return spzPath;

            // Check custom registered paths
            foreach (var customPath in m_CustomSourcePaths)
            {
                plyPath = Path.Combine(customPath, assetName + ".ply");
                if (File.Exists(plyPath))
                    return plyPath;

                spzPath = Path.Combine(customPath, assetName + ".spz");
                if (File.Exists(spzPath))
                    return spzPath;
            }

            return null;
        }

        AssetSource GetAssetSource(string sourceFilePath)
        {
            if (sourceFilePath.Contains(Application.streamingAssetsPath))
                return AssetSource.StreamingAssets;
            else if (sourceFilePath.Contains(GetOtherSourcesPath()))
                return AssetSource.Manual;
            else
                return AssetSource.Manual;
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

        void SaveMetadata(string cacheFolder, string assetName, int splatCount, float3 boundsMin, float3 boundsMax, QualitySettings quality, bool useChunks, AssetSource source, string sourceFilename, long sourceFileSize)
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
                boundsMaxZ = boundsMax.z,
                source = (int)source,
                sourceFilename = sourceFilename,
                sourceFileSize = sourceFileSize,
                createdTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()
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
            int chunkCount = (splatData.Length + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize;
            NativeArray<GaussianSplatAsset.ChunkInfo> chunks = new(chunkCount, Allocator.TempJob);
            
            // Process each chunk to calculate bounds
            for (int chunkIdx = 0; chunkIdx < chunkCount; chunkIdx++)
            {
                float3 chunkMinpos = float.PositiveInfinity;
                float3 chunkMinscl = float.PositiveInfinity;
                float4 chunkMincol = float.PositiveInfinity;
                float3 chunkMinshs = float.PositiveInfinity;
                float3 chunkMaxpos = float.NegativeInfinity;
                float3 chunkMaxscl = float.NegativeInfinity;
                float4 chunkMaxcol = float.NegativeInfinity;
                float3 chunkMaxshs = float.NegativeInfinity;

                int splatBegin = math.min(chunkIdx * GaussianSplatAsset.kChunkSize, splatData.Length);
                int splatEnd = math.min((chunkIdx + 1) * GaussianSplatAsset.kChunkSize, splatData.Length);

                // Calculate bounds for this chunk
                for (int i = splatBegin; i < splatEnd; ++i)
                {
                    InputSplatData s = splatData[i];
                    float3 scale = math.pow(s.scale, 1.0f / 8.0f);
                    float opacity = SquareCentered01(s.opacity);

                    chunkMinpos = math.min(chunkMinpos, s.pos);
                    chunkMinscl = math.min(chunkMinscl, scale);
                    chunkMincol = math.min(chunkMincol, new float4(s.dc0, opacity));
                    chunkMinshs = math.min(chunkMinshs, s.sh1);
                    chunkMinshs = math.min(chunkMinshs, s.sh2);
                    chunkMinshs = math.min(chunkMinshs, s.sh3);
                    chunkMinshs = math.min(chunkMinshs, s.sh4);
                    chunkMinshs = math.min(chunkMinshs, s.sh5);
                    chunkMinshs = math.min(chunkMinshs, s.sh6);
                    chunkMinshs = math.min(chunkMinshs, s.sh7);
                    chunkMinshs = math.min(chunkMinshs, s.sh8);
                    chunkMinshs = math.min(chunkMinshs, s.sh9);
                    chunkMinshs = math.min(chunkMinshs, s.shA);
                    chunkMinshs = math.min(chunkMinshs, s.shB);
                    chunkMinshs = math.min(chunkMinshs, s.shC);
                    chunkMinshs = math.min(chunkMinshs, s.shD);
                    chunkMinshs = math.min(chunkMinshs, s.shE);
                    chunkMinshs = math.min(chunkMinshs, s.shF);

                    chunkMaxpos = math.max(chunkMaxpos, s.pos);
                    chunkMaxscl = math.max(chunkMaxscl, scale);
                    chunkMaxcol = math.max(chunkMaxcol, new float4(s.dc0, opacity));
                    chunkMaxshs = math.max(chunkMaxshs, s.sh1);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh2);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh3);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh4);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh5);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh6);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh7);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh8);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh9);
                    chunkMaxshs = math.max(chunkMaxshs, s.shA);
                    chunkMaxshs = math.max(chunkMaxshs, s.shB);
                    chunkMaxshs = math.max(chunkMaxshs, s.shC);
                    chunkMaxshs = math.max(chunkMaxshs, s.shD);
                    chunkMaxshs = math.max(chunkMaxshs, s.shE);
                    chunkMaxshs = math.max(chunkMaxshs, s.shF);
                }

                // Ensure bounds are not zero
                chunkMaxpos = math.max(chunkMaxpos, chunkMinpos + 1.0e-5f);
                chunkMaxscl = math.max(chunkMaxscl, chunkMinscl + 1.0e-5f);
                chunkMaxcol = math.max(chunkMaxcol, chunkMincol + 1.0e-5f);
                chunkMaxshs = math.max(chunkMaxshs, chunkMinshs + 1.0e-5f);

                // Store chunk info
                GaussianSplatAsset.ChunkInfo info = default;
                info.posX = new float2(chunkMinpos.x, chunkMaxpos.x);
                info.posY = new float2(chunkMinpos.y, chunkMaxpos.y);
                info.posZ = new float2(chunkMinpos.z, chunkMaxpos.z);
                info.sclX = math.f32tof16(chunkMinscl.x) | (math.f32tof16(chunkMaxscl.x) << 16);
                info.sclY = math.f32tof16(chunkMinscl.y) | (math.f32tof16(chunkMaxscl.y) << 16);
                info.sclZ = math.f32tof16(chunkMinscl.z) | (math.f32tof16(chunkMaxscl.z) << 16);
                info.colR = math.f32tof16(chunkMincol.x) | (math.f32tof16(chunkMaxcol.x) << 16);
                info.colG = math.f32tof16(chunkMincol.y) | (math.f32tof16(chunkMaxcol.y) << 16);
                info.colB = math.f32tof16(chunkMincol.z) | (math.f32tof16(chunkMaxcol.z) << 16);
                info.colA = math.f32tof16(chunkMincol.w) | (math.f32tof16(chunkMaxcol.w) << 16);
                info.shR = math.f32tof16(chunkMinshs.x) | (math.f32tof16(chunkMaxshs.x) << 16);
                info.shG = math.f32tof16(chunkMinshs.y) | (math.f32tof16(chunkMaxshs.y) << 16);
                info.shB = math.f32tof16(chunkMinshs.z) | (math.f32tof16(chunkMaxshs.z) << 16);
                chunks[chunkIdx] = info;

                // Normalize splat data to 0..1 within chunk bounds (matches editor behavior)
                for (int i = splatBegin; i < splatEnd; ++i)
                {
                    InputSplatData s = splatData[i];
                    s.pos = ((float3)s.pos - chunkMinpos) / (chunkMaxpos - chunkMinpos);
                    s.scale = ((float3)s.scale - chunkMinscl) / (chunkMaxscl - chunkMinscl);
                    s.dc0 = ((float3)s.dc0 - chunkMincol.xyz) / (chunkMaxcol.xyz - chunkMincol.xyz);
                    s.opacity = (s.opacity - chunkMincol.w) / (chunkMaxcol.w - chunkMincol.w);
                    s.sh1 = ((float3)s.sh1 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh2 = ((float3)s.sh2 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh3 = ((float3)s.sh3 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh4 = ((float3)s.sh4 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh5 = ((float3)s.sh5 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh6 = ((float3)s.sh6 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh7 = ((float3)s.sh7 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh8 = ((float3)s.sh8 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh9 = ((float3)s.sh9 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shA = ((float3)s.shA - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shB = ((float3)s.shB - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shC = ((float3)s.shC - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shD = ((float3)s.shD - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shE = ((float3)s.shE - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shF = ((float3)s.shF - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    splatData[i] = s;
                }
            }

            // Write chunks to file
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            unsafe
            {
                fs.Write(new ReadOnlySpan<byte>((void*)chunks.GetUnsafePtr(), chunks.Length * UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()));
            }
            chunks.Dispose();
        }

        static void CreatePositionsData(NativeArray<InputSplatData> splatData, string filePath, GaussianSplatAsset.VectorFormat format)
        {
            int formatSize = GaussianSplatAsset.GetVectorSize(format);
            int dataLen = splatData.Length * formatSize;
            // Align to 8-byte boundary (matching editor version)
            dataLen = (dataLen + 7) / 8 * 8;
            
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            unsafe
            {
                for (int i = 0; i < splatData.Length; i++)
                {
                    var pos = (Vector3)splatData[i].pos;
                    byte[] data = EncodeVector(pos, format);
                    fs.Write(data, 0, data.Length);
                }
                // Write padding to align to 8 bytes
                int paddingSize = dataLen - (splatData.Length * formatSize);
                if (paddingSize > 0)
                {
                    byte[] padding = new byte[paddingSize];
                    fs.Write(padding, 0, paddingSize);
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
            // Create texture-sized buffer with proper swizzling (same as editor)
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(splatData.Length);
            int colorSize = GaussianSplatAsset.GetColorSize(format);
            int textureTotalSize = texWidth * texHeight * colorSize;
            byte[] textureData = new byte[textureTotalSize];

            // Map each splat to its texture position and encode
            for (int i = 0; i < splatData.Length; i++)
            {
                var splat = splatData[i];
                var color = new float4(splat.dc0.x, splat.dc0.y, splat.dc0.z, splat.opacity);
                byte[] colorBytes = EncodeColor(color, format);

                if (colorBytes.Length != colorSize)
                {
                    UnityEngine.Debug.LogError($"EncodeColor returned {colorBytes.Length} bytes but expected {colorSize} for format {format}");
                    System.Array.Resize(ref colorBytes, colorSize);
                }

                // Get texture position for this splat (with Morton swizzling)
                int texPos = SplatIndexToTextureIndex(i, texWidth);
                int byteOffset = texPos * colorSize;

                if (byteOffset >= 0 && byteOffset + colorSize <= textureTotalSize)
                {
                    System.Array.Copy(colorBytes, 0, textureData, byteOffset, colorSize);
                }
            }

            // Write the full texture buffer to file
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            fs.Write(textureData, 0, textureData.Length);
        }

        static int SplatIndexToTextureIndex(int idx, int texWidth)
        {
            // Decode Morton code for 16x16 tiles
            uint2 xy = DecodeMorton2D_16x16((uint)idx);
            uint width = (uint)(texWidth / 16);
            uint idx_shifted = (uint)idx >> 8;
            uint x = (idx_shifted % width) * 16 + xy.x;
            uint y = (idx_shifted / width) * 16 + xy.y;
            return (int)(y * texWidth + x);
        }

        static uint2 DecodeMorton2D_16x16(uint t)
        {
            t = (t & 0xFF) | ((t & 0xFE) << 7); // -EAFBGCHEAFBGCHD
            t &= 0x5555;                        // -E-F-G-H-A-B-C-D
            t = (t ^ (t >> 1)) & 0x3333;        // --EF--GH--AB--CD
            t = (t ^ (t >> 2)) & 0x0f0f;        // ----EFGH----ABCD
            return new uint2(t & 0xF, t >> 8);  // --------EFGHABCD
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
            float3[] shCoeffs = { splat.sh1, splat.sh2, splat.sh3, splat.sh4, splat.sh5, splat.sh6, 
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
            // Convert to float16 (half precision) using proper bit-level conversion
            float3[] shCoeffs = { splat.sh1, splat.sh2, splat.sh3, splat.sh4, splat.sh5, splat.sh6, 
                                  splat.sh7, splat.sh8, splat.sh9, splat.shA, splat.shB, splat.shC, 
                                  splat.shD, splat.shE, splat.shF };
            foreach (var sh in shCoeffs)
            {
                fs.Write(BitConverter.GetBytes(FloatToHalf(sh.x)), 0, 2);
                fs.Write(BitConverter.GetBytes(FloatToHalf(sh.y)), 0, 2);
                fs.Write(BitConverter.GetBytes(FloatToHalf(sh.z)), 0, 2);
            }
            // Padding
            fs.Write(BitConverter.GetBytes((ushort)0), 0, 2);
            fs.Write(BitConverter.GetBytes((ushort)0), 0, 2);
            fs.Write(BitConverter.GetBytes((ushort)0), 0, 2);
        }

        static void WriteSHNorm11(FileStream fs, InputSplatData splat)
        {
            float3[] shCoeffs = { splat.sh1, splat.sh2, splat.sh3, splat.sh4, splat.sh5, splat.sh6, 
                                  splat.sh7, splat.sh8, splat.sh9, splat.shA, splat.shB, splat.shC, 
                                  splat.shD, splat.shE, splat.shF };
            foreach (var sh in shCoeffs)
            {
                fs.Write(BitConverter.GetBytes(EncodeFloat3ToNorm11(sh)), 0, 4);
            }
        }

        static void WriteSHNorm6(FileStream fs, InputSplatData splat)
        {
            float3[] shCoeffs = { splat.sh1, splat.sh2, splat.sh3, splat.sh4, splat.sh5, splat.sh6, 
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

        static byte[] EncodeVector(float3 v, GaussianSplatAsset.VectorFormat format)
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

        static byte[] EncodeNorm16(float3 v)
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

        static byte[] EncodeColor(float4 color, GaussianSplatAsset.ColorFormat format)
        {
            return format switch
            {
                GaussianSplatAsset.ColorFormat.Float32x4 => ConcatBytes(
                    BitConverter.GetBytes(color.x),
                    BitConverter.GetBytes(color.y),
                    BitConverter.GetBytes(color.z),
                    BitConverter.GetBytes(color.w)),
                GaussianSplatAsset.ColorFormat.Float16x4 => EncodeHalf4(color),
                GaussianSplatAsset.ColorFormat.Norm8x4 => new byte[] 
                { 
                    (byte)(math.saturate(color.x) * 255), 
                    (byte)(math.saturate(color.y) * 255), 
                    (byte)(math.saturate(color.z) * 255), 
                    (byte)(math.saturate(color.w) * 255) 
                },
                _ => new byte[0]
            };
        }

        static byte[] EncodeHalf4(float4 color)
        {
            // Proper Float16 (half precision) encoding using bit-level conversion
            byte[] result = new byte[8];
            unsafe
            {
                fixed (byte* ptr = result)
                {
                    ushort* halfPtr = (ushort*)ptr;
                    halfPtr[0] = FloatToHalf(color.x);
                    halfPtr[1] = FloatToHalf(color.y);
                    halfPtr[2] = FloatToHalf(color.z);
                    halfPtr[3] = FloatToHalf(color.w);
                }
            }
            return result;
        }

        // Convert float32 to float16 (half precision) using IEEE 754 bit manipulation
        static ushort FloatToHalf(float value)
        {
            unsafe
            {
                uint bits = *(uint*)&value;
                uint sign = bits >> 31;
                uint exponent = (bits >> 23) & 0xFF;
                uint mantissa = bits & 0x7FFFFF;

                // Handle special cases
                if (exponent == 255) // Inf or NaN
                    return (ushort)((sign << 15) | 0x7FFF);
                
                if (exponent == 0) // Zero or subnormal
                    return (ushort)(sign << 15);

                // Bias adjustment: float32 bias is 127, float16 bias is 15
                int newExponent = (int)exponent - 127 + 15;

                if (newExponent >= 31) // Overflow to infinity
                    return (ushort)((sign << 15) | 0x7C00);
                
                if (newExponent <= 0) // Underflow to zero
                    return (ushort)(sign << 15);

                // Truncate mantissa from 23 bits to 10 bits
                uint newMantissa = mantissa >> 13;
                
                return (ushort)((sign << 15) | ((uint)newExponent << 10) | newMantissa);
            }
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

        static ulong EncodeFloat3ToNorm16(float3 v)
        {
            return (ulong)(v.x * 65535.5f) | ((ulong)(v.y * 65535.5f) << 16) | ((ulong)(v.z * 65535.5f) << 32);
        }

        static float SquareCentered01(float x)
        {
            // Transform value centered at 0.5 to be more uniformly distributed
            x = x - 0.5f;
            return x * x * 4.0f + 0.5f;
        }

        static uint EncodeFloat3ToNorm11(float3 v)
        {
            return (uint)(v.x * 2047.5f) | ((uint)(v.y * 1023.5f) << 11) | ((uint)(v.z * 2047.5f) << 21);
        }

        static ushort EncodeFloat3ToNorm655(float3 v)
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
