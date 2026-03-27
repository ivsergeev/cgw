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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel vm)
            vm.CancelEditCommand.Execute(null);
        base.OnKeyDown(e);
    }
}
