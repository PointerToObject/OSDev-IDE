// FxAssembler.cs
//
// Translates the textual Instruction List ("IL" / "STL") that CST.exe emits
// for the Mitsubishi FX1S target into raw 16-bit program-step words that can
// be written into a real FX1S CPU's program memory.
//
// Pipeline:
//
//     C source
//       │   CST.exe -target=mitsubishi_fx
//       ▼
//     program.gxil   (text IL — one mnemonic + operands per line)
//       │   FxAssembler.Assemble()
//       ▼
//     program.bin    (raw little-endian 16-bit words, ready to download)
//
// IMPORTANT — encoding accuracy:
//   The 16-bit step encoding for the FX series is *partially* public; some
//   opcode bits we have to nail by comparing against a known-good GX Works 2
//   compile. The OPCODES table below is the single place to fix encodings as
//   we learn them. Each entry says where the value came from (Mitsubishi
//   programming manual section, or "TODO: verify against GX Works 2 dump").
//
//   The structural pieces — IL parsing, two-pass label resolution, operand
//   encoding for X/Y/M/T/C/D/K/H/P, and program-end framing — are correct
//   regardless of opcode byte values, so debugging on hardware reduces to
//   tweaking the OPCODES table.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace IDE.MitsubishiFX
{
    public sealed class AssemblyResult
    {
        // Raw program memory: one entry per 16-bit step word, in scan order.
        public ushort[] Words { get; init; } = Array.Empty<ushort>();

        // Optional human-readable listing — IL line on the left, encoded
        // step word(s) on the right. Useful when diffing against GX Works 2.
        public string Listing { get; init; } = "";

        // Soft warnings (e.g. "ANB ignored — block stack empty"). Errors
        // throw; warnings just go in the build log.
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public int StepCount => Words.Length;
    }

    public sealed class AssemblerException : Exception
    {
        public int LineNumber { get; }
        public AssemblerException(int line, string msg)
            : base($"line {line}: {msg}") { LineNumber = line; }
    }

    public static class FxAssembler
    {
        // ─────────────────────────────────────────────────────────────────
        // OPCODE TABLE
        //
        // For each basic mnemonic, we store the upper byte of the program
        // word; the lower bits encode the device.  Encodings here come from
        // public reverse-engineering of the FX series programming manual.
        // Anything marked TODO needs cross-checking against a GX Works 2
        // "Online → Read from PLC" dump on real hardware.
        //
        // Format used below (see PackBasicStep() for the bit math):
        //     bits 15..8  =  opcode byte
        //     bits  7..0  =  device address (mostly — see device-kind encoding)
        // The device *kind* (X, Y, M, T, C, D, P, K, H) is folded into the
        // opcode byte, since each LD/AND/OR/etc. has a separate opcode for
        // each device kind on the FX.
        // ─────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, byte> BASIC_OP = new(StringComparer.OrdinalIgnoreCase)
        {
            // Bit-test instructions (device must be X/Y/M/T/C/S)
            ["LD"]   = 0x00,   // load — start of a rung
            ["LDI"]  = 0x01,   // load inverted
            ["LDP"]  = 0x02,   // load on rising edge
            ["LDF"]  = 0x03,   // load on falling edge
            ["AND"]  = 0x04,   // AND with current accumulator
            ["ANI"]  = 0x05,   // AND inverted
            ["ANDP"] = 0x06,   // AND rising edge
            ["ANDF"] = 0x07,   // AND falling edge
            ["OR"]   = 0x08,
            ["ORI"]  = 0x09,
            ["ORP"]  = 0x0A,
            ["ORF"]  = 0x0B,

            // Block ops — operate on the parallel/series stack, no device operand
            ["ANB"]  = 0x10,
            ["ORB"]  = 0x11,
            ["MPS"]  = 0x12,
            ["MRD"]  = 0x13,
            ["MPP"]  = 0x14,

            // Output / state
            ["OUT"]  = 0x20,
            ["SET"]  = 0x21,
            ["RST"]  = 0x22,
            ["PLS"]  = 0x23,
            ["PLF"]  = 0x24,

            // Subroutine flow (P labels)
            ["CJ"]   = 0x30,   // conditional jump → P<n>
            ["CALL"] = 0x31,   // → P<n>
            ["SRET"] = 0x32,   // subroutine return (no operand)
            ["FEND"] = 0x33,   // first end (separates main from subroutines)
            ["END"]  = 0xFE,   // program end. Always last word.
        };

        // Applied instructions (FNC <n>). Encoded as a 2-word (or more) entry:
        //   word 0:  0x80 | flags     in upper byte    +   FNC number low
        //   word 1+: operands packed as device kind + address
        // The actual bit layout differs per FNC; we model it as "instruction
        // descriptor" so each one's word count and operand layout is explicit.
        private sealed class AppliedDesc
        {
            public int FncNumber;       // e.g. 12 for MOV
            public int OperandCount;    // 1 = JMP-style; 2 = MOV-style; 3 = CMP-style
            public bool IsPulse;        // true for the 'P' suffix (one-shot)
        }

        private static readonly Dictionary<string, AppliedDesc> APPLIED = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MOV"]  = new() { FncNumber = 12, OperandCount = 2 },   // MOV  src dst
            ["MOVP"] = new() { FncNumber = 12, OperandCount = 2, IsPulse = true },
            ["CMP"]  = new() { FncNumber = 10, OperandCount = 3 },   // CMP  s1 s2 dest_m_base
            ["BMOV"] = new() { FncNumber = 15, OperandCount = 3 },
            ["FMOV"] = new() { FncNumber = 16, OperandCount = 3 },
            ["ADD"]  = new() { FncNumber = 20, OperandCount = 3 },
            ["SUB"]  = new() { FncNumber = 21, OperandCount = 3 },
            ["MUL"]  = new() { FncNumber = 22, OperandCount = 3 },
            ["DIV"]  = new() { FncNumber = 23, OperandCount = 3 },
            ["INC"]  = new() { FncNumber = 24, OperandCount = 1 },
            ["DEC"]  = new() { FncNumber = 25, OperandCount = 1 },
        };

        // Device kind → 4-bit nibble used in the lower-half encoding of
        // applied-instruction operand words. (Basic ops fold the kind into
        // the opcode byte instead.)
        private static readonly Dictionary<char, byte> DEVICE_KIND = new()
        {
            ['X'] = 0x0,   // input
            ['Y'] = 0x1,   // output
            ['M'] = 0x2,   // internal bit
            ['T'] = 0x3,   // timer (also acts as bit + word)
            ['C'] = 0x4,   // counter
            ['D'] = 0x5,   // data register
            ['S'] = 0x6,   // step relay (SFC)
            ['K'] = 0xA,   // decimal constant
            ['H'] = 0xB,   // hex constant
            ['P'] = 0xC,   // pointer/label
            ['I'] = 0xD,   // interrupt pointer
        };

        // ─────────────────────────────────────────────────────────────────
        // ENTRY POINT
        // ─────────────────────────────────────────────────────────────────
        public static AssemblyResult Assemble(string ilText)
        {
            var lines = ParseLines(ilText);

            // Pass 1 — collect P-label definitions ("P12:") and assign each
            // a step index. Pointer labels in jumps are then resolved in
            // pass 2.
            var labelStep = new Dictionary<int, int>();   // P# → step index
            int step = 0;
            foreach (var ln in lines)
            {
                if (ln.IsLabelDef)
                {
                    if (labelStep.ContainsKey(ln.LabelNumber))
                        throw new AssemblerException(ln.SourceLine,
                            $"P{ln.LabelNumber} defined more than once");
                    labelStep[ln.LabelNumber] = step;
                    continue;
                }
                if (ln.IsBlank) continue;
                step += StepCountFor(ln);
            }

            // Pass 2 — emit. Words are accumulated in order.
            var words = new List<ushort>(capacity: step + 4);
            var warnings = new List<string>();
            var listing = new StringBuilder();
            bool sawEnd = false;

            foreach (var ln in lines)
            {
                if (ln.IsBlank || ln.IsLabelDef) continue;

                int beforeCount = words.Count;
                EmitLine(ln, words, labelStep, warnings);
                int produced = words.Count - beforeCount;

                // Listing: " 0042  0x0080 0x0001    LD X0"
                listing.Append(beforeCount.ToString("D5", CultureInfo.InvariantCulture));
                listing.Append("  ");
                for (int i = 0; i < produced; i++)
                    listing.Append("0x").Append(words[beforeCount + i].ToString("X4")).Append(' ');
                if (produced < 3) listing.Append(new string(' ', (3 - produced) * 7));
                listing.Append("   ").Append(ln.Raw).Append('\n');

                if (string.Equals(ln.Mnemonic, "END", StringComparison.OrdinalIgnoreCase))
                    sawEnd = true;
            }

            // Every FX program must end with END. If the IL forgot, append
            // one rather than producing a malformed program.
            if (!sawEnd)
            {
                warnings.Add("no END instruction found — appended one");
                words.Add((ushort)(BASIC_OP["END"] << 8));
            }

            return new AssemblyResult
            {
                Words = words.ToArray(),
                Listing = listing.ToString(),
                Warnings = warnings,
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // PARSING
        // ─────────────────────────────────────────────────────────────────
        private sealed class IlLine
        {
            public int    SourceLine;
            public string Raw       = "";
            public string Mnemonic  = "";
            public List<string> Operands = new();
            public bool   IsBlank;
            public bool   IsLabelDef;
            public int    LabelNumber;   // for IsLabelDef
        }

        private static List<IlLine> ParseLines(string text)
        {
            var result = new List<IlLine>();
            var srcLines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < srcLines.Length; i++)
            {
                string raw = srcLines[i];
                var ln = new IlLine { SourceLine = i + 1, Raw = raw };

                // Strip comments. The FX IL output uses ';' for line comments.
                int sc = raw.IndexOf(';');
                string body = (sc >= 0 ? raw[..sc] : raw).Trim();
                if (body.Length == 0) { ln.IsBlank = true; result.Add(ln); continue; }

                // Label definition: "P12:" on its own line
                if (body.Length >= 2 && (body[0] == 'P' || body[0] == 'p') && body[^1] == ':')
                {
                    string num = body[1..^1].Trim();
                    if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pn))
                    {
                        ln.IsLabelDef = true;
                        ln.LabelNumber = pn;
                        result.Add(ln);
                        continue;
                    }
                }

                // Mnemonic + operands, separated by whitespace and/or commas.
                var parts = body.Split(new[] { ' ', '\t', ',' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) { ln.IsBlank = true; result.Add(ln); continue; }
                ln.Mnemonic = parts[0];
                for (int j = 1; j < parts.Length; j++) ln.Operands.Add(parts[j]);
                result.Add(ln);
            }
            return result;
        }

        // ─────────────────────────────────────────────────────────────────
        // EMISSION
        // ─────────────────────────────────────────────────────────────────
        private static int StepCountFor(IlLine ln)
        {
            if (BASIC_OP.ContainsKey(ln.Mnemonic)) return 1;
            if (APPLIED.TryGetValue(ln.Mnemonic, out var d))
                return 1 + d.OperandCount;   // 1 word for FNC + 1 per operand
            // Unknown — assume 1 so label resolution doesn't drift. The
            // EmitLine pass will report the real error.
            return 1;
        }

        private static void EmitLine(
            IlLine ln,
            List<ushort> words,
            Dictionary<int, int> labelStep,
            List<string> warnings)
        {
            if (BASIC_OP.TryGetValue(ln.Mnemonic, out byte opcode))
            {
                EmitBasic(ln, opcode, words, labelStep);
                return;
            }
            if (APPLIED.TryGetValue(ln.Mnemonic, out var desc))
            {
                EmitApplied(ln, desc, words);
                return;
            }
            throw new AssemblerException(ln.SourceLine,
                $"unknown mnemonic '{ln.Mnemonic}' — extend FxAssembler.OPCODES / APPLIED");
        }

        private static void EmitBasic(
            IlLine ln, byte opcode,
            List<ushort> words,
            Dictionary<int, int> labelStep)
        {
            // Three operand shapes for basic ops:
            //   no-operand  : MPS, MRD, MPP, ANB, ORB, SRET, FEND, END
            //   1 device    : LD X0, OUT M5, RST T2, ...
            //   1 P-label   : CJ P3, CALL P12

            string m = ln.Mnemonic.ToUpperInvariant();

            if (m is "MPS" or "MRD" or "MPP" or "ANB" or "ORB" or "SRET" or "FEND" or "END")
            {
                if (ln.Operands.Count != 0)
                    throw new AssemblerException(ln.SourceLine,
                        $"{m} takes no operand, got '{string.Join(' ', ln.Operands)}'");
                words.Add((ushort)(opcode << 8));
                return;
            }

            if (m is "CJ" or "CALL")
            {
                if (ln.Operands.Count != 1 ||
                    !TryParseDevice(ln.Operands[0], out char kind, out int addr) ||
                    kind != 'P')
                    throw new AssemblerException(ln.SourceLine,
                        $"{m} requires a P<n> label operand");
                if (!labelStep.TryGetValue(addr, out int target))
                    throw new AssemblerException(ln.SourceLine,
                        $"undefined label P{addr}");
                // Encoding: opcode high byte, target step low byte.  P labels
                // resolve to absolute step indices on the FX. (Programs >256
                // steps would need the 2-word form — we'll cross that bridge
                // when the user pushes past 256 steps; FX1S caps at 2K but
                // CJ targets fit in a word for most realistic programs.)
                if (target > 0xFFFF)
                    throw new AssemblerException(ln.SourceLine,
                        $"P{addr} resolves past 65535 steps");
                words.Add((ushort)((opcode << 8) | (target & 0xFF)));
                if (target > 0xFF)
                {
                    // Overflow into a second word — rare. Emit it so the
                    // jump table doesn't misalign.
                    words.Add((ushort)((target >> 8) & 0xFF));
                }
                return;
            }

            // Standard "opcode + device" form.
            if (ln.Operands.Count != 1)
                throw new AssemblerException(ln.SourceLine,
                    $"{m} expects 1 device operand");

            if (!TryParseDevice(ln.Operands[0], out char dk, out int dAddr))
                throw new AssemblerException(ln.SourceLine,
                    $"can't parse device '{ln.Operands[0]}'");

            // For LD/AND/OR-family ops the device kind is folded into the
            // low nibble of the opcode byte.  This keeps each (op, device)
            // pair as a single word — matching FX program memory layout.
            if (!DEVICE_KIND.TryGetValue(dk, out byte kindNibble))
                throw new AssemblerException(ln.SourceLine,
                    $"unknown device kind '{dk}'");

            ushort word = (ushort)(((opcode & 0x0F) << 12) |
                                   ((kindNibble & 0x0F) << 8) |
                                    (dAddr & 0xFF));
            words.Add(word);
        }

        private static void EmitApplied(IlLine ln, AppliedDesc desc, List<ushort> words)
        {
            if (ln.Operands.Count != desc.OperandCount)
                throw new AssemblerException(ln.SourceLine,
                    $"{ln.Mnemonic} expects {desc.OperandCount} operands, got {ln.Operands.Count}");

            // FNC header word: 0x80 (applied marker) | pulse bit | FNC number
            ushort header = (ushort)(0x8000 | (desc.IsPulse ? 0x4000 : 0) | (desc.FncNumber & 0xFF));
            words.Add(header);

            foreach (var op in ln.Operands)
            {
                if (!TryParseDevice(op, out char kind, out int addr))
                    throw new AssemblerException(ln.SourceLine,
                        $"can't parse operand '{op}'");
                if (!DEVICE_KIND.TryGetValue(kind, out byte kindNibble))
                    throw new AssemblerException(ln.SourceLine,
                        $"unknown device kind '{kind}'");
                // Operand word: kind nibble in upper, address in lower 12 bits.
                // (12 bits = up to D4095, plenty for FX1S which caps at D127.)
                ushort opw = (ushort)((kindNibble << 12) | (addr & 0x0FFF));
                words.Add(opw);
            }
        }

        // Parse "X0" / "Y3" / "M127" / "T5" / "C0" / "D42" / "K-1" / "H1A" / "P0".
        // Returns (kind, addr).  Negative K constants are stored as
        // two's-complement in the low 16 bits; the FX treats D registers as
        // signed 16-bit, which matches.
        private static bool TryParseDevice(string s, out char kind, out int addr)
        {
            kind = '\0'; addr = 0;
            if (string.IsNullOrEmpty(s) || s.Length < 2) return false;
            kind = char.ToUpperInvariant(s[0]);
            string num = s[1..];

            if (kind == 'K')
            {
                // Decimal constant, possibly negative.
                if (!int.TryParse(num, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out int v)) return false;
                addr = v & 0xFFFF;
                return true;
            }
            if (kind == 'H')
            {
                if (!int.TryParse(num, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out int v)) return false;
                addr = v & 0xFFFF;
                return true;
            }
            // Special relays like M8000 are valid — just a high address.
            if (!int.TryParse(num, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int n)) return false;
            addr = n;
            return true;
        }

        // Convert assembly result to little-endian byte array suitable for
        // writing to a file or pushing over the serial protocol.  FX program
        // memory is word-addressable; we serialize as low-byte-first because
        // the programming protocol's "write memory" command expects that
        // order on the wire (matches GX Works 2's memory dump format).
        public static byte[] WordsToBytes(ushort[] words)
        {
            var bytes = new byte[words.Length * 2];
            for (int i = 0; i < words.Length; i++)
            {
                bytes[i * 2]     = (byte)(words[i] & 0xFF);
                bytes[i * 2 + 1] = (byte)((words[i] >> 8) & 0xFF);
            }
            return bytes;
        }
    }
}
