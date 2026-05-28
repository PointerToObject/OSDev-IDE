using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OSDevIDE.Sim
{
    /// <summary>
    /// Renders resize grips around the current selection, plus the marquee
    /// rectangle while the user drags an empty patch of canvas.  Lives in a
    /// sibling Canvas above the widgets so it doesn't disturb their layout.
    ///
    /// Grip layout (0..7):
    ///     0 - 1 - 2
    ///     |       |
    ///     7       3
    ///     |       |
    ///     6 - 5 - 4
    ///
    /// Each grip captures mouse on press and reports drag deltas to the host
    /// via the supplied resize callback so the host can mutate the model.
    /// </summary>
    public class DesignerOverlay
    {
        private readonly Canvas _layer;
        private readonly List<Rectangle> _grips = new();
        private Rectangle? _marquee;
        private readonly Brush _gripFill   = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private readonly Brush _gripStroke = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));
        private const double GripSize = 9;

        public event Action<int, double, double>? ResizeStarted;  // grip index, mouse-x, mouse-y
        public event Action<int, double, double>? ResizeDragging; // grip index, current-x, current-y
        public event Action<int>? ResizeEnded;

        public DesignerOverlay(Canvas overlayLayer)
        {
            _layer = overlayLayer;
            _layer.IsHitTestVisible = true;
        }

        /// <summary>
        /// Show resize grips around <paramref name="rect"/>. Pass null to hide.
        /// </summary>
        public void ShowGrips(Rect? rect)
        {
            ClearGrips();
            if (rect == null) return;
            var r = rect.Value;
            var pts = GripCenters(r);
            for (int i = 0; i < pts.Length; i++)
            {
                var grip = MakeGrip();
                int idx = i;
                grip.MouseLeftButtonDown += (s, e) =>
                {
                    var p = e.GetPosition(_layer);
                    grip.CaptureMouse();
                    ResizeStarted?.Invoke(idx, p.X, p.Y);
                    e.Handled = true;
                };
                grip.MouseMove += (s, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed || !grip.IsMouseCaptured) return;
                    var p = e.GetPosition(_layer);
                    ResizeDragging?.Invoke(idx, p.X, p.Y);
                };
                grip.MouseLeftButtonUp += (s, e) =>
                {
                    if (grip.IsMouseCaptured) grip.ReleaseMouseCapture();
                    ResizeEnded?.Invoke(idx);
                };
                Canvas.SetLeft(grip, pts[i].X - GripSize / 2);
                Canvas.SetTop(grip,  pts[i].Y - GripSize / 2);
                grip.Cursor = GripCursor(i);
                _layer.Children.Add(grip);
                _grips.Add(grip);
            }
        }

        public void HideGrips() => ClearGrips();

        private void ClearGrips()
        {
            foreach (var g in _grips) _layer.Children.Remove(g);
            _grips.Clear();
        }

        private Rectangle MakeGrip() => new()
        {
            Width = GripSize, Height = GripSize,
            Fill = _gripFill, Stroke = _gripStroke, StrokeThickness = 1,
            SnapsToDevicePixels = true,
        };

        private static Point[] GripCenters(Rect r) => new[] {
            new Point(r.Left,             r.Top),
            new Point(r.Left + r.Width/2, r.Top),
            new Point(r.Right,            r.Top),
            new Point(r.Right,            r.Top + r.Height/2),
            new Point(r.Right,            r.Bottom),
            new Point(r.Left + r.Width/2, r.Bottom),
            new Point(r.Left,             r.Bottom),
            new Point(r.Left,             r.Top + r.Height/2),
        };

        private static Cursor GripCursor(int i) => i switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            3 or 7 => Cursors.SizeWE,
            _ => Cursors.Arrow,
        };

        // ----------------- Marquee selection -----------------

        public void BeginMarquee(Point origin)
        {
            EndMarquee();
            _marquee = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0x4E, 0xC9, 0xB0)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x4E, 0xC9, 0xB0)),
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(_marquee, origin.X); Canvas.SetTop(_marquee, origin.Y);
            _marquee.Width = 0; _marquee.Height = 0;
            _layer.Children.Add(_marquee);
        }

        public Rect UpdateMarquee(Point origin, Point current)
        {
            if (_marquee == null) return Rect.Empty;
            double x = Math.Min(origin.X, current.X);
            double y = Math.Min(origin.Y, current.Y);
            double w = Math.Abs(current.X - origin.X);
            double h = Math.Abs(current.Y - origin.Y);
            Canvas.SetLeft(_marquee, x); Canvas.SetTop(_marquee, y);
            _marquee.Width = w; _marquee.Height = h;
            return new Rect(x, y, w, h);
        }

        public void EndMarquee()
        {
            if (_marquee != null) _layer.Children.Remove(_marquee);
            _marquee = null;
        }

        // ----------------- Alignment guides -----------------

        private readonly List<Line> _guides = new();

        /// <summary>
        /// Show alignment guide lines (typically while dragging — when the
        /// dragged widget's edge lines up with another widget's edge).
        /// Coordinates are in canvas space. Pass empty list to clear.
        /// </summary>
        public void ShowGuides(IEnumerable<(double x1, double y1, double x2, double y2)> lines)
        {
            foreach (var l in _guides) _layer.Children.Remove(l);
            _guides.Clear();
            foreach (var (x1, y1, x2, y2) in lines)
            {
                var line = new Line
                {
                    X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                    Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x70, 0xFF)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false,
                };
                _layer.Children.Add(line);
                _guides.Add(line);
            }
        }
    }
}
