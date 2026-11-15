using Avalonia.Controls;
using HajdeAno.App.ViewModels;

namespace HajdeAno.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Save the draft even if the window is closed before the autosave timer fired.
        (DataContext as MainWindowViewModel)?.FlushDrafts();
        base.OnClosing(e);
    }
}
