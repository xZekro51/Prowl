// VoxelGI_InjectLight.glsl — Direct light injection into the voxel grid.
// Reads voxel albedo, applies N.L shading from the directional light,
// and writes back the lit radiance.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(rgba16f, binding = 0) uniform image3D VoxelRadiance;

layout(std140, binding = 1) uniform Params
{
    vec3 _LightDirection;
    float _LightIntensity;
    vec3 _LightColor;
    float _VoxelGridSize;
    vec3 _VoxelGridCenter;
    int _VoxelResolution;
};

void main()
{
    ivec3 voxelCoord = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(voxelCoord, ivec3(_VoxelResolution))))
        return;

    vec4 voxelData = imageLoad(VoxelRadiance, voxelCoord);

    // Skip empty voxels
    if (voxelData.a < 0.01)
        return;

    // Simple hemisphere lighting: assume voxel normal is roughly upward
    // A more accurate approach would use the stored normal texture
    vec3 albedo = voxelData.rgb;

    // Approximate diffuse lighting using the light direction
    // Since we don't have per-voxel normals in this pass, use a simple
    // ambient + directional approximation
    float ambient = 0.15;
    float NdotL = max(0.0, -_LightDirection.y * 0.5 + 0.5); // hemisphere approx
    vec3 lighting = _LightColor * _LightIntensity * NdotL + vec3(ambient);
    vec3 litColor = albedo * lighting;

    imageStore(VoxelRadiance, voxelCoord, vec4(litColor, voxelData.a));
}
