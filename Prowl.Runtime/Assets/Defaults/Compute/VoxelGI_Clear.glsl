// VoxelGI_Clear.glsl — Clear the entire voxel grid to zero.
// Dispatched once per frame before AABB fill passes.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(rgba16f, binding = 0) uniform image3D VoxelRadiance;

layout(std140, binding = 1) uniform Params
{
    int _VoxelResolution;
};

void main()
{
    ivec3 coord = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(coord, ivec3(_VoxelResolution))))
        return;
    imageStore(VoxelRadiance, coord, vec4(0.0));
}
