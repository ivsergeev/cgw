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

        if (source is JsonObject)
            return FilterObject(source, paths);

        return source.DeepClone();
    }

    private static JsonObject FilterObject(JsonNode source, string[] paths)
    {
        var result = new JsonObject();

        foreach (var path in paths)
        {
            var segments = path.Split('.');
            MergePath(result, source, segments, 0);
        }

        return result;
    }

    private static void MergePath(JsonNode target, JsonNode source, string[] segments, int index)
    {
        if (index >= segments.Length)
            return;

        var key = segments[index];
        var sourceObj = source as JsonObject;
        if (sourceObj == null || !sourceObj.ContainsKey(key))
            return;

        var value = sourceObj[key];
        var isLeaf = index == segments.Length - 1;

        if (isLeaf)
        {
            // Leaf — copy value if not already present
            var targetObj = target as JsonObject;
            if (targetObj != null && !targetObj.ContainsKey(key))
                targetObj[key] = value?.DeepClone();
            return;
        }

        if (value is JsonArray sourceArr)
        {
            // Array traversal: merge remaining path into each element by index
            var targetObj = target as JsonObject;
            if (targetObj == null) return;

            if (!targetObj.ContainsKey(key))
            {
                // First path through this array — create skeleton with empty objects
                var newArr = new JsonArray();
                for (int i = 0; i < sourceArr.Count; i++)
                {
                    var item = sourceArr[i];
                    if (item is JsonObject itemObj)
                    {
                        var itemResult = new JsonObject();
                        MergePath(itemResult, itemObj, segments, index + 1);
                        newArr.Add(itemResult);
                    }
                    else if (item == null)
                        newArr.Add(null);
                    else
                        newArr.Add(item.DeepClone());
                }
                targetObj[key] = newArr;
            }
            else if (targetObj[key] is JsonArray existingArr)
            {
                // Subsequent paths — merge into existing elements by index
                for (int i = 0; i < sourceArr.Count && i < existingArr.Count; i++)
                {
                    var srcItem = sourceArr[i];
                    var tgtItem = existingArr[i];
                    if (srcItem is JsonObject srcObj && tgtItem is JsonObject tgtObj)
                        MergePath(tgtObj, srcObj, segments, index + 1);
                }
            }
            return;
        }

        if (value is JsonObject)
        {
            // Nested object — ensure intermediate exists in target
            var targetObj = target as JsonObject;
            if (targetObj == null) return;

            if (!targetObj.ContainsKey(key) || targetObj[key] is not JsonObject)
                targetObj[key] = new JsonObject();

            MergePath(targetObj[key]!, value, segments, index + 1);
            return;
        }
    }
}
