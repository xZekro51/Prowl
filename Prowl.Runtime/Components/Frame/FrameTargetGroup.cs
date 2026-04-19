// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Prowl.Echo;
using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// Defines a group of weighted targets for group framing.
/// Equivalent to Cinemachine TargetGroup.
/// </summary>
[AddComponentMenu("Frame/Target Group")]
public class FrameTargetGroup : MonoBehaviour
{
    [Serializable]
    public struct Target
    {
        [SerializeIgnore] public Transform? Transform;
        public float Weight;
        public float Radius;
    }

    [SerializeField] private List<Target> _targets = [];

    public int Count => _targets.Count;
    public bool IsEmpty => _targets.Count == 0;
    public Float3 GroupCenter { get; private set; }
    public float GroupRadius { get; private set; }

    public void AddMember(Transform transform, float weight = 1f, float radius = 0f)
    {
        _targets.Add(new Target { Transform = transform, Weight = weight, Radius = radius });
    }

    public bool RemoveMember(Transform transform)
    {
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            if (_targets[i].Transform == transform) { _targets.RemoveAt(i); return true; }
        }
        return false;
    }

    public void Clear() => _targets.Clear();

    public override void LateUpdate() => Recalculate();

    public void Recalculate()
    {
        if (_targets.Count == 0) { GroupCenter = Transform.Position; GroupRadius = 0f; return; }

        Float3 center = Float3.Zero;
        float totalWeight = 0f;
        var span = CollectionsMarshal.AsSpan(_targets);

        for (int i = 0; i < span.Length; i++)
        {
            ref Target t = ref span[i];
            if (t.Transform == null || t.Weight <= 0f) continue;
            center += t.Transform.Position * t.Weight;
            totalWeight += t.Weight;
        }

        center = totalWeight > 0f ? center / totalWeight : Transform.Position;
        GroupCenter = center;

        float maxDist = 0f;
        for (int i = 0; i < span.Length; i++)
        {
            ref Target t = ref span[i];
            if (t.Transform == null || t.Weight <= 0f) continue;
            float dist = Float3.Length(t.Transform.Position - center) + t.Radius;
            if (dist > maxDist) maxDist = dist;
        }
        GroupRadius = maxDist;
    }
}
