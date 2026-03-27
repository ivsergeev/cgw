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

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        // Disable minimize via platform interop on Windows
        if (System.OperatingSystem.IsWindows())
            DisableMinimizeButton();
    }

    private void DisableMinimizeButton()
    {
        var handle = TryGetPlatformHandle();
        if (handle != null)
        {
            const int GWL_STYLE = -16;
            const uint WS_MINIMIZEBOX = 0x00020000;
            var hwnd = handle.Handle;
            var style = (uint)GetWindowLong(hwnd, GWL_STYLE);
            style &= ~WS_MINIMIZEBOX;
            SetWindowLong(hwnd, GWL_STYLE, (int)style);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel vm)
            vm.CancelEditCommand.Execute(null);
        base.OnKeyDown(e);
    }
}
