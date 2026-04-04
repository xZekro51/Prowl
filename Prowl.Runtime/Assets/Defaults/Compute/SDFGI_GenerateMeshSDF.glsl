// SDFGI_GenerateMeshSDF.glsl — Brute-force SDF generation from mesh triangles.
// Dispatched once per unique mesh by MeshSDFCache.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(r16f, binding = 0) uniform image3D MeshSDF;

// Triangle data packed as SSBO — each triangle = 3 consecutive vec4 (xyz + padding)
layout(std430, binding = 2) readonly buffer TriangleData
{
    vec4 triangles[];
};

layout(std140, binding = 1) uniform Params
{
    vec3  _BoundsMin;
    float _BoundsExtent;
    int   _SDFResolution;
    int   _TriangleCount;
};

// Squared length helper
float dot2(vec3 v) { return dot(v, v); }

// Point-triangle unsigned distance (Inigo Quilez's method)
float PointTriangleDistance(vec3 p, vec3 a, vec3 b, vec3 c)
{
    vec3 ba = b - a; vec3 pa = p - a;
    vec3 cb = c - b; vec3 pb = p - b;
    vec3 ac = a - c; vec3 pc = p - c;
    vec3 nor = cross(ba, ac);

    float signCheck = sign(dot(cross(ba, nor), pa))
                    + sign(dot(cross(cb, nor), pb))
                    + sign(dot(cross(ac, nor), pc));

    if (signCheck < 2.0)
    {
        // Outside — distance to nearest edge
        return sqrt(min(min(
            dot2(ba * clamp(dot(ba, pa) / dot(ba, ba), 0.0, 1.0) - pa),
            dot2(cb * clamp(dot(cb, pb) / dot(cb, cb), 0.0, 1.0) - pb)),
            dot2(ac * clamp(dot(ac, pc) / dot(ac, ac), 0.0, 1.0) - pc)));
    }
    else
    {
        // Inside triangle — distance to plane
        return sqrt(dot(nor, pa) * dot(nor, pa) / dot(nor, nor));
    }
}

void main()
{
    ivec3 coord = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(coord, ivec3(_SDFResolution))))
        return;

    // Map voxel coordinate to local-space position
    vec3 localPos = _BoundsMin +
        (vec3(coord) + 0.5) / float(_SDFResolution) * _BoundsExtent;

    float minDist = 1e10;
    for (int i = 0; i < _TriangleCount; i++)
    {
        vec3 v0 = triangles[i * 3 + 0].xyz;
        vec3 v1 = triangles[i * 3 + 1].xyz;
        vec3 v2 = triangles[i * 3 + 2].xyz;
        float d = PointTriangleDistance(localPos, v0, v1, v2);
        minDist = min(minDist, d);
    }

    // Normalize distance to SDF extent so values are in [0, 1] range relative to bounds
    float normalizedDist = minDist / _BoundsExtent;

    imageStore(MeshSDF, coord, vec4(normalizedDist, 0.0, 0.0, 0.0));
}
