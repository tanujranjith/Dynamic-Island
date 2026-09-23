using System.Windows;
using DynamicIsland.Windows.ViewModels;

namespace DynamicIsland.Windows.Views;

public partial class QComparisonView : System.Windows.Controls.UserControl
{
    public QComparisonView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            var narrow = ActualWidth < 760 || ActualHeight < 340;
            ComparisonScroller.VerticalScrollBarVisibility = narrow ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
            ComparisonLayout.Height = narrow ? double.NaN : ActualHeight;
            System.Windows.Controls.Grid.SetColumn(RightCard, narrow ? 0 : 2);
            System.Windows.Controls.Grid.SetRow(RightCard, narrow ? 1 : 0);
            AnswerColumns.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 18);
            AnswerColumns.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            AnswerColumns.RowDefinitions[0].Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            AnswerColumns.RowDefinitions[1].Height = narrow ? GridLength.Auto : new GridLength(0);
            LeftCard.Height = RightCard.Height = narrow ? 280 : double.NaN;
            RightCard.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);
        };
    }
    private IslandViewModel? Model => DataContext as IslandViewModel;
    private void LeftCopy_Click(object sender, RoutedEventArgs e) => Model?.CopyComparisonAnswer(false);
    private void RightCopy_Click(object sender, RoutedEventArgs e) => Model?.CopyComparisonAnswer(true);
    private async void LeftRetry_Click(object sender, RoutedEventArgs e) { if (Model is { } vm) await vm.RetryComparisonAsync(false); }
    private async void RightRetry_Click(object sender, RoutedEventArgs e) { if (Model is { } vm) await vm.RetryComparisonAsync(true); }
}
