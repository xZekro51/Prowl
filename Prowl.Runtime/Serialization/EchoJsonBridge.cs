// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Text.Json;
using System.Text.Json.Nodes;

using Prowl.Echo;

namespace Prowl.Runtime.Serialization;

/// <summary>
/// Bidirectional bridge between <see cref="EchoObject"/> and
/// <see cref="System.Text.Json.Nodes.JsonNode"/>. Used by both the editor
/// serializers and the runtime asset/scene loaders so that the conversion
/// logic is not duplicated.
/// </summary>
public static class EchoJsonBridge
{
    // ── EchoObject → JsonNode ────────────────────────────────────

    public static JsonNode? EchoToJson(EchoObject echo)
    {
        switch (echo.TagType)
        {
            case EchoType.Null:
                return null;

            case EchoType.Byte:
                return JsonValue.Create(echo.ByteValue);
            case EchoType.sByte:
                return JsonValue.Create((int)echo.Value);
            case EchoType.Short:
                return JsonValue.Create(echo.ShortValue);
            case EchoType.UShort:
                return JsonValue.Create((int)(ushort)echo.Value);
            case EchoType.Int:
                return JsonValue.Create(echo.IntValue);
            case EchoType.UInt:
                return JsonValue.Create((long)(uint)echo.Value);
            case EchoType.Long:
                return JsonValue.Create(echo.LongValue);
            case EchoType.ULong:
                // JSON doesn't have unsigned 64-bit; store as string
                return JsonValue.Create(((ulong)echo.Value).ToString());
            case EchoType.Float:
                return JsonValue.Create(echo.FloatValue);
            case EchoType.Double:
                return JsonValue.Create(echo.DoubleValue);
            case EchoType.Bool:
                return JsonValue.Create(echo.BoolValue);
            case EchoType.String:
                return JsonValue.Create(echo.StringValue);

            case EchoType.ByteArray:
                return JsonValue.Create(Convert.ToBase64String(echo.ByteArrayValue));

            case EchoType.List:
            {
                var arr = new JsonArray();
                foreach (EchoObject item in echo.List)
                {
                    var wrapper = new JsonObject
                    {
                        ["$echoType"] = (int)item.TagType,
                        ["$value"] = EchoToJson(item)
                    };
                    arr.Add(wrapper);
                }
                return arr;
            }

            case EchoType.Compound:
            {
                var obj = new JsonObject();
                obj["$echoType"] = (int)EchoType.Compound;
                foreach (var kvp in echo.Tags)
                {
                    var wrapper = new JsonObject
                    {
                        ["$echoType"] = (int)kvp.Value.TagType,
                        ["$value"] = EchoToJson(kvp.Value)
                    };
                    obj[kvp.Key] = wrapper;
                }
                return obj;
            }

            default:
                return JsonValue.Create(echo.ToString());
        }
    }

    // ── JsonNode → EchoObject ────────────────────────────────────

    public static EchoObject JsonToEcho(JsonNode node)
    {
        if (node is JsonObject jobj)
        {
            // Check if this is a typed wrapper
            if (jobj.ContainsKey("$echoType") && jobj.ContainsKey("$value"))
            {
                int typeId = jobj["$echoType"]!.GetValue<int>();
                var echoType = (EchoType)typeId;
                JsonNode? valueNode = jobj["$value"];
                return ReconstructEcho(echoType, valueNode);
            }

            // If $echoType is present but no $value, it's a compound with keys
            if (jobj.ContainsKey("$echoType"))
            {
                int typeId = jobj["$echoType"]!.GetValue<int>();
                if ((EchoType)typeId == EchoType.Compound)
                {
                    var compound = EchoObject.NewCompound();
                    foreach (var kvp in jobj)
                    {
                        if (kvp.Key == "$echoType") continue;
                        if (kvp.Value != null)
                            compound.Add(kvp.Key, JsonToEcho(kvp.Value));
                    }
                    return compound;
                }
            }

            // Fallback: treat as a compound
            var fallback = EchoObject.NewCompound();
            foreach (var kvp in jobj)
            {
                if (kvp.Value != null)
                    fallback.Add(kvp.Key, JsonToEcho(kvp.Value));
            }
            return fallback;
        }

        if (node is JsonArray jarr)
        {
            var list = EchoObject.NewList();
            foreach (JsonNode? item in jarr)
            {
                if (item != null)
                    list.ListAdd(JsonToEcho(item));
                else
                    list.ListAdd(new EchoObject());
            }
            return list;
        }

        if (node is JsonValue jval)
        {
            if (jval.TryGetValue<bool>(out bool bv)) return new EchoObject(bv);
            if (jval.TryGetValue<int>(out int iv)) return new EchoObject(iv);
            if (jval.TryGetValue<long>(out long lv)) return new EchoObject(lv);
            if (jval.TryGetValue<float>(out float fv)) return new EchoObject(fv);
            if (jval.TryGetValue<double>(out double dv)) return new EchoObject(dv);
            if (jval.TryGetValue<string>(out string? sv)) return new EchoObject(sv ?? "");
        }

        return new EchoObject();
    }

    public static EchoObject ReconstructEcho(EchoType type, JsonNode? valueNode)
    {
        if (valueNode == null)
            return new EchoObject();

        switch (type)
        {
            case EchoType.Null:
                return new EchoObject();
            case EchoType.Byte:
                return new EchoObject((byte)valueNode.GetValue<int>());
            case EchoType.sByte:
                return new EchoObject((sbyte)valueNode.GetValue<int>());
            case EchoType.Short:
                return new EchoObject((short)valueNode.GetValue<int>());
            case EchoType.UShort:
                return new EchoObject((ushort)valueNode.GetValue<int>());
            case EchoType.Int:
                return new EchoObject(valueNode.GetValue<int>());
            case EchoType.UInt:
                return new EchoObject((uint)valueNode.GetValue<long>());
            case EchoType.Long:
                return new EchoObject(valueNode.GetValue<long>());
            case EchoType.ULong:
                return new EchoObject(ulong.Parse(valueNode.GetValue<string>()));
            case EchoType.Float:
                return new EchoObject(valueNode.GetValue<float>());
            case EchoType.Double:
                return new EchoObject(valueNode.GetValue<double>());
            case EchoType.Bool:
                return new EchoObject(valueNode.GetValue<bool>());
            case EchoType.String:
                return new EchoObject(valueNode.GetValue<string>() ?? "");
            case EchoType.ByteArray:
                return new EchoObject(Convert.FromBase64String(valueNode.GetValue<string>()));

            case EchoType.List:
            {
                var list = EchoObject.NewList();
                if (valueNode is JsonArray arr)
                {
                    foreach (JsonNode? item in arr)
                    {
                        if (item != null)
                            list.ListAdd(JsonToEcho(item));
                        else
                            list.ListAdd(new EchoObject());
                    }
                }
                return list;
            }

            case EchoType.Compound:
            {
                if (valueNode is JsonObject obj)
                    return JsonToEcho(obj);
                return EchoObject.NewCompound();
            }

            default:
                return new EchoObject();
        }
    }
}
