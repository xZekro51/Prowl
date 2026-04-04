// SDFGI_ProbeUpdate.glsl — Incrementally update irradiance probes.
// Each probe traces rays through the global SDF and accumulates L1 SH.

#include "GICommon"

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(rgba16f, binding = 0) uniform image3D ProbeIrradiance;
layout(binding = 2) uniform sampler3D GlobalSDF;

layout(std140, binding = 1) uniform Params
{
    vec3  _CascadeCenter;
    float _CascadeSize;
    int   _ProbeResolution;
    int   _ProbeUpdateOffset;
    float _Hysteresis;
    vec3  _LightDirection;
    float _LightIntensity;
    vec3  _LightColor;
    float _Padding0;
    vec3  _SkyColor;
};

// Generate uniformly distributed directions on a sphere using Fibonacci spiral
vec3 GetSphereDirection(int index, int count)
{
    float goldenRatio = (1.0 + sqrt(5.0)) / 2.0;
    float theta = 2.0 * 3.14159265 * float(index) / goldenRatio;
    float phi = acos(1.0 - 2.0 * (float(index) + 0.5) / float(count));
    return vec3(
        cos(theta) * sin(phi),
        cos(phi),
        sin(theta) * sin(phi)
    );
}

void main()
{
    ivec3 probeCoord = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(probeCoord, ivec3(_ProbeResolution))))
        return;

    // Compute probe world position
    vec3 probeWorldPos = _CascadeCenter +
        ((vec3(probeCoord) + 0.5) / float(_ProbeResolution) - 0.5) * 2.0 * _CascadeSize;

    vec4 newSH = vec4(0.0);
    int numRays = 16;

    for (int r = 0; r < numRays; r++)
    {
        vec3 dir = GetSphereDirection(r, numRays);

        float hitDist = MarchSDF(GlobalSDF, probeWorldPos, dir,
                                  _CascadeCenter, _CascadeSize,
                                  _CascadeSize * 2.0, 64);

        vec3 radiance;
        if (hitDist > 0.0)
        {
            // Hit geometry — use direct light contribution at hit point
            float NdotL = max(0.0, dot(-dir, _LightDirection));
            radiance = _LightColor * _LightIntensity * NdotL * 0.3;
        }
        else
        {
            // No hit — sky radiance
            radiance = _SkyColor * 0.5;
        }

        newSH += SHEncodeL1(dir, radiance);
    }
    newSH /= float(numRays);

    // Temporal blend with previous value
    vec4 prevSH = imageLoad(ProbeIrradiance, probeCoord);
    vec4 blendedSH = mix(newSH, prevSH, _Hysteresis);

    imageStore(ProbeIrradiance, probeCoord, blendedSH);
}
