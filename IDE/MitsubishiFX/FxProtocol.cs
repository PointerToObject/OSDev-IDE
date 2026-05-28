// FxProtocol.cs
//
// Wire-level MELSEC FX programming protocol — the same protocol GX Developer
// and GX Works 2 use to download programs over the FX programming port.
// Modern clones (like the YKHMI Mini-15MT-DC with its USB-C port) bridge the
// physical layer to a virtual COM port, but speak the same protocol on top.
//
// Frame format (Computer Link / "FX dedicated protocol"):
//
//     ENQ <CMD> <ADDRESS:4-hex> <COUNT:2-hex> [<DATA-hex>] <SUM:2-hex> ETX
//                                                          └─ checksum
//                                                             of CMD..DATA
//
// All address/count/data fields are ASCII-hex (uppercase), so a single byte
// in PLC memory takes 2 ASCII chars on the wire.  This is per Mitsubishi's
// "FX Communication User's Manual" (jy997d16901), Format 1.
//
// Responses:
//     ACK (0x06)           → success (for write/run/stop)
//     STX <data> ETX <SUM> → success with payload (for read)
//     NAK (0x15) <code:2>  → failure (code = error number)
//
// The FxDownloader on top of this issues sequences of frames to STOP the
// CPU, write program memory, then RUN.

using System;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace IDE.MitsubishiFX
{
    public sealed class FxProtocolException : Exception
    {
        public FxProtocolException(string msg) : base(msg) { }
        public FxProtocolException(string msg, Exception inner) : base(msg, inner) { }
    }

    /// <summary>
    /// Low-level frame builder + wire I/O.  No knowledge of *what* the bytes
    /// being read/written represent — the FxDownloader layer maps semantic
    /// operations like "write program memory" into address ranges.
    /// </summary>
    public sealed class FxProtocol : IDisposable
    {
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const byte ENQ = 0x05;
        private const byte ACK = 0x06;
        private const byte NAK = 0x15;

        // Documented FX programming-port command bytes (ASCII).
        // Source: Mitsubishi FX Communication User's Manual.
        public const byte CMD_DEVICE_READ   = (byte)'0';   // "0" — batch read
        public const byte CMD_DEVICE_WRITE  = (byte)'1';   // "1" — batch write
        public const byte CMD_FORCE_ON      = (byte)'7';   // "7" — force bit ON
        public const byte CMD_FORCE_OFF     = (byte)'8';   // "8" — force bit OFF
        public const byte CMD_PC_RUN        = (byte)'A';   // "A" — remote RUN
        public const byte CMD_PC_STOP       = (byte)'B';   // "B" — remote STOP

        private readonly SerialPort _port;
        private readonly Action<string>? _log;

        public FxProtocol(string portName, int baud, Parity parity, int dataBits, StopBits stop,
                          int readTimeoutMs = 2000, int writeTimeoutMs = 2000,
                          Action<string>? log = null)
        {
            _log = log;
            _port = new SerialPort(portName, baud, parity, dataBits, stop)
            {
                ReadTimeout  = readTimeoutMs,
                WriteTimeout = writeTimeoutMs,
                Handshake    = System.IO.Ports.Handshake.None,
                DtrEnable    = true,   // some USB bridges need DTR to wake the FT/CH chip
                RtsEnable    = true,
            };
        }

        // FX1S programming-port factory — uses the native settings of a
        // genuine FX1S (9600/7E1).  Many clones use the same; some USB-C
        // bridges expose the protocol at 9600/8N1 instead.  Both shapes are
        // selectable from the connection dialog.
        public static FxProtocol OpenFxNative(string portName, Action<string>? log = null) =>
            new(portName, 9600, Parity.Even, 7, StopBits.One, log: log);

        public static FxProtocol Open8N1(string portName, int baud = 9600, Action<string>? log = null) =>
            new(portName, baud, Parity.None, 8, StopBits.One, log: log);

        public void Connect()
        {
            try { _port.Open(); }
            catch (Exception ex)
            {
                throw new FxProtocolException($"can't open {_port.PortName}: {ex.Message}", ex);
            }
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
            _log?.Invoke($"[fx] opened {_port.PortName} @ {_port.BaudRate} {_port.Parity} {_port.DataBits}{(_port.StopBits == StopBits.One ? "1" : "?")}\n");
        }

        /// <summary>
        /// Send a bare ENQ and wait for ACK.  This is the FX programming-port
        /// "are you there?" handshake — every protocol session starts with it.
        /// Returns true on ACK, false on NAK or timeout.
        ///
        /// Important diagnostic: if Handshake fails we know the *physical*
        /// layer is wrong (bad serial profile, dead PLC, wrong cable). If it
        /// succeeds but a real frame later NAKs, the framing/checksum is the
        /// culprit, not the wire.
        /// </summary>
        public bool ProbeEnq(int retries = 3)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    _port.DiscardInBuffer();
                    _log?.Invoke("[probe] -> <ENQ>\n");
                    _port.Write(new[] { ENQ }, 0, 1);
                    int reply = _port.ReadByte();
                    string tag = reply switch
                    {
                        ACK => " (ACK — handshake OK)",
                        NAK => " (NAK)",
                        _   => "",
                    };
                    _log?.Invoke($"[probe] <- 0x{reply:X2}{tag}\n");
                    if (reply == ACK) return true;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[probe] retry {i + 1}: {ex.Message}\n");
                }
                Thread.Sleep(100);
            }
            return false;
        }

        public void Dispose()
        {
            try { if (_port.IsOpen) _port.Close(); }
            catch { /* nothing useful to do here */ }
            _port.Dispose();
        }

        // ─────────────────────────────────────────────────────────────────
        // FRAME BUILD / SEND / RECEIVE
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Send a "command + address + count [+ data]" frame and read the
        /// PLC's reply.  Returns the data bytes from a STX..ETX response, or
        /// an empty array for ACK-only replies.
        /// </summary>
        public byte[] Transact(byte cmd, int address, int count, byte[]? payload = null)
        {
            // Build the inner part (everything between ENQ and ETX) as ASCII.
            var inner = new StringBuilder(8 + (payload?.Length ?? 0) * 2);
            inner.Append((char)cmd);
            inner.Append(address.ToString("X4"));   // 4 hex chars
            inner.Append(count.ToString("X2"));     // 2 hex chars

            if (payload is { Length: > 0 })
            {
                foreach (byte b in payload)
                    inner.Append(b.ToString("X2"));
            }

            byte sum = ComputeSum(inner);

            // FX programming-port direct protocol frame:
            //   ENQ + cmd + addr(4 hex) + count(2 hex) + [data hex] + checksum(2 hex)
            //
            // No ETX in the request — that only shows up in the *response*
            // when the PLC sends back a payload (STX..ETX..sum). The earlier
            // implementation included an ETX here, which matched the
            // Computer-Link Format 1 protocol used by RS-232 expansion
            // modules but NOT the programming-port direct protocol the
            // YKHMI USB-C bridge speaks.
            var frame = new byte[1 + inner.Length + 2];
            int p = 0;
            frame[p++] = ENQ;
            for (int i = 0; i < inner.Length; i++) frame[p++] = (byte)inner[i];
            string sumHex = sum.ToString("X2");
            frame[p++] = (byte)sumHex[0];
            frame[p++] = (byte)sumHex[1];

            _log?.Invoke($"[tx] {Ascii(frame)}\n");
            _port.Write(frame, 0, frame.Length);

            return ReadReply();
        }

        private byte[] ReadReply()
        {
            int first = ReadByte();
            if (first == ACK) { _log?.Invoke("[rx] <ACK>\n"); return Array.Empty<byte>(); }
            if (first == NAK)
            {
                // FX programming-port direct protocol sends a bare NAK on
                // error — no follow-up bytes. Our previous implementation
                // tried to read 2 more bytes as an "error code", which would
                // time out and replace the useful "the PLC said no" message
                // with a confusing "timeout" or empty error code string.
                _log?.Invoke("[rx] <NAK>\n");
                throw new FxProtocolException(
                    "PLC replied NAK — frame structure or checksum was rejected. " +
                    "Check the [tx] frame above against the FX programming protocol.");
            }
            if (first != STX)
                throw new FxProtocolException($"unexpected first reply byte 0x{first:X2}");

            // Read until ETX, then 2 checksum bytes.
            var buf = new System.Collections.Generic.List<byte>();
            while (true)
            {
                int b = ReadByte();
                if (b == ETX) break;
                buf.Add((byte)b);
            }
            int s1 = ReadByte();
            int s2 = ReadByte();
            _log?.Invoke($"[rx] STX..ETX ({buf.Count} bytes) sum={ (char)s1 }{ (char)s2 }\n");

            // Convert the ASCII-hex payload back to bytes.  Each PLC byte is
            // 2 ASCII chars on the wire.
            if ((buf.Count & 1) != 0)
                throw new FxProtocolException("odd-length payload from PLC");
            var data = new byte[buf.Count / 2];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)((HexNibble(buf[i * 2]) << 4) | HexNibble(buf[i * 2 + 1]));
            return data;
        }

        private int ReadByte()
        {
            try { return _port.ReadByte(); }
            catch (TimeoutException)
            {
                throw new FxProtocolException("PLC timed out — wrong baud/parity, wrong COM port, or PLC not in programmable mode");
            }
        }

        private static byte ComputeSum(StringBuilder inner)
        {
            // Sum modulo 256 of the ASCII characters between (but not
            // including) ENQ and ETX.  We add ETX in at the call site.
            int sum = 0;
            for (int i = 0; i < inner.Length; i++) sum += inner[i];
            return (byte)(sum & 0xFF);
        }

        private static int HexNibble(byte ascii)
        {
            if (ascii >= '0' && ascii <= '9') return ascii - '0';
            if (ascii >= 'A' && ascii <= 'F') return ascii - 'A' + 10;
            if (ascii >= 'a' && ascii <= 'f') return ascii - 'a' + 10;
            throw new FxProtocolException($"bad hex char 0x{ascii:X2}");
        }

        private static string Ascii(byte[] bytes)
        {
            // Render a frame for the log — printable chars as-is, control
            // bytes shown as <NAME>.
            var sb = new StringBuilder();
            foreach (byte b in bytes)
            {
                sb.Append(b switch
                {
                    STX => "<STX>",
                    ETX => "<ETX>",
                    ENQ => "<ENQ>",
                    ACK => "<ACK>",
                    NAK => "<NAK>",
                    >= 0x20 and < 0x7F => ((char)b).ToString(),
                    _ => $"<{b:X2}>",
                });
            }
            return sb.ToString();
        }
    }
}
