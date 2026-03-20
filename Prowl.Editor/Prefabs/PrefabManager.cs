// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Serialization;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Prowl.Editor.Services;

namespace Prowl.Editor.Prefabs;

/// <summary>
/// Manages prefab creation, saving, and instantiation.
/// Prefabs are stored as JSON files (.prefab) containing serialized
/// GameObject hierarchy data.
/// </summary>
public sealed class PrefabManager
{
    public const string PrefabExtension = ".prefab";

    /// <summary>
    /// Creates a prefab asset file from a GameObject hierarchy.
    /// Saves the hierarchy to the given absolute path.
    /// </summary>
    public void CreatePrefab(GameObject source, string absolutePath)
    {
        var data = SerializeHierarchy(source);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        string json = JsonSerializer.Serialize(data, options);
        File.WriteAllText(absolutePath, json);
        Debug.Log($"[Prefab] Created prefab: {absolutePath}");
    }

    /// <summary>
    /// Instantiates a GameObject hierarchy from a prefab file.
    /// Returns the root GameObject (not yet added to a scene).
    /// </summary>
    public GameObject? InstantiatePrefab(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            Debug.LogError($"[Prefab] File not found: {absolutePath}");
            return null;
        }

        string json = File.ReadAllText(absolutePath);
        var data = JsonSerializer.Deserialize<PrefabData>(json);
        if (data == null) return null;

        var go = DeserializeHierarchy(data);
        Debug.Log($"[Prefab] Instantiated from: {absolutePath}");
        return go;
    }

    /// <summary>
    /// Instantiates a prefab and adds it to the current scene.
    /// </summary>
    public GameObject? InstantiatePrefabInScene(string absolutePath, ISceneService sceneService)
    {
        var go = InstantiatePrefab(absolutePath);
        if (go == null) return null;

        if (sceneService.CurrentScene == null)
            sceneService.CreateNewScene();

        sceneService.CurrentScene!.Add(go);
        return go;
    }

    private static PrefabData SerializeHierarchy(GameObject go)
    {
        var data = new PrefabData
        {
            Name = go.Name,
            LocalPosition = Vec3(go.Transform.LocalPosition),
            LocalEulerAngles = Vec3(go.Transform.LocalEulerAngles),
            LocalScale = Vec3(go.Transform.LocalScale),
            Components = [],
            Children = [],
        };

        // Serialize components (type names + serializable field values)
        foreach (var comp in go.GetComponents())
        {
            var compData = new PrefabComponentData
            {
                TypeName = comp.GetType().AssemblyQualifiedName ?? comp.GetType().FullName ?? comp.GetType().Name,
                Fields = SerializeFields(comp),
            };
            data.Components.Add(compData);
        }

        foreach (var child in go.Children)
        {
            if (!child.IsDisposed)
                data.Children.Add(SerializeHierarchy(child));
        }

        return data;
    }

    private static Dictionary<string, object?> SerializeFields(MonoBehaviour comp)
    {
        var fields = new Dictionary<string, object?>();
        var serializableFields = RuntimeUtils.GetSerializableFields(comp);

        foreach (var field in serializableFields)
        {
            var value = field.GetValue(comp);
            if (value == null)
            {
                fields[field.Name] = null;
                continue;
            }

            var type = value.GetType();
            if (type == typeof(float) || type == typeof(double) ||
                type == typeof(int) || type == typeof(long) ||
                type == typeof(bool) || type == typeof(string))
            {
                fields[field.Name] = value;
            }
            else if (type == typeof(Float3))
            {
                var v = (Float3)value;
                fields[field.Name] = new float[] { v.X, v.Y, v.Z };
            }
            else if (type == typeof(Float2))
            {
                var v = (Float2)value;
                fields[field.Name] = new float[] { v.X, v.Y };
            }
            // Skip complex types for now
        }

        return fields;
    }

    private static GameObject DeserializeHierarchy(PrefabData data)
    {
        var go = new GameObject(data.Name ?? "Prefab");
        go.Transform.LocalPosition = ToFloat3(data.LocalPosition);
        go.Transform.LocalEulerAngles = ToFloat3(data.LocalEulerAngles);
        go.Transform.LocalScale = ToFloat3(data.LocalScale);

        // Restore components
        if (data.Components != null)
        {
            foreach (var compData in data.Components)
            {
                if (string.IsNullOrEmpty(compData.TypeName)) continue;

                Type? compType = RuntimeUtils.FindType(compData.TypeName);
                if (compType == null || !typeof(MonoBehaviour).IsAssignableFrom(compType))
                    continue;

                var comp = go.AddComponent(compType);
                if (comp != null && compData.Fields != null)
                {
                    ApplyFields(comp, compData.Fields);
                }
            }
        }

        if (data.Children != null)
        {
            foreach (var childData in data.Children)
            {
                var child = DeserializeHierarchy(childData);
                child.SetParent(go, false);
            }
        }

        return go;
    }

    private static void ApplyFields(MonoBehaviour comp, Dictionary<string, object?> fields)
    {
        var serializableFields = RuntimeUtils.GetSerializableFields(comp);
        foreach (var field in serializableFields)
        {
            if (!fields.TryGetValue(field.Name, out var value) || value == null)
                continue;

            try
            {
                if (field.FieldType == typeof(float) && value is JsonElement je1 && je1.ValueKind == JsonValueKind.Number)
                    field.SetValue(comp, je1.GetSingle());
                else if (field.FieldType == typeof(double) && value is JsonElement je2 && je2.ValueKind == JsonValueKind.Number)
                    field.SetValue(comp, je2.GetDouble());
                else if (field.FieldType == typeof(int) && value is JsonElement je3 && je3.ValueKind == JsonValueKind.Number)
                    field.SetValue(comp, je3.GetInt32());
                else if (field.FieldType == typeof(bool) && value is JsonElement je4)
                    field.SetValue(comp, je4.GetBoolean());
                else if (field.FieldType == typeof(string) && value is JsonElement je5 && je5.ValueKind == JsonValueKind.String)
                    field.SetValue(comp, je5.GetString());
                else if (value is JsonElement)
                {
                    // Skip unsupported JsonElement types
                }
                else
                {
                    field.SetValue(comp, Convert.ChangeType(value, field.FieldType));
                }
            }
            catch
            {
                // Silently skip fields that can't be deserialized
            }
        }
    }

    private static float[] Vec3(Float3 v) => [v.X, v.Y, v.Z];

    private static Float3 ToFloat3(float[]? arr)
    {
        if (arr == null || arr.Length < 3) return Float3.One;
        return new Float3(arr[0], arr[1], arr[2]);
    }
}

/// <summary> Serialized prefab data for a single GameObject. </summary>
public sealed class PrefabData
{
    public string? Name { get; set; }
    public float[]? LocalPosition { get; set; }
    public float[]? LocalEulerAngles { get; set; }
    public float[]? LocalScale { get; set; }
    public List<PrefabComponentData>? Components { get; set; }
    public List<PrefabData>? Children { get; set; }
}

/// <summary> Serialized component data within a prefab. </summary>
public sealed class PrefabComponentData
{
    public string? TypeName { get; set; }
    public Dictionary<string, object?>? Fields { get; set; }
}
