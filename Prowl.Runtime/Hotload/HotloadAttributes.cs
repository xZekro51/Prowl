// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime;

/// <summary>
/// Called on a MonoBehaviour instance after a hotload has migrated its fields.
/// Use this to rebuild runtime caches, re-acquire GPU resources, etc.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OnHotloadedAttribute : Attribute { }

/// <summary>
/// Called on a MonoBehaviour instance before a full hotload unloads the old assembly.
/// Use this to release unmanaged resources that cannot survive ALC unload.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OnCodeCleanupAttribute : Attribute { }

/// <summary>
/// Called on a MonoBehaviour instance after a full hotload loads new assemblies,
/// before field migration. Use this to prepare for incoming state.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OnCodeInitializingAttribute : Attribute { }

/// <summary>
/// When applied to a class, static fields will be automatically reset to default
/// values during a full hotload, preventing stale static state from leaking.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AutoStaticsCleanupAttribute : Attribute { }

/// <summary>
/// Marks a field as being preserved across hotloads even if the field type changes
/// (basic type coercion will be attempted). Without this attribute, fields whose
/// types have changed between old/new assemblies are silently skipped.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class PreserveOnHotloadAttribute : Attribute { }
