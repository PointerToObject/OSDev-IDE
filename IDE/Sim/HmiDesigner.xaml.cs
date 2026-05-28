using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OSDevIDE.Sim
{
    /// <summary>
    /// Industrial HMI design + run surface.
    ///
    /// Two modes:
    ///   DESIGN — palette drag-drop, widget selection (single + marquee +
    ///            ctrl-click multi), drag-to-move with alignment guides,
    ///            8-grip resize, keyboard nudge, duplicate, z-order,
    ///            alignment, tag-tree browser
    ///   RUN    — widgets are live; operator clicks lamps / buttons / etc.
    ///
    /// Persistence: <see cref="HmiDoc"/> JSON next to source as `<src>.hmi`.
    /// </summary>
    public partial class HmiDesigner : UserControl
    {
        private readonly TagDatabase _db;
        private HmiDoc _doc = new();
        private readonly Dictionary<HmiWidgetModel, HmiWidgetBase> _byModel = new();
        private readonly HashSet<HmiWidgetBase> _selection = new();
        private bool _designMode = true;
        public string HmiPath { get; private set; } = "";

        // Drag state for widget body move
        private Point _dragStart;
        private bool _draggingWidgets;
        private Dictionary<HmiWidgetModel, Point> _dragOrigins = new();

        // Marquee state
        private DesignerOverlay _overlay = null!;
        private Point _marqueeOrigin;
        private bool _marqueeActive;

        // Resize state
        private int _resizeGrip = -1;
        private Rect _resizeStartRect;
        private Point _resizeMouseStart;
        private HmiWidgetModel? _resizeTarget;

        // Clipboard for Ctrl+C / Ctrl+V
        private List<HmiWidgetModel> _clipboard = new();

        public HmiDesigner(TagDatabase db, string hmiPath)
        {
            InitializeComponent();
            _db = db;
            HmiPath = hmiPath;
            PaletteList.ItemsSource = BuildPaletteItems();
            UpdatePathLabel();

            if (File.Exists(hmiPath))
            {
                try { _doc = HmiDoc.Load(hmiPath); }
                catch (Exception ex) { MessageBox.Show($"Failed to load HMI: {ex.Message}"); }
            }

            DrawGridBackground();
            _overlay = new DesignerOverlay(OverlayLayer);
            _overlay.ResizeStarted  += OnResizeStarted;
            _overlay.ResizeDragging += OnResizeDragging;
            _overlay.ResizeEnded    += OnResizeEnded;

            RebuildCanvas();
            BuildTagTree(null);

            PreviewKeyDown += OnKeyDown;
            Focusable = true;
        }

        // =================================================================
        //                       Public hooks
        // =================================================================

        /// <summary>External tick from SimWindow — runs after each VM scan so
        /// sample-based widgets (Trend) can record a sample.</summary>
        public void Tick()
        {
            foreach (var w in _byModel.Values)
            {
                if (w is TrendWidget t) t.Refresh();
            }
        }

        public void SetDesignMode(bool design)
        {
            _designMode = design;
            DesignModeText.Text = design ? "DESIGN" : "RUN";
            DesignModeIcon.Kind = design
                ? MaterialDesignThemes.Wpf.PackIconKind.Pencil
                : MaterialDesignThemes.Wpf.PackIconKind.Play;
            DesignModeIcon.Foreground = design
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07))
                : new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
            StatusModeText.Text = design ? "DESIGN" : "RUN";
            StatusIcon.Kind = DesignModeIcon.Kind;
            foreach (var w in _byModel.Values) w.ApplyDesignMode(design);
            DesignCanvas.Cursor = design ? Cursors.Arrow : Cursors.Hand;
            if (!design) { ClearSelection(); _overlay.HideGrips(); }
            RebuildPropertyPanel();
        }

        // =================================================================
        //                       Toolbar handlers
        // =================================================================

        private void ToggleDesignMode_Click(object sender, RoutedEventArgs e) => SetDesignMode(!_designMode);

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(HmiPath)!);
                _doc.Save(HmiPath);
                UpdatePathLabel();
                StatusActionText.Text = "Saved.";
            }
            catch (Exception ex) { MessageBox.Show($"Save failed: {ex.Message}"); }
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "HMI files (*.hmi)|*.hmi|All files (*.*)|*.*",
                InitialDirectory = System.IO.Path.GetDirectoryName(HmiPath),
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                HmiPath = dlg.FileName;
                _doc = HmiDoc.Load(HmiPath);
                RebuildCanvas();
                UpdatePathLabel();
            }
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelection();
        private void BringFront_Click(object sender, RoutedEventArgs e) => MoveZ(+1);
        private void SendBack_Click(object sender, RoutedEventArgs e)   => MoveZ(-1);
        private void AlignLeft_Click(object sender, RoutedEventArgs e)   => AlignSelection(align: 'L');
        private void AlignCenter_Click(object sender, RoutedEventArgs e) => AlignSelection(align: 'C');
        private void AlignTop_Click(object sender, RoutedEventArgs e)    => AlignSelection(align: 'T');

        private void UpdatePathLabel() => HmiPathText.Text = HmiPath;

        // =================================================================
        //                       Palette (drag source)
        // =================================================================

        private class PaletteItem
        {
            public string Name { get; set; } = "";
            public string Icon { get; set; } = "Shape";
        }

        private static List<PaletteItem> BuildPaletteItems()
        {
            // Map each widget type to a MaterialDesign PackIconKind name.
            string IconFor(string t) => t switch
            {
                "Lamp"          => "LightbulbOn",
                "Button"        => "GestureTap",
                "Toggle"        => "ToggleSwitch",
                "NumberDisplay" => "Counter",
                "NumberEntry"   => "Keyboard",
                "Label"         => "FormatText",
                "Tank"          => "Cup",
                "Flame"         => "Fire",
                "Valve"         => "ValveOpen",
                "Pump"          => "Pump",
                "Motor"         => "Engine",
                "Gauge"         => "Gauge",
                "Trend"         => "ChartLine",
                "Bargraph"      => "ChartBar",
                "PressureGauge" => "GaugeFull",
                "SteamStack"    => "SmokeDetectorVariant",
                "AlarmStrip"    => "AlertCircle",
                "Selector"      => "RotateRight",
                "PIDBlock"      => "FunctionVariant",
                _               => "Shape",
            };
            var list = new List<PaletteItem>();
            foreach (var t in ThemedWidgets.PaletteTypes)
                list.Add(new PaletteItem { Name = t, Icon = IconFor(t) });
            return list;
        }

        private void Palette_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_designMode) return;
            if (sender is FrameworkElement fe && fe.Tag is string typeName)
                DragDrop.DoDragDrop(fe, typeName, DragDropEffects.Copy);
        }

        // =================================================================
        //                       Canvas drag-drop (creation)
        // =================================================================

        private void Canvas_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Canvas_Drop(object sender, DragEventArgs e)
        {
            if (!_designMode) return;
            if (!e.Data.GetDataPresent(DataFormats.StringFormat)) return;
            var typeName = (string)e.Data.GetData(DataFormats.StringFormat);
            var pos = e.GetPosition(DesignCanvas);

            var m = new HmiWidgetModel
            {
                Type = typeName,
                X = SnapToGrid(pos.X - 40),
                Y = SnapToGrid(pos.Y - 40),
                W = DefaultWidth(typeName),
                H = DefaultHeight(typeName),
                Tag = FirstSuitableTag(typeName),
                Z = (_doc.Widgets.Count == 0) ? 0 : _doc.Widgets.Max(w => w.Z) + 1,
            };
            _doc.Widgets.Add(m);
            AddWidgetVisual(m);
            SelectOnly(m);
            StatusActionText.Text = $"Added {typeName}";
        }

        private static double SnapToGrid(double v) => Math.Round(v / 8.0) * 8.0;

        private static double DefaultWidth(string type) => type switch
        {
            "Tank" => 80,  "Flame" => 60,  "Valve" => 60, "Pump" => 80,  "Motor" => 90,
            "Gauge" => 140, "PressureGauge" => 140, "Trend" => 260, "Bargraph" => 80,
            "SteamStack" => 80, "AlarmStrip" => 320, "Selector" => 80, "PIDBlock" => 200,
            "NumberDisplay" or "NumberEntry" => 140, "Label" => 140,
            "Button" or "Toggle" => 110, _ => 80,
        };
        private static double DefaultHeight(string type) => type switch
        {
            "Tank" => 200, "Flame" => 100, "Valve" => 60, "Pump" => 90, "Motor" => 90,
            "Gauge" => 110, "PressureGauge" => 140, "Trend" => 130, "Bargraph" => 200,
            "SteamStack" => 200, "AlarmStrip" => 36, "Selector" => 90, "PIDBlock" => 90,
            "NumberDisplay" or "NumberEntry" => 60, "Label" => 28,
            "Button" or "Toggle" => 40, _ => 80,
        };

        private string? FirstSuitableTag(string type)
        {
            bool wantBool = type is "Lamp" or "Button" or "Toggle" or "Flame" or "Valve" or "Pump" or "SteamStack";
            foreach (var t in _db.Tags)
            {
                if (t.Name.StartsWith("_") || t.Name.EndsWith("_ret")) continue;
                if (wantBool && t.IsBool) return t.Name;
                if (!wantBool && t.IsNumeric) return t.Name;
                if (type == "Motor" && t.IsUserStruct) return t.Name;
            }
            return null;
        }

        // =================================================================
        //                       Canvas rebuild + grid
        // =================================================================

        private void RebuildCanvas()
        {
            // Strip old widget visuals (preserve grid lines drawn at startup).
            var keepGrid = DesignCanvas.Children.OfType<Line>().ToList();
            DesignCanvas.Children.Clear();
            foreach (var line in keepGrid) DesignCanvas.Children.Add(line);

            _byModel.Clear();
            _selection.Clear();
            _overlay?.HideGrips();

            DesignCanvas.Width = _doc.Width;
            DesignCanvas.Height = _doc.Height;
            OverlayLayer.Width = _doc.Width;
            OverlayLayer.Height = _doc.Height;
            DrawGridBackground();

            foreach (var m in _doc.Widgets.OrderBy(w => w.Z)) AddWidgetVisual(m);
            RebuildPropertyPanel();
            UpdateStatusBar();
        }

        private void DrawGridBackground()
        {
            // Remove old grid lines, then redraw on current canvas size.
            var oldLines = DesignCanvas.Children.OfType<Line>().Where(l => (l.Tag as string) == "grid").ToList();
            foreach (var l in oldLines) DesignCanvas.Children.Remove(l);

            double step = 16;
            var minor = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28));
            var major = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x36));
            for (double x = 0; x <= DesignCanvas.Width; x += step)
            {
                var line = new Line
                {
                    X1 = x, Y1 = 0, X2 = x, Y2 = DesignCanvas.Height,
                    Stroke = (((int)x % 80) == 0) ? major : minor,
                    StrokeThickness = 1, SnapsToDevicePixels = true, IsHitTestVisible = false,
                    Tag = "grid",
                };
                Panel.SetZIndex(line, -100);
                DesignCanvas.Children.Add(line);
            }
            for (double y = 0; y <= DesignCanvas.Height; y += step)
            {
                var line = new Line
                {
                    X1 = 0, Y1 = y, X2 = DesignCanvas.Width, Y2 = y,
                    Stroke = (((int)y % 80) == 0) ? major : minor,
                    StrokeThickness = 1, SnapsToDevicePixels = true, IsHitTestVisible = false,
                    Tag = "grid",
                };
                Panel.SetZIndex(line, -100);
                DesignCanvas.Children.Add(line);
            }
        }

        private void AddWidgetVisual(HmiWidgetModel m)
        {
            var w = ThemedWidgets.Build(m, _db, _designMode);
            w.Width = m.W; w.Height = m.H;
            Canvas.SetLeft(w, m.X);
            Canvas.SetTop(w, m.Y);
            Panel.SetZIndex(w, m.Z);
            w.WidgetSelected += sel => HandleWidgetClicked(m);
            w.PreviewMouseLeftButtonDown += (s, e) => StartWidgetDrag(m, e);
            w.PreviewMouseMove           += (s, e) => DoWidgetDrag(e);
            w.PreviewMouseLeftButtonUp   += (s, e) => EndWidgetDrag();

            // Right-click context menu
            w.ContextMenu = BuildContextMenu(m);

            DesignCanvas.Children.Add(w);
            _byModel[m] = w;
        }

        private ContextMenu BuildContextMenu(HmiWidgetModel m)
        {
            var cm = new ContextMenu();
            void Add(string label, RoutedEventHandler h) {
                var mi = new MenuItem { Header = label };
                mi.Click += h;
                cm.Items.Add(mi);
            }
            Add("Duplicate (Ctrl+D)", (s, e) => DuplicateSelection());
            Add("Delete (Del)",       (s, e) => DeleteSelection());
            cm.Items.Add(new Separator());
            Add("Bring to Front",     (s, e) => MoveZ(+1));
            Add("Send to Back",       (s, e) => MoveZ(-1));
            cm.Items.Add(new Separator());
            Add("Align Left",         (s, e) => AlignSelection('L'));
            Add("Align Center",       (s, e) => AlignSelection('C'));
            Add("Align Top",          (s, e) => AlignSelection('T'));
            return cm;
        }

        // =================================================================
        //                       Selection
        // =================================================================

        private void HandleWidgetClicked(HmiWidgetModel m)
        {
            if (!_designMode) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl) ToggleSelected(m);
            else      SelectOnly(m);
        }

        private void SelectOnly(HmiWidgetModel m)
        {
            ClearSelection();
            if (_byModel.TryGetValue(m, out var w)) _selection.Add(w);
            HighlightSelection();
            RebuildPropertyPanel();
            UpdateStatusBar();
            Focus();
        }

        private void ToggleSelected(HmiWidgetModel m)
        {
            if (!_byModel.TryGetValue(m, out var w)) return;
            if (!_selection.Remove(w)) _selection.Add(w);
            HighlightSelection();
            RebuildPropertyPanel();
            UpdateStatusBar();
            Focus();
        }

        private void ClearSelection()
        {
            _selection.Clear();
            HighlightSelection();
            _overlay?.HideGrips();
        }

        private void HighlightSelection()
        {
            foreach (var w in _byModel.Values)
            {
                bool isSel = _selection.Contains(w);
                w.BorderBrush = isSel
                    ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
                    : System.Windows.Media.Brushes.Transparent;
                w.BorderThickness = new Thickness(isSel ? 2 : 0);
            }
            // Show resize grips around the bounding box of all selected widgets.
            if (_selection.Count == 0) { _overlay?.HideGrips(); return; }
            _overlay?.ShowGrips(SelectionBoundingRect());
        }

        private Rect SelectionBoundingRect()
        {
            double left = double.PositiveInfinity, top = double.PositiveInfinity;
            double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
            foreach (var w in _selection)
            {
                var m = w.Model;
                left   = Math.Min(left,   m.X);
                top    = Math.Min(top,    m.Y);
                right  = Math.Max(right,  m.X + m.W);
                bottom = Math.Max(bottom, m.Y + m.H);
            }
            return new Rect(left, top, right - left, bottom - top);
        }

        // =================================================================
        //                       Canvas-level mouse: marquee
        // =================================================================

        private void Canvas_MouseLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (!_designMode) return;
            // Only fire on blank canvas (widgets handle their own click + drag).
            if (e.Source != DesignCanvas) return;
            ClearSelection();
            RebuildPropertyPanel();

            // Begin marquee from the canvas background.
            _marqueeActive = true;
            _marqueeOrigin = e.GetPosition(DesignCanvas);
            _overlay.BeginMarquee(_marqueeOrigin);
            DesignCanvas.CaptureMouse();
            UpdateStatusBar();
            e.Handled = true;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(DesignCanvas);
            StatusMouseText.Text = $"X={(int)p.X}  Y={(int)p.Y}";

            if (_marqueeActive)
            {
                var rect = _overlay.UpdateMarquee(_marqueeOrigin, p);
                _selection.Clear();
                foreach (var (model, vis) in _byModel)
                {
                    var wr = new Rect(model.X, model.Y, model.W, model.H);
                    if (rect.IntersectsWith(wr)) _selection.Add(vis);
                }
                HighlightSelection();
                UpdateStatusBar();
            }
        }

        private void Canvas_MouseLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (_marqueeActive)
            {
                _marqueeActive = false;
                _overlay.EndMarquee();
                if (DesignCanvas.IsMouseCaptured) DesignCanvas.ReleaseMouseCapture();
                RebuildPropertyPanel();
            }
        }

        // =================================================================
        //                       Widget drag (move)
        // =================================================================

        private void StartWidgetDrag(HmiWidgetModel m, MouseButtonEventArgs e)
        {
            if (!_designMode) return;
            // If this widget isn't in selection, replace selection with it.
            if (!_byModel.TryGetValue(m, out var w)) return;
            if (!_selection.Contains(w))
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                if (ctrl) _selection.Add(w);
                else      { _selection.Clear(); _selection.Add(w); }
                HighlightSelection();
                RebuildPropertyPanel();
                UpdateStatusBar();
            }

            _dragStart = e.GetPosition(DesignCanvas);
            _draggingWidgets = true;
            _dragOrigins = _selection.ToDictionary(x => x.Model, x => new Point(x.Model.X, x.Model.Y));
            w.CaptureMouse();
            e.Handled = true;
        }

        private void DoWidgetDrag(MouseEventArgs e)
        {
            if (!_draggingWidgets || !_designMode) return;
            var p = e.GetPosition(DesignCanvas);
            double dx = SnapToGrid(p.X - _dragStart.X);
            double dy = SnapToGrid(p.Y - _dragStart.Y);
            foreach (var (model, origin) in _dragOrigins)
            {
                model.X = SnapToGrid(origin.X + dx);
                model.Y = SnapToGrid(origin.Y + dy);
                if (_byModel.TryGetValue(model, out var vis))
                {
                    Canvas.SetLeft(vis, model.X);
                    Canvas.SetTop(vis,  model.Y);
                }
            }
            ShowAlignmentGuides();
            UpdatePropertyFieldsForSelection();
            _overlay.ShowGrips(SelectionBoundingRect());
        }

        private void EndWidgetDrag()
        {
            if (!_draggingWidgets) return;
            _draggingWidgets = false;
            foreach (var v in _byModel.Values) if (v.IsMouseCaptured) v.ReleaseMouseCapture();
            _overlay.ShowGuides(Array.Empty<(double, double, double, double)>());
            _overlay.ShowGrips(SelectionBoundingRect());
            StatusActionText.Text = "Moved.";
        }

        /// <summary>
        /// Show pink alignment guides when the dragged widget's edges line up
        /// with any non-dragged widget's edges.  Cheap O(n*m) scan; HMIs
        /// rarely exceed a couple dozen widgets.
        /// </summary>
        private void ShowAlignmentGuides()
        {
            var guides = new List<(double, double, double, double)>();
            var others = _byModel.Keys.Where(k => !_selection.Any(s => s.Model == k)).ToList();
            foreach (var s in _selection)
            {
                var sm = s.Model;
                double[] sxs = { sm.X, sm.X + sm.W / 2, sm.X + sm.W };
                double[] sys = { sm.Y, sm.Y + sm.H / 2, sm.Y + sm.H };
                foreach (var o in others)
                {
                    double[] oxs = { o.X, o.X + o.W / 2, o.X + o.W };
                    double[] oys = { o.Y, o.Y + o.H / 2, o.Y + o.H };
                    foreach (var sx in sxs) foreach (var ox in oxs)
                        if (Math.Abs(sx - ox) < 1)
                            guides.Add((ox, Math.Min(sm.Y, o.Y) - 8, ox, Math.Max(sm.Y + sm.H, o.Y + o.H) + 8));
                    foreach (var sy in sys) foreach (var oy in oys)
                        if (Math.Abs(sy - oy) < 1)
                            guides.Add((Math.Min(sm.X, o.X) - 8, oy, Math.Max(sm.X + sm.W, o.X + o.W) + 8, oy));
                }
            }
            _overlay.ShowGuides(guides);
        }

        // =================================================================
        //                       Resize via grips
        // =================================================================

        private void OnResizeStarted(int grip, double mx, double my)
        {
            if (_selection.Count == 0) return;
            _resizeGrip = grip;
            _resizeStartRect = SelectionBoundingRect();
            _resizeMouseStart = new Point(mx, my);
            // For multi-select, we resize each independently by the same
            // proportional bbox scale (simplest, predictable). For single
            // selection, the target IS that one widget.
            _resizeTarget = _selection.Count == 1 ? _selection.First().Model : null;
        }

        private void OnResizeDragging(int grip, double mx, double my)
        {
            if (_resizeGrip < 0) return;
            double dx = mx - _resizeMouseStart.X;
            double dy = my - _resizeMouseStart.Y;
            ApplyResize(grip, dx, dy);
            _overlay.ShowGrips(SelectionBoundingRect());
            UpdatePropertyFieldsForSelection();
        }

        private void OnResizeEnded(int grip)
        {
            _resizeGrip = -1;
            _resizeTarget = null;
            _overlay.ShowGrips(SelectionBoundingRect());
            StatusActionText.Text = "Resized.";
        }

        private void ApplyResize(int grip, double dx, double dy)
        {
            // Per grip, compute new bbox.
            var r = _resizeStartRect;
            double nx = r.X, ny = r.Y, nw = r.Width, nh = r.Height;
            switch (grip)
            {
                case 0: nx += dx; ny += dy; nw -= dx; nh -= dy; break; // TL
                case 1:           ny += dy;           nh -= dy; break; // T
                case 2:           ny += dy; nw += dx; nh -= dy; break; // TR
                case 3:                     nw += dx;           break; // R
                case 4:                     nw += dx; nh += dy; break; // BR
                case 5:                               nh += dy; break; // B
                case 6: nx += dx;           nw -= dx; nh += dy; break; // BL
                case 7: nx += dx;           nw -= dx;           break; // L
            }
            nw = Math.Max(8, SnapToGrid(nw));
            nh = Math.Max(8, SnapToGrid(nh));
            nx = SnapToGrid(nx);
            ny = SnapToGrid(ny);

            if (_resizeTarget != null)
            {
                // Direct single-widget resize.
                _resizeTarget.X = nx; _resizeTarget.Y = ny;
                _resizeTarget.W = nw; _resizeTarget.H = nh;
                if (_byModel.TryGetValue(_resizeTarget, out var w))
                {
                    Canvas.SetLeft(w, nx); Canvas.SetTop(w, ny);
                    w.Width = nw; w.Height = nh; w.Refresh();
                }
            }
            else
            {
                // Multi-select: scale each widget proportionally inside new bbox.
                double sx = r.Width  > 0 ? nw / r.Width  : 1;
                double sy = r.Height > 0 ? nh / r.Height : 1;
                foreach (var s in _selection)
                {
                    var m = s.Model;
                    m.X = SnapToGrid(nx + (m.X - r.X) * sx);
                    m.Y = SnapToGrid(ny + (m.Y - r.Y) * sy);
                    m.W = Math.Max(8, SnapToGrid(m.W * sx));
                    m.H = Math.Max(8, SnapToGrid(m.H * sy));
                    Canvas.SetLeft(s, m.X); Canvas.SetTop(s, m.Y);
                    s.Width = m.W; s.Height = m.H; s.Refresh();
                }
            }
        }

        // =================================================================
        //                       Keyboard ops
        // =================================================================

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_designMode) return;

            bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // Block keys from editor textboxes
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;

            int step = shift ? 10 : 1;
            switch (e.Key)
            {
                case Key.Left:  NudgeSelection(-step, 0); e.Handled = true; return;
                case Key.Right: NudgeSelection(+step, 0); e.Handled = true; return;
                case Key.Up:    NudgeSelection(0, -step); e.Handled = true; return;
                case Key.Down:  NudgeSelection(0, +step); e.Handled = true; return;
                case Key.Delete: case Key.Back:
                    if (_selection.Count > 0) { DeleteSelection(); e.Handled = true; }
                    return;
            }

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.S: Save_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.D: DuplicateSelection(); e.Handled = true; return;
                    case Key.C: CopySelection();      e.Handled = true; return;
                    case Key.V: PasteClipboard();     e.Handled = true; return;
                    case Key.A:
                        _selection.Clear();
                        foreach (var v in _byModel.Values) _selection.Add(v);
                        HighlightSelection(); RebuildPropertyPanel(); UpdateStatusBar();
                        e.Handled = true; return;
                }
            }
        }

        private void NudgeSelection(int dx, int dy)
        {
            foreach (var s in _selection)
            {
                s.Model.X += dx; s.Model.Y += dy;
                Canvas.SetLeft(s, s.Model.X); Canvas.SetTop(s, s.Model.Y);
            }
            _overlay.ShowGrips(SelectionBoundingRect());
            UpdatePropertyFieldsForSelection();
        }

        // =================================================================
        //                       Duplicate / Copy / Paste / Z-order
        // =================================================================

        private void DuplicateSelection()
        {
            if (_selection.Count == 0) return;
            var newModels = new List<HmiWidgetModel>();
            foreach (var s in _selection.ToList())
            {
                var clone = CloneModel(s.Model);
                clone.X += 16; clone.Y += 16;
                clone.Z = (_doc.Widgets.Count == 0) ? 0 : _doc.Widgets.Max(w => w.Z) + 1;
                _doc.Widgets.Add(clone);
                AddWidgetVisual(clone);
                newModels.Add(clone);
            }
            _selection.Clear();
            foreach (var nm in newModels) if (_byModel.TryGetValue(nm, out var v)) _selection.Add(v);
            HighlightSelection(); RebuildPropertyPanel(); UpdateStatusBar();
            StatusActionText.Text = $"Duplicated {newModels.Count}.";
        }

        private void CopySelection()
        {
            _clipboard = _selection.Select(s => CloneModel(s.Model)).ToList();
            StatusActionText.Text = $"Copied {_clipboard.Count}.";
        }

        private void PasteClipboard()
        {
            if (_clipboard.Count == 0) return;
            var pasted = new List<HmiWidgetModel>();
            foreach (var t in _clipboard)
            {
                var clone = CloneModel(t);
                clone.X += 24; clone.Y += 24;
                clone.Z = (_doc.Widgets.Count == 0) ? 0 : _doc.Widgets.Max(w => w.Z) + 1;
                _doc.Widgets.Add(clone);
                AddWidgetVisual(clone);
                pasted.Add(clone);
            }
            _selection.Clear();
            foreach (var p in pasted) if (_byModel.TryGetValue(p, out var v)) _selection.Add(v);
            HighlightSelection(); RebuildPropertyPanel(); UpdateStatusBar();
            StatusActionText.Text = $"Pasted {pasted.Count}.";
        }

        private static HmiWidgetModel CloneModel(HmiWidgetModel src)
        {
            var json = JsonSerializer.Serialize(src);
            return JsonSerializer.Deserialize<HmiWidgetModel>(json)!;
        }

        private void DeleteSelection()
        {
            foreach (var s in _selection.ToList())
            {
                _doc.Widgets.Remove(s.Model);
                _byModel.Remove(s.Model);
                DesignCanvas.Children.Remove(s);
            }
            _selection.Clear();
            _overlay.HideGrips();
            RebuildPropertyPanel(); UpdateStatusBar();
            StatusActionText.Text = "Deleted.";
        }

        private void MoveZ(int dir)
        {
            if (_selection.Count == 0) return;
            int target = dir > 0 ? _doc.Widgets.Max(w => w.Z) + 1 : _doc.Widgets.Min(w => w.Z) - 1;
            foreach (var s in _selection)
            {
                s.Model.Z = target;
                Panel.SetZIndex(s, target);
                target += dir;
            }
            StatusActionText.Text = dir > 0 ? "Brought to front." : "Sent to back.";
        }

        private void AlignSelection(char align)
        {
            if (_selection.Count < 2) return;
            var first = _selection.First().Model;
            foreach (var s in _selection.Skip(1))
            {
                switch (align)
                {
                    case 'L': s.Model.X = first.X; break;
                    case 'C': s.Model.X = first.X + (first.W - s.Model.W) / 2; break;
                    case 'T': s.Model.Y = first.Y; break;
                }
                Canvas.SetLeft(s, s.Model.X); Canvas.SetTop(s, s.Model.Y);
            }
            _overlay.ShowGrips(SelectionBoundingRect());
            UpdatePropertyFieldsForSelection();
            StatusActionText.Text = "Aligned.";
        }

        // =================================================================
        //                       Property panel
        // =================================================================

        private readonly Dictionary<string, TextBox> _propBoxes = new();
        private readonly Dictionary<string, ComboBox> _propCombos = new();

        private void UpdatePropertyFieldsForSelection()
        {
            if (_selection.Count == 1)
            {
                var m = _selection.First().Model;
                if (_propBoxes.TryGetValue("X", out var bx)) bx.Text = m.X.ToString("F0");
                if (_propBoxes.TryGetValue("Y", out var by)) by.Text = m.Y.ToString("F0");
                if (_propBoxes.TryGetValue("Width", out var bw))  bw.Text = m.W.ToString("F0");
                if (_propBoxes.TryGetValue("Height", out var bh)) bh.Text = m.H.ToString("F0");
            }
        }

        private void RebuildPropertyPanel()
        {
            PropertyPanel.Children.Clear();
            _propBoxes.Clear();
            _propCombos.Clear();

            if (_selection.Count == 0)
            {
                PropertyPanel.Children.Add(new TextBlock
                {
                    Text = _designMode
                        ? "No widget selected.\n\n• Drag a widget from the palette onto the canvas.\n• Click a widget to select it.\n• Drag a rectangle on the canvas to multi-select.\n• Ctrl+click toggles a widget in/out of the selection."
                        : "Run mode — widgets are live.",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
                    FontSize = 11, TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            if (_selection.Count > 1)
            {
                PropertyPanel.Children.Add(new TextBlock
                {
                    Text = $"{_selection.Count} widgets selected",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                    FontSize = 14, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8),
                });
                PropertyPanel.Children.Add(new TextBlock
                {
                    Text = "Use the toolbar align buttons or right-click for group ops.",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
                    FontSize = 11, TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            var sel = _selection.First();
            var model = sel.Model;
            PropertyPanel.Children.Add(Header($"{model.Type}"));

            HmiWidgetBase? VisualFor(HmiWidgetModel mm) => _byModel.TryGetValue(mm, out var w) ? w : null;
            void RefreshFor(HmiWidgetModel mm) { VisualFor(mm)?.Refresh(); }
            void RebuildFor(HmiWidgetModel mm) { if (VisualFor(mm) != null) RebuildVisual(mm); }

            AddNumProp("X", model, () => model.X, v => { model.X = v; var w = VisualFor(model); if (w != null) Canvas.SetLeft(w, v); _overlay.ShowGrips(SelectionBoundingRect()); });
            AddNumProp("Y", model, () => model.Y, v => { model.Y = v; var w = VisualFor(model); if (w != null) Canvas.SetTop(w, v);  _overlay.ShowGrips(SelectionBoundingRect()); });
            AddNumProp("Width",  model, () => model.W, v => { model.W = Math.Max(8, v); var w = VisualFor(model); if (w != null) { w.Width  = model.W; w.Refresh(); } _overlay.ShowGrips(SelectionBoundingRect()); });
            AddNumProp("Height", model, () => model.H, v => { model.H = Math.Max(8, v); var w = VisualFor(model); if (w != null) { w.Height = model.H; w.Refresh(); } _overlay.ShowGrips(SelectionBoundingRect()); });

            AddTagProp("Tag", model);
            AddTextProp("Label", model, () => model.Label ?? "", v => { model.Label = v; RebuildFor(model); });

            if (model.Type is "Tank" or "Gauge" or "Trend" or "NumberDisplay"
                              or "Bargraph" or "PressureGauge" or "Selector" or "PIDBlock")
            {
                AddNumProp("Min", model, () => model.Min, v => { model.Min = v; RefreshFor(model); });
                AddNumProp("Max", model, () => model.Max, v => { model.Max = v; RefreshFor(model); });
            }
            if (model.Type is "Tank" or "Gauge" or "Bargraph" or "PressureGauge")
            {
                AddNumProp("Low alarm",  model, () => model.LowAlarm,  v => { model.LowAlarm  = v; RefreshFor(model); });
                AddNumProp("High alarm", model, () => model.HighAlarm, v => { model.HighAlarm = v; RefreshFor(model); });
            }
            if (model.Type is "NumberDisplay" or "NumberEntry" or "Gauge"
                              or "PressureGauge" or "Bargraph" or "PIDBlock")
            {
                AddTextProp("Format", model, () => model.Format ?? "", v => { model.Format = v; RefreshFor(model); });
                AddTextProp("Units",  model, () => model.Units  ?? "", v => { model.Units  = v; RebuildFor(model); });
            }
            if (model.Type == "Trend")
                AddNumProp("Samples", model, () => model.Samples, v => { model.Samples = (int)Math.Max(8, v); RefreshFor(model); });

            if (model.Type == "Button")
            {
                AddChoiceProp("Mode", model, () => model.Mode ?? "Momentary",
                    v => { model.Mode = v; RebuildFor(model); },
                    new[] { "Momentary", "Latching", "Set", "Reset" });
                AddTextProp("Reset target", model,
                    () => (model.Format != null && model.Format.StartsWith("ResetTag:")) ? model.Format.Substring("ResetTag:".Length) : "",
                    v => { model.Format = string.IsNullOrWhiteSpace(v) ? null : "ResetTag:" + v.Trim(); RebuildFor(model); });
            }

            PropertyPanel.Children.Add(new Button
            {
                Content = "Delete",
                Background = new SolidColorBrush(Color.FromRgb(0x8B, 0x2B, 0x2B)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5E)),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(8, 6, 8, 6),
                Command = new RelayCmd(DeleteSelection),
            });
        }

        private void RebuildVisual(HmiWidgetModel m)
        {
            if (!_byModel.TryGetValue(m, out var old)) return;
            int idx = DesignCanvas.Children.IndexOf(old);
            DesignCanvas.Children.Remove(old);
            _byModel.Remove(m);
            _selection.Remove(old);

            var w = ThemedWidgets.Build(m, _db, _designMode);
            w.Width = m.W; w.Height = m.H;
            Canvas.SetLeft(w, m.X); Canvas.SetTop(w, m.Y);
            Panel.SetZIndex(w, m.Z);
            w.WidgetSelected += sel => HandleWidgetClicked(m);
            w.PreviewMouseLeftButtonDown += (s, e) => StartWidgetDrag(m, e);
            w.PreviewMouseMove           += (s, e) => DoWidgetDrag(e);
            w.PreviewMouseLeftButtonUp   += (s, e) => EndWidgetDrag();
            w.ContextMenu = BuildContextMenu(m);
            DesignCanvas.Children.Insert(idx, w);
            _byModel[m] = w;
            _selection.Add(w);
            HighlightSelection();
        }

        private TextBlock Header(string text) => new()
        {
            Text = text, FontWeight = FontWeights.Bold, FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            Margin = new Thickness(0, 0, 0, 10),
        };

        private void AddNumProp(string label, HmiWidgetModel m, Func<double> get, Action<double> set)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)) });
            var tb = new TextBox
            {
                Text = get().ToString("F0"),
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
                FontFamily = new FontFamily("Consolas"),
            };
            void Commit() {
                try {
                    if (double.TryParse(tb.Text, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var v))
                        set(v);
                    else tb.Text = get().ToString("F0");
                } catch (Exception ex) {
                    System.Diagnostics.Debug.WriteLine($"Num-prop commit failed: {ex}");
                }
            }
            tb.LostFocus += (s, e) => Commit();
            tb.KeyDown   += (s, e) => { if (e.Key == Key.Enter) Commit(); };
            sp.Children.Add(tb);
            PropertyPanel.Children.Add(sp);
            _propBoxes[label] = tb;
        }

        private void AddTextProp(string label, HmiWidgetModel m, Func<string> get, Action<string> set)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)) });
            var tb = new TextBox
            {
                Text = get(),
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            };
            void Commit() {
                try { set(tb.Text); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Text-prop commit failed: {ex}"); }
            }
            tb.LostFocus += (s, e) => Commit();
            tb.KeyDown   += (s, e) => { if (e.Key == Key.Enter) Commit(); };
            sp.Children.Add(tb);
            PropertyPanel.Children.Add(sp);
            _propBoxes[label] = tb;
        }

        private void AddChoiceProp(string label, HmiWidgetModel m, Func<string> get, Action<string> set, string[] choices)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)) });
            var cb = new ComboBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            };
            foreach (var c in choices) cb.Items.Add(c);
            cb.SelectedItem = get();
            cb.SelectionChanged += (s, e) =>
            {
                try { if (cb.SelectedItem is string v) set(v); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Choice commit failed: {ex}"); }
            };
            sp.Children.Add(cb);
            PropertyPanel.Children.Add(sp);
            _propCombos[label] = cb;
        }

        private void AddTagProp(string label, HmiWidgetModel m)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)) });
            var cb = new ComboBox
            {
                IsEditable = true,
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            };
            foreach (var t in _db.Tags)
            {
                cb.Items.Add(t.Name);
                foreach (var member in t.Members.Keys) cb.Items.Add($"{t.Name}.{member}");
            }
            cb.Text = m.Tag ?? "";
            void Commit()
            {
                try { m.Tag = cb.Text; if (_byModel.ContainsKey(m)) RebuildVisual(m); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Tag commit failed: {ex}"); }
            }
            cb.LostFocus += (s, e) => Commit();
            cb.SelectionChanged += (s, e) => Commit();
            sp.Children.Add(cb);
            PropertyPanel.Children.Add(sp);
            _propCombos[label] = cb;
        }

        // =================================================================
        //                       Tag tree
        // =================================================================

        private void BuildTagTree(string? filter)
        {
            TagTree.Items.Clear();
            foreach (var t in _db.Tags)
            {
                if (t.Name.StartsWith("_") || t.Name.EndsWith("_ret")) continue;
                if (!string.IsNullOrEmpty(filter) && !t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !t.DataType.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                var item = new TreeViewItem
                {
                    Header = $"{t.Name}  ▸ {t.DataType}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                    FontSize = 11,
                    Tag = t.Name,
                };
                if (t.IsStructured || t.IsUserStruct || t.Members.Count > 0)
                {
                    foreach (var m in StructLikeMembers(t))
                    {
                        var sub = new TreeViewItem
                        {
                            Header = $".{m}",
                            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xCD, 0xCD)),
                            FontSize = 10, Tag = $"{t.Name}.{m}",
                        };
                        item.Items.Add(sub);
                    }
                    item.IsExpanded = true;
                }
                TagTree.Items.Add(item);
            }
        }

        private static IEnumerable<string> StructLikeMembers(Tag t)
        {
            if (t.IsStructured) { foreach (var m in new[] { "PRE", "ACC", "DN", "EN", "TT" }) yield return m; }
            foreach (var m in t.Members.Keys) yield return m;
        }

        private void TagFilter_Changed(object sender, TextChangedEventArgs e) => BuildTagTree(TagFilterBox.Text);

        private void TagTree_SelectedChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // Clicking a tag tree node assigns it to the currently-selected widget.
            if (_selection.Count != 1) return;
            if (e.NewValue is TreeViewItem item && item.Tag is string tagName)
            {
                var sel = _selection.First();
                sel.Model.Tag = tagName;
                RebuildVisual(sel.Model);
                if (_propCombos.TryGetValue("Tag", out var cb)) cb.Text = tagName;
                StatusActionText.Text = $"Bound to {tagName}";
            }
        }

        // =================================================================
        //                       Status bar
        // =================================================================

        private void UpdateStatusBar()
        {
            StatusSelectionText.Text = _selection.Count == 0
                ? "0 selected"
                : _selection.Count == 1
                    ? $"1 selected: {_selection.First().Model.Type}"
                    : $"{_selection.Count} selected";
        }

        // =================================================================

        private class RelayCmd : ICommand
        {
            private readonly Action _a;
            public RelayCmd(Action a) { _a = a; }
            public event EventHandler? CanExecuteChanged;
            public bool CanExecute(object? p) => true;
            public void Execute(object? p) => _a();
        }
    }
}
