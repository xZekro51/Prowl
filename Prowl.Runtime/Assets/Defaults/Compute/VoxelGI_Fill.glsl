// VoxelGI_Fill.glsl — Fill voxels overlapping a single AABB with albedo data.
// Dispatched once per renderable. Each invocation checks if its voxel center falls
// inside the renderable's world-space bounding box and writes albedo + opacity.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(rgba16f, binding = 0) uniform image3D VoxelRadiance;

layout(std140, binding = 1) uniform Params
{
    vec3  _VoxelGridCenter;
    float _VoxelGridSize;
    int   _VoxelResolution;
    vec3  _AABBMin;
    vec3  _AABBMax;
    vec4  _Albedo;
};

void main()
{
    ivec3 coord = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(coord, ivec3(_VoxelResolution))))
        return;

    // Convert voxel coordinate to world position (center of voxel)
    vec3 worldPos = _VoxelGridCenter +
        ((vec3(coord) + 0.5) / float(_VoxelResolution) - 0.5) * 2.0 * _VoxelGridSize;

    // Check if this voxel center falls inside the renderable's AABB
    if (all(greaterThanEqual(worldPos, _AABBMin)) && all(lessThanEqual(worldPos, _AABBMax)))
    {
        vec4 existing = imageLoad(VoxelRadiance, coord);
        // Blend with any previously written data at this voxel
        if (existing.a > 0.01)
            imageStore(VoxelRadiance, coord, vec4(mix(existing.rgb, _Albedo.rgb, 0.5), 1.0));
        else
            imageStore(VoxelRadiance, coord, vec4(_Albedo.rgb, 1.0));
    }
}
