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

        // For a panel that never takes the focus; see MainForm.
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);
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

    // The one secret between this PC and the phone.
    //
    // It used to be a session token that the page fetched from /config. Anything
    // else on the Wi-Fi could fetch it just as easily, so all it proved was that
    // a request came from someone who could reach the port -- and anyone who
    // could reach the port could type into whatever window had focus.
    //
    // The key now travels in the QR code, after the '#'. A browser never sends
    // that part of an address to any server, so the key exists on the panel the
    // user is looking at, on the phone that scanned it, and nowhere else. The
    // page sends it back as a header on every request, and the server never
    // hands it out.
    //
    // It lives on disk, so the phone stays paired across restarts and network
    // changes; a key that changed on every launch would mean rescanning every
    // morning. "Reset phone pairing" throws it away. It is never written to the
    // log.
    internal static class Pairing
    {
        private const string FileName = "pairing-key.txt";
        // 96 random bits, as 24 hex digits. Far beyond guessing over a LAN, and
        // short enough that the QR code only grows by one size step.
        private const int KeyBytes = 12;
        private static readonly object Gate = new object();
        private static string _key;
        private static bool _used;

        public static string Key
        {
            get { lock (Gate) { Load(); return _key; } }
        }

        // Whether any phone has ever made a request with the current key. Lets
        // the panel open on the QR code until the first phone is paired.
        public static bool Used
        {
            get { lock (Gate) { Load(); return _used; } }
        }

        public static string Link(string baseUrl)
        {
            return baseUrl + "#k=" + Key;
        }

        // Constant time, so how quickly a guess is refused says nothing about
        // how close it was.
        public static bool Matches(string supplied)
        {
            if (string.IsNullOrEmpty(supplied)) return false;
            string key = Key;
            string candidate = supplied.Trim().ToLowerInvariant();
            if (candidate.Length != key.Length) return false;
            int diff = 0;
            for (int i = 0; i < key.Length; i++) diff |= key[i] ^ candidate[i];
            return diff == 0;
        }

        // Called on every accepted request; only the first one does any work.
        public static void MarkUsed()
        {
            lock (Gate)
            {
                Load();
                if (_used) return;
                _used = true;
                Save();
            }
            Log.Write("phone paired: the first request with the current key arrived");
        }

        // Every phone that scanned the old code stops working until it scans the
        // new one.
        public static void Reset()
        {
            lock (Gate)
            {
                _key = NewKey();
                _used = false;
                Save();
            }
            Log.Write("pairing reset: the old key no longer works, rescan the QR code");
        }

        // Reads the file once; later calls are free. A missing or damaged file
        // gets a fresh key, which unpairs any phone, but that is the only safe
        // reading of a key that cannot be trusted.
        private static void Load()
        {
            if (_key != null) return;
            string path = Store.File(FileName);
            try
            {
                if (System.IO.File.Exists(path))
                {
                    foreach (string raw in System.IO.File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
                        {
                            string value = line.Substring(4).Trim().ToLowerInvariant();
                            if (IsKey(value)) _key = value;
                        }
                        else if (line.StartsWith("paired:", StringComparison.OrdinalIgnoreCase))
                        {
                            _used = true;
                        }
                    }
                    if (_key == null) Log.Write("WARN: " + FileName + " held no usable key, so a new one was made");
                }
            }
            catch (Exception e)
            {
                Log.Write("WARN: could not read " + FileName + ": " + e.Message);
            }
            if (_key == null)
            {
                _key = NewKey();
                _used = false;
                Save();
            }
        }

        // A failed save is survivable: the key still works for this run, and
        // the phone has to rescan after the next start. Said once, in the log.
        private static void Save()
        {
            var text = new StringBuilder();
            text.Append("# Dictation Bridge pairing key. Anyone who has it can type on this PC.\r\n");
            text.Append("# Delete this file, or use \"Reset phone pairing\", to unpair every phone.\r\n");
            text.Append("key: ").Append(_key).Append("\r\n");
            if (_used)
            {
                text.Append("paired: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
            }
            try
            {
                System.IO.File.WriteAllText(Store.File(FileName), text.ToString(), Encoding.ASCII);
            }
            catch (Exception e)
            {
                Log.Write("WARN: could not save " + FileName + " (the phone will need to rescan " +
                          "after a restart): " + e.Message);
            }
        }

        private static string NewKey()
        {
            var bytes = new byte[KeyBytes];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var sb = new StringBuilder(KeyBytes * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static bool IsKey(string value)
        {
            if (value == null || value.Length != KeyBytes * 2) return false;
            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
            }
            return true;
        }
    }

    internal static class Certs
    {
        public const string PfxPassword = "dictation-bridge";

        // How many addresses a certificate lists besides 127.0.0.1. Each new
        // certificate carries the old one's addresses forward, newest first, so
        // this is also how many networks a PC can visit before the oldest drops
        // off the list.
        private const int MaxAddresses = 16;

        // Self-signed, and that is the floor rather than a preference. Safari
        // only exposes SpeechRecognition on a secure origin, so plain HTTP cannot
        // be used here at all, and iOS has no "proceed anyway" button the way
        // macOS Safari does. A CA hierarchy was tried and reverted: SslStream on
        // Windows refuses to serve a chain it cannot validate to a root in a
        // local trust store, and installing that root on a user's PC is not
        // something this app should do silently.
        //
        // The phone trusts this certificate itself, so every replacement means
        // installing the new one on the phone. It is replaced only when it does
        // not list the address in the QR code, or is a month from expiring.
        // replaced says that a certificate the phone may already trust was
        // thrown away, which is when the person has to be told.
        public static X509Certificate2 Ensure(string ip, out string cerPath, out bool replaced)
        {
            string pfxPath = Store.File("dictation-bridge.pfx");
            cerPath = Store.File("dictation-bridge.cer");
            replaced = false;

            X509Certificate2 existing = Load(pfxPath);
            if (existing == null && System.IO.File.Exists(pfxPath))
            {
                // There was one, so the phone may trust it; Load said why it
                // could not be read.
                Log.Write("  the phone has to install the new " + System.IO.Path.GetFileName(cerPath) +
                          " once");
                replaced = true;
            }
            if (existing != null)
            {
                bool covers = Covers(existing, ip);
                if (covers && existing.NotAfter > DateTime.Now.AddDays(30))
                {
                    Log.Write("reusing certificate " + existing.Thumbprint + ", which covers " +
                              DescribeSan(existing));
                    // Only a person reads the .cer, and they may have tidied it
                    // away; it is the public half, so it can be written again.
                    if (!System.IO.File.Exists(cerPath)) WriteCer(existing, cerPath);
                    return existing;
                }

                // The one case that makes the phone install a certificate again,
                // so say exactly why.
                if (!covers)
                {
                    Log.Write("address " + ip + " is not in the saved certificate, which covers " +
                              DescribeSan(existing) + "; issuing a new one");
                }
                else
                {
                    Log.Write("the saved certificate expires on " +
                              existing.NotAfter.ToString("yyyy-MM-dd") + "; issuing a new one");
                }
                Log.Write("  the phone has to install the new " + System.IO.Path.GetFileName(cerPath) +
                          " once. It lists the old addresses as well, so going back to one of" +
                          " them will not need another");
                replaced = true;
            }

            List<IPAddress> addresses = AddressesFor(ip, existing);
            if (existing != null) existing.Dispose();
            Log.Write("generating a certificate for " + ip);
            X509Certificate2 cert = CreateLeaf(addresses);
            System.IO.File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, PfxPassword));
            WriteCer(cert, cerPath);
            Log.Write("certificate " + cert.Thumbprint + " covers " + DescribeSan(cert));
            cert.Dispose();

            return Load(pfxPath);
        }

        // The addresses the saved certificate lists, so that of two equally good
        // addresses the one the phone already trusts goes in the QR code. Empty
        // when there is no certificate yet.
        public static List<IPAddress> SavedAddresses()
        {
            using (X509Certificate2 saved = Load(Store.File("dictation-bridge.pfx")))
            {
                return saved == null ? new List<IPAddress>() : AddressesIn(saved);
            }
        }

        private static void WriteCer(X509Certificate2 cert, string cerPath)
        {
            try
            {
                System.IO.File.WriteAllBytes(cerPath, cert.Export(X509ContentType.Cert));
            }
            catch (Exception e)
            {
                Log.Write("WARN: could not write " + cerPath + ": " + e.Message);
            }
        }

        // What a new certificate lists: the address in the QR code, this PC's
        // other addresses that a phone could reach, then everything the
        // certificate it replaces listed. A laptop that moves between home and
        // the office settles on one certificate that covers both, instead of
        // asking the phone for a new one at every move.
        private static List<IPAddress> AddressesFor(string ip, X509Certificate2 previous)
        {
            var list = new List<IPAddress>();
            IPAddress chosen;
            if (IPAddress.TryParse(ip, out chosen)) AddAddress(list, chosen);
            foreach (Addresses.Candidate c in Addresses.Scan())
            {
                if (!c.Virtual) AddAddress(list, c.Address);
            }
            if (previous != null)
            {
                foreach (IPAddress a in AddressesIn(previous)) AddAddress(list, a);
            }
            if (list.Count > MaxAddresses) list.RemoveRange(MaxAddresses, list.Count - MaxAddresses);
            return list;
        }

        private static void AddAddress(List<IPAddress> list, IPAddress a)
        {
            if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) return;
            foreach (IPAddress b in list)
            {
                if (b.Equals(a)) return;
            }
            list.Add(a);
        }

        // Five years, which is longer than the 825 days Apple allows a TLS
        // certificate, and that is fine: the limit is not applied to a
        // certificate the user installed and trusted as its own root, which is
        // exactly what this one is (trustd skips "user-anchored leafs"). Do not
        // shorten it to satisfy a rule that does not apply; every renewal is a
        // reinstall on the phone.
        private static X509Certificate2 CreateLeaf(List<IPAddress> addresses)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=Dictation Bridge", rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                // The phone checks the address it connected to against this
                // list, and the QR code always carries an address. localhost and
                // 127.0.0.1 are for trying the page on the PC itself.
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName("localhost");
                san.AddIpAddress(IPAddress.Loopback);
                foreach (IPAddress a in addresses) san.AddIpAddress(a);
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

        public static string Describe(X509Certificate2 cert)
        {
            if (cert == null) return "none";
            return cert.Subject + "  thumbprint " + cert.Thumbprint;
        }

        public static string DescribeSan(X509Certificate2 cert)
        {
            var names = new List<string>();
            var addresses = new List<IPAddress>();
            ReadSan(cert, names, addresses);
            foreach (IPAddress a in addresses) names.Add(a.ToString());
            return names.Count == 0 ? "nothing" : string.Join(", ", names.ToArray());
        }

        // True only when the certificate lists the address itself, because that
        // is what the phone checks.
        //
        // This used to search the text of X509Extension.Format, which differs
        // between Windows versions and display languages, and it counted any
        // certificate listing dictation-bridge.local as covering every address.
        // Every certificate listed it, so none was ever replaced: a PC that
        // moved to a new address kept serving a certificate that did not match
        // it, and installing that certificate again could not help. Nothing ever
        // published that name, either, so it is no longer in the list.
        internal static bool Covers(X509Certificate2 cert, string ip)
        {
            IPAddress want;
            if (cert == null || !IPAddress.TryParse(ip, out want)) return false;
            foreach (IPAddress a in AddressesIn(cert))
            {
                if (a.Equals(want)) return true;
            }
            return false;
        }

        internal static List<IPAddress> AddressesIn(X509Certificate2 cert)
        {
            var names = new List<string>();
            var addresses = new List<IPAddress>();
            ReadSan(cert, names, addresses);
            return addresses;
        }

        // The names and addresses in the certificate's subjectAltName.
        internal static void ReadSan(X509Certificate2 cert, List<string> names, List<IPAddress> addresses)
        {
            try
            {
                foreach (X509Extension ext in cert.Extensions)
                {
                    if (ext.Oid != null && ext.Oid.Value == "2.5.29.17") ReadSan(ext.RawData, names, addresses);
                }
            }
            catch (Exception e)
            {
                Log.Quiet("could not read a certificate's addresses: " + e.Message);
            }
        }

        // The extension's DER: a SEQUENCE of GeneralName, where [2] is a DNS
        // name and [7] an IP address of four or sixteen bytes. Anything else is
        // skipped, and anything malformed ends the list where it is.
        internal static void ReadSan(byte[] der, List<string> names, List<IPAddress> addresses)
        {
            int pos = 0, tag, length;
            if (der == null || !ReadTag(der, ref pos, der.Length, out tag, out length) || tag != 0x30) return;
            int end = pos + length;
            while (pos < end)
            {
                if (!ReadTag(der, ref pos, end, out tag, out length)) break;
                if (tag == 0x82)
                {
                    names.Add(Encoding.ASCII.GetString(der, pos, length));
                }
                else if (tag == 0x87 && (length == 4 || length == 16))
                {
                    var bytes = new byte[length];
                    Array.Copy(der, pos, bytes, 0, length);
                    addresses.Add(new IPAddress(bytes));
                }
                pos += length;
            }
        }

        // One DER tag and length, leaving pos on the first byte of the value.
        // False for anything malformed, or that would run past end.
        private static bool ReadTag(byte[] der, ref int pos, int end, out int tag, out int length)
        {
            tag = 0;
            length = 0;
            if (der == null || end > der.Length || pos + 2 > end) return false;
            tag = der[pos++];
            // Tag numbers above 30 take extra bytes; a SAN never uses them.
            if ((tag & 0x1F) == 0x1F) return false;
            int first = der[pos++];
            if (first < 0x80)
            {
                length = first;
            }
            else
            {
                int count = first & 0x7F;
                if (count == 0 || count > 3 || pos + count > end) return false;
                for (int i = 0; i < count; i++) length = (length << 8) | der[pos++];
            }
            return length <= end - pos;
        }
    }

    // Which of this PC's addresses goes in the QR code, and following it when
    // it changes.
    //
    // It used to be the first IPv4 address on the first adapter whose name did
    // not look virtual, in whatever order Windows listed them, chosen once at
    // startup. A VPN or a second adapter could put an address the phone cannot
    // reach in the code, and a laptop that woke up on another network kept
    // showing the old address until the app was restarted.
    internal static class Addresses
    {
        internal sealed class Candidate
        {
            public IPAddress Address;
            public string Adapter;
            // A default gateway: the adapter is on the network with the router,
            // which is nearly always the one the phone is on as well.
            public bool Gateway;
            public bool Virtual;
            public bool Private;
            public bool Physical;
            // Listed in the saved certificate, so the phone already trusts it.
            public bool Trusted;
        }

        // Adapter names that mean a virtual machine, a container, a VPN or a
        // tether: something a phone on the Wi-Fi cannot reach.
        private static readonly string[] VirtualWords =
        {
            "vmware", "virtualbox", "hyper-v", "vethernet", "wsl", "docker", "loopback",
            "bluetooth", "tap-", "vpn", "wireguard", "tailscale", "zerotier", "hamachi", "npcap"
        };

        private static readonly object Gate = new object();
        private static string _current = "127.0.0.1";
        private static bool _fixed;
        private static string _scheme = "https";
        private static int _port;

        // The address in the QR code now.
        public static string Current
        {
            get { lock (Gate) { return _current; } }
        }

        // The page's address, which is what the QR code carries before the key.
        public static string Url
        {
            get { lock (Gate) { return _scheme + "://" + _current + ":" + _port + "/"; } }
        }

        public static void Serving(string scheme, int port)
        {
            lock (Gate)
            {
                _scheme = scheme;
                _port = port;
            }
        }

        // At startup. requested is what --address said, if anything, and it is
        // used exactly as given and never moved.
        public static string Choose(string requested)
        {
            List<Candidate> all = Scan();
            Candidate best = Pick(all, null, Certs.SavedAddresses());
            Log.Write("addresses: " + Describe(all));
            string ip;
            if (requested != null)
            {
                bool mine = false;
                foreach (Candidate c in all)
                {
                    if (c.Address.ToString() == requested) mine = true;
                }
                ip = requested;
                Log.Write("address: " + ip + ", chosen with --address" + (mine ? "" :
                    ". It is not one of this PC's, so the phone has to reach it some other way"));
            }
            else if (best != null)
            {
                ip = best.Address.ToString();
                Log.Write("address: " + ip);
                if (best.Virtual)
                {
                    Log.Write("WARN: only virtual adapters have an address, so the phone may not " +
                              "reach it. Start with --address to choose one");
                }
            }
            else
            {
                ip = "127.0.0.1";
                Log.Write("WARN: no usable IPv4 address, so the QR code says 127.0.0.1, which a " +
                          "phone cannot reach. It moves as soon as the network is up");
            }
            lock (Gate)
            {
                _current = ip;
                _fixed = requested != null;
            }
            return ip;
        }

        // After Windows says an address came or went. Returns the new address
        // when the QR code has to change, and null when it does not. Runs on a
        // worker thread, because making a certificate takes a moment.
        // newCertificate says the phone has to install a new one as well.
        public static string Follow(out bool newCertificate)
        {
            newCertificate = false;
            lock (Gate)
            {
                if (_fixed) return null;
                List<Candidate> all = Scan();
                Candidate next = Pick(all, _current, Certs.SavedAddresses());
                // No network at all, usually for a moment: keep the old code up,
                // since the same address is the likeliest thing to come back.
                if (next == null) return null;
                string ip = next.Address.ToString();
                if (ip == _current) return null;

                Log.Write("address changed from " + _current + " to " + ip + "; addresses: " +
                          Describe(all));
                if (Servers.Secure)
                {
                    string cerPath;
                    bool replaced;
                    X509Certificate2 cert = Certs.Ensure(ip, out cerPath, out replaced);
                    if (cert == null)
                    {
                        Log.Write("WARN: no certificate for " + ip + ", so the QR code stays on " + _current);
                        return null;
                    }
                    // Whether the phone has to install one is down to what it is
                    // served, not to what Ensure did: a .pfx deleted while the app
                    // ran gives a new certificate that replaced nothing on disk.
                    newCertificate = Servers.UseCertificate(cert);
                }
                _current = ip;
                return ip;
            }
        }

        public static List<Candidate> Scan()
        {
            var list = new List<Candidate>();
            NetworkInterface[] adapters;
            try
            {
                adapters = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception e)
            {
                Log.Write("WARN: could not list the network adapters: " + e.Message);
                return list;
            }
            foreach (NetworkInterface ni in adapters)
            {
                // One adapter that cannot be read should not hide the others.
                try { Add(list, ni); }
                catch (Exception e) { Log.Quiet("skipped adapter " + ni.Name + ": " + e.Message); }
            }
            return list;
        }

        private static void Add(List<Candidate> list, NetworkInterface ni)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) return;
            NetworkInterfaceType type = ni.NetworkInterfaceType;
            if (type == NetworkInterfaceType.Loopback || type == NetworkInterfaceType.Tunnel) return;

            IPInterfaceProperties props = ni.GetIPProperties();
            bool gateway = false;
            foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
            {
                if (g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any)) gateway = true;
            }
            string words = (ni.Name + " " + ni.Description).ToLowerInvariant();
            bool isVirtual = false;
            foreach (string w in VirtualWords)
            {
                if (words.Contains(w)) isVirtual = true;
            }
            bool physical = type == NetworkInterfaceType.Wireless80211 ||
                type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.GigabitEthernet ||
                type == NetworkInterfaceType.FastEthernetT || type == NetworkInterfaceType.FastEthernetFx;

            foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
            {
                IPAddress a = ua.Address;
                if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                byte[] b = a.GetAddressBytes();
                // 169.254.x.x is what Windows gives itself when nothing answered.
                if (b[0] == 169 && b[1] == 254) continue;
                list.Add(new Candidate
                {
                    Address = a,
                    Adapter = ni.Name,
                    Gateway = gateway,
                    Virtual = isVirtual,
                    // What home and office networks hand out. 100.64/10 is not
                    // one of them: it is carrier NAT, and Tailscale.
                    Private = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                              (b[0] == 192 && b[1] == 168),
                    Physical = physical
                });
            }
        }

        // The address for the QR code. The one already in it stays for as long
        // as this PC has it, even when a better one turns up, so plugging in a
        // cable does not strand a phone that is working. Otherwise the best one
        // by Better, and of two equals the one Windows listed first. Null when
        // there is nothing to choose from.
        internal static Candidate Pick(List<Candidate> all, string current, List<IPAddress> trusted)
        {
            Candidate best = null;
            foreach (Candidate c in all)
            {
                c.Trusted = false;
                foreach (IPAddress t in trusted)
                {
                    if (t.Equals(c.Address)) c.Trusted = true;
                }
            }
            foreach (Candidate c in all)
            {
                if (current != null && c.Address.ToString() == current) return c;
                if (best == null || Better(c, best)) best = c;
            }
            return best;
        }

        // Each test only settles a tie in the ones before it.
        private static bool Better(Candidate a, Candidate b)
        {
            if (a.Gateway != b.Gateway) return a.Gateway;
            if (a.Virtual != b.Virtual) return !a.Virtual;
            if (a.Private != b.Private) return a.Private;
            if (a.Physical != b.Physical) return a.Physical;
            if (a.Trusted != b.Trusted) return a.Trusted;
            return false;
        }

        // For the log, so a wrong pick can be explained from it.
        internal static string Describe(List<Candidate> all)
        {
            if (all.Count == 0) return "none";
            var parts = new List<string>();
            foreach (Candidate c in all)
            {
                var notes = new List<string>();
                if (c.Gateway) notes.Add("router");
                if (c.Virtual) notes.Add("virtual");
                if (!c.Private) notes.Add("not a private address");
                if (c.Trusted) notes.Add("in the certificate");
                parts.Add(c.Address + " on " + c.Adapter +
                          (notes.Count > 0 ? " (" + string.Join(", ", notes.ToArray()) + ")" : ""));
            }
            return string.Join("; ", parts.ToArray());
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
        // Dictation text never gets this far: only its length is logged.
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

    // What became of a chunk from the phone. The page is told, because words
    // that were not typed are words somebody has to say again.
    internal enum TypeResult
    {
        Typed,
        // This app's own window had the focus. The keys would land in the
        // panel, so nothing is sent.
        OwnWindow,
        // No window had the focus: the PC is locked, asleep, or between windows.
        NoWindow,
        // Windows dropped every key. A program running as administrator does
        // that to an app that is not (UIPI).
        Blocked,
        // Some keys went in before Windows dropped the rest.
        Partial
    }

    internal static class Injector
    {
        // One chunk at a time. Requests arrive on their own threads, and two
        // chunks typed at once would interleave their keys.
        private static readonly object Gate = new object();

        // No trailing space, and no option for one any more. The page streams
        // every partial result the moment it appears and owns the spacing,
        // because only it knows where the recogniser put a boundary; a space per
        // chunk doubled the spaces between words ("want" + "to" -> "want  to").
        public static TypeResult TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return TypeResult.Typed;
            lock (Gate)
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero)
                {
                    // It used to send anyway, and then blame an elevated window
                    // for the keys going nowhere.
                    Log.Quiet("SKIP: no window has the focus (locked, asleep, or switching)");
                    return TypeResult.NoWindow;
                }
                uint pid;
                Native.GetWindowThreadProcessId(fg, out pid);
                if (pid == ProcessIdHelper.Current)
                {
                    Log.Quiet("SKIP: the Dictation Bridge window has the focus");
                    return TypeResult.OwnWindow;
                }

                var buffer = new List<Native.INPUT>(text.Length * 2);
                foreach (char c in text)
                {
                    // Surrogate halves go through individually; Windows reassembles them.
                    buffer.Add(Unicode((ushort)c, false));
                    buffer.Add(Unicode((ushort)c, true));
                }

                int size = Marshal.SizeOf(typeof(Native.INPUT));
                uint sent = 0;
                const int perCall = 64;
                for (int i = 0; i < buffer.Count; i += perCall)
                {
                    int count = Math.Min(perCall, buffer.Count - i);
                    var chunk = new Native.INPUT[count];
                    buffer.CopyTo(i, chunk, 0, count);
                    uint done = Native.SendInput((uint)count, chunk, size);
                    sent += done;
                    // Stop at the first refusal. Carrying on would type the end of
                    // the sentence without its middle.
                    if (done != count) break;
                    Thread.Sleep(8);
                }

                if (sent == buffer.Count)
                {
                    // A count, not the words. The log is the file people are asked to
                    // attach to bug reports, and it used to carry the start of every
                    // sentence they had dictated.
                    Log.Quiet("typed " + text.Length + " char" + (text.Length == 1 ? "" : "s"));
                    return TypeResult.Typed;
                }
                Log.Write("WARN: SendInput sent " + sent + "/" + buffer.Count +
                          ". If the target app is elevated this is expected (UIPI blocks it).");
                return sent == 0 ? TypeResult.Blocked : TypeResult.Partial;
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
    }

    internal static class ProcessIdHelper
    {
        public static readonly uint Current = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    }

    // What the phone is doing, as far as its status polls say.
    internal enum PhoneState
    {
        // No poll for ten seconds: the page is closed, or the phone is locked.
        Away,
        // Polling, from a page too old to say whether it is listening.
        Connected,
        // Polling, with the Start button off.
        Idle,
        // Polling, with the Start button on.
        Listening
    }

    // Something the phone has to do before it can carry on, which the panel
    // says until a status poll shows it has been done. In order of weight: a
    // new certificate needs installing and scanning, a new address only
    // scanning.
    internal enum Notice
    {
        None,
        NewAddress,
        NewCertificate
    }

    internal sealed class Bridge
    {
        private readonly object _gate = new object();
        private Notice _notice;
        private int _typedCount;
        private int _missedCount;
        private string _recentTyped = "";
        private DateTime _lastSeen = DateTime.MinValue;
        private DateTime _lastRefused = DateTime.MinValue;
        private TypeResult _lastMiss = TypeResult.Typed;
        private DateTime _lastMissAt = DateTime.MinValue;
        private long _received;
        // What the newest poll said: true with the page's Start button on, false
        // with it off, null from a page that does not say.
        private bool? _phoneListening;

        // "Connected" means the phone polled recently. An open connection says
        // nothing: one can outlive the page that opened it by a good while. A
        // page polls whether it is listening or not, so the panel used to say
        // "listening on the phone" for a page that was only open; each poll now
        // says which.
        public PhoneState Phone
        {
            get
            {
                lock (_gate)
                {
                    if ((DateTime.UtcNow - _lastSeen).TotalSeconds >= 10) return PhoneState.Away;
                    if (!_phoneListening.HasValue) return PhoneState.Connected;
                    return _phoneListening.Value ? PhoneState.Listening : PhoneState.Idle;
                }
            }
        }

        // A rolling window rather than the last chunk. The phone streams partial
        // results as they are recognised, so the most recent arrival is often a
        // single word or character; showing that in the quote card would look like
        // a broken app. What is worth watching is the sentence filling in.
        public string LastText
        {
            get { lock (_gate) { return _recentTyped; } }
        }

        // A chunk went untyped in the last few seconds: what to tell the person
        // at the PC, short enough for the panel's status line. Null otherwise.
        public string RecentMiss
        {
            get
            {
                lock (_gate)
                {
                    if ((DateTime.UtcNow - _lastMissAt).TotalSeconds >= 8) return null;
                    return MissText(_lastMiss);
                }
            }
        }

        public static string MissText(TypeResult result)
        {
            switch (result)
            {
                case TypeResult.OwnWindow: return "not typed: this panel had focus";
                case TypeResult.NoWindow: return "not typed: no window in focus";
                case TypeResult.Blocked: return "not typed: window runs as admin";
                case TypeResult.Partial: return "partly typed: Windows refused";
                default: return null;
            }
        }

        // What the page gets in the reply, so it can say why.
        public static string ReasonCode(TypeResult result)
        {
            switch (result)
            {
                case TypeResult.OwnWindow: return "own-window";
                case TypeResult.NoWindow: return "no-window";
                case TypeResult.Blocked: return "blocked";
                case TypeResult.Partial: return "partial";
                default: return "typed";
            }
        }

        // Something asked without the right pairing key in the last half
        // minute. Usually a phone that was paired before "Reset phone pairing",
        // which is exactly when the panel should say to scan the code again.
        public bool RecentlyRefused
        {
            get { lock (_gate) { return (DateTime.UtcNow - _lastRefused).TotalSeconds < 30; } }
        }

        public void Refused()
        {
            lock (_gate) { _lastRefused = DateTime.UtcNow; }
            RaiseChanged();
        }

        // Cleared by the next status poll: a page that gets one through has
        // the certificate and the address, whatever it had before.
        public Notice PendingNotice
        {
            get { lock (_gate) { return _notice; } }
        }

        public void Notify(Notice notice)
        {
            lock (_gate)
            {
                if (notice > _notice) _notice = notice;
            }
            RaiseChanged();
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

        // A status poll. state is what the page says it is doing, "listening" or
        // "idle"; anything else, including nothing, leaves it unknown rather than
        // guessed.
        public void Touch(string state)
        {
            bool firstContact;
            lock (_gate)
            {
                firstContact = _lastSeen == DateTime.MinValue;
                _lastSeen = DateTime.UtcNow;
                _notice = Notice.None;
                _phoneListening = state == "listening" ? true
                    : state == "idle" ? false : (bool?)null;
            }
            if (firstContact) Log.Write("phone reached the page");
            RaiseChanged();
        }

        // The newest chunk number seen from each page, and what became of it. A
        // chunk can be typed and its reply still lost -- the Wi-Fi blinks, or the
        // page stops waiting -- and the page then sends it again, which used to
        // type it twice. Bounded, because every load of the page brings a new id.
        private sealed class Seen
        {
            public long Seq;
            // Null until typing finishes.
            public TypeResult? Result;
        }

        private readonly Dictionary<string, Seen> _seen = new Dictionary<string, Seen>();
        private const int MaxPages = 32;

        // There is no armed state and no buffer. The phone's Start button is the only
        // on/off switch: while the page is listening it sends, and whatever it
        // sends is typed straight into the focused window. That used to be gated
        // by a desktop toggle as well, which was a second gate for one decision --
        // and it is the reason a phone left face-up on a desk could pick up room
        // noise without spraying it at whatever window you were working in.
        //
        // repeat is set for a chunk seen already, which is not typed again; the
        // result is then what happened the first time, as far as is known. A chunk
        // with no page or number is typed whatever it is.
        public TypeResult OnText(string text, string page, long seq, out bool repeat)
        {
            repeat = false;
            Seen seen = null;
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(page) && seq > 0)
                {
                    Seen last;
                    if (_seen.TryGetValue(page, out last) && seq <= last.Seq)
                    {
                        // Older numbers too: the page sends one chunk at a time, so
                        // a number below the newest is a late copy of a try that
                        // timed out, and nobody is waiting for its answer.
                        repeat = true;
                        return seq == last.Seq && last.Result.HasValue
                            ? last.Result.Value : TypeResult.Typed;
                    }
                    if (last == null && _seen.Count >= MaxPages) _seen.Clear();
                    // Marked before typing, not after: a chunk that fails partway
                    // is better lost than typed again over the part that made it.
                    seen = new Seen { Seq = seq };
                    _seen[page] = seen;
                }
                _received++;
            }

            string keys = Typeable(text);
            TypeResult result = keys.Length == 0 ? TypeResult.Typed : Injector.TypeText(keys);
            lock (_gate)
            {
                if (seen != null) seen.Result = result;
                if (result == TypeResult.Typed)
                {
                    _typedCount++;
                    _recentTyped += keys;
                    // Keep the quote card a readable length rather than an
                    // ever-growing wall that has to re-layout on every chunk.
                    if (_recentTyped.Length > 200)
                    {
                        // Not between the halves of an emoji.
                        int cut = _recentTyped.Length - 200;
                        if (char.IsLowSurrogate(_recentTyped[cut])) cut++;
                        _recentTyped = _recentTyped.Substring(cut);
                    }
                }
                else
                {
                    _missedCount++;
                    _lastMiss = result;
                    _lastMissAt = DateTime.UtcNow;
                }
            }
            RaiseChanged();
            return result;
        }

        // Only characters that type as themselves. KEYEVENTF_UNICODE sends a
        // control character as a key, and programs act on it: a carriage return
        // or a line feed is Enter, which sends a half-finished chat message or
        // submits a form; a tab moves the focus; a backspace deletes. Line breaks
        // and tabs become a space, so the words either side stay apart, and the
        // rest is dropped, with any surrogate that has lost its other half.
        public static string Typeable(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
                if (c == '\r' || c == '\n' || c == '\t' || c == '\v' || c == '\f' ||
                    c == '\u0085' || c == '\u2028' || c == '\u2029')
                {
                    sb.Append(' ');
                }
                else if (c < 0x20 || (c >= 0x7f && c <= 0x9f))
                {
                    continue;
                }
                else if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c).Append(text[i + 1]);
                        i++;
                    }
                }
                else if (!char.IsLowSurrogate(c))
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // Counts only. The typed text itself used to ride along as "last", which
        // put the tail of everything said on the wire twice a second for a page
        // that never read it.
        public string StatusJson()
        {
            int typed;
            int missed;
            bool present;
            long received;
            lock (_gate)
            {
                typed = _typedCount;
                missed = _missedCount;
                received = _received;
                present = (DateTime.UtcNow - _lastSeen).TotalSeconds < 10;
            }
            return "{\"typed\":" + typed +
                   ",\"missed\":" + missed +
                   ",\"received\":" + received +
                   ",\"phone\":" + (present ? "true" : "false") + "}";
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
        public static int StartAll(Bridge bridge, int port, byte[] pfx, bool allowFallback)
        {
            // Imported once, not per connection: per-connection imports made
            // things worse, not better. Without one the server speaks plain
            // HTTP, and Secure says so.
            if (pfx != null)
            {
                try
                {
                    _cert = new X509Certificate2(
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
                Accept(listener, bridge);
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

        // How long a connection may go without sending a byte, the TLS handshake
        // included. There was no limit at all: anything that connected and said
        // nothing, or a phone that left the Wi-Fi halfway through a request, held
        // its thread until the app quit.
        private const int ReceiveMs = 20000;
        private const int SendMs = 10000;
        // What replies tell the browser. Shorter than ReceiveMs on purpose: a
        // browser that honours it retires an idle connection before this end
        // closes it, and closing first is the race that costs a request -- the
        // browser sends on a connection the server is in the middle of hanging up.
        private const int KeepAliveSeconds = 15;
        private const int MaxRequestsPerConnection = 1000;
        // A thread per connection is right for one phone, which opens two or
        // three. It is wrong for anything that opens thousands, so past this many
        // a new connection is closed as it arrives.
        private const int MaxConnections = 64;
        private static int _open;

        // The certificate a new connection is served with. It changes only when
        // the PC moves to an address the old one does not list; a connection
        // already open keeps the one it started with.
        private static volatile X509Certificate2 _cert;

        public static bool Secure
        {
            get { return _cert != null; }
        }

        // From the next connection on. True when it is not the certificate
        // served until now, which means the phone has to install it. Takes cert
        // either way. The old one is left to the connections still using it.
        public static bool UseCertificate(X509Certificate2 cert)
        {
            X509Certificate2 old = _cert;
            if (cert == null || old == null) return false;
            if (cert.Thumbprint == old.Thumbprint)
            {
                cert.Dispose();
                return false;
            }
            _cert = cert;
            Log.Write("serving certificate " + cert.Thumbprint);
            return true;
        }

        private static void Accept(TcpListener listener, Bridge bridge)
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
                    if (Interlocked.Increment(ref _open) > MaxConnections)
                    {
                        Interlocked.Decrement(ref _open);
                        Log.Quiet("closed a new connection from " + RemoteOf(client) + ": " +
                                  MaxConnections + " are open already");
                        try { client.Close(); } catch (Exception) { }
                        continue;
                    }
                    X509Certificate2 cert = _cert;
                    new Thread(() =>
                    {
                        try { Handle(client, bridge, cert); }
                        finally { Interlocked.Decrement(ref _open); }
                    }) { IsBackground = true }.Start();
                }
            }) { IsBackground = true }.Start();
        }

        private static string RemoteOf(TcpClient client)
        {
            try { return Host(client.Client.RemoteEndPoint.ToString()); }
            catch (Exception) { return "an unknown address"; }
        }

        // The page sends the pairing key in this header on every request. A
        // header rather than a body field: a GET has no body, and a browser will
        // not attach a custom header to a request from another site without first
        // asking this server, which never says yes.
        private const string KeyHeader = "X-Dictation-Key";
        // On each status poll: whether the page is listening, for the panel.
        private const string StateHeader = "X-Dictation-State";
        // Safari's request heads run to about a kilobyte.
        private const int MaxHead = 16384;
        private const int MaxBody = 65536;

        private sealed class Request
        {
            public string Method = "";
            public string Path = "/";
            public string Version = "HTTP/1.0";
            public string Remote = "";
            // Whether the client will send another request on this connection.
            public bool KeepAlive;
            public byte[] Body = new byte[0];
            public readonly Dictionary<string, string> Headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string Header(string name)
            {
                string value;
                return Headers.TryGetValue(name, out value) ? value : null;
            }

            public string BodyText()
            {
                return Encoding.UTF8.GetString(Body);
            }
        }

        private sealed class Reply
        {
            public int Status = 200;
            public string Type = "application/json";
            public string Body = "";
            // Extra header lines, each ending in \r\n.
            public string Headers = "";
        }

        private static Reply Json(int status, string body)
        {
            var reply = new Reply();
            reply.Status = status;
            reply.Body = body;
            return reply;
        }

        // A short code the page can act on, and nothing a stranger could learn
        // from.
        private static Reply Error(int status, string code)
        {
            return Json(status, "{\"error\":" + Bridge.Json(code) + "}");
        }

        private static Reply NotAllowed(string allow)
        {
            Reply reply = Error(405, "method");
            reply.Headers = "Allow: " + allow + "\r\n";
            return reply;
        }

        // A connection's stream, and whatever arrived past the end of the last
        // request. Browsers do not pipeline, but one read can still return the
        // start of the next request along with the end of this one, and those
        // bytes belong to it.
        private sealed class Wire
        {
            public Stream Stream;
            public byte[] Carry = new byte[0];
            // From the first byte of a request to its last, so a connection that
            // dropped partway through can be told from an idle one closing.
            public bool MidRequest;
        }

        private static void Handle(TcpClient client, Bridge bridge, X509Certificate2 cert)
        {
            Stream stream = null;
            var wire = new Wire();
            string remote = RemoteOf(client);
            try
            {
                client.NoDelay = true;
                // Before the handshake, so a client that connects and never says
                // hello cannot hold the thread either.
                client.ReceiveTimeout = ReceiveMs;
                client.SendTimeout = SendMs;
                stream = client.GetStream();
                if (cert != null)
                {
                    var ssl = new SslStream(stream, false);
                    stream = ssl;
                    try
                    {
                        ssl.AuthenticateAsServer(cert, false,
                            SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    }
                    catch (Exception e)
                    {
                        // Quiet: a phone that locks mid-handshake, or a scanner
                        // that connects and leaves, repeats this line exactly.
                        Log.Quiet("TLS failed from " + remote + ": " + e.Message);
                        return;
                    }
                }
                wire.Stream = stream;
                string scheme = cert != null ? "https" : "http";

                // Many requests to a connection. It used to close after every
                // reply, so each chunk of speech paid for a fresh TCP and TLS
                // handshake: several round trips on a phone's Wi-Fi before its
                // first character could be typed.
                for (int served = 1; ; served++)
                {
                    Reply reply;
                    Request request = ReadRequest(wire, out reply);
                    if (request == null)
                    {
                        // Refused before it was read in full, so the rest of it may
                        // still be on its way and the connection cannot be reused.
                        if (reply != null) Send(stream, reply, false);
                        else if (wire.MidRequest)
                        {
                            Log.Quiet("connection from " + remote + " ended mid-request");
                        }
                        return;
                    }
                    request.Remote = remote;

                    // Quiet, not Write: the phone polls /status every second or
                    // two and those repeats would bury the startup and crash
                    // lines. The client's ephemeral port is left out, or nothing
                    // would ever match and nothing would be suppressed.
                    Log.Quiet(request.Method + " " + request.Path + " from " + remote +
                              " body=" + request.Body.Length);
                    try
                    {
                        reply = Route(request, bridge, scheme);
                    }
                    catch (Exception e)
                    {
                        // Answered, not dropped. A dropped request is one the page
                        // sends again, and this one may have got partway.
                        Log.Detail(request.Method + " " + request.Path + " failed", e);
                        reply = Error(500, "failed");
                    }
                    bool keep = request.KeepAlive && served < MaxRequestsPerConnection;
                    Send(stream, reply, keep);
                    if (!keep) return;
                }
            }
            catch (IOException e)
            {
                // How a connection usually ends, now that it outlives a request:
                // the phone locked, left the Wi-Fi, or said nothing for ReceiveMs.
                // Worth a line only when it cut a request short.
                if (wire.MidRequest)
                {
                    Log.Quiet("connection from " + remote + " dropped mid-request: " + e.Message);
                }
            }
            catch (Exception e)
            {
                // Full detail: this is the catch that used to swallow a crash on a
                // per-connection thread down to a type name and a message.
                Log.Detail("connection error", e);
            }
            finally
            {
                if (stream != null)
                {
                    try { stream.Dispose(); } catch (Exception) { }
                }
                try { client.Close(); } catch (Exception) { }
            }
        }

        // One request off the connection. Null with no reply when the connection
        // ended or sent something not worth answering; null with a reply when it
        // is worth saying what was wrong, after which the connection is closed.
        private static Request ReadRequest(Wire wire, out Reply refusal)
        {
            refusal = null;
            var received = new MemoryStream();
            received.Write(wire.Carry, 0, wire.Carry.Length);
            wire.Carry = new byte[0];
            wire.MidRequest = received.Length > 0;
            var chunk = new byte[4096];
            int headEnd = FindHeadEnd(received.GetBuffer(), (int)received.Length, 0);
            while (headEnd < 0)
            {
                if (received.Length > MaxHead)
                {
                    refusal = Error(431, "head-too-large");
                    return null;
                }
                int n = wire.Stream.Read(chunk, 0, chunk.Length);
                if (n <= 0) return null;
                wire.MidRequest = true;
                int from = (int)received.Length;
                received.Write(chunk, 0, n);
                headEnd = FindHeadEnd(received.GetBuffer(), (int)received.Length, from);
            }

            byte[] all = received.ToArray();
            string head = Encoding.ASCII.GetString(all, 0, headEnd);
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2)
            {
                refusal = Error(400, "bad-request");
                return null;
            }

            var request = new Request();
            request.Method = first[0];
            string target = first[1];
            int query = target.IndexOf('?');
            request.Path = query >= 0 ? target.Substring(0, query) : target;
            if (first.Length >= 3) request.Version = first[2];
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string name = lines[i].Substring(0, colon).Trim();
                // The first copy wins. A request that repeats a header is either
                // broken or trying something, and neither deserves the second
                // say.
                if (!request.Headers.ContainsKey(name))
                {
                    request.Headers[name] = lines[i].Substring(colon + 1).Trim();
                }
            }
            request.KeepAlive = KeepsAlive(request);

            // Chunked bodies are not supported. Browsers never send one for a
            // fetch with a string body, and reading it as empty -- which is
            // what would happen otherwise -- fails with a misleading reason.
            if (request.Header("Transfer-Encoding") != null)
            {
                refusal = Error(411, "length-required");
                return null;
            }
            int want = 0;
            string declared = request.Header("Content-Length");
            if (declared != null && (!int.TryParse(declared, out want) || want < 0))
            {
                refusal = Error(400, "bad-length");
                return null;
            }
            if (want > MaxBody)
            {
                // Said out loud. An oversized body used to be read as an empty
                // one, so the request failed with a reason that had nothing to
                // do with it.
                refusal = Error(413, "too-large");
                return null;
            }

            var body = new MemoryStream();
            body.Write(all, headEnd, all.Length - headEnd);
            while (body.Length < want)
            {
                int n = wire.Stream.Read(chunk, 0, chunk.Length);
                if (n <= 0) return null;
                body.Write(chunk, 0, n);
            }
            byte[] buffer = body.GetBuffer();
            byte[] bytes = new byte[want];
            Array.Copy(buffer, bytes, want);
            request.Body = bytes;
            // Anything past the body is the start of the next request.
            int extra = (int)body.Length - want;
            if (extra > 0)
            {
                wire.Carry = new byte[extra];
                Array.Copy(buffer, want, wire.Carry, 0, extra);
            }
            wire.MidRequest = false;
            return request;
        }

        // Just past the blank line that ends a request head, or -1. Scans from
        // a little before the newest bytes, so a terminator split across two
        // reads is still found.
        private static int FindHeadEnd(byte[] data, int length, int from)
        {
            for (int i = Math.Max(3, from); i < length; i++)
            {
                if (data[i] == 10 && data[i - 1] == 13 && data[i - 2] == 10 && data[i - 3] == 13)
                {
                    return i + 1;
                }
            }
            return -1;
        }

        // HTTP/1.1 keeps a connection open unless told to close it; 1.0 closes
        // it unless asked to keep it.
        private static bool KeepsAlive(Request request)
        {
            bool close = false, keep = false;
            foreach (string token in (request.Header("Connection") ?? "").Split(','))
            {
                string option = token.Trim();
                if (option.Equals("close", StringComparison.OrdinalIgnoreCase)) close = true;
                if (option.Equals("keep-alive", StringComparison.OrdinalIgnoreCase)) keep = true;
            }
            if (close) return false;
            return request.Version == "HTTP/1.1" || keep;
        }

        private static Reply Route(Request request, Bridge bridge, string scheme)
        {
            string path = request.Path;
            if (path == "/" || path == "/index.html")
            {
                if (request.Method != "GET") return NotAllowed("GET");
                var page = new Reply();
                page.Type = "text/html; charset=utf-8";
                page.Body = ServedPage();
                // Nothing else gets to frame the page: it holds the pairing key.
                page.Headers = "Content-Security-Policy: frame-ancestors 'none'\r\n" +
                               "X-Frame-Options: DENY\r\n";
                return page;
            }
            if (path == "/favicon.ico" ||
                path.StartsWith("/apple-touch-icon", StringComparison.Ordinal))
            {
                // Safari asks for these on every visit, and a 404 apiece was the
                // most common line in the log. There is no icon to give it.
                var nothing = new Reply();
                nothing.Status = 204;
                return nothing;
            }
            if (path == "/config")
            {
                if (request.Method != "GET") return NotAllowed("GET");
                // The page version only, so a phone left open across a rebuild
                // notices and reloads itself. This used to carry the session
                // token as well, which handed it to anyone who asked.
                return Json(200, "{\"version\":" + Bridge.Json(PageVersion()) + "}");
            }

            bool status = path == "/status";
            bool speak = path == "/speak";
            bool diag = path == "/diag";
            if (!status && !speak && !diag)
            {
                var missing = new Reply();
                missing.Status = 404;
                missing.Type = "text/plain; charset=utf-8";
                missing.Body = "not found";
                return missing;
            }
            string method = status ? "GET" : "POST";
            if (request.Method != method) return NotAllowed(method);

            Reply refusal = Admit(request, bridge, scheme, !status);
            if (refusal != null) return refusal;

            if (status)
            {
                bridge.Touch(request.Header(StateHeader));
                return Json(200, bridge.StatusJson());
            }
            if (speak)
            {
                string body = request.BodyText();
                string spoken = Extract(body, "text");
                if (spoken == null) return Error(400, "no-text");
                // Which page sent it and its number there, so a chunk sent again
                // after a lost reply is recognised rather than typed twice.
                string page = Extract(body, "page");
                if (page != null && page.Length > 64) page = page.Substring(0, 64);
                long seq;
                if (!long.TryParse(Extract(body, "seq") ?? "",
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out seq))
                {
                    seq = 0;
                }
                bool repeat;
                TypeResult result = bridge.OnText(spoken, page, seq, out repeat);
                if (repeat)
                {
                    Log.Quiet("chunk " + seq + " arrived again from " + request.Remote +
                              "; not typed again");
                }
                // "typed" says whether the words reached a window, and "reason"
                // why not, so the phone can tell its user to say them again.
                return Json(200, "{\"ok\":true,\"typed\":" +
                                 (result == TypeResult.Typed ? "true" : "false") +
                                 (result == TypeResult.Typed ? "" :
                                     ",\"reason\":" + Bridge.Json(Bridge.ReasonCode(result))) +
                                 (repeat ? ",\"dup\":true" : "") + "}");
            }

            // Written verbatim to a diagnostics file so a failure that only
            // reproduces on the phone can be read back after the fact.
            Log.WriteDiag(request.BodyText());
            Log.Write("  diagnostics accepted from " + request.Remote + " (" +
                      request.Body.Length + " bytes)");
            return Json(200, "{\"ok\":true}");
        }

        // Everything that types, reports or reveals anything comes through here.
        // The key is what keeps a stranger out. The rest is belt and braces
        // against a web page open in some other tab on the same network, which
        // would not know the key either but has no business trying.
        private static Reply Admit(Request request, Bridge bridge, string scheme, bool hasBody)
        {
            string why = null;
            int status = 401;
            string origin = request.Header("Origin");
            string site = request.Header("Sec-Fetch-Site");
            string type = request.Header("Content-Type");
            string key = request.Header(KeyHeader);
            if (origin != null && !string.Equals(origin, scheme + "://" + request.Header("Host"),
                    StringComparison.OrdinalIgnoreCase))
            {
                status = 403;
                why = "sent from another site (" + Clip(origin) + ")";
            }
            else if (site == "cross-site" || site == "same-site")
            {
                status = 403;
                why = "sent from another site (" + site + ")";
            }
            else if (hasBody && (type == null || !type.TrimStart().StartsWith(
                         "application/json", StringComparison.OrdinalIgnoreCase)))
            {
                status = 415;
                why = "not JSON";
            }
            else if (!Pairing.Matches(key))
            {
                why = key == null ? "no pairing key" : "wrong pairing key";
            }

            if (why == null)
            {
                Strikes.Clear(request.Remote);
                Pairing.MarkUsed();
                return null;
            }

            // Checked only once a request has already failed, so a phone with
            // the right key is never held up by refusals from the same address.
            int wait = Strikes.BlockedFor(request.Remote);
            if (wait > 0)
            {
                Reply slow = Error(429, "slow-down");
                slow.Headers = "Retry-After: " + wait + "\r\n";
                return slow;
            }

            // Quiet: a phone left open after a reset asks every few seconds, and
            // one line about it is enough.
            Log.Quiet("refused " + request.Method + " " + request.Path + " from " +
                      request.Remote + ": " + why);
            if (Strikes.Add(request.Remote))
            {
                Log.Write("refusing " + request.Remote + " for " + Strikes.BlockSeconds +
                          "s after " + Strikes.Limit + " refused requests inside a minute");
            }
            if (status == 401) bridge.Refused();
            return Error(status, status == 401 ? "not-paired"
                : status == 403 ? "cross-site" : "not-json");
        }

        private static string Clip(string text)
        {
            return text.Length <= 80 ? text : text.Substring(0, 80) + "...";
        }

        // Refused requests per address. A 96-bit key cannot be guessed at any
        // rate a network allows, so this is not what keeps the key secret. It
        // stops one misbehaving device from filling the log, one refusal at a
        // time, for as long as it cares to keep asking.
        private static class Strikes
        {
            public const int Limit = 30;
            public const int BlockSeconds = 120;
            private const int MaxTracked = 256;

            private sealed class Entry
            {
                public DateTime WindowStart;
                public int Count;
                public DateTime BlockedUntil;
            }

            private static readonly Dictionary<string, Entry> Table =
                new Dictionary<string, Entry>();

            public static int BlockedFor(string host)
            {
                lock (Table)
                {
                    Entry entry;
                    if (!Table.TryGetValue(host, out entry)) return 0;
                    double left = (entry.BlockedUntil - DateTime.UtcNow).TotalSeconds;
                    return left > 0 ? (int)Math.Ceiling(left) : 0;
                }
            }

            // True for the refusal that starts a block.
            public static bool Add(string host)
            {
                DateTime now = DateTime.UtcNow;
                lock (Table)
                {
                    if (Table.Count >= MaxTracked) Prune(now);
                    Entry entry;
                    if (!Table.TryGetValue(host, out entry))
                    {
                        entry = new Entry { WindowStart = now };
                        Table[host] = entry;
                    }
                    if ((now - entry.WindowStart).TotalSeconds > 60)
                    {
                        entry.WindowStart = now;
                        entry.Count = 0;
                    }
                    entry.Count++;
                    if (entry.Count < Limit) return false;
                    entry.BlockedUntil = now.AddSeconds(BlockSeconds);
                    entry.WindowStart = now;
                    entry.Count = 0;
                    return true;
                }
            }

            public static void Clear(string host)
            {
                lock (Table) { Table.Remove(host); }
            }

            private static void Prune(DateTime now)
            {
                var stale = new List<string>();
                foreach (KeyValuePair<string, Entry> pair in Table)
                {
                    if ((now - pair.Value.WindowStart).TotalSeconds > 60 &&
                        pair.Value.BlockedUntil <= now)
                    {
                        stale.Add(pair.Key);
                    }
                }
                foreach (string host in stale) Table.Remove(host);
                // Still full means a flood of addresses. Start again rather than
                // grow without limit.
                if (Table.Count >= MaxTracked) Table.Clear();
            }
        }

        private static void Send(Stream stream, Reply reply, bool keep)
        {
            byte[] payload = Encoding.UTF8.GetBytes(reply.Body ?? "");
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(reply.Status).Append(' ')
                .Append(Reason(reply.Status)).Append("\r\n");
            if (reply.Status != 204)
            {
                head.Append("Content-Type: ").Append(reply.Type).Append("\r\n");
                head.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            }
            // no-store alone is not reliably honoured on iOS: Safari keeps a copy
            // in its back/forward cache and will re-serve it after the desktop
            // rebuilds the page. Spell it out every way.
            head.Append("Cache-Control: no-store, no-cache, must-revalidate, max-age=0\r\n");
            head.Append("Pragma: no-cache\r\n");
            head.Append("Expires: 0\r\n");
            // There is no Access-Control-Allow-Origin any more. It used to be "*",
            // which let any web page on any device read /status -- including the
            // tail of what had just been typed.
            head.Append("X-Content-Type-Options: nosniff\r\n");
            head.Append("Referrer-Policy: no-referrer\r\n");
            head.Append(reply.Headers);
            head.Append(keep
                ? "Connection: keep-alive\r\nKeep-Alive: timeout=" + KeepAliveSeconds + "\r\n\r\n"
                : "Connection: close\r\n\r\n");

            // One write, so a small reply goes out as one TLS record rather than
            // two: every status poll is one of these.
            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            var whole = new byte[headBytes.Length + payload.Length];
            Buffer.BlockCopy(headBytes, 0, whole, 0, headBytes.Length);
            Buffer.BlockCopy(payload, 0, whole, headBytes.Length, payload.Length);
            stream.Write(whole, 0, whole.Length);
            stream.Flush();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 415: return "Unsupported Media Type";
                case 429: return "Too Many Requests";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Error";
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

        // The page as it goes to the phone: the embedded copy with its own version
        // written into it. A copy Safari kept from an older build can then tell it
        // is stale the moment it opens, by comparing that with /config, instead of
        // only noticing when the desktop changes while it is open. See checkVersion()
        // in the page. The version is still the hash of the page as built, so it
        // does not depend on itself.
        private const string VersionMark = "__PAGE_VERSION__";
        private static string _served;

        public static string ServedPage()
        {
            if (_served != null) return _served;
            string page = Page();
            int at = page.IndexOf(VersionMark, StringComparison.Ordinal);
            _served = at < 0 ? page
                : page.Substring(0, at) + PageVersion() + page.Substring(at + VersionMark.Length);
            return _served;
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
            // Not focusable, like everything else on the panel; see PaintDot.
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
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

    // The panel's palette and type. Light, because the Windows apps that sit on
    // top of your work all tend to be dark, and a dark block on a light document
    // is the thing your eye snags on.
    internal static class Skin
    {
        public static readonly Color Surface = Color.FromArgb(255, 255, 255);
        public static readonly Color Field = Color.FromArgb(243, 244, 247);
        public static readonly Color FieldHot = Color.FromArgb(232, 235, 241);
        public static readonly Color FieldDown = Color.FromArgb(220, 224, 232);
        public static readonly Color Ink = Color.FromArgb(32, 35, 42);
        public static readonly Color InkSoft = Color.FromArgb(108, 115, 128);
        public static readonly Color InkFaint = Color.FromArgb(160, 166, 178);
        public static readonly Color Accent = Color.FromArgb(24, 105, 220);
        public static readonly Color AccentSoft = Color.FromArgb(232, 240, 254);
        public static readonly Color Good = Color.FromArgb(22, 138, 74);
        public static readonly Color Warn = Color.FromArgb(184, 122, 0);
        public static readonly Color Danger = Color.FromArgb(197, 48, 48);
        public static readonly Color ShadowEdge = Color.FromArgb(216, 220, 228);

        // Made once and shared. These used to be properties that built a new Font
        // on every read, so each control that asked got one of its own, and none
        // of them was ever disposed.
        public static readonly Font Ui = new Font("Segoe UI", 9F);
        public static readonly Font UiSmall = new Font("Segoe UI", 8.25F);
        public static readonly Font Quote = new Font("Segoe UI", 10F);

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

        // DrawString's placement for a ContentAlignment, which has one bit per
        // cell: the rows are 0x7, 0x70 and 0x700, the columns 0x111, 0x222 and
        // 0x444. Wrapped text keeps whole lines only, so a line that does not fit
        // is left out rather than cut through the middle.
        public static StringFormat Format(ContentAlignment align, bool wrap)
        {
            int a = (int)align;
            var f = new StringFormat();
            f.LineAlignment = (a & 0x7) != 0 ? StringAlignment.Near
                : (a & 0x700) != 0 ? StringAlignment.Far : StringAlignment.Center;
            f.Alignment = (a & 0x111) != 0 ? StringAlignment.Near
                : (a & 0x444) != 0 ? StringAlignment.Far : StringAlignment.Center;
            f.FormatFlags = wrap ? StringFormatFlags.LineLimit
                : StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            f.Trimming = StringTrimming.EllipsisCharacter;
            return f;
        }
    }

    // Small self-painted pieces. Doing these by hand is what stops the panel
    // looking like a stack of grey WinForms rectangles.
    //
    // None of them can take the focus, and nothing else may be added to the panel
    // that can. WinForms focuses a selectable control when it is clicked, focusing
    // it activates the panel, and typing is refused while the panel is the window
    // in front (TypeResult.OwnWindow). See MainForm.
    internal sealed class PaintDot : Control
    {
        private Color _color = Skin.InkFaint;
        private bool _breathe;
        private int _phase;

        public PaintDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            // Explicit, not Color.Transparent: a custom-painted control given
            // that back colour throws "not a valid owner window handle" unless
            // it is parented first, and once parented it still routes through
            // the layered path that aliases text.
            BackColor = Skin.Surface;
            Size = new Size(12, 12);
        }

        // Repaints only on a change: the panel refreshes several times a second,
        // and the dot is nearly always what it was.
        public void Set(Color color, bool breathe)
        {
            if (color == _color && breathe == _breathe) return;
            _color = color;
            _breathe = breathe;
            Invalidate();
        }

        // One step of the breath, from the panel's pulse timer. A dot that is not
        // breathing is left alone.
        public void Step()
        {
            _phase = (_phase + 1) % 4;
            if (_breathe) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = 1F;
            if (_breathe)
            {
                // 4 -> 5 -> 6 -> 5, a calm breathing ring rather than a blink.
                int[] radii = { 4, 5, 6, 5 };
                scale = radii[_phase] / 4F;
            }
            float cx = Width / 2F, cy = Height / 2F;
            float r = 3.2F * scale;
            if (scale > 1.05F)
            {
                using (Pen halo = new Pen(Color.FromArgb(70, _color)))
                {
                    halo.Width = 1.6F;
                    g.DrawEllipse(halo, cx - r - 2.2F, cy - r - 2.2F, (r + 2.2F) * 2, (r + 2.2F) * 2);
                }
            }
            using (SolidBrush b = new SolidBrush(_color))
            {
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            }
        }
    }

    internal sealed class PaintText : Control
    {
        private string _caption = "";
        private Color _ink = Skin.InkSoft;
        private bool _link;
        private bool _hot;
        private Font _underlined;
        public Font TextFont = Skin.UiSmall;
        public ContentAlignment Align = ContentAlignment.MiddleLeft;
        // Wraps onto as many lines as fit, rather than trimming to one.
        public bool Wrap;

        public PaintText()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            // An explicit parent colour rather than Color.Transparent, which
            // throws before parenting and, on a parent, still forces the
            // layered rendering path that makes text jagged.
            BackColor = Skin.Surface;
        }

        // Text that does something when it is clicked: the hand cursor, and an
        // underline while the mouse is over it.
        public bool Link
        {
            get { return _link; }
            set
            {
                _link = value;
                Cursor = value ? Cursors.Hand : Cursors.Default;
            }
        }

        public void Set(string text, Color color)
        {
            text = text ?? "";
            if (text == _caption && color == _ink) return;
            _caption = text;
            _ink = color;
            AccessibleName = text;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            if (_link)
            {
                _hot = true;
                Invalidate();
            }
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot)
            {
                _hot = false;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Font font = TextFont;
            if (_hot)
            {
                if (_underlined == null)
                {
                    _underlined = new Font(TextFont, TextFont.Style | FontStyle.Underline);
                }
                font = _underlined;
            }
            // Graphics.DrawString wants a StringFormat, not TextFormatFlags.
            using (StringFormat f = Skin.Format(Align, Wrap))
            using (SolidBrush b = new SolidBrush(_ink))
            {
                e.Graphics.DrawString(_caption, font, b, new RectangleF(0, 0, Width, Height), f);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _underlined != null)
            {
                _underlined.Dispose();
                _underlined = null;
            }
            base.Dispose(disposing);
        }
    }

    // A rounded card.
    internal sealed class PaintCard : Control
    {
        public Color Fill = Skin.Field;
        public int Corner = 8;

        public PaintCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Skin.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Skin.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Corner))
            using (SolidBrush b = new SolidBrush(Fill))
            {
                g.FillPath(b, path);
            }
        }
    }

    // What was typed most recently, as a quote. Painted, where it used to be a
    // read-only TextBox: a TextBox shows the start of its text and hides the rest,
    // and the words worth seeing are the newest ones, at the end. The TextBox
    // could also take the focus, which stopped the typing it was there to show.
    //
    // The words are drawn with TextRenderer, which is GDI, as the TextBox was,
    // and not Graphics.DrawString like the rest of the panel. They are mostly
    // Chinese, and GDI follows the system's font links from Segoe UI to its
    // Chinese faces; that is the drawing path every other window on the PC
    // shows them with.
    internal sealed class PaintQuote : Control
    {
        private const string Ellipsis = "\u2026";
        // One line at a time. NoPrefix, or an "&" in what was said would
        // underline the next letter; NoPadding, so widths add up to the box's.
        private const TextFormatFlags OneLine = TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.Left;
        private static readonly Size Unbounded = new Size(int.MaxValue, int.MaxValue);
        // Closing punctuation stays at the end of the line it closes.
        private const string NoLineStart = "\uFF0C\u3002\u3001\uFF1B\uFF1A\uFF01\uFF1F\uFF09\u300D\u300F\u3009\u300B\u3011\u3015,.;:!?)";

        private string _text = "";
        // Shown, faintly, until something has been typed: an empty grey box
        // looked like a part of the panel that had failed to load.
        public string Placeholder = "";
        // The last layout, kept until the text or the width changes.
        private List<string> _lines;
        private string _linesFor;
        private Size _linesBox;

        public PaintQuote()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Skin.Surface;
        }

        public void SetText(string text)
        {
            text = text ?? "";
            if (text == _text) return;
            _text = text;
            AccessibleName = text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            string text = _text.Trim();
            bool empty = text.Length == 0;
            using (GraphicsPath card = Skin.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            using (SolidBrush fill = new SolidBrush(empty ? Skin.Field : Skin.AccentSoft))
            {
                g.FillPath(fill, card);
            }
            if (empty)
            {
                using (StringFormat f = Skin.Format(ContentAlignment.MiddleCenter, true))
                using (SolidBrush ink = new SolidBrush(Skin.InkFaint))
                {
                    g.DrawString(Placeholder, Skin.UiSmall, ink, new RectangleF(8, 0, Width - 16, Height), f);
                }
                return;
            }
            // A quote bar inside the card, clear of its rounded corners. It used
            // to run the card's full height, square, and poke out past them.
            if (Height > 20)
            {
                using (GraphicsPath bar = Skin.RoundedPath(new Rectangle(10, 9, 3, Height - 19), 1))
                using (SolidBrush accent = new SolidBrush(Skin.Accent))
                {
                    g.FillPath(accent, bar);
                }
            }
            // Three lines, even in a Chinese face a couple of pixels taller.
            Rectangle box = new Rectangle(21, 3, Math.Max(1, Width - 31), Math.Max(1, Height - 6));
            // Measured with a Chinese character in it: the face GDI links to
            // for Chinese stands taller than Segoe UI.
            int lineHeight = Math.Max(1, TextRenderer.MeasureText(g, "Ag\u4E2D", Skin.Quote, Unbounded, OneLine).Height);
            if (_lines == null || _linesFor != text || _linesBox != box.Size)
            {
                _lines = LinesOf(g, text, Skin.Quote, box.Width, Math.Max(1, box.Height / lineHeight));
                _linesFor = text;
                _linesBox = box.Size;
            }
            int top = box.Y + (box.Height - _lines.Count * lineHeight) / 2;
            foreach (string line in _lines)
            {
                TextRenderer.DrawText(g, line, Skin.Quote, new Rectangle(box.X, top, box.Width, lineHeight),
                    Skin.Ink, OneLine | TextFormatFlags.NoClipping);
                top += lineHeight;
            }
        }

        // The lines to show. Wrapped from the start, the ordinary way, when it all
        // fits; when it does not, laid out from the end instead, so that the
        // newest words are the ones on show and the oldest go behind an
        // ellipsis. Done here rather than by GDI's word wrap, which only lays out
        // from the start, and which cannot break a run with no spaces in it -- an
        // address, or a sentence of Chinese where it finds no break -- so it
        // shows the run's beginning, cut off at the right.
        internal static List<string> LinesOf(IDeviceContext g, string text, Font font, int width, int maxLines)
        {
            var lines = new List<string>();
            int start = 0;
            while (start < text.Length && lines.Count <= maxLines)
            {
                int end = BreakBefore(text, start, FitTo(g, text, start, font, width));
                lines.Add(text.Substring(start, end - start).Trim());
                start = end;
                while (start < text.Length && text[start] == ' ') start++;
            }
            if (lines.Count <= maxLines) return lines;

            lines.Clear();
            int stop = text.Length;
            while (stop > 0 && lines.Count < maxLines)
            {
                bool first = lines.Count == maxLines - 1;
                int from = FitFrom(g, text, stop, font, width, "");
                if (from > 0 && first) from = FitFrom(g, text, stop, font, width, Ellipsis);
                from = BreakAfter(text, from, stop);
                string line = text.Substring(from, stop - from).Trim();
                lines.Insert(0, from > 0 && first ? Ellipsis + line : line);
                stop = from;
                while (stop > 0 && text[stop - 1] == ' ') stop--;
            }
            return lines;
        }

        private static int WidthOf(IDeviceContext g, string s, Font font)
        {
            return s.Length == 0 ? 0 : TextRenderer.MeasureText(g, s, font, Unbounded, OneLine).Width;
        }

        // The furthest a line from start can run and still fit; one character
        // at least, so a layout always gets somewhere.
        private static int FitTo(IDeviceContext g, string text, int start, Font font, int width)
        {
            if (WidthOf(g, text.Substring(start), font) <= width) return text.Length;
            int lo = start, hi = text.Length;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (WidthOf(g, text.Substring(start, mid - start), font) <= width) lo = mid;
                else hi = mid;
            }
            return Math.Max(lo, start + 1);
        }

        // The earliest a line ending at stop can begin, with prefix in front of
        // it, and still fit.
        private static int FitFrom(IDeviceContext g, string text, int stop, Font font, int width, string prefix)
        {
            if (WidthOf(g, prefix + text.Substring(0, stop), font) <= width) return 0;
            int lo = 0, hi = stop;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (WidthOf(g, prefix + text.Substring(mid, stop - mid), font) <= width) hi = mid;
                else lo = mid;
            }
            return Math.Min(hi, stop - 1);
        }

        // Where a line that fits from start to end should stop: at a space if one
        // is near, or else wherever two characters may be parted. Chinese has no
        // spaces and is parted between any two characters, as it is everywhere.
        private static int BreakBefore(string text, int start, int end)
        {
            if (end >= text.Length) return text.Length;
            if (text[end] == ' ' || text[end - 1] == ' ') return end;
            int space = text.LastIndexOf(' ', end - 1, end - start);
            if (space > start && end - space <= (end - start) / 3) return space;
            int at = end;
            while (at > start + 1 && !CanPart(text, at)) at--;
            if (CanPart(text, at)) return at;
            // Nowhere inside the line: run on to the next place there is.
            at = end;
            while (at < text.Length && !CanPart(text, at)) at++;
            return at;
        }

        // The same from the other end: where a line that fits from from to stop
        // should begin.
        private static int BreakAfter(string text, int from, int stop)
        {
            if (from <= 0) return 0;
            if (text[from] == ' ' || text[from - 1] == ' ') return from;
            int space = text.IndexOf(' ', from, stop - from);
            if (space >= 0 && space + 1 < stop && space + 1 - from <= (stop - from) / 3) return space + 1;
            int at = from;
            while (at < stop - 1 && !CanPart(text, at)) at++;
            if (CanPart(text, at)) return at;
            at = from;
            while (at > 0 && !CanPart(text, at)) at--;
            return at;
        }

        // Whether a line may end just before text[i]. Not inside an emoji -- the
        // halves of a surrogate pair, a joiner, a variation selector or a skin
        // tone -- nor before a combining mark or closing punctuation.
        private static bool CanPart(string text, int i)
        {
            if (i <= 0 || i >= text.Length) return true;
            char c = text[i];
            char before = text[i - 1];
            if (char.IsLowSurrogate(c) || c == '\u200D' || before == '\u200D') return false;
            if (c >= '\uFE00' && c <= '\uFE0F') return false;
            if (c == '\uD83C' && i + 1 < text.Length && text[i + 1] >= '\uDFFB' && text[i + 1] <= '\uDFFF')
            {
                return false;
            }
            System.Globalization.UnicodeCategory kind = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (kind == System.Globalization.UnicodeCategory.NonSpacingMark
                || kind == System.Globalization.UnicodeCategory.SpacingCombiningMark
                || kind == System.Globalization.UnicodeCategory.EnclosingMark)
            {
                return false;
            }
            return NoLineStart.IndexOf(c) < 0;
        }
    }

    internal enum ButtonGlyph
    {
        None,
        ChevronDown,
        ChevronUp
    }

    // A flat button that paints its own rounded shape, hover and press. It never
    // takes the focus; see PaintDot.
    internal sealed class SoftButton : Button
    {
        public Color Fill = Skin.Field;
        public Color Hover = Skin.FieldHot;
        public Color Pressed = Skin.FieldDown;
        public Color Ink = Skin.Ink;
        public int Corner = 7;
        // Drawn instead of the text when set. A chevron drawn with a pen comes out
        // the same in every font; a "+" or "_" in Segoe UI sat off centre.
        public ButtonGlyph Glyph = ButtonGlyph.None;
        private bool _hot;
        private bool _down;

        public SoftButton()
        {
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Cursor = Cursors.Hand;
            BackColor = Skin.Surface;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hot = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = false;
            _down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _down = true;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            // Hover used to be keyed to IsDefault, which a panel that never has
            // the focus never sets, so it did not show at all.
            Color fill = !Enabled ? Fill : _down ? Pressed : _hot ? Hover : Fill;
            Color ink = Enabled ? Ink : Skin.InkFaint;

            g.Clear(BackColor);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Skin.RoundedPath(r, Corner))
            using (SolidBrush b = new SolidBrush(fill))
            {
                g.FillPath(b, p);
            }

            if (Glyph != ButtonGlyph.None)
            {
                float cx = Width / 2F, cy = Height / 2F;
                // Down points down: the panel opens downwards.
                float dy = Glyph == ButtonGlyph.ChevronDown ? 2.25F : -2.25F;
                using (Pen pen = new Pen(ink, 1.6F))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx - 4.5F, cy - dy),
                        new PointF(cx, cy + dy),
                        new PointF(cx + 4.5F, cy - dy)
                    });
                }
                return;
            }

            // Graphics.DrawString needs a StringFormat rather than TextFormatFlags.
            using (StringFormat f = Skin.Format(ContentAlignment.MiddleCenter, false))
            using (SolidBrush b = new SolidBrush(ink))
            {
                g.DrawString(Text, Font, b, new RectangleF(r.X, r.Y, r.Width, r.Height), f);
            }
        }
    }

    // What the strip and the tray icon say, decided in one place so the two
    // cannot disagree. The first match wins.
    internal sealed class PanelStatus
    {
        public readonly string Strip;
        public readonly string Tip;
        public readonly Color Ink;
        public readonly Color Dot;
        public readonly bool Breathe;

        private PanelStatus(string strip, string tip, Color ink, Color dot, bool breathe)
        {
            Strip = strip;
            Tip = tip;
            Ink = ink;
            Dot = dot;
            Breathe = breathe;
        }

        public static PanelStatus Of(Bridge bridge)
        {
            // Words that did not land come first: it is the one thing here that
            // asks the person at the PC to do something now.
            string miss = bridge.RecentMiss;
            if (miss != null) return new PanelStatus(miss, miss, Skin.Warn, Skin.Warn, false);
            // Then whatever the phone has to catch up with. Ahead of the phone's
            // own state, which comes from polls made before the change and says
            // nothing about whether the phone can carry on after it.
            switch (bridge.PendingNotice)
            {
                case Notice.NewCertificate:
                    return new PanelStatus("new certificate: install it on the phone",
                        "new certificate, install it on the phone", Skin.Warn, Skin.Warn, false);
                case Notice.NewAddress:
                    return new PanelStatus("new address: scan the QR code again",
                        "new address, scan the QR code again", Skin.Warn, Skin.Warn, false);
            }
            switch (bridge.Phone)
            {
                case PhoneState.Listening:
                    return new PanelStatus("listening on the phone", "listening on the phone",
                        Skin.Good, Skin.Good, true);
                case PhoneState.Idle:
                    return new PanelStatus("phone connected: tap Start listening",
                        "phone connected, not listening", Skin.InkSoft, Skin.Accent, false);
                case PhoneState.Connected:
                    return new PanelStatus("phone connected", "phone connected",
                        Skin.InkSoft, Skin.Accent, false);
            }
            if (bridge.RecentlyRefused)
            {
                return new PanelStatus("phone not paired: scan the QR code",
                    "phone not paired, scan the QR code", Skin.Warn, Skin.Warn, false);
            }
            if (!Pairing.Used)
            {
                return new PanelStatus("scan the QR code to pair", "scan the QR code to pair",
                    Skin.Warn, Skin.InkFaint, false);
            }
            return new PanelStatus("phone not connected: open the page", "phone not connected",
                Skin.InkSoft, Skin.InkFaint, false);
        }
    }

    // A small always-on-top panel, in the shape of the floating helpers people
    // already keep beside their work: a status strip, with the QR code and the
    // newest words a click away. Borderless and draggable so it never gets in
    // the way.
    //
    // It never takes the focus. Words are typed into whichever window has the
    // focus, and the one window they must never go to is this one, so a click on
    // the panel used to be enough to stop dictation: the click activated the
    // panel, the next chunk was refused as OwnWindow, and the user had to click
    // back into their document to carry on. WS_EX_NOACTIVATE and MA_NOACTIVATE
    // stop a click activating it, ShowWithoutActivation stops showing it doing
    // so, and none of the controls on it is selectable. The menu is the one
    // exception, and it hands the focus back; see ShowMenu.
    internal sealed class MainForm : Form
    {
        // Tag for a menu item that opens a window of its own, which should keep
        // the focus when the menu closes rather than hand it back.
        public static readonly object KeepsFocus = new object();

        private readonly Bridge _bridge;
        // Changes when the PC moves to another address while running.
        private string _url;
        private readonly ContextMenuStrip _panelMenu;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _pulse;
        private readonly System.Windows.Forms.Timer _copied;
        private readonly ToolTip _tips;

        // Not readonly: these are built by BuildUi, which is called from the
        // constructor but is not itself one.
        private Panel _strip;
        private Panel _detail;
        private Panel _body;
        private SoftButton _collapse;
        private SoftButton _copyButton;
        private PaintDot _lamp;
        private PaintText _phoneState;
        private PaintQuote _quote;
        private QrView _qr;
        private PaintText _address;
        private PaintText _caption;

        private Point _dragOrigin;
        private bool _dragging;
        private bool _expanded;
        // Expanded for the phone's sake: until a phone pairs, or catches up with
        // a new address or certificate. Folded away once, when it has.
        private bool _holdOpen;
        private int _refreshQueued;
        // Where the focus was before the menu took it, and whether the item that
        // was chosen wants to keep it.
        private IntPtr _focusBeforeMenu = IntPtr.Zero;
        private bool _menuKeepsFocus;

        // One place that owns the sizes, so expand and collapse cannot disagree.
        private static readonly Size CollapsedSize = new Size(320, 56);
        private static readonly Size ExpandedSize = new Size(320, 326);
        private const int StripHeight = 48;
        // The QR code and the card around it. The pairing key makes the code one
        // size step bigger, 41 modules with the quiet zone; 124px keeps every
        // module three whole pixels wide, which a phone camera resolves easily.
        private const int QrSide = 124;
        private const int QrInset = 8;
        private const int UrlCardHeight = QrSide + QrInset * 2;
        // The quote has the rest of the height above the buttons. It used to be
        // shorter, over 24 pixels of nothing.
        private const int QuoteRowHeight = 76;
        private const int FooterHeight = 44;
        private const string CopyLabel = "Copy pairing link";
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

            // ShowAlways, or a tip shows only while the panel is the active
            // window, which it never is.
            _tips = new ToolTip { ShowAlways = true, InitialDelay = 500 };

            BuildUi(this);
            UsePanelMenu(this);
            ResizeRounded();
            Resize += (s, e) => ResizeRounded();
            if (_panelMenu != null)
            {
                _panelMenu.ItemClicked += OnMenuItemClicked;
                _panelMenu.Closed += OnMenuClosed;
            }

            _bridge.Changed += RefreshSoon;
            _timer = new System.Windows.Forms.Timer { Interval = 700 };
            _timer.Tick += (s, e) =>
            {
                WatchPhone();
                Refresh2();
            };
            _timer.Start();

            // A slow breath on the lamp while the phone is listening, so it is
            // obvious at a glance without reading anything.
            _pulse = new System.Windows.Forms.Timer { Interval = 900 };
            _pulse.Tick += (s, e) => _lamp.Step();
            _pulse.Start();

            // Puts the copy button's label back after it has said "Copied".
            _copied = new System.Windows.Forms.Timer { Interval = 1500 };
            _copied.Tick += (s, e) =>
            {
                _copied.Stop();
                _copyButton.Text = CopyLabel;
                _copyButton.Ink = Skin.Ink;
                _copyButton.Invalidate();
            };

            FormClosed += (s, e) =>
            {
                _bridge.Changed -= RefreshSoon;
                if (_panelMenu != null)
                {
                    _panelMenu.ItemClicked -= OnMenuItemClicked;
                    _panelMenu.Closed -= OnMenuClosed;
                }
                _timer.Stop();
                _pulse.Stop();
                _copied.Stop();
                _tips.Dispose();
                SavePosition();
            };
            LoadPosition();
            Refresh2();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // TOOLWINDOW as well, which keeps it out of Alt+Tab, the one other
                // way it could be handed the focus by accident.
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // The click still reaches the control under the mouse; the window in
            // front stays in front.
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)Native.MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        // Shows the panel on top of everything without taking the focus. Show()
        // alone puts it back where it was in the z-order, which can be under
        // another window that is always on top.
        public void ShowQuietly()
        {
            Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
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
                // The spot was saved for the collapsed strip, and the panel may
                // be opening expanded, on the QR code.
                ClampIntoView();
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
            // The lamp, what the phone is doing, and the button for the details.
            // Placed by hand, in LayOutStrip: the button used to be docked to the
            // right, which stretched it into a grey slab the height of the strip
            // that the panel's rounded corner then cut into.
            _strip = new Panel
            {
                Dock = DockStyle.Top,
                Height = StripHeight,
                BackColor = Skin.Surface
            };

            _lamp = new PaintDot { Location = new Point(12, (StripHeight - 12) / 2) };

            // Where the on/off switch is, rather than a second one: the phone's
            // Start button is the only switch.
            _phoneState = new PaintText
            {
                Location = new Point(30, (StripHeight - 24) / 2),
                Size = new Size(200, 24),
                TextFont = Skin.UiSmall,
                Align = ContentAlignment.MiddleLeft
            };

            _collapse = new SoftButton
            {
                Size = new Size(28, 28),
                Fill = Skin.Surface,
                Ink = Skin.InkSoft,
                Corner = 8
            };
            _collapse.Click += (s, e) => SetExpanded(!_expanded);

            _strip.Controls.Add(_lamp);
            _strip.Controls.Add(_phoneState);
            _strip.Controls.Add(_collapse);
            _strip.Resize += (s, e) => LayOutStrip();

            // ---- everything below the strip --------------------------------
            _detail = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Skin.Surface,
                Padding = new Padding(14, 4, 14, 10)
            };

            // The QR code, on a soft card with the address beside it. Scanning
            // beats typing a URL into a phone keyboard, and the address changes
            // every time the machine lands on a new network, so it has to be
            // shown, not written down.
            var urlCard = new Panel
            {
                Dock = DockStyle.Top,
                Height = UrlCardHeight,
                BackColor = Skin.Surface
            };
            var urlBg = new PaintCard
            {
                Dock = DockStyle.Fill,
                Fill = Skin.Field,
                Corner = 8
            };

            // The code carries the pairing link, not the bare address: scanning
            // it is what pairs the phone.
            _qr = new QrView
            {
                Location = new Point(QrInset, QrInset),
                Size = new Size(QrSide, QrSide)
            };
            _qr.SetAddress(Pairing.Link(_url));

            // The address is still shown, because it is what to check when the
            // phone cannot connect, but the caption says what actually pairs.
            // Both wrap. They used to share a LinkLabel, which cut a longer
            // address off at both ends and pushed the caption out of sight.
            var caption = new PaintText
            {
                TextFont = Skin.UiSmall,
                Align = ContentAlignment.BottomCenter,
                Wrap = true,
                BackColor = Skin.Field
            };
            caption.Set(CaptionText(), Skin.InkSoft);
            _caption = caption;
            var address = new PaintText
            {
                TextFont = Skin.UiSmall,
                Align = ContentAlignment.TopCenter,
                Wrap = true,
                Link = true,
                BackColor = Skin.Field
            };
            address.Set(_url, Skin.Accent);
            _address = address;
            // MouseClick, not Click, which a right-click raises as well.
            address.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) CopyPairingLink();
            };
            _tips.SetToolTip(address, "Copy the pairing link");
            urlCard.Resize += (s, e) =>
            {
                // Absolute coordinates inside a docked panel do not shrink that
                // panel's minimum size, so the text is kept in step with the card
                // here rather than relying on Dock.
                int left = QrInset + QrSide + 6;
                int width = Math.Max(0, urlCard.ClientSize.Width - left - 8);
                caption.SetBounds(left, 18, width, 50);
                address.SetBounds(left, 74, width, 48);
            };

            urlCard.Controls.Add(address);
            urlCard.Controls.Add(caption);
            urlCard.Controls.Add(_qr);
            urlCard.Controls.Add(urlBg);
            urlBg.SendToBack();

            // What was typed most recently, filling in as it arrives.
            var lastRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = QuoteRowHeight,
                BackColor = Skin.Surface,
                Padding = new Padding(0, 8, 0, 2)
            };
            _quote = new PaintQuote
            {
                Dock = DockStyle.Fill,
                Placeholder = "What you say appears here as it is typed"
            };
            lastRow.Controls.Add(_quote);

            // ---- footer ----------------------------------------------------
            // Just the two buttons that still do something. The queue list, the
            // hold-until-armed checkbox and the hotkey row all went with the
            // arming model, and a row of disabled-looking furniture would be worse
            // than less.
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = FooterHeight,
                BackColor = Skin.Surface,
                ColumnCount = 2,
                RowCount = 1
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, FooterHeight));

            _copyButton = SoftButtonOf(CopyLabel, (s, e) => CopyPairingLink());
            _copyButton.Margin = new Padding(0, 6, 4, 6);
            // Close() alone is swallowed by hide-to-tray, so quit is routed back
            // to the tray context which knows how to really exit.
            var quitButton = SoftButtonOf("Quit", (s, e) => Quit());
            quitButton.Ink = Skin.Danger;
            quitButton.Margin = new Padding(4, 6, 0, 6);

            bottom.Controls.Add(_copyButton, 0, 0);
            bottom.Controls.Add(quitButton, 1, 0);

            // Everything below the strip must live inside _detail. Adding the
            // bottom bar to _body instead left its buttons drawn on top of the
            // collapsed strip, where they swallowed clicks aimed for the expand
            // button.
            _detail.Controls.Add(bottom);
            _detail.Controls.Add(lastRow);
            _detail.Controls.Add(urlCard);

            _body.Controls.Add(_detail);
            _body.Controls.Add(_strip);

            host.Controls.Add(_body);
            LayOutStrip();
            // Open on the QR code until a phone has paired: before that, the
            // code is the only thing on the panel worth looking at. The same
            // goes for a phone that has to catch up with a new certificate.
            _holdOpen = PhoneNeedsCode();
            SetExpanded(_holdOpen);

            // Anything that is not a button or the address moves the panel.
            foreach (Control c in new Control[] {
                _strip, _lamp, _phoneState, _body, _detail, urlCard, urlBg, _qr,
                caption, lastRow, _quote, bottom })
            {
                MakeDraggable(c);
            }
            MouseEventHandler toggle = (s, e) =>
            {
                if (e.Button == MouseButtons.Left) SetExpanded(!_expanded);
            };
            _strip.MouseDoubleClick += toggle;
            _lamp.MouseDoubleClick += toggle;
            _phoneState.MouseDoubleClick += toggle;
        }

        private void LayOutStrip()
        {
            int width = _strip.ClientSize.Width;
            _collapse.Location = new Point(width - _collapse.Width - 10,
                (StripHeight - _collapse.Height) / 2);
            _phoneState.Width = Math.Max(0, _collapse.Left - 6 - _phoneState.Left);
        }

        private static SoftButton SoftButtonOf(string text, EventHandler onClick)
        {
            var b = new SoftButton
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = Skin.UiSmall
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
        // not need two places that explain what it can do. Every control passes
        // its right-click on: WinForms gives a click to the control under the
        // mouse and never to its parent, so a handler on the form alone would
        // work only on the few pixels that are genuinely the form.
        //
        // One menu instance, shared with the tray icon rather than copied:
        // ToolStripMenuItem is not thread safe, and two copies would drift apart.
        private void UsePanelMenu(Control root)
        {
            if (_panelMenu == null) return;
            root.MouseUp += OnPanelMouseUp;
            foreach (Control child in root.Controls)
            {
                UsePanelMenu(child);
            }
        }

        private void OnPanelMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) ShowMenu((Control)sender, e.Location);
        }

        // The menu is the one part of the panel that takes the focus. A menu on a
        // window that is not in front does not close when you click somewhere
        // else -- NotifyIcon brings its own window forward before it shows the
        // tray menu for the same reason -- so the panel comes to the front while
        // its menu is open, and hands the focus back to wherever it was when the
        // menu closes, unless the item chosen opened a window of its own.
        private void ShowMenu(Control source, Point at)
        {
            IntPtr before = Native.GetForegroundWindow();
            _focusBeforeMenu = before == Handle ? IntPtr.Zero : before;
            _menuKeepsFocus = false;
            Native.SetForegroundWindow(Handle);
            _panelMenu.Show(source, at);
        }

        private void OnMenuItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            _menuKeepsFocus = e.ClickedItem != null && e.ClickedItem.Tag == KeepsFocus;
        }

        private void OnMenuClosed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            if (_focusBeforeMenu == IntPtr.Zero) return;
            // Queued, so it runs after the item's own Click handler.
            try { BeginInvoke((MethodInvoker)ReturnFocus); }
            catch (InvalidOperationException) { }
        }

        private void ReturnFocus()
        {
            IntPtr back = _focusBeforeMenu;
            _focusBeforeMenu = IntPtr.Zero;
            if (back == IntPtr.Zero || _menuKeepsFocus) return;
            // Not if a click somewhere else closed the menu: that click has put
            // the focus where it was wanted. Hiding the panel moves the focus
            // too, to whatever Windows picks, so it is handed back then as well.
            if (Visible && Native.GetForegroundWindow() != Handle) return;
            if (Native.IsWindow(back)) Native.SetForegroundWindow(back);
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
            if (y + Height > area.Bottom) y = area.Bottom - Height;
            if (y < area.Top) y = area.Top;
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
            string what = expanded ? "Hide the QR code" : "Show the QR code";
            _collapse.Glyph = expanded ? ButtonGlyph.ChevronUp : ButtonGlyph.ChevronDown;
            _collapse.AccessibleName = what;
            _tips.SetToolTip(_collapse, what);
            _collapse.Invalidate();
            _strip.Invalidate();
            // Opening downwards near the bottom of the screen would put the
            // buttons out of reach.
            if (expanded && IsHandleCreated) ClampIntoView();
        }

        // The link carries the key, so the log only says that it was copied. The
        // button says so as well, for a moment: nothing else on screen changes.
        public void CopyPairingLink()
        {
            bool copied = false;
            try
            {
                Clipboard.SetText(Pairing.Link(_url));
                copied = true;
                Log.Write("copied the pairing link");
            }
            catch (Exception e)
            {
                Log.Write("WARN: could not copy the pairing link: " + e.Message);
            }
            _copyButton.Text = copied ? "Copied" : "Could not copy";
            _copyButton.Ink = copied ? Skin.Good : Skin.Danger;
            _copyButton.Invalidate();
            _copied.Stop();
            _copied.Start();
        }

        // After a reset: the old code no longer works, so show the new one.
        public void ShowPairing()
        {
            _qr.SetAddress(Pairing.Link(_url));
            _holdOpen = true;
            SetExpanded(true);
            Refresh2();
        }

        // After the PC moved to another address while running: the page on the
        // phone is still asking the old one, so show the code for the new one.
        // A panel the user hid stays hidden; the tray icon says it as well.
        public void ShowAddress(string url)
        {
            _url = url;
            _qr.SetAddress(Pairing.Link(_url));
            _address.Set(_url, Skin.Accent);
            _holdOpen = true;
            SetExpanded(true);
            Refresh2();
        }

        private bool PhoneNeedsCode()
        {
            return !Pairing.Used || _bridge.PendingNotice != Notice.None;
        }

        // Collapses once, when the phone has paired or caught up: the code has
        // done its job, and the strip is what is worth watching from then on.
        // Runs on the timer, so it is always on the UI thread.
        private void WatchPhone()
        {
            if (!_holdOpen || PhoneNeedsCode()) return;
            _holdOpen = false;
            if (_expanded) SetExpanded(false);
        }

        // Changed is raised on whichever server thread answered the phone, and a
        // control may only be touched from the thread that made it. The refresh
        // used to run right there, on the server thread. It is queued to the
        // panel's own thread instead, one at a time: polls that arrive while one
        // is waiting ride along with it.
        private void RefreshSoon()
        {
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    Interlocked.Exchange(ref _refreshQueued, 0);
                    Refresh2();
                });
            }
            catch (InvalidOperationException)
            {
                // No window yet, or none any more; the timer covers both.
                Interlocked.Exchange(ref _refreshQueued, 0);
            }
        }

        private void Refresh2()
        {
            if (IsDisposed) return;
            PanelStatus now = PanelStatus.Of(_bridge);
            _lamp.Set(now.Dot, now.Breathe);
            _phoneState.Set(now.Strip, now.Ink);
            _caption.Set(CaptionText(), Skin.InkSoft);
            _quote.SetText(_bridge.LastText);
        }

        // What to do with the code. Scanning a new one is no use until the phone
        // trusts the new certificate, so then it says where that is first.
        private string CaptionText()
        {
            return _bridge.PendingNotice == Notice.NewCertificate
                ? "Right-click for the new certificate, then scan to pair"
                : "Scan with the iPhone camera to pair";
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Bridge _bridge;
        private readonly NotifyIcon _tray;
        private readonly MainForm _form;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _settle;
        // One icon per colour, made the first time it is needed and kept: the
        // state is checked every second, and it is nearly always the same.
        private readonly Dictionary<int, Icon> _icons = new Dictionary<int, Icon>();
        private bool _reallyQuitting;

        public TrayContext(Bridge bridge, string url)
        {
            _bridge = bridge;

            // DictationBridge.MainForm, qualified: in an ApplicationContext, plain
            // MainForm is the context's own MainForm property.
            var quitItem = new ToolStripMenuItem("Quit");
            quitItem.Tag = DictationBridge.MainForm.KeepsFocus;
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
            logItem.Tag = DictationBridge.MainForm.KeepsFocus;
            logItem.Click += (s, e) => ShowInExplorer("dictation-bridge.log");

            // The file the phone has to install, which otherwise means finding
            // the data folder. Not there on plain HTTP, which has no certificate.
            var cerItem = new ToolStripMenuItem("Show the certificate file");
            cerItem.Tag = DictationBridge.MainForm.KeepsFocus;
            cerItem.Click += (s, e) => ShowInExplorer("dictation-bridge.cer");

            // One copy item. There used to be two, "Copy app address" and "Copy
            // page address", and they copied the same string. The address alone
            // no longer pairs a phone, so what is worth copying is the link that
            // carries the key.
            var copyItem = new ToolStripMenuItem("Copy pairing link");
            copyItem.Click += (s, e) => _form.CopyPairingLink();

            // It asks first, in a dialog that needs the focus.
            var resetItem = new ToolStripMenuItem("Reset phone pairing...");
            resetItem.Tag = DictationBridge.MainForm.KeepsFocus;
            resetItem.Click += (s, e) => ResetPairing();

            // Hide as well as show. The panel never takes the focus, so Alt+F4
            // cannot reach it; this is the way to put it away.
            var showItem = new ToolStripMenuItem("Hide the panel");
            showItem.Click += (s, e) =>
            {
                if (_form.Visible) _form.Hide();
                else ShowWindow();
            };

            // No arm item and no buffer item: the phone's Start button is the only
            // on/off switch, and there is nothing to hold text for, because
            // whatever the page sends is typed.
            var menu = new ContextMenuStrip();
            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(copyItem);
            menu.Items.Add(resetItem);
            if (Servers.Secure) menu.Items.Add(cerItem);
            menu.Items.Add(logItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quitItem);
            menu.Opening += (s, e) =>
            {
                showItem.Text = _form.Visible ? "Hide the panel" : "Show the panel";
            };

            _tray = new NotifyIcon
            {
                Icon = IconFor(Skin.InkFaint),
                Text = "Dictation Bridge",
                ContextMenuStrip = menu,
                Visible = true
            };
            // One click, as the README has always said. It used to take two.
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowWindow();
            };

            _form = new MainForm(_bridge, url, menu);
            _form.QuitRequested += () =>
            {
                _reallyQuitting = true;
                Log.SetReason("quit from the panel");
                ExitThread();
            };
            _form.ShowQuietly();
            _form.FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing && !_reallyQuitting)
                {
                    e.Cancel = true;
                    _form.Hide();
                }
            };

            Update();
            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (s, e) => Update();
            _timer.Start();

            // Windows says when an address comes or goes, often several times
            // over for one change, so let it settle before looking.
            _settle = new System.Windows.Forms.Timer { Interval = 3000 };
            _settle.Tick += (s, e) =>
            {
                _settle.Stop();
                ThreadPool.QueueUserWorkItem(delegate { FollowAddress(); });
            };
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
        }

        // Raised on a worker thread, and the timer belongs to this one.
        private void OnAddressChanged(object sender, EventArgs e)
        {
            try
            {
                _form.BeginInvoke((MethodInvoker)delegate
                {
                    _settle.Stop();
                    _settle.Start();
                });
            }
            catch (InvalidOperationException)
            {
                // No window yet, or none any more.
            }
        }

        // On a worker thread: a new address can mean making a certificate.
        private void FollowAddress()
        {
            try
            {
                bool newCertificate;
                if (Addresses.Follow(out newCertificate) == null) return;
                _bridge.Notify(newCertificate ? Notice.NewCertificate : Notice.NewAddress);
                string url = Addresses.Url;
                _form.BeginInvoke((MethodInvoker)delegate { _form.ShowAddress(url); });
            }
            catch (InvalidOperationException)
            {
                // The panel went while this ran: the app is quitting.
            }
            catch (Exception e)
            {
                Log.Detail("WARN: could not follow the address change", e);
            }
        }

        // Explorer with the file selected, ready to attach or send.
        private static void ShowInExplorer(string name)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + Store.File(name) + "\"");
            }
            catch (Exception e)
            {
                Log.Detail("could not show " + name, e);
            }
        }

        // Asks first: every phone that scanned the old code stops typing until
        // it scans the new one, and there is no undo.
        private void ResetPairing()
        {
            DialogResult answer = MessageBox.Show(_form,
                "Unpair every phone?\n\n" +
                "A new QR code replaces the old one. Phones that scanned the old " +
                "code stop typing on this PC until they scan the new one.",
                "Reset phone pairing", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
            Pairing.Reset();
            _form.ShowPairing();
            ShowWindow();
        }

        // Brings the panel back without taking the focus from the window being
        // typed into. It used to activate it, which did.
        private void ShowWindow()
        {
            if (_form.WindowState == FormWindowState.Minimized)
            {
                _form.WindowState = FormWindowState.Normal;
            }
            _form.ShowQuietly();
        }

        private void Update()
        {
            PanelStatus now = PanelStatus.Of(_bridge);
            string tip = "Dictation Bridge: " + now.Tip;
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            // Only on a change: each one is a call into the shell.
            if (_tray.Text != tip) _tray.Text = tip;
            Icon icon = IconFor(now.Dot);
            if (!ReferenceEquals(_tray.Icon, icon)) _tray.Icon = icon;
        }

        // A white microphone on a dot of the strip's colour, so the state shows
        // without hovering. It used to be the stock application icon, the blank
        // window that plenty of other programs in the tray show as well.
        private Icon IconFor(Color color)
        {
            Icon icon;
            if (_icons.TryGetValue(color.ToArgb(), out icon)) return icon;
            Size size = SystemInformation.SmallIconSize;
            using (var bmp = new Bitmap(size.Width, size.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    PaintTrayIcon(g, size.Width, color);
                }
                // GetHicon makes a handle that an Icon from FromHandle does not
                // own. The clone owns a copy, and the original is destroyed here
                // rather than leaked.
                IntPtr handle = bmp.GetHicon();
                try
                {
                    using (Icon borrowed = Icon.FromHandle(handle))
                    {
                        icon = (Icon)borrowed.Clone();
                    }
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
            _icons[color.ToArgb()] = icon;
            return icon;
        }

        internal static void PaintTrayIcon(Graphics g, int side, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float k = side / 16F;
            using (var dot = new SolidBrush(color))
            {
                g.FillEllipse(dot, 0.5F * k, 0.5F * k, 15F * k, 15F * k);
            }
            using (var white = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.White, 1.3F * k))
            using (var capsule = new GraphicsPath())
            {
                float w = 4F * k;
                float x = 8F * k - w / 2;
                capsule.AddArc(x, 3F * k, w, w, 180, 180);
                capsule.AddArc(x, 9.5F * k - w, w, w, 0, 180);
                capsule.CloseFigure();
                g.FillPath(white, capsule);
                // The cradle round it, and the stand.
                g.DrawArc(pen, 4.75F * k, 4.5F * k, 6.5F * k, 6.5F * k, 0, 180);
                g.DrawLine(pen, 8F * k, 11F * k, 8F * k, 13F * k);
            }
        }

        protected override void ExitThreadCore()
        {
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
            _settle.Stop();
            _timer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            foreach (Icon icon in _icons.Values) icon.Dispose();
            _icons.Clear();
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
            // is the only port to choose. The old --ws port is no longer used.
            int httpPort = Servers.DefaultPort;
            bool portWasChosen = false;
            string address = null;
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
                // For when the address the app picks is not the one the phone can
                // reach: a VPN, a second network card, or the PC's own hotspot.
                if (args[i] == "--address")
                {
                    IPAddress parsed;
                    if (IPAddress.TryParse(args[i + 1], out parsed) &&
                        parsed.AddressFamily == AddressFamily.InterNetwork)
                    {
                        address = parsed.ToString();
                    }
                    else
                    {
                        Log.Write("WARN: --address " + args[i + 1] + " is not an IPv4 address " +
                                  "such as 192.168.1.20, so the app picks one itself");
                    }
                }
            }
            Log.Write("port: " + httpPort + (portWasChosen ? " (chosen on the command line)" : " (default)"));

            Log.Say("Dictation Bridge");
            Log.Say("===============");
            Log.Say("Your files are in: " + Store.Dir);
            Log.Say("");

            Log.Stage("picking a network address");
            string ip = Addresses.Choose(address);

            Log.Stage("certificate");
            X509Certificate2 cert = null;
            byte[] pfx = null;
            string caCerPath = Store.File("dictation-bridge.cer");
            bool replaced = false;
            try
            {
                cert = Certs.Ensure(ip, out caCerPath, out replaced);
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

            // Before the listener, so a pairing file that cannot be read or
            // written is reported at startup rather than on the first request.
            Log.Stage("pairing key");
            Log.Write(Pairing.Used
                ? "pairing: a phone is paired (key in data\\pairing-key.txt)"
                : "pairing: no phone paired yet, scan the QR code on the panel");

            var bridge = new Bridge();
            // The phone trusted the old certificate, and the panel is where the
            // person will look.
            if (replaced) bridge.Notify(Notice.NewCertificate);
            // One port for the page and the socket. iOS accepted TLS on the HTTP
            // port but rejected it on a second listener using the same
            // certificate, so both now share the port that already worked.
            Log.Stage("starting the listener on port " + httpPort);
            // Only move the port when it was not asked for. A port passed on the
            // command line is a decision, usually because something else is
            // already using the default, so it is never quietly second-guessed.
            int boundPort = Servers.StartAll(bridge, httpPort, pfx, !portWasChosen);
            if (boundPort != httpPort)
            {
                Log.Write("NOTE: the app is on port " + boundPort + " rather than " +
                          httpPort + ". Use the address below and rescan the QR code.");
            }

            // From the listener rather than from the certificate: one that could
            // not be imported leaves the server on plain HTTP, and an https link
            // to it would never load.
            bool secure = Servers.Secure;
            string scheme = secure ? "https" : "http";
            Addresses.Serving(scheme, boundPort);
            string url = Addresses.Url;

            Log.Write("listening: " + scheme + " on " + boundPort + " (page and socket share it)");
            Log.Write("open this on the phone: " + url);
            Log.Write("embedded page version: " + Servers.PageVersion());

            Log.Say("");
            if (secure)
            {
                Log.Say("  On the iPhone, once:");
                Log.Say("   1. Send this file to the phone and tap it (right-click the panel,");
                Log.Say("      Show the certificate file):");
                Log.Say("");
                Log.Say("        " + caCerPath);
                Log.Say("");
                Log.Say("   2. Settings > General > VPN & Device Management > Install.");
                Log.Say("   3. Settings > General > About > Certificate Trust Settings");
                Log.Say("      > enable the Dictation Bridge certificate.");
                Log.Say("   4. Scan the QR code on the panel with the Camera app.");
            }
            else
            {
                Log.Say("  Scan the QR code on the panel with the Camera app.");
            }
            // The address alone opens the page but cannot pair it: the key is in
            // the QR code, and only there.
            Log.Say("  The code carries the pairing key, so typing " + url);
            Log.Say("  by hand opens the page without pairing it.");
            Log.Say("  Leave Start on and it keeps listening. Whatever you say is typed");
            Log.Say("  straight into whatever window you are working in.");
            Log.Say("");

            Log.Stage("building the panel");
            Application.EnableVisualStyles();

            TrayContext context = new TrayContext(bridge, url);

            Log.Stage("running");
            Log.Write("uptime at startup: " + (Environment.TickCount / 1000) + "s since boot");
            Application.Run(context);
            return 0;
        }
    }
}
