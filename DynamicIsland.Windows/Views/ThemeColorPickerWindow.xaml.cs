using System.Windows;
using System.Windows.Media;
using DynamicIsland.Windows.Infrastructure;
using Color = System.Windows.Media.Color;

namespace DynamicIsland.Windows.Views;

public partial class ThemeColorPickerWindow : Window
{
    private bool _updating = true;
    public string SelectedColorHex { get; private set; } = ThemePalette.DefaultCustomColor;
    public ThemeColorPickerWindow(string initial)
    {
        InitializeComponent(); _updating = false; SetColor(ThemeColor.Parse(initial));
    }
    private void SetColor(ThemeColor color, bool updateHex = true)
    {
        _updating = true;
        SelectedColorHex = color.Hex;
        if (updateHex) HexInput.Text = color.Hex;
        Red.Value = color.R; Green.Value = color.G; Blue.Value = color.B;
        RedValue.Text = color.R.ToString(); GreenValue.Text = color.G.ToString(); BlueValue.Text = color.B.ToString();
        ColorPreview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        var foreground = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(ThemePalette.Custom(color.Hex).Text)!;
        PreviewTitle.Foreground = foreground; PreviewAnswer.Foreground = foreground;
        ApplyButton.IsEnabled = true; ErrorText.Text = ""; _updating = false;
    }
    private void HexInput_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_updating) return;
        if (ThemeColor.TryParse(HexInput.Text, out var color)) SetColor(color, false);
        else { ApplyButton.IsEnabled = false; ErrorText.Text = "Enter a valid HEX color, for example #241C3C."; }
    }
    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating) SetColor(new((byte)Red.Value, (byte)Green.Value, (byte)Blue.Value));
    }
    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string hex }) SetColor(ThemeColor.Parse(hex));
    }
    private void Apply_Click(object sender, RoutedEventArgs e) { if (ApplyButton.IsEnabled) DialogResult = true; }
    private async void Eyedropper_Click(object sender, RoutedEventArgs e)
    {
        EyedropperButton.IsEnabled = false; Opacity = 0;
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            // Let the compositor remove this picker before sampling.
            await Task.Delay(150);
            var hex = await ScreenColorPicker.PickAsync();
            if (hex is not null) SetColor(ThemeColor.Parse(hex));
        }
        catch { ErrorText.Text = "Couldn't sample this screen. You can still use HEX or RGB."; }
        finally { Opacity = 1; EyedropperButton.IsEnabled = true; Activate(); }
    }
}
