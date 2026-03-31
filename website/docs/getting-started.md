---
id: getting-started
title: Getting Started
sidebar_position: 2
---

# Getting Started

Getting Prowl up and running is super easy!

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
- A compatible IDE:
  - [Visual Studio 2022 17.8+](https://visualstudio.microsoft.com/vs/preview/)
  - [Visual Studio Code](https://code.visualstudio.com/) with the C# extension
  - [JetBrains Rider](https://www.jetbrains.com/rider/)

## Installation

1. **Clone the repository**

   ```bash
   git clone https://github.com/ProwlEngine/Prowl.git
   cd Prowl
   ```

2. **Update submodules**

   On Windows:
   ```bash
   UpdateSubmodules.bat
   ```

   On Linux / macOS:
   ```bash
   ./UpdateSubmodules.sh
   ```

3. **Open the solution**

   Open the `.sln` file in your preferred IDE.

4. **Run the Editor**

   Set `Prowl.Editor` as the startup project and run it.

That's it! 🎉

## Running from VS Code

- Open `Prowl.Editor/Program.cs`
- Go to **Run → Start Debugging**
- Make sure you have the `Program.cs` file selected

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

- Read the [Event System](/docs/architecture/event-system) and [Vulkan Rendering Pipeline](/docs/architecture/vulkan-rendering-pipeline) docs to understand how the engine is structured
- Check the [Rendering](/docs/features/rendering), [Physics](/docs/features/physics), [Scripting](/docs/features/scripting), and [Asset Pipeline](/docs/features/asset-pipeline) docs for detailed feature documentation
- See [Contributing](/docs/contributing) to learn how to help develop Prowl
