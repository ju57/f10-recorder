using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Platform;

namespace F10Recorder;

internal static class Ui
{
    public static readonly IBrush Background = Brush("#171B19"), Surface = Brush("#202622"), Input = Brush("#29312C"), Border = Brush("#344239"), Text = Brush("#EDF4EF"), Muted = Brush("#A5B5AB"), Accent = Brush("#53C58B"), Warning = Brush("#E6BB75");
    static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
    public static TextBlock Label(string text, double size = 14, IBrush color = null, FontWeight? weight = null) => new() { Text = text, FontSize = size, Foreground = color ?? Text, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    public static Button Button(string text, Action action = null, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 10), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        if (primary) button.Classes.Add("primary"); if (action != null) button.Click += (_, _) => action(); return button;
    }
    public static void AddStyles(Styles styles)
    {
        styles.Add(new Style(s => s.OfType<Window>()).Set(TemplatedControl.FontFamilyProperty, new FontFamily("Segoe UI, Noto Sans")).Set(TemplatedControl.ForegroundProperty, Text));
        styles.Add(new Style(s => s.OfType<Button>()).Set(TemplatedControl.BackgroundProperty, Input).Set(TemplatedControl.ForegroundProperty, Text).Set(TemplatedControl.BorderBrushProperty, Border).Set(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)));
        styles.Add(new Style(s => s.OfType<TextBox>()).Set(TemplatedControl.BackgroundProperty, Input).Set(TemplatedControl.ForegroundProperty, Text).Set(TemplatedControl.BorderBrushProperty, Border));
        styles.Add(new Style(s => s.OfType<ComboBox>()).Set(TemplatedControl.BackgroundProperty, Input).Set(TemplatedControl.ForegroundProperty, Text).Set(TemplatedControl.BorderBrushProperty, Border));
        styles.Add(new Style(s => s.OfType<Button>().Class("primary")).Set(TemplatedControl.BackgroundProperty, Accent).Set(TemplatedControl.ForegroundProperty, Background));
        styles.Add(new Style(s => s.OfType<Button>().Class("primary").Class(":pointerover")).Set(TemplatedControl.BackgroundProperty, Brush("#6CD69F")));
    }
    static WindowIcon readyIcon, warningIcon;
    public static WindowIcon TrayIcon(bool ready)
    {
        if (readyIcon == null)
        {
            readyIcon = new WindowIcon(AssetLoader.Open(new Uri("avares://F10Recorder.Desktop/Assets/ready.png"))); warningIcon = new WindowIcon(AssetLoader.Open(new Uri("avares://F10Recorder.Desktop/Assets/warning.png")));
        }
        return ready ? readyIcon : warningIcon;
    }
}

internal static class StyleExtensions
{
    public static Style Set<T>(this Style style, AvaloniaProperty<T> property, T value) { style.Setters.Add(new Setter(property, value)); return style; }
}
