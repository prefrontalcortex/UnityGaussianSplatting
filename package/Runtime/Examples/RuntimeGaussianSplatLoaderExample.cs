// SPDX-License-Identifier: MIT

using UnityEngine;
using GaussianSplatting.Runtime;

namespace GaussianSplatting.Examples
{
    /// <summary>
    /// Simple example showing how to use GaussianSplatRuntimeAssetCreator
    /// 
    /// Setup:
    /// 1. Add a PLY or SPZ file to Assets/StreamingAssets/GaussianSplats/
    /// 2. Attach this script to a GameObject
    /// 3. Set the asset name (without extension) in the inspector
    /// 4. Play the scene
    /// </summary>
    public class RuntimeGaussianSplatLoaderExample : MonoBehaviour
    {
        [SerializeField] string m_AssetName = "mymodel";
        [SerializeField] GaussianSplatRenderer m_Renderer;

        GaussianSplatAsset m_CurrentAsset;
        GaussianSplatRuntimeAssetCreator m_Creator;

        void Start()
        {
            // Create or get the processor component
            m_Creator = gameObject.GetComponent<GaussianSplatRuntimeAssetCreator>();
            if (m_Creator == null)
                m_Creator = gameObject.AddComponent<GaussianSplatRuntimeAssetCreator>();

            // Hook up callbacks
            m_Creator.OnProgress += HandleProgress;
            m_Creator.OnComplete += HandleComplete;

            // Start processing
            Debug.Log($"Loading Gaussian Splat: {m_AssetName}");
            StartCoroutine(m_Creator.LoadAndProcessAsync(m_AssetName));
        }

        void HandleProgress(string status, float progress)
        {
            // Update UI or display progress
            Debug.Log($"[{status}] {progress:P0}");
        }

        void HandleComplete(GaussianSplatAsset asset, bool success)
        {
            if (success)
            {
                m_CurrentAsset = asset;
                
                // Find or create a renderer
                if (m_Renderer == null)
                    m_Renderer = GetComponent<GaussianSplatRenderer>();
                
                if (m_Renderer == null)
                {
                    var go = new GameObject("GaussianSplatRenderer");
                    go.transform.SetParent(transform);
                    m_Renderer = go.AddComponent<GaussianSplatRenderer>();
                }

                // Assign the loaded asset
                m_Renderer.m_Asset = asset;
                
                Debug.Log($"✓ Successfully loaded and rendered '{m_AssetName}'");
                Debug.Log($"  Splats: {asset.splatCount:N0}");
                Debug.Log($"  Bounds: {asset.boundsMin} to {asset.boundsMax}");
            }
            else
            {
                Debug.LogError($"✗ Failed to load '{m_AssetName}'");
            }
        }

        void OnDestroy()
        {
            if (m_Creator != null)
            {
                m_Creator.OnProgress -= HandleProgress;
                m_Creator.OnComplete -= HandleComplete;
            }
        }
    }
}
