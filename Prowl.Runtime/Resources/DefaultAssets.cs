// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Resources;

/// <summary>
/// Default shaders embedded in the runtime
/// </summary>
public enum DefaultShader
{
    Standard,
    Unlit,
    Line,
    Invalid,
    UI,
    Gizmos,
    Blit,
    DirectionalLight,
    SpotLight,
    PointLight,
    DeferredCompose,
    Particle,
    Terrain,
    Refraction,

    ProceduralSkybox,
    Tonemapper,
    SSR,
    FXAA,
    Bloom,
    BokehDoF,
    GTAO,
    SSPT,
    DebugView,
    SelectionOutline,
    SDF,
    SDFUI,

    // Global Illumination
    VoxelGI_Voxelize,
    VoxelGI_InjectLight,
    VoxelGI_Mipmap,
    VoxelGI_ConeTrace,
    SDFGI_GenerateSDF,
    SDFGI_MergeSDF,
    SDFGI_ProbeUpdate,
    SDFGI_ProbeTrace,
    GI_TemporalBlend,

    // GI Debug Visualization
    GI_DebugVoxelGrid,
    GI_DebugSDFSlice,
    GI_DebugProbeGrid,
}

/// <summary>
/// Default models/meshes embedded in the runtime
/// </summary>
public enum DefaultModel
{
    Cube,
    Sphere,
    Cylinder,
    Plane,
    SkyDome,
    UnitCube // 1mcube.obj - 1 meter cube
}

/// <summary>
/// Default textures embedded in the runtime
/// </summary>
public enum DefaultTexture
{
    White,
    Gray18, // Also accessible as Gray
    Normal,
    Surface,
    Emission,
    Grid,
    Noise
}

/// <summary>
/// Default shader include files (GLSL)
/// </summary>
public enum DefaultShaderInclude
{
    Fragment,
    PBR,
    Random,
    ShaderVariables,
    Shadow,
    VertexAttributes,
    GICommon,
}
