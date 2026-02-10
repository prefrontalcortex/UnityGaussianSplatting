# Multi-World Gaussian Splat Support

This package provides independent multi-world support for Gaussian Splatting without tight coupling to any specific module.

## File Organization

```
StreamingAssets/
└── GaussianSplats/          # Manually added source files

PersistentDataPath/
└── GaussianSplats/
    ├── sources/             # Additional source files (flexible)
    ├── world1/              # Processed cache for world1
    │   ├── metadata.json
    │   ├── data.pos
    │   ├── data.oth
    │   ├── data.col
    │   ├── data.shs
    │   └── data.chk (optional)
    └── world2/              # Processed cache for world2
        └── ...
```

## Basic Usage

### 1. Load from StreamingAssets
```csharp
var creator = gameObject.AddComponent<GaussianSplatRuntimeAssetCreator>();
creator.QualityLevel = GaussianSplatRuntimeAssetCreator.QualityPreset.High;
creator.OnComplete += (asset, success) => {
    if (success && asset != null) {
        renderer.SetAsset(asset);
    }
};

StartCoroutine(creator.LoadAndProcessAsync("myworld"));
```

### 2. List All Available Assets
```csharp
var assets = creator.ListAvailableAssets();
foreach (var asset in assets)
{
    Debug.Log($"{asset.name} - Source: {asset.source}, Cached: {asset.isCached}, Size: {asset.fileSize} bytes");
}
```

### 3. Delete Asset
```csharp
creator.DeleteAsset("myworld", deleteSource: true);  // Delete both cache and source
creator.DeleteAsset("myworld", deleteSource: false); // Delete only cache
```

## Integration with Other Modules

### Pattern: Register Custom Source Path

Other modules (Marble AI, custom world loader, etc.) can register their own source path **without the Gaussian Splat package knowing about them**:

```csharp
// In your Marble AI module or wherever files are placed
public class MarbleAIIntegration
{
    private GaussianSplatRuntimeAssetCreator m_SplatCreator;
    private string m_ModelCacheFolder;
    
    public void Initialize(GaussianSplatRuntimeAssetCreator creator)
    {
        m_SplatCreator = creator;
        m_ModelCacheFolder = Path.Combine(Application.persistentDataPath, "MarbleAI", "models");
        
        // Register the path - now all files in this folder are discoverable
        m_SplatCreator.RegisterSourcePath(m_ModelCacheFolder);
    }
    
    public void DownloadWorldModel(string worldId)
    {
        string modelPath = Path.Combine(m_ModelCacheFolder, $"{worldId}.ply");
        
        // Your module handles downloading, we just process the result
        StartCoroutine(DownloadWorldData(worldId, modelPath));
    }
    
    private IEnumerator DownloadWorldData(string worldId, string savePath)
    {
        // Download to savePath using your API
        // ...
        
        // Once downloaded, tell GaussianSplat to process it
        m_SplatCreator.OnComplete += (asset, success) => {
            if (success) {
                // Renderer now has the world loaded
                m_SplatRenderer.SetAsset(asset);
            }
        };
        
        StartCoroutine(m_SplatCreator.ProcessFileAsync(worldId, savePath));
        yield break;
    }
}
```

### Pattern: Direct File Processing

If another module already has a file at a known path:

```csharp
// Simply use ProcessFileAsync to convert any PLY/SPZ file
StartCoroutine(creator.ProcessFileAsync("worldName", "/path/to/file.ply"));
```

## Asset Discovery

The system automatically discovers files from (in priority order):

1. **StreamingAssets/GaussianSplats/** - Files bundled with the app
2. **PersistentDataPath/GaussianSplats/sources/** - Default location for additional files
3. **Registered custom paths** - Any path registered via `RegisterSourcePath()`

Use `ListAvailableAssets()` to see all discovered files across all sources.

## Asset Metadata

Each processed asset tracks:

- **Source**: Where the file came from (StreamingAssets, Manual, or registered path)
- **Source Filename**: Original file name
- **Source File Size**: Size of the source PLY/SPZ file
- **SplatCount**: Number of Gaussian splats in the model
- **Bounds**: Min/Max positions
- **Cached**: Whether the processed version exists
- **Created Timestamp**: When it was processed
- **Quality Settings**: Position/Scale/Color/SH formats used

## Independent Module Integration

### No Coupling Required

The Gaussian Splat package does **not** need to know about:
- Marble AI
- Your custom world system
- Any API integrations
- File download logic

Your module just needs to:
1. Provide PLY/SPZ files in a folder
2. Call `RegisterSourcePath()` or `ProcessFileAsync()`
3. Listen for `OnComplete` callback

### Example: Minimal Integration

```csharp
// Your custom module
public class CustomWorldLoader
{
    public static void LoadWorld(string worldName, string filePath, GaussianSplatRuntimeAssetCreator creator)
    {
        creator.OnComplete += (asset, success) => {
            if (success && asset != null)
            {
                Debug.Log($"World '{worldName}' loaded successfully");
                // Use the asset (set renderer, etc)
            }
        };
        
        creator.StartCoroutine(creator.ProcessFileAsync(worldName, filePath));
    }
}
```

## Switching Between Worlds

```csharp
// List available worlds
var worlds = creator.ListAvailableAssets();

// Switch to a different world
creator.OnComplete += (asset, success) => {
    if (success && asset != null)
    {
        renderer.SetAsset(asset);  // Smoothly switches renderer to new asset
    }
};

StartCoroutine(creator.LoadAndProcessAsync(worlds[nextWorld].name));
```

## Performance Notes

- First load of an asset: ~5-30 seconds (depends on point cloud size and quality)
- Subsequent loads: &lt;100ms (loaded from cache)
- Cache is persistent across app restarts
- Delete cache to force reprocessing with different quality settings

## Troubleshooting

**Asset not found:**
- Check that the file exists at `StreamingAssets/GaussianSplats/` or in registered source path
- Use `ListAvailableAssets()` to verify file discovery
- Check console for "Could not find {assetName}" error

**Wrong position/colors:**
- Ensure you're using the latest cached version
- Delete cache folder and reprocess: `creator.DeleteAsset(name, deleteSource: false)`

**Quality different than expected:**
- Check `creator.QualityLevel` setting before processing
- Metadata shows actual quality: `GetAssetInfo(name)`
