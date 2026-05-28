using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OSDevIDE.Sim
{
    /// <summary>
    /// Minimal numeric-force prompt. Used by SimWindow's tag-context-menu
    /// "Force value…" command — operator types a value, hits Enter, and the
    /// VM treats the tag as locked to that value until Unforce.
    /// </summary>
    public class ForceValueDialog : Window
    {
        public double Value { get; private set; }

        public ForceValueDialog(Tag tag)
        {
            Title = $"Force {tag.Name}";
            Width = 320; Height = 160;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22));
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
            ResizeMode = ResizeMode.NoResize;
            FontFamily = new FontFamily("Segoe UI");

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock
            {
                Text = $"Force value for {tag.Name} ({tag.DataType}):",
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0x9C, 0x9C)),
            });
            var box = new TextBox
            {
                Text = tag.AsDouble().ToString("G6", System.Globalization.CultureInfo.InvariantCulture),
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x55)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x6A)),
                FontFamily = new FontFamily("Consolas"), FontSize = 16,
                Padding = new Thickness(6, 4, 6, 4),
            };
            box.Loaded += (s, e) => { box.SelectAll(); box.Focus(); };
            root.Children.Add(box);

            var btns = new StackPanel { Orientation = Orientation.Horizontal,
                                         HorizontalAlignment = HorizontalAlignment.Right,
                                         Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4),
                Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5E)),
                Margin = new Thickness(0, 0, 6, 0) };
            cancel.Click += (s, e) => { DialogResult = false; Close(); };
            var ok = new Button { Content = "Force", Padding = new Thickness(14, 4, 14, 4),
                Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x2D, 0x0D)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x55)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x40, 0x15)),
                FontWeight = FontWeights.SemiBold };
            void Commit() {
                if (double.TryParse(box.Text, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    Value = v;
                    DialogResult = true;
                    Close();
                }
            }
            ok.Click += (s, e) => Commit();
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) Commit();
                if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            };
            btns.Children.Add(cancel);
            btns.Children.Add(ok);
            root.Children.Add(btns);
            Content = root;
        }
    }
}
