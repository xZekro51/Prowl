// GI_Common.glsl — Shared utilities for VoxelGI and SDFGI

// ─── Spherical Harmonics L1 ───────────────────────────────────

// Encode radiance from a given direction into L1 SH coefficients (4 floats)
vec4 SHEncodeL1(vec3 direction, vec3 radiance)
{
    // L0 band
    float sh0 = 0.282095;
    // L1 band
    float sh1 = 0.488603 * direction.y;
    float sh2 = 0.488603 * direction.z;
    float sh3 = 0.488603 * direction.x;

    float luminance = dot(radiance, vec3(0.2126, 0.7152, 0.0722));
    return vec4(sh0, sh1, sh2, sh3) * luminance;
}

// Decode L1 SH irradiance for a given normal direction
vec3 SHDecodeL1(vec4 sh, vec3 normal)
{
    float sh0 = 0.282095;
    float sh1 = 0.488603 * normal.y;
    float sh2 = 0.488603 * normal.z;
    float sh3 = 0.488603 * normal.x;

    float irradiance = max(0.0, sh0 * sh.x + sh1 * sh.y + sh2 * sh.z + sh3 * sh.w);
    return vec3(irradiance);
}

// ─── 3D Texture Coordinate Transforms ────────────────────────

// Convert world position to voxel grid UVW [0, 1]
vec3 WorldToVoxelUVW(vec3 worldPos, vec3 gridCenter, float gridSize)
{
    return (worldPos - gridCenter) / (gridSize * 2.0) + 0.5;
}

// Convert world position to voxel integer coordinates
ivec3 WorldToVoxelCoord(vec3 worldPos, vec3 gridCenter, float gridSize, int resolution)
{
    vec3 uvw = WorldToVoxelUVW(worldPos, gridCenter, gridSize);
    return ivec3(uvw * float(resolution));
}

// Convert voxel integer coordinates to world position
vec3 VoxelCoordToWorld(ivec3 coord, vec3 gridCenter, float gridSize, int resolution)
{
    return gridCenter + ((vec3(coord) + 0.5) / float(resolution) - 0.5) * 2.0 * gridSize;
}

// Check if UVW coordinates are within [0, 1] bounds
bool IsInsideVolume(vec3 uvw)
{
    return all(greaterThanEqual(uvw, vec3(0.0))) && all(lessThanEqual(uvw, vec3(1.0)));
}

// Check if UVW coordinates are within a margin (for cascade blending)
bool IsInsideVolumeMargin(vec3 uvw, float margin)
{
    return all(greaterThan(uvw, vec3(margin))) && all(lessThan(uvw, vec3(1.0 - margin)));
}

// ─── SDF Ray Marching ────────────────────────────────────────

// March a ray through a signed distance field
// Returns hit distance, or -1.0 if no hit
float MarchSDF(sampler3D sdfTexture, vec3 origin, vec3 direction,
               vec3 cascadeCenter, float cascadeSize, float maxDist, int maxSteps)
{
    float t = 0.0;
    for (int i = 0; i < maxSteps; i++)
    {
        vec3 p = origin + direction * t;
        vec3 uvw = WorldToVoxelUVW(p, cascadeCenter, cascadeSize);

        if (!IsInsideVolume(uvw))
            return -1.0; // out of bounds

        float d = texture(sdfTexture, uvw).r;
        if (d < 0.001)
            return t; // hit

        t += d;
        if (t > maxDist)
            return -1.0;
    }
    return -1.0;
}

// ─── Tangent Frame Construction ──────────────────────────────

// Build an orthonormal tangent frame from a normal vector
void BuildTangentFrame(vec3 normal, out vec3 tangent, out vec3 bitangent)
{
    // Frisvad's method for building a tangent frame
    if (normal.z < -0.9999)
    {
        tangent = vec3(0.0, -1.0, 0.0);
        bitangent = vec3(-1.0, 0.0, 0.0);
    }
    else
    {
        float a = 1.0 / (1.0 + normal.z);
        float b = -normal.x * normal.y * a;
        tangent = vec3(1.0 - normal.x * normal.x * a, b, -normal.x);
        bitangent = vec3(b, 1.0 - normal.y * normal.y * a, -normal.y);
    }
}

// ─── World Position Reconstruction ───────────────────────────

// Reconstruct world position from depth and screen UV
// (Same as DeferredCompose.shader for consistency)
vec3 GI_WorldPosFromDepth(float depth, vec2 texCoord, mat4 invVP)
{
#ifdef PROWL_VULKAN
    float z = depth;
#else
    float z = depth * 2.0 - 1.0;
#endif
    vec2 ndcXY = vec2(texCoord.x * 2.0 - 1.0, 1.0 - texCoord.y * 2.0);
    vec4 clipSpacePosition = vec4(ndcXY, z, 1.0);
    vec4 worldSpacePosition = invVP * clipSpacePosition;
    worldSpacePosition /= worldSpacePosition.w;
    return worldSpacePosition.xyz;
}

// ─── Cone Tracing Helpers ────────────────────────────────────

// Predefined diffuse cone directions in tangent space (6 cones)
const int GI_CONE_COUNT_6 = 6;
const vec3 GI_CONE_DIRS_6[6] = vec3[6](
    vec3(0.0, 1.0, 0.0),
    vec3(0.0, 0.5, 0.866025),
    vec3(0.823639, 0.5, 0.267617),
    vec3(0.509037, 0.5, -0.700629),
    vec3(-0.509037, 0.5, -0.700629),
    vec3(-0.823639, 0.5, 0.267617)
);
const float GI_CONE_WEIGHTS_6[6] = float[6](
    0.25, 0.15, 0.15, 0.15, 0.15, 0.15
);
