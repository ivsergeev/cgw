using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CorpGateway.Models;
using CorpGateway.Services;
using ReactiveUI;

namespace CorpGateway.ViewModels;

public class MainViewModel : ReactiveObject
{
    private readonly SkillsRepository _repo;
    private readonly LocalApiServer _server;
    private readonly AppConfig _config;
    private readonly ChromeCdpService _cdpService;

    // ── Observable state ────────────────────────────────────────────────────
    private SkillGroup? _selectedGroup;
    private SkillViewModel? _selectedSkill;
    private string _statusText = "Ready";
    private bool _isServerRunning;
    private string _editPanelMode = "";
    private string _searchText = "";
    private bool _isCdpConnected;
    private string _cdpStatusText = "Chrome: нет подключения";
    private int _cdpPort;
    private string _errorMessage = "";
    private bool _startWithSystem;

    public ObservableCollection<SkillGroup> Groups { get; } = new();
    public ObservableCollection<SkillViewModel> Skills { get; } = new();

    public SkillGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedGroup, value);
            RefreshSkills();
            (StartAddSkillCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public SkillViewModel? SelectedSkill
    {
        get => _selectedSkill;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSkill, value);
            ErrorMessage = "";
            if (value != null)
            {
                EditSkill.LoadFrom(value.Model);
                EditPanelMode = "EditSkill";
            }
            else
            {
                EditPanelMode = "";
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public bool IsServerRunning
    {
        get => _isServerRunning;
        set => this.RaiseAndSetIfChanged(ref _isServerRunning, value);
    }

    public string EditPanelMode
    {
        get => _editPanelMode;
        set => this.RaiseAndSetIfChanged(ref _editPanelMode, value);
    }

    public string SearchText
    {
        get => _searchText;
        set { this.RaiseAndSetIfChanged(ref _searchText, value); RefreshSkills(); }
    }

    public bool IsCdpConnected
    {
        get => _isCdpConnected;
        set => this.RaiseAndSetIfChanged(ref _isCdpConnected, value);
    }

    public string CdpStatusText
    {
        get => _cdpStatusText;
        set => this.RaiseAndSetIfChanged(ref _cdpStatusText, value);
    }

    public int CdpPort
    {
        get => _cdpPort;
        set
        {
            if (value < 1 || value > 65535) return;
            this.RaiseAndSetIfChanged(ref _cdpPort, value);
            _config.CdpPort = value;
        }
    }


    public bool StartWithSystem
    {
        get => _startWithSystem;
        set
        {
            this.RaiseAndSetIfChanged(ref _startWithSystem, value);
            _config.StartWithSystem = value;
            if (OperatingSystem.IsWindows())
                AutoStartService.SetAutoStart(value);
            Task.Run(async () => { try { await _config.SaveAsync(); } catch { } });
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public int ApiPort => _config.ApiPort;
    public string ApiToken => _config.ApiToken;

    // ── Sub-ViewModels ───────────────────────────────────────────────────────
    public EditGroupViewModel EditGroup { get; } = new();
    public EditSkillViewModel EditSkill { get; }
    public TestPanelViewModel TestPanel { get; }

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand StartAddGroupCommand { get; }
    public ICommand SaveGroupCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand StartAddSkillCommand { get; }
    public ICommand SaveSkillCommand { get; }
    public ICommand TestSkillCommand { get; }

    public ICommand DeleteSkillCommand { get; }
    public ICommand DeleteGroupCommand { get; }
    public ICommand ConnectCdpCommand { get; }
    public ICommand DisconnectCdpCommand { get; }
    public ICommand ExportSkillsCommand { get; }
    public ICommand ImportSkillsCommand { get; }

    // ── Constructor ──────────────────────────────────────────────────────────
    public MainViewModel(SkillsRepository repo, LocalApiServer server, AppConfig config,
        ChromeCdpService cdpService)
    {
        _repo = repo;
        _server = server;
        _config = config;
        _cdpService = cdpService;
        _cdpPort = config.CdpPort;
        _startWithSystem = config.StartWithSystem;

        EditSkill = new EditSkillViewModel(repo);
        TestPanel = new TestPanelViewModel(config);

        IsServerRunning = server.IsRunning;
        StatusText = server.IsRunning
            ? $"API на порту {server.Port}"
            : "API сервер остановлен";

        StartAddGroupCommand = new RelayCommand(_ =>
        {
            ErrorMessage = "";
            EditGroup.Reset();
            EditPanelMode = "EditGroup";
        });

        SaveGroupCommand = new AsyncRelayCommand(_ => SaveGroupAsync());

        CancelEditCommand = new RelayCommand(_ =>
        {
            ErrorMessage = "";
            if (EditPanelMode == "Test" && _selectedSkill != null &&
                _repo.GetSkill(_selectedSkill.Model.Id) != null)
            {
                EditSkill.LoadFrom(_selectedSkill.Model);
                EditPanelMode = "EditSkill";
            }
            else
            {
                EditPanelMode = "";
            }
        });

        StartAddSkillCommand = new RelayCommand(
            _ => { if (_selectedGroup != null) { ErrorMessage = ""; EditSkill.Reset(_selectedGroup.Id); EditPanelMode = "EditSkill"; } },
            _ => _selectedGroup != null);

        SaveSkillCommand = new AsyncRelayCommand(_ => SaveSkillAsync());

        TestSkillCommand = new RelayCommand(param =>
        {
            if (param is SkillViewModel vm) { ErrorMessage = ""; TestPanel.LoadSkill(vm.Model); EditPanelMode = "Test"; }
        });

        DeleteSkillCommand = new AsyncRelayCommand(async param =>
        {
            if (param is SkillViewModel vm) await DeleteSkillAsync(vm);
        });

        DeleteGroupCommand = new AsyncRelayCommand(async param =>
        {
            if (param is SkillGroup g) await DeleteGroupAsync(g);
        });

        // CDP commands
        ConnectCdpCommand = new AsyncRelayCommand(async _ =>
        {
            CdpStatusText = "Chrome: подключение...";
            var ok = await _cdpService.ConnectAsync(CdpPort);
            IsCdpConnected = ok;
            CdpStatusText = ok
                ? $"Chrome: подключен (порт {CdpPort})"
                : "Chrome: ошибка подключения";
        });

        DisconnectCdpCommand = new AsyncRelayCommand(async _ =>
        {
            await _cdpService.DisconnectAsync();
            IsCdpConnected = false;
            CdpStatusText = "Chrome: нет подключения";
        });

        _cdpService.ConnectionLost += _ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsCdpConnected = false;
                CdpStatusText = "Chrome: соединение потеряно";
            });
        };

        ExportSkillsCommand = new AsyncRelayCommand(_ => ExportSkillsAsync());
        ImportSkillsCommand = new AsyncRelayCommand(_ => ImportSkillsAsync());
    }

    // ── Initialization ───────────────────────────────────────────────────────
    public async Task InitializeAsync()
    {
        await _repo.LoadAsync();
        LoadGroups();
        if (Groups.Count > 0) SelectedGroup = Groups[0];
    }

    // ── Groups ───────────────────────────────────────────────────────────────
    private void LoadGroups()
    {
        Groups.Clear();
        foreach (var g in _repo.GetGroups()) Groups.Add(g);
    }

    private void RefreshSkills()
    {
        Skills.Clear();
        var source = _selectedGroup != null
            ? _repo.GetSkillsByGroup(_selectedGroup.Id)
            : _repo.GetSkills();

        var filter = _searchText.Trim();
        foreach (var s in source)
        {
            if (string.IsNullOrEmpty(filter) ||
                s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Skills.Add(new SkillViewModel(s));
        }
    }

    // ── Async operations ──────────────────────────────────────────────────────
    private async Task SaveGroupAsync()
    {
        ErrorMessage = "";
        if (string.IsNullOrWhiteSpace(EditGroup.Name))
        { ErrorMessage = "Укажите название группы"; return; }

        try
        {
            if (EditGroup.IsNew)
            {
                await _repo.AddGroupAsync(new SkillGroup
                {
                    Name = EditGroup.Name,
                    Description = EditGroup.Description,
                    Color = EditGroup.Color
                });
            }
            else
            {
                var existing = _repo.GetGroups().FirstOrDefault(g => g.Id == EditGroup.Id);
                if (existing != null)
                {
                    existing.Name = EditGroup.Name;
                    existing.Description = EditGroup.Description;
                    existing.Color = EditGroup.Color;
                    await _repo.UpdateGroupAsync(existing);
                }
            }
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; return; }

        LoadGroups();
        EditPanelMode = "";
    }

    private async Task SaveSkillAsync()
    {
        ErrorMessage = "";

        // Validate required fields
        if (string.IsNullOrWhiteSpace(EditSkill.Name))
        { ErrorMessage = "Укажите название скила"; return; }

        if (string.IsNullOrWhiteSpace(EditSkill.Url))
        { ErrorMessage = "Укажите URL"; return; }

        if (!Uri.TryCreate(EditSkill.Url.Trim(), UriKind.Absolute, out _))
        { ErrorMessage = "URL имеет некорректный формат"; return; }

        // Validate BodyTemplate is valid JSON (if provided)
        var bodyTpl = EditSkill.BodyTemplate?.Trim() ?? "";
        if (!string.IsNullOrEmpty(bodyTpl))
        {
            try { System.Text.Json.JsonDocument.Parse(bodyTpl); }
            catch { ErrorMessage = "Body Template не является валидным JSON"; return; }
        }

        // Validate parameter names: non-empty and unique
        var paramNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in EditSkill.Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            { ErrorMessage = "Все параметры должны иметь название"; return; }
            if (!paramNames.Add(p.Name.Trim()))
            { ErrorMessage = $"Дублирующееся имя параметра: {p.Name.Trim()}"; return; }
        }

        var isNew = EditSkill.Id == Guid.Empty;
        var skill = EditSkill.ToModel();
        try
        {
            if (isNew || _repo.GetSkill(skill.Id) == null)
                await _repo.AddSkillAsync(skill);
            else
                await _repo.UpdateSkillAsync(skill);
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; return; }

        // Sync Id back so re-save doesn't create duplicate
        EditSkill.Id = skill.Id;
        RefreshSkills();
        EditPanelMode = "";
    }

    private async Task DeleteSkillAsync(SkillViewModel vm)
    {
        await _repo.DeleteSkillAsync(vm.Model.Id);
        if (SelectedSkill?.Model.Id == vm.Model.Id)
        {
            _selectedSkill = null;
            this.RaisePropertyChanged(nameof(SelectedSkill));
            EditPanelMode = "";
            ErrorMessage = "";
        }
        RefreshSkills();
    }

    private async Task DeleteGroupAsync(SkillGroup group)
    {
        await _repo.DeleteGroupAsync(group.Id);
        EditPanelMode = "";
        ErrorMessage = "";
        LoadGroups();
        if (SelectedGroup?.Id == group.Id)
            SelectedGroup = Groups.FirstOrDefault();
        RefreshSkills();
    }

    private async Task ExportSkillsAsync()
    {
        ErrorMessage = "";
        var sp = GetStorageProvider();
        if (sp == null) return;

        var groupIds = _selectedGroup != null
            ? new List<string> { _selectedGroup.Id }
            : null;

        var json = _repo.ExportGroups(groupIds);
        var groupName = _selectedGroup?.Name ?? "all";

        var file = await sp.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            DefaultExtension = "json",
            SuggestedFileName = $"cgw-skills-{groupName}.json",
            FileTypeChoices = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
            }
        });
        if (file == null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(json);
        StatusText = $"Экспортировано в {file.Name}";
    }

    private async Task ImportSkillsAsync()
    {
        ErrorMessage = "";
        var sp = GetStorageProvider();
        if (sp == null) return;

        var files = await sp.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
            }
        });
        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new System.IO.StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var (groupsAdded, skillsAdded, skipped) = await _repo.ImportAsync(json);
            LoadGroups();
            RefreshSkills();
            StatusText = $"Импортировано: {groupsAdded} групп, {skillsAdded} скилов" +
                         (skipped > 0 ? $" ({skipped} пропущено как дубликаты)" : "");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ошибка импорта: {ex.Message}";
        }
    }

    private Avalonia.Platform.Storage.IStorageProvider? GetStorageProvider()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.Windows.FirstOrDefault(w => w.IsActive)
                ?? desktop.Windows.FirstOrDefault();
            return window?.StorageProvider;
        }
        return null;
    }
}

// ── Command helpers ────────────────────────────────────────────────────────────
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
    public void Execute(object? p) => _execute(p);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => !_isRunning;

    public async void Execute(object? p)
    {
        if (_isRunning) return;
        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _execute(p); }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
