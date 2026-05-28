using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace OSDevIDE.Sim
{
    /// <summary>
    /// Auto-introspect HMI widgets. One widget per <see cref="Tag"/>:
    ///   BOOL    → labeled lamp with toggle button
    ///   TIMER   → labeled PRE / ACC / DN strip
    ///   COUNTER → labeled PRE / ACC / DN strip
    ///   NUMERIC → labeled read-only display + edit textbox
    ///   ARRAY   → first-N values strip
    ///
    /// The widgets bind via INotifyPropertyChanged on Tag, so the VM mutating
    /// tags causes immediate UI refresh.
    /// </summary>
    public static class HmiWidgets
    {
        private static readonly Brush PanelBg   = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
        private static readonly Brush PanelEdge = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        private static readonly Brush LabelFg   = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85));
        private static readonly Brush ValueFg   = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        private static readonly Brush LampOn    = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private static readonly Brush LampOff   = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x44));

        public static UIElement Build(Tag tag, TagDatabase db)
        {
            var card = new Border
            {
                Background = PanelBg,
                BorderBrush = PanelEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(6),
            };

            if (tag.ArraySize > 0)         card.Child = BuildArray(tag);
            else if (tag.IsBool)           card.Child = BuildBool(tag);
            else if (tag.IsStructured)     card.Child = BuildStructured(tag);
            else if (tag.IsUserStruct)     card.Child = BuildUserStruct(tag, db);
            else                           card.Child = BuildNumeric(tag);
            return card;
        }

        private static UIElement BuildUserStruct(Tag tag, TagDatabase db)
        {
            // Render a UDT-style card: header + one row per known member.
            // Members are discovered lazily (the sim adds them on first write),
            // so we rebuild the rows whenever the tag fires PropertyChanged.
            var sp = new StackPanel();
            sp.Children.Add(Header(tag, tag.DataType));
            var rows = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            sp.Children.Add(rows);

            void Rebuild()
            {
                rows.Children.Clear();
                foreach (var kv in tag.Members)
                {
                    var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var name = new TextBlock { Text = kv.Key, Foreground = LabelFg, FontSize = 11,
                                                FontFamily = new FontFamily("Consolas") };
                    var val  = new TextBlock { Text = FormatMember(kv.Value), Foreground = ValueFg,
                                                FontSize = 12, FontFamily = new FontFamily("Consolas"),
                                                FontWeight = FontWeights.SemiBold };
                    Grid.SetColumn(name, 0); Grid.SetColumn(val, 1);
                    row.Children.Add(name); row.Children.Add(val);
                    rows.Children.Add(row);
                }
                if (rows.Children.Count == 0)
                {
                    rows.Children.Add(new TextBlock
                    {
                        Text = "(no members yet — write one to populate)",
                        Foreground = LabelFg, FontSize = 10, FontStyle = FontStyles.Italic,
                    });
                }
            }

            Rebuild();
            tag.PropertyChanged += (s, e) => Rebuild();
            return sp;
        }

        private static string FormatMember(double v)
        {
            if (v == Math.Truncate(v) && Math.Abs(v) < 1e15) return ((long)v).ToString();
            return v.ToString("G6");
        }

        private static StackPanel Header(Tag tag, string subtitle)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = tag.Name, Foreground = ValueFg, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
            });
            sp.Children.Add(new TextBlock
            {
                Text = "  " + subtitle, Foreground = LabelFg, FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 0, 0),
            });
            return sp;
        }

        private static UIElement BuildBool(Tag tag)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(Header(tag, "BOOL"), 0);
            grid.Children.Add(Header(tag, "BOOL"));

            var lamp = new Ellipse_Like(tag);
            Grid.SetRow(lamp, 1);
            grid.Children.Add(lamp);
            return grid;
        }

        private static UIElement BuildNumeric(Tag tag)
        {
            var sp = new StackPanel();
            sp.Children.Add(Header(tag, tag.DataType));

            var box = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Foreground = ValueFg,
                BorderBrush = PanelEdge,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(0, 6, 0, 0),
                Text = tag.DisplayValue(),
            };
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    if (tag.IsReal && double.TryParse(box.Text, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
                        tag.Real = d;
                    else if (int.TryParse(box.Text, out var i))
                        tag.Int = i;
                    e.Handled = true;
                }
            };
            tag.PropertyChanged += (s, e) =>
            {
                if (!box.IsKeyboardFocused) box.Text = tag.DisplayValue();
            };
            sp.Children.Add(box);
            return sp;
        }

        private static UIElement BuildStructured(Tag tag)
        {
            var sp = new StackPanel();
            sp.Children.Add(Header(tag, tag.DataType));

            var line = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pre = MakeField("PRE", tag, nameof(Tag.PRE));
            var acc = MakeField("ACC", tag, nameof(Tag.ACC));
            var dn  = new Ellipse_Like(tag, useDn: true) { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };

            Grid.SetColumn(pre, 0);
            Grid.SetColumn(acc, 1);
            Grid.SetColumn(dn, 2);
            line.Children.Add(pre); line.Children.Add(acc); line.Children.Add(dn);
            sp.Children.Add(line);
            return sp;
        }

        private static UIElement MakeField(string label, Tag tag, string prop)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            sp.Children.Add(new TextBlock { Text = label, Foreground = LabelFg, FontSize = 10 });
            var tb = new TextBlock { Foreground = ValueFg, FontFamily = new FontFamily("Consolas"), FontSize = 14 };
            var binding = new Binding(prop) { Source = tag, Mode = BindingMode.OneWay };
            tb.SetBinding(TextBlock.TextProperty, binding);
            sp.Children.Add(tb);
            return sp;
        }

        private static UIElement BuildArray(Tag tag)
        {
            var sp = new StackPanel();
            sp.Children.Add(Header(tag, $"{tag.DataType}[{tag.ArraySize}]"));
            var inner = new TextBlock
            {
                Foreground = ValueFg, FontFamily = new FontFamily("Consolas"), FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                Text = PreviewArray(tag),
            };
            tag.PropertyChanged += (s, e) => inner.Text = PreviewArray(tag);
            sp.Children.Add(inner);
            return sp;
        }

        private static string PreviewArray(Tag tag)
        {
            if (tag.Array == null) return "[]";
            int n = Math.Min(8, tag.ArraySize);
            var parts = new string[n];
            for (int i = 0; i < n; i++) parts[i] = tag.Array[i]?.ToString() ?? "0";
            return "[" + string.Join(", ", parts) + (tag.ArraySize > 8 ? ", …]" : "]");
        }

        /// <summary>
        /// A clickable lamp showing the boolean state of a tag. For TIMER /
        /// COUNTER, displays the DN bit if <paramref name="useDn"/> is true.
        /// Click toggles the bool (only meaningful in Program mode; in Run mode
        /// the VM may re-drive it next scan).
        /// </summary>
        private class Ellipse_Like : Border
        {
            private readonly Tag _tag;
            private readonly bool _useDn;
            public Ellipse_Like(Tag tag, bool useDn = false)
            {
                _tag = tag;
                _useDn = useDn;
                Width = 64; Height = 28;
                CornerRadius = new CornerRadius(14);
                HorizontalAlignment = HorizontalAlignment.Left;
                Margin = new Thickness(0, 8, 0, 0);
                Cursor = Cursors.Hand;
                Update();
                tag.PropertyChanged += (s, e) => Update();
                MouseLeftButtonDown += (s, e) =>
                {
                    if (useDn) tag.DN = !tag.DN;
                    else       tag.Bool = !tag.Bool;
                };
            }
            private void Update()
            {
                bool on = _useDn ? _tag.DN : _tag.Bool;
                Background = on ? LampOn : LampOff;
            }
        }
    }
}
