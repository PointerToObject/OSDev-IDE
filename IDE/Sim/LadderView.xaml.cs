using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OSDevIDE.Sim
{
    /// <summary>
    /// Live graphical ladder view. Renders the compiled rungs of a routine
    /// as classic AB-style ladder logic — power rails, contact symbols,
    /// coil symbols, instruction boxes, branches — and highlights energized
    /// segments green as the VM runs.
    ///
    /// Power-flow evaluation walks the parsed rung items the same way the
    /// VM does (series AND, branch OR), but on a read-only path that only
    /// reports per-instruction energization for visualization. We don't
    /// execute outputs here — that's the VM's job.
    /// </summary>
    public partial class LadderView : UserControl
    {
        private PlcProgram? _program;
        private LadderVm?   _vm;
        private TagDatabase? _db;
        private string _currentRoutine = "";
        private ExpressionEvaluator? _eval;

        // -------- Visual palette --------
        private static readonly Brush RailOn      = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private static readonly Brush RailOff     = new SolidColorBrush(Color.FromRgb(0x35, 0x4A, 0x44));
        private static readonly Brush WireOn      = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private static readonly Brush WireOff     = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x52));
        private static readonly Brush BoxBg       = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x22));
        private static readonly Brush BoxEdge     = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x6A));
        private static readonly Brush BoxEdgeOn   = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private static readonly Brush LabelFg     = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xCF));
        private static readonly Brush LabelDim    = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly Brush RungNumberFg = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x6A));

        // -------- Layout constants --------
        private const double LeftRailX   = 70;
        private const double RungSpacing = 70;
        private const double FirstRungY  = 50;
        private const double SlotW       = 110;
        private const double ItemH       = 40;
        private const double BranchVPad  = 36;
        private const double Margin      = 30;
        private static readonly FontFamily Mono = new("Consolas");

        public LadderView()
        {
            InitializeComponent();
        }

        public void Load(PlcProgram program, LadderVm vm, TagDatabase db)
        {
            _program = program;
            _vm = vm;
            _db = db;
            _eval = new ExpressionEvaluator(db);
            RoutineCombo.Items.Clear();
            foreach (var r in program.Routines.Keys.OrderBy(n => n == program.MainRoutine ? 0 : 1).ThenBy(n => n))
                RoutineCombo.Items.Add(r);
            if (RoutineCombo.Items.Count > 0) RoutineCombo.SelectedIndex = 0;
        }

        /// <summary>Called from SimWindow after each VM tick.</summary>
        public void Refresh() => Render();

        private void Routine_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (RoutineCombo.SelectedItem is string name)
            {
                _currentRoutine = name;
                Render();
            }
        }

        // =================================================================
        //                       Rendering
        // =================================================================

        private void Render()
        {
            LadderCanvas.Children.Clear();
            if (_program == null || _db == null) return;
            if (string.IsNullOrEmpty(_currentRoutine)) return;
            if (!_program.Routines.TryGetValue(_currentRoutine, out var routine)) return;

            // Per rung: lay out items into slots, measure width, then draw.
            double y = FirstRungY;
            double maxX = LeftRailX + 800;
            int rungIdx = 0;
            int activeCount = 0;
            foreach (var rung in routine.Rungs)
            {
                bool isLast = (rungIdx == routine.Rungs.Count - 1);
                var (rungHeight, endX, rungEnergized) = DrawRung(rung, rungIdx, y);
                if (rungEnergized) activeCount++;
                if (endX > maxX) maxX = endX;
                y += rungHeight + RungSpacing - ItemH;  // RungSpacing is the row pitch
                rungIdx++;
            }

            // Power rails — left vertical
            double railH = Math.Max(0, y - FirstRungY);
            var leftRail = new Line
            {
                X1 = LeftRailX, Y1 = FirstRungY - 16,
                X2 = LeftRailX, Y2 = FirstRungY + railH,
                Stroke = RailOn, StrokeThickness = 3, SnapsToDevicePixels = true,
            };
            LadderCanvas.Children.Add(leftRail);

            // Right vertical rail — at maxX
            double rightX = maxX + Margin;
            var rightRail = new Line
            {
                X1 = rightX, Y1 = FirstRungY - 16,
                X2 = rightX, Y2 = FirstRungY + railH,
                Stroke = RailOff, StrokeThickness = 2, SnapsToDevicePixels = true,
            };
            LadderCanvas.Children.Add(rightRail);

            LadderCanvas.Width = rightX + Margin;
            LadderCanvas.Height = FirstRungY + railH + 16;

            StatsText.Text = $"{routine.Rungs.Count} rungs   {activeCount} energized";
        }

        /// <summary>
        /// Draw one rung at y. Returns (extra-height-used-by-branches, end-x, rung-energized).
        /// </summary>
        private (double extraH, double endX, bool energized) DrawRung(RungDef rung, int rungIdx, double y)
        {
            // Rung number on the left margin
            var num = new TextBlock
            {
                Text = rungIdx.ToString().PadLeft(3, '0'),
                Foreground = RungNumberFg, FontFamily = Mono, FontSize = 11,
            };
            Canvas.SetLeft(num, 8); Canvas.SetTop(num, y + ItemH / 2 - 8);
            LadderCanvas.Children.Add(num);

            double cy = y + ItemH / 2;          // vertical center of this rung's row

            // Evaluate power flow as we render so we know what to highlight
            var ctx = new FlowCtx { Flow = true };
            double x = LeftRailX;
            double extraH = 0;
            foreach (var item in rung.Parsed)
            {
                var (nextX, addH, _) = DrawItem(item, x, cy, ctx, branchTopY: cy, branchBottomY: cy);
                x = nextX;
                if (addH > extraH) extraH = addH;
            }

            // Wire from last instruction to right rail (drawn after we know rightX in Render).
            // For now extend the rung wire to the end-of-row by drawing it long.
            // Final rail join is done in Render after measuring maxX.
            return (extraH, x, ctx.Flow);
        }

        /// <summary>Recursively draw an item (instruction or branch) and advance x.</summary>
        private (double nextX, double addH, bool flowOut) DrawItem(RungItem item, double x, double cy, FlowCtx ctx, double branchTopY, double branchBottomY)
        {
            if (item is Branch br)
            {
                // Branch: lay out each path at its own y, OR the flow at the end.
                double branchSpacing = ItemH + BranchVPad;
                int paths = br.Paths.Count;
                double topY = cy - (paths - 1) * branchSpacing / 2.0;

                double maxEndX = x;
                bool anyTrue = false;
                bool flowIn = ctx.Flow;
                var endPositions = new List<(double pyEnd, double yPath, bool pathFlow)>();

                for (int i = 0; i < paths; i++)
                {
                    double yPath = topY + i * branchSpacing;
                    var subCtx = new FlowCtx { Flow = flowIn };
                    double sx = x;
                    foreach (var sub in br.Paths[i])
                    {
                        var (nx, _, _) = DrawItem(sub, sx, yPath, subCtx, branchTopY, branchBottomY);
                        sx = nx;
                    }
                    endPositions.Add((sx, yPath, subCtx.Flow));
                    if (subCtx.Flow) anyTrue = true;
                    if (sx > maxEndX) maxEndX = sx;
                }

                // Vertical connectors at the start of the branch
                double topMostY = endPositions.Min(p => p.yPath);
                double botMostY = endPositions.Max(p => p.yPath);
                bool anyPathTrueAtStart = flowIn;  // power enters all paths equally
                LadderCanvas.Children.Add(VLine(x, topMostY, botMostY, anyPathTrueAtStart));

                // Horizontal stubs from x to each path's start at its y (if path's y != cy)
                foreach (var (_, yPath, _) in endPositions)
                {
                    LadderCanvas.Children.Add(HLine(x, x, yPath, anyPathTrueAtStart));  // join dot
                }

                // Equalize ends: extend each path's end to maxEndX
                foreach (var (endX, yPath, pathFlow) in endPositions)
                {
                    if (endX < maxEndX)
                        LadderCanvas.Children.Add(HLine(endX, maxEndX, yPath, pathFlow));
                }

                // Vertical connector at the end of the branch
                LadderCanvas.Children.Add(VLine(maxEndX, topMostY, botMostY, anyTrue));

                ctx.Flow = anyTrue;
                double addH = Math.Max(0, (botMostY - topMostY) - ItemH);
                return (maxEndX, addH, anyTrue);
            }

            if (item is Instr ins)
            {
                return DrawInstr(ins, x, cy, ctx);
            }
            return (x, 0, ctx.Flow);
        }

        // -------- Instruction drawing --------

        private (double nextX, double addH, bool flowOut) DrawInstr(Instr ins, double x, double cy, FlowCtx ctx)
        {
            // Wire-in: line from previous x to this instruction's left edge.
            double slotX = x;
            double slotEnd = x + SlotW;

            switch (ins.Name)
            {
                // ----- Inputs (modify power flow) -----
                case "XIC":
                    DrawContact(slotX, cy, ins.Args.Count > 0 ? ins.Args[0] : "", energized: ctx.Flow, normallyClosed: false, tagTrue: ReadBool(ins.Args.Count > 0 ? ins.Args[0] : ""));
                    if (!ReadBool(ins.Args.Count > 0 ? ins.Args[0] : "")) ctx.Flow = false;
                    return (slotEnd, 0, ctx.Flow);
                case "XIO":
                    DrawContact(slotX, cy, ins.Args.Count > 0 ? ins.Args[0] : "", energized: ctx.Flow, normallyClosed: true, tagTrue: ReadBool(ins.Args.Count > 0 ? ins.Args[0] : ""));
                    if (ReadBool(ins.Args.Count > 0 ? ins.Args[0] : "")) ctx.Flow = false;
                    return (slotEnd, 0, ctx.Flow);
                case "EQU": case "NEQ": case "GRT": case "LES": case "GEQ": case "LEQ":
                {
                    double a = _eval!.Eval(ins.Args.ElementAtOrDefault(0) ?? "0");
                    double b = _eval!.Eval(ins.Args.ElementAtOrDefault(1) ?? "0");
                    bool result = ins.Name switch
                    {
                        "EQU" => a == b, "NEQ" => a != b,
                        "GRT" => a >  b, "LES" => a <  b,
                        "GEQ" => a >= b, "LEQ" => a <= b,
                        _ => false,
                    };
                    DrawCompareBox(slotX, cy, ins.Name, ins.Args, ctx.Flow && result, ctx.Flow);
                    ctx.Flow = ctx.Flow && result;
                    return (slotEnd + 30, 0, ctx.Flow);
                }
                case "LIM":
                {
                    double lo = _eval!.Eval(ins.Args.ElementAtOrDefault(0) ?? "0");
                    double tst = _eval!.Eval(ins.Args.ElementAtOrDefault(1) ?? "0");
                    double hi = _eval!.Eval(ins.Args.ElementAtOrDefault(2) ?? "0");
                    bool result = tst >= lo && tst <= hi;
                    DrawCompareBox(slotX, cy, "LIM", ins.Args, ctx.Flow && result, ctx.Flow);
                    ctx.Flow = ctx.Flow && result;
                    return (slotEnd + 40, 0, ctx.Flow);
                }
                case "ONS":
                {
                    DrawCompareBox(slotX, cy, "ONS", ins.Args, ctx.Flow, ctx.Flow);
                    // We don't track edge history here; approximate as flow-through.
                    return (slotEnd, 0, ctx.Flow);
                }

                // ----- Outputs (right-side; coil-style) -----
                case "OTE":
                    DrawCoil(slotX, cy, ins.Args.Count > 0 ? ins.Args[0] : "", energized: ctx.Flow, ReadBool(ins.Args.Count > 0 ? ins.Args[0] : ""), kind: "OTE");
                    return (slotEnd, 0, ctx.Flow);
                case "OTL":
                    DrawCoil(slotX, cy, ins.Args.Count > 0 ? ins.Args[0] : "", energized: ctx.Flow, ReadBool(ins.Args.Count > 0 ? ins.Args[0] : ""), kind: "OTL");
                    return (slotEnd, 0, ctx.Flow);
                case "OTU":
                    DrawCoil(slotX, cy, ins.Args.Count > 0 ? ins.Args[0] : "", energized: ctx.Flow, ReadBool(ins.Args.Count > 0 ? ins.Args[0] : ""), kind: "OTU");
                    return (slotEnd, 0, ctx.Flow);

                case "MOV": case "CPT":
                case "ADD": case "SUB": case "MUL": case "DIV":
                case "TON": case "TOF": case "RTO":
                case "CTU": case "CTD":
                case "RES": case "JSR": case "JMP": case "LBL":
                case "RET": case "NOP": case "COP": case "FILL":
                    DrawOutputBox(slotX, cy, ins.Name, ins.Args, ctx.Flow);
                    return (slotEnd + 30, 0, ctx.Flow);

                default:
                    DrawOutputBox(slotX, cy, ins.Name, ins.Args, ctx.Flow);
                    return (slotEnd, 0, ctx.Flow);
            }
        }

        // -------- Shape primitives --------

        private void DrawContact(double x, double cy, string tagText, bool energized, bool normallyClosed, bool tagTrue)
        {
            // Wire stub before
            LadderCanvas.Children.Add(HLine(x, x + (SlotW - 30) / 2, cy, energized && tagTrue));
            double cx = x + SlotW / 2;
            double half = 14;
            // Two vertical bars
            var lb = new Line { X1 = cx - half / 2, Y1 = cy - half, X2 = cx - half / 2, Y2 = cy + half,
                                Stroke = energized && (normallyClosed ? !tagTrue : tagTrue) ? WireOn : WireOff,
                                StrokeThickness = 2.5 };
            var rb = new Line { X1 = cx + half / 2, Y1 = cy - half, X2 = cx + half / 2, Y2 = cy + half,
                                Stroke = lb.Stroke,
                                StrokeThickness = 2.5 };
            LadderCanvas.Children.Add(lb); LadderCanvas.Children.Add(rb);
            // Diagonal slash for normally-closed contact (XIO)
            if (normallyClosed)
            {
                var slash = new Line { X1 = cx - half + 2, Y1 = cy + half - 2, X2 = cx + half - 2, Y2 = cy - half + 2,
                                       Stroke = lb.Stroke, StrokeThickness = 2 };
                LadderCanvas.Children.Add(slash);
            }
            // Wire stub after — only "powered" if the contact closed AND we had power in
            bool finalFlow = normallyClosed ? !tagTrue : tagTrue;
            LadderCanvas.Children.Add(HLine(cx + half / 2, x + SlotW, cy, energized && finalFlow));

            // Tag label above
            DrawLabel(tagText, cx, cy - half - 14, centerX: true,
                fg: (energized && finalFlow) ? LabelFg : LabelDim);
            // Value indicator below
            DrawLabel(tagTrue ? "1" : "0", cx, cy + half + 2, centerX: true,
                fg: tagTrue ? RailOn : LabelDim, sz: 10);
        }

        private void DrawCoil(double x, double cy, string tagText, bool energized, bool tagTrue, string kind)
        {
            // Wire stub before
            LadderCanvas.Children.Add(HLine(x, x + (SlotW - 30) / 2, cy, energized));
            double cx = x + SlotW / 2;
            double rx = 16, ry = 12;
            // Parens — drawn as two arc paths
            var lp = ArcPath(cx - rx, cy - ry, cx - rx, cy + ry, sweepRight: true);
            var rp = ArcPath(cx + rx, cy - ry, cx + rx, cy + ry, sweepRight: false);
            Brush coilStroke = (energized || tagTrue) ? WireOn : WireOff;
            lp.Stroke = coilStroke; lp.StrokeThickness = 2.5;
            rp.Stroke = coilStroke; rp.StrokeThickness = 2.5;
            LadderCanvas.Children.Add(lp); LadderCanvas.Children.Add(rp);
            // Mark for OTL/OTU
            if (kind == "OTL" || kind == "OTU")
            {
                var letter = new TextBlock { Text = kind == "OTL" ? "L" : "U",
                    Foreground = coilStroke, FontFamily = Mono, FontWeight = FontWeights.Bold, FontSize = 11 };
                Canvas.SetLeft(letter, cx - 4); Canvas.SetTop(letter, cy - 7);
                LadderCanvas.Children.Add(letter);
            }
            // Wire stub after
            LadderCanvas.Children.Add(HLine(cx + rx, x + SlotW, cy, energized));
            DrawLabel(tagText, cx, cy - ry - 14, centerX: true, fg: tagTrue ? LabelFg : LabelDim);
            DrawLabel($"[{kind}]", cx, cy + ry + 2, centerX: true, fg: LabelDim, sz: 9);
        }

        private void DrawCompareBox(double x, double cy, string op, List<string> args, bool resultTrue, bool flowIn)
        {
            // Wire stub before
            LadderCanvas.Children.Add(HLine(x, x + 8, cy, flowIn));
            double boxW = SlotW - 8;
            double boxH = ItemH;
            var border = new Border
            {
                Width = boxW, Height = boxH,
                Background = BoxBg,
                BorderBrush = resultTrue ? BoxEdgeOn : BoxEdge,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(2),
            };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock {
                Text = op, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = resultTrue ? RailOn : LabelFg,
                FontFamily = Mono, FontSize = 11, FontWeight = FontWeights.Bold });
            sp.Children.Add(new TextBlock {
                Text = string.Join("  ", args.Select(a => a.Length > 12 ? a.Substring(0, 11) + "…" : a)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = LabelDim, FontFamily = Mono, FontSize = 10 });
            border.Child = sp;
            Canvas.SetLeft(border, x + 8); Canvas.SetTop(border, cy - boxH / 2);
            LadderCanvas.Children.Add(border);
            // Wire stub after
            LadderCanvas.Children.Add(HLine(x + 8 + boxW, x + SlotW + 30, cy, flowIn && resultTrue));
        }

        private void DrawOutputBox(double x, double cy, string op, List<string> args, bool energized)
        {
            // Wire stub before
            LadderCanvas.Children.Add(HLine(x, x + 8, cy, energized));
            double boxW = SlotW - 8;
            double boxH = ItemH;
            var border = new Border
            {
                Width = boxW, Height = boxH,
                Background = BoxBg,
                BorderBrush = energized ? BoxEdgeOn : BoxEdge,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(2),
            };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock {
                Text = op, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = energized ? RailOn : LabelFg,
                FontFamily = Mono, FontSize = 11, FontWeight = FontWeights.Bold });
            sp.Children.Add(new TextBlock {
                Text = string.Join("  ", args.Select(a => a.Length > 12 ? a.Substring(0, 11) + "…" : a)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = LabelDim, FontFamily = Mono, FontSize = 10 });
            border.Child = sp;
            Canvas.SetLeft(border, x + 8); Canvas.SetTop(border, cy - boxH / 2);
            LadderCanvas.Children.Add(border);
            // Wire stub after (output passes flow through)
            LadderCanvas.Children.Add(HLine(x + 8 + boxW, x + SlotW + 30, cy, energized));
        }

        private void DrawLabel(string text, double x, double y, bool centerX, Brush? fg = null, double sz = 11)
        {
            var tb = new TextBlock { Text = text, Foreground = fg ?? LabelFg, FontFamily = Mono, FontSize = sz };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double left = centerX ? x - tb.DesiredSize.Width / 2 : x;
            Canvas.SetLeft(tb, left); Canvas.SetTop(tb, y);
            LadderCanvas.Children.Add(tb);
        }

        private Line HLine(double x1, double x2, double y, bool on) => new()
        {
            X1 = x1, Y1 = y, X2 = x2, Y2 = y,
            Stroke = on ? WireOn : WireOff, StrokeThickness = on ? 2.5 : 2,
            SnapsToDevicePixels = true,
        };
        private Line VLine(double x, double y1, double y2, bool on) => new()
        {
            X1 = x, Y1 = y1, X2 = x, Y2 = y2,
            Stroke = on ? WireOn : WireOff, StrokeThickness = on ? 2.5 : 2,
            SnapsToDevicePixels = true,
        };

        private static Path ArcPath(double x1, double y1, double x2, double y2, bool sweepRight)
        {
            // Quarter-arc style; sweep direction chooses which way the paren bulges.
            var fig = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false };
            var arc = new ArcSegment
            {
                Point = new Point(x2, y2),
                Size = new Size(8, Math.Abs(y2 - y1) / 2.0),
                SweepDirection = sweepRight ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
            };
            fig.Segments.Add(arc);
            return new Path { Data = new PathGeometry(new[] { fig }) };
        }

        private bool ReadBool(string operand)
        {
            if (_db == null || string.IsNullOrEmpty(operand)) return false;
            return _db.ReadBoolOperand(operand);
        }

        private class FlowCtx { public bool Flow; }
    }
}
