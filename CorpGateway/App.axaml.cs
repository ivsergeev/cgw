using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using CorpGateway.Services;
using CorpGateway.ViewModels;
using CorpGateway.Views;

namespace CorpGateway;

public class App : Application
{
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private LocalApiServer? _server;
    private SkillsRepository? _repo;
    private AppConfig? _config;
    private ChromeCdpService? _cdpService;
    private MainViewModel? _mainVm;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Bootstrap services
            _config = await AppConfig.LoadAsync();
            _repo = new SkillsRepository();
            _cdpService = new ChromeCdpService();
            _server = new LocalApiServer(_repo, _cdpService);

            await _repo.LoadAsync();
            _server.Start(_config.ApiPort, _config.ApiToken);

            // Auto-connect to Chrome CDP if configured
            if (_config.CdpAutoConnect)
                await _cdpService.ConnectAsync(_config.CdpPort);

            // Sync auto-start registry with config
            if (OperatingSystem.IsWindows())
                AutoStartService.SetAutoStart(_config.StartWithSystem);

            _mainVm = new MainViewModel(_repo, _server, _config, _cdpService);
            await _mainVm.InitializeAsync();

            // Build tray icon
            SetupTrayIcon();

            // Show main window unless start-minimized
            if (!_config.StartMinimized)
                ShowMainWindow();

            desktop.Exit += (_, _) => Cleanup();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon()
    {
        var menu = new NativeMenu();

        var openItem = new NativeMenuItem("Открыть CorpGateway");
        openItem.Click += (_, _) => ShowMainWindow();
        menu.Add(openItem);

        menu.Add(new NativeMenuItemSeparator());

        var statusItem = new NativeMenuItem($"API на порту {_config!.ApiPort}") { IsEnabled = false };
        menu.Add(statusItem);

        var tokenItem = new NativeMenuItem("Копировать API токен");
        tokenItem.Click += (_, _) => CopyTokenToClipboard();
        menu.Add(tokenItem);

        menu.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("Выход");
        quitItem.Click += (_, _) =>
        {
            Cleanup();
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        };
        menu.Add(quitItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "CorpGateway",
            Menu = menu,
            IsVisible = true
        };

        // Use a simple built-in icon; replace with Assets/tray.ico in your project
        try
        {
            _trayIcon.Icon = new WindowIcon(
                AssetLoader.Open(new Uri("avares://CorpGateway/Assets/tray.ico")));
        }
        catch
        {
            // Icon file not found — tray still works, just no custom icon
        }

        _trayIcon.Clicked += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null || !_mainWindow.IsVisible)
        {
            _mainWindow = new MainWindow
            {
                DataContext = _mainVm,
                ShowInTaskbar = false
            };
            _mainWindow.Closing += (_, e) =>
            {
                // Hide instead of close → stays in tray
                e.Cancel = true;
                _mainWindow.Hide();
            };
            _mainWindow.Show();
        }
        else
        {
            _mainWindow.Activate();
        }
    }

    private void CopyTokenToClipboard()
    {
        if (_config == null) return;
        var clipboard = TopLevel.GetTopLevel(_mainWindow)?.Clipboard;
        clipboard?.SetTextAsync(_config.ApiToken);
    }

    private void Cleanup()
    {
        _cdpService?.DisconnectAsync().GetAwaiter().GetResult();
        _server?.Stop();
        _trayIcon?.Dispose();
    }
}
