using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OSDevIDE.Sim
{
    /// <summary>
    /// Industrial-style HMI widgets. Each widget is a <see cref="FrameworkElement"/>
    /// that subscribes to its bound <see cref="Tag"/>'s PropertyChanged event
    /// and re-renders itself with semantic colors / animations / numeric scales.
    ///
    /// All widgets:
    ///   • Inherit from <see cref="HmiWidgetBase"/>, which routes mouse events
    ///     out to the designer (selection + drag) when in design mode.
    ///   • Re-call <see cref="HmiWidgetBase.Refresh"/> whenever the bound tag
    ///     fires PropertyChanged (marshalled to the dispatcher).
    ///   • Read additional properties off the <see cref="HmiWidgetModel"/>:
    ///     Min/Max, LowAlarm/HighAlarm, Units, Format, etc.
    /// </summary>
    public static class ThemedWidgets
    {
        public static IReadOnlyList<string> PaletteTypes { get; } = new[] {
            // Inputs
            "Button", "Toggle", "Selector", "NumberEntry",
            // Outputs / actuators
            "Lamp", "Valve", "Pump", "Motor",
            // Process / analog
            "Tank", "Flame", "SteamStack", "Gauge", "PressureGauge", "Bargraph",
            // Indicators / text
            "NumberDisplay", "Label", "AlarmStrip", "PIDBlock",
            // Trend
            "Trend",
        };

        public static HmiWidgetBase Build(HmiWidgetModel m, TagDatabase db, bool designMode)
        {
            HmiWidgetBase w = m.Type switch
            {
                "Lamp"           => new LampWidget(m, db),
                "Button"         => new ButtonWidget(m, db),
                "Toggle"         => new ToggleWidget(m, db),
                "NumberDisplay"  => new NumberDisplayWidget(m, db),
                "NumberEntry"    => new NumberEntryWidget(m, db),
                "Label"          => new LabelWidget(m, db),
                "Tank"           => new TankWidget(m, db),
                "Flame"          => new FlameWidget(m, db),
                "Valve"          => new ValveWidget(m, db),
                "Pump"           => new PumpWidget(m, db),
                "Motor"          => new MotorWidget(m, db),
                "Gauge"          => new GaugeWidget(m, db),
                "PressureGauge"  => new PressureGaugeWidget(m, db),
                "Bargraph"       => new BargraphWidget(m, db),
                "SteamStack"     => new SteamStackWidget(m, db),
                "AlarmStrip"     => new AlarmStripWidget(m, db),
                "Selector"       => new SelectorWidget(m, db),
                "PIDBlock"       => new PIDBlockWidget(m, db),
                "Trend"          => new TrendWidget(m, db),
                _                => new LampWidget(m, db),
            };
            w.ApplyDesignMode(designMode);
            return w;
        }
    }

    // ======================================================================
    //                              BASE CLASS
    // ======================================================================

    public abstract class HmiWidgetBase : UserControl
    {
        public HmiWidgetModel Model { get; }
        protected TagDatabase Db { get; }
        public bool DesignMode { get; private set; }
        public event Action<HmiWidgetBase>? WidgetSelected;

        // -------- Industrial palette (used by every widget) --------
        protected static readonly Color C_Bg          = Color.FromRgb(0x1B, 0x1B, 0x22);
        protected static readonly Color C_BgDeep      = Color.FromRgb(0x10, 0x10, 0x16);
        protected static readonly Color C_Edge        = Color.FromRgb(0x3F, 0x3F, 0x46);
        protected static readonly Color C_EdgeBright  = Color.FromRgb(0x60, 0x60, 0x6A);
        protected static readonly Color C_Label       = Color.FromRgb(0x85, 0x85, 0x85);
        protected static readonly Color C_Value       = Color.FromRgb(0xE6, 0xE6, 0xE6);
        protected static readonly Color C_Accent      = Color.FromRgb(0x4E, 0xC9, 0xB0);   // green run
        protected static readonly Color C_AccentDim   = Color.FromRgb(0x2A, 0x55, 0x4A);
        protected static readonly Color C_Warn        = Color.FromRgb(0xFF, 0xC1, 0x07);   // amber
        protected static readonly Color C_Crit        = Color.FromRgb(0xE6, 0x3E, 0x3E);   // red alarm
        protected static readonly Color C_Info        = Color.FromRgb(0x40, 0xA8, 0xFF);   // blue
        protected static readonly Color C_Flame1      = Color.FromRgb(0xFF, 0x60, 0x10);
        protected static readonly Color C_Flame2      = Color.FromRgb(0xFF, 0xC1, 0x07);
        protected static readonly Color C_Water       = Color.FromRgb(0x33, 0x99, 0xFF);
        protected static readonly Color C_WaterLight  = Color.FromRgb(0x80, 0xC8, 0xFF);
        protected static readonly Color C_Steam       = Color.FromRgb(0xCF, 0xD8, 0xE0);

        protected static SolidColorBrush B(Color c) => new(c);
        protected static LinearGradientBrush VertGrad(Color top, Color bottom) =>
            new(top, bottom, new Point(0, 0), new Point(0, 1));
        protected static LinearGradientBrush HorzGrad(Color left, Color right) =>
            new(left, right, new Point(0, 0), new Point(1, 0));
        protected static RadialGradientBrush Lens(Color core, Color edge) =>
            new(core, edge) { Center = new Point(0.35, 0.35), GradientOrigin = new Point(0.35, 0.35), RadiusX = 0.7, RadiusY = 0.7 };

        /// <summary>Switch design ↔ run; in design mode inner content is
        /// hit-test-invisible so the outer UserControl receives mouse events.</summary>
        public void ApplyDesignMode(bool design)
        {
            DesignMode = design;
            if (Content is UIElement c) c.IsHitTestVisible = !design;
            Cursor = design ? Cursors.SizeAll : Cursors.Hand;
        }

        protected HmiWidgetBase(HmiWidgetModel m, TagDatabase db)
        {
            Model = m;
            Db = db;
            Width = m.W;
            Height = m.H;
            Background = System.Windows.Media.Brushes.Transparent;
            Focusable = true;
            Cursor = Cursors.Hand;
            ToolTip = $"{m.Type}: {m.Tag ?? "(unbound)"}";

            if (!string.IsNullOrEmpty(m.Tag))
            {
                var tag = ResolveTag(m.Tag);
                if (tag != null) tag.PropertyChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Refresh));
            }

            MouseLeftButtonDown += (s, e) =>
            {
                WidgetSelected?.Invoke(this);
                if (!DesignMode) HandleClick();
                e.Handled = false;
            };
        }

        protected Tag? ResolveTag(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            int dot = name.IndexOf('.');
            int br = name.IndexOf('[');
            string baseName = name;
            if (dot > 0 && (br < 0 || dot < br)) baseName = name.Substring(0, dot);
            else if (br > 0) baseName = name.Substring(0, br);
            return Db.Find(baseName);
        }

        protected double ReadNum() => string.IsNullOrEmpty(Model.Tag) ? 0 : Db.ReadOperand(Model.Tag!);
        protected bool ReadBool() => !string.IsNullOrEmpty(Model.Tag) && Db.ReadBoolOperand(Model.Tag!);
        protected void WriteBool(bool v) { if (!string.IsNullOrEmpty(Model.Tag)) Db.WriteBoolOperand(Model.Tag!, v); }
        protected void WriteNum(double v) { if (!string.IsNullOrEmpty(Model.Tag)) Db.WriteOperand(Model.Tag!, v); }

        /// <summary>Classify numeric tag state: Normal / Low / High based on alarm bands.</summary>
        protected (Color color, string state) ClassifyAnalog(double v)
        {
            if (Model.LowAlarm == 0 && Model.HighAlarm == 0) return (C_Accent, "");
            if (v <= Model.LowAlarm)  return (C_Crit, "LOW");
            if (v >= Model.HighAlarm) return (C_Crit, "HIGH");
            double range = Model.HighAlarm - Model.LowAlarm;
            if (range > 0)
            {
                double margin = range * 0.1;
                if (v <= Model.LowAlarm + margin || v >= Model.HighAlarm - margin)
                    return (C_Warn, "WARN");
            }
            return (C_Accent, "OK");
        }

        public abstract void Refresh();
        protected virtual void HandleClick() { }

        protected static FontFamily Mono => new("Consolas");

        protected static TextBlock MakeLabel(string text, double sz, Color c, FontWeight? fw = null) => new()
        {
            Text = text, FontSize = sz, Foreground = B(c),
            FontWeight = fw ?? FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    // ======================================================================
    //                            INPUT WIDGETS
    // ======================================================================

    public class LampWidget : HmiWidgetBase
    {
        private readonly Border _bezel = new();
        private readonly Ellipse _lens = new();
        private readonly Ellipse _highlight = new();
        private readonly TextBlock _label = new();
        private readonly TextBlock _state = new();

        public LampWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _bezel.CornerRadius = new CornerRadius(100);
            _bezel.Background = VertGrad(Color.FromRgb(0x40, 0x40, 0x48), Color.FromRgb(0x18, 0x18, 0x1E));
            _bezel.BorderBrush = B(C_EdgeBright);
            _bezel.BorderThickness = new Thickness(2);
            _bezel.Margin = new Thickness(8);
            _bezel.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2, Opacity = 0.6 };
            var lensGrid = new Grid();
            _lens.HorizontalAlignment = HorizontalAlignment.Stretch;
            _lens.VerticalAlignment = VerticalAlignment.Stretch;
            _lens.Margin = new Thickness(6);
            _highlight.Width = 12; _highlight.Height = 6;
            _highlight.HorizontalAlignment = HorizontalAlignment.Center;
            _highlight.VerticalAlignment = VerticalAlignment.Top;
            _highlight.Margin = new Thickness(0, 10, 0, 0);
            _highlight.Fill = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
            lensGrid.Children.Add(_lens);
            lensGrid.Children.Add(_highlight);
            _bezel.Child = lensGrid;
            Grid.SetRow(_bezel, 0);
            grid.Children.Add(_bezel);

            _state.FontSize = 10;
            _state.HorizontalAlignment = HorizontalAlignment.Center;
            _state.FontWeight = FontWeights.Bold;
            _state.FontFamily = Mono;
            Grid.SetRow(_state, 1); grid.Children.Add(_state);

            _label.FontSize = 11;
            _label.Foreground = B(C_Label);
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(_label, 2); grid.Children.Add(_label);

            Content = grid;
            Refresh();
        }

        public override void Refresh()
        {
            bool on = ReadBool();
            _lens.Fill = on ? Lens(Color.FromRgb(0xB0, 0xFF, 0xE0), C_Accent)
                            : Lens(Color.FromRgb(0x30, 0x35, 0x3A), Color.FromRgb(0x18, 0x1A, 0x1F));
            _highlight.Opacity = on ? 0.9 : 0.3;
            _state.Text = on ? "ON" : "OFF";
            _state.Foreground = on ? B(C_Accent) : B(C_Label);
            _label.Text = string.IsNullOrEmpty(Model.Label) ? Model.Tag ?? "" : Model.Label;
        }

        protected override void HandleClick() => WriteBool(!ReadBool());
    }

    /// <summary>
    /// Industrial pushbutton with configurable behavior modes:
    ///   "Momentary" (default) — writes 1 on press, 0 on release / mouse-leave
    ///   "Latching"            — click toggles bound bool (sticky on/off)
    ///   "Set"                 — writes 1 on click, leaves it (latched on)
    ///   "Reset"               — writes 0 on click, leaves it (latched off);
    ///                           if a "reset target" is configured via the
    ///                           Format property (e.g. Format = "ResetTag:start"),
    ///                           clicking ALSO writes 0 to that other tag —
    ///                           classic Start/Stop seal-in clear.
    ///
    /// Mode is read from <see cref="HmiWidgetModel.Mode"/>; defaults to
    /// "Momentary" when empty. The Format property doubles as a hook for
    /// "ResetTag:&lt;name&gt;" so the Reset button can clear a partner tag
    /// (the START's tag) on click — common industrial Stop-button behavior.
    /// </summary>
    public class ButtonWidget : HmiWidgetBase
    {
        private readonly Border _body = new();
        private readonly TextBlock _text = new();
        private readonly TextBlock _modeBadge = new();
        public ButtonWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            _body.CornerRadius = new CornerRadius(4);
            _body.BorderBrush = B(C_EdgeBright);
            _body.BorderThickness = new Thickness(2);
            _body.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 2, Opacity = 0.5 };
            _text.FontWeight = FontWeights.Bold;
            _text.FontSize = 14;
            _text.HorizontalAlignment = HorizontalAlignment.Center;
            _text.VerticalAlignment = VerticalAlignment.Center;
            _text.Foreground = B(C_Value);

            _modeBadge.FontSize = 8;
            _modeBadge.FontFamily = Mono;
            _modeBadge.HorizontalAlignment = HorizontalAlignment.Right;
            _modeBadge.VerticalAlignment = VerticalAlignment.Top;
            _modeBadge.Margin = new Thickness(0, 2, 4, 0);
            _modeBadge.Foreground = B(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

            var grid = new Grid();
            grid.Children.Add(_text);
            grid.Children.Add(_modeBadge);
            _body.Child = grid;
            Content = _body;

            _body.PreviewMouseLeftButtonDown += OnDown;
            _body.PreviewMouseLeftButtonUp   += OnUp;
            _body.MouseLeave                 += OnLeave;
            Refresh();
        }

        private string Mode => string.IsNullOrEmpty(Model.Mode) ? "Momentary" : Model.Mode!;

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (DesignMode) return;
            switch (Mode)
            {
                case "Momentary": WriteBool(true); break;
                case "Latching":  WriteBool(!ReadBool()); break;
                case "Set":       WriteBool(true);  HandleResetTarget(true);  break;
                case "Reset":     WriteBool(false); HandleResetTarget(false); break;
            }
        }
        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (DesignMode) return;
            if (Mode == "Momentary") WriteBool(false);
        }
        private void OnLeave(object sender, MouseEventArgs e)
        {
            if (DesignMode) return;
            if (Mode == "Momentary") WriteBool(false);
        }

        /// <summary>
        /// If the user wrote `Format = "ResetTag:start"` on a Reset/Set button,
        /// also write the opposite value to that partner tag. Lets a STOP
        /// button (Reset on its own tag `stop`=1) ALSO clear `start`=0 — the
        /// classic Start/Stop seal-in release.
        /// </summary>
        private void HandleResetTarget(bool pressedValue)
        {
            if (string.IsNullOrEmpty(Model.Format)) return;
            var fmt = Model.Format!;
            const string prefix = "ResetTag:";
            if (!fmt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            var partner = fmt.Substring(prefix.Length).Trim();
            if (string.IsNullOrEmpty(partner)) return;
            // Whatever we wrote to our own tag, write the OPPOSITE to partner.
            Db.WriteBoolOperand(partner, !pressedValue);
        }

        public override void Refresh()
        {
            bool on = ReadBool();
            _body.Background = on
                ? VertGrad(Color.FromRgb(0x6F, 0xE0, 0xC0), Color.FromRgb(0x2E, 0x80, 0x60))
                : VertGrad(Color.FromRgb(0x50, 0x50, 0x58), Color.FromRgb(0x2A, 0x2A, 0x30));
            _text.Text = string.IsNullOrEmpty(Model.Label) ? (Model.Tag ?? "BTN") : Model.Label;
            _text.Foreground = on ? Brushes.Black : B(C_Value);
            string mode = Mode;
            _modeBadge.Text = mode == "Momentary" ? "" : mode.ToUpperInvariant();
            _modeBadge.Foreground = on ? new SolidColorBrush(Color.FromArgb(0xAA, 0x00, 0x00, 0x00))
                                       : new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
            ToolTip = $"Button [{mode}] → {Model.Tag ?? "(unbound)"}";
        }
    }

    public class ToggleWidget : HmiWidgetBase
    {
        private readonly Border _body = new();
        private readonly Border _knob = new();
        private readonly TextBlock _label = new();
        public ToggleWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _body.CornerRadius = new CornerRadius(100);
            _body.Background = VertGrad(Color.FromRgb(0x18, 0x18, 0x1F), Color.FromRgb(0x28, 0x28, 0x33));
            _body.BorderBrush = B(C_Edge);
            _body.BorderThickness = new Thickness(2);
            _body.Margin = new Thickness(4);
            var inner = new Grid();
            _knob.Background = VertGrad(Color.FromRgb(0xF0, 0xF0, 0xF0), Color.FromRgb(0x90, 0x90, 0x9A));
            _knob.BorderBrush = B(C_EdgeBright); _knob.BorderThickness = new Thickness(1);
            _knob.CornerRadius = new CornerRadius(100);
            _knob.Margin = new Thickness(3);
            _knob.HorizontalAlignment = HorizontalAlignment.Left;
            inner.Children.Add(_knob);
            _body.Child = inner;
            Grid.SetRow(_body, 0); grid.Children.Add(_body);

            _label.FontSize = 11; _label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(_label, 1); grid.Children.Add(_label);

            _body.PreviewMouseLeftButtonDown += (s, e) => { if (!DesignMode) WriteBool(!ReadBool()); };
            SizeChanged += (s, e) => Refresh();
            Content = grid;
            Refresh();
        }
        public override void Refresh()
        {
            bool on = ReadBool();
            _body.Background = on
                ? VertGrad(Color.FromRgb(0x10, 0x60, 0x4A), Color.FromRgb(0x2C, 0xA8, 0x88))
                : VertGrad(Color.FromRgb(0x18, 0x18, 0x1F), Color.FromRgb(0x28, 0x28, 0x33));
            double h = Math.Max(20, _body.ActualHeight - 8);
            _knob.Width = h; _knob.Height = h;
            _knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _label.Foreground = on ? B(C_Accent) : B(C_Label);
            _label.Text = (string.IsNullOrEmpty(Model.Label) ? (Model.Tag ?? "TOGGLE") : Model.Label) + (on ? "  ON" : "  OFF");
        }
    }

    public class NumberDisplayWidget : HmiWidgetBase
    {
        private readonly Border _frame = new();
        private readonly TextBlock _label = new();
        private readonly TextBlock _value = new();
        private readonly TextBlock _units = new();

        public NumberDisplayWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            _frame.Background = VertGrad(Color.FromRgb(0x0F, 0x10, 0x14), Color.FromRgb(0x1A, 0x1B, 0x22));
            _frame.BorderBrush = B(C_Edge);
            _frame.BorderThickness = new Thickness(1);
            _frame.CornerRadius = new CornerRadius(4);
            _frame.Padding = new Thickness(10, 6, 10, 6);
            _frame.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1, Opacity = 0.5 };

            var sp = new StackPanel();
            _label.Foreground = B(C_Label); _label.FontSize = 10; _label.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(_label);
            var valLine = new StackPanel { Orientation = Orientation.Horizontal };
            _value.Foreground = B(C_Accent); _value.FontSize = 26; _value.FontWeight = FontWeights.Bold;
            _value.FontFamily = Mono; _value.VerticalAlignment = VerticalAlignment.Bottom;
            _units.Foreground = B(C_Label); _units.FontSize = 12; _units.Margin = new Thickness(4, 0, 0, 6);
            _units.VerticalAlignment = VerticalAlignment.Bottom;
            valLine.Children.Add(_value);
            valLine.Children.Add(_units);
            sp.Children.Add(valLine);
            _frame.Child = sp;
            Content = _frame;
            Refresh();
        }
        public override void Refresh()
        {
            _label.Text = (Model.Label ?? Model.Tag ?? "").ToUpperInvariant();
            var v = ReadNum();
            _value.Text = string.IsNullOrEmpty(Model.Format) ? v.ToString("G6") : v.ToString(Model.Format);
            _units.Text = Model.Units ?? "";
            var (c, _) = ClassifyAnalog(v);
            _value.Foreground = B(c);
        }
    }

    public class NumberEntryWidget : HmiWidgetBase
    {
        private readonly TextBox _box = new();
        private readonly TextBlock _label = new();
        public NumberEntryWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var sp = new StackPanel();
            _label.Foreground = B(C_Label); _label.FontSize = 10; _label.FontWeight = FontWeights.SemiBold;
            _box.Background = B(C_BgDeep); _box.Foreground = B(C_Value); _box.BorderBrush = B(C_Edge);
            _box.FontFamily = Mono; _box.FontSize = 16; _box.Padding = new Thickness(6, 3, 6, 3);
            _box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && double.TryParse(_box.Text,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    WriteNum(d); e.Handled = true;
                }
            };
            sp.Children.Add(_label); sp.Children.Add(_box);
            Content = sp;
            Refresh();
        }
        public override void Refresh()
        {
            _label.Text = (Model.Label ?? Model.Tag ?? "").ToUpperInvariant();
            if (!_box.IsKeyboardFocused)
            {
                var v = ReadNum();
                string s = string.IsNullOrEmpty(Model.Format) ? v.ToString("G6") : v.ToString(Model.Format);
                _box.Text = string.IsNullOrEmpty(Model.Units) ? s : $"{s} {Model.Units}";
            }
        }
    }

    public class LabelWidget : HmiWidgetBase
    {
        private readonly TextBlock _tb = new();
        public LabelWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            _tb.Foreground = B(C_Value); _tb.FontSize = 14; _tb.FontWeight = FontWeights.SemiBold;
            _tb.TextWrapping = TextWrapping.Wrap;
            _tb.VerticalAlignment = VerticalAlignment.Center;
            Content = _tb;
            Refresh();
        }
        public override void Refresh()
        {
            _tb.Text = Model.Label ?? Model.Tag ?? "Label";
        }
    }

    /// <summary>
    /// 3-position rotary selector (HAND / OFF / AUTO style). Click rotates
    /// to the next position. Writes 0/1/2 to the bound numeric tag.
    /// </summary>
    public class SelectorWidget : HmiWidgetBase
    {
        private readonly Ellipse _body = new();
        private readonly Line _indicator = new();
        private readonly TextBlock _posText = new();
        private readonly TextBlock _label = new();
        public SelectorWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            _body.Stroke = B(C_EdgeBright); _body.StrokeThickness = 2;
            _body.Fill = VertGrad(Color.FromRgb(0x40, 0x40, 0x48), Color.FromRgb(0x18, 0x18, 0x1E));
            _indicator.Stroke = B(C_Warn); _indicator.StrokeThickness = 4;
            _posText.HorizontalAlignment = HorizontalAlignment.Center;
            _posText.VerticalAlignment = VerticalAlignment.Bottom;
            _posText.Foreground = B(C_Value); _posText.FontSize = 10; _posText.FontWeight = FontWeights.Bold;
            _posText.FontFamily = Mono;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Top;
            _label.Foreground = B(C_Label); _label.FontSize = 10;
            grid.Children.Add(_body); grid.Children.Add(_indicator);
            grid.Children.Add(_posText); grid.Children.Add(_label);
            Content = grid;
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }
        protected override void HandleClick()
        {
            int pos = (int)ReadNum();
            pos = (pos + 1) % 3;
            WriteNum(pos);
        }
        public override void Refresh()
        {
            double w = Math.Max(40, ActualWidth);
            double h = Math.Max(40, ActualHeight - 12);
            double cx = w / 2, cy = h / 2 + 4;
            double r = Math.Min(w, h) * 0.38;
            _body.Width = r * 2; _body.Height = r * 2;
            _body.HorizontalAlignment = HorizontalAlignment.Center;
            _body.VerticalAlignment = VerticalAlignment.Center;
            int pos = Math.Clamp((int)ReadNum(), 0, 2);
            double[] angles = { -Math.PI / 2, 0, Math.PI / 2 };  // up, right, down
            double a = angles[pos];
            _indicator.X1 = cx; _indicator.Y1 = cy;
            _indicator.X2 = cx + r * 0.85 * Math.Cos(a);
            _indicator.Y2 = cy + r * 0.85 * Math.Sin(a);
            _posText.Text = pos switch { 0 => "HAND", 1 => "OFF", 2 => "AUTO", _ => "?" };
            _label.Text = Model.Label ?? Model.Tag ?? "";
        }
    }

    // ======================================================================
    //                          PROCESS / ANALOG
    // ======================================================================

    public class TankWidget : HmiWidgetBase
    {
        private readonly Rectangle _shell = new();
        private readonly Rectangle _fillRect = new();
        private readonly Rectangle _glassHighlight = new();
        private readonly TextBlock _label = new();
        private readonly TextBlock _val = new();
        private readonly Grid _ticks = new();
        private readonly TextBlock _state = new();

        public TankWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            _shell.RadiusX = 8; _shell.RadiusY = 8;
            _shell.Stroke = B(C_EdgeBright);
            _shell.StrokeThickness = 2;
            _shell.Fill = VertGrad(Color.FromRgb(0x10, 0x12, 0x18), Color.FromRgb(0x1F, 0x22, 0x2C));

            _fillRect.RadiusX = 4; _fillRect.RadiusY = 4;
            _fillRect.VerticalAlignment = VerticalAlignment.Bottom;
            _fillRect.HorizontalAlignment = HorizontalAlignment.Stretch;
            _fillRect.Margin = new Thickness(6);

            _glassHighlight.HorizontalAlignment = HorizontalAlignment.Left;
            _glassHighlight.VerticalAlignment = VerticalAlignment.Stretch;
            _glassHighlight.Margin = new Thickness(10, 8, 0, 8);
            _glassHighlight.Width = 8;
            _glassHighlight.RadiusX = 4; _glassHighlight.RadiusY = 4;
            _glassHighlight.Fill = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));

            _val.Foreground = B(C_Value); _val.FontSize = 14; _val.FontWeight = FontWeights.Bold;
            _val.HorizontalAlignment = HorizontalAlignment.Center;
            _val.VerticalAlignment = VerticalAlignment.Center;
            _val.FontFamily = Mono;
            _val.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Opacity = 0.8 };

            _state.HorizontalAlignment = HorizontalAlignment.Right;
            _state.VerticalAlignment = VerticalAlignment.Top;
            _state.Margin = new Thickness(0, 4, 6, 0);
            _state.FontSize = 9; _state.FontWeight = FontWeights.Bold; _state.FontFamily = Mono;

            _label.Foreground = B(C_Label); _label.FontSize = 10;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Bottom;
            _label.Margin = new Thickness(0, 0, 0, 2);

            grid.Children.Add(_shell);
            grid.Children.Add(_fillRect);
            grid.Children.Add(_glassHighlight);
            grid.Children.Add(_ticks);
            grid.Children.Add(_val);
            grid.Children.Add(_state);
            grid.Children.Add(_label);
            Content = grid;

            SizeChanged += (s, e) => Refresh();
            Refresh();
        }

        public override void Refresh()
        {
            double v = ReadNum();
            double min = Model.Min, max = Model.Max;
            if (max <= min) max = min + 100;
            double pct = Math.Clamp((v - min) / (max - min), 0, 1);
            double innerH = Math.Max(0, ActualHeight - 12);
            _fillRect.Height = innerH * pct;

            var (alarmColor, state) = ClassifyAnalog(v);
            _fillRect.Fill = VertGrad(
                Color.FromArgb(0xFF, alarmColor.R, alarmColor.G, alarmColor.B),
                Color.FromArgb(0xCC,
                    (byte)Math.Max(0, alarmColor.R - 30),
                    (byte)Math.Max(0, alarmColor.G - 30),
                    (byte)Math.Max(0, alarmColor.B - 30)));

            _val.Text = string.IsNullOrEmpty(Model.Format) ? v.ToString("F1") : v.ToString(Model.Format);
            if (!string.IsNullOrEmpty(Model.Units)) _val.Text += " " + Model.Units;

            _state.Text = state;
            _state.Foreground = B(state == "OK" || string.IsNullOrEmpty(state) ? C_Accent
                                : state == "WARN" ? C_Warn : C_Crit);

            _label.Text = Model.Label ?? Model.Tag ?? "";

            BuildTicks();
        }

        private void BuildTicks()
        {
            _ticks.Children.Clear();
            double h = ActualHeight - 12;
            if (h <= 0) return;
            for (int i = 0; i <= 10; i++)
            {
                var ln = new Line
                {
                    X1 = 0, X2 = (i % 5 == 0) ? 8 : 4,
                    Y1 = 0, Y2 = 0,
                    Stroke = B(C_EdgeBright),
                    StrokeThickness = 1,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 6 + i * h / 10, 6, 0),
                    SnapsToDevicePixels = true,
                };
                _ticks.Children.Add(ln);
            }
        }
    }

    public class FlameWidget : HmiWidgetBase
    {
        private readonly Path _outerFlame = new();
        private readonly Path _innerFlame = new();
        private readonly Path _coreFlame = new();
        private readonly Ellipse _glow = new();
        private readonly TextBlock _label = new();
        private readonly TextBlock _state = new();
        private readonly DispatcherTimer _pulse;
        private double _phase;

        public FlameWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            _glow.Fill = new RadialGradientBrush(
                Color.FromArgb(0x80, 0xFF, 0x80, 0x20), Color.FromArgb(0x00, 0xFF, 0x80, 0x20));
            _outerFlame.Fill = B(C_Flame1);
            _innerFlame.Fill = B(C_Flame2);
            _coreFlame.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xE0));
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Bottom;
            _label.Foreground = B(C_Label); _label.FontSize = 10;
            _state.HorizontalAlignment = HorizontalAlignment.Right;
            _state.VerticalAlignment = VerticalAlignment.Top;
            _state.Margin = new Thickness(0, 2, 4, 0);
            _state.FontSize = 9; _state.FontWeight = FontWeights.Bold;
            _state.FontFamily = Mono;
            grid.Children.Add(_glow);
            grid.Children.Add(_outerFlame);
            grid.Children.Add(_innerFlame);
            grid.Children.Add(_coreFlame);
            grid.Children.Add(_state);
            grid.Children.Add(_label);
            Content = grid;

            _pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _pulse.Tick += (s, e) => { _phase += 0.5; if (_phase > Math.PI * 4) _phase -= Math.PI * 4; Refresh(); };
            _pulse.Start();

            SizeChanged += (s, e) => Refresh();
            Refresh();
        }
        public override void Refresh()
        {
            bool on = ReadBool();
            double w = Math.Max(20, ActualWidth);
            double h = Math.Max(20, ActualHeight - 12);
            double amp = on ? (1.0 + 0.08 * Math.Sin(_phase) + 0.04 * Math.Sin(_phase * 1.7)) : 0.18;
            _outerFlame.Data = FlameGeometry(w, h, amp, 1.0);
            _innerFlame.Data = FlameGeometry(w, h, amp * 0.95, 0.7);
            _coreFlame.Data  = FlameGeometry(w, h, amp * 0.85, 0.35);
            _glow.Width = w * 1.2; _glow.Height = h * 1.2;
            _glow.Opacity = on ? 0.8 : 0;
            _state.Text = on ? "FIRING" : "OFF";
            _state.Foreground = on ? B(C_Flame2) : B(C_Label);
            _label.Text = Model.Label ?? Model.Tag ?? "";
        }
        private static Geometry FlameGeometry(double w, double h, double scale, double inner)
        {
            double cx = w / 2.0;
            double bottomY = h;
            double topY = h - h * scale * inner;
            double sideY = h * (0.55 * scale);
            double half = (w * 0.38) * inner;
            var fig = new PathFigure { StartPoint = new Point(cx, topY), IsClosed = true };
            fig.Segments.Add(new BezierSegment(new Point(cx - half, topY + sideY * 0.4),
                                               new Point(cx - half, sideY + (bottomY - sideY) * 0.5),
                                               new Point(cx - half * 0.3, bottomY), true));
            fig.Segments.Add(new LineSegment(new Point(cx + half * 0.3, bottomY), true));
            fig.Segments.Add(new BezierSegment(new Point(cx + half, sideY + (bottomY - sideY) * 0.5),
                                               new Point(cx + half, topY + sideY * 0.4),
                                               new Point(cx, topY), true));
            return new PathGeometry(new[] { fig });
        }
    }

    public class ValveWidget : HmiWidgetBase
    {
        private readonly Path _body = new();
        private readonly Line _stem = new();
        private readonly Rectangle _handle = new();
        private readonly TextBlock _label = new();
        private readonly TextBlock _state = new();
        public ValveWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            _body.Stroke = B(C_EdgeBright); _body.StrokeThickness = 2;
            _stem.Stroke = B(C_EdgeBright); _stem.StrokeThickness = 3;
            _handle.Fill = VertGrad(Color.FromRgb(0x70, 0x70, 0x7A), Color.FromRgb(0x40, 0x40, 0x48));
            _handle.Stroke = B(C_Edge);
            _handle.HorizontalAlignment = HorizontalAlignment.Center;
            _handle.VerticalAlignment = VerticalAlignment.Top;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Bottom;
            _label.Foreground = B(C_Label); _label.FontSize = 10;
            _state.HorizontalAlignment = HorizontalAlignment.Right;
            _state.VerticalAlignment = VerticalAlignment.Top;
            _state.Margin = new Thickness(0, 2, 4, 0);
            _state.FontSize = 9; _state.FontWeight = FontWeights.Bold;
            _state.FontFamily = Mono;
            grid.Children.Add(_body); grid.Children.Add(_stem); grid.Children.Add(_handle);
            grid.Children.Add(_state); grid.Children.Add(_label);
            Content = grid;
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }
        public override void Refresh()
        {
            double w = Math.Max(30, ActualWidth);
            double h = Math.Max(20, ActualHeight - 12);
            bool open = ReadBool();
            double cx = w / 2.0, cy = h * 0.6;
            double r = Math.Min(w, h) * 0.42;
            var g = new GeometryGroup();
            g.Children.Add(new PathGeometry(new[] {
                new PathFigure(new Point(cx - r, cy - r * 0.7), new []
                {
                    new LineSegment(new Point(cx, cy), true) as PathSegment,
                    new LineSegment(new Point(cx - r, cy + r * 0.7), true),
                }, true)
            }));
            g.Children.Add(new PathGeometry(new[] {
                new PathFigure(new Point(cx + r, cy - r * 0.7), new []
                {
                    new LineSegment(new Point(cx, cy), true) as PathSegment,
                    new LineSegment(new Point(cx + r, cy + r * 0.7), true),
                }, true)
            }));
            _body.Data = g;
            _body.Fill = open ? VertGrad(Color.FromRgb(0x60, 0xE0, 0xB0), Color.FromRgb(0x18, 0x80, 0x60))
                              : VertGrad(Color.FromRgb(0xE6, 0x3E, 0x3E), Color.FromRgb(0x80, 0x10, 0x10));
            // Stem from center up to where handle sits
            _stem.X1 = cx; _stem.X2 = cx;
            _stem.Y1 = cy - r * 0.7; _stem.Y2 = 4;
            _handle.Width = w * 0.45; _handle.Height = 5;
            _handle.Margin = new Thickness(0, 2, 0, 0);
            _state.Text = open ? "OPEN" : "SHUT";
            _state.Foreground = open ? B(C_Accent) : B(C_Crit);
            _label.Text = Model.Label ?? Model.Tag ?? "";
        }
    }

    public class PumpWidget : HmiWidgetBase
    {
        private readonly Ellipse _body = new();
        private readonly Path _impeller = new();
        private readonly TextBlock _label = new();
        private readonly TextBlock _state = new();
        private readonly DispatcherTimer _spin;
        private double _angle;
        public PumpWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            _body.Stroke = B(C_EdgeBright); _body.StrokeThickness = 2;
            _impeller.Stroke = B(C_EdgeBright); _impeller.StrokeThickness = 1;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Bottom;
            _label.Foreground = B(C_Label); _label.FontSize = 10;
            _state.HorizontalAlignment = HorizontalAlignment.Right;
            _state.VerticalAlignment = VerticalAlignment.Top;
            _state.Margin = new Thickness(0, 2, 4, 0);
            _state.FontSize = 9; _state.FontWeight = FontWeights.Bold;
            _state.FontFamily = Mono;
            grid.Children.Add(_body); grid.Children.Add(_impeller);
            grid.Children.Add(_state); grid.Children.Add(_label);
            Content = grid;

            _spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _spin.Tick += (s, e) =>
            {
                if (ReadBool()) { _angle += 0.32; if (_angle > Math.PI * 2) _angle -= Math.PI * 2; UpdateImpeller(); }
            };
            _spin.Start();
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }
        public override void Refresh()
        {
            bool on = ReadBool();
            _body.Fill = on
                ? new RadialGradientBrush(Color.FromRgb(0x18, 0x80, 0x60), Color.FromRgb(0x08, 0x40, 0x30))
                : new RadialGradientBrush(Color.FromRgb(0x30, 0x30, 0x38), Color.FromRgb(0x14, 0x14, 0x1A));
            _state.Text = on ? "RUN" : "STOP";
            _state.Foreground = on ? B(C_Accent) : B(C_Label);
            _label.Text = Model.Label ?? Model.Tag ?? "";
            UpdateImpeller();
        }
        private void UpdateImpeller()
        {
            double w = Math.Max(40, ActualWidth);
            double h = Math.Max(40, ActualHeight - 12);
            double cx = w / 2, cy = h / 2 + 2;
            double r = Math.Min(w, h) * 0.32;
            _body.Width = r * 2 + 8; _body.Height = r * 2 + 8;
            _body.HorizontalAlignment = HorizontalAlignment.Center;
            _body.VerticalAlignment = VerticalAlignment.Center;

            var g = new GeometryGroup();
            for (int i = 0; i < 5; i++)
            {
                double a = _angle + i * Math.PI * 2 / 5;
                var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
                double bx = cx + r * Math.Cos(a);
                double by = cy + r * Math.Sin(a);
                double bx2 = cx + r * Math.Cos(a + 0.5);
                double by2 = cy + r * Math.Sin(a + 0.5);
                fig.Segments.Add(new LineSegment(new Point(bx, by), true));
                fig.Segments.Add(new ArcSegment(new Point(bx2, by2), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                g.Children.Add(new PathGeometry(new[] { fig }));
            }
            _impeller.Data = g;
            _impeller.Fill = ReadBool() ? B(C_Accent) : B(Color.FromRgb(0x55, 0x55, 0x5E));
        }
    }

    public class MotorWidget : HmiWidgetBase
    {
        private readonly Ellipse _housing = new();
        private readonly Rectangle _basePlate = new();
        private readonly Path _blades = new();
        private readonly Ellipse _rotor = new();
        private readonly TextBlock _idBadge = new();
        private readonly TextBlock _label = new();
        private readonly TextBlock _state = new();
        private readonly DispatcherTimer _spin;
        private double _angle;

        public MotorWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            _basePlate.Fill = VertGrad(Color.FromRgb(0x30, 0x30, 0x38), Color.FromRgb(0x18, 0x18, 0x20));
            _basePlate.RadiusX = 4; _basePlate.RadiusY = 4;
            _basePlate.VerticalAlignment = VerticalAlignment.Bottom;
            _basePlate.HorizontalAlignment = HorizontalAlignment.Stretch;
            _basePlate.Margin = new Thickness(4, 0, 4, 12);
            _basePlate.Height = 10;
            _housing.Stroke = B(C_EdgeBright); _housing.StrokeThickness = 3;
            _housing.Fill = new RadialGradientBrush(Color.FromRgb(0x35, 0x35, 0x3D), Color.FromRgb(0x14, 0x14, 0x18));
            _rotor.Fill = B(Color.FromRgb(0x55, 0x55, 0x5E));
            _rotor.Stroke = B(C_Edge); _rotor.StrokeThickness = 1;
            _blades.Stroke = B(C_Edge); _blades.StrokeThickness = 1;
            _idBadge.HorizontalAlignment = HorizontalAlignment.Left;
            _idBadge.VerticalAlignment = VerticalAlignment.Top;
            _idBadge.Margin = new Thickness(4, 2, 0, 0);
            _idBadge.Foreground = B(C_Value); _idBadge.FontSize = 10;
            _idBadge.FontFamily = Mono; _idBadge.FontWeight = FontWeights.Bold;
            _state.HorizontalAlignment = HorizontalAlignment.Right;
            _state.VerticalAlignment = VerticalAlignment.Top;
            _state.Margin = new Thickness(0, 2, 4, 0);
            _state.FontSize = 9; _state.FontWeight = FontWeights.Bold; _state.FontFamily = Mono;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Bottom;
            _label.Foreground = B(C_Label); _label.FontSize = 10;
            grid.Children.Add(_basePlate);
            grid.Children.Add(_housing);
            grid.Children.Add(_blades);
            grid.Children.Add(_rotor);
            grid.Children.Add(_idBadge);
            grid.Children.Add(_state);
            grid.Children.Add(_label);
            Content = grid;

            _spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _spin.Tick += (s, e) =>
            {
                if (IsRunning()) { _angle += 0.5; if (_angle > Math.PI * 2) _angle -= Math.PI * 2; UpdateBlades(); }
            };
            _spin.Start();
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }
        public override void Refresh()
        {
            bool running = IsRunning();
            UpdateBlades();
            _blades.Fill = running ? B(C_Accent) : B(Color.FromRgb(0x55, 0x55, 0x5E));
            double id = ReadMotorId();
            _idBadge.Text = id >= 0 ? $"#{(int)id}" : "";
            _state.Text = running ? "RUN" : "STOP";
            _state.Foreground = running ? B(C_Accent) : B(C_Label);
            _label.Text = Model.Label ?? Model.Tag ?? "";
        }
        private bool IsRunning()
        {
            if (string.IsNullOrEmpty(Model.Tag)) return false;
            var t = ResolveTag(Model.Tag);
            if (t == null) return false;
            if (t.Members.ContainsKey("running")) return Db.ReadOperand(Model.Tag + ".running") != 0;
            return Db.ReadBoolOperand(Model.Tag!);
        }
        private double ReadMotorId()
        {
            if (string.IsNullOrEmpty(Model.Tag)) return -1;
            var t = ResolveTag(Model.Tag);
            if (t == null || !t.Members.ContainsKey("motor_id")) return -1;
            return Db.ReadOperand(Model.Tag + ".motor_id");
        }
        private void UpdateBlades()
        {
            double w = Math.Max(40, ActualWidth);
            double h = Math.Max(40, ActualHeight - 12);
            double cx = w / 2, cy = h / 2;
            double r = Math.Min(w, h) * 0.34;
            _housing.Width = r * 2 + 8; _housing.Height = r * 2 + 8;
            _housing.HorizontalAlignment = HorizontalAlignment.Center;
            _housing.VerticalAlignment = VerticalAlignment.Center;
            _rotor.Width = 10; _rotor.Height = 10;
            _rotor.HorizontalAlignment = HorizontalAlignment.Center;
            _rotor.VerticalAlignment = VerticalAlignment.Center;

            var g = new GeometryGroup();
            for (int i = 0; i < 4; i++)
            {
                double ang = _angle + i * Math.PI / 2;
                var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
                double bx = cx + r * Math.Cos(ang);
                double by = cy + r * Math.Sin(ang);
                double px1 = cx + (r * 0.22) * Math.Cos(ang + Math.PI / 7);
                double py1 = cy + (r * 0.22) * Math.Sin(ang + Math.PI / 7);
                double px2 = cx + (r * 0.22) * Math.Cos(ang - Math.PI / 7);
                double py2 = cy + (r * 0.22) * Math.Sin(ang - Math.PI / 7);
                fig.Segments.Add(new LineSegment(new Point(px1, py1), true));
                fig.Segments.Add(new LineSegment(new Point(bx, by), true));
                fig.Segments.Add(new LineSegment(new Point(px2, py2), true));
                g.Children.Add(new PathGeometry(new[] { fig }));
            }
            _blades.Data = g;
        }
    }

    public class GaugeWidget : HmiWidgetBase
    {
        private readonly Path _arc = new();
        private readonly Path _alarmArc = new();
        private readonly Line _needle = new();
        private readonly Ellipse _pivot = new();
        private readonly TextBlock _val = new();
        private readonly TextBlock _label = new();
        private readonly TextBlock _units = new();
        private readonly Grid _ticks = new();
        public GaugeWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            _arc.Stroke = B(C_Edge); _arc.StrokeThickness = 6;
            _alarmArc.Stroke = B(C_Crit); _alarmArc.StrokeThickness = 6;
            _needle.Stroke = B(C_Warn); _needle.StrokeThickness = 3;
            _pivot.Width = 10; _pivot.Height = 10;
            _pivot.Fill = B(C_EdgeBright);
            _pivot.HorizontalAlignment = HorizontalAlignment.Center;
            _pivot.VerticalAlignment = VerticalAlignment.Center;
            _val.Foreground = B(C_Value); _val.FontSize = 16; _val.FontWeight = FontWeights.Bold;
            _val.HorizontalAlignment = HorizontalAlignment.Center; _val.VerticalAlignment = VerticalAlignment.Center;
            _val.FontFamily = Mono; _val.Margin = new Thickness(0, 0, 0, 16);
            _units.Foreground = B(C_Label); _units.FontSize = 9;
            _units.HorizontalAlignment = HorizontalAlignment.Center;
            _units.VerticalAlignment = VerticalAlignment.Center;
            _units.Margin = new Thickness(0, 14, 0, 0);
            _label.Foreground = B(C_Label); _label.FontSize = 10;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Bottom;
            grid.Children.Add(_arc); grid.Children.Add(_alarmArc);
            grid.Children.Add(_ticks);
            grid.Children.Add(_needle); grid.Children.Add(_pivot);
            grid.Children.Add(_val); grid.Children.Add(_units); grid.Children.Add(_label);
            Content = grid;
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }
        public override void Refresh()
        {
            double w = Math.Max(60, ActualWidth);
            double h = Math.Max(60, ActualHeight - 12);
            double cx = w / 2, cy = h * 0.82;
            double r = Math.Min(w, h) * 0.6;
            var fig = new PathFigure { StartPoint = new Point(cx - r, cy) };
            fig.Segments.Add(new ArcSegment(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            _arc.Data = new PathGeometry(new[] { fig });

            // Red alarm arc segment if HighAlarm is set
            if (Model.HighAlarm > Model.LowAlarm && Model.Max > Model.Min)
            {
                double t1 = Math.Clamp((Model.HighAlarm - Model.Min) / (Model.Max - Model.Min), 0, 1);
                double a1 = Math.PI * (1.0 - t1);
                var afig = new PathFigure { StartPoint = new Point(cx + r * Math.Cos(a1), cy - r * Math.Sin(a1)) };
                afig.Segments.Add(new ArcSegment(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                _alarmArc.Data = new PathGeometry(new[] { afig });
            } else { _alarmArc.Data = null; }

            BuildTicks(cx, cy, r);

            double v = ReadNum();
            double min = Model.Min, max = Model.Max;
            double t = max > min ? Math.Clamp((v - min) / (max - min), 0, 1) : 0;
            double ang = Math.PI * (1.0 - t);
            _needle.X1 = cx; _needle.Y1 = cy;
            _needle.X2 = cx + r * 0.92 * Math.Cos(ang);
            _needle.Y2 = cy - r * 0.92 * Math.Sin(ang);
            var (c, _) = ClassifyAnalog(v);
            _needle.Stroke = B(c);
            _val.Text = v.ToString(string.IsNullOrEmpty(Model.Format) ? "F1" : Model.Format);
            _units.Text = Model.Units ?? "";
            _label.Text = Model.Label ?? Model.Tag ?? "";
        }
        private void BuildTicks(double cx, double cy, double r)
        {
            _ticks.Children.Clear();
            for (int i = 0; i <= 10; i++)
            {
                double t = i / 10.0;
                double a = Math.PI * (1.0 - t);
                double rx1 = cx + r * 1.02 * Math.Cos(a);
                double ry1 = cy - r * 1.02 * Math.Sin(a);
                double tickLen = (i % 5 == 0) ? 0.12 : 0.06;
                double rx2 = cx + r * (1.02 - tickLen) * Math.Cos(a);
                double ry2 = cy - r * (1.02 - tickLen) * Math.Sin(a);
                _ticks.Children.Add(new Line { X1 = rx1, Y1 = ry1, X2 = rx2, Y2 = ry2,
                    Stroke = B(C_EdgeBright), StrokeThickness = (i % 5 == 0) ? 1.5 : 1 });
            }
        }
    }

    public class PressureGaugeWidget : HmiWidgetBase
    {
        // Round, industrial-PSI-style gauge. 270° sweep; numeric labels at
        // 0/25/50/75/100% positions. Red zone for HighAlarm region.
        private readonly Ellipse _outerBezel = new();
        private readonly Ellipse _face = new();
        private readonly Path _redZone = new();
        private readonly Line _needle = new();
        private readonly Ellipse _pivot = new();
        private readonly Grid _ticks = new();
        private readonly TextBlock _val = new();
        private readonly TextBlock _units = new();
        private readonly TextBlock _label = new();

        public PressureGaugeWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            _outerBezel.Stroke = B(C_EdgeBright); _outerBezel.StrokeThickness = 3;
            _outerBezel.Fill = VertGrad(Color.FromRgb(0x40, 0x40, 0x48), Color.FromRgb(0x20, 0x20, 0x26));
            _face.Fill = new RadialGradientBrush(Color.FromRgb(0xF8, 0xF6, 0xEE), Color.FromRgb(0xD8, 0xD2, 0xC0));
            _face.Stroke = B(C_Edge); _face.StrokeThickness = 1;
            _redZone.Stroke = B(C_Crit); _redZone.StrokeThickness = 8;
            _needle.Stroke = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x26));
            _needle.StrokeThickness = 2.5;
            _pivot.Width = 9; _pivot.Height = 9;
            _pivot.Fill = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x48));
            _pivot.HorizontalAlignment = HorizontalAlignment.Center;
            _pivot.VerticalAlignment = VerticalAlignment.Center;
            _val.Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x48));
            _val.FontFamily = Mono; _val.FontWeight = FontWeights.Bold; _val.FontSize = 14;
            _val.HorizontalAlignment = HorizontalAlignment.Center;
            _val.VerticalAlignment = VerticalAlignment.Bottom;
            _val.Margin = new Thickness(0, 0, 0, 18);
            _units.Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x6A));
            _units.FontSize = 9;
            _units.HorizontalAlignment = HorizontalAlignment.Center;
            _units.VerticalAlignment = VerticalAlignment.Bottom;
            _units.Margin = new Thickness(0, 0, 0, 6);
            _label.Foreground = B(C_Label); _label.FontSize = 10;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Bottom;
            _label.Margin = new Thickness(0, 0, 0, -14);
            grid.Children.Add(_outerBezel);
            grid.Children.Add(_face);
            grid.Children.Add(_redZone);
            grid.Children.Add(_ticks);
            grid.Children.Add(_needle);
            grid.Children.Add(_pivot);
            grid.Children.Add(_val);
            grid.Children.Add(_units);
            grid.Children.Add(_label);
            Content = grid;
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }
        public override void Refresh()
        {
            double w = Math.Max(60, ActualWidth);
            double h = Math.Max(60, ActualHeight - 14);
            double cx = w / 2, cy = h / 2 + 4;
            double r = Math.Min(w, h) * 0.42;
            _outerBezel.Width = r * 2 + 14; _outerBezel.Height = r * 2 + 14;
            _outerBezel.HorizontalAlignment = HorizontalAlignment.Center;
            _outerBezel.VerticalAlignment = VerticalAlignment.Center;
            _face.Width = r * 2; _face.Height = r * 2;
            _face.HorizontalAlignment = HorizontalAlignment.Center;
            _face.VerticalAlignment = VerticalAlignment.Center;

            // 270° sweep from 225° (lower-left) clockwise to 315° (lower-right)
            // I.e. start angle = 5π/4 (lower-left), end angle = -π/4 (lower-right),
            // going clockwise covers 3π/2 radians.
            double startAng = Math.PI * 1.25;  // 225°
            double sweep    = -Math.PI * 1.5;  // -270° (clockwise)

            // Red zone arc from HighAlarm to Max
            if (Model.HighAlarm > Model.Min && Model.Max > Model.Min)
            {
                double t1 = Math.Clamp((Model.HighAlarm - Model.Min) / (Model.Max - Model.Min), 0, 1);
                double a1 = startAng + sweep * t1;
                double a2 = startAng + sweep * 1.0;
                var afig = new PathFigure
                {
                    StartPoint = new Point(cx + r * 0.9 * Math.Cos(a1), cy - r * 0.9 * Math.Sin(a1))
                };
                afig.Segments.Add(new ArcSegment(
                    new Point(cx + r * 0.9 * Math.Cos(a2), cy - r * 0.9 * Math.Sin(a2)),
                    new Size(r * 0.9, r * 0.9), 0, false, SweepDirection.Clockwise, true));
                _redZone.Data = new PathGeometry(new[] { afig });
            }
            else _redZone.Data = null;

            BuildTicks(cx, cy, r, startAng, sweep);

            double v = ReadNum();
            double t = (Model.Max > Model.Min)
                ? Math.Clamp((v - Model.Min) / (Model.Max - Model.Min), 0, 1)
                : 0;
            double ang = startAng + sweep * t;
            _needle.X1 = cx; _needle.Y1 = cy;
            _needle.X2 = cx + r * 0.85 * Math.Cos(ang);
            _needle.Y2 = cy - r * 0.85 * Math.Sin(ang);

            _val.Text = v.ToString(string.IsNullOrEmpty(Model.Format) ? "F0" : Model.Format);
            _units.Text = string.IsNullOrEmpty(Model.Units) ? "PSI" : Model.Units;
            _label.Text = Model.Label ?? Model.Tag ?? "";
        }
        private void BuildTicks(double cx, double cy, double r, double startAng, double sweep)
        {
            _ticks.Children.Clear();
            for (int i = 0; i <= 10; i++)
            {
                double t = i / 10.0;
                double a = startAng + sweep * t;
                double tickLen = (i % 5 == 0) ? 0.15 : 0.08;
                _ticks.Children.Add(new Line
                {
                    X1 = cx + r * (1.0 - tickLen) * Math.Cos(a),
                    Y1 = cy - r * (1.0 - tickLen) * Math.Sin(a),
                    X2 = cx + r * 1.0 * Math.Cos(a),
                    Y2 = cy - r * 1.0 * Math.Sin(a),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x48)),
                    StrokeThickness = (i % 5 == 0) ? 1.8 : 1,
                });
                if (i % 5 == 0)
                {
                    double val = Model.Min + (Model.Max - Model.Min) * t;
                    var lbl = new TextBlock
                    {
                        Text = val.ToString("F0"),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x38)),
                        FontSize = 8, FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double lx = cx + r * (1.0 - 0.28) * Math.Cos(a) - lbl.DesiredSize.Width / 2;
                    double ly = cy - r * (1.0 - 0.28) * Math.Sin(a) - lbl.DesiredSize.Height / 2;
                    lbl.Margin = new Thickness(lx, ly, 0, 0);
                    _ticks.Children.Add(lbl);
                }
            }
        }
    }

    public class BargraphWidget : HmiWidgetBase
    {
        private readonly Border _frame = new();
        private readonly Rectangle _fill = new();
        private readonly Grid _ticks = new();
        private readonly Line _alarmLine = new();
        private readonly TextBlock _val = new();
        private readonly TextBlock _label = new();
        public BargraphWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            _frame.Background = VertGrad(Color.FromRgb(0x10, 0x12, 0x18), Color.FromRgb(0x1F, 0x22, 0x2C));
            _frame.BorderBrush = B(C_EdgeBright);
            _frame.BorderThickness = new Thickness(2);
            _frame.CornerRadius = new CornerRadius(3);
            var grid = new Grid { Margin = new Thickness(2) };
            _fill.VerticalAlignment = VerticalAlignment.Bottom;
            _fill.HorizontalAlignment = HorizontalAlignment.Stretch;
            _val.Foreground = B(C_Value);
            _val.FontSize = 12; _val.FontWeight = FontWeights.Bold; _val.FontFamily = Mono;
            _val.HorizontalAlignment = HorizontalAlignment.Center;
            _val.VerticalAlignment = VerticalAlignment.Top;
            _val.Margin = new Thickness(0, 4, 0, 0);
            _val.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Opacity = 0.6 };
            _alarmLine.Stroke = B(C_Crit); _alarmLine.StrokeThickness = 2;
            _alarmLine.StrokeDashArray = new DoubleCollection { 3, 2 };
            grid.Children.Add(_fill); grid.Children.Add(_ticks);
            grid.Children.Add(_alarmLine); grid.Children.Add(_val);
            _frame.Child = grid;
            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_frame, 0); outer.Children.Add(_frame);
            _label.Foreground = B(C_Label); _label.FontSize = 10;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(_label, 1); outer.Children.Add(_label);
            Content = outer;
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }
        public override void Refresh()
        {
            double v = ReadNum();
            double min = Model.Min, max = Model.Max;
            if (max <= min) max = min + 100;
            double pct = Math.Clamp((v - min) / (max - min), 0, 1);
            double innerH = Math.Max(0, _frame.ActualHeight - 6);
            _fill.Height = innerH * pct;
            var (c, _) = ClassifyAnalog(v);
            _fill.Fill = VertGrad(
                Color.FromArgb(0xFF, c.R, c.G, c.B),
                Color.FromArgb(0xCC, (byte)Math.Max(0, c.R-40), (byte)Math.Max(0, c.G-40), (byte)Math.Max(0, c.B-40)));

            _val.Text = v.ToString(string.IsNullOrEmpty(Model.Format) ? "F1" : Model.Format) +
                        (string.IsNullOrEmpty(Model.Units) ? "" : " " + Model.Units);

            BuildTicks(innerH);

            // High-alarm line
            if (Model.HighAlarm > Model.Min && Model.Max > Model.Min)
            {
                double tAlarm = Math.Clamp((Model.HighAlarm - min) / (max - min), 0, 1);
                double y = innerH - tAlarm * innerH;
                _alarmLine.X1 = 0; _alarmLine.X2 = _frame.ActualWidth - 6;
                _alarmLine.Y1 = y; _alarmLine.Y2 = y;
                _alarmLine.HorizontalAlignment = HorizontalAlignment.Stretch;
                _alarmLine.VerticalAlignment = VerticalAlignment.Top;
                _alarmLine.Visibility = Visibility.Visible;
            }
            else _alarmLine.Visibility = Visibility.Collapsed;

            _label.Text = Model.Label ?? Model.Tag ?? "";
        }
        private void BuildTicks(double innerH)
        {
            _ticks.Children.Clear();
            for (int i = 0; i <= 10; i++)
            {
                double y = innerH - innerH * i / 10;
                _ticks.Children.Add(new Line
                {
                    X1 = 0, Y1 = y, X2 = (i % 5 == 0) ? 6 : 3, Y2 = y,
                    Stroke = B(C_EdgeBright), StrokeThickness = 1,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                });
            }
        }
    }

    /// <summary>
    /// Animated steam stack — vertical pipe with rising "puff" rings while the
    /// bound bool is on. Looks great paired with a boiler tank.
    /// </summary>
    public class SteamStackWidget : HmiWidgetBase
    {
        private readonly Rectangle _stack = new();
        private readonly Grid _puffs = new();
        private readonly TextBlock _label = new();
        private readonly DispatcherTimer _anim;
        private double _t;
        private readonly List<double> _puffPhases = new() { 0.0, 0.33, 0.66 };

        public SteamStackWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            var grid = new Grid();
            _stack.Fill = VertGrad(Color.FromRgb(0x40, 0x40, 0x48), Color.FromRgb(0x20, 0x20, 0x26));
            _stack.Stroke = B(C_EdgeBright); _stack.StrokeThickness = 2;
            _stack.HorizontalAlignment = HorizontalAlignment.Center;
            _stack.VerticalAlignment = VerticalAlignment.Bottom;
            _stack.RadiusX = 2; _stack.RadiusY = 2;
            _label.Foreground = B(C_Label); _label.FontSize = 10;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Bottom;
            grid.Children.Add(_stack);
            grid.Children.Add(_puffs);
            grid.Children.Add(_label);
            Content = grid;

            _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _anim.Tick += (s, e) => { if (ReadBool()) { _t += 0.012; UpdatePuffs(); } else { UpdatePuffs(); } };
            _anim.Start();
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }
        public override void Refresh()
        {
            double w = Math.Max(20, ActualWidth);
            double h = Math.Max(40, ActualHeight - 12);
            _stack.Width = w * 0.4; _stack.Height = h * 0.55;
            _label.Text = Model.Label ?? Model.Tag ?? "";
            UpdatePuffs();
        }
        private void UpdatePuffs()
        {
            _puffs.Children.Clear();
            bool on = ReadBool();
            if (!on) return;
            double w = Math.Max(20, ActualWidth);
            double h = Math.Max(40, ActualHeight - 12);
            double stackTopY = h * 0.45;
            double puffArea = stackTopY;
            for (int i = 0; i < _puffPhases.Count; i++)
            {
                double phase = (_t + _puffPhases[i]) % 1.0;
                double size = 14 + phase * 26;
                double opacity = (1 - phase) * 0.85;
                double yOffset = (1 - phase) * puffArea + 6;
                var ell = new Ellipse
                {
                    Width = size, Height = size * 0.7,
                    Fill = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), C_Steam.R, C_Steam.G, C_Steam.B)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness((phase - 0.5) * 10, yOffset, 0, 0),
                };
                _puffs.Children.Add(ell);
            }
        }
    }

    /// <summary>
    /// Multi-line alarm strip. Latches messages from a bool tag when it goes
    /// true and shows them with timestamp + severity color. Click to ACK.
    /// </summary>
    public class AlarmStripWidget : HmiWidgetBase
    {
        private readonly Border _frame = new();
        private readonly TextBlock _msg = new();
        private readonly TextBlock _label = new();
        private string _activeMsg = "(no alarms)";
        private DateTime _activeSince = DateTime.MinValue;
        private bool _wasOn;
        public AlarmStripWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            _frame.CornerRadius = new CornerRadius(3);
            _frame.BorderBrush = B(C_EdgeBright);
            _frame.BorderThickness = new Thickness(1);
            _frame.Padding = new Thickness(8, 4, 8, 4);
            _msg.FontFamily = Mono; _msg.FontSize = 12; _msg.FontWeight = FontWeights.Bold;
            _msg.VerticalAlignment = VerticalAlignment.Center;
            _frame.Child = _msg;
            var sp = new StackPanel();
            sp.Children.Add(_frame);
            _label.Foreground = B(C_Label); _label.FontSize = 9;
            _label.HorizontalAlignment = HorizontalAlignment.Right;
            sp.Children.Add(_label);
            Content = sp;
            Refresh();
        }
        protected override void HandleClick()
        {
            // ACK clears (until next rising edge)
            _activeMsg = "(no alarms)";
            Refresh();
        }
        public override void Refresh()
        {
            bool on = ReadBool();
            if (on && !_wasOn)
            {
                _activeMsg = (Model.Label ?? Model.Tag ?? "ALARM") + " — ACTIVE";
                _activeSince = DateTime.Now;
            }
            _wasOn = on;
            _frame.Background = on
                ? VertGrad(Color.FromRgb(0xC0, 0x20, 0x20), Color.FromRgb(0x70, 0x00, 0x00))
                : VertGrad(Color.FromRgb(0x18, 0x18, 0x1E), Color.FromRgb(0x0C, 0x0C, 0x10));
            _msg.Foreground = on ? Brushes.White : B(C_Label);
            _msg.Text = _activeMsg;
            _label.Text = _activeSince == DateTime.MinValue ? "" : $"since {_activeSince:HH:mm:ss}";
        }
    }

    public class PIDBlockWidget : HmiWidgetBase
    {
        // Compact PID summary block: SP / PV / OUT side by side.
        // Reads SP from tag.sp, PV from tag (or tag.pv), OUT from tag.out
        // members when available, falling back to the tag itself for PV.
        private readonly Border _frame = new();
        private readonly TextBlock _sp = new();
        private readonly TextBlock _pv = new();
        private readonly TextBlock _ot = new();
        private readonly TextBlock _label = new();
        public PIDBlockWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            _frame.Background = VertGrad(Color.FromRgb(0x10, 0x12, 0x18), Color.FromRgb(0x1F, 0x22, 0x2C));
            _frame.BorderBrush = B(C_EdgeBright);
            _frame.BorderThickness = new Thickness(2);
            _frame.CornerRadius = new CornerRadius(4);
            var g = new Grid { Margin = new Thickness(8, 4, 8, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            void Col(int c, string head, TextBlock val, Color valC)
            {
                var hb = new TextBlock { Text = head, Foreground = B(C_Label), FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetColumn(hb, c); Grid.SetRow(hb, 0); g.Children.Add(hb);
                val.Foreground = B(valC); val.FontSize = 18; val.FontWeight = FontWeights.Bold;
                val.FontFamily = Mono;
                val.HorizontalAlignment = HorizontalAlignment.Center;
                val.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(val, c); Grid.SetRow(val, 1); g.Children.Add(val);
            }
            Col(0, "SP", _sp, C_Info);
            Col(1, "PV", _pv, C_Accent);
            Col(2, "OUT", _ot, C_Warn);
            _label.Foreground = B(C_Label); _label.FontSize = 9;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumnSpan(_label, 3); Grid.SetRow(_label, 2);
            g.Children.Add(_label);
            _frame.Child = g;
            Content = _frame;
            Refresh();
        }
        public override void Refresh()
        {
            string fmt = string.IsNullOrEmpty(Model.Format) ? "F1" : Model.Format;
            string units = string.IsNullOrEmpty(Model.Units) ? "" : " " + Model.Units;
            double sp = ReadMemberOr("sp", ReadNum());
            double pv = ReadMemberOr("pv", ReadNum());
            double ot = ReadMemberOr("out", 0);
            _sp.Text = sp.ToString(fmt) + units;
            _pv.Text = pv.ToString(fmt) + units;
            _ot.Text = ot.ToString(fmt);
            var (c, _) = ClassifyAnalog(pv);
            _pv.Foreground = B(c);
            _label.Text = Model.Label ?? Model.Tag ?? "PID";
        }
        private double ReadMemberOr(string name, double fallback)
        {
            if (string.IsNullOrEmpty(Model.Tag)) return fallback;
            var t = ResolveTag(Model.Tag);
            if (t == null) return fallback;
            if (t.Members.ContainsKey(name)) return Db.ReadOperand(Model.Tag + "." + name);
            return fallback;
        }
    }

    public class TrendWidget : HmiWidgetBase
    {
        private readonly Border _frame = new();
        private readonly Polyline _line = new();
        private readonly Grid _gridLines = new();
        private readonly TextBlock _label = new();
        private readonly TextBlock _current = new();
        private readonly Queue<double> _samples = new();

        public TrendWidget(HmiWidgetModel m, TagDatabase db) : base(m, db)
        {
            _frame.Background = VertGrad(Color.FromRgb(0x10, 0x12, 0x18), Color.FromRgb(0x1F, 0x22, 0x2C));
            _frame.BorderBrush = B(C_EdgeBright); _frame.BorderThickness = new Thickness(1);
            _frame.CornerRadius = new CornerRadius(3);
            var grid = new Grid();
            _line.Stroke = B(C_Accent); _line.StrokeThickness = 1.8;
            _line.StrokeLineJoin = PenLineJoin.Round;
            _label.Foreground = B(C_Label); _label.FontSize = 10;
            _label.HorizontalAlignment = HorizontalAlignment.Left;
            _label.VerticalAlignment = VerticalAlignment.Top;
            _label.Margin = new Thickness(6, 4, 0, 0);
            _current.Foreground = B(C_Accent); _current.FontFamily = Mono;
            _current.FontSize = 11; _current.FontWeight = FontWeights.Bold;
            _current.HorizontalAlignment = HorizontalAlignment.Right;
            _current.VerticalAlignment = VerticalAlignment.Top;
            _current.Margin = new Thickness(0, 4, 6, 0);
            grid.Children.Add(_gridLines);
            grid.Children.Add(_line);
            grid.Children.Add(_label);
            grid.Children.Add(_current);
            _frame.Child = grid;
            Content = _frame;
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }
        public override void Refresh()
        {
            int cap = Math.Max(8, Model.Samples);
            _samples.Enqueue(ReadNum());
            while (_samples.Count > cap) _samples.Dequeue();

            double w = Math.Max(20, _frame.ActualWidth - 2);
            double h = Math.Max(20, _frame.ActualHeight - 2);
            double min = Model.Min, max = Model.Max;
            if (max <= min) { min = 0; max = 1; }

            // Grid lines (4 horizontal, 4 vertical)
            _gridLines.Children.Clear();
            for (int i = 1; i < 4; i++)
            {
                double y = h * i / 4;
                _gridLines.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = B(Color.FromRgb(0x2A, 0x2A, 0x32)), StrokeThickness = 1 });
                double x = w * i / 4;
                _gridLines.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = h,
                    Stroke = B(Color.FromRgb(0x2A, 0x2A, 0x32)), StrokeThickness = 1 });
            }

            var pts = new PointCollection();
            int n = _samples.Count;
            int i2 = 0;
            foreach (var s in _samples)
            {
                double x = (n <= 1) ? 0 : (i2 / (double)(n - 1)) * w;
                double t = Math.Clamp((s - min) / (max - min), 0, 1);
                double y = h - t * h;
                pts.Add(new Point(x, y));
                i2++;
            }
            _line.Points = pts;
            double cur = _samples.Count == 0 ? 0 : _samples.Last();
            _current.Text = cur.ToString(string.IsNullOrEmpty(Model.Format) ? "F1" : Model.Format);
            _label.Text = Model.Label ?? Model.Tag ?? "";
        }
    }
}
