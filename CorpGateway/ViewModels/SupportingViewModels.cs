using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CorpGateway.Models;
using CorpGateway.Services;
using ReactiveUI;

namespace CorpGateway.ViewModels;

// ── Skill list item ────────────────────────────────────────────────────────
public class SkillViewModel : ReactiveObject
{
    public Skill Model { get; }
    public string Name => Model.Name;
    public string Description => Model.Description;
    public string Signature => Model.CompactSignature;
    public string Method => Model.HttpMethod;

    public SkillViewModel(Skill model) => Model = model;
}

// ── Edit Group form VM ─────────────────────────────────────────────────────
public class EditGroupViewModel : ReactiveObject
{
    private string _id = "";
    private string _name = "";
    private string _description = "";
    private string _color = "#5B8DEF";

    public string Id { get => _id; set => this.RaiseAndSetIfChanged(ref _id, value); }
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }
    public string Color { get => _color; set => this.RaiseAndSetIfChanged(ref _color, value); }
    public bool IsNew => string.IsNullOrEmpty(_id);

    public void Reset() { Id = ""; Name = ""; Description = ""; Color = "#5B8DEF"; }

    public void LoadFrom(SkillGroup g)
    {
        Id = g.Id; Name = g.Name; Description = g.Description; Color = g.Color;
    }
}

// ── Parameter row VM ──────────────────────────────────────────────────────
public class ParameterViewModel : ReactiveObject
{
    private string _name = "";
    private string _description = "";
    private ParameterType _type = ParameterType.String;
    private bool _required = true;
    private string _defaultValue = "";

    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }
    public ParameterType Type { get => _type; set => this.RaiseAndSetIfChanged(ref _type, value); }
    public bool Required { get => _required; set => this.RaiseAndSetIfChanged(ref _required, value); }
    public string DefaultValue { get => _defaultValue; set => this.RaiseAndSetIfChanged(ref _defaultValue, value); }

    public SkillParameter ToModel() => new()
    {
        Name = Name, Description = Description, Type = Type,
        Required = Required,
        DefaultValue = string.IsNullOrEmpty(DefaultValue) ? null : DefaultValue
    };

    public static ParameterViewModel FromModel(SkillParameter p) => new()
    {
        _name = p.Name, _description = p.Description, _type = p.Type,
        _required = p.Required, _defaultValue = p.DefaultValue ?? ""
    };
}

// ── Edit Skill form VM ─────────────────────────────────────────────────────
public class EditSkillViewModel : ReactiveObject
{
    private Guid _id;
    private string _groupId = "";
    private string _name = "";
    private string _description = "";
    private string _url = "";
    private string _httpMethod = "GET";
    private bool _cacheEnabled = false;
    private int _cacheTtl = 60;
    private string _bodyTemplate = "";

    public Guid Id
    {
        get => _id;
        set { this.RaiseAndSetIfChanged(ref _id, value); this.RaisePropertyChanged(nameof(IsNew)); }
    }
    public bool IsNew => _id == Guid.Empty;
    public string GroupId { get => _groupId; set => this.RaiseAndSetIfChanged(ref _groupId, value); }

    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); this.RaisePropertyChanged(nameof(PreviewSignature)); }
    }

    public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }
    public string Url { get => _url; set => this.RaiseAndSetIfChanged(ref _url, value); }
    public string HttpMethod
    {
        get => _httpMethod;
        set { this.RaiseAndSetIfChanged(ref _httpMethod, value); this.RaisePropertyChanged(nameof(HasBody)); }
    }
    public bool CacheEnabled { get => _cacheEnabled; set => this.RaiseAndSetIfChanged(ref _cacheEnabled, value); }
    public int CacheTtl { get => _cacheTtl; set => this.RaiseAndSetIfChanged(ref _cacheTtl, value); }
    public string BodyTemplate { get => _bodyTemplate; set => this.RaiseAndSetIfChanged(ref _bodyTemplate, value); }

    public bool HasBody => HttpMethod is "POST" or "PUT" or "PATCH";

    public ObservableCollection<ParameterViewModel> Parameters { get; } = new();
    public string[] HttpMethods { get; } = { "GET", "POST", "PUT", "PATCH", "DELETE" };

    public string PreviewSignature
    {
        get
        {
            var name = string.IsNullOrEmpty(Name) ? "skill_name" : Name;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var p in Parameters)
            {
                var t = p.Type switch
                {
                    ParameterType.Integer => "int",
                    ParameterType.Float => "float",
                    ParameterType.Boolean => "bool",
                    ParameterType.Date => "date",
                    _ => "str"
                };
                parts.Add(p.Required ? $"{p.Name}:{t}" : $"{p.Name}:{t}?");
            }
            return $"{name}({string.Join(", ", parts)})";
        }
    }

    public System.Windows.Input.ICommand AddParameterCommand { get; }
    public System.Windows.Input.ICommand RemoveParameterCommand { get; }

    public EditSkillViewModel(SkillsRepository _)
    {
        AddParameterCommand = new RelayCommand(_ =>
        {
            Parameters.Add(new ParameterViewModel());
            this.RaisePropertyChanged(nameof(PreviewSignature));
        });

        RemoveParameterCommand = new RelayCommand(param =>
        {
            if (param is ParameterViewModel p) Parameters.Remove(p);
            this.RaisePropertyChanged(nameof(PreviewSignature));
        });

        Parameters.CollectionChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(PreviewSignature));
    }

    public void Reset(string groupId)
    {
        Id = Guid.Empty; GroupId = groupId; Name = ""; Description = "";
        Url = ""; HttpMethod = "GET"; CacheEnabled = false; CacheTtl = 60;
        BodyTemplate = "";
        Parameters.Clear();
    }

    public void LoadFrom(Skill s)
    {
        Id = s.Id; GroupId = s.GroupId; Name = s.Name; Description = s.Description;
        Url = s.Url; HttpMethod = s.HttpMethod; CacheEnabled = s.CacheEnabled;
        CacheTtl = s.CacheTtlSeconds; BodyTemplate = s.BodyTemplate;
        Parameters.Clear();
        foreach (var p in s.Parameters) Parameters.Add(ParameterViewModel.FromModel(p));
    }

    public Skill ToModel()
    {
        var skill = new Skill
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            GroupId = GroupId, Name = Name.Trim(), Description = Description.Trim(),
            Url = Url.Trim(), HttpMethod = HttpMethod,
            BodyTemplate = BodyTemplate?.Trim() ?? "",
            CacheEnabled = CacheEnabled, CacheTtlSeconds = CacheTtl
        };
        foreach (var p in Parameters) skill.Parameters.Add(p.ToModel());
        return skill;
    }
}

// ── Test panel VM ──────────────────────────────────────────────────────────
public class TestPanelViewModel : ReactiveObject
{
    private readonly AppConfig _config;
    private Skill? _skill;
    private string _responseText = "";
    private bool _isTesting = false;

    public string SkillName => _skill?.Name ?? "";
    public ObservableCollection<TestParamViewModel> ParamValues { get; } = new();

    public string ResponseText
    {
        get => _responseText;
        set => this.RaiseAndSetIfChanged(ref _responseText, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        set => this.RaiseAndSetIfChanged(ref _isTesting, value);
    }

    public System.Windows.Input.ICommand RunTestCommand { get; }

    public TestPanelViewModel(AppConfig config)
    {
        _config = config;
        RunTestCommand = new AsyncRelayCommand(_ => RunTestAsync());
    }

    public void LoadSkill(Skill skill)
    {
        _skill = skill;
        ParamValues.Clear();
        foreach (var p in skill.Parameters)
            ParamValues.Add(new TestParamViewModel { Name = p.Name, Required = p.Required, Type = p.Type });
        ResponseText = "";
        this.RaisePropertyChanged(nameof(SkillName));
    }

    private async Task RunTestAsync()
    {
        if (_skill == null) return;
        IsTesting = true;
        ResponseText = "Вызов...";
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_config.ApiToken}");

            var paramDict = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var p in ParamValues) paramDict[p.Name] = p.Value;

            var body = new { skill = _skill.Name, parameters = paramDict };
            var resp = await http.PostAsync(
                $"http://localhost:{_config.ApiPort}/invoke",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

            var respJson = await resp.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<object>(respJson);
            ResponseText = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) { ResponseText = $"Ошибка: {ex.Message}"; }
        finally { IsTesting = false; }
    }

}

public class TestParamViewModel : ReactiveObject
{
    private string _value = "";
    public string Name { get; set; } = "";
    public bool Required { get; set; }
    public ParameterType Type { get; set; } = ParameterType.String;
    public string Value { get => _value; set => this.RaiseAndSetIfChanged(ref _value, value); }

    public string TypeLabel => Type switch
    {
        ParameterType.Integer => "int",
        ParameterType.Float => "float",
        ParameterType.Boolean => "bool",
        ParameterType.Date => "date",
        _ => "string"
    };
}
