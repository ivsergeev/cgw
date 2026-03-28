using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
    private WindowIcon? _trayIconConnected;
    private WindowIcon? _trayIconDisconnected;

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

        var chromeItem = new NativeMenuItem("Запустить Chrome (CDP)");
        chromeItem.Click += (_, _) => LaunchChromeWithCdp();
        menu.Add(chromeItem);

        menu.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("Выход");
        quitItem.Click += (_, _) =>
        {
            Cleanup();
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        };
        menu.Add(quitItem);

        // Generate tray icons with status indicator overlay
        BuildTrayIcons();

        _trayIcon = new TrayIcon
        {
            ToolTipText = "CorpGateway — нет подключения",
            Menu = menu,
            Icon = _trayIconDisconnected,
            IsVisible = true
        };

        _trayIcon.Clicked += (_, _) => ShowMainWindow();

        // Update tray icon when CDP connection state changes
        if (_mainVm != null)
        {
            _mainVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsCdpConnected))
                    UpdateTrayIconState(_mainVm.IsCdpConnected);
            };
            // Set initial state
            UpdateTrayIconState(_mainVm.IsCdpConnected);
        }
    }

    private void UpdateTrayIconState(bool connected)
    {
        if (_trayIcon == null) return;
        _trayIcon.Icon = connected ? _trayIconConnected : _trayIconDisconnected;
        _trayIcon.ToolTipText = connected
            ? $"CorpGateway — Chrome: {_config?.CdpPort}"
            : "CorpGateway";
    }

    private void BuildTrayIcons()
    {
        try
        {
            using var baseStream = AssetLoader.Open(new Uri("avares://CorpGateway/Assets/tray.ico"));
            var baseBitmap = new Bitmap(baseStream);

            _trayIconConnected = BuildOverlayIcon(baseBitmap, Color.Parse("#22C55E"));
            _trayIconDisconnected = BuildOverlayIcon(baseBitmap, Color.Parse("#EF4444"));
        }
        catch
        {
            // Fallback — no overlay, just use base icon for both
        }
    }

    private static WindowIcon? BuildOverlayIcon(Bitmap baseBitmap, Color dotColor)
    {
        var size = Math.Max((int)baseBitmap.Size.Width, 32);
        var rtb = new RenderTargetBitmap(new PixelSize(size, size));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawImage(baseBitmap, new Rect(0, 0, size, size));

            // Draw status dot in bottom-right corner
            var dotRadius = size / 5.0;
            var center = new Point(size - dotRadius - 1, size - dotRadius - 1);
            ctx.DrawEllipse(new SolidColorBrush(dotColor), null, center, dotRadius, dotRadius);
        }

        using var ms = new MemoryStream();
        rtb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null || !_mainWindow.IsVisible)
        {
            _mainWindow = new MainWindow
            {
                DataContext = _mainVm,
                ShowInTaskbar = false,
                WindowState = WindowState.Maximized,
                Topmost = true
            };
            _mainWindow.Closing += (_, e) =>
            {
                // Hide instead of close → stays in tray
                e.Cancel = true;
                _mainWindow.Hide();
            };
            _mainWindow.Show();
            // Reset Topmost after window is shown so it doesn't stay always-on-top
            _mainWindow.Topmost = false;
        }
        else
        {
            _mainWindow.WindowState = WindowState.Maximized;
            _mainWindow.Topmost = true;
            _mainWindow.Activate();
            // Reset after activation
            _mainWindow.Topmost = false;
        }
    }

    private async void CopyTokenToClipboard()
    {
        if (_config == null) return;
        var clipboard = TopLevel.GetTopLevel(_mainWindow)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(_config.ApiToken);
    }

    private void LaunchChromeWithCdp()
    {
        var port = _config?.CdpPort ?? 9222;
        var profileDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chrome-cgw-{Environment.UserName}");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chrome",
                Arguments = $"--remote-debugging-port={port} --user-data-dir=\"{profileDir}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // chrome not in PATH — try default install locations on Windows
            var paths = new[]
            {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
            };
            foreach (var path in paths)
            {
                if (!System.IO.File.Exists(path)) continue;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = $"--remote-debugging-port={port} --user-data-dir=\"{profileDir}\"",
                        UseShellExecute = false
                    });
                    return;
                }
                catch { }
            }
        }
    }

    private void Cleanup()
    {
        // Timeout disconnect to avoid hanging on shutdown if Chrome is unresponsive
        try
        {
            var disconnectTask = _cdpService?.DisconnectAsync() ?? Task.CompletedTask;
            disconnectTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch { }
        _server?.Stop();
        _trayIcon?.Dispose();
    }
}
