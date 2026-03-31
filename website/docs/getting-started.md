---
id: getting-started
title: Getting Started
sidebar_position: 2
description: Get Prowl up and running in minutes with this step-by-step guide.
keywords: [prowl, getting started, installation, setup, dotnet]
---

import Tabs from '@theme/Tabs';
import TabItem from '@theme/TabItem';

# Getting Started

Getting Prowl up and running is super easy!

## Prerequisites

:::info Required Software

- [**.NET 9 SDK**](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) — required to build and run
- A compatible IDE (pick one):
  - [Visual Studio 2022 17.8+](https://visualstudio.microsoft.com/vs/preview/)
  - [Visual Studio Code](https://code.visualstudio.com/) with the C# extension
  - [JetBrains Rider](https://www.jetbrains.com/rider/)

:::

## Installation

### 1. Clone the repository

```bash title="Terminal"
git clone https://github.com/ProwlEngine/Prowl.git
cd Prowl
```

### 2. Update submodules

<Tabs groupId="operating-system">
  <TabItem value="win" label="🪟 Windows" default>

```bash title="Command Prompt or PowerShell"
UpdateSubmodules.bat
```

  </TabItem>
  <TabItem value="unix" label="🐧 Linux / 🍎 macOS">

```bash title="Terminal"
./UpdateSubmodules.sh
```

  </TabItem>
</Tabs>

### 3. Open the solution

Open the `.sln` file in your preferred IDE.

### 4. Run the Editor

Set **`Prowl.Editor`** as the startup project and run it.

:::tip That's it! 🎉

The editor should open and you're ready to start exploring. If you run into issues, check the [GitHub Issues](https://github.com/ProwlEngine/Prowl/issues) or ask on [Discord](https://discord.gg/BqnJ9Rn4sn).

:::

<details>
<summary>💡 Running from VS Code</summary>

1. Open `Prowl.Editor/Program.cs`
2. Go to **Run → Start Debugging**
3. Make sure you have the `Program.cs` file selected

</details>

## Project Structure

| Project | Description |
|---------|-------------|
| `Prowl.Runtime` | Core engine runtime — ECS, rendering, physics, audio, serialization |
| `Prowl.Editor` | Editor application — panels, inspectors, build system, project management |
| `Prowl.Launcher` | Project launcher / manager |
| `Prowl.EventSystem.Generators` | Roslyn source generator for the event system |
| `Prowl.Runtime.Test` | Unit tests for the runtime |
| `Prowl.Editor.Tests` | Unit tests for the editor |
| `Prowl.Benchmarks` | Performance benchmarks |
| `Samples/` | Example projects demonstrating engine features |
| `External/` | Third-party libraries (Veldrid, DotRecast) |

## Next Steps

:::note Where to go from here

- 📐 **Architecture** — Read the [Event System](./architecture/event-system) and [Vulkan Rendering Pipeline](./architecture/vulkan-rendering-pipeline) docs to understand how the engine is structured
- 🎨 **Features** — Explore [Rendering](./features/rendering), [Physics](./features/physics), [Scripting](./features/scripting), and [Asset Pipeline](./features/asset-pipeline) for detailed feature docs
- 🤝 **Contributing** — See [Contributing](./contributing) to learn how to help develop Prowl

:::
