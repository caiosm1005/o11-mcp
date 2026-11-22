using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ServiceStudioBridge;

/// <summary>
/// Turns Service Studio's objects into JSON by reflection, without JsonSerializer (whose
/// reflection mode a host can switch off). Byte arrays become their length, Service Studio's
/// object keys become their ID, members whose name looks like a secret are left out, and list
/// length and the depth of Service Studio's own object graphs are capped.
/// </summary>
internal static partial class Json
{
    private const int MaxDepth = 4;
    private const int MaxItems = 5000;

    public static JsonNode? ToNode(object? value) => ToNode(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));

    public static string Write(JsonNode? node)
    {
        if (node == null)
        {
            return "null";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [GeneratedRegex("secret|password|token|credential|cookie", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    /// <summary>Types the bridge builds its results from: its own (anonymous types included) and standard collections.</summary>
    private static bool IsOwn(Type type) =>
        type.Assembly == typeof(Json).Assembly || (type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) ?? false);

    /// <summary>Service Studio's ObjectKey (or a subclass of it), or anything shaped like one.</summary>
    private static bool IsObjectKey(Type type)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            if (t.Name == "ObjectKey")
            {
                return true;
            }
        }

        return type.GetProperty("AsGuid") != null && type.GetProperty("IsLocal") != null;
    }

    /// <summary>A global key as its GUID; a local one as its parent's ID and its local name.</summary>
    private static string KeyText(object key)
    {
        var guid = Reflect.TryGet(key, "AsGuid")?.ToString();
        if (Reflect.TryGet(key, "IsLocal") is not true)
        {
            return guid ?? key.ToString() ?? string.Empty;
        }

        var parent = Reflect.TryGet(key, "Parent");
        var parentText = parent != null && IsObjectKey(parent.GetType()) ? KeyText(parent) : parent?.ToString();
        return $"{parentText}/{Reflect.TryGet(key, "LocalPart")}";
    }

    private static JsonNode? ToNode(object? value, int depth, HashSet<object> path)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.DeepClone();
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case Enum e:
                return JsonValue.Create(e.ToString());
            case int i:
                return JsonValue.Create(i);
            case long l:
                return JsonValue.Create(l);
            case short or ushort or byte or sbyte or uint:
                return JsonValue.Create(Convert.ToInt64(value));
            case ulong ul:
                return JsonValue.Create(ul);
            case decimal m:
                return JsonValue.Create(m);
            case double d:
                return double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString());
            case float f:
                return float.IsFinite(f) ? JsonValue.Create(f) : JsonValue.Create(f.ToString());
            case char c:
                return JsonValue.Create(c.ToString());
            case DateTime dt:
                return dt == default ? null : JsonValue.Create(dt.ToString("o"));
            case DateTimeOffset dto:
                return JsonValue.Create(dto.ToString("o"));
            case Guid g:
                return JsonValue.Create(g.ToString());
            case TimeSpan ts:
                return JsonValue.Create(ts.ToString());
            case byte[] bytes:
                return new JsonObject { ["bytes"] = bytes.Length };
        }

        var type = value.GetType();
        if (IsObjectKey(type))
        {
            return JsonValue.Create(KeyText(value));
        }

        // The depth cap is for Service Studio's object graphs: the bridge's own wrappers (pages,
        // picked fields, lists) don't count toward it.
        var own = IsOwn(type);
        if ((depth >= MaxDepth && !own) || type.Name == "Hash")
        {
            return JsonValue.Create(value.ToString());
        }

        var next = own ? depth : depth + 1;

        if (!type.IsValueType && !path.Add(value))
        {
            return JsonValue.Create("(cycle)");
        }

        try
        {
            if (value is IDictionary dictionary)
            {
                var map = new JsonObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = entry.Key.ToString() ?? string.Empty;
                    if (!SensitiveName().IsMatch(key) && !map.ContainsKey(key))
                    {
                        map[key] = ToNode(entry.Value, next, path);
                    }
                }

                return map;
            }

            if (value is IEnumerable sequence)
            {
                var array = new JsonArray();
                foreach (var item in sequence)
                {
                    if (array.Count >= MaxItems)
                    {
                        array.Add((JsonNode?)JsonValue.Create("(truncated)"));
                        break;
                    }

                    array.Add(ToNode(item, next, path));
                }

                return array;
            }

            var result = new JsonObject();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0 || SensitiveName().IsMatch(property.Name) || result.ContainsKey(property.Name))
                {
                    continue;
                }

                object? member;
                try
                {
                    member = property.GetValue(value);
                }
                catch
                {
                    continue;
                }

                result[property.Name] = ToNode(member, next, path);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!SensitiveName().IsMatch(field.Name) && !result.ContainsKey(field.Name))
                {
                    result[field.Name] = ToNode(field.GetValue(value), next, path);
                }
            }

            return result;
        }
        finally
        {
            if (!type.IsValueType)
            {
                path.Remove(value);
            }
        }
    }
}
