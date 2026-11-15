using Avalonia.Controls;
using Avalonia.Input;
using HajdeAno.App.ViewModels;

namespace HajdeAno.App.Views;

public partial class ReviewView : UserControl
{
    public ReviewView() => InitializeComponent();

    /// <summary>Explain why copying is not possible when the disabled button is clicked.</summary>
    private void OnCopyAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ReviewViewModel review && !review.CopyCommand.CanExecute(null))
        {
            review.ExplainWhyCopyIsDisabled();
            e.Handled = true;
        }
    }
}
