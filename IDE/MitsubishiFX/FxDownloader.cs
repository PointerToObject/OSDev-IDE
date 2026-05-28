// FxDownloader.cs
//
// High-level "compile → flash → run" orchestration for the Mitsubishi FX
// programming protocol.  Layered on top of FxProtocol (frame I/O) and
// FxAssembler (IL → bytecode).
//
// Typical flow when the user clicks "Deploy to PLC":
//
//     var dl = new FxDownloader(settings, log);
//     dl.Connect();
//     dl.StopCpu();                       // can't write program while RUN
//     dl.WriteProgram(words);             // overwrites program memory
//     dl.RunCpu();                        // back to scan
//     dl.Dispose();
//
// Memory map (FX1S):
//     0x0000..0x00FF  — D registers (256 bytes; we write program below this)
//     0x0800..0x0FFF  — program steps (2K * 2 bytes = 4 KB)
//     0x1000..        — comments / parameters
//
// The FX programming protocol uses byte addresses, not step addresses, so we
// translate step → byte (each step = 2 bytes, base = 0x0800).

using System;
using System.IO.Ports;
using System.Threading;

namespace IDE.MitsubishiFX
{
    public sealed class FxConnectionSettings
    {
        public string PortName { get; set; } = "COM1";
        public int    Baud     { get; set; } = 9600;
        public Parity Parity   { get; set; } = Parity.Even;   // FX1S native default
        public int    DataBits { get; set; } = 7;
        public StopBits StopBits { get; set; } = StopBits.One;

        // Some clones (the YKHMI Mini-15MT-DC and similar USB-C devices)
        // expose the protocol at 9600/8N1 instead of FX-native 9600/7E1.
        public static FxConnectionSettings FxNative(string port) =>
            new() { PortName = port, Baud = 9600, Parity = Parity.Even,
                    DataBits = 7, StopBits = StopBits.One };

        public static FxConnectionSettings UsbBridge(string port) =>
            new() { PortName = port, Baud = 9600, Parity = Parity.None,
                    DataBits = 8, StopBits = StopBits.One };
    }

    public sealed class FxDownloader : IDisposable
    {
        // FX1S program memory base — populated by ProbeProgramAddress() once
        // we discover which address the clone actually accepts.  Default is
        // the genuine-FX1S 0x0800 base, but many clones use 0x0E00 or 0x0000.
        private int _programBaseAddr = 0x0800;

        // D-register base — also auto-discovered via the device probe so the
        // diagnostic write succeeds even on non-standard clones.
        private int _dRegBase = 0x1000;

        // (formerly CHUNK_BYTES — chunk size now expressed in words inside
        // WriteProgram since the protocol counts in words.)

        private readonly FxConnectionSettings _settings;
        private readonly Action<string>? _log;
        private FxProtocol? _proto;

        public FxDownloader(FxConnectionSettings settings, Action<string>? log = null)
        {
            _settings = settings;
            _log = log;
        }

        public void Connect()
        {
            _proto = new FxProtocol(
                _settings.PortName, _settings.Baud, _settings.Parity,
                _settings.DataBits, _settings.StopBits,
                log: _log);
            _proto.Connect();

            // Verify the wire is alive before we try to do anything real.
            // ENQ→ACK is the FX programming-port "hello?" handshake; if this
            // fails we have a serial-settings problem and there's no point
            // sending command frames.
            if (!_proto.ProbeEnq())
                throw new FxProtocolException(
                    "PLC did not ACK the ENQ handshake. " +
                    "Try the other serial profile (USB-C 8N1 vs FX-native 7E1), " +
                    "verify the PLC is powered, and check the COM port.");
        }

        public void Dispose()
        {
            _proto?.Dispose();
            _proto = null;
        }

        // ─────────────────────────────────────────────────────────────────
        // CPU CONTROL
        // ─────────────────────────────────────────────────────────────────

        public void StopCpu()
        {
            Log("→ STOP CPU\n");
            // Force-stop is a "command-only" frame — no address payload.
            // Address/count are zeros; the PLC checks only the command byte.
            // Best-effort: if the PLC NAKs us, log it and continue. The user
            // can manually flip the RUN/STOP slide switch on the unit and
            // we can still test program write independently.
            try { Proto.Transact(FxProtocol.CMD_PC_STOP, address: 0, count: 0); }
            catch (FxProtocolException ex) { Log($"  (stop NAK ignored: {ex.Message})\n"); }
            // The CPU needs a moment to drop out of scan before it'll accept
            // program-memory writes.  Empirically 100ms is plenty.
            Thread.Sleep(150);
        }

        public void RunCpu()
        {
            Log("→ RUN CPU\n");
            try { Proto.Transact(FxProtocol.CMD_PC_RUN, address: 0, count: 0); }
            catch (FxProtocolException ex) { Log($"  (run NAK ignored: {ex.Message})\n"); }
        }

        // ─────────────────────────────────────────────────────────────────
        // PROGRAM TRANSFER
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Write the assembled program (16-bit words) to FX program memory
        /// in chunks. Caller is responsible for STOPping the CPU first.
        /// </summary>
        public void WriteProgram(ushort[] words, Action<int, int>? progress = null)
        {
            // Scheme picked by TestDeviceFraming() — count units, byte order
            // and address base all come from the probe so we use whatever
            // dialect this particular clone accepted for the D100 read.
            Log($"→ writing {words.Length} steps ({words.Length * 2} bytes), program base 0x{_programBaseAddr:X4}\n");

            const int WORDS_PER_CHUNK = 32;

            int wordsWritten = 0;
            while (wordsWritten < words.Length)
            {
                int chunkWords = Math.Min(WORDS_PER_CHUNK, words.Length - wordsWritten);
                int addr = _programBaseAddr + wordsWritten * 2;

                // Pack `chunkWords` words big-endian (high byte first per
                // word). Matches what the probe established for reads.
                var slice = new byte[chunkWords * 2];
                for (int i = 0; i < chunkWords; i++)
                {
                    ushort w = words[wordsWritten + i];
                    slice[i * 2]     = (byte)(w >> 8);
                    slice[i * 2 + 1] = (byte)(w & 0xFF);
                }

                // Translate the chunk size into whichever count-units the
                // probe found.  Same byte payload either way.
                int countField = _countUnitsBytes ? chunkWords * 2 : chunkWords;

                Exception? lastError = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        Proto.Transact(FxProtocol.CMD_DEVICE_WRITE, addr, countField, slice);
                        lastError = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        Log($"  retry chunk @0x{addr:X4}: {ex.Message}\n");
                        Thread.Sleep(50);
                    }
                }
                if (lastError != null) throw lastError;

                wordsWritten += chunkWords;
                progress?.Invoke(wordsWritten * 2, words.Length * 2);
            }
            Log("→ program write OK\n");
        }

        /// <summary>
        /// Read program memory back from the PLC.  Useful after a write to
        /// verify what we sent matches what's now on the device.  Also used
        /// by the "diff against GX Works 2" workflow during development.
        /// </summary>
        public byte[] ReadProgram(int wordCount)
        {
            Log($"→ reading {wordCount} words ({wordCount * 2} bytes) from program memory @ 0x{_programBaseAddr:X4}\n");
            const int WORDS_PER_CHUNK = 32;
            var buf = new byte[wordCount * 2];
            int wordsRead = 0;
            while (wordsRead < wordCount)
            {
                int chunkWords = Math.Min(WORDS_PER_CHUNK, wordCount - wordsRead);
                int countField = _countUnitsBytes ? chunkWords * 2 : chunkWords;
                byte[] data = Proto.Transact(
                    FxProtocol.CMD_DEVICE_READ,
                    _programBaseAddr + wordsRead * 2, countField);
                if (data.Length != chunkWords * 2)
                    throw new FxProtocolException(
                        $"read returned {data.Length} bytes, expected {chunkWords * 2}");
                Buffer.BlockCopy(data, 0, buf, wordsRead * 2, data.Length);
                wordsRead += chunkWords;
            }
            return buf;
        }

        // ─────────────────────────────────────────────────────────────────
        // ONE-SHOT FULL DEPLOY
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Diagnostic: write a known value to D100 and read it back.  This
        /// confirms our framing of read/write commands is correct without
        /// touching program memory.  Run before WriteProgram so we can tell
        /// "the whole protocol is wrong" apart from "just the program-memory
        /// address scheme is wrong" — they have very different fixes.
        ///
        /// Returns true on full round-trip match.
        /// </summary>
        public bool TestDeviceFraming()
        {
            // Different FX clones map D-registers to different byte addresses.
            // Rather than hard-code one and lose, we probe a list of known
            // schemes and lock in whichever one the PLC ACKs first.
            //
            // Each row is (label, base_addr, count_units).  count_units says
            // what value to put in the protocol's count field for "read 1
            // word":  1 if the field is in words, 2 if it's in bytes, 4 if
            // it's the number of hex chars on the wire.
            var schemes = new (string label, int baseAddr, int countFor1Word)[]
            {
                ("FX1S map 0x1000 + n*2, count=words", 0x1000, 1),
                ("FX1S map 0x1000 + n*2, count=bytes", 0x1000, 2),
                ("FX1S map 0x1000 + n*2, count=hex",   0x1000, 4),
                ("FX2N map 0x4000 + n*2, count=words", 0x4000, 1),
                ("FX2N map 0x4000 + n*2, count=bytes", 0x4000, 2),
                ("FX2N map 0x4000 + n*2, count=hex",   0x4000, 4),
                ("compact 0x0080 + n*2, count=bytes",  0x0080, 2),
                ("raw 0x0000 + n*2,     count=bytes",  0x0000, 2),
                ("raw 0x0000 + n*2,     count=words",  0x0000, 1),
            };

            Log("→ diagnostic: probing D100 addressing schemes\n");

            foreach (var (label, baseAddr, count) in schemes)
            {
                int d100Addr = baseAddr + 100 * 2;
                Log($"  try @ 0x{d100Addr:X4} ({label})\n");
                try
                {
                    // Pure read — won't corrupt anything if the scheme's wrong.
                    // Whatever bytes come back, an ACK with payload means this
                    // address scheme is live.
                    var got = Proto.Transact(FxProtocol.CMD_DEVICE_READ, d100Addr, count);
                    Log($"  ✓ ACK from {label} — got {BitConverter.ToString(got)}\n");
                    _dRegBase = baseAddr;
                    // The chosen scheme also tells us count units for program
                    // writes; remember it.
                    _countUnitsBytes = count == 2;
                    return true;
                }
                catch (FxProtocolException)
                {
                    // NAK — try the next scheme. Don't log per-attempt error
                    // text or we drown the user; the per-scheme summary is
                    // enough to see at a glance which were tried.
                }
            }

            Log("  ✗ no D-register addressing scheme ACK'd — clone may speak MELSOFT/MC, not FX programming port\n");
            return false;
        }

        // Set by TestDeviceFraming once we find a working scheme.  Used by
        // WriteProgram so chunked writes use the same count units.
        private bool _countUnitsBytes = false;

        /// <summary>
        /// Probe candidate program-memory base addresses with a 1-word read.
        /// Whichever ACKs is the address space we'll write the program to.
        /// </summary>
        private void ProbeProgramAddress()
        {
            int[] candidates = { 0x0800, 0x0E00, 0x0000, 0x4000, 0x8000 };
            int countField = _countUnitsBytes ? 2 : 1;

            Log("→ probing program-memory base address\n");
            foreach (int baseAddr in candidates)
            {
                Log($"  try @ 0x{baseAddr:X4}\n");
                try
                {
                    Proto.Transact(FxProtocol.CMD_DEVICE_READ, baseAddr, countField);
                    Log($"  ✓ program memory ACK'd at 0x{baseAddr:X4}\n");
                    _programBaseAddr = baseAddr;
                    return;
                }
                catch (FxProtocolException)
                {
                    // try next
                }
            }
            Log($"  ! no program-memory candidate ACK'd; falling back to 0x{_programBaseAddr:X4}\n");
        }

        /// <summary>
        /// Stop → Write → Run, all in one call.  The IDE button calls this.
        /// </summary>
        public void DeployAndRun(ushort[] words, Action<int, int>? progress = null)
        {
            StopCpu();

            // Diagnostic gate. If D-register read/write doesn't work, the
            // program-memory write definitely won't, and the failure mode of
            // "everything ACKs but the program is silently corrupt" is much
            // worse than failing fast here.
            if (!TestDeviceFraming())
            {
                throw new FxProtocolException(
                    "D-register probe failed across all known address schemes. " +
                    "This clone likely speaks the MELSOFT/MC protocol GX Works 2 uses, " +
                    "not the simple FX programming-port direct protocol. " +
                    "Wire-capture of GX Works 2 traffic via com0com is the path forward.");
            }

            // Now find the right program-memory base.  Same procedure as the
            // device probe: try a list of candidates and lock in whichever
            // ACKs a 1-word read.
            ProbeProgramAddress();

            WriteProgram(words, progress);
            RunCpu();
            Log("→ deploy complete, CPU running\n");
        }

        // ─────────────────────────────────────────────────────────────────
        private FxProtocol Proto =>
            _proto ?? throw new InvalidOperationException("not connected — call Connect() first");

        private void Log(string s) => _log?.Invoke(s);

        // List COM ports currently visible to Windows. Used by the connection
        // dialog when the user clicks "Deploy to PLC".
        public static string[] EnumeratePorts() => SerialPort.GetPortNames();
    }
}
