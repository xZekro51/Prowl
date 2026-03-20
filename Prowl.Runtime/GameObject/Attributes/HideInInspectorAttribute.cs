// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime;

/// <summary>
/// Hides a field from the Inspector panel.
/// The field is still serialized normally; it simply won't be displayed
/// in the editor's default inspector view.
/// In Debug mode the field becomes visible again.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class HideInInspectorAttribute : Attribute
{
}
