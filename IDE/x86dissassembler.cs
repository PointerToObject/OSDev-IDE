using System;
using System.Collections.Generic;
using System.Text;

namespace OSDevIDE
{
    /// <summary>
    /// Real x86-32 disassembler for SubsetC compiler output.
    /// Handles ModR/M, SIB, displacement, immediates, 0x0F prefixes,
    /// and all instructions the compiler actually generates.
    /// </summary>
    public class x86Disassembler
    {
        private byte[] _code;
        private int _pos;
        private int _baseAddr;
        private int _instrStart;

        private static readonly string[] Reg32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };
        private static readonly string[] Reg16 = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" };
        private static readonly string[] Reg8 = { "al", "cl", "dl", "bl", "ah", "ch", "dh", "bh" };
        private static readonly string[] SegRegs = { "es", "cs", "ss", "ds", "fs", "gs" };

        private static readonly string[] CondCodes = {
            "o", "no", "b", "ae", "e", "ne", "be", "a",
            "s", "ns", "p", "np", "l", "ge", "le", "g"
        };

        private static readonly string[] AluOps = { "add", "or", "adc", "sbb", "and", "sub", "xor", "cmp" };
        private static readonly string[] ShiftOps = { "rol", "ror", "rcl", "rcr", "shl", "shr", "sal", "sar" };

        public x86Disassembler(byte[] code, int baseAddress = 0)
        {
            _code = code;
            _baseAddr = baseAddress;
        }

        public List<DisasmLine> DisassembleRange(int offset, int count)
        {
            var result = new List<DisasmLine>();
            _pos = offset;
            int end = Math.Min(offset + count, _code.Length);

            while (_pos < end)
            {
                try
                {
                    var line = DisassembleOne();
                    if (line != null)
                        result.Add(line);
                    else
                        break;
                }
                catch
                {
                    // If decode fails, emit db and advance
                    if (_pos < _code.Length)
                    {
                        result.Add(new DisasmLine
                        {
                            Address = _baseAddr + _pos,
                            Bytes = new byte[] { _code[_pos] },
                            Mnemonic = "db",
                            Operands = $"0x{_code[_pos]:X2}"
                        });
                        _pos++;
                    }
                    else break;
                }
            }
            return result;
        }

        private byte ReadByte()
        {
            if (_pos >= _code.Length) throw new IndexOutOfRangeException();
            return _code[_pos++];
        }

        private ushort ReadWord()
        {
            byte lo = ReadByte();
            byte hi = ReadByte();
            return (ushort)(lo | (hi << 8));
        }

        private uint ReadDword()
        {
            byte b0 = ReadByte();
            byte b1 = ReadByte();
            byte b2 = ReadByte();
            byte b3 = ReadByte();
            return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        }

        private int ReadSByte() => (sbyte)ReadByte();
        private int ReadSWord() => (short)ReadWord();
        private int ReadSDword() => (int)ReadDword();

        private byte[] GetInstrBytes()
        {
            int len = _pos - _instrStart;
            byte[] b = new byte[len];
            Array.Copy(_code, _instrStart, b, 0, len);
            return b;
        }

        // Decode ModR/M byte → (mod, reg, rm)
        private (int mod, int reg, int rm) DecodeModRM(byte b) =>
            ((b >> 6) & 3, (b >> 3) & 7, b & 7);

        // Full effective address string from ModR/M + optional SIB
        private string DecodeRM32(byte modrm, string size = "dword")
        {
            var (mod, _, rm) = DecodeModRM(modrm);

            if (mod == 3)
            {
                // Register direct
                if (size == "byte") return Reg8[rm];
                if (size == "word") return Reg16[rm];
                return Reg32[rm];
            }

            string addr;

            if (rm == 4) // SIB follows
            {
                byte sib = ReadByte();
                int scale = (sib >> 6) & 3;
                int index = (sib >> 3) & 7;
                int bas = sib & 7;

                if (mod == 0 && bas == 5)
                {
                    int disp32 = ReadSDword();
                    if (index == 4)
                        addr = $"0x{(uint)disp32:X}";
                    else
                        addr = $"{Reg32[index]}*{1 << scale} + 0x{(uint)disp32:X}";
                }
                else
                {
                    addr = Reg32[bas];
                    if (index != 4)
                        addr += $" + {Reg32[index]}*{1 << scale}";

                    if (mod == 1)
                    {
                        int disp8 = ReadSByte();
                        if (disp8 < 0) addr += $" - {-disp8}";
                        else if (disp8 > 0) addr += $" + {disp8}";
                    }
                    else if (mod == 2)
                    {
                        int disp32 = ReadSDword();
                        if (disp32 < 0) addr += $" - 0x{-disp32:X}";
                        else addr += $" + 0x{disp32:X}";
                    }
                }
            }
            else if (mod == 0 && rm == 5)
            {
                // disp32 only
                uint disp32 = ReadDword();
                addr = $"0x{disp32:X}";
            }
            else
            {
                addr = Reg32[rm];
                if (mod == 1)
                {
                    int disp8 = ReadSByte();
                    if (disp8 < 0) addr += $" - {-disp8}";
                    else if (disp8 > 0) addr += $" + {disp8}";
                }
                else if (mod == 2)
                {
                    int disp32 = ReadSDword();
                    if (disp32 < 0) addr += $" - 0x{-disp32:X}";
                    else addr += $" + 0x{disp32:X}";
                }
            }

            string prefix = size == "byte" ? "byte" : size == "word" ? "word" : "dword";
            return $"{prefix} [{addr}]";
        }

        private DisasmLine MakeLine(string mnemonic, string operands = "")
        {
            return new DisasmLine
            {
                Address = _baseAddr + _instrStart,
                Bytes = GetInstrBytes(),
                Mnemonic = mnemonic,
                Operands = operands
            };
        }

        public DisasmLine DisassembleOne()
        {
            if (_pos >= _code.Length) return null;
            _instrStart = _pos;

            byte op = ReadByte();

            // ── Simple single-byte instructions ──
            switch (op)
            {
                case 0x90: return MakeLine("nop");
                case 0xC3: return MakeLine("ret");
                case 0xC9: return MakeLine("leave");
                case 0xCC: return MakeLine("int3");
                case 0xF4: return MakeLine("hlt");
                case 0xFA: return MakeLine("cli");
                case 0xFB: return MakeLine("sti");
                case 0xFC: return MakeLine("cld");
                case 0xFD: return MakeLine("std");
                case 0x99: return MakeLine("cdq");
                case 0x98: return MakeLine("cwde");
                case 0x9C: return MakeLine("pushfd");
                case 0x9D: return MakeLine("popfd");
                case 0xF3: // rep prefix
                    {
                        byte next = ReadByte();
                        if (next == 0xA4) return MakeLine("rep movsb");
                        if (next == 0xA5) return MakeLine("rep movsd");
                        if (next == 0xAA) return MakeLine("rep stosb");
                        if (next == 0xAB) return MakeLine("rep stosd");
                        if (next == 0xAE) return MakeLine("repe scasb");
                        if (next == 0xAF) return MakeLine("repe scasd");
                        _pos--;
                        return MakeLine("rep");
                    }
                case 0xF2: // repne prefix
                    {
                        byte next = ReadByte();
                        if (next == 0xAE) return MakeLine("repne scasb");
                        _pos--;
                        return MakeLine("repne");
                    }
                case 0xA4: return MakeLine("movsb");
                case 0xA5: return MakeLine("movsd");
                case 0xAA: return MakeLine("stosb");
                case 0xAB: return MakeLine("stosd");
                case 0xAC: return MakeLine("lodsb");
                case 0xAD: return MakeLine("lodsd");
            }

            // ── push/pop reg32 ──
            if (op >= 0x50 && op <= 0x57)
                return MakeLine("push", Reg32[op - 0x50]);
            if (op >= 0x58 && op <= 0x5F)
                return MakeLine("pop", Reg32[op - 0x58]);

            // ── inc/dec reg32 ──
            if (op >= 0x40 && op <= 0x47)
                return MakeLine("inc", Reg32[op - 0x40]);
            if (op >= 0x48 && op <= 0x4F)
                return MakeLine("dec", Reg32[op - 0x48]);

            // ── mov reg8, imm8 ──
            if (op >= 0xB0 && op <= 0xB7)
            {
                byte imm = ReadByte();
                return MakeLine("mov", $"{Reg8[op - 0xB0]}, 0x{imm:X2}");
            }

            // ── mov reg32, imm32 ──
            if (op >= 0xB8 && op <= 0xBF)
            {
                uint imm = ReadDword();
                return MakeLine("mov", $"{Reg32[op - 0xB8]}, 0x{imm:X}");
            }

            // ── ALU r/m32, r32 and r32, r/m32 ──
            // 00-05: add, 08-0D: or, 10-15: adc, 18-1D: sbb
            // 20-25: and, 28-2D: sub, 30-35: xor, 38-3D: cmp
            if ((op & 0xC0) == 0 && (op & 0x06) <= 0x03)
            {
                int aluIdx = (op >> 3) & 7;
                int dir = op & 0x02;  // 0=rm,r  2=r,rm
                int w = op & 0x01;    // 0=byte  1=dword
                string size = w == 0 ? "byte" : "dword";

                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);

                string regStr = w == 0 ? Reg8[reg] : Reg32[reg];
                string rmStr;
                int savedPos = _pos;

                // Need to decode rm before choosing operand order
                rmStr = DecodeRM32(modrm, size);

                if (dir == 0)
                    return MakeLine(AluOps[aluIdx], $"{rmStr}, {regStr}");
                else
                    return MakeLine(AluOps[aluIdx], $"{regStr}, {rmStr}");
            }

            // ── ALU eax, imm32 (short form) ──
            // 05: add eax,imm32  0D: or  15: adc  1D: sbb  25: and  2D: sub  35: xor  3D: cmp
            if ((op & 0xC7) == 0x05)
            {
                int aluIdx = (op >> 3) & 7;
                uint imm = ReadDword();
                return MakeLine(AluOps[aluIdx], $"eax, 0x{imm:X}");
            }

            // ── ALU al, imm8 (short form) ──
            if ((op & 0xC7) == 0x04)
            {
                int aluIdx = (op >> 3) & 7;
                byte imm = ReadByte();
                return MakeLine(AluOps[aluIdx], $"al, 0x{imm:X2}");
            }

            // ── push imm32 ──
            if (op == 0x68)
            {
                uint imm = ReadDword();
                return MakeLine("push", $"0x{imm:X}");
            }

            // ── push imm8 ──
            if (op == 0x6A)
            {
                int imm = ReadSByte();
                return MakeLine("push", $"0x{imm:X}");
            }

            // ── 0x80: ALU r/m8, imm8 ──
            if (op == 0x80 || op == 0x82)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "byte");
                byte imm = ReadByte();
                return MakeLine(AluOps[reg], $"{rmStr}, 0x{imm:X2}");
            }

            // ── 0x81: ALU r/m32, imm32 ──
            if (op == 0x81)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                uint imm = ReadDword();
                return MakeLine(AluOps[reg], $"{rmStr}, 0x{imm:X}");
            }

            // ── 0x83: ALU r/m32, imm8 (sign-extended) ──
            if (op == 0x83)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                int imm = ReadSByte();
                if (imm < 0)
                    return MakeLine(AluOps[reg], $"{rmStr}, -{-imm}");
                return MakeLine(AluOps[reg], $"{rmStr}, {imm}");
            }

            // ── test r/m8, r8 ──
            if (op == 0x84)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "byte");
                return MakeLine("test", $"{rmStr}, {Reg8[reg]}");
            }

            // ── test r/m32, r32 ──
            if (op == 0x85)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                return MakeLine("test", $"{rmStr}, {Reg32[reg]}");
            }

            // ── xchg r/m32, r32 ──
            if (op == 0x87)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                return MakeLine("xchg", $"{rmStr}, {Reg32[reg]}");
            }

            // ── mov r/m8, r8 ──
            if (op == 0x88)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "byte");
                return MakeLine("mov", $"{rmStr}, {Reg8[reg]}");
            }

            // ── mov r/m32, r32 ──
            if (op == 0x89)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                return MakeLine("mov", $"{rmStr}, {Reg32[reg]}");
            }

            // ── mov r8, r/m8 ──
            if (op == 0x8A)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "byte");
                return MakeLine("mov", $"{Reg8[reg]}, {rmStr}");
            }

            // ── mov r32, r/m32 ──
            if (op == 0x8B)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                return MakeLine("mov", $"{Reg32[reg]}, {rmStr}");
            }

            // ── lea r32, m ──
            if (op == 0x8D)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                // LEA shows the address calculation, not memory load
                return MakeLine("lea", $"{Reg32[reg]}, {rmStr}");
            }

            // ── mov r/m32, seg ──
            if (op == 0x8C)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "word");
                return MakeLine("mov", $"{rmStr}, {(reg < 6 ? SegRegs[reg] : "??")}");
            }

            // ── mov seg, r/m16 ──
            if (op == 0x8E)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "word");
                return MakeLine("mov", $"{(reg < 6 ? SegRegs[reg] : "??")}, {rmStr}");
            }

            // ── test al, imm8 ──
            if (op == 0xA8)
            {
                byte imm = ReadByte();
                return MakeLine("test", $"al, 0x{imm:X2}");
            }

            // ── test eax, imm32 ──
            if (op == 0xA9)
            {
                uint imm = ReadDword();
                return MakeLine("test", $"eax, 0x{imm:X}");
            }

            // ── mov al/eax, moffs ──
            if (op == 0xA0) { uint addr = ReadDword(); return MakeLine("mov", $"al, byte [0x{addr:X}]"); }
            if (op == 0xA1) { uint addr = ReadDword(); return MakeLine("mov", $"eax, dword [0x{addr:X}]"); }
            if (op == 0xA2) { uint addr = ReadDword(); return MakeLine("mov", $"byte [0x{addr:X}], al"); }
            if (op == 0xA3) { uint addr = ReadDword(); return MakeLine("mov", $"dword [0x{addr:X}], eax"); }

            // ── mov r/m32, imm32 ──
            if (op == 0xC7)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                uint imm = ReadDword();
                return MakeLine("mov", $"{rmStr}, 0x{imm:X}");
            }

            // ── mov r/m8, imm8 ──
            if (op == 0xC6)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "byte");
                byte imm = ReadByte();
                return MakeLine("mov", $"{rmStr}, 0x{imm:X2}");
            }

            // ── shift r/m32 by imm8 ──
            if (op == 0xC1)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                byte imm = ReadByte();
                return MakeLine(ShiftOps[reg], $"{rmStr}, {imm}");
            }

            // ── shift r/m8 by imm8 ──
            if (op == 0xC0)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "byte");
                byte imm = ReadByte();
                return MakeLine(ShiftOps[reg], $"{rmStr}, {imm}");
            }

            // ── shift r/m32 by 1 ──
            if (op == 0xD1)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                return MakeLine(ShiftOps[reg], $"{rmStr}, 1");
            }

            // ── shift r/m32 by cl ──
            if (op == 0xD3)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                return MakeLine(ShiftOps[reg], $"{rmStr}, cl");
            }

            // ── shift r/m8 by cl ──
            if (op == 0xD2)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "byte");
                return MakeLine(ShiftOps[reg], $"{rmStr}, cl");
            }

            // ── call rel32 ──
            if (op == 0xE8)
            {
                int rel = ReadSDword();
                int target = _baseAddr + _pos + rel;
                return MakeLine("call", $"0x{(uint)target:X8}");
            }

            // ── jmp rel32 ──
            if (op == 0xE9)
            {
                int rel = ReadSDword();
                int target = _baseAddr + _pos + rel;
                return MakeLine("jmp", $"0x{(uint)target:X8}");
            }

            // ── jmp rel8 ──
            if (op == 0xEB)
            {
                int rel = ReadSByte();
                int target = _baseAddr + _pos + rel;
                return MakeLine("jmp short", $"0x{(uint)target:X8}");
            }

            // ── Jcc rel8 (short conditional jumps) ──
            if (op >= 0x70 && op <= 0x7F)
            {
                int rel = ReadSByte();
                int target = _baseAddr + _pos + rel;
                return MakeLine($"j{CondCodes[op - 0x70]}", $"0x{(uint)target:X8}");
            }

            // ── int imm8 ──
            if (op == 0xCD)
            {
                byte vec = ReadByte();
                return MakeLine("int", $"0x{vec:X2}");
            }

            // ── in/out ──
            if (op == 0xE4) { byte port = ReadByte(); return MakeLine("in", $"al, 0x{port:X2}"); }
            if (op == 0xE5) { byte port = ReadByte(); return MakeLine("in", $"eax, 0x{port:X2}"); }
            if (op == 0xE6) { byte port = ReadByte(); return MakeLine("out", $"0x{port:X2}, al"); }
            if (op == 0xE7) { byte port = ReadByte(); return MakeLine("out", $"0x{port:X2}, eax"); }
            if (op == 0xEC) return MakeLine("in", "al, dx");
            if (op == 0xED) return MakeLine("in", "eax, dx");
            if (op == 0xEE) return MakeLine("out", "dx, al");
            if (op == 0xEF) return MakeLine("out", "dx, eax");

            // ── ret imm16 ──
            if (op == 0xC2)
            {
                ushort imm = ReadWord();
                return MakeLine("ret", $"0x{imm:X}");
            }

            // ── F6: unary r/m8 (test/not/neg/mul/imul/div/idiv) ──
            if (op == 0xF6)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "byte");
                switch (reg)
                {
                    case 0: byte imm = ReadByte(); return MakeLine("test", $"{rmStr}, 0x{imm:X2}");
                    case 2: return MakeLine("not", rmStr);
                    case 3: return MakeLine("neg", rmStr);
                    case 4: return MakeLine("mul", rmStr);
                    case 5: return MakeLine("imul", rmStr);
                    case 6: return MakeLine("div", rmStr);
                    case 7: return MakeLine("idiv", rmStr);
                }
            }

            // ── F7: unary r/m32 (test/not/neg/mul/imul/div/idiv) ──
            if (op == 0xF7)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                switch (reg)
                {
                    case 0: uint imm = ReadDword(); return MakeLine("test", $"{rmStr}, 0x{imm:X}");
                    case 2: return MakeLine("not", rmStr);
                    case 3: return MakeLine("neg", rmStr);
                    case 4: return MakeLine("mul", rmStr);
                    case 5: return MakeLine("imul", rmStr);
                    case 6: return MakeLine("div", rmStr);
                    case 7: return MakeLine("idiv", rmStr);
                }
            }

            // ── FE: inc/dec r/m8 ──
            if (op == 0xFE)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm, "byte");
                return MakeLine(reg == 0 ? "inc" : "dec", rmStr);
            }

            // ── FF: inc/dec/call/jmp/push r/m32 ──
            if (op == 0xFF)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                switch (reg)
                {
                    case 0: return MakeLine("inc", rmStr);
                    case 1: return MakeLine("dec", rmStr);
                    case 2: return MakeLine("call", rmStr);
                    case 4: return MakeLine("jmp", rmStr);
                    case 6: return MakeLine("push", rmStr);
                }
            }

            // ── imul r32, r/m32, imm32 ──
            if (op == 0x69)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                int imm = ReadSDword();
                return MakeLine("imul", $"{Reg32[reg]}, {rmStr}, 0x{imm:X}");
            }

            // ── imul r32, r/m32, imm8 ──
            if (op == 0x6B)
            {
                byte modrm = ReadByte();
                var (mod, reg, rm) = DecodeModRM(modrm);
                string rmStr = DecodeRM32(modrm);
                int imm = ReadSByte();
                return MakeLine("imul", $"{Reg32[reg]}, {rmStr}, {imm}");
            }

            // ── loop/loopz/loopnz ──
            if (op == 0xE0) { int rel = ReadSByte(); return MakeLine("loopne", $"0x{(uint)(_baseAddr + _pos + rel):X8}"); }
            if (op == 0xE1) { int rel = ReadSByte(); return MakeLine("loope", $"0x{(uint)(_baseAddr + _pos + rel):X8}"); }
            if (op == 0xE2) { int rel = ReadSByte(); return MakeLine("loop", $"0x{(uint)(_baseAddr + _pos + rel):X8}"); }

            // ── 0x0F two-byte opcodes ──
            if (op == 0x0F)
            {
                byte op2 = ReadByte();

                // ── Jcc rel32 (near conditional jumps) ──
                if (op2 >= 0x80 && op2 <= 0x8F)
                {
                    int rel = ReadSDword();
                    int target = _baseAddr + _pos + rel;
                    return MakeLine($"j{CondCodes[op2 - 0x80]}", $"0x{(uint)target:X8}");
                }

                // ── SETcc r/m8 ──
                if (op2 >= 0x90 && op2 <= 0x9F)
                {
                    byte modrm = ReadByte();
                    var (mod, reg, rm) = DecodeModRM(modrm);
                    string rmStr = DecodeRM32(modrm, "byte");
                    return MakeLine($"set{CondCodes[op2 - 0x90]}", rmStr);
                }

                // ── movzx r32, r/m8 ──
                if (op2 == 0xB6)
                {
                    byte modrm = ReadByte();
                    var (mod, reg, rm) = DecodeModRM(modrm);
                    string rmStr = DecodeRM32(modrm, "byte");
                    return MakeLine("movzx", $"{Reg32[reg]}, {rmStr}");
                }

                // ── movzx r32, r/m16 ──
                if (op2 == 0xB7)
                {
                    byte modrm = ReadByte();
                    var (mod, reg, rm) = DecodeModRM(modrm);
                    string rmStr = DecodeRM32(modrm, "word");
                    return MakeLine("movzx", $"{Reg32[reg]}, {rmStr}");
                }

                // ── movsx r32, r/m8 ──
                if (op2 == 0xBE)
                {
                    byte modrm = ReadByte();
                    var (mod, reg, rm) = DecodeModRM(modrm);
                    string rmStr = DecodeRM32(modrm, "byte");
                    return MakeLine("movsx", $"{Reg32[reg]}, {rmStr}");
                }

                // ── movsx r32, r/m16 ──
                if (op2 == 0xBF)
                {
                    byte modrm = ReadByte();
                    var (mod, reg, rm) = DecodeModRM(modrm);
                    string rmStr = DecodeRM32(modrm, "word");
                    return MakeLine("movsx", $"{Reg32[reg]}, {rmStr}");
                }

                // ── imul r32, r/m32 ──
                if (op2 == 0xAF)
                {
                    byte modrm = ReadByte();
                    var (mod, reg, rm) = DecodeModRM(modrm);
                    string rmStr = DecodeRM32(modrm);
                    return MakeLine("imul", $"{Reg32[reg]}, {rmStr}");
                }

                // ── bsf/bsr ──
                if (op2 == 0xBC) { byte modrm = ReadByte(); var (m, r, rm2) = DecodeModRM(modrm); return MakeLine("bsf", $"{Reg32[r]}, {DecodeRM32(modrm)}"); }
                if (op2 == 0xBD) { byte modrm = ReadByte(); var (m, r, rm2) = DecodeModRM(modrm); return MakeLine("bsr", $"{Reg32[r]}, {DecodeRM32(modrm)}"); }

                // ── cmov ──
                if (op2 >= 0x40 && op2 <= 0x4F)
                {
                    byte modrm = ReadByte();
                    var (mod, reg, rm) = DecodeModRM(modrm);
                    string rmStr = DecodeRM32(modrm);
                    return MakeLine($"cmov{CondCodes[op2 - 0x40]}", $"{Reg32[reg]}, {rmStr}");
                }

                // ── rdtsc, cpuid ──
                if (op2 == 0x31) return MakeLine("rdtsc");
                if (op2 == 0xA2) return MakeLine("cpuid");

                // ── lgdt, lidt, sgdt, sidt, invlpg ──
                if (op2 == 0x01)
                {
                    byte modrm = ReadByte();
                    var (mod, reg, rm) = DecodeModRM(modrm);
                    string rmStr = DecodeRM32(modrm);
                    switch (reg)
                    {
                        case 0: return MakeLine("sgdt", rmStr);
                        case 1: return MakeLine("sidt", rmStr);
                        case 2: return MakeLine("lgdt", rmStr);
                        case 3: return MakeLine("lidt", rmStr);
                        case 7: return MakeLine("invlpg", rmStr);
                    }
                }

                // ── ltr, str, lldt ──
                if (op2 == 0x00)
                {
                    byte modrm = ReadByte();
                    var (mod, reg, rm) = DecodeModRM(modrm);
                    string rmStr = DecodeRM32(modrm, "word");
                    switch (reg)
                    {
                        case 0: return MakeLine("sldt", rmStr);
                        case 1: return MakeLine("str", rmStr);
                        case 2: return MakeLine("lldt", rmStr);
                        case 3: return MakeLine("ltr", rmStr);
                    }
                }

                // ── wrmsr/rdmsr ──
                if (op2 == 0x30) return MakeLine("wrmsr");
                if (op2 == 0x32) return MakeLine("rdmsr");

                // ── mov CRn, r32 / mov r32, CRn ──
                if (op2 == 0x20) { byte modrm = ReadByte(); var (m, r, rm2) = DecodeModRM(modrm); return MakeLine("mov", $"{Reg32[rm2]}, cr{r}"); }
                if (op2 == 0x22) { byte modrm = ReadByte(); var (m, r, rm2) = DecodeModRM(modrm); return MakeLine("mov", $"cr{r}, {Reg32[rm2]}"); }

                // Unknown 0F prefix
                return MakeLine("db", $"0x0F, 0x{op2:X2}");
            }

            // ── Segment override prefixes — re-read with context ──
            if (op == 0x26 || op == 0x2E || op == 0x36 || op == 0x3E || op == 0x64 || op == 0x65)
            {
                // Just note the prefix and decode next instruction
                string seg = op == 0x26 ? "es" : op == 0x2E ? "cs" : op == 0x36 ? "ss" :
                             op == 0x3E ? "ds" : op == 0x64 ? "fs" : "gs";
                // For simplicity, just show the prefix
                return MakeLine(seg + ":", "");
            }

            // ── Operand size prefix ──
            if (op == 0x66)
            {
                // Next instruction uses 16-bit operands — peek and decode
                byte next = ReadByte();
                if (next >= 0xB8 && next <= 0xBF)
                {
                    ushort imm = ReadWord();
                    return MakeLine("mov", $"{Reg16[next - 0xB8]}, 0x{imm:X4}");
                }
                if (next == 0x89) { byte modrm = ReadByte(); var (m, r, rm2) = DecodeModRM(modrm); string rmS = DecodeRM32(modrm, "word"); return MakeLine("mov", $"{rmS}, {Reg16[r]}"); }
                if (next == 0x8B) { byte modrm = ReadByte(); var (m, r, rm2) = DecodeModRM(modrm); string rmS = DecodeRM32(modrm, "word"); return MakeLine("mov", $"{Reg16[r]}, {rmS}"); }
                if (next >= 0x50 && next <= 0x57) return MakeLine("push", Reg16[next - 0x50]);
                if (next >= 0x58 && next <= 0x5F) return MakeLine("pop", Reg16[next - 0x58]);
                if (next == 0x90) return MakeLine("nop");
                // Fallback
                _pos--;
                return MakeLine("db", "0x66");
            }

            // ── mov cr0/cr3 special (via 0F) already handled above ──

            // Unknown opcode — emit as db
            return MakeLine("db", $"0x{op:X2}");
        }
    }

    public class DisasmLine
    {
        public int Address { get; set; }
        public byte[] Bytes { get; set; }
        public string Mnemonic { get; set; }
        public string Operands { get; set; }

        public string Format()
        {
            string bytesHex = Bytes != null ? string.Join(" ", Array.ConvertAll(Bytes, b => b.ToString("X2"))) : "";
            string addr = $"0x{Address:X8}";
            return $"{addr}  {bytesHex,-24} {Mnemonic,-10} {Operands}";
        }
    }
}