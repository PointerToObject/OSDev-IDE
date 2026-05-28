using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OSDevIDE.Sim
{
    public enum SimMode { Program, Run }

    /// <summary>
    /// Ladder logic virtual machine. One scan tick:
    ///   1. Set up routine execution stack with MainRoutine at the bottom.
    ///   2. Walk rungs sequentially; for each rung, evaluate input chain into
    ///      a `rung_true` boolean, then execute output instructions.
    ///   3. JSR pushes a new routine frame; RET pops. JMP/LBL jump within
    ///      the current routine's rungs.
    ///   4. Timer ACC advances by the configured scan delta.
    ///   5. Output coils (OTE) are driven to rung-true; OTL/OTU only on
    ///      rung-true.
    ///
    /// The VM is deliberately single-threaded; the host calls Step() from a
    /// DispatcherTimer.
    /// </summary>
    public class LadderVm
    {
        public PlcProgram Program { get; }
        public TagDatabase Db { get; }
        public SimMode Mode { get; set; } = SimMode.Program;
        public int ScanCount { get; private set; }
        public double LastScanMs { get; private set; }
        public int ScanIntervalMs { get; set; } = 10;
        public string CurrentRoutine { get; private set; } = "";

        public event Action? Ticked;

        private readonly ExpressionEvaluator _eval;
        // ONS storage: rising-edge memory per tag name (Logix-style storage bit)
        private readonly Dictionary<string, bool> _onsPrev = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxJsrDepth = 32;
        private const int MaxRungsPerScan = 100_000;

        public LadderVm(PlcProgram program, TagDatabase db)
        {
            Program = program;
            Db = db;
            _eval = new ExpressionEvaluator(db);
        }

        public void Step()
        {
            if (Mode != SimMode.Run) return;
            var sw = Stopwatch.StartNew();
            try
            {
                ExecuteRoutine(Program.MainRoutine, depth: 0, executedBudget: MaxRungsPerScan);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("VM error: " + ex);
            }
            sw.Stop();
            LastScanMs = sw.Elapsed.TotalMilliseconds;
            ScanCount++;
            AdvanceTimers(ScanIntervalMs);
            Ticked?.Invoke();
        }

        private void AdvanceTimers(int dtMs)
        {
            foreach (var tag in Db.Tags)
            {
                if (tag.DataType != "TIMER") continue;
                if (tag.EN)
                {
                    if (!tag.DN)
                    {
                        tag.ACC += dtMs;
                        if (tag.ACC >= tag.PRE && tag.PRE > 0)
                        {
                            tag.ACC = tag.PRE;
                            tag.DN = true;
                            tag.TT = false;
                        }
                        else
                        {
                            tag.TT = true;
                        }
                    }
                }
                else
                {
                    // EN dropped — TON resets when input goes false
                    if (tag.ACC != 0) tag.ACC = 0;
                    tag.DN = false;
                    tag.TT = false;
                }
            }
        }

        private int ExecuteRoutine(string name, int depth, int executedBudget)
        {
            if (depth >= MaxJsrDepth) return executedBudget;
            if (!Program.Routines.TryGetValue(name, out var routine)) return executedBudget;

            var prev = CurrentRoutine;
            CurrentRoutine = name;

            // Build label index for JMP targets
            var labelToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < routine.Rungs.Count; i++)
            {
                foreach (var item in routine.Rungs[i].Parsed)
                {
                    if (item is Instr ins && ins.Name == "LBL" && ins.Args.Count >= 1)
                        labelToIdx[ins.Args[0]] = i;
                }
            }

            int idx = 0;
            while (idx < routine.Rungs.Count && executedBudget > 0)
            {
                executedBudget--;
                var rung = routine.Rungs[idx];
                var ctx = new ExecCtx
                {
                    PowerFlow = true,
                    JumpTo = null,
                    Returned = false,
                };
                ExecuteItems(rung.Parsed, ctx);

                if (ctx.Returned) { CurrentRoutine = prev; return executedBudget; }
                if (ctx.JumpTo != null && labelToIdx.TryGetValue(ctx.JumpTo, out var targetIdx))
                {
                    idx = targetIdx + 1;
                    continue;
                }
                idx++;
            }

            CurrentRoutine = prev;
            return executedBudget;
        }

        private class ExecCtx
        {
            public bool PowerFlow;
            public string? JumpTo;
            public bool Returned;
        }

        private void ExecuteItems(List<RungItem> items, ExecCtx ctx)
        {
            foreach (var item in items)
            {
                if (ctx.Returned || ctx.JumpTo != null) return;

                if (item is Branch b)
                {
                    bool anyTrue = false;
                    // Each sub-path starts from the current power flow, runs in
                    // sequence, and contributes its final power flow to the OR.
                    foreach (var path in b.Paths)
                    {
                        var sub = new ExecCtx { PowerFlow = ctx.PowerFlow };
                        ExecuteItems(path, sub);
                        if (sub.Returned) { ctx.Returned = true; return; }
                        if (sub.JumpTo != null) { ctx.JumpTo = sub.JumpTo; return; }
                        if (sub.PowerFlow) anyTrue = true;
                    }
                    ctx.PowerFlow = anyTrue;
                    continue;
                }

                if (item is Instr ins) ExecuteInstr(ins, ctx);
            }
        }

        private void ExecuteInstr(Instr ins, ExecCtx ctx)
        {
            string a0 = ins.Args.Count > 0 ? ins.Args[0] : "";
            string a1 = ins.Args.Count > 1 ? ins.Args[1] : "";
            string a2 = ins.Args.Count > 2 ? ins.Args[2] : "";

            switch (ins.Name)
            {
                // -------------------- INPUTS (modify power flow) --------------------
                case "XIC":
                    ctx.PowerFlow &= Db.ReadBoolOperand(a0);
                    return;
                case "XIO":
                    ctx.PowerFlow &= !Db.ReadBoolOperand(a0);
                    return;
                case "EQU": ctx.PowerFlow &= _eval.Eval(a0) == _eval.Eval(a1); return;
                case "NEQ": ctx.PowerFlow &= _eval.Eval(a0) != _eval.Eval(a1); return;
                case "GRT": ctx.PowerFlow &= _eval.Eval(a0) >  _eval.Eval(a1); return;
                case "LES": ctx.PowerFlow &= _eval.Eval(a0) <  _eval.Eval(a1); return;
                case "GEQ": ctx.PowerFlow &= _eval.Eval(a0) >= _eval.Eval(a1); return;
                case "LEQ": ctx.PowerFlow &= _eval.Eval(a0) <= _eval.Eval(a1); return;
                case "LIM":
                    {
                        // LIM(low, test, high) — true if low <= test <= high
                        double lo = _eval.Eval(a0), tst = _eval.Eval(a1), hi = _eval.Eval(a2);
                        ctx.PowerFlow &= tst >= lo && tst <= hi;
                        return;
                    }
                case "ONS":
                    {
                        // Logix ONS uses a storage bit; rises one scan on each
                        // 0→1 of the storage bit's *driving* power flow.
                        bool prev = _onsPrev.TryGetValue(a0, out var p) && p;
                        bool now = ctx.PowerFlow;
                        _onsPrev[a0] = now;
                        ctx.PowerFlow = now && !prev;
                        return;
                    }

                // -------------------- OUTPUTS (act when rung-true) --------------------
                case "OTE":
                    Db.WriteBoolOperand(a0, ctx.PowerFlow);
                    return;
                case "OTL":
                    if (ctx.PowerFlow) Db.WriteBoolOperand(a0, true);
                    return;
                case "OTU":
                    if (ctx.PowerFlow) Db.WriteBoolOperand(a0, false);
                    return;
                case "MOV":
                    if (ctx.PowerFlow) Db.WriteOperand(a1, _eval.Eval(a0));
                    return;
                case "CPT":
                    if (ctx.PowerFlow) Db.WriteOperand(a0, _eval.Eval(a1));
                    return;
                case "ADD":
                    if (ctx.PowerFlow) Db.WriteOperand(a2, _eval.Eval(a0) + _eval.Eval(a1));
                    return;
                case "SUB":
                    if (ctx.PowerFlow) Db.WriteOperand(a2, _eval.Eval(a0) - _eval.Eval(a1));
                    return;
                case "MUL":
                    if (ctx.PowerFlow) Db.WriteOperand(a2, _eval.Eval(a0) * _eval.Eval(a1));
                    return;
                case "DIV":
                    if (ctx.PowerFlow)
                    {
                        double r = _eval.Eval(a1);
                        Db.WriteOperand(a2, r != 0 ? _eval.Eval(a0) / r : 0);
                    }
                    return;

                case "TON":
                    {
                        var t = Db.GetOrAdd(a0, "TIMER");
                        t.PRE = (int)_eval.Eval(a1);
                        if (ctx.PowerFlow)
                        {
                            t.EN = true;
                        }
                        else
                        {
                            t.EN = false;
                            t.DN = false;
                            t.TT = false;
                            t.ACC = 0;
                        }
                        return;
                    }
                case "TOF":
                    {
                        var t = Db.GetOrAdd(a0, "TIMER");
                        t.PRE = (int)_eval.Eval(a1);
                        // TOF: DN = true while EN true; on falling edge, accumulate
                        // until PRE, then DN = false.
                        if (ctx.PowerFlow)
                        {
                            t.EN = true; t.DN = true; t.ACC = 0; t.TT = false;
                        }
                        else if (t.EN)
                        {
                            t.EN = false; t.TT = true; t.ACC = 0;
                        }
                        return;
                    }
                case "RES":
                    if (ctx.PowerFlow)
                    {
                        var t = Db.Find(a0);
                        if (t != null) { t.ACC = 0; t.DN = false; t.EN = false; t.TT = false; t.CU = false; }
                    }
                    return;
                case "CTU":
                    {
                        var t = Db.GetOrAdd(a0, "COUNTER");
                        t.PRE = (int)_eval.Eval(a1);
                        bool prevCu = t.CU;
                        t.CU = ctx.PowerFlow;
                        if (ctx.PowerFlow && !prevCu) t.ACC++;
                        if (t.ACC >= t.PRE) t.DN = true;
                        return;
                    }
                case "CTD":
                    {
                        var t = Db.GetOrAdd(a0, "COUNTER");
                        t.PRE = (int)_eval.Eval(a1);
                        bool prevCu = t.CU;
                        t.CU = ctx.PowerFlow;
                        if (ctx.PowerFlow && !prevCu) t.ACC--;
                        if (t.ACC <= 0) t.DN = true;
                        return;
                    }

                case "JSR":
                    if (ctx.PowerFlow && ins.Args.Count >= 1)
                    {
                        var sub = ins.Args[0];
                        ExecuteRoutine(sub, depth: 1, executedBudget: 10000);
                    }
                    return;
                case "JMP":
                    if (ctx.PowerFlow && ins.Args.Count >= 1) ctx.JumpTo = ins.Args[0];
                    return;
                case "LBL":
                    return;
                case "RET":
                    if (ctx.PowerFlow) ctx.Returned = true;
                    return;
                case "NOP":
                    return;
                case "COP":
                    // COP(src, dst, len) — copy array elements
                    if (ctx.PowerFlow)
                    {
                        var srcTag = Db.Find(StripIndex(a0));
                        var dstTag = Db.Find(StripIndex(a1));
                        int len = (int)_eval.Eval(a2);
                        if (srcTag?.Array != null && dstTag?.Array != null)
                        {
                            for (int i = 0; i < len && i < srcTag.ArraySize && i < dstTag.ArraySize; i++)
                                dstTag.Array[i] = srcTag.Array[i];
                        }
                    }
                    return;
                case "FILL":
                    if (ctx.PowerFlow)
                    {
                        double val = _eval.Eval(a0);
                        var dstTag = Db.Find(StripIndex(a1));
                        int len = (int)_eval.Eval(a2);
                        if (dstTag?.Array != null)
                        {
                            for (int i = 0; i < len && i < dstTag.ArraySize; i++)
                                dstTag.Array[i] = val;
                        }
                    }
                    return;

                default:
                    // Unknown instruction — no-op
                    return;
            }
        }

        private static string StripIndex(string s)
        {
            int br = s.IndexOf('[');
            return br > 0 ? s.Substring(0, br) : s;
        }
    }
}
