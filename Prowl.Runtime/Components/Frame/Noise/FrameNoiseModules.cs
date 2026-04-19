// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// Multi-layered Perlin noise for procedural camera shake.
/// Equivalent to Cinemachine BasicMultiChannelPerlin.
/// </summary>
public class FrameBasicMultiChannelPerlin : FrameNoiseComponent
{
    public Float3 PositionAmplitude = new(0.02f, 0.02f, 0.02f);
    public Float3 PositionFrequency = new(0.5f, 0.3f, 0.4f);
    public Float3 RotationAmplitude = new(0.3f, 0.3f, 0.1f);
    public Float3 RotationFrequency = new(0.3f, 0.2f, 0.4f);

    [Range(0f, 10f)] public float AmplitudeGain = 1f;
    [Range(0f, 10f)] public float FrequencyGain = 1f;

    private float _time;
    private readonly int _seed = Random.Shared.Next(0, 10000);

    public override void MutateState(ref CameraState state, float deltaTime)
    {
        if (AmplitudeGain <= 0f) return;
        _time += deltaTime * FrequencyGain;

        Float3 posNoise = new(
            PerlinNoise(_time * PositionFrequency.X + _seed) * PositionAmplitude.X,
            PerlinNoise(_time * PositionFrequency.Y + _seed + 100) * PositionAmplitude.Y,
            PerlinNoise(_time * PositionFrequency.Z + _seed + 200) * PositionAmplitude.Z
        );
        state.Position += (state.Orientation * posNoise) * AmplitudeGain;

        float rx = PerlinNoise(_time * RotationFrequency.X + _seed + 300) * RotationAmplitude.X * AmplitudeGain;
        float ry = PerlinNoise(_time * RotationFrequency.Y + _seed + 400) * RotationAmplitude.Y * AmplitudeGain;
        float rz = PerlinNoise(_time * RotationFrequency.Z + _seed + 500) * RotationAmplitude.Z * AmplitudeGain;

        Quaternion noiseRot = Quaternion.FromEuler(new Float3(rx, ry, rz));
        state.Orientation = state.Orientation * noiseRot;
    }

    private static float PerlinNoise(float x)
    {
        int xi = (int)Maths.Floor(x);
        float xf = x - xi;
        float t = xf * xf * (3f - 2f * xf);
        return Maths.Lerp(Hash(xi), Hash(xi + 1), t);
    }

    private static float Hash(int n)
    {
        n = (n << 13) ^ n;
        return 1.0f - ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 1073741824.0f;
    }
}

/// <summary>
/// Impulse-driven shake — apply a decaying impulse from gameplay events.
/// Equivalent to Cinemachine Impulse Source + Listener.
/// </summary>
public class FrameImpulseNoise : FrameNoiseComponent
{
    [Range(0.01f, 5f)] public float DecayTime = 0.3f;
    public Float3 PositionAmplitude = new(0.05f, 0.05f, 0.05f);
    public Float3 RotationAmplitude = new(0.5f, 0.5f, 0.2f);

    private float _impulseStrength;
    private float _impulseTime;

    public void GenerateImpulse(Float3 direction, float force = 1f)
    {
        _impulseStrength = force;
        _impulseTime = 0f;
    }

    public override void MutateState(ref CameraState state, float deltaTime)
    {
        if (_impulseStrength <= 0.001f) return;

        _impulseTime += deltaTime;
        float envelope = _impulseStrength * Maths.Exp(-_impulseTime / Maths.Max(DecayTime, 0.001f));

        if (envelope < 0.001f) { _impulseStrength = 0f; return; }

        float t = _impulseTime * 30f;
        Float3 posNoise = new(
            Maths.Sin(t * 1.1f) * PositionAmplitude.X,
            Maths.Sin(t * 1.3f + 1f) * PositionAmplitude.Y,
            Maths.Sin(t * 0.9f + 2f) * PositionAmplitude.Z
        );
        state.Position += posNoise * envelope;

        float rx = Maths.Sin(t * 1.7f + 3f) * RotationAmplitude.X * envelope;
        float ry = Maths.Sin(t * 1.5f + 4f) * RotationAmplitude.Y * envelope;
        float rz = Maths.Sin(t * 1.2f + 5f) * RotationAmplitude.Z * envelope;
        state.Orientation = state.Orientation * Quaternion.FromEuler(new Float3(rx, ry, rz));
    }
}
