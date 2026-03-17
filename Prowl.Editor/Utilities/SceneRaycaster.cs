// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Rendering;
using Prowl.Editor.Services;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Editor.Utilities;

/// <summary>
/// Performs raycasting against scene objects for mouse picking in the editor.
/// Tests against world-space axis-aligned bounding boxes of renderable objects
/// and falls back to a default unit-cube AABB for objects without a mesh.
/// </summary>
public static class SceneRaycaster
{
    /// <summary>
    /// Result of a scene pick operation.
    /// </summary>
    public readonly record struct PickResult(GameObject? Hit, float Distance);

    /// <summary>
    /// Picks the closest <see cref="GameObject"/> under the given viewport-local
    /// pixel coordinate. Returns <c>null</c> if nothing was hit.
    /// </summary>
    public static PickResult Pick(Float2 viewportPos, float vpWidth, float vpHeight, SceneCamera camera)
    {
        if (vpWidth <= 0 || vpHeight <= 0) return default;

        var (origin, direction) = camera.ViewportToRay(viewportPos, vpWidth, vpHeight);

        Scene? scene = Scene.Current;
        if (scene == null) return default;

        GameObject? closest = null;
        float closestDist = float.MaxValue;

        foreach (GameObject go in scene.AllObjects)
        {
            if (go.IsDisposed) continue;
            if (go.HideFlags.HasFlag(HideFlags.Hide) ||
                go.HideFlags.HasFlag(HideFlags.HideAndDontSave)) continue;

            // Compute a world-space AABB for this object
            Float3 min, max;
            if (!TryGetWorldBounds(go, out min, out max))
                continue;

            if (RayIntersectsAABB(origin, direction, min, max, out float t) && t < closestDist)
            {
                closestDist = t;
                closest = go;
            }
        }

        return new PickResult(closest, closestDist);
    }

    /// <summary>
    /// Tries to compute a world-space AABB for a <see cref="GameObject"/>.
    /// Uses the MeshRenderer's bounds if available, otherwise a unit cube
    /// centred on the object's position (so cameras, lights, empties can be picked too).
    /// </summary>
    private static bool TryGetWorldBounds(GameObject go, out Float3 min, out Float3 max)
    {
        // Try MeshRenderer first for accurate bounds
        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null && renderer.IsValid() && renderer.Mesh.IsValid())
        {
            renderer.GetCullingData(out bool renderable, out var aabb);
            if (renderable)
            {
                min = aabb.Min;
                max = aabb.Max;
                return true;
            }
        }

        // Fallback: use a small unit AABB at the object's world position
        // This allows picking lights, cameras, empties, etc.
        Float3 pos = go.Transform.Position;
        Float3 halfExt = new(0.5f, 0.5f, 0.5f);
        min = pos - halfExt;
        max = pos + halfExt;
        return true;
    }

    /// <summary>
    /// Slab-method ray vs AABB intersection test.
    /// Returns true if the ray hits, with <paramref name="tHit"/> set to the
    /// distance along the ray. Negative <c>tHit</c> means the origin is inside.
    /// </summary>
    private static bool RayIntersectsAABB(Float3 origin, Float3 dir, Float3 min, Float3 max, out float tHit)
    {
        tHit = 0f;

        float tmin = float.MinValue;
        float tmax = float.MaxValue;

        // X slab
        if (MathF.Abs(dir.X) < 1e-8f)
        {
            if (origin.X < min.X || origin.X > max.X) return false;
        }
        else
        {
            float invD = 1f / dir.X;
            float t1 = (min.X - origin.X) * invD;
            float t2 = (max.X - origin.X) * invD;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return false;
        }

        // Y slab
        if (MathF.Abs(dir.Y) < 1e-8f)
        {
            if (origin.Y < min.Y || origin.Y > max.Y) return false;
        }
        else
        {
            float invD = 1f / dir.Y;
            float t1 = (min.Y - origin.Y) * invD;
            float t2 = (max.Y - origin.Y) * invD;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return false;
        }

        // Z slab
        if (MathF.Abs(dir.Z) < 1e-8f)
        {
            if (origin.Z < min.Z || origin.Z > max.Z) return false;
        }
        else
        {
            float invD = 1f / dir.Z;
            float t1 = (min.Z - origin.Z) * invD;
            float t2 = (max.Z - origin.Z) * invD;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return false;
        }

        // tmin < 0 means origin is inside the box; still a hit
        tHit = tmin >= 0 ? tmin : tmax;
        return tmax >= 0;
    }
}
