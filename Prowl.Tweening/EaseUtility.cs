// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// Evaluates the standard easing equations through a single jump table. Useful on its own when
/// you want an eased value without creating a tween.
/// </summary>
public static class EaseUtility
{
    private const float PI = MathF.PI;
    private const float HalfPI = MathF.PI * 0.5f;
    private const float TwoPI = MathF.PI * 2f;
    private const float DefaultOvershoot = 1.70158f;

    /// <summary>
    /// Evaluates <paramref name="ease"/> at normalized time <paramref name="t"/> (usually 0..1).
    /// </summary>
    /// <param name="ease">The easing equation to sample.</param>
    /// <param name="t">Normalized time, 0 at the start of the cycle and 1 at its end.</param>
    /// <param name="overshoot">Overshoot for Back eases, amplitude for Elastic eases.</param>
    /// <param name="period">Period for Elastic eases; 0 uses the default (0.3).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Evaluate(Ease ease, float t, float overshoot = DefaultOvershoot, float period = 0f)
    {
        switch (ease)
        {
            case Ease.Linear: return t;

            case Ease.InSine: return 1f - MathF.Cos(t * HalfPI);
            case Ease.OutSine: return MathF.Sin(t * HalfPI);
            case Ease.InOutSine: return -0.5f * (MathF.Cos(PI * t) - 1f);

            case Ease.InQuad: return t * t;
            case Ease.OutQuad: return -t * (t - 2f);
            case Ease.InOutQuad:
                if ((t *= 2f) < 1f) return 0.5f * t * t;
                return -0.5f * (--t * (t - 2f) - 1f);

            case Ease.InCubic: return t * t * t;
            case Ease.OutCubic: t -= 1f; return t * t * t + 1f;
            case Ease.InOutCubic:
                if ((t *= 2f) < 1f) return 0.5f * t * t * t;
                t -= 2f;
                return 0.5f * (t * t * t + 2f);

            case Ease.InQuart: return t * t * t * t;
            case Ease.OutQuart: t -= 1f; return -(t * t * t * t - 1f);
            case Ease.InOutQuart:
                if ((t *= 2f) < 1f) return 0.5f * t * t * t * t;
                t -= 2f;
                return -0.5f * (t * t * t * t - 2f);

            case Ease.InQuint: return t * t * t * t * t;
            case Ease.OutQuint: t -= 1f; return t * t * t * t * t + 1f;
            case Ease.InOutQuint:
                if ((t *= 2f) < 1f) return 0.5f * t * t * t * t * t;
                t -= 2f;
                return 0.5f * (t * t * t * t * t + 2f);

            case Ease.InExpo: return t <= 0f ? 0f : MathF.Pow(2f, 10f * (t - 1f));
            case Ease.OutExpo: return t >= 1f ? 1f : -MathF.Pow(2f, -10f * t) + 1f;
            case Ease.InOutExpo:
                if (t <= 0f) return 0f;
                if (t >= 1f) return 1f;
                if ((t *= 2f) < 1f) return 0.5f * MathF.Pow(2f, 10f * (t - 1f));
                return 0.5f * (-MathF.Pow(2f, -10f * --t) + 2f);

            case Ease.InCirc: return -(MathF.Sqrt(1f - t * t) - 1f);
            case Ease.OutCirc: t -= 1f; return MathF.Sqrt(1f - t * t);
            case Ease.InOutCirc:
                if ((t *= 2f) < 1f) return -0.5f * (MathF.Sqrt(1f - t * t) - 1f);
                t -= 2f;
                return 0.5f * (MathF.Sqrt(1f - t * t) + 1f);

            case Ease.InElastic: return InElastic(t, overshoot, period);
            case Ease.OutElastic: return OutElastic(t, overshoot, period);
            case Ease.InOutElastic: return InOutElastic(t, overshoot, period);

            case Ease.InBack: return t * t * ((overshoot + 1f) * t - overshoot);
            case Ease.OutBack: t -= 1f; return t * t * ((overshoot + 1f) * t + overshoot) + 1f;
            case Ease.InOutBack:
                overshoot *= 1.525f;
                if ((t *= 2f) < 1f) return 0.5f * (t * t * ((overshoot + 1f) * t - overshoot));
                t -= 2f;
                return 0.5f * (t * t * ((overshoot + 1f) * t + overshoot) + 2f);

            case Ease.InBounce: return 1f - OutBounce(1f - t);
            case Ease.OutBounce: return OutBounce(t);
            case Ease.InOutBounce:
                if (t < 0.5f) return (1f - OutBounce(1f - t * 2f)) * 0.5f;
                return OutBounce(t * 2f - 1f) * 0.5f + 0.5f;

            default: return t;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float OutBounce(float t)
    {
        const float N = 7.5625f;
        const float D = 2.75f;
        if (t < 1f / D) return N * t * t;
        if (t < 2f / D) { t -= 1.5f / D; return N * t * t + 0.75f; }
        if (t < 2.5f / D) { t -= 2.25f / D; return N * t * t + 0.9375f; }
        t -= 2.625f / D;
        return N * t * t + 0.984375f;
    }

    private static float InElastic(float t, float amplitude, float period)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        if (period == 0f) period = 0.3f;

        float s;
        if (amplitude < 1f) { amplitude = 1f; s = period * 0.25f; }
        else s = period / TwoPI * MathF.Asin(1f / amplitude);

        t -= 1f;
        return -(amplitude * MathF.Pow(2f, 10f * t) * MathF.Sin((t - s) * TwoPI / period));
    }

    private static float OutElastic(float t, float amplitude, float period)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        if (period == 0f) period = 0.3f;

        float s;
        if (amplitude < 1f) { amplitude = 1f; s = period * 0.25f; }
        else s = period / TwoPI * MathF.Asin(1f / amplitude);

        return amplitude * MathF.Pow(2f, -10f * t) * MathF.Sin((t - s) * TwoPI / period) + 1f;
    }

    private static float InOutElastic(float t, float amplitude, float period)
    {
        if (t <= 0f) return 0f;
        if ((t *= 2f) >= 2f) return 1f;
        if (period == 0f) period = 0.3f * 1.5f;

        float s;
        if (amplitude < 1f) { amplitude = 1f; s = period * 0.25f; }
        else s = period / TwoPI * MathF.Asin(1f / amplitude);

        if (t < 1f)
        {
            t -= 1f;
            return -0.5f * (amplitude * MathF.Pow(2f, 10f * t) * MathF.Sin((t - s) * TwoPI / period));
        }

        t -= 1f;
        return amplitude * MathF.Pow(2f, -10f * t) * MathF.Sin((t - s) * TwoPI / period) * 0.5f + 1f;
    }
}
