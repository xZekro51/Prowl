---
id: asset-pipeline
title: Asset Pipeline
sidebar_position: 4
description: Prowl's asset pipeline — meta files, GUID references, import caching, custom importers, and build system.
keywords: [prowl, assets, pipeline, import, build, guid]
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

<details>
<summary><strong>📁 Supported Formats</strong></summary>

Prowl supports many major file formats through third-party libraries:

| Category | Formats | Library |
|----------|---------|---------|
| **3D Models** | FBX, OBJ, GLTF, and more | [Assimp](https://github.com/assimp/assimp) |
| **Images** | PNG, JPG, BMP, TGA, and more | [ImageSharp](https://github.com/SixLabors/ImageSharp) |
| **Audio** | WAV (more formats planned) | Built-in |
| **Shaders** | Custom GLSL-based format | Built-in |

</details>

## Asset Events

The asset pipeline integrates with the [Event System](../architecture/event-system):

```csharp title="Reacting to asset changes"
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

:::info

Prowl includes a complete build system for creating standalone applications — **Windows, macOS, and Linux** from any platform.

:::

| Feature | Description |
|---------|-------------|
| **Packed Asset Files** | Assets are bundled into optimized pack files |
| **Tiny Builds** | Only used assets are exported, keeping builds small |
| **Cross-Platform** | Build for Windows, macOS, and Linux from any platform |

### Build Pipeline

```
┌──────────────────────────────────────────────────┐
│ 1. Analyze scene references                       │
│    └─ Determine which assets are needed           │
├──────────────────────────────────────────────────┤
│ 2. Serialize & pack                               │
│    └─ Referenced assets + dependencies → pack     │
├──────────────────────────────────────────────────┤
│ 3. Compile runtime project                        │
│    └─ With packed assets embedded                 │
├──────────────────────────────────────────────────┤
│ 4. Produce standalone executable                  │
│    └─ For target platform                         │
└──────────────────────────────────────────────────┘
```

:::tip

Only assets that are actually referenced by your scenes (and their transitive dependencies) end up in the build. This keeps standalone builds as small as possible.

:::
