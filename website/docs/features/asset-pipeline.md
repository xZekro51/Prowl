---
id: asset-pipeline
title: Asset Pipeline
sidebar_position: 4
---

# Asset Pipeline

Prowl includes a powerful asset pipeline for managing project resources.

## Core Features

| Feature | Description |
|---------|-------------|
| **Meta Files** | Each asset has a companion `.meta` file containing import settings and a stable GUID |
| **Reference by GUID** | Assets are referenced by GUID, so renaming or moving files doesn't break references |
| **Import Caching** | Imported assets are cached to avoid redundant re-imports |
| **Custom Importers** | Support for implementing custom importers for new file types |
| **Sub-Assets** | Assets can be stored inside other assets |
| **Dependency Tracking** | The pipeline tracks dependencies between assets |

## Supported Formats

Prowl supports many major file formats through third-party libraries:

- **3D Models** — via [Assimp](https://github.com/assimp/assimp) (FBX, OBJ, GLTF, and more)
- **Images** — via [ImageSharp](https://github.com/SixLabors/ImageSharp) (PNG, JPG, BMP, TGA, and more)
- **Audio** — WAV files (more formats planned)
- **Shaders** — Custom GLSL-based shader format

## Asset Events

The asset pipeline integrates with the [Event System](/docs/architecture/event-system):

```csharp
// React to asset changes
AssetEvents.OnAssetsRefreshed += () =>
{
    // Asset database was refreshed
};

AssetEvents.OnAssetsImported += (args) =>
{
    // New files imported — args.ImportedPaths contains the paths
};

AssetEvents.OnAssetDeleted += (args) =>
{
    // An asset was deleted — args.RelativePath contains the path
};
```

## Build System

Prowl includes a complete build system for creating standalone applications:

- **Packed Asset Files** — Assets are bundled into optimized pack files
- **Tiny Builds** — Only used assets are exported, keeping builds small
- **Cross-Platform** — Build for Windows, macOS, and Linux from any platform

### Build Pipeline

1. The editor analyzes scene references to determine which assets are needed
2. Referenced assets (and their dependencies) are serialized and packed
3. The runtime project is compiled with the packed assets
4. A standalone executable is produced for the target platform
