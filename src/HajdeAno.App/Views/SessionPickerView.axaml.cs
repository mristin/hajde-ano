using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HajdeAno.App.Views;

public partial class SessionPickerView : UserControl
{
    public SessionPickerView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NewSessionButton.Focus();
    }
}
