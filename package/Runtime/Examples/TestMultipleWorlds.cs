// SPDX-License-Identifier: MIT

using System.Collections;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEngine;

namespace GaussianSplatting.Runtime.Examples
{
    /// <summary>
    /// Test script for multiple worlds functionality.
    /// Attach this to a GameObject and test various scenarios.
    /// </summary>
    public class TestMultipleWorlds : MonoBehaviour
    {
        [SerializeField] GaussianSplatRenderer m_Renderer;
        GaussianSplatRuntimeAssetCreator m_Creator;
        
        List<GaussianSplatRuntimeAssetCreator.AssetInfo> m_AvailableAssets = new();
        int m_CurrentAssetIndex = 0;

        void Start()
        {
            // Create the asset creator
            m_Creator = gameObject.AddComponent<GaussianSplatRuntimeAssetCreator>();
            m_Creator.QualityLevel = GaussianSplatRuntimeAssetCreator.QualityPreset.High;
            m_Creator.OnProgress += OnProgress;
            m_Creator.OnComplete += OnComplete;

            Debug.Log("=== Gaussian Splat Multiple Worlds Test ===");
            Debug.Log("Commands:");
            Debug.Log("  L - List available assets");
            Debug.Log("  D - Discover sources");
            Debug.Log("  R - Register test source path");
            Debug.Log("  P - Process specific file");
            Debug.Log("  N - Load next asset");
            Debug.Log("  1/2/3/4 - Set quality (VeryHigh/High/Medium/Low)");
            Debug.Log("  DEL - Delete current asset");
            Debug.Log("  SPACE - Print test paths");
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.L))
                TestListAssets();
            
            if (Input.GetKeyDown(KeyCode.D))
                TestDiscoverSources();
            
            if (Input.GetKeyDown(KeyCode.R))
                TestRegisterSourcePath();
            
            if (Input.GetKeyDown(KeyCode.P))
                TestProcessFile();
            
            if (Input.GetKeyDown(KeyCode.N))
                TestLoadNextAsset();
            
            if (Input.GetKeyDown(KeyCode.Delete))
                TestDeleteAsset();
            
            if (Input.GetKeyDown(KeyCode.Space))
                PrintTestPaths();
            
            // Quality presets
            if (Input.GetKeyDown(KeyCode.Alpha1))
                m_Creator.QualityLevel = GaussianSplatRuntimeAssetCreator.QualityPreset.VeryHigh;
            if (Input.GetKeyDown(KeyCode.Alpha2))
                m_Creator.QualityLevel = GaussianSplatRuntimeAssetCreator.QualityPreset.High;
            if (Input.GetKeyDown(KeyCode.Alpha3))
                m_Creator.QualityLevel = GaussianSplatRuntimeAssetCreator.QualityPreset.Medium;
            if (Input.GetKeyDown(KeyCode.Alpha4))
                m_Creator.QualityLevel = GaussianSplatRuntimeAssetCreator.QualityPreset.Low;
        }

        void TestListAssets()
        {
            Debug.Log("\n=== TESTING: List Available Assets ===");
            m_AvailableAssets = m_Creator.ListAvailableAssets();
            
            if (m_AvailableAssets.Count == 0)
            {
                Debug.LogWarning("No assets found! Add PLY/SPZ files to:");
                PrintTestPaths();
                return;
            }

            Debug.Log($"Found {m_AvailableAssets.Count} assets:");
            for (int i = 0; i < m_AvailableAssets.Count; i++)
            {
                var asset = m_AvailableAssets[i];
                Debug.Log($"  [{i}] {asset.name} (Size: {asset.fileSize} bytes, Cached: {asset.isCached}, Source: {asset.source})");
            }
        }

        void TestDiscoverSources()
        {
            Debug.Log("\n=== TESTING: Discover Source Paths ===");
            var paths = m_Creator.GetSourcePaths();
            
            foreach (var path in paths)
            {
                Debug.Log($"  Source Path: {path}");
                if (Directory.Exists(path))
                {
                    var files = Directory.GetFiles(path, "*.ply");
                    var spzFiles = Directory.GetFiles(path, "*.spz");
                    Debug.Log($"    - PLY files: {files.Length}");
                    Debug.Log($"    - SPZ files: {spzFiles.Length}");
                }
                else
                {
                    Debug.LogWarning($"    - Path does not exist (create it if needed)");
                }
            }
        }

        void TestRegisterSourcePath()
        {
            Debug.Log("\n=== TESTING: Register Source Path ===");
            string testPath = Path.Combine(Application.persistentDataPath, "GaussianSplats", "sources");
            Debug.Log($"Registering: {testPath}");
            
            if (!Directory.Exists(testPath))
            {
                Directory.CreateDirectory(testPath);
                Debug.Log($"Created directory: {testPath}");
            }

            m_Creator.RegisterSourcePath(testPath);
            Debug.Log("✓ Source path registered");
            Debug.Log($"$Copy your PLY/SPZ files to: {testPath}");
        }

        void TestProcessFile()
        {
            Debug.Log("\n=== TESTING: Process Specific File ===");
            
            if (m_AvailableAssets.Count == 0)
            {
                Debug.LogError("No assets available. Run 'L' first to discover assets.");
                return;
            }

            var assetInfo = m_AvailableAssets[m_CurrentAssetIndex];
            Debug.Log($"Processing: {assetInfo.name}");
            StartCoroutine(m_Creator.ProcessFileAsync(assetInfo.name, assetInfo.sourceFile));
        }

        void TestLoadNextAsset()
        {
            Debug.Log("\n=== TESTING: Load Next Asset ===");
            
            if (m_AvailableAssets.Count == 0)
            {
                Debug.LogError("No assets available. Run 'L' first to discover assets.");
                return;
            }

            m_CurrentAssetIndex = (m_CurrentAssetIndex + 1) % m_AvailableAssets.Count;
            var assetInfo = m_AvailableAssets[m_CurrentAssetIndex];
            
            Debug.Log($"Loading asset [{m_CurrentAssetIndex}]: {assetInfo.name}");
            StartCoroutine(m_Creator.LoadAndProcessAsync(assetInfo.name));
        }

        void TestDeleteAsset()
        {
            Debug.Log("\n=== TESTING: Delete Asset ===");
            
            if (m_AvailableAssets.Count == 0)
            {
                Debug.LogError("No assets available.");
                return;
            }

            var assetInfo = m_AvailableAssets[m_CurrentAssetIndex];
            Debug.Log($"Deleting: {assetInfo.name}");
            
            bool success = m_Creator.DeleteAsset(assetInfo.name, deleteSource: false);
            if (success)
            {
                Debug.Log("✓ Asset deleted");
                TestListAssets();  // Refresh list
            }
            else
            {
                Debug.LogError("Failed to delete asset");
            }
        }

        void PrintTestPaths()
        {
            Debug.Log("\n=== Test Paths ===");
            Debug.Log($"StreamingAssets: {Path.Combine(Application.streamingAssetsPath, "GaussianSplats")}");
            Debug.Log($"PersistentData: {Path.Combine(Application.persistentDataPath, "GaussianSplats")}");
            Debug.Log($"Current Quality: {m_Creator.QualityLevel}");
            Debug.Log($"Current Asset: {(m_AvailableAssets.Count > 0 ? m_AvailableAssets[m_CurrentAssetIndex].name : "None")}");
        }

        void OnProgress(string status, float progress)
        {
            Debug.Log($"[{progress:P0}] {status}");
        }

        void OnComplete(GaussianSplatAsset asset, bool success)
        {
            if (success && asset != null)
            {
                Debug.Log($"✓ Asset loaded successfully: {asset.name}");
                Debug.Log($"  Splat Count: {asset.splatCount:N0}");
                Debug.Log($"  Bounds: {asset.boundsMin} to {asset.boundsMax}");
                
                if (m_Renderer != null)
                {
                    m_Renderer.m_Asset = asset;
                    Debug.Log("✓ Renderer updated");
                }
                else
                {
                    Debug.LogWarning("Renderer not assigned - can't display asset");
                }
            }
            else
            {
                Debug.LogError("Failed to load asset");
            }
        }
    }
}
