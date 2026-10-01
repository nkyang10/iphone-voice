using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace DictationBridge
{
    internal static class Native
    {
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;
        public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        // The union must be as large as its widest member or Marshal.SizeOf
        // under-reports INPUT's size and SendInput rejects it with
        // ERROR_INVALID_PARAMETER (87). Mouse is the widest on x64.
        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION U;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("winmm.dll")]
        public static extern uint timeGetTime();
    }

    internal static class Certs
    {
        public const string PfxPassword = "dictation-bridge";

        // Self-signed, and that is the floor rather than a preference. Safari
        // only exposes SpeechRecognition on a secure origin, so plain HTTP cannot
        // be used here at all, and iOS has no "proceed anyway" button the way
        // macOS Safari does. A CA hierarchy was tried and reverted: SslStream on
        // Windows refuses to serve a chain it cannot validate to a root in a
        // local trust store, and installing that root on a user's PC is not
        // something this app should do silently.
        //
        // Known limitation: if the machine's address changes to one the SAN does
        // not already list, the certificate is regenerated and the phone must
        // install the new one.
        public static X509Certificate2 Ensure(string ip, out string cerPath)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string pfxPath = System.IO.Path.Combine(dir, "dictation-bridge.pfx");
            string caCerPath = System.IO.Path.Combine(dir, "dictation-bridge.cer");
            cerPath = caCerPath;

            // Reuse the saved certificate unless it is about to expire or it does
            // not cover today's address. The SAN carries the stable .local name as
            // well as every current IP, so a phone using either keeps working.
            X509Certificate2 existing = Load(pfxPath);
            if (existing != null && Covers(existing, ip)
                && existing.NotAfter > DateTime.Now.AddDays(30))
            {
                Log.Write("reusing certificate " + existing.Thumbprint);
                return existing;
            }

            if (existing != null)
            {
                // Worth saying out loud: this is the one case that makes the
                // phone ask to trust a new certificate.
                Log.Write("address " + ip + " is not covered by the saved " +
                          "certificate; issuing a new one");
                Log.Write("  if the phone already trusted the old one, it will " +
                          "reject this until " + StableName +
                          " or the new .cer is installed again");
            }

            Log.Write("generating certificate for " + ip);
            X509Certificate2 cert = CreateLeaf(ip);
            System.IO.File.WriteAllBytes(pfxPath,
                cert.Export(X509ContentType.Pfx, PfxPassword));
            System.IO.File.WriteAllBytes(caCerPath, cert.Export(X509ContentType.Cert));
            Log.Write("certificate " + cert.Thumbprint + " covers " + DescribeSan(cert));

            return Load(pfxPath);
        }

        private static X509Certificate2 CreateLeaf(string ip)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=Dictation Bridge", rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                var san = new SubjectAlternativeNameBuilder();
                AddLocalAddresses(san);
                request.CertificateExtensions.Add(san.Build());
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

                return request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddYears(5));
            }
        }

        private static X509Certificate2 Load(string pfxPath)
        {
            if (!System.IO.File.Exists(pfxPath)) return null;
            try
            {
                return new X509Certificate2(
                    System.IO.File.ReadAllBytes(pfxPath), PfxPassword,
                    X509KeyStorageFlags.Exportable);
            }
            catch (Exception e)
            {
                Log.Write("could not read saved certificate: " + e.Message);
                return null;
            }
        }

        // Every address the phone might legitimately use goes into the SAN, so a
        // name mismatch on the socket port cannot happen.
        // Stable name for the app, so the certificate survives a change of
        // address. iOS resolves .local names through Bonjour, so if this
        // machine is visible that way the phone can use the name instead of the
        // IP and one installed certificate keeps working on every network.
        public const string StableName = "dictation-bridge.local";

        public static void AddLocalAddresses(SubjectAlternativeNameBuilder san)
        {
            san.AddDnsName(StableName);
            san.AddDnsName(StaleHostName);
            san.AddIpAddress(IPAddress.Loopback);
            san.AddDnsName("localhost");
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        san.AddIpAddress(ua.Address);
                    }
                }
            }
            catch (Exception) { }
        }

        // The old computer name is kept in the SAN as well, because it rarely
        // changes and a phone may still be using it.
        public static string StaleHostName
        {
            get
            {
                try
                {
                    string n = System.Net.Dns.GetHostName();
                    if (string.IsNullOrEmpty(n)) return "unused.local";
                    int dot = n.IndexOf('.');
                    if (dot > 0) n = n.Substring(0, dot);
                    return n + ".local";
                }
                catch (Exception) { return "unused.local"; }
            }
        }

        public static string Describe(X509Certificate2 cert)
        {
            if (cert == null) return "none";
            return cert.Subject + "  thumbprint " + cert.Thumbprint;
        }

        public static string DescribeSan(X509Certificate2 cert)
        {
            var parts = new List<string>();
            foreach (X509Extension ext in cert.Extensions)
            {
                if (ext.Oid.Value != "2.5.29.17") continue;
                parts.Add(ext.Format(false).Replace("\r", " ").Replace("\n", " "));
            }
            return string.Join(" ", parts.ToArray());
        }

        // True when the certificate is good for today: it either carries the
        // address, or it carries the stable name, in which case the phone can be
        // reached without the address mattering.
        private static bool Covers(X509Certificate2 cert, string ip)
        {
            foreach (X509Extension ext in cert.Extensions)
            {
                if (ext.Oid.Value != "2.5.29.17") continue;
                string text = ext.Format(false);
                if (text.Contains("DNS Name=" + StableName)) return true;
                if (text.Contains(ip)) return true;
            }
            return false;
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly string Path =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dictation-bridge.log");

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            lock (Gate)
            {
                try
                {
                    // File.AppendAllText defaults to UTF-8, but be explicit: the
                    // log carries Cantonese and emoji, and a lossy fallback mangles
                    // them for good.
                    File.AppendAllText(Path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
            Console.WriteLine(line);
        }

        // Phone diagnostics go to their own file: verbose, and one write per
        // report rather than interleaved with the running log.
        private static readonly string DiagPath =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics.log");

        // Keep the file from growing forever: keep the newest chunk and rename
        // the old one. Phone diagnostics are small, but this runs daily.
        private const long DiagMaxBytes = 4L * 1024 * 1024;

        public static void WriteDiag(string report)
        {
            lock (Gate)
            {
                try
                {
                    RollDiag();
                    File.AppendAllText(DiagPath,
                        "===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====" +
                        Environment.NewLine + report + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
        }

        private static void RollDiag()
        {
            try
            {
                if (!File.Exists(DiagPath)) return;
                var info = new FileInfo(DiagPath);
                if (info.Length < DiagMaxBytes) return;
                string old = DiagPath + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(DiagPath, old);
            }
            catch (Exception) { }
        }
    }

    internal static class Injector
    {
        public static void TypeText(string text, bool appendSpace)
        {
            if (string.IsNullOrEmpty(text)) return;

            IntPtr fg = Native.GetForegroundWindow();
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            if (pid == (uint)ProcessIdHelper.Current)
            {
                Log.Write("SKIP: desktop window has focus");
                return;
            }

            var buffer = new List<Native.INPUT>(text.Length * 2 + 2);
            foreach (char c in text)
            {
                // Surrogate halves go through individually; Windows reassembles them.
                buffer.Add(Unicode((ushort)c, false));
                buffer.Add(Unicode((ushort)c, true));
            }
            if (appendSpace && !char.IsWhiteSpace(text[text.Length - 1]))
            {
                buffer.Add(Virtual(0x20, false));
                buffer.Add(Virtual(0x20, true));
            }

            int size = Marshal.SizeOf(typeof(Native.INPUT));
            uint sent = 0;
            const int perCall = 64;
            for (int i = 0; i < buffer.Count; i += perCall)
            {
                int count = Math.Min(perCall, buffer.Count - i);
                var chunk = new Native.INPUT[count];
                buffer.CopyTo(i, chunk, 0, count);
                sent += Native.SendInput((uint)count, chunk, size);
                Thread.Sleep(8);
            }

            if (sent != buffer.Count)
            {
                Log.Write("WARN: SendInput sent " + sent + "/" + buffer.Count +
                          ". If the target app is elevated this is expected (UIPI blocks it).");
            }
            else
            {
                Log.Write("typed: " + Bridge.Describe(Preview(text)));
            }
        }

        private static Native.INPUT Unicode(ushort ch, bool up)
        {
            var input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.U.ki.wVk = 0;
            input.U.ki.wScan = ch;
            input.U.ki.dwFlags = Native.KEYEVENTF_UNICODE | (up ? Native.KEYEVENTF_KEYUP : 0);
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = UIntPtr.Zero;
            return input;
        }

        private static Native.INPUT Virtual(ushort vk, bool up)
        {
            var input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.U.ki.wVk = vk;
            input.U.ki.wScan = 0;
            input.U.ki.dwFlags = up ? Native.KEYEVENTF_KEYUP : 0;
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = UIntPtr.Zero;
            return input;
        }

        private static string Preview(string s)
        {
            string flat = s.Replace("\n", " ").Replace("\r", " ");
            return flat.Length <= 60 ? flat : flat.Substring(0, 60) + "...";
        }
    }

    internal static class ProcessIdHelper
    {
        public static readonly uint Current = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    }

    internal sealed class Bridge
    {
        private readonly string _token;
        private readonly object _gate = new object();
        private readonly List<string> _buffer = new List<string>();
        private bool _armed;
        private bool _flushOnArm = true;
        private int _typedCount;
        private string _lastTyped = "";
        private DateTime _lastSeen = DateTime.MinValue;
        private long _received;

        public Bridge(string token)
        {
            _token = token;
        }

        public bool Armed { get { lock (_gate) { return _armed; } } }
        public int Buffered { get { lock (_gate) { return _buffer.Count; } } }
        public bool GetFlushOnArm()
        {
            lock (_gate) { return _flushOnArm; }
        }

        public void SetFlushOnArm(bool value)
        {
            lock (_gate) { _flushOnArm = value; }
        }

        // With plain HTTP there is no persistent connection, so "connected" means
        // the phone polled recently.
        public bool PhonePresent
        {
            get { lock (_gate) { return (DateTime.UtcNow - _lastSeen).TotalSeconds < 10; } }
        }

        public int Typed
        {
            get { lock (_gate) { return _typedCount; } }
        }

        public string LastText
        {
            get { lock (_gate) { return _lastTyped; } }
        }

        public int Received
        {
            get { lock (_gate) { return (int)_received; } }
        }

        public bool CheckToken(string token)
        {
            return string.Equals(token, _token, StringComparison.Ordinal);
        }

        public event Action Changed;

        private void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null)
            {
                try { handler(); } catch (Exception) { }
            }
        }

        public void Touch()
        {
            bool firstContact;
            lock (_gate)
            {
                firstContact = _lastSeen == DateTime.MinValue;
                _lastSeen = DateTime.UtcNow;
            }
            if (firstContact) Log.Write("phone reached the page");
            RaiseChanged();
        }

        public void OnText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool armed;
            lock (_gate)
            {
                _received++;
                armed = _armed;
            }

            if (armed)
            {
                Injector.TypeText(text, true);
                lock (_gate)
                {
                    _typedCount++;
                    _lastTyped = text;
                }
            }
            else
            {
                bool drop;
                lock (_gate) { drop = !_flushOnArm; }
                if (drop)
                {
                    Log.Write("dropped (unarmed): " + text);
                }
                else
                {
                    lock (_gate) { _buffer.Add(text); }
                    Log.Write("buffered (" + _buffer.Count + "): " + Describe(text));
                }
            }
            RaiseChanged();
        }

        public void SetArmed(bool armed)
        {
            List<string> pending = null;
            lock (_gate)
            {
                _armed = armed;
                if (armed && _flushOnArm && _buffer.Count > 0)
                {
                    pending = new List<string>(_buffer);
                    _buffer.Clear();
                }
            }

            Log.Write(armed ? "ARMED - typing into focus" : "disarmed");
            RaiseChanged();

            if (pending == null) return;
            Log.Write("flushing " + pending.Count + " buffered utterance(s)");
            foreach (string t in pending)
            {
                Injector.TypeText(t, true);
                lock (_gate)
                {
                    _typedCount++;
                    _lastTyped = t;
                }
            }
            RaiseChanged();
        }

        public void ClearBuffer()
        {
            lock (_gate) { _buffer.Clear(); }
            Log.Write("buffer cleared");
            RaiseChanged();
        }

        public List<string> SnapshotBuffer()
        {
            lock (_gate) { return new List<string>(_buffer); }
        }

        public int TypedCount
        {
            get { lock (_gate) { return _typedCount; } }
        }

        public string LastTyped
        {
            get { lock (_gate) { return _lastTyped; } }
        }

        public int WordCount
        {
            get
            {
                lock (_gate)
                {
                    int n = 0;
                    foreach (string t in _buffer) n += t.Split(' ').Length;
                    return n;
                }
            }
        }

        // Escapes non-ASCII so the log is readable in any console codepage, while
        // keeping the exact text in the buffer and in what gets typed.
        public static string Describe(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text)
            {
                if (c < 0x80) sb.Append(c);
                else sb.Append("\\u").Append(((int)c).ToString("x4"));
            }
            return sb.ToString();
        }

        public string StatusJson()
        {
            bool armed;
            int buffered;
            int typed;
            string last;
            bool present;
            int received;
            lock (_gate)
            {
                armed = _armed;
                buffered = _buffer.Count;
                typed = _typedCount;
                last = _lastTyped;
                received = (int)_received;
                present = (DateTime.UtcNow - _lastSeen).TotalSeconds < 10;
            }
            return "{\"armed\":" + (armed ? "true" : "false") +
                   ",\"buffered\":" + buffered +
                   ",\"typed\":" + typed +
                   ",\"received\":" + received +
                   ",\"phone\":" + (present ? "true" : "false") +
                   ",\"last\":" + Json(last) + "}";
        }

        public void CountReceived()
        {
            lock (_gate) { _received++; }
        }

        // Minimal JSON string escaping for the values we echo back.
        public static string Json(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            var sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append("\"").ToString();
        }
    }

    internal static class Servers
    {
        // One port serves both the page and the socket. A separate socket port
        // meant a second TLS listener, and iOS failed that handshake while
        // accepting the identical certificate on the HTTP port. Sharing the
        // port keeps a single, proven TLS path.
        public static void StartAll(Bridge bridge, string token, int port, byte[] pfx)
        {
            // One certificate for the life of the process. Per-connection imports
            // made things worse, not better.
            X509Certificate2 cert = null;
            if (pfx != null)
            {
                try
                {
                    cert = new X509Certificate2(
                        pfx, Certs.PfxPassword, X509KeyStorageFlags.Exportable);
                }
                catch (Exception e)
                {
                    Log.Write("WARN: could not import certificate: " + e.Message);
                }
            }

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            new Thread(() =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch (Exception) { break; }
                    new Thread(() => Handle(client, bridge, token, null, cert, port))
                    { IsBackground = true }.Start();
                }
            }) { IsBackground = true }.Start();
        }

        private static void Handle(TcpClient client, Bridge bridge, string token,
            string unusedPath, X509Certificate2 cert, int servePort)
        {
            try
            {
                client.NoDelay = true;
                string remote = client.Client.RemoteEndPoint.ToString();
                NetworkStream raw = client.GetStream();

                Stream stream = raw;
                if (cert != null)
                {
                    var ssl = new SslStream(raw, false);
                    try
                    {
                        ssl.AuthenticateAsServer(cert, false,
                            SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    }
                    catch (Exception e)
                    {
                        Log.Write("TLS failed from " + remote + ": " + e.Message);
                        try { client.Close(); } catch (Exception) { }
                        return;
                    }
                    stream = ssl;
                }

                // Read until the end of the request head.
                var buf = new List<byte>();
                var chunk = new byte[4096];
                int headEnd = -1;
                while (headEnd < 0)
                {
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0) { client.Close(); return; }
                    for (int k = 0; k < n; k++) buf.Add(chunk[k]);

                    int len = buf.Count;
                    for (int i = 3; i < len; i++)
                    {
                        if (buf[i - 3] == 13 && buf[i - 2] == 10 &&
                            buf[i - 1] == 13 && buf[i] == 10)
                        {
                            // i is the index of the final \n.
                            headEnd = i + 1;
                            break;
                        }
                    }
                    if (buf.Count > 65536) { client.Close(); return; }
                }

                byte[] all = buf.ToArray();
                string head = Encoding.ASCII.GetString(all, 0, headEnd);
                string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) { client.Close(); return; }

                string[] first = lines[0].Split(' ');
                string method = first.Length > 0 ? first[0] : "";
                string path = first.Length > 1 ? first[1] : "/";

                string contentLength = "0";
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0) continue;
                    if (lines[i].Substring(0, colon).Trim()
                        .Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = lines[i].Substring(colon + 1).Trim();
                    }
                }

                int want;
                if (!int.TryParse(contentLength, out want)) want = 0;
                if (want < 0 || want > 65536) want = 0;

                var body = new List<byte>();
                for (int i = headEnd; i < all.Length; i++) body.Add(all[i]);
                while (body.Count < want)
                {
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0) break;
                    for (int k = 0; k < n; k++) body.Add(chunk[k]);
                }

                int q = path.IndexOf('?');
                if (q >= 0) path = path.Substring(0, q);

                Log.Write(method + " " + path + " from " + remote + " body=" + want);

                string outBody;
                string outType = "text/plain; charset=utf-8";
                int status = 200;

                if (path == "/" || path == "/index.html")
                {
                    outBody = Page();
                    outType = "text/html; charset=utf-8";
                }
                else if (path == "/config")
                {
                    // The page version lets an already-open phone notice that the
                    // desktop rebuilt the page and reload itself, instead of
                    // quietly running stale JavaScript.
                    outBody = "{\"token\":" + Bridge.Json(token) +
                              ",\"version\":" + Bridge.Json(PageVersion()) + "}";
                    outType = "application/json";
                }
                else if (path == "/status")
                {
                    bridge.Touch();
                    outBody = bridge.StatusJson();
                    outType = "application/json";
                }
                else if (path == "/diag" && method == "POST")
                {
                    // Written verbatim to a diagnostics file so a failure that only
                    // reproduces on the phone can be read back after the fact.
                    string reportText = Encoding.UTF8.GetString(body.ToArray());
                    string supplied = Extract(reportText, "token");
                    if (supplied != null && bridge.CheckToken(supplied))
                    {
                        Log.WriteDiag(reportText);
                        Log.Write("  diagnostics accepted from " + remote +
                                  " (" + want + " bytes)");
                        outBody = "{\"ok\":true}";
                    }
                    else
                    {
                        status = 403;
                        outBody = "{\"error\":\"bad token\"}";
                    }
                    outType = "application/json";
                }
                else if (path == "/speak" && method == "POST")
                {
                    string payloadText = Encoding.UTF8.GetString(body.ToArray());
                    string supplied = Extract(payloadText, "token");
                    string spoken = Extract(payloadText, "text");

                    if (supplied == null || !bridge.CheckToken(supplied))
                    {
                        status = 403;
                        outBody = "{\"error\":\"bad token\"}";
                        outType = "application/json";
                        Log.Write("  rejected /speak: bad token");
                    }
                    else
                    {
                        bridge.OnText(spoken ?? "");
                        outBody = "{\"ok\":true}";
                        outType = "application/json";
                    }
                }
                else
                {
                    status = 404;
                    outBody = "not found";
                }

                byte[] payloadBytes = Encoding.UTF8.GetBytes(outBody);
                string statusText = status == 200 ? "200 OK"
                    : status == 403 ? "403 Forbidden" : "404 Not Found";
                // no-store alone is not reliably honoured on iOS: Safari keeps a
                // copy in its back/forward cache and will re-serve it after the
                // desktop rebuilds the page. Spell it out every way.
                byte[] responseHead = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 " + statusText + "\r\n" +
                    "Content-Type: " + outType + "\r\n" +
                    "Content-Length: " + payloadBytes.Length + "\r\n" +
                    "Cache-Control: no-store, no-cache, must-revalidate, max-age=0\r\n" +
                    "Pragma: no-cache\r\n" +
                    "Expires: 0\r\n" +
                    "Access-Control-Allow-Origin: *\r\n" +
                    "Connection: close\r\n\r\n");
                stream.Write(responseHead, 0, responseHead.Length);
                stream.Write(payloadBytes, 0, payloadBytes.Length);
                stream.Flush();
            }
            catch (Exception e)
            {
                Log.Write("connection error: " + e.GetType().Name + " - " + e.Message);
            }
            finally
            {
                try { client.Close(); } catch (Exception) { }
            }
        }

        // Cheap fingerprint of the embedded page. Changes whenever the page is
        // rebuilt, which is exactly when a phone needs to reload.
        private static string _version;
        private static System.Security.Cryptography.SHA1 _sha;

        public static string PageVersion()
        {
            if (_version != null) return _version;
            try
            {
                string page = Page();
                if (_sha == null) _sha = System.Security.Cryptography.SHA1.Create();
                byte[] hash = _sha.ComputeHash(Encoding.UTF8.GetBytes(page));
                _version = BitConverter.ToString(hash).Replace("-", "").Substring(0, 12);
            }
            catch (Exception)
            {
                _version = "unknown";
            }
            return _version;
        }

        // The page is compiled into the executable, so this is one file to copy
        // and there is no way for a stray index.html in the folder to change what
        // the phone is served. Read once and cached: it never changes at runtime.
        private static string _page;

        public static string Page()
        {
            if (_page != null) return _page;
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("DictationBridge.page.html"))
                {
                    if (s != null)
                    {
                        using (var reader = new StreamReader(s, Encoding.UTF8))
                        {
                            _page = reader.ReadToEnd();
                            Log.Write("serving the embedded page (" + _page.Length + " bytes)");
                            return _page;
                        }
                    }
                }
                Log.Write("WARN: embedded page resource missing");
            }
            catch (Exception e)
            {
                Log.Write("WARN: could not read embedded page: " + e.Message);
            }

            // Built without the embedded resource: fall back to a file so the
            // source tree still works.
            foreach (string relative in new[] { "index.html", "web\\index.html" })
            {
                string candidate = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, relative);
                if (System.IO.File.Exists(candidate))
                {
                    _page = System.IO.File.ReadAllText(candidate);
                    Log.Write("serving index.html from disk (" + _page.Length + " bytes)");
                    return _page;
                }
            }

            _page = "<!DOCTYPE html><meta charset=utf-8>" +
                    "<title>Dictation Bridge</title>" +
                    "<p style='font:17px system-ui;padding:24px'>" +
                    "The page could not be loaded. Restart the app, or rebuild it " +
                    "with build.ps1 so the page is embedded.</p>";
            return _page;
        }

        // Tiny extractor for the two fields we care about. Avoids a JSON library
        // and any risk of it throwing on unexpected input from the network.
        private static string Extract(string json, string field)
        {
            string needle = "\"" + field + "\"";
            int at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return null;

            int colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0) return null;

            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return null;

            if (json[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < json.Length && json[i] != '"')
                {
                    if (json[i] == '\\' && i + 1 < json.Length)
                    {
                        i++;
                        switch (json[i])
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (i + 4 < json.Length)
                                {
                                    int code;
                                    if (int.TryParse(json.Substring(i + 1, 4),
                                        System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        out code))
                                    {
                                        sb.Append((char)code);
                                    }
                                    i += 4;
                                }
                                break;
                            default: sb.Append(json[i]); break;
                        }
                        i++;
                    }
                    else
                    {
                        sb.Append(json[i]);
                        i++;
                    }
                }
                return sb.ToString();
            }

            int end = i;
            while (end < json.Length && json[end] != ',' && json[end] != '}') end++;
            return json.Substring(i, end - i).Trim();
        }
    }




    internal sealed class HotkeyWindow : NativeWindow
    {
        public event Action Pressed;

        // Ctrl+Alt+D by default, but the user can rebind it. The choice is
        // remembered so it survives a restart.
        private const int HotkeyId = 0x0BD1;
        private const string PrefFile = "dictation-bridge-hotkey.txt";

        private uint _mods = Native.MOD_CONTROL | Native.MOD_ALT;
        private uint _key = (uint)Keys.D;
        private bool _registered;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams { Parent = Native.HWND_MESSAGE });
            Load();
        }

        public string Combination
        {
            get
            {
                string s = "";
                if ((_mods & Native.MOD_CONTROL) != 0) s += "Ctrl+";
                if ((_mods & Native.MOD_ALT) != 0) s += "Alt+";
                if ((_mods & Native.MOD_SHIFT) != 0) s += "Shift+";
                if ((_mods & Native.MOD_WIN) != 0) s += "Win+";
                return s + KeyName(_key);
            }
        }

        // Keys.ToString() turns non-printable keys into names like "Oem7" or
        // "D1", which is useless in a shortcut label.
        public static string KeyName(uint key)
        {
            switch (key)
            {
                case (uint)Keys.Space: return "Space";
                case (uint)Keys.Escape: return "Esc";
                case (uint)Keys.Enter: return "Enter";
                case (uint)Keys.Tab: return "Tab";
                case (uint)Keys.Back: return "Backspace";
                case (uint)Keys.Delete: return "Del";
                case (uint)Keys.Insert: return "Ins";
                case (uint)Keys.Home: return "Home";
                case (uint)Keys.End: return "End";
                case (uint)Keys.PageUp: return "PgUp";
                case (uint)Keys.PageDown: return "PgDn";
                case (uint)Keys.Left: return "Left";
                case (uint)Keys.Right: return "Right";
                case (uint)Keys.Up: return "Up";
                case (uint)Keys.Down: return "Down";
            }
            if (key >= (uint)Keys.F1 && key <= (uint)Keys.F24)
            {
                return "F" + (key - (uint)Keys.F1 + 1).ToString();
            }
            if (key >= 0x30 && key <= 0x39) return ((char)key).ToString();
            if (key >= 0x41 && key <= 0x5A) return ((char)key).ToString();
            if (key >= 0x60 && key <= 0x69) return "Num" + (char)key;
            return "key" + key.ToString("X2");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                if (Pressed != null) Pressed();
            }
            base.WndProc(ref m);
        }

        private void Load()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, PrefFile);
                if (!System.IO.File.Exists(path)) return;
                string[] parts = System.IO.File.ReadAllText(path).Split('+');
                uint k = 0;
                if (parts.Length < 2) return;
                Keys key = (Keys)Enum.Parse(typeof(Keys), parts[parts.Length - 1], true);
                k = (uint)key;
                _key = k;
                _mods = 0;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string m = parts[i].Trim().ToLowerInvariant();
                    if (m == "ctrl") _mods |= Native.MOD_CONTROL;
                    else if (m == "alt") _mods |= Native.MOD_ALT;
                    else if (m == "shift") _mods |= Native.MOD_SHIFT;
                    else if (m == "win") _mods |= Native.MOD_WIN;
                }
                Log.Write("restored hotkey: " + Combination);
            }
            catch (Exception e)
            {
                Log.Write("could not read saved hotkey: " + e.Message);
            }
        }

        private void Save()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, PrefFile);
                string s = "";
                if ((_mods & Native.MOD_CONTROL) != 0) s += "Ctrl+";
                if ((_mods & Native.MOD_ALT) != 0) s += "Alt+";
                if ((_mods & Native.MOD_SHIFT) != 0) s += "Shift+";
                if ((_mods & Native.MOD_WIN) != 0) s += "Win+";
                s += KeyName(_key);
                System.IO.File.WriteAllText(path, s);
            }
            catch (Exception) { }
        }

        // Rebinds to whatever the user pressed. A modifier on its own is not a
        // hotkey, so keep waiting rather than registering something useless.
        public bool TryBind(uint mods, uint key)
        {
            if (mods == 0) return false;

            Unregister();
            if (!Native.RegisterHotKey(Handle, HotkeyId,
                    mods | Native.MOD_NOREPEAT, key))
            {
                Log.Write("WARN: " + Describe(mods, key) + " is already taken by another app.");
                // Put the previous binding back so the app stays usable.
                Native.RegisterHotKey(Handle, HotkeyId, _mods | Native.MOD_NOREPEAT, _key);
                _registered = true;
                return false;
            }

            _mods = mods;
            _key = key;
            _registered = true;
            Save();
            Log.Write("hotkey registered: " + Combination);
            return true;
        }

        private static string Describe(uint mods, uint key)
        {
            string s = "";
            if ((mods & Native.MOD_CONTROL) != 0) s += "Ctrl+";
            if ((mods & Native.MOD_ALT) != 0) s += "Alt+";
            if ((mods & Native.MOD_SHIFT) != 0) s += "Shift+";
            if ((mods & Native.MOD_WIN) != 0) s += "Win+";
            return s + KeyName(key);
        }

        public void Unregister()
        {
            if (!_registered) return;
            Native.UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }

        public void Register()
        {
            if (Native.RegisterHotKey(Handle, HotkeyId,
                    _mods | Native.MOD_NOREPEAT, _key))
            {
                _registered = true;
                Log.Write("hotkey registered: " + Combination);
            }
            else
            {
                Log.Write("WARN: " + Combination +
                          " is already taken. Rebind it in the panel.");
            }
        }
    }

    // Captures the next key combination the user presses, then rebinds.
    internal sealed class HotkeyCaptureForm : Form
    {
        private readonly HotkeyWindow _hotkeys;
        public bool Bound { get; private set; }

        public HotkeyCaptureForm(HotkeyWindow hotkeys)
        {
            _hotkeys = hotkeys;
            Text = "Set hotkey";
            ClientSize = new Size(340, 150);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(24, 26, 30);
            ForeColor = Color.FromArgb(232, 234, 237);
            Font = new Font("Segoe UI", 9F);
            TopMost = true;
            KeyPreview = true;

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Text = "Press the combination you want to use.",
                TextAlign = ContentAlignment.MiddleCenter
            };
            var status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold)
            };
            status.Text = "waiting...";

            var cancel = new Button
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                Text = "Cancel",
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false
            };
            cancel.Click += (s, e) => { Bound = false; Close(); };

            Controls.Add(status);
            Controls.Add(hint);
            Controls.Add(cancel);
            _status = status;
        }

        private readonly Label _status;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;

            if (key == Keys.Escape)
            {
                Bound = false;
                Close();
                return true;
            }

            // F12 is reserved by the debugger, per RegisterHotKey docs.
            if (key == Keys.F12) return true;

            uint modBits = 0;
            if ((mods & Keys.Control) != 0) modBits |= Native.MOD_CONTROL;
            if ((mods & Keys.Alt) != 0) modBits |= Native.MOD_ALT;
            if ((mods & Keys.Shift) != 0) modBits |= Native.MOD_SHIFT;
            if ((mods & Keys.LWin) != 0 || (mods & Keys.RWin) != 0) modBits |= Native.MOD_WIN;

            // A bare modifier is not bindable; wait for a real key.
            if (modBits == 0) return true;

            _status.Text = key == Keys.D ? "D" : key.ToString();
            if (_hotkeys.TryBind(modBits, (uint)key))
            {
                Bound = true;
                Close();
            }
            else
            {
                _status.Text = "taken - try another";
            }
            return true;
        }
    }

    // A small always-on-top panel, in the shape of the floating helpers people
    // already keep beside their work: a status strip you can hit to arm, with the
    // details a click away. Borderless and draggable so it never gets in the way.
    internal sealed class MainForm : Form
    {
        private readonly Bridge _bridge;
        private readonly string _url;
        private readonly HotkeyWindow _hotkeys;
        private readonly System.Windows.Forms.Timer _timer;

        // Not readonly: these are built by BuildUi, which is called from the
        // constructor but is not itself one.
        private Panel _strip;
        private Label _status;
        private Panel _detail;
        private Panel _body;
        private Button _toggle;
        private Button _collapse;
        private Button _setHotkey;
        private Label _hotkeyLabel;
        private CheckBox _flushBox;
        private TextBox _lastText;
        private ListBox _bufferList;
        private LinkLabel _urlLink;

        private Point _dragOrigin;
        private bool _dragging;
        private bool _expanded;
        private bool _suppressFlushEvent;

        // One place that owns the sizes, so expand and collapse cannot disagree.
        private static readonly Size CollapsedSize = new Size(270, 46);
        private static readonly Size ExpandedSize = new Size(270, 320);
        private const string PositionFile = "dictation-bridge-position.txt";

        public MainForm(Bridge bridge, string url, HotkeyWindow hotkeys)
        {
            _bridge = bridge;
            _url = url;
            _hotkeys = hotkeys;

            Text = "Dictation Bridge";
            FormBorderStyle = FormBorderStyle.None;
            Size = CollapsedSize;
            // Lock the width and allow only the height to change. Without this,
            // any child whose minimum size exceeds the client width makes
            // WinForms grow the form, and the panel no longer matches its
            // collapsed and expanded sizes.
            MinimumSize = CollapsedSize;
            MaximumSize = ExpandedSize;
            AutoSize = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(22, 24, 28);
            ForeColor = Color.FromArgb(232, 234, 237);
            Font = new Font("Segoe UI", 9F);
            TopMost = true;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            Padding = new Padding(1);
            try { DoubleBuffered = true; } catch (Exception) { }

            // Soft shadow, the detail that makes a floating panel look deliberate.
            try
            {
                BackColor = Color.FromArgb(0, 0, 0);
                TransparencyKey = Color.FromArgb(0, 0, 0);
                var shadow = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(0, 0, 0),
                    Padding = new Padding(1, 1, 2, 2)
                };
                Controls.Add(shadow);
                var surface = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(22, 24, 28),
                    Padding = new Padding(0, 0, 1, 1)
                };
                shadow.Controls.Add(surface);
                BuildUi(surface);
            }
            catch (Exception)
            {
                // Transparency unsupported: fall back to a plain panel.
                Controls.Clear();
                BuildUi(this);
            }

            _bridge.Changed += Refresh2;
            _timer = new System.Windows.Forms.Timer { Interval = 700 };
            _timer.Tick += (s, e) => Refresh2();
            _timer.Start();
            FormClosed += (s, e) => { _timer.Stop(); SavePosition(); };
            LoadPosition();
            Refresh2();
        }

        // A panel that opens on top of the taskbar, or half off the edge of a
        // screen, is worse than no panel. Remember where the user put it, but
        // only reuse that spot if it is still on a screen that exists.
        private void LoadPosition()
        {
            Point? remembered = null;
            try
            {
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, PositionFile);
                if (System.IO.File.Exists(path))
                {
                    string[] parts = System.IO.File.ReadAllText(path).Split(',');
                    int px, py;
                    if (parts.Length == 2
                        && int.TryParse(parts[0], out px)
                        && int.TryParse(parts[1], out py))
                    {
                        remembered = new Point(px, py);
                    }
                }
            }
            catch (Exception) { }

            Rectangle area = remembered.HasValue
                ? Screen.FromPoint(remembered.Value).WorkingArea
                : Screen.PrimaryScreen.WorkingArea;

            if (remembered.HasValue && OnScreen(remembered.Value, area))
            {
                Location = remembered.Value;
                return;
            }

            // Default: centred, biased a little above the middle so it does not
            // sit on top of the text you are working on.
            Location = new Point(
                area.Left + (area.Width - Width) / 2,
                area.Top + (area.Height - Height) / 3);
        }

        private bool OnScreen(Point p, Rectangle area)
        {
            // Require a decent strip of the panel to be reachable, not just one
            // pixel, or the user cannot click it back into view.
            const int Grab = 60;
            return p.X < area.Right - 10 && p.X + Grab > area.Left
                && p.Y < area.Bottom - 10 && p.Y + Grab > area.Top;
        }

        private void SavePosition()
        {
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PositionFile),
                    Location.X + "," + Location.Y);
            }
            catch (Exception) { }
        }

        private void BuildUi(Control host)
        {
            _body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(22, 24, 28),
                Padding = new Padding(10, 8, 10, 10)
            };

            _strip = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(22, 24, 28)
            };

            _toggle = new Button
            {
                Dock = DockStyle.Left,
                Width = 104,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Text = "DISARMED"
            };
            _toggle.FlatAppearance.BorderSize = 0;
            _toggle.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 64, 67);
            _toggle.Click += (s, e) => _bridge.SetArmed(!_bridge.Armed);

            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8F)
            };

            _collapse = new Button
            {
                Dock = DockStyle.Right,
                Width = 24,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Text = "_",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(154, 160, 166)
            };
            _collapse.FlatAppearance.BorderSize = 0;
            _collapse.Click += (s, e) => SetExpanded(!_expanded);

            _strip.Controls.Add(_status);
            _strip.Controls.Add(_toggle);
            _strip.Controls.Add(_collapse);

            _detail = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(22, 24, 28)
            };

            var phoneRow = new Panel { Dock = DockStyle.Top, Height = 18 };
            _urlLink = new LinkLabel
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
                Font = new Font("Segoe UI", 8F),
                LinkColor = Color.FromArgb(138, 180, 248),
                ActiveLinkColor = Color.FromArgb(138, 180, 248)
            };
            _urlLink.Text = _url;
            _urlLink.LinkClicked += (s, e) => Copy(_url);
            phoneRow.Controls.Add(_urlLink);

            var lastRow = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 2, 0, 2) };
            _lastText = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.None,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(30, 33, 38),
                ForeColor = Color.FromArgb(138, 180, 248),
                Font = new Font("Segoe UI", 9F),
                TextAlign = HorizontalAlignment.Center
            };
            lastRow.Controls.Add(_lastText);

            _bufferList = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(30, 33, 38),
                ForeColor = Color.FromArgb(232, 234, 237),
                IntegralHeight = false,
                Font = new Font("Segoe UI", 8F)
            };

            var listWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 2) };
            listWrap.Controls.Add(_bufferList);

            var listLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 15,
                Text = "BUFFERED - typed when you arm",
                Font = new Font("Segoe UI", 7F, FontStyle.Bold),
                ForeColor = Color.FromArgb(128, 134, 139)
            };

var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 104,
                BackColor = Color.FromArgb(22, 24, 28),
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(0, 4, 0, 0)
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            for (int i = 0; i < 4; i++)
            {
                bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            }
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));

            _flushBox = new CheckBox
            {
                Text = "Buffer while disarmed",
                Checked = true,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8F),
                FlatStyle = FlatStyle.Flat
            };
            _flushBox.FlatAppearance.BorderSize = 0;
            _flushBox.CheckedChanged += (s, e) =>
            {
                if (_suppressFlushEvent) return;
                _bridge.SetFlushOnArm(_flushBox.Checked);
                Log.Write(_flushBox.Checked
                    ? "mode: buffer and flush on arm"
                    : "mode: drop while disarmed");
            };

            _hotkeyLabel = new Label
            {
                Text = "Hotkey: " + _hotkeys.Combination,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8F),
                ForeColor = Color.FromArgb(154, 160, 166),
                AutoEllipsis = true
            };

            _setHotkey = FlatButton("Change hotkey", (s, e) => Rebind());
            var clearButton = FlatButton("Clear buffer", (s, e) => _bridge.ClearBuffer());
            var copyButton = FlatButton("Copy address", (s, e) => Copy(_url));
            // Close() alone would be swallowed by the hide-to-tray handler, so quitting is
// routed back to the tray context which knows how to really exit.
var quitButton = FlatButton("Quit", (s, e) => Quit());

            bottom.Controls.Add(_flushBox, 0, 0);
            bottom.SetColumnSpan(_flushBox, 2);
            bottom.Controls.Add(_hotkeyLabel, 0, 1);
            bottom.SetColumnSpan(_hotkeyLabel, 2);
            bottom.Controls.Add(_setHotkey, 0, 2);
            bottom.Controls.Add(clearButton, 1, 2);
            bottom.Controls.Add(copyButton, 0, 3);
            bottom.Controls.Add(quitButton, 1, 3);

            // Everything below the strip must live inside _detail. Adding the
            // bottom bar to _body instead left its buttons drawn on top of the
            // collapsed strip, where they swallowed clicks aimed for "+".
            _detail.Controls.Add(listWrap);
            _detail.Controls.Add(listLabel);
            _detail.Controls.Add(lastRow);
            _detail.Controls.Add(phoneRow);
            _detail.Controls.Add(bottom);

            _body.Controls.Add(_detail);
            _body.Controls.Add(_strip);

            host.Controls.Add(_body);
            SetExpanded(false);

            // Drag anywhere on the strip or the padding.
            MakeDraggable(_strip);
            MakeDraggable(_body);
            MakeDraggable(_detail);
            MakeDraggable(_status);
            _status.MouseDoubleClick += (s, e) => SetExpanded(!_expanded);
        }

        private static Button FlatButton(string text, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI", 8F),
                BackColor = Color.FromArgb(45, 49, 56),
                ForeColor = Color.FromArgb(232, 234, 237),
                Margin = new Padding(2)
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += onClick;
            return b;
        }

        private void Rebind()
        {
            using (var capture = new HotkeyCaptureForm(_hotkeys))
            {
                capture.ShowDialog(this);
                if (capture.Bound)
                {
                    Log.Write("hotkey changed to " + _hotkeys.Combination);
                }
            }
            Refresh2();
        }

        private void MakeDraggable(Control c)
        {
            c.MouseDown += OnDragStart;
            c.MouseMove += OnDragMove;
            c.MouseUp += OnDragEnd;
        }

        private void OnDragStart(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragOrigin = e.Location;
            // Without capture the move events go to whichever control is under
            // the cursor, not to the one that took the mouse down, so the form's
            // own OnMouseMove never fires and the panel does not follow.
            try { ((Control)sender).Capture = true; } catch (Exception) { }
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Location = new Point(Location.X + e.X - _dragOrigin.X,
                                  Location.Y + e.Y - _dragOrigin.Y);
        }

        private void OnDragEnd(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            try { ((Control)sender).Capture = false; } catch (Exception) { }
            // Keep it reachable if it was dragged somewhere awkward.
            ClampIntoView();
            // Save on every drop, not only on exit, so the position survives a
            // crash or a task-manager kill.
            SavePosition();
        }

        private void ClampIntoView()
        {
            Rectangle area = Screen.FromPoint(Location).WorkingArea;
            int x = Location.X;
            int y = Location.Y;
            if (x + Width < area.Left + 60) x = area.Left + 60 - Width;
            if (x > area.Right - 60) x = area.Right - 60;
            if (y < area.Top) y = area.Top;
            if (y + Height > area.Bottom) y = area.Bottom - Height;
            Location = new Point(x, y);
        }

        public event Action QuitRequested;

        private void Quit()
        {
            if (QuitRequested != null) QuitRequested();
        }

        private void SetExpanded(bool expanded)
        {
            _expanded = expanded;
            // Visible alone was not enough: while collapsed, clicks at those
            // coordinates still reached the Quit button underneath and closed the
            // app. Disabling removes it from hit testing as well.
            _detail.Visible = expanded;
            _detail.Enabled = expanded;
            // Size last: assigning ClientSize afterwards recomputes the outer
            // size and eats the one-pixel shadow border.
            Size = expanded ? ExpandedSize : CollapsedSize;
            PerformLayout();
            foreach (Control c in _body.Controls) c.PerformLayout();
            _body.PerformLayout();
            _collapse.Text = expanded ? "_" : "+";
            _collapse.Invalidate();
            _strip.Invalidate();
        }

        private void Copy(string text)
        {
            try { Clipboard.SetText(text); Log.Write("copied " + text); }
            catch (Exception) { }
        }

        private void Refresh2()
        {
            if (IsDisposed) return;
            bool armed = _bridge.Armed;
            bool present = _bridge.PhonePresent;
            int buffered = _bridge.Buffered;

            _toggle.Text = armed ? "ARMED" : "DISARMED";
            _toggle.BackColor = armed
                ? Color.FromArgb(26, 115, 232)
                : Color.FromArgb(52, 56, 62);
            _toggle.ForeColor = armed ? Color.White : Color.FromArgb(154, 160, 166);

            if (present) _status.Text = "phone ok";
            else _status.Text = "no phone";
            _status.ForeColor = present
                ? Color.FromArgb(52, 168, 83)
                : Color.FromArgb(249, 171, 0);

            _setHotkey.Text = "Change hotkey";
            _hotkeyLabel.Text = "Hotkey: " + _hotkeys.Combination;

            _lastText.Text = _bridge.LastText;

            if (_bufferList.Items.Count != buffered)
            {
                _bufferList.BeginUpdate();
                _bufferList.Items.Clear();
                foreach (string t in _bridge.SnapshotBuffer()) _bufferList.Items.Add(t);
                _bufferList.EndUpdate();
                if (buffered > 0) _bufferList.TopIndex = _bufferList.Items.Count - 1;
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Bridge _bridge;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _armItem;
        private readonly ToolStripMenuItem _bufferItem;
        private readonly HotkeyWindow _hotkeys;

        public TrayContext(Bridge bridge, string token, string url)
        {
            _bridge = bridge;
            _appUrl = url;

            _armItem = new ToolStripMenuItem("Armed (Ctrl+Alt+D)");
            _armItem.Click += (s, e) => _bridge.SetArmed(!_bridge.Armed);
            _armItem.CheckOnClick = false;

            _bufferItem = new ToolStripMenuItem("Buffer while disarmed");
            _bufferItem.Click += (s, e) =>
            {
                bool next = !_bridge.GetFlushOnArm();
                _bridge.SetFlushOnArm(next);
                _bufferItem.Checked = next;
                Log.Write(next ? "mode: buffer and flush on arm" : "mode: drop while disarmed");
            };
            _bufferItem.Checked = true;

            var quitItem = new ToolStripMenuItem("Quit");
            quitItem.Click += (s, e) =>
            {
                _reallyQuitting = true;
                ExitThread();
            };

            var copyItem = new ToolStripMenuItem("Copy page address");
            copyItem.Click += (s, e) =>
            {
                try { Clipboard.SetText(url); Log.Write("copied " + url); } catch (Exception) { }
            };

            var clearItem = new ToolStripMenuItem("Clear buffer");
            clearItem.Click += (s, e) => _bridge.ClearBuffer();

            var appItem = new ToolStripMenuItem("Copy app address");
            appItem.Click += (s, e) => CopyText(_appUrl);

            var showItem = new ToolStripMenuItem("Show window");
            showItem.Click += (s, e) => ShowWindow();

            var menu = new ContextMenuStrip();
            menu.Items.Add(showItem);
            menu.Items.Add(_armItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_bufferItem);
            menu.Items.Add(clearItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(appItem);
            menu.Items.Add(copyItem);
            menu.Items.Add(quitItem);


            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Dictation Bridge",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += (s, e) => ShowWindow();

            _hotkeys = new HotkeyWindow();
            _hotkeys.Pressed += () => _bridge.SetArmed(!_bridge.Armed);
            _hotkeys.Register();

            _form = new MainForm(_bridge, url, _hotkeys);
            _form.QuitRequested += () =>
            {
                _reallyQuitting = true;
                ExitThread();
            };
            _form.Show();
            _form.FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing && !_reallyQuitting)
                {
                    e.Cancel = true;
                    _form.Hide();
                }
            };

            Update();
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (s, e) => Update();
            timer.Start();
        }

        private readonly MainForm _form;
        private readonly string _appUrl;
        private bool _reallyQuitting;

        private static void CopyText(string text)
        {
            try { Clipboard.SetText(text); }
            catch (Exception) { }
        }

        private void ShowWindow()
        {
            if (_form.WindowState == FormWindowState.Minimized)
            {
                _form.WindowState = FormWindowState.Normal;
            }
            _form.Show();
            _form.Activate();
            _form.BringToFront();
        }

        private void Update()
        {
            bool armed = _bridge.Armed;
            _armItem.Checked = armed;
            int buffered = _bridge.Buffered;
            bool present = _bridge.PhonePresent;

            string status = armed ? "ARMED" : "disarmed";
            if (buffered > 0) status += " (" + buffered + " buffered)";
            if (!present) status += " - no phone";

            string tip = "Dictation Bridge: " + status;
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            _tray.Text = tip;
        }

        protected override void ExitThreadCore()
        {
            _tray.Visible = false;
            _tray.Dispose();
            base.ExitThreadCore();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // One port serves the page, the status poll and dictation, so --port
            // is the only knob. The old --ws port is no longer used.
            int httpPort = 8080;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--port" || args[i] == "--http")
                {
                    int parsed;
                    if (int.TryParse(args[i + 1], out parsed)) httpPort = parsed;
                }
            }

            string token = Guid.NewGuid().ToString("N").Substring(0, 8);

            Console.WriteLine("Dictation Bridge");
            Console.WriteLine("===============");

            string ip = PrimaryAddress();
            X509Certificate2 cert = null;
            byte[] pfx = null;
            string caCerPath = null;
            try
            {
                cert = Certs.Ensure(ip, out caCerPath);
                if (cert != null)
                {
                    pfx = cert.Export(X509ContentType.Pfx, Certs.PfxPassword);
                    Log.Write("certificate: " + Certs.Describe(cert));
                }
            }
            catch (Exception e)
            {
                Log.Write("WARN: could not create certificate, falling back to http: " + e.Message);
            }

            bool secure = pfx != null;
            string scheme = secure ? "https" : "http";

            var bridge = new Bridge(token);
            // One port for the page and the socket. iOS accepted TLS on the HTTP
            // port but rejected it on a second listener using the same
            // certificate, so both now share the port that already worked.
            Servers.StartAll(bridge, token, httpPort, pfx);

            string url = scheme + "://" + ip + ":" + httpPort + "/";

            Log.Write("listening: " + scheme + " on " + httpPort + " (page and socket share it)");
            Log.Write("open this on the phone: " + url);
            Log.Write("token: " + token);

            Console.WriteLine();
            if (secure)
            {
                Console.WriteLine("  On the iPhone, once:");
                Console.WriteLine("   1. Send " + System.IO.Path.GetFileName(caCerPath) +
                                  " to the phone and tap it to install.");
                Console.WriteLine("   2. Settings > General > VPN & Device Management > Install.");
                Console.WriteLine("   3. Settings > General > About > Certificate Trust Settings");
                Console.WriteLine("      > enable the Dictation Bridge certificate.");
                Console.WriteLine("   4. Open:  " + url);
            }
            else
            {
                Console.WriteLine("  Open:  " + url);
            }
            Console.WriteLine("  Tap Start once. After that, only the Windows hotkey matters.");
            Console.WriteLine("  Ctrl+Alt+D toggles typing.");
            Console.WriteLine();

            Application.EnableVisualStyles();

            TrayContext context = new TrayContext(bridge, token, url);
            Application.Run(context);
            return 0;
        }

        // Virtual adapters (VMware, Hyper-V, WSL) and link-local addresses are
        // not reachable from a phone, so rank real hardware first.
        private static string PrimaryAddress()
        {
            List<string> best = new List<string>();
            List<string> fallback = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    bool virtualAdapter = desc.Contains("vmware") || desc.Contains("virtualbox") ||
                        desc.Contains("hyper-v") || desc.Contains("vethernet") ||
                        desc.Contains("loopback") || desc.Contains("bluetooth") ||
                        desc.Contains("tap-") || desc.Contains("wsl");

                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        // 169.254.x.x is link-local; not usable across networks.
                        if (ua.Address.ToString().StartsWith("169.254.")) continue;

                        if (virtualAdapter) fallback.Add(ua.Address.ToString());
                        else best.Add(ua.Address.ToString());
                    }
                }
            }
            catch (Exception) { }

            if (best.Count > 0)
            {
                Log.Write("lan addresses: " + string.Join(", ", best.ToArray()));
                return best[0];
            }
            if (fallback.Count > 0)
            {
                Log.Write("WARN: only virtual adapter found: " + string.Join(", ", fallback.ToArray()));
                return fallback[0];
            }
            Log.Write("WARN: no usable IPv4 address found, using loopback");
            return "127.0.0.1";
        }
    }
}
