using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HajdeAno.App.ViewModels;

namespace HajdeAno.App.Views;

public partial class SessionPickerView : UserControl
{
    private SessionPickerViewModel? _viewModel;

    public SessionPickerView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NewSessionButton.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as SessionPickerViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionPickerViewModel.IsUnlocking) && _viewModel!.IsUnlocking)
        {
            // Once the box is visible, so that the user can type the password right away.
            Dispatcher.UIThread.Post(() => UnlockPasswordBox.Focus());
        }
    }
}
