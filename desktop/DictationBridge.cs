using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    // QRCoder, vendored under vendor\QRCoder. MIT, (c) 2013-2018 Raffael
    // Herrmann; see vendor\QRCoder\LICENSE.txt. Only the encoder is used.
    using QRCoder;
    internal static class Native
    {
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint KEYEVENTF_KEYUP = 0x0002;
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

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("winmm.dll")]
        public static extern uint timeGetTime();
    }

    // Everything the app generates lives in one folder next to the exe, so the
    // program itself stays a single untouched file and everything disposable can
    // be deleted in one go.
    internal static class Store
    {
        private const string FolderName = "data";

        public static string Dir
        {
            get
            {
                string dir = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, FolderName);
                try
                {
                    if (!System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);
                }
                catch (Exception e)
                {
                    Log.Write("WARN: could not create " + dir + ": " + e.Message);
                }
                return dir;
            }
        }

        public static string File(string name)
        {
            return System.IO.Path.Combine(Dir, name);
        }
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
            string pfxPath = Store.File("dictation-bridge.pfx");
            string caCerPath = Store.File("dictation-bridge.cer");
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

    // One log file for the whole run, crash.txt for anything that got away, and
    // last-run.txt for "did the previous run finish".
    //
    // None of the Console.WriteLine calls below reach a person. This is a
    // /target:winexe, so launched from Explorer it has no console window at all
    // and they go nowhere. The file is the only record that survives, which is
    // why the crash paths are the interesting part of this class: before this,
    // a thread that threw outside a handler took the process down silently and
    // left a log that simply stopped mid-sentence.
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly string Path = Store.File("dictation-bridge.log");
        private static readonly string CrashPath = Store.File("crash.txt");
        private static readonly string RunPath = Store.File("last-run.txt");

        // Phone diagnostics go to their own file: verbose, and one write per
        // report rather than interleaved with the running log.
        private static readonly string DiagPath = Store.File("diagnostics.log");

        // The main log rolls too. It used not to, because /status arrives every
        // second or two for as long as the page is open and nobody ever deleted
        // it. That left megabytes of poll noise with the startup and crash lines
        // a user actually needs buried somewhere in the middle.
        private const long MainMaxBytes = 2L * 1024 * 1024;
        private const long DiagMaxBytes = 4L * 1024 * 1024;
        private const long CrashMaxBytes = 256L * 1024;

        private static int _run;
        private static bool _failed;
        private static bool _ended;
        private static string _reason = "no reason recorded";
        private static string _quietKey;
        private static int _quietCount;

        public static void Write(string message)
        {
            string line = Stamp() + "  " + Escape(message);
            lock (Gate)
            {
                FlushQuiet();
                Append(line + Environment.NewLine);
            }
            Console.WriteLine(line);
        }

        // Startup instructions. Previously written straight to the console,
        // which is to say: printed nowhere, unless someone happened to start the
        // exe from a command prompt. They are the first thing anyone needs after
        // a failed start, so they belong in the file too.
        public static void Say(string message)
        {
            Write(message);
        }

        // Where startup got to. If the app dies without reaching the next
        // marker, the last marker written is the answer, which is the whole
        // point: startup runs before any window exists, so nothing else logs.
        public static void Stage(string name)
        {
            Write("--- " + name);
        }

        // The phone polls /status every second or two for as long as the page is
        // open. Written out in full that is thousands of near-identical lines a
        // session. Collapse consecutive repeats and report the count: nothing is
        // lost, and the lines that explain a death stay readable.
        public static void Quiet(string message)
        {
            lock (Gate)
            {
                if (message == _quietKey)
                {
                    _quietCount++;
                    return;
                }
                FlushQuiet();
                _quietKey = message;
                _quietCount = 0;
            }
            Write(message);
        }

        private static void FlushQuiet()
        {
            if (_quietCount <= 0) return;
            Append(Stamp() + "    (" + _quietCount +
                   " more identical line(s), suppressed; last one above)" +
                   Environment.NewLine);
            _quietCount = 0;
            _quietKey = null;
        }

        // A failure somebody will actually want to read. Message-only logging is
        // what makes a bug unreproducible, so the whole exception chain goes in,
        // stack trace included, and it goes in escaped: exception text is
        // localised, and a Chinese Windows otherwise puts non-ASCII into a file
        // people open in whatever console they have.
        public static void Detail(string message, Exception e)
        {
            Write(message);
            foreach (string line in Chain(e)) Write(line);
        }

        // Something no handler caught. Goes to the log and to crash.txt, because
        // "did it throw?" is the first question and crash.txt is a small file to
        // attach. Also flips the run marker, so the next launch knows.
        public static void Crash(Exception e, string where, bool terminating)
        {
            _failed = true;
            var lines = new List<string>();
            lines.Add("=== crash at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                      " in " + where + (terminating ? ", process is going down" : "") + " ===");
            lines.Add("run: " + _run);

            Thread thread = null;
            try { thread = Thread.CurrentThread; } catch (Exception) { }
            if (thread != null)
            {
                lines.Add("thread: " + (thread.Name == null ? "(unnamed)" : thread.Name) +
                          " managedId=" + thread.ManagedThreadId +
                          " background=" + thread.IsBackground +
                          " priority=" + thread.Priority);
            }
            lines.Add("uptime: " + (Environment.TickCount / 1000) + "s");
            lines.Add("os: " + Safe(WinVersion));
            lines.Add("app: " + Escape(Environment.Version.ToString()));
            lines.Add("data: " + Escape(Store.Dir));
            lines.Add("");
            lines.AddRange(Chain(e));

            lock (Gate)
            {
                Append(Stamp() + "  CRASH in " + where + Environment.NewLine);
                foreach (string line in lines) Append("    " + Escape(line) + Environment.NewLine);
                try
                {
                    Roll(CrashPath, CrashMaxBytes);
                    // Raw UTF-8 here rather than escaped: this file is read in
                    // Notepad and attached to a report, not pasted into a console
                    // with an arbitrary code page. The log keeps the escapes.
                    File.AppendAllText(CrashPath,
                        string.Join(Environment.NewLine, lines.ToArray()) +
                        Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
        }

        private static List<string> Chain(Exception e)
        {
            var lines = new List<string>();
            int depth = 0;
            while (e != null && depth < 8)
            {
                string pad = new string(' ', 4 + depth * 2);
                lines.Add(pad + e.GetType().FullName + ": " + e.Message);

                if (e is SocketException)
                {
                    lines.Add(pad + "  socket error code: " +
                              (int)((SocketException)e).ErrorCode + " " +
                              ((SocketException)e).SocketErrorCode);
                }
                if (e is UnauthorizedAccessException)
                    lines.Add(pad + "  permissions: the data folder or the cert store is not writable by this user");
                if (e is FileNotFoundException || e is DirectoryNotFoundException)
                    lines.Add(pad + "  missing file: the folder it wanted does not exist or was deleted under us");
                if (e is ObjectDisposedException)
                    lines.Add(pad + "  disposed: something was closed or torn down while still in use");

                if (!string.IsNullOrEmpty(e.Source))
                    lines.Add(pad + "  source: " + e.Source);

                if (!string.IsNullOrEmpty(e.StackTrace))
                {
                    foreach (string frame in e.StackTrace.Split('\n'))
                    {
                        if (frame.Trim().Length > 0) lines.Add(pad + "  at " + frame.Trim());
                    }
                }
                else
                {
                    lines.Add(pad + "  (no stack trace: thrown before any managed frame ran)");
                }

                e = e.InnerException;
                depth++;
                if (e != null) lines.Add(pad + "caused by:");
            }
            if (e == null && depth == 0)
                lines.Add("    (no exception object: the runtime reported one without detail)");
            return lines;
        }

        // Installed before anything else runs. An exception that escapes a
        // thread kills the process outright, and there is nothing on screen to
        // show for it: no console, no window yet at startup.
        public static void InstallCrashHandlers()
        {
            try
            {
                // Without this, WinForms handles a UI-thread exception itself and
                // shows its own dialog, so Application.ThreadException never fires.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) =>
                {
                    Crash(e.Exception, "the UI thread", false);
                    // Carry on rather than exit. A paint or click handler that
                    // throws does not mean the dictation path is broken, and
                    // staying up is what lets the rest of the session be logged.
                    if (_uiErrors < 5)
                    {
                        _uiErrors++;
                        Box("Dictation Bridge hit a problem on its window.",
                            e.Exception.GetType().Name + ": " + e.Exception.Message +
                            "\r\n\r\nThe app has carried on. Everything is written to:\r\n" +
                            Path);
                    }
                };
            }
            catch (Exception) { }

            try
            {
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Crash(e.ExceptionObject as Exception,
                        e.IsTerminating ? "a thread with no handler" : "a background fault",
                        e.IsTerminating);
                };
            }
            catch (Exception) { }

            try
            {
                // A faulted task nobody awaited would otherwise be swallowed
                // silently by the default .NET 4 behaviour.
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    Crash(e.Exception, "an unobserved task", false);
                    e.SetObserved();
                };
            }
            catch (Exception) { }

            try
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => { EndRun(); };
            }
            catch (Exception) { }
        }

        private static int _uiErrors;

        // Called first, before any work. Frames the session, records what the
        // machine looks like, and reports whether the *previous* run finished.
        // A crash cannot clean up after itself, so last-run.txt still says
        // "running"; the next launch says so out loud, which is the difference
        // between "it died" and "it exited" when someone is reading the log.
        public static void BeginRun(string[] args)
        {
            string previous = null;
            lock (Gate)
            {
                try
                {
                    if (File.Exists(RunPath)) previous = File.ReadAllText(RunPath);
                }
                catch (Exception) { }

                // The counter lives only in this file, so carry it across launches.
                // Restarting at 1 every time makes a log holding several runs
                // impossible to follow, which is the one job this log has.
                _run = 1 + NumberAfter(previous, "run:");

                if (previous != null)
                {
                    if (previous.IndexOf("state: running", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Write("** the previous run did NOT shut down cleanly: it was killed, or");
                        Write("** it crashed somewhere nothing could catch. Its marker was:");
                        foreach (string line in previous.Split('\n'))
                        {
                            if (line.Trim().Length > 0) Write("**   " + line.Trim());
                        }
                    }
                    else
                    {
                        foreach (string line in previous.Split('\n'))
                        {
                            if (line.Trim().Length > 0) Write("previous run: " + line.Trim());
                        }
                    }
                }

                try
                {
                    File.WriteAllText(RunPath, Marker("running", "still starting"),
                        new UTF8Encoding(false));
                }
                catch (Exception) { }
            }

            Write("=== run " + _run + " starting ===");
            Write("  when:    " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Write("  exe:     " + AppDomain.CurrentDomain.BaseDirectory);
            Write("  args:    " + (args == null || args.Length == 0 ? "(none)" : string.Join(" ", args)));
            Write("  os:      " + WinVersion() + "  x64=" + Safe(Arch));
            Write("  osname:  registry says " + Safe(WinName));
            Write("  runtime: " + Environment.Version + " (CLR " + Safe(Clr) + ")");
            Write("  user:    " + Environment.UserName);
            Write("  console: " + (Safe(Redirected) == "true" ? "redirected, no window" : "attached"));
            Write("  data:    " + Store.Dir);
            Write("  log:     " + Path);
            Write("  uptime:  " + (Environment.TickCount / 1000) + "s since boot");
        }

        private static string Arch()
        {
            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            string wow = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
            if (!string.IsNullOrEmpty(wow)) arch = arch + " running as " + wow;
            return string.IsNullOrEmpty(arch) ? "unknown" : arch;
        }

        // Environment.OSVersion lies here. Without a supportedOS manifest in the
        // exe — and adding one changes how the app is shelled, so it is not worth
        // it — Windows reports 6.2 for every modern release, which is useless in
        // a bug report. The registry still has the truth, though its ProductName
        // is itself stale: it says "Windows 10" on a Windows 11 machine. The
        // build number is the only part worth trusting, so lead with that.
        private static string WinVersion()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key == null) return Environment.OSVersion.ToString();
                    object build = key.GetValue("CurrentBuild");
                    if (build == null) return Environment.OSVersion.ToString();

                    string text = "Windows build " + build;
                    object ubr = key.GetValue("UBR");
                    if (ubr != null) text += "." + ubr;
                    string display = key.GetValue("DisplayVersion") as string;
                    if (display != null) text += ", release " + display;
                    return text;
                }
            }
            catch (Exception)
            {
                return Environment.OSVersion.ToString();
            }
        }

        // The registry's ProductName, kept separate and labelled, because it says
        // "Windows 10" on Windows 11 and would otherwise be taken as the answer.
        private static string WinName()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = key == null ? null : key.GetValue("ProductName") as string;
                    return name == null
                        ? "(unknown; trust the build number above)"
                        : "\"" + name + "\" (this registry field is stale on Windows 11)";
                }
            }
            catch (Exception)
            {
                return "(unavailable)";
            }
        }

        private static string Clr()
        {
            return System.Runtime.InteropServices.RuntimeEnvironment.GetSystemVersion();
        }

        private static string Redirected()
        {
            return Console.IsOutputRedirected.ToString();
        }

        // A getter that throws must not take startup down with it. Windows 11
        // exposes everything used here, but the exe is handed to strangers on
        // whatever they have installed.
        private static string Safe(Func<string> get)
        {
            try { return get(); } catch (Exception) { return "(unavailable)"; }
        }

        public static void SetReason(string why)
        {
            _reason = why;
        }

        public static void MarkFailed()
        {
            _failed = true;
        }

        public static void EndRun()
        {
            lock (Gate)
            {
                if (_ended) return;
                _ended = true;
                FlushQuiet();
                try
                {
                    File.WriteAllText(RunPath,
                        Marker(_failed ? "crashed" : "exited", _reason),
                        new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
            Write("=== run " + _run + " ended: " + _reason +
                  (_failed ? "  [a crash was recorded]" : "") + " ===");
        }

        private static string Marker(string state, string reason)
        {
            return "state: " + state + Environment.NewLine +
                   "reason: " + reason + Environment.NewLine +
                   "run: " + _run + Environment.NewLine +
                   "when: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                   Environment.NewLine;
        }

        // One integer field out of the run marker, so the counter and the quoted
        // text above can never disagree.
        private static int NumberAfter(string text, string label)
        {
            if (text == null) return 0;
            foreach (string line in text.Split('\n'))
            {
                if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
                int value;
                if (int.TryParse(line.Substring(label.Length).Trim(), out value)) return value;
            }
            return 0;
        }

        // A startup failure that only reaches the log looks identical to the app
        // never having run, so put it on screen as well.
        public static void Fatal(string headline, string detail)
        {
            _failed = true;
            Write("FATAL: " + headline);
            if (!string.IsNullOrEmpty(detail)) Write("FATAL: " + detail);
            Write("FATAL: nothing else will run. The full log is " + Path);
            Box(headline, (detail == null ? "" : detail + "\r\n\r\n") +
                         "The log is here, please send it:\r\n" + Path);
        }

        private static void Box(string headline, string body)
        {
            try
            {
                MessageBox.Show(body, "Dictation Bridge", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception) { }
        }

        public static void WriteDiag(string report)
        {
            lock (Gate)
            {
                try
                {
                    Roll(DiagPath, DiagMaxBytes);
                    File.AppendAllText(DiagPath,
                        "===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====" +
                        Environment.NewLine + report + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
        }

        // Keep the file from growing forever: keep the newest chunk and rename
        // the old one.
        private static void Roll(string path, long max)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (!File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length < max) return;
                string old = path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            catch (Exception) { }
        }

        private static void Append(string text)
        {
            try
            {
                Roll(Path, MainMaxBytes);
                // File.AppendAllText defaults to UTF-8, but be explicit: the
                // log carries Cantonese and emoji, and a lossy fallback mangles
                // them for good.
                File.AppendAllText(Path, text, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static string Stamp()
        {
            return DateTime.Now.ToString("HH:mm:ss");
        }

        // Non-ASCII becomes \uXXXX so the file survives any console code page.
        // Same rule as Bridge.Describe, applied to everything that is not
        // dictation text.
        private static string Escape(string text)
        {
            if (text == null) return "";
            var sb = new StringBuilder(text.Length + 16);
            foreach (char c in text)
            {
                if (c >= 0x20 && c < 0x7f) sb.Append(c);
                else if (c == '\t') sb.Append("    ");
                else sb.Append("\\u").Append(((int)c).ToString("x4"));
            }
            return sb.ToString();
        }
    }

    internal static class Injector
    {
        // appendSpace stays because it is still the right thing for a whole
        // utterance, but the page no longer sends whole utterances: it streams every
        // partial result the moment it appears, and a space appended to each chunk
        // would double the spaces between words ("want" + "to" -> "want  to").
        // The page owns spacing now, because only it knows where the recogniser put
        // a boundary between two results.
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
        private int _typedCount;
        private string _lastTyped = "";
        private string _recentTyped = "";
        private DateTime _lastSeen = DateTime.MinValue;
        private long _received;

        public Bridge(string token)
        {
            _token = token;
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

        // A rolling window rather than the last chunk. The phone streams partial
        // results as they are recognised, so the most recent arrival is often a
        // single word or character; showing that in the quote card would look like
        // a broken app. What is worth watching is the sentence filling in.
        public string LastText
        {
            get { lock (_gate) { return _recentTyped; } }
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
                // A listener that throws used to vanish. Quiet, because one bad
                // status update would otherwise write a line per phone poll.
                try { handler(); }
                catch (Exception e) { Log.Quiet("a status listener threw " + e.GetType().Name); }
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

        // There is no armed state and no buffer. The phone's Start button is the only
        // on/off switch: while the page is listening it sends, and whatever it
        // sends is typed straight into the focused window. That used to be gated
        // by a desktop toggle as well, which was a second gate for one decision --
        // and it is the reason a phone left face-up on a desk could pick up room
        // noise without spraying it at whatever window you were working in.
        public void OnText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_gate)
            {
                _received++;
            }

            // No trailing space: the page streams deltas and already carries the
            // separator the recogniser implied. Adding one per chunk types a space
            // between every word.
            Injector.TypeText(text, false);
            lock (_gate)
            {
                _typedCount++;
                _lastTyped = text;
                _recentTyped += text;
                // Keep the quote card a readable length rather than an ever-growing
                // wall that the TextBox has to re-layout on every chunk.
                if (_recentTyped.Length > 200)
                {
                    _recentTyped = _recentTyped.Substring(_recentTyped.Length - 200);
                }
            }
            RaiseChanged();
        }

        public int TypedCount
        {
            get { lock (_gate) { return _typedCount; } }
        }

        public string LastTyped
        {
            get { lock (_gate) { return _lastTyped; } }
        }

        // Escapes non-ASCII so the log is readable in any console codepage, while
        // keeping the exact text in what gets typed.
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
            int typed;
            string last;
            bool present;
            int received;
            lock (_gate)
            {
                typed = _typedCount;
                last = _recentTyped;
                received = (int)_received;
                present = (DateTime.UtcNow - _lastSeen).TotalSeconds < 10;
            }
            return "{\"typed\":" + typed +
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
        // Not 8080. That is the single most contested port on a Windows machine:
        // half the dev servers ever written default to it, and Docker Desktop
        // hands out large reserved blocks that routinely swallow it. Someone
        // reported the app simply vanishing because of exactly that.
        public const int DefaultPort = 17123;

        // Where the random fallback samples from, when the first choice is
        // unusable. Private to this app, and below the OS dynamic port range.
        private const int RandomLow = 17123;
        private const int RandomHigh = 17999;
        // One port serves both the page and the socket. A separate socket port
        // meant a second TLS listener, and iOS failed that handshake while
        // accepting the identical certificate on the HTTP port. Sharing the
        // port keeps a single, proven TLS path.
        public static int StartAll(Bridge bridge, string token, int port, byte[] pfx,
            bool allowFallback)
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
                    Log.Detail("WARN: could not import certificate", e);
                }
            }

            // A bare listener.Start() used to end the process on a busy port: the
            // bind happens before any window exists, so nothing caught it and a
            // winexe has no console to print to. Probe instead.
            //
            // AccessDenied is the interesting one and not what it looks like. It
            // is not a conflict -- nothing is listening -- it is Windows saying
            // "this port is reserved", which Hyper-V, WSL2 and Docker Desktop all
            // do in large blocks.
            Exception firstError = null;

            // Candidates in the order they get tried.
            var candidates = new List<int>();
            candidates.Add(port);
            if (allowFallback)
            {
                // Random, not sequential. Walking upward was the obvious fix and it
                // is the wrong one: a reserved block can be thousands of ports
                // wide, so 8080, 8081, 8082... walks straight into the next one.
                // Sampling finds a free port in one step whatever the block size.
                //
                // The range is private to this app and deliberately below the OS
                // dynamic port range. Those are a bad place to run a server: they
                // are in active use by outgoing connections, so a listener there
                // can be stolen out from under us.
                var picks = new List<int>();
                var random = new Random();
                // Bounded, not "loop until we have enough": this only ever adds a
                // port it has not seen, so asking for more distinct ports than the
                // range holds would spin forever. Keep the two numbers in step.
                for (int i = 0; i < 40 && picks.Count < RandomHigh - RandomLow; i++)
                {
                    int candidate = RandomLow + random.Next(RandomHigh - RandomLow + 1);
                    if (candidate != port && !picks.Contains(candidate)) picks.Add(candidate);
                }
                candidates.AddRange(picks);
            }

            int rejected = 0;
            foreach (int candidate in candidates)
            {
                var listener = new TcpListener(IPAddress.Any, candidate);
                try
                {
                    listener.Start();
                }
                catch (SocketException e)
                {
                    try { listener.Stop(); } catch (Exception) { }
                    if (firstError == null) firstError = e;
                    rejected++;
                    // Name the first few so the reason is legible, then count the
                    // rest: 41 identical lines helps nobody.
                    if (rejected <= 8)
                    {
                        Log.Write("port " + candidate + " is not usable: socket error " +
                                  (int)e.ErrorCode + " (" + e.SocketErrorCode + ")" +
                                  ((SocketError)e.ErrorCode == SocketError.AccessDenied
                                      ? " -- reserved by Windows, not in use" : ""));
                    }
                    continue;
                }

                // Reported whether the loop succeeded or ran out, because on failure
                // this count is the only summary there is.
                if (rejected > 8)
                {
                    Log.Write("... and " + (rejected - 8) + " more unusable port(s)");
                }
                if (candidate != port)
                {
                    Log.Write("port " + port + " was unusable, so the app moved to " +
                              candidate + " instead");
                }
                Log.Write("listener bound to port " + candidate);
                Accept(listener, bridge, token, cert, candidate);
                return candidate;
            }

            if (rejected > 8)
            {
                Log.Write("... and " + (rejected - 8) + " more unusable port(s)");
            }
            Log.Write("FATAL: none of the " + candidates.Count +
                      " ports tried were usable, starting at " + port);
            SocketException firstSocket = firstError as SocketException;
            if (firstSocket != null)
            {
                if ((SocketError)firstSocket.ErrorCode == SocketError.AccessDenied)
                {
                    Log.Write("FATAL: Windows has reserved the ranges the app tried.");
                    Log.Write("FATAL: Hyper-V, WSL2 and Docker Desktop reserve large blocks.");
                    Log.Write("FATAL: The reserved ranges on this machine:");
                    foreach (string line in ReservedRanges()) Log.Write("FATAL:   " + line);
                }
                else
                {
                    Log.Write("FATAL: another program already has those ports open.");
                }
            }
            Log.Write("FATAL: start the app with  --port <number>  outside those ranges.");
            throw firstError;
        }

        // There is no managed API for the excluded-port list, so ask netsh. Its
        // headings are localised but the rows are bare numbers, so they can be
        // matched without understanding the language. Anything unrecognised is
        // passed through rather than dropped: this is a diagnostic, and a
        // diagnostic that quietly loses lines is worse than a ragged one.
        private static List<string> ReservedRanges()
        {
            var lines = new List<string>();
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(
                    "netsh.exe", "interface ipv4 show excludedportrange protocol=tcp");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                string output = "";
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    // Read before WaitForExit: waiting first can deadlock on a
                    // full pipe, and this is a startup diagnostic that must not
                    // become one.
                    output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                }

                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.IndexOf('*') >= 0) continue;   // managed-port marker

                    // Split on runs of whitespace: netsh pads its columns with
                    // spaces, so a plain Split(' ') yields a dozen empty strings
                    // and the row never matches.
                    var parts = new List<string>();
                    foreach (string piece in line.Split(' ', '\t'))
                    {
                        if (piece.Length > 0) parts.Add(piece);
                    }
                    if (parts.Count != 2) continue;
                    if (!Digits(parts[0]) || !Digits(parts[1])) continue;
                    lines.Add(parts[0] + " - " + parts[1]);
                }
                if (lines.Count == 0)
                {
                    lines.Add("(could not parse the list; run  netsh interface ipv4 " +
                              "show excludedportrange protocol=tcp  to see it)");
                }
            }
            catch (Exception)
            {
                lines.Add("(could not run netsh; run  netsh interface ipv4 " +
                          "show excludedportrange protocol=tcp  to see it)");
            }
            return lines;
        }

        private static bool Digits(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        private static void Accept(TcpListener listener, Bridge bridge, string token,
            X509Certificate2 cert, int port)
        {
            new Thread(() =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch (Exception e)
                    {
                        // Expected on shutdown. Anything else means the listener is
                        // dead and every request will now fail, so say which it was
                        // rather than breaking out in silence.
                        if (!(e is InvalidOperationException) &&
                            !(e is ObjectDisposedException))
                        {
                            Log.Detail("listener stopped accepting connections", e);
                        }
                        break;
                    }
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

                // Quiet, not Write: the phone polls /status every second or two and
                // those repeats would bury the startup and crash lines. The
                // client's ephemeral port is left out of the key, or nothing would
                // ever match and nothing would be suppressed.
                Log.Quiet(method + " " + path + " from " + Host(remote) +
                          " body=" + want);

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
                // Full detail: this is the catch that used to swallow a crash on a
                // per-connection thread down to a type name and a message.
                Log.Detail("connection error", e);
            }
            finally
            {
                try { client.Close(); } catch (Exception) { }
            }
        }

        // Drops the ephemeral port from "192.168.1.58:54786". Nothing needs it,
        // and it changes on every request, which defeats any repeat detection.
        private static string Host(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return endpoint;
            int colon = endpoint.LastIndexOf(':');
            return colon <= 0 ? endpoint : endpoint.Substring(0, colon);
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
                            // "loaded", not "serving": this runs once, and the
                            // startup version stamp now triggers it before any
                            // request arrives.
                            Log.Write("page loaded from the embedded resource (" +
                                      _page.Length + " bytes)");
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
            // source tree still works. Only for development, never on a normal
            // install where the resource is compiled in.
            foreach (string relative in new[] { "index.html", "web\\index.html" })
            {
                string candidate = System.IO.Path.Combine(
                    Environment.CurrentDirectory, relative);
                if (System.IO.File.Exists(candidate))
                {
                    _page = System.IO.File.ReadAllText(candidate);
                    Log.Write("page loaded from disk instead (" + _page.Length + " bytes)");
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

    // Paints the QR code for the panel's own page address.
    //
    // The encoding is QRCoder's, vendored under vendor\QRCoder and compiled in
    // by build.ps1. Do not hand-write an encoder here: a QR code has enough
    // interacting rules (masking, penalty scoring, placement order, Galois
    // field arithmetic) that a plausible-looking matrix is very often not a
    // scannable one. The first attempt at this was written by hand and rendered
    // a clean-looking code that no scanner could read. QRCoder is 46 KB of
    // compiled-in MIT source, which is cheaper than being wrong.
    internal sealed class QrView : Control
    {
        private bool[,] _modules;

        public QrView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.White;
        }

        // Encodes the address and repaints. Errors are swallowed deliberately:
        // an address that cannot be encoded must not stop the panel from
        // appearing, and the address is always shown in text right beside this.
        public void SetAddress(string address)
        {
            try
            {
                using (QRCodeData data = QRCodeGenerator.GenerateQrCode(
                    address, QRCodeGenerator.ECCLevel.M))
                {
                    // The matrix already carries the four-module quiet zone that
                    // AddQuietZone pads around it, so n counts the quiet zone as
                    // well as the code. Adding another one here would waste a
                    // third of the available pixels and halve the module size.
                    int n = data.ModuleMatrix.Count;
                    bool[,] matrix = new bool[n, n];
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                            matrix[y, x] = data.ModuleMatrix[y][x];
                    _modules = matrix;
                }
            }
            catch (Exception)
            {
                _modules = null;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            if (_modules == null) return;

            int n = _modules.GetLength(0);

            // Whole pixels per module, no antialiasing. A half-pixel module
            // produces blurred edges that a phone camera can fail to resolve.
            int cell = Math.Max(1, Math.Min(Width, Height) / n);
            int drawn = cell * n;
            int offsetX = (Width - drawn) / 2;
            int offsetY = (Height - drawn) / 2;

            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

            using (SolidBrush dark = new SolidBrush(Skin.Ink))
            {
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        if (!_modules[y, x]) continue;
                        g.FillRectangle(dark,
                            offsetX + x * cell,
                            offsetY + y * cell,
                            cell, cell);
                    }
                }
            }
        }
    }

    // A small always-on-top panel, in the shape of the floating helpers people
    // already keep beside their work: a status strip you can hit to arm, with the
    // details a click away. Borderless and draggable so it never gets in the way.
    // Light palette. Windows apps that sit on top of your work all tend to be
    // dark, and a dark block on a light document is the thing your eye snags on.
    internal static class Skin
    {
        public static readonly Color Page = Color.FromArgb(250, 250, 252);
        public static readonly Color Surface = Color.FromArgb(255, 255, 255);
        public static readonly Color Field = Color.FromArgb(243, 244, 247);
        public static readonly Color Ink = Color.FromArgb(32, 35, 42);
        public static readonly Color InkSoft = Color.FromArgb(108, 115, 128);
        public static readonly Color InkFaint = Color.FromArgb(160, 166, 178);
        public static readonly Color Line = Color.FromArgb(228, 230, 236);
        public static readonly Color Accent = Color.FromArgb(24, 105, 220);
        public static readonly Color AccentSoft = Color.FromArgb(232, 240, 254);
        public static readonly Color Good = Color.FromArgb(22, 138, 74);
        public static readonly Color Warn = Color.FromArgb(184, 122, 0);
        public static readonly Color Danger = Color.FromArgb(197, 48, 48);
        public static readonly Color Shadow = Color.FromArgb(28, 32, 44);
        public static readonly Color ShadowEdge = Color.FromArgb(216, 220, 228);

        public static Font Ui { get { return new Font("Segoe UI", 9F); } }
        public static Font UiSmall { get { return new Font("Segoe UI", 8.25F); } }
        public static Font UiTiny { get { return new Font("Segoe UI", 7.5F); } }
        public static Font Strong { get { return new Font("Segoe UI", 9.25F, FontStyle.Bold); } }
        public static Font Badge { get { return new Font("Segoe UI", 8.5F, FontStyle.Bold); } }
        public static Font Title { get { return new Font("Segoe UI", 10.5F, FontStyle.Bold); } }
        public static Font Quote { get { return new Font("Segoe UI", 10F); } }

        // Rounded corners make a borderless panel look like a card rather than a
        // rectangle someone forgot to title-bar.
        public static void Round(Control c, int radius)
        {
            try
            {
                using (GraphicsPath path = Rounded(c.ClientRectangle.Size, radius))
                {
                    Region region = new Region(path);
                    c.Region = region;
                }
            }
            catch (Exception) { }
        }

        private static GraphicsPath Rounded(Size size, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(0, 0, d, d, 180, 90);
            p.AddArc(size.Width - d, 0, d, d, 270, 90);
            p.AddArc(size.Width - d, size.Height - d, d, d, 0, 90);
            p.AddArc(0, size.Height - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Small self-painted pieces. Doing these by hand is what stops the panel
    // looking like a stack of grey WinForms rectangles.
    internal sealed class PaintDot : Control
    {
        public Color DotColor = Skin.Good;
        public bool Breathe;
        public int Phase;

        public PaintDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            // Explicit, not Color.Transparent: a custom-painted control given
            // that back colour throws "not a valid owner window handle" unless
            // it is parented first, and once parented it still routes through
            // the layered path that aliases text.
            BackColor = Skin.Surface;
            Size = new Size(12, 12);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float scale = 1F;
            if (Breathe)
            {
                // 3 -> 5 -> 6 -> 5, a calm breathing ring rather than a blink.
                int[] radii = { 4, 5, 6, 5 };
                scale = radii[Phase % 4] / 4F;
            }
            float cx = Width / 2F, cy = Height / 2F;
            float r = 3.2F * scale;
            if (scale > 1.05F)
            {
                using (Pen halo = new Pen(Color.FromArgb(70, DotColor)))
                {
                    halo.Width = 1.6F;
                    g.DrawEllipse(halo, cx - r - 2.2F, cy - r - 2.2F, (r + 2.2F) * 2, (r + 2.2F) * 2);
                }
            }
            using (SolidBrush b = new SolidBrush(DotColor))
            {
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            }
        }
    }

    internal sealed class PaintText : Control
    {
        public string Caption = "";
        public Color InkColor = Skin.InkSoft;
        public Font TextFont = Skin.UiSmall;
        public ContentAlignment Align = ContentAlignment.MiddleLeft;

        public PaintText()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            // An explicit parent colour rather than Color.Transparent, which
            // throws before parenting and, on a parent, still forces the
            // layered rendering path that makes text jagged.
            BackColor = Skin.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            // Graphics.DrawString wants a StringFormat, not TextFormatFlags.
            using (StringFormat f = new StringFormat())
            {
                f.Alignment = Align == ContentAlignment.MiddleCenter
                    ? StringAlignment.Center
                    : Align == ContentAlignment.MiddleRight
                        ? StringAlignment.Far
                        : StringAlignment.Near;
                f.LineAlignment = StringAlignment.Center;
                f.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                f.Trimming = StringTrimming.EllipsisCharacter;
                using (SolidBrush b = new SolidBrush(InkColor))
                {
                    e.Graphics.DrawString(Caption, TextFont, b,
                        new RectangleF(0, 0, Width, Height), f);
                }
            }
        }

        public void Set(string text, Color color)
        {
            Caption = text;
            InkColor = color;
            Invalidate();
        }
    }

    // A rounded card with an optional left accent bar.
    internal sealed class PaintCard : Control
    {
        public Color Fill = Skin.Field;
        public int Corner = 8;
        public Color? Accent = null;

        public PaintCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Skin.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Skin.RoundedPath(r, Corner))
            using (SolidBrush b = new SolidBrush(Fill))
            {
                g.FillPath(b, path);
            }
            if (Accent.HasValue)
            {
                using (SolidBrush b = new SolidBrush(Accent.Value))
                using (GraphicsPath bar = Skin.RoundedPath(
                    new Rectangle(0, 0, 3, Height - 1), 2))
                {
                    g.FillPath(b, bar);
                }
            }
        }
    }

    // A flat button that paints its own rounded shape and hover states.
    internal sealed class SoftButton : Button
    {
        public Color Fill = Skin.Field;
        public Color Hover = Color.FromArgb(232, 235, 241);
        public Color Ink = Skin.Ink;
        public int Corner = 7;
        public bool Primary;

        public SoftButton()
        {
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Skin.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            bool hot = Enabled && IsDefault;
            Color fill = hot ? Hover : Fill;
            Color ink = Primary ? Color.White : Ink;

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Skin.RoundedPath(r, Corner))
            using (SolidBrush b = new SolidBrush(fill))
            {
                g.FillPath(b, p);
            }
            // Graphics.DrawString needs a StringFormat rather than TextFormatFlags.
            using (StringFormat f = new StringFormat())
            {
                f.Alignment = StringAlignment.Center;
                f.LineAlignment = StringAlignment.Center;
                f.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                f.Trimming = StringTrimming.EllipsisCharacter;
                using (SolidBrush b = new SolidBrush(Enabled ? ink : Skin.InkFaint))
                {
                    g.DrawString(Text, Font, b, new RectangleF(r.X, r.Y, r.Width, r.Height), f);
                }
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Bridge _bridge;
        private readonly string _url;
        private readonly ContextMenuStrip _panelMenu;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _pulse;

        // Not readonly: these are built by BuildUi, which is called from the
        // constructor but is not itself one.
        private Panel _strip;
        private Panel _detail;
        private Panel _body;
        private Button _collapse;
        private TextBox _lastText;
        private LinkLabel _urlLink;

        private Point _dragOrigin;
        private bool _dragging;
        private bool _expanded;
        private int _pulsePhase;
        private PaintDot _lamp;
        private PaintText _phoneState;
        private PaintCard _lastCard;
        private QrView _qr;

        // One place that owns the sizes, so expand and collapse cannot disagree.
        // The expanded panel lost the queue list, the hold-until-armed checkbox and
        // the hotkey row, so it is shorter than it was.
        private static readonly Size CollapsedSize = new Size(320, 56);
        private static readonly Size ExpandedSize = new Size(320, 318);
        private const string PositionFile = "dictation-bridge-position.txt";

        public MainForm(Bridge bridge, string url, ContextMenuStrip panelMenu)
        {
            _bridge = bridge;
            _url = url;
            _panelMenu = panelMenu;

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
            BackColor = Skin.Surface;
            ForeColor = Skin.Ink;
            Font = Skin.Ui;
            TopMost = true;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            Padding = new Padding(0);
            // No TransparencyKey. It switches the window into a layered mode where
            // GDI text antialiasing is switched off, so every custom-painted
            // string comes out jagged. An opaque card with rounded corners is
            // both crisper and simpler.
            BackColor = Skin.ShadowEdge;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            BuildUi(this);
            UsePanelMenu(this);
            ResizeRounded();
            Resize += (s, e) => ResizeRounded();

            _bridge.Changed += Refresh2;
            _timer = new System.Windows.Forms.Timer { Interval = 700 };
            _timer.Tick += (s, e) => Refresh2();
            _timer.Start();

            // A slow breath on the lamp while the phone is in touch, so it is obvious at a
            // glance that the app is listening without needing to read anything.
            _pulse = new System.Windows.Forms.Timer { Interval = 900 };
            _pulse.Tick += (s, e) =>
            {
                _pulsePhase = (_pulsePhase + 1) % 4;
                if (_lamp != null) _lamp.Invalidate();
            };
            _pulse.Start();

            FormClosed += (s, e) =>
            {
                _timer.Stop();
                _pulse.Stop();
                SavePosition();
            };
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
                string path = Store.File(PositionFile);
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
                    Store.File(PositionFile), Location.X + "," + Location.Y);
            }
            catch (Exception) { }
        }

private void BuildUi(Control host)
        {
            _body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Skin.Surface,
                Padding = new Padding(2)
            };

            // ---- the strip -------------------------------------------------
            _strip = new Panel
            {
                Dock = DockStyle.Top,
                Height = 48,
                BackColor = Skin.Surface
            };

            _collapse = new SoftButton
            {
                Dock = DockStyle.Right,
                Width = 28,
                Height = 30,
                Text = "+",
                Font = new Font("Segoe UI", 11F),
                Ink = Skin.InkSoft,
                Corner = 8
            };
            _collapse.Click += (s, e) => SetExpanded(!_expanded);

            var stripPad = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Skin.Surface
            };

            _lamp = new PaintDot
            {
                Location = new Point(12, 18),
                Size = new Size(12, 12)
            };

            // "Start on the phone" replaces what used to be an armed/disarmed
            // button: the only on/off switch is on the phone now, so the strip says
            // where it is rather than offering a second one.
            _phoneState = new PaintText
            {
                Location = new Point(30, 9),
                Size = new Size(200, 24),
                TextFont = Skin.UiSmall,
                InkColor = Skin.InkSoft,
                Align = ContentAlignment.MiddleLeft
            };

            stripPad.Controls.Add(_phoneState);
            stripPad.Controls.Add(_lamp);

            _strip.Controls.Add(stripPad);
            _strip.Controls.Add(_collapse);
            stripPad.BringToFront();

            // ---- everything below the strip --------------------------------
            _detail = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Skin.Surface,
                Padding = new Padding(14, 4, 14, 10)
            };

            // Address, on a soft card so it reads as a field rather than a link.
            // The QR code sits on the left of it: scanning beats typing a URL
            // into a phone keyboard, and the address changes every time the
            // machine lands on a new network, so it has to be shown not written
            // down.
            var urlCard = new Panel
            {
                Dock = DockStyle.Top,
                Height = 132,
                BackColor = Skin.Surface
            };
            var urlBg = new PaintCard
            {
                Dock = DockStyle.Fill,
                Fill = Skin.Field,
                Corner = 8
            };

            _qr = new QrView
            {
                Location = new Point(8, 8),
                // The encoder emits a 29-module symbol for a typical address
                // (21 + quiet zone). At 4px per module that is 116px, which
                // leaves a module wide enough for a phone camera to resolve.
                Size = new Size(116, 116)
            };
            _qr.SetAddress(_url);

            var urlRight = new Panel
            {
                Location = new Point(130, 0),
                Size = new Size(urlCard.Width - 130, urlCard.Height),
                BackColor = Skin.Field
            };
            _urlLink = new LinkLabel
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
                Font = Skin.UiSmall,
                LinkColor = Skin.Accent,
                ActiveLinkColor = Skin.Accent,
                BackColor = Skin.Field
            };
            _urlLink.Text = _url;
            _urlLink.LinkClicked += (s, e) => Copy(_url);
            urlRight.Controls.Add(_urlLink);
            urlCard.Resize += (s, e) =>
            {
                // Absolute coordinates inside a docked panel do not shrink that
                // panel's minimum size, so the right-hand box is kept in step
                // with the card here rather than relying on Dock.
                urlRight.Width = urlCard.ClientSize.Width - 130;
                urlRight.Height = urlCard.ClientSize.Height;
            };

            urlCard.Controls.Add(urlRight);
            urlCard.Controls.Add(_qr);
            urlCard.Controls.Add(urlBg);
            urlBg.SendToBack();

// Last typed utterance, shown as a quote with an accent bar.
            var lastRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Skin.Surface,
                Padding = new Padding(0, 5, 0, 5)
            };
            // Only the left accent bar is painted; the fill belongs to the
            // TextBox, which sits on top of it.
            _lastCard = new PaintCard
            {
                Dock = DockStyle.Fill,
                Fill = Skin.AccentSoft,
                Corner = 8,
                Accent = Skin.Accent
            };
            // A read-only TextBox cannot take Color.Transparent either, so the card
            // behind it carries the colour instead.
            _lastText = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.None,
                BorderStyle = BorderStyle.None,
                BackColor = Skin.AccentSoft,
                ForeColor = Skin.Ink,
                Font = Skin.Quote,
                TextAlign = HorizontalAlignment.Left,
                Margin = new Padding(9, 3, 9, 3)
            };
            lastRow.Controls.Add(_lastText);
            lastRow.Controls.Add(_lastCard);
            _lastCard.SendToBack();

            // ---- footer ----------------------------------------------------
            // Just the two buttons that still do something. The queue list, the
            // hold-until-armed checkbox and the hotkey row all went with the
            // arming model, and a row of disabled-looking furniture would be worse
            // than less.
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                BackColor = Skin.Surface,
                ColumnCount = 2,
                RowCount = 1
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

            var copyButton = SoftButtonOf("Copy address", (s, e) => Copy(_url));
            // Close() alone is swallowed by hide-to-tray, so quit is routed back
            // to the tray context which knows how to really exit.
            var quitButton = SoftButtonOf("Quit", (s, e) => Quit());
            quitButton.Ink = Skin.Danger;

            bottom.Controls.Add(copyButton, 0, 0);
            bottom.Controls.Add(quitButton, 1, 0);

            // Everything below the strip must live inside _detail. Adding the
            // bottom bar to _body instead left its buttons drawn on top of the
            // collapsed strip, where they swallowed clicks aimed for "+".
            _detail.Controls.Add(bottom);
            _detail.Controls.Add(lastRow);
            _detail.Controls.Add(urlCard);

            _body.Controls.Add(_detail);
            _body.Controls.Add(_strip);

            host.Controls.Add(_body);
            SetExpanded(false);

            MakeDraggable(_strip);
            MakeDraggable(_body);
            MakeDraggable(_detail);
            MakeDraggable(stripPad);
            _phoneState.MouseDoubleClick += (s, e) => SetExpanded(!_expanded);
        }

        private static SoftButton SoftButtonOf(string text, EventHandler onClick)
        {
            var b = new SoftButton
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = Skin.UiSmall,
                Margin = new Padding(0, 6, 5, 6)
            };
            b.Click += onClick;
            return b;
        }

        private void MakeDraggable(Control c)
        {
            c.MouseDown += OnDragStart;
            c.MouseMove += OnDragMove;
            c.MouseUp += OnDragEnd;
        }

        // A right-click anywhere on the panel opens the tray menu, so the app does
        // not need two places that explain what it can do.
        //
        // Assigning the same ContextMenuStrip to the form and to every control under
        // it is what makes "anywhere" actually true. WinForms sends WM_CONTEXTMENU
        // to the control under the cursor and never routes it up to a parent, so
        // setting it on the form alone would work only on the few pixels that are
        // genuinely the form -- and the panel is almost entirely children. Setting
        // it on each child also replaces that child's own right-click behaviour,
        // which is the part that would otherwise show two menus at once: the read-
        // only TextBox draws the native edit menu itself.
        //
        // One instance, not a copy per control: ToolStripMenuItem is not thread
        // safe, and the tray icon is still using the same one. Assigning it also
        // means Shift+F10 and the context-menu key work on the panel, which they
        // did not before.
        private void UsePanelMenu(Control root)
        {
            if (_panelMenu == null) return;
            root.ContextMenuStrip = _panelMenu;
            foreach (Control child in root.Controls)
            {
                UsePanelMenu(child);
            }
        }

        // Rounded corners via a Region rather than a transparency key: the shape is
        // clipped at paint time so the card edges stay crisp and the text inside
        // keeps GDI antialiasing.
        private void ResizeRounded()
        {
            try
            {
                using (GraphicsPath p = Skin.RoundedPath(
                    new Rectangle(0, 0, Width - 1, Height - 1), 12))
                {
                    Region = new Region(p);
                }
            }
            catch (Exception) { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush b = new SolidBrush(Skin.Surface))
            {
                g.FillRectangle(b, ClientRectangle);
            }
            using (Pen edge = new Pen(Skin.ShadowEdge))
            {
                using (GraphicsPath p = Skin.RoundedPath(
                    new Rectangle(0, 0, Width - 2, Height - 2), 12))
                {
                    g.DrawPath(edge, p);
                }
            }
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
            // The lamp and the label now track whether the phone is in touch,
            // not whether an armed flag is set. There is no armed flag.
            bool present = _bridge.PhonePresent;

            if (_lamp != null)
            {
                _lamp.DotColor = present ? Skin.Accent : Skin.InkFaint;
                _lamp.Breathe = present;
                _lamp.Phase = _pulsePhase;
                _lamp.Invalidate();
            }

            if (_phoneState != null)
            {
                if (present) _phoneState.Set("listening on the phone", Skin.Good);
                else _phoneState.Set("tap Start on the phone", Skin.Warn);
            }

            _lastText.Text = _bridge.LastText;
            if (_lastCard != null)
            {
                bool hasText = !string.IsNullOrEmpty(_bridge.LastText);
                _lastCard.Accent = hasText ? Skin.Accent : (Color?)null;
                _lastCard.Fill = hasText ? Skin.AccentSoft : Skin.Field;
                _lastCard.Invalidate();
                _lastText.BackColor = hasText ? Skin.AccentSoft : Skin.Field;
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Bridge _bridge;
        private readonly NotifyIcon _tray;

        public TrayContext(Bridge bridge, string token, string url)
        {
            _bridge = bridge;
            _appUrl = url;

            var quitItem = new ToolStripMenuItem("Quit");
            quitItem.Click += (s, e) =>
            {
                _reallyQuitting = true;
                // "from the tray menu" would be wrong half the time now that the
                // same menu opens from a right-click on the panel.
                Log.SetReason("quit from the menu");
                ExitThread();
            };

            // The log is the only record this app leaves, and with no console
            // window finding it means hunting through AppData-style folders. Put
            // it one click away and select the file, ready to attach.
            var logItem = new ToolStripMenuItem("Show the log file");
            logItem.Click += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe",
                        "/select,\"" + Store.File("dictation-bridge.log") + "\"");
                }
                catch (Exception ex)
                {
                    Log.Detail("could not open the log file", ex);
                }
            };

            var copyItem = new ToolStripMenuItem("Copy page address");
            copyItem.Click += (s, e) =>
            {
                try { Clipboard.SetText(url); Log.Write("copied " + url); } catch (Exception) { }
            };

            var appItem = new ToolStripMenuItem("Copy app address");
            appItem.Click += (s, e) => CopyText(_appUrl);

            var showItem = new ToolStripMenuItem("Show window");
            showItem.Click += (s, e) => ShowWindow();

            // No arm item and no buffer item: the phone's Start button is the only
            // on/off switch, and there is nothing to hold text for, because
            // whatever the page sends is typed.
            var menu = new ContextMenuStrip();
            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(appItem);
            menu.Items.Add(copyItem);
            menu.Items.Add(logItem);
            menu.Items.Add(quitItem);

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Dictation Bridge",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += (s, e) => ShowWindow();

            _form = new MainForm(_bridge, url, menu);
            _form.QuitRequested += () =>
            {
                _reallyQuitting = true;
                Log.SetReason("quit from the panel");
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
            // "typing" rather than "armed": the armed/buffered wording described a
            // gate that no longer exists. What the tooltip can honestly report is
            // whether the phone is still in touch.
            string status = _bridge.PhonePresent ? "typing" : "waiting for the phone";
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
            // Before anything else: if this run dies from something we do not
            // expect, these are the only things that can say so. Handlers go on
            // first, then the session is framed, because everything after this
            // point is work that can fail.
            Log.InstallCrashHandlers();
            Log.BeginRun(args);
            try
            {
                return Run(args);
            }
            catch (Exception e)
            {
                Log.Crash(e, "startup", true);
                Log.Fatal("Dictation Bridge stopped before it could start listening.",
                    e.GetType().Name + ": " + e.Message);
                return 1;
            }
            finally
            {
                // Also covers the paths that ended cleanly: without this the
                // marker would still say "running" and the next launch would
                // report a crash that never happened.
                Log.EndRun();
            }
        }

        private static int Run(string[] args)
        {
            // One port serves the page, the status poll and dictation, so --port
            // is the only knob. The old --ws port is no longer used.
            int httpPort = Servers.DefaultPort;
            bool portWasChosen = false;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--port" || args[i] == "--http")
                {
                    int parsed;
                    if (int.TryParse(args[i + 1], out parsed))
                    {
                        httpPort = parsed;
                        portWasChosen = true;
                    }
                }
            }
            Log.Write("port: " + httpPort + (portWasChosen ? " (chosen on the command line)" : " (default)"));

            string token = Guid.NewGuid().ToString("N").Substring(0, 8);

            Log.Say("Dictation Bridge");
            Log.Say("===============");
            Log.Say("Your files are in: " + Store.Dir);
            Log.Say("");

            Log.Stage("picking a network address");
            string ip = PrimaryAddress();

            Log.Stage("certificate");
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
                // Falling back to http is survivable but the reason matters: it
                // is almost always the cert store or the data folder.
                Log.Detail("WARN: could not create certificate, falling back to http", e);
            }

            bool secure = pfx != null;
            string scheme = secure ? "https" : "http";

            var bridge = new Bridge(token);
            // One port for the page and the socket. iOS accepted TLS on the HTTP
            // port but rejected it on a second listener using the same
            // certificate, so both now share the port that already worked.
            Log.Stage("starting the listener on port " + httpPort);
            // Only move the port when it was not asked for. A port passed on the
            // command line is a decision, usually because something else is
            // already using the default, so it is never quietly second-guessed.
            int boundPort = Servers.StartAll(bridge, token, httpPort, pfx, !portWasChosen);
            if (boundPort != httpPort)
            {
                Log.Write("NOTE: the app is on port " + boundPort + " rather than " +
                          httpPort + ". Use the address below and rescan the QR code.");
            }

            string url = scheme + "://" + ip + ":" + boundPort + "/";

            Log.Write("listening: " + scheme + " on " + boundPort + " (page and socket share it)");
            Log.Write("open this on the phone: " + url);
            Log.Write("token: " + token);
            Log.Write("embedded page version: " + Servers.PageVersion());

            Log.Say("");
            if (secure)
            {
                Log.Say("  On the iPhone, once:");
                Log.Say("   1. Send this file to the phone and tap it:");
                Log.Say("");
                Log.Say("        " + caCerPath);
                Log.Say("");
                Log.Say("   2. Settings > General > VPN & Device Management > Install.");
                Log.Say("   3. Settings > General > About > Certificate Trust Settings");
                Log.Say("      > enable the Dictation Bridge certificate.");
                Log.Say("   4. Open:  " + url);
            }
            else
            {
                Log.Say("  Open:  " + url);
            }
            Log.Say("  Leave Start on and it keeps listening. Whatever you say is typed");
            Log.Say("  straight into whatever window you are working in.");
            Log.Say("");

            Log.Stage("building the panel");
            Application.EnableVisualStyles();

            TrayContext context = new TrayContext(bridge, token, url);

            Log.Stage("running");
            Log.Write("uptime at startup: " + (Environment.TickCount / 1000) + "s since boot");
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
