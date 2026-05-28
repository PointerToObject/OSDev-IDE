using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OSDevIDE.Sim
{
    public partial class SimWindow : Window
    {
        private readonly PlcProgram _program;
        private readonly TagDatabase _db;
        private readonly LadderVm _vm;
        private readonly DispatcherTimer _timer;
        private HmiDesigner? _designer;
        private LadderView? _ladderView;

        public SimWindow(string l5xPath, string? hmiPathHint = null)
        {
            InitializeComponent();

            _program = L5XReader.Load(l5xPath);
            _db = new TagDatabase();
            L5XReader.HydrateDatabase(_program, _db);
            _vm = new LadderVm(_program, _db);

            TagGrid.ItemsSource = _db.Tags;
            BuildAutoHmi();

            // Designer auto-loads if a .hmi file exists at the conventional path
            // (or hint). Else opens empty in design mode.
            string hmiPath = !string.IsNullOrEmpty(hmiPathHint)
                ? hmiPathHint!
                : Path.Combine(Path.GetDirectoryName(l5xPath)!, "..", "Source", "main.hmi");
            try { hmiPath = Path.GetFullPath(hmiPath); } catch { }
            _designer = new HmiDesigner(_db, hmiPath);
            HmiHost.Content = _designer;

            _ladderView = new LadderView();
            _ladderView.Load(_program, _vm, _db);
            LadderHost.Content = _ladderView;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_vm.ScanIntervalMs) };
            _timer.Tick += (s, e) => { _vm.Step(); UpdateStatus(); _designer?.Tick(); _ladderView?.Refresh(); };

            UpdateStatus();
            _ladderView?.Refresh();
            StatusText.Text = $"Loaded {_program.Name} - {_db.Tags.Count} tags, {_program.Routines.Count} routines";
        }

        private void UpdateStatus()
        {
            ScanCountText.Text = $"Scans: {_vm.ScanCount}";
            ScanTimeText.Text  = $"Last scan: {_vm.LastScanMs:F2} ms";
            RoutineText.Text   = string.IsNullOrEmpty(_vm.CurrentRoutine) ? "—" : _vm.CurrentRoutine;
            int forced = 0;
            foreach (var t in _db.Tags) if (t.IsForced) forced++;
            ForcedCountText.Text = forced == 0 ? "" : $"⚠ {forced} forced";
        }

        private void TagGrid_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (TagGrid.SelectedItem is not Tag tag) return;
            var cm = new ContextMenu();

            MenuItem MI(string head, Action act, bool enabled = true)
            {
                var m = new MenuItem { Header = head, IsEnabled = enabled };
                m.Click += (s, args) => act();
                return m;
            }

            if (tag.IsBool)
            {
                cm.Items.Add(MI("Force ON",  () => { tag.Force(true);  UpdateStatus(); _ladderView?.Refresh(); }));
                cm.Items.Add(MI("Force OFF", () => { tag.Force(false); UpdateStatus(); _ladderView?.Refresh(); }));
            }
            else if (!tag.IsStructured && !tag.IsUserStruct)
            {
                cm.Items.Add(MI("Force value…", () =>
                {
                    var dlg = new ForceValueDialog(tag) { Owner = this };
                    if (dlg.ShowDialog() == true)
                    {
                        tag.Force(dlg.Value);
                        UpdateStatus(); _ladderView?.Refresh();
                    }
                }));
            }

            cm.Items.Add(new Separator());
            cm.Items.Add(MI("Remove force", () => { tag.Unforce(); UpdateStatus(); _ladderView?.Refresh(); }, tag.IsForced));
            cm.Items.Add(MI("Unforce all",  () => UnforceAll_Click(sender, new RoutedEventArgs())));

            cm.PlacementTarget = TagGrid;
            cm.IsOpen = true;
            e.Handled = true;
        }

        private void UnforceAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in _db.Tags) if (t.IsForced) t.Unforce();
            UpdateStatus();
            _ladderView?.Refresh();
            StatusText.Text = "All forces cleared.";
        }

        private void ModeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.Mode == SimMode.Program)
            {
                _vm.Mode = SimMode.Run;
                ModeText.Text = "RUN";
                ModeIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Play;
                ModeIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                _timer.Interval = TimeSpan.FromMilliseconds(_vm.ScanIntervalMs);
                _timer.Start();
                StatusText.Text = "Running.";
                // Flip the designer into Run mode automatically — operator UX
                _designer?.SetDesignMode(false);
            }
            else
            {
                _vm.Mode = SimMode.Program;
                ModeText.Text = "PROGRAM";
                ModeIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Pause;
                ModeIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
                _timer.Stop();
                StatusText.Text = "Halted. Edit tags freely; press Step or switch to Run.";
            }
        }

        private void StepOnce_Click(object sender, RoutedEventArgs e)
        {
            var savedMode = _vm.Mode;
            _vm.Mode = SimMode.Run;
            _vm.Step();
            _vm.Mode = savedMode;
            UpdateStatus();
            _designer?.Tick();
            _ladderView?.Refresh();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (_timer.IsEnabled) _timer.Stop();
            _vm.Mode = SimMode.Program;
            ModeText.Text = "PROGRAM";
            ModeIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Pause;
            foreach (var tag in _db.Tags)
            {
                tag.Bool = false; tag.Int = 0; tag.Real = 0;
                tag.PRE = 0; tag.ACC = 0; tag.DN = false; tag.EN = false; tag.TT = false; tag.CU = false;
            }
            L5XReader.HydrateDatabase(_program, _db);
            StatusText.Text = "Tags reset to initial values.";
            UpdateStatus();
            _ladderView?.Refresh();
        }

        private void ScanRate_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Guard: SelectedIndex="1" in the XAML fires this during
            // InitializeComponent, before _vm / _timer are constructed.
            if (_vm == null || _timer == null) return;

            if (ScanRateCombo.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out var ms))
            {
                _vm.ScanIntervalMs = ms;
                if (_timer.IsEnabled)
                {
                    _timer.Stop();
                    _timer.Interval = TimeSpan.FromMilliseconds(ms);
                    _timer.Start();
                }
            }
        }

        private void BuildAutoHmi()
        {
            AutoHmiPanel.Children.Clear();
            foreach (var tag in _db.Tags)
            {
                if (tag.Name.StartsWith("_") || tag.Name.EndsWith("_ret"))
                    continue;
                AutoHmiPanel.Children.Add(HmiWidgets.Build(tag, _db));
            }
        }
    }
}
