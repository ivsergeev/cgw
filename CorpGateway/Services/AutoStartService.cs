using System;
using System.IO;

namespace CorpGateway.Services;

public static class AutoStartService
{
    private const string AppName = "CorpGateway";

    public static void SetAutoStart(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                SetAutoStartWindows(enabled);
            else if (OperatingSystem.IsLinux())
                SetAutoStartLinux(enabled);
            else if (OperatingSystem.IsMacOS())
                SetAutoStartMacOS(enabled);
        }
        catch
        {
            // Auto-start setup may fail in restricted environments
        }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return IsAutoStartEnabledWindows();
            if (OperatingSystem.IsLinux())
                return File.Exists(GetLinuxDesktopPath());
            if (OperatingSystem.IsMacOS())
                return File.Exists(GetMacOSPlistPath());
        }
        catch { }
        return false;
    }

    // ── Windows: Registry ────────────────────────────────────────────────
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetAutoStartWindows(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true);
        if (key == null) return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsAutoStartEnabledWindows()
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey);
        return key?.GetValue(AppName) != null;
    }

    // ── Linux: .desktop file in ~/.config/autostart ──────────────────────
    private static string GetLinuxDesktopPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart", $"{AppName}.desktop");

    private static void SetAutoStartLinux(bool enabled)
    {
        var path = GetLinuxDesktopPath();
        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"""
                [Desktop Entry]
                Type=Application
                Name={AppName}
                Exec={exePath}
                X-GNOME-Autostart-enabled=true
                Hidden=false
                NoDisplay=false
                """.Replace("                ", ""));
        }
        else
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── macOS: LaunchAgent plist in ~/Library/LaunchAgents ────────────────
    private static string GetMacOSPlistPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", $"com.{AppName.ToLowerInvariant()}.plist");

    private static void SetAutoStartMacOS(bool enabled)
    {
        var path = GetMacOSPlistPath();
        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.{AppName.ToLowerInvariant()}</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{exePath}</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                </dict>
                </plist>
                """.Replace("                ", ""));
        }
        else
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
