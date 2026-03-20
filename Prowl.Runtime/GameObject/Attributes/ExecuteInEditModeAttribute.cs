// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime;

/// <summary>
/// When applied to a <see cref="MonoBehaviour"/> subclass, the component's
/// lifecycle methods (Start, Update, FixedUpdate, LateUpdate, etc.) will
/// execute even when the editor is in edit mode.
/// Without this attribute, those methods only run during play mode.
/// </summary>
/// <remarks>
/// This is analogous to Unity's [ExecuteInEditMode] / [ExecuteAlways] attribute.
/// Rendering components (IRenderable, IRenderableLight) always execute in edit
/// mode regardless of this attribute so that scene previews work correctly.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class ExecuteInEditModeAttribute : Attribute
{
}
