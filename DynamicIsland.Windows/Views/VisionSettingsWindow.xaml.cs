using System.Windows;
using DynamicIsland.Windows.ViewModels;

namespace DynamicIsland.Windows.Views;

public partial class VisionSettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public VisionSettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        // Preview frames only flow while this window is visible.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) _viewModel.BeginPreview();
            else _viewModel.EndPreview();
        };
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.SaveAsync(false);
        Hide();
    }
}
