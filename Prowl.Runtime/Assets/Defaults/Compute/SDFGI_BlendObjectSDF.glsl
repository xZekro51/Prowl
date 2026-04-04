// SDFGI_BlendObjectSDF.glsl — Blend one object's SDF into the global cascade.
// Dispatched once per object per cascade by SDFGISystem.UpdateGlobalSDF().

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(r16f, binding = 0) uniform image3D GlobalSDF;
layout(binding = 2) uniform sampler3D ObjectSDF;

layout(std140, binding = 1) uniform Params
{
    vec3  _CascadeCenter;
    float _CascadeSize;
    int   _CascadeResolution;
    float _Padding0;
    float _Padding1;
    float _Padding2;
    vec3  _ObjectCenter;
    float _ObjectExtent;
};

void main()
{
    ivec3 coord = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(coord, ivec3(_CascadeResolution))))
        return;

    // Current global SDF distance
    float currentDist = imageLoad(GlobalSDF, coord).r;

    // World position of this cascade voxel
    vec3 worldPos = _CascadeCenter +
        ((vec3(coord) + 0.5) / float(_CascadeResolution) - 0.5) * 2.0 * _CascadeSize;

    // Transform to object's local SDF space [0, 1]
    vec3 localUVW = (worldPos - _ObjectCenter) / (_ObjectExtent * 2.0) + 0.5;

    // Skip if outside the object's SDF volume
    if (any(lessThan(localUVW, vec3(0.0))) || any(greaterThan(localUVW, vec3(1.0))))
        return;

    // Sample object SDF (normalized distance) and convert to world-space distance
    float objectDist = texture(ObjectSDF, localUVW).r * _ObjectExtent * 2.0;

    // Take minimum (union of all objects)
    float mergedDist = min(currentDist, objectDist);
    imageStore(GlobalSDF, coord, vec4(mergedDist, 0.0, 0.0, 0.0));
}
