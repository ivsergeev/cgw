using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CorpGateway.Models;

namespace CorpGateway.Services;

public class SkillsStore
{
    public List<SkillGroup> Groups { get; set; } = new();
    public List<Skill> Skills { get; set; } = new();
}

public class SkillsRepository
{
    private readonly string _storePath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private SkillsStore _store = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SkillsRepository()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "CorpGateway");
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "skills.json");
    }

    public async Task LoadAsync()
    {
        await _ioLock.WaitAsync();
        try
        {
            if (!File.Exists(_storePath))
            {
                _store = CreateDefaultStore();
                await SaveInternalAsync();
                return;
            }

            var json = await File.ReadAllTextAsync(_storePath);
            _store = JsonSerializer.Deserialize<SkillsStore>(json, _jsonOptions) ?? new SkillsStore();
        }
        finally { _ioLock.Release(); }
    }

    public async Task SaveAsync()
    {
        await _ioLock.WaitAsync();
        try { await SaveInternalAsync(); }
        finally { _ioLock.Release(); }
    }

    private async Task SaveInternalAsync()
    {
        var json = JsonSerializer.Serialize(_store, _jsonOptions);
        await File.WriteAllTextAsync(_storePath, json);
    }

    public IReadOnlyList<SkillGroup> GetGroups() => _store.Groups.AsReadOnly();
    public IReadOnlyList<Skill> GetSkills() => _store.Skills.AsReadOnly();

    /// <summary>Returns only skills belonging to enabled groups (for API clients).</summary>
    public IReadOnlyList<Skill> GetEnabledSkills()
    {
        var enabledGroupIds = _store.Groups.Where(g => g.Enabled).Select(g => g.Id).ToHashSet();
        return _store.Skills.Where(s => enabledGroupIds.Contains(s.GroupId)).ToList().AsReadOnly();
    }

    public IReadOnlyList<Skill> GetSkillsByGroup(string groupId) =>
        _store.Skills.FindAll(s => s.GroupId == groupId).AsReadOnly();

    public async Task SetGroupEnabledAsync(string groupId, bool enabled)
    {
        var group = _store.Groups.Find(g => g.Id == groupId);
        if (group != null)
        {
            group.Enabled = enabled;
            await SaveAsync();
        }
    }

    public Skill? GetSkill(Guid id) => _store.Skills.Find(s => s.Id == id);

    public bool GroupNameExists(string name, string? excludeId = null) =>
        _store.Groups.Exists(g =>
            g.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            g.Id != excludeId);

    public bool SkillNameExists(string name, Guid? excludeId = null) =>
        _store.Skills.Exists(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            s.Id != excludeId);

    public async Task AddGroupAsync(SkillGroup group)
    {
        if (GroupNameExists(group.Name))
            throw new InvalidOperationException($"Group '{group.Name}' already exists");
        _store.Groups.Add(group);
        await SaveAsync();
    }

    public async Task UpdateGroupAsync(SkillGroup group)
    {
        if (GroupNameExists(group.Name, group.Id))
            throw new InvalidOperationException($"Group '{group.Name}' already exists");
        var idx = _store.Groups.FindIndex(g => g.Id == group.Id);
        if (idx >= 0) _store.Groups[idx] = group;
        await SaveAsync();
    }

    public async Task DeleteGroupAsync(string groupId)
    {
        _store.Groups.RemoveAll(g => g.Id == groupId);
        _store.Skills.RemoveAll(s => s.GroupId == groupId);
        await SaveAsync();
    }

    public async Task AddSkillAsync(Skill skill)
    {
        if (SkillNameExists(skill.Name))
            throw new InvalidOperationException($"Skill '{skill.Name}' already exists");
        skill.CreatedAt = DateTime.UtcNow;
        skill.UpdatedAt = DateTime.UtcNow;
        _store.Skills.Add(skill);
        await SaveAsync();
    }

    public async Task UpdateSkillAsync(Skill skill)
    {
        if (SkillNameExists(skill.Name, skill.Id))
            throw new InvalidOperationException($"Skill '{skill.Name}' already exists");
        skill.UpdatedAt = DateTime.UtcNow;
        var idx = _store.Skills.FindIndex(s => s.Id == skill.Id);
        if (idx >= 0) _store.Skills[idx] = skill;
        await SaveAsync();
    }

    public async Task DeleteSkillAsync(Guid id)
    {
        _store.Skills.RemoveAll(s => s.Id == id);
        await SaveAsync();
    }

    /// <summary>
    /// Export compact skill definitions for agent context.
    /// Returns a minimal text block to minimize token usage.
    /// </summary>
    public string ExportCompact(string? groupId = null)
    {
        var enabledGroups = _store.Groups.Where(g => g.Enabled).ToList();
        if (groupId != null)
            enabledGroups = enabledGroups.Where(g => g.Id == groupId).ToList();

        var lines = new System.Text.StringBuilder();
        lines.AppendLine("# Available skills");

        foreach (var group in enabledGroups)
        {
            var groupSkills = _store.Skills.Where(s => s.GroupId == group.Id).ToList();
            if (groupSkills.Count == 0) continue;

            // Group header with description
            if (!string.IsNullOrWhiteSpace(group.Description))
                lines.AppendLine($"\n## {group.Description}");
            else
                lines.AppendLine($"\n## {group.Name}");

            foreach (var s in groupSkills)
            {
                lines.Append(s.CompactSignature);
                if (!string.IsNullOrEmpty(s.Description))
                    lines.Append($"  // {s.Description}");
                lines.AppendLine();
            }
        }
        return lines.ToString();
    }

    /// <summary>
    /// Export groups and their skills as JSON string.
    /// If groupIds is null, exports all groups.
    /// </summary>
    public string ExportGroups(IEnumerable<string>? groupIds = null)
    {
        var ids = groupIds?.ToHashSet();
        var groups = ids == null
            ? _store.Groups
            : _store.Groups.Where(g => ids.Contains(g.Id)).ToList();
        var skills = ids == null
            ? _store.Skills
            : _store.Skills.Where(s => ids.Contains(s.GroupId)).ToList();

        var export = new SkillsStore { Groups = groups, Skills = skills };
        return JsonSerializer.Serialize(export, _jsonOptions);
    }

    /// <summary>
    /// Import groups and skills from JSON string.
    /// Skips groups/skills with duplicate names, assigns new IDs.
    /// Returns (groupsAdded, skillsAdded, skipped).
    /// </summary>
    public async Task<(int GroupsAdded, int SkillsAdded, int Skipped)> ImportAsync(string json)
    {
        var import = JsonSerializer.Deserialize<SkillsStore>(json, _jsonOptions);
        if (import == null) return (0, 0, 0);

        int groupsAdded = 0, skillsAdded = 0, skipped = 0;
        var groupIdMap = new Dictionary<string, string>(); // old ID → new ID

        foreach (var g in import.Groups)
        {
            if (GroupNameExists(g.Name))
            {
                // Map to existing group with same name
                var existing = _store.Groups.First(x =>
                    x.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
                groupIdMap[g.Id] = existing.Id;
                skipped++;
                continue;
            }
            var newId = Guid.NewGuid().ToString("N")[..8];
            groupIdMap[g.Id] = newId;
            _store.Groups.Add(new SkillGroup
            {
                Id = newId, Name = g.Name,
                Description = g.Description, Color = g.Color,
                Enabled = g.Enabled
            });
            groupsAdded++;
        }

        foreach (var s in import.Skills)
        {
            if (SkillNameExists(s.Name))
            {
                skipped++;
                continue;
            }
            var newGroupId = groupIdMap.GetValueOrDefault(s.GroupId, s.GroupId);
            s.Id = Guid.NewGuid();
            s.GroupId = newGroupId;
            s.CreatedAt = DateTime.UtcNow;
            s.UpdatedAt = DateTime.UtcNow;
            _store.Skills.Add(s);
            skillsAdded++;
        }

        await SaveAsync();
        return (groupsAdded, skillsAdded, skipped);
    }

    private static SkillsStore CreateDefaultStore()
    {
        var defaultGroup = new SkillGroup
        {
            Id = "default",
            Name = "General",
            Description = "General purpose skills",
            Color = "#5B8DEF"
        };

        return new SkillsStore
        {
            Groups = new List<SkillGroup> { defaultGroup },
            Skills = new List<Skill>()
        };
    }
}
