using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CorpGateway.Services;

/// <summary>
/// Filters JSON responses by a comma-separated whitelist of dot-notation paths.
/// Preserves the original JSON structure (nesting).
/// Supports array traversal: "items.name" extracts "name" from each element of "items" array.
/// </summary>
public static class JsonFilterHelper
{
    /// <summary>
    /// Applies a whitelist filter to a JSON string.
    /// Returns the filtered JSON string, or the original if filter is empty or parsing fails.
    /// </summary>
    public static string ApplyFilter(string json, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return json;

        var paths = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Length == 0)
            return json;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return json; }

        if (root == null)
            return json;

        var result = FilterNode(root, paths);
        return result?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "{}";
    }

    private static JsonNode? FilterNode(JsonNode source, string[] paths)
    {
        if (source is JsonArray arr)
            return FilterArray(arr, paths);

        if (source is JsonObject)
            return FilterObject(source, paths);

        // Primitive — return as-is if any path matches
        return source.DeepClone();
    }

    private static JsonNode FilterArray(JsonArray arr, string[] paths)
    {
        var result = new JsonArray();
        foreach (var item in arr)
        {
            if (item == null) continue;
            var filtered = FilterNode(item, paths);
            if (filtered != null)
                result.Add(filtered);
        }
        return result;
    }

    private static JsonObject FilterObject(JsonNode source, string[] paths)
    {
        var result = new JsonObject();

        foreach (var path in paths)
        {
            var segments = path.Split('.');
            SetValueByPath(result, source, segments, 0);
        }

        return result;
    }

    private static void SetValueByPath(JsonObject target, JsonNode source, string[] segments, int index)
    {
        if (index >= segments.Length)
            return;

        var key = segments[index];
        var sourceObj = source as JsonObject;
        if (sourceObj == null || !sourceObj.ContainsKey(key))
            return;

        var value = sourceObj[key];

        if (index == segments.Length - 1)
        {
            // Leaf — copy value
            if (!target.ContainsKey(key))
                target[key] = value?.DeepClone();
            return;
        }

        // Intermediate segment
        var remaining = segments[(index + 1)..];

        if (value is JsonArray arr)
        {
            // Array traversal: apply remaining path to each element
            var filteredArr = new JsonArray();
            foreach (var item in arr)
            {
                if (item is JsonObject itemObj)
                {
                    var itemResult = new JsonObject();
                    SetValueByPath(itemResult, itemObj, segments, index + 1);
                    if (itemResult.Count > 0)
                        filteredArr.Add(itemResult);
                }
                else if (remaining.Length == 0)
                {
                    filteredArr.Add(item?.DeepClone());
                }
            }
            if (!target.ContainsKey(key))
                target[key] = filteredArr;
            else if (target[key] is JsonArray existingArr)
            {
                // Merge arrays — add missing items
                foreach (var item in filteredArr)
                    existingArr.Add(item?.DeepClone());
            }
            return;
        }

        if (value is JsonObject)
        {
            // Nested object — ensure intermediate exists
            if (!target.ContainsKey(key) || target[key] is not JsonObject)
                target[key] = new JsonObject();

            SetValueByPath((JsonObject)target[key]!, value, segments, index + 1);
            return;
        }
    }
}
