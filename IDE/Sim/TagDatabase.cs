using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OSDevIDE.Sim
{
    /// <summary>
    /// One simulated PLC tag. Supports scalar BOOL/DINT/REAL and the
    /// structured types TIMER and COUNTER (which expose .PRE/.ACC/.DN/.EN
    /// members). Arrays are flattened to indexed siblings (`name[0]` etc.).
    /// </summary>
    public class Tag : INotifyPropertyChanged
    {
        public string Name { get; }
        public string DataType { get; }
        public int ArraySize { get; }   // 0 = scalar

        // Scalar state
        private bool _boolVal;
        private int _intVal;
        private double _realVal;

        // Structured (TIMER/COUNTER) members
        private int _pre;
        private int _acc;
        private bool _dn;
        private bool _en;
        private bool _tt;       // TIMER timing
        private bool _cu;       // CTU cu

        // Array state — boxed values per index (only used when ArraySize > 0)
        public object[]? Array { get; }

        // Generic struct-member storage. The L5X compiler emits things like
        // `motor1.motor_id` for arbitrary user-defined struct members; the
        // sim doesn't statically know the member layout, so we keep a flat
        // bag keyed by member name. ReadMember/WriteMember fall back here
        // when the member isn't one of the well-known TIMER/COUNTER bits.
        private readonly Dictionary<string, double> _members =
            new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, double> Members => _members;
        internal double GetMember(string name) =>
            _members.TryGetValue(name, out var v) ? v : 0;
        internal void SetMember(string name, double v)
        {
            if (_members.TryGetValue(name, out var cur) && cur == v) return;
            _members[name] = v;
            OnChanged(nameof(Members));
            OnChanged(nameof(DisplayValue));
        }

        public Tag(string name, string dataType, int arraySize = 0)
        {
            Name = name;
            DataType = dataType.ToUpperInvariant();
            ArraySize = arraySize;
            if (arraySize > 0) Array = new object[arraySize];
        }

        // ---- Force state ----
        // Real-PLC behavior: forcing locks a tag to a value. Reads return the
        // forced value; writes are silently dropped. Used heavily for
        // debugging — fake an input, override an output, etc.
        private bool   _forcedBool;
        private double _forcedNum;
        public bool IsForcedBool { get; private set; }
        public bool IsForcedNum  { get; private set; }
        public bool IsForced => IsForcedBool || IsForcedNum;

        public void Force(bool value)
        {
            IsForcedBool = true; IsForcedNum = false;
            _forcedBool = value;
            OnChanged(nameof(IsForced));
            OnChanged(nameof(Bool));
            OnChanged(nameof(DisplayValue));
        }
        public void Force(double value)
        {
            IsForcedNum = true; IsForcedBool = false;
            _forcedNum = value;
            OnChanged(nameof(IsForced));
            OnChanged(nameof(Int));
            OnChanged(nameof(Real));
            OnChanged(nameof(DisplayValue));
        }
        public void Unforce()
        {
            bool was = IsForced;
            IsForcedBool = false;
            IsForcedNum  = false;
            if (was)
            {
                OnChanged(nameof(IsForced));
                OnChanged(nameof(Bool));
                OnChanged(nameof(Int));
                OnChanged(nameof(Real));
                OnChanged(nameof(DisplayValue));
            }
        }

        public bool Bool
        {
            get => IsForcedBool ? _forcedBool : _boolVal;
            set { if (IsForcedBool) return; if (_boolVal != value) { _boolVal = value; OnChanged(); } }
        }
        public int  Int
        {
            get => IsForcedNum ? (int)_forcedNum : _intVal;
            set { if (IsForcedNum) return; if (_intVal != value) { _intVal = value; OnChanged(); } }
        }
        public double Real
        {
            get => IsForcedNum ? _forcedNum : _realVal;
            set { if (IsForcedNum) return; if (_realVal != value) { _realVal = value; OnChanged(); } }
        }

        public int  PRE  { get => _pre; set { if (_pre != value) { _pre = value; OnChanged(); } } }
        public int  ACC  { get => _acc; set { if (_acc != value) { _acc = value; OnChanged(); } } }
        public bool DN   { get => _dn;  set { if (_dn  != value) { _dn  = value; OnChanged(); } } }
        public bool EN   { get => _en;  set { if (_en  != value) { _en  = value; OnChanged(); } } }
        public bool TT   { get => _tt;  set { if (_tt  != value) { _tt  = value; OnChanged(); } } }
        public bool CU   { get => _cu;  set { if (_cu  != value) { _cu  = value; OnChanged(); } } }

        public bool IsStructured => DataType == "TIMER" || DataType == "COUNTER";
        public bool IsBool       => DataType == "BOOL";
        public bool IsReal       => DataType == "REAL" || DataType == "LREAL";
        public bool IsNumeric    => !IsBool && !IsStructured && !IsUserStruct;
        // Any non-primitive, non-TIMER/COUNTER data type — likely a UDT.
        // The sim tracks its members in the generic _members bag.
        public bool IsUserStruct => DataType != "BOOL" && DataType != "DINT" &&
                                    DataType != "INT"  && DataType != "SINT" &&
                                    DataType != "REAL" && DataType != "LREAL" &&
                                    DataType != "TIMER" && DataType != "COUNTER" &&
                                    DataType != "STRING" && DataType != "VOID";

        /// <summary>
        /// Read a scalar value as double (BOOL → 0/1, int → double, real → real).
        /// </summary>
        public double AsDouble()
        {
            // Goes through the public properties so force overrides apply.
            if (IsBool) return Bool ? 1.0 : 0.0;
            if (IsReal) return Real;
            return Int;
        }

        public string DisplayValue()
        {
            string suffix = IsForced ? "  [F]" : "";
            if (ArraySize > 0) return $"[{ArraySize}]";
            if (IsStructured)  return $"PRE={_pre} ACC={_acc} DN={(_dn ? 1 : 0)}";
            if (IsBool)        return (Bool ? "1" : "0") + suffix;
            if (IsReal)        return Real.ToString("G6") + suffix;
            if (IsUserStruct && _members.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in _members) parts.Add($"{kv.Key}={FormatMember(kv.Value)}");
                return "{ " + string.Join(", ", parts) + " }" + suffix;
            }
            return Int.ToString() + suffix;
        }

        private static string FormatMember(double v)
        {
            if (v == Math.Truncate(v) && Math.Abs(v) < 1e15) return ((long)v).ToString();
            return v.ToString("G6");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? prop = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayValue)));
        }
    }

    /// <summary>
    /// Tag dictionary. Lookup is case-insensitive (Logix is case-preserving but
    /// case-insensitive on access). Member references like `t.DN` are resolved
    /// on read via <see cref="ReadMember"/> / <see cref="WriteMember"/>.
    /// </summary>
    public class TagDatabase
    {
        public ObservableCollection<Tag> Tags { get; } = new();
        private readonly Dictionary<string, Tag> _byName = new(StringComparer.OrdinalIgnoreCase);

        public Tag Add(Tag t)
        {
            if (_byName.TryGetValue(t.Name, out var existing)) return existing;
            _byName[t.Name] = t;
            Tags.Add(t);
            return t;
        }

        public Tag? Find(string name) =>
            _byName.TryGetValue(name, out var t) ? t : null;

        public Tag GetOrAdd(string name, string dataType = "DINT")
        {
            if (_byName.TryGetValue(name, out var t)) return t;
            return Add(new Tag(name, dataType));
        }

        /// <summary>
        /// Read an operand that may be a literal, plain tag, indexed tag, or
        /// member-access (`t.DN`, `c.ACC`). Returns 0 if not resolvable.
        /// </summary>
        public double ReadOperand(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            text = text.Trim();

            if (double.TryParse(text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lit))
                return lit;

            // Member access
            int dot = text.IndexOf('.');
            if (dot > 0)
            {
                var baseName = text.Substring(0, dot);
                var member = text.Substring(dot + 1);
                var t = ResolveBase(baseName);
                if (t == null) return 0;
                return ReadMember(t, member);
            }

            // Indexed
            int br = text.IndexOf('[');
            if (br > 0 && text.EndsWith("]"))
            {
                var baseName = text.Substring(0, br);
                var idxText = text.Substring(br + 1, text.Length - br - 2);
                var t = Find(baseName);
                if (t == null || t.Array == null) return 0;
                int idx = (int)ReadOperand(idxText);
                if (idx < 0 || idx >= t.ArraySize) return 0;
                var v = t.Array[idx];
                if (v == null) return 0;
                return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            }

            var tag = Find(text);
            return tag?.AsDouble() ?? 0;
        }

        public bool ReadBoolOperand(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            int dot = text.IndexOf('.');
            if (dot > 0)
            {
                var t = ResolveBase(text.Substring(0, dot));
                if (t == null) return false;
                return ReadMember(t, text.Substring(dot + 1)) != 0;
            }
            return ReadOperand(text) != 0;
        }

        public void WriteOperand(string text, double value)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();

            int dot = text.IndexOf('.');
            if (dot > 0)
            {
                var t = ResolveBase(text.Substring(0, dot));
                if (t != null) WriteMember(t, text.Substring(dot + 1), value);
                return;
            }

            int br = text.IndexOf('[');
            if (br > 0 && text.EndsWith("]"))
            {
                var baseName = text.Substring(0, br);
                var idxText = text.Substring(br + 1, text.Length - br - 2);
                var t = Find(baseName);
                if (t == null || t.Array == null) return;
                int idx = (int)ReadOperand(idxText);
                if (idx < 0 || idx >= t.ArraySize) return;
                t.Array[idx] = value;
                return;
            }

            var tag = Find(text);
            if (tag == null) tag = Add(new Tag(text, "DINT"));
            if (tag.IsBool)      tag.Bool = value != 0;
            else if (tag.IsReal) tag.Real = value;
            else                 tag.Int = (int)value;
        }

        public void WriteBoolOperand(string text, bool value)
        {
            int dot = text.IndexOf('.');
            if (dot > 0)
            {
                var t = ResolveBase(text.Substring(0, dot));
                if (t != null) WriteMember(t, text.Substring(dot + 1), value ? 1 : 0);
                return;
            }
            var tag = Find(text);
            if (tag == null) tag = Add(new Tag(text, "BOOL"));
            if (tag.IsBool) tag.Bool = value;
            else            tag.Int = value ? 1 : 0;
        }

        private Tag? ResolveBase(string baseName)
        {
            int br = baseName.IndexOf('[');
            if (br > 0) baseName = baseName.Substring(0, br);
            return Find(baseName);
        }

        private double ReadMember(Tag t, string member)
        {
            return member.ToUpperInvariant() switch
            {
                "DN"  => t.DN ? 1 : 0,
                "EN"  => t.EN ? 1 : 0,
                "TT"  => t.TT ? 1 : 0,
                "CU"  => t.CU ? 1 : 0,
                "ACC" => t.ACC,
                "PRE" => t.PRE,
                _     => t.GetMember(member),
            };
        }

        private void WriteMember(Tag t, string member, double value)
        {
            switch (member.ToUpperInvariant())
            {
                case "DN":  t.DN = value != 0; break;
                case "EN":  t.EN = value != 0; break;
                case "TT":  t.TT = value != 0; break;
                case "CU":  t.CU = value != 0; break;
                case "ACC": t.ACC = (int)value; break;
                case "PRE": t.PRE = (int)value; break;
                default:    t.SetMember(member, value); break;
            }
        }
    }
}
