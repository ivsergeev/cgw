using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CorpGateway.Models;

public enum ParameterType
{
    String,
    Integer,
    Float,
    Boolean,
    Date
}

public class SkillParameter
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ParameterType Type { get; set; } = ParameterType.String;
    public bool Required { get; set; } = true;
    public string? DefaultValue { get; set; }
}

public class Skill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string Url { get; set; } = "";
    public string HttpMethod { get; set; } = "GET";
    public List<SkillParameter> Parameters { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// JSON template for request body. Parameters are substituted as {{name}}.
    /// If empty, remaining parameters are sent as flat JSON object.
    /// Example: {"body":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{comment}}"}]}]}}
    /// </summary>
    public string BodyTemplate { get; set; } = "";

    /// <summary>
    /// Origin (scheme + host) from which to execute fetch().
    /// Use when the API endpoint is on a different domain than the SPA that holds the auth session.
    /// If empty, the origin is extracted from the Url automatically.
    /// </summary>
    public string FetchOrigin { get; set; } = "";

    /// <summary>
    /// Comma-separated list of dot-notation paths to keep in the response.
    /// Example: "key, fields.summary, fields.status.name, fields.assignee.displayName"
    /// If empty, the full response is returned.
    /// Supports array traversal: "fields.components.name" extracts name from each element.
    /// </summary>
    public string ResponseFilter { get; set; } = "";

    public bool CacheEnabled { get; set; } = false;
    public int CacheTtlSeconds { get; set; } = 60;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Compact one-liner representation for the agent prompt.
    /// Example: get_employee(id:int, dept:str?) → {name, email, manager}
    /// </summary>
    [JsonIgnore]
    public string CompactSignature
    {
        get
        {
            var paramStr = string.Join(", ", Parameters.ConvertAll(p =>
            {
                var typeShort = p.Type switch
                {
                    ParameterType.String => "str",
                    ParameterType.Integer => "int",
                    ParameterType.Float => "float",
                    ParameterType.Boolean => "bool",
                    ParameterType.Date => "date",
                    _ => "str"
                };
                return p.Required ? $"{p.Name}:{typeShort}" : $"{p.Name}:{typeShort}?";
            }));
            return $"{Name}({paramStr})";
        }
    }
}

public class SkillGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Color { get; set; } = "#5B8DEF";
    public bool Enabled { get; set; } = true;
}
