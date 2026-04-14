// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Prowl.Echo;
using Prowl.Runtime;

namespace Prowl.Editor.Scripting;

/// <summary>
/// Handles instance migration during full hotloads: serializes live component state,
/// maps old types to new types, and restores field values after assembly reload.
/// </summary>
public static class InstanceMigrationEngine
{
    /// <summary>
    /// Snapshot of a single component's state for migration.
    /// </summary>
    public readonly struct ComponentSnapshot
    {
        /// <summary>The fully-qualified type name of the component.</summary>
        public string TypeName { get; init; }

        /// <summary>The component's Identifier GUID for matching after reload.</summary>
        public Guid Identifier { get; init; }

        /// <summary>Serialized field state.</summary>
        public EchoObject SerializedState { get; init; }

        /// <summary>Whether the component was enabled.</summary>
        public bool Enabled { get; init; }
    }

    /// <summary>
    /// Snapshot of a GameObject and all its components.
    /// </summary>
    public readonly struct GameObjectSnapshot
    {
        public string Name { get; init; }
        public Guid Identifier { get; init; }
        public List<ComponentSnapshot> Components { get; init; }
        public List<GameObjectSnapshot> Children { get; init; }
    }

    /// <summary>
    /// Capture the state of the entire scene hierarchy for migration.
    /// Called before ALC unload during a full hotload.
    /// </summary>
    public static List<GameObjectSnapshot> CaptureSceneState(Runtime.Resources.Scene scene)
    {
        var snapshots = new List<GameObjectSnapshot>();

        foreach (var go in scene.RootObjects)
        {
            snapshots.Add(CaptureGameObject(go));
        }

        HotloadLogger.LogDetail($"Captured state: {CountComponents(snapshots)} components across {CountGameObjects(snapshots)} GameObjects");
        return snapshots;
    }

    /// <summary>
    /// Invoke [OnCodeCleanup] methods on all components in the scene.
    /// Called before assembly unload to let components release resources.
    /// </summary>
    public static void InvokeCodeCleanup(Runtime.Resources.Scene scene)
    {
        int invoked = 0;
        foreach (var go in scene.RootObjects)
        {
            invoked += InvokeCodeCleanupRecursive(go);
        }

        if (invoked > 0)
            HotloadLogger.LogDetail($"Invoked [OnCodeCleanup] on {invoked} component(s).");
    }

    /// <summary>
    /// Invoke [OnCodeInitializing] methods on all components in the scene.
    /// Called after assembly load, before field migration.
    /// </summary>
    public static void InvokeCodeInitializing(Runtime.Resources.Scene scene)
    {
        int invoked = 0;
        foreach (var go in scene.RootObjects)
        {
            invoked += InvokeCodeInitializingRecursive(go);
        }

        if (invoked > 0)
            HotloadLogger.LogDetail($"Invoked [OnCodeInitializing] on {invoked} component(s).");
    }

    /// <summary>
    /// Invoke [OnHotloaded] methods on all components in the scene.
    /// Called after field migration to let components rebuild caches.
    /// </summary>
    public static void InvokeOnHotloaded(Runtime.Resources.Scene scene)
    {
        int invoked = 0;
        foreach (var go in scene.RootObjects)
        {
            invoked += InvokeOnHotloadedRecursive(go);
        }

        if (invoked > 0)
            HotloadLogger.LogDetail($"Invoked [OnHotloaded] on {invoked} component(s).");
    }

    /// <summary>
    /// Reset static fields on types marked with [AutoStaticsCleanup].
    /// Called after loading new assemblies.
    /// </summary>
    public static void CleanupAutoStatics(Assembly[] assemblies)
    {
        int cleaned = 0;

        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var type in types)
            {
                if (!type.IsDefined(typeof(AutoStaticsCleanupAttribute), false)) continue;

                var statics = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var field in statics)
                {
                    if (field.IsInitOnly || field.IsLiteral) continue; // skip readonly/const

                    try
                    {
                        field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
                        cleaned++;
                    }
                    catch (Exception ex)
                    {
                        HotloadLogger.LogTrace($"Could not reset static field {type.Name}.{field.Name}: {ex.Message}");
                    }
                }
            }
        }

        if (cleaned > 0)
            HotloadLogger.LogDetail($"Reset {cleaned} static field(s) marked with [AutoStaticsCleanup].");
    }

    private static GameObjectSnapshot CaptureGameObject(GameObject go)
    {
        var componentSnapshots = new List<ComponentSnapshot>();

        foreach (var comp in go.GetComponents<MonoBehaviour>())
        {
            if (comp.IsNotValid()) continue;

            // Only snapshot user-script components (from the script ALC)
            var compType = comp.GetType();
            if (!IsUserScriptType(compType)) continue;

            try
            {
                var serialized = Serializer.Serialize(comp);
                componentSnapshots.Add(new ComponentSnapshot
                {
                    TypeName = compType.FullName ?? compType.Name,
                    Identifier = comp.Identifier,
                    SerializedState = serialized,
                    Enabled = comp.Enabled,
                });

                HotloadLogger.LogTrace($"  Captured: {compType.Name} on '{go.Name}' (ID: {comp.Identifier})");
            }
            catch (Exception ex)
            {
                HotloadLogger.LogWarning($"Failed to capture {compType.Name} on '{go.Name}': {ex.Message}");
            }
        }

        var children = new List<GameObjectSnapshot>();
        foreach (var child in go.Children)
        {
            if (child.IsValid())
                children.Add(CaptureGameObject(child));
        }

        return new GameObjectSnapshot
        {
            Name = go.Name,
            Identifier = go.Identifier,
            Components = componentSnapshots,
            Children = children,
        };
    }

    /// <summary>
    /// Restore migrated state into the live scene after assembly reload.
    /// Matches components by Identifier GUID and applies field values.
    /// </summary>
    public static MigrationReport RestoreSceneState(
        Runtime.Resources.Scene scene,
        List<GameObjectSnapshot> snapshots,
        Assembly[] newAssemblies)
    {
        var report = new MigrationReport();
        var typeCache = BuildTypeCache(newAssemblies);

        foreach (var goSnapshot in snapshots)
        {
            RestoreGameObject(scene, goSnapshot, typeCache, report);
        }

        return report;
    }

    private static void RestoreGameObject(
        Runtime.Resources.Scene scene,
        GameObjectSnapshot snapshot,
        Dictionary<string, Type> typeCache,
        MigrationReport report)
    {
        // Find the matching GameObject by Identifier
        var go = FindGameObjectByIdentifier(scene, snapshot.Identifier);
        if (go == null)
        {
            report.OrphanedSnapshots++;
            HotloadLogger.LogTrace($"GameObject '{snapshot.Name}' (ID: {snapshot.Identifier}) not found after reload.");
            // Process children anyway — they might exist under a different parent
            foreach (var child in snapshot.Children)
                RestoreGameObject(scene, child, typeCache, report);
            return;
        }

        foreach (var compSnapshot in snapshot.Components)
        {
            // Find the component by Identifier
            var comp = FindComponentByIdentifier(go, compSnapshot.Identifier);

            if (comp == null)
            {
                // Component doesn't exist — try to create it if the type still exists
                if (typeCache.TryGetValue(compSnapshot.TypeName, out var newType))
                {
                    try
                    {
                        comp = (MonoBehaviour)go.AddComponent(newType);
                        comp.Identifier = compSnapshot.Identifier;
                        report.ComponentsRecreated++;
                    }
                    catch (Exception ex)
                    {
                        HotloadLogger.LogWarning($"Failed to recreate {compSnapshot.TypeName} on '{go.Name}': {ex.Message}");
                        report.ComponentsFailed++;
                        continue;
                    }
                }
                else
                {
                    HotloadLogger.LogTrace($"Type '{compSnapshot.TypeName}' no longer exists, skipping.");
                    report.TypesRemoved++;
                    continue;
                }
            }

            // Migrate fields
            MigrateComponent(comp, compSnapshot, report);
        }

        // Recurse into children
        foreach (var child in snapshot.Children)
        {
            RestoreGameObject(scene, child, typeCache, report);
        }
    }

    private static void MigrateComponent(MonoBehaviour comp, ComponentSnapshot snapshot, MigrationReport report)
    {
        var compType = comp.GetType();

        // Check if the component implements IHotloadUpgrader
        if (comp is IHotloadUpgrader upgrader)
        {
            try
            {
                upgrader.OnHotloadUpgrade(snapshot.SerializedState);
                report.ComponentsMigrated++;
                HotloadLogger.LogTrace($"  Custom migration: {compType.Name} via IHotloadUpgrader");
                return;
            }
            catch (Exception ex)
            {
                HotloadLogger.LogWarning($"IHotloadUpgrader failed for {compType.Name}: {ex.Message}");
                report.ComponentsFailed++;
                return;
            }
        }

        // Automatic reflection-based field migration from serialized state
        try
        {
            MigrateFieldsFromSnapshot(comp, compType, snapshot.SerializedState);
            comp.Enabled = snapshot.Enabled;
            report.ComponentsMigrated++;
            HotloadLogger.LogTrace($"  Auto-migrated: {compType.Name}");
        }
        catch (Exception ex)
        {
            HotloadLogger.LogWarning($"Auto-migration failed for {compType.Name}: {ex.Message}");
            report.ComponentsFailed++;
        }
    }

    /// <summary>
    /// Migrate field values from a serialized EchoObject snapshot into a live component
    /// using reflection. Matches fields by name and attempts type-compatible assignment.
    /// Handles edge cases: generics, delegates, type mismatches, and [PreserveOnHotload].
    /// </summary>
    private static void MigrateFieldsFromSnapshot(MonoBehaviour target, Type targetType, EchoObject snapshot)
    {
        if (snapshot.TagType != EchoType.Compound) return;

        var fields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var field in fields)
        {
            // Skip fields marked with [SerializeIgnore]
            if (field.IsDefined(typeof(SerializeIgnoreAttribute), true)) continue;

            // Private fields need [SerializeField] to be included
            if (!field.IsPublic && !field.IsDefined(typeof(SerializeFieldAttribute), true)) continue;

            string fieldName = field.Name;
            if (!snapshot.TryGet(fieldName, out var fieldData)) continue;

            try
            {
                // Skip delegate fields — they cannot be safely migrated across ALCs
                if (typeof(Delegate).IsAssignableFrom(field.FieldType))
                {
                    HotloadLogger.LogTrace($"    Field '{fieldName}' is a delegate type, skipping migration.");
                    continue;
                }

                var value = Serializer.Deserialize(fieldData, field.FieldType);
                if (value != null || !field.FieldType.IsValueType)
                {
                    field.SetValue(target, value);
                }
            }
            catch (Exception ex)
            {
                // If [PreserveOnHotload] is set, attempt basic type coercion
                if (field.IsDefined(typeof(PreserveOnHotloadAttribute), true))
                {
                    try
                    {
                        var rawValue = DeserializeAsObject(fieldData);
                        if (rawValue != null)
                        {
                            var converted = Convert.ChangeType(rawValue, field.FieldType);
                            field.SetValue(target, converted);
                            HotloadLogger.LogTrace($"    Field '{fieldName}' coerced via [PreserveOnHotload].");
                            continue;
                        }
                    }
                    catch
                    {
                        HotloadLogger.LogTrace($"    Field '{fieldName}' [PreserveOnHotload] coercion failed.");
                    }
                }

                HotloadLogger.LogTrace($"    Field '{fieldName}' migration skipped: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Attempt to extract a primitive value from an EchoObject for type coercion.
    /// </summary>
    private static object? DeserializeAsObject(EchoObject data)
    {
        return data.TagType switch
        {
            EchoType.String => data.StringValue,
            EchoType.Byte => data.ByteValue,
            EchoType.sByte => data.sByteValue,
            EchoType.Short => data.ShortValue,
            EchoType.Int => data.IntValue,
            EchoType.Long => data.LongValue,
            EchoType.UShort => data.UShortValue,
            EchoType.UInt => data.UIntValue,
            EchoType.ULong => data.ULongValue,
            EchoType.Float => data.FloatValue,
            EchoType.Double => data.DoubleValue,
            EchoType.Bool => data.BoolValue,
            _ => null,
        };
    }

    private static Dictionary<string, Type> BuildTypeCache(Assembly[] assemblies)
    {
        var cache = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var type in types)
            {
                var fullName = type.FullName;
                if (fullName != null)
                    cache[fullName] = type;
            }
        }

        return cache;
    }

    private static GameObject? FindGameObjectByIdentifier(Runtime.Resources.Scene scene, Guid id)
    {
        foreach (var root in scene.RootObjects)
        {
            var found = FindGameObjectByIdentifierRecursive(root, id);
            if (found != null) return found;
        }
        return null;
    }

    private static GameObject? FindGameObjectByIdentifierRecursive(GameObject go, Guid id)
    {
        if (go.Identifier == id) return go;
        foreach (var child in go.Children)
        {
            if (child.IsNotValid()) continue;
            var found = FindGameObjectByIdentifierRecursive(child, id);
            if (found != null) return found;
        }
        return null;
    }

    private static MonoBehaviour? FindComponentByIdentifier(GameObject go, Guid id)
    {
        foreach (var comp in go.GetComponents<MonoBehaviour>())
        {
            if (comp.IsValid() && comp.Identifier == id)
                return comp;
        }
        return null;
    }

    private static bool IsUserScriptType(Type type)
    {
        var asm = type.Assembly;
        // User scripts are loaded in a non-default ALC
        var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm);
        return context != null && context != System.Runtime.Loader.AssemblyLoadContext.Default;
    }

    private static int InvokeCodeCleanupRecursive(GameObject go)
    {
        int count = 0;
        foreach (var comp in go.GetComponents<MonoBehaviour>())
        {
            if (comp.IsNotValid() || !IsUserScriptType(comp.GetType())) continue;
            count += InvokeAttributeMethod<OnCodeCleanupAttribute>(comp);
        }
        foreach (var child in go.Children)
        {
            if (child.IsValid()) count += InvokeCodeCleanupRecursive(child);
        }
        return count;
    }

    private static int InvokeCodeInitializingRecursive(GameObject go)
    {
        int count = 0;
        foreach (var comp in go.GetComponents<MonoBehaviour>())
        {
            if (comp.IsNotValid() || !IsUserScriptType(comp.GetType())) continue;
            count += InvokeAttributeMethod<OnCodeInitializingAttribute>(comp);
        }
        foreach (var child in go.Children)
        {
            if (child.IsValid()) count += InvokeCodeInitializingRecursive(child);
        }
        return count;
    }

    private static int InvokeOnHotloadedRecursive(GameObject go)
    {
        int count = 0;
        foreach (var comp in go.GetComponents<MonoBehaviour>())
        {
            if (comp.IsNotValid() || !IsUserScriptType(comp.GetType())) continue;
            count += InvokeAttributeMethod<OnHotloadedAttribute>(comp);
        }
        foreach (var child in go.Children)
        {
            if (child.IsValid()) count += InvokeOnHotloadedRecursive(child);
        }
        return count;
    }

    private static int InvokeAttributeMethod<TAttribute>(MonoBehaviour comp) where TAttribute : Attribute
    {
        var methods = comp.GetType().GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        int count = 0;
        foreach (var method in methods)
        {
            if (!method.IsDefined(typeof(TAttribute), true)) continue;
            if (method.GetParameters().Length != 0) continue;

            try
            {
                method.Invoke(comp, null);
                count++;
            }
            catch (Exception ex)
            {
                HotloadLogger.LogWarning(
                    $"[{typeof(TAttribute).Name}] failed on {comp.GetType().Name}.{method.Name}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        return count;
    }

    private static int CountComponents(List<GameObjectSnapshot> snapshots)
    {
        int count = 0;
        foreach (var s in snapshots)
        {
            count += s.Components.Count;
            count += CountComponents(s.Children);
        }
        return count;
    }

    private static int CountGameObjects(List<GameObjectSnapshot> snapshots)
    {
        int count = snapshots.Count;
        foreach (var s in snapshots)
            count += CountGameObjects(s.Children);
        return count;
    }
}

/// <summary>
/// Summary report of a migration pass.
/// </summary>
public sealed class MigrationReport
{
    public int ComponentsMigrated { get; set; }
    public int ComponentsRecreated { get; set; }
    public int ComponentsFailed { get; set; }
    public int TypesRemoved { get; set; }
    public int OrphanedSnapshots { get; set; }

    public override string ToString() =>
        $"Migration: {ComponentsMigrated} migrated, {ComponentsRecreated} recreated, " +
        $"{ComponentsFailed} failed, {TypesRemoved} types removed, {OrphanedSnapshots} orphaned";
}
