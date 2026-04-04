// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Prowl.Runtime.Resources;

namespace Prowl.Runtime.Rendering.GI;

/// <summary>
/// Shared utility methods for the Global Illumination systems.
/// </summary>
public static class GIUtils
{
    /// <summary>
    /// Resolves the effective GI mode and intensity by checking the primary
    /// directional light's override first, then falling back to scene settings.
    /// </summary>
    public static (Scene.GlobalIlluminationParams.GIMode Mode, float Intensity)
        ResolveGISettings(Scene scene, IReadOnlyList<IRenderableLight> lights)
    {
        // Check for directional light override
        foreach (IRenderableLight light in lights)
        {
            if (light is DirectionalLight dirLight && dirLight.OverrideSceneGI)
            {
                return (dirLight.GIModeOverride, dirLight.GIIntensityOverride);
            }
        }

        // Fall back to scene settings
        return (scene.GlobalIllumination.Mode, scene.GlobalIllumination.Intensity);
    }
}
