using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using CorpGateway.ViewModels;

namespace CorpGateway.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindows())
            HookWndProc();
    }

    // ── Win32: intercept minimize at WM_SYSCOMMAND level ─────────────────
    private void HookWndProc()
    {
        var handle = TryGetPlatformHandle();
        if (handle == null) return;
        var hwnd = handle.Handle;

        _originalWndProc = GetWindowLongPtr(hwnd, GWLP_WNDPROC);
        _wndProcDelegate = WndProc; // prevent GC
        SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        _hwnd = hwnd;
    }

    private IntPtr _hwnd;
    private IntPtr _originalWndProc;
    private WndProcDelegate? _wndProcDelegate;

    private const int GWLP_WNDPROC = -4;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_MINIMIZE)
        {
            // Swallow minimize → hide to tray instead
            Hide();
            return IntPtr.Zero;
        }
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ── Keyboard ─────────────────────────────────────────────────────────
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel vm)
            vm.CancelEditCommand.Execute(null);
        base.OnKeyDown(e);
    }
}
