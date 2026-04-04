// SDFGI_MergeSDF.glsl — Initialize global SDF cascade with maximum distance (empty space).
// Dispatched once per cascade before per-object SDF blending passes.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(r16f, binding = 0) uniform image3D GlobalSDF;

layout(std140, binding = 1) uniform Params
{
    vec3 _CascadeCenter;
    float _CascadeSize;
    int _CascadeResolution;
};

void main()
{
    ivec3 coord = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(coord, ivec3(_CascadeResolution))))
        return;

    // Initialize with maximum distance (empty space)
    float dist = _CascadeSize * 2.0;

    imageStore(GlobalSDF, coord, vec4(dist, 0.0, 0.0, 0.0));
}
