// Pad audio: the microphone and the speaker of a DualSense connected over
// Bluetooth, reached through ordinary HID access — no driver, no virtual sound
// card. Over Bluetooth the pad carries its audio inside its HID reports:
//
//   microphone  input report 0x31, low nibble of byte 1 = 2, one 71-byte Opus
//               packet (CELT, 10 ms) at offset 3, CRC-32 in the last 4 bytes
//   switch      output report 0x32, sub-packet 0x11, flag bit 0 = microphone on
//   speaker     output report 0x39: sub-packet 0x11 (flags), 0x12 (2 x 64
//               haptics samples), 0x13 (2 x 200-byte Opus frames, 10 ms each)
//
// Layout taken from awalol/DS5Dongle (MIT), src/audio.cpp and src/bt.cpp.
// Compiled by padaudio.ps1 with the C# 5 compiler Windows ships, so no
// string interpolation, no `out var`, no expression-bodied members.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PadAudio
{
    public sealed class PadInfo
    {
        public string Path;
        public int ProductId;
        public int InputLength;
        public int OutputLength;
        public string Serial;
        public bool Bluetooth { get { return InputLength == Protocol.BtInputLength; } }
    }

    public static class Log
    {
        static StreamWriter file;
        static readonly object gate = new object();

        public static void Open(string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            file.AutoFlush = true;
        }

        static string Esc(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') { b.Append('\\').Append(c); }
                else if (c < 0x20) { b.AppendFormat("\\u{0:x4}", (int)c); }
                else { b.Append(c); }
            }
            return b.ToString();
        }

        // One JSON object per line, on disk and mirrored to stderr.
        public static void Write(string level, string ev, params object[] kv)
        {
            StringBuilder b = new StringBuilder();
            b.Append("{\"ts\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            b.Append("\",\"level\":\"").Append(level).Append("\",\"mod\":\"padaudio\",\"event\":\"").Append(Esc(ev)).Append('"');
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                b.Append(",\"").Append(Esc(Convert.ToString(kv[i]))).Append("\":\"");
                b.Append(Esc(Convert.ToString(kv[i + 1], System.Globalization.CultureInfo.InvariantCulture))).Append('"');
            }
            b.Append('}');
            string line = b.ToString();
            lock (gate)
            {
                if (file != null) { file.WriteLine(line); }
                Console.Error.WriteLine(line);
            }
        }
    }

    public static class Protocol
    {
        public const int VendorSony = 0x054C;
        public const int ProductDualSense = 0x0CE6;
        public const int ProductDualSenseEdge = 0x0DF2;
        public const int BtInputLength = 78;
        // In a state report over Bluetooth (0x31 type 1): byte 11 holds the PS
        // button (bit 0), the touchpad click (bit 1) and the microphone button (bit 2).
        public const int ButtonsByte = 11;
        public const int MicButton = 0x04;
        public const int MicOpusSize = 71;
        public const int SpeakerOpusSize = 200;
        public const int HapticsSize = 64;
        public const int SwitchReportLength = 142;
        public const int AudioReportLength = 547;

        static readonly uint[] table = MakeTable();

        static uint[] MakeTable()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) { c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
                t[i] = c;
            }
            return t;
        }

        // CRC-32 over a one-byte transport header (0xA2 for output, 0xA1 for
        // input) followed by the report without its last four bytes.
        public static uint Crc(byte header, byte[] data, int length)
        {
            uint c = 0xFFFFFFFFu;
            c = table[(c ^ header) & 0xFF] ^ (c >> 8);
            for (int i = 0; i < length; i++) { c = table[(c ^ data[i]) & 0xFF] ^ (c >> 8); }
            return c ^ 0xFFFFFFFFu;
        }

        public static void Seal(byte[] report, int length)
        {
            uint c = Crc(0xA2, report, length - 4);
            report[length - 4] = (byte)c;
            report[length - 3] = (byte)(c >> 8);
            report[length - 2] = (byte)(c >> 16);
            report[length - 1] = (byte)(c >> 24);
        }

        public static bool InputCrcOk(byte[] report, int length)
        {
            uint c = Crc(0xA1, report, length - 4);
            uint got = (uint)(report[length - 4] | report[length - 3] << 8 | report[length - 2] << 16 | report[length - 1] << 24);
            return c == got;
        }

        // The long form, as jsj/vds sends it: the short one (length 1, flags 3,
        // from DS5Dongle) does open the stream, but what arrives is mostly
        // silence with clicks.
        // The long form, as jsj/vds sends it. The PS5's own values (flags 0x6F,
        // then 00 1F 28) were tried on 2026-09-27: they open the stream too,
        // but at a far lower level.
        public static byte[] MicSwitch(int seq, bool on, int bufferLength)
        {
            byte[] p = new byte[SwitchReportLength];
            p[0] = 0x32;
            p[1] = (byte)((seq & 0x0F) << 4);
            p[2] = 0x91;            // sub-packet 0x11, bit 7 set
            p[3] = 7;               // its length
            p[4] = (byte)(on ? 0xFF : 0xFE);
            for (int i = 5; i <= 9; i++) { p[i] = (byte)bufferLength; }
            p[10] = (byte)(seq & 0x0F);
            p[11] = 0x92;           // sub-packet 0x12: haptics, 64 silent samples
            p[12] = HapticsSize;
            Seal(p, p.Length);
            return p;
        }

        // The pad's state report, reduced to its audio fields: without it the
        // speaker stays at the volume and the route the pad woke up with, which
        // is silence. Offsets are those of the state block (DS5Dongle's
        // SetStateData), which starts at byte 4 of the report.
        public static byte[] AudioState(int seq, int speakerVolume, int micVolume, int audioControl, int muteLight)
        {
            byte[] p = new byte[SwitchReportLength];
            p[0] = 0x32;
            p[1] = (byte)((seq & 0x0F) << 4);
            p[2] = 0x90;            // sub-packet 0x10, bit 7 set
            p[3] = 0x3F;            // its length
            p[4] = 0x10 | 0x20 | 0x40 | 0x80;  // allow headphone, speaker and microphone volume, audio control
            p[5] = 0x01 | 0x02;     // allow mute light, audio mute; nothing is ever muted
            p[4 + 8] = (byte)muteLight;   // the orange light of the microphone button: 0 off, 1 on, 2 pulsing
            p[4 + 4] = 0x7F;        // headphone volume
            p[4 + 5] = (byte)speakerVolume;
            p[4 + 6] = (byte)micVolume;
            // bits 0-1 microphone select, 2 echo cancel, 3 noise cancel,
            // 4-5 output route (3 = speaker only), 6-7 input route
            p[4 + 7] = (byte)audioControl;
            Seal(p, p.Length);
            return p;
        }

        public static byte[] AudioReport(int seq, int packetCounter, bool micOn, int bufferLength, byte[] opusA, byte[] opusB)
        {
            byte[] p = new byte[AudioReportLength];
            p[0] = 0x39;
            p[1] = (byte)((seq & 0x0F) << 4);
            p[2] = 0x91;
            p[3] = 6;
            p[4] = (byte)(micOn ? 0x7F : 0x7E);
            p[5] = (byte)bufferLength;
            p[6] = (byte)bufferLength;
            p[7] = (byte)bufferLength;
            p[8] = (byte)bufferLength;
            p[9] = (byte)packetCounter;
            p[10] = 0xD2;           // sub-packet 0x12: haptics, left silent
            p[11] = HapticsSize;
            p[140] = 0xD3;          // sub-packet 0x13: the pad's own speaker
            p[141] = SpeakerOpusSize;
            Buffer.BlockCopy(opusA, 0, p, 142, SpeakerOpusSize);
            Buffer.BlockCopy(opusB, 0, p, 142 + SpeakerOpusSize, SpeakerOpusSize);
            Seal(p, p.Length);
            return p;
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid Class; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort Version; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage; public ushort UsagePage;
            public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps; public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps; public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, StringBuilder b, int len);
        [DllImport("hid.dll")] public static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, int n);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int needed, IntPtr info);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);

        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint SHARE_RW = 3, OPEN_EXISTING = 3, OVERLAPPED = 0x40000000;
    }

    static class Opus
    {
        const string L = "libopus-0.dll";
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr opus_decoder_create(int fs, int channels, out int error);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_decode(IntPtr st, byte[] data, int len, short[] pcm, int frameSize, int fec);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern void opus_decoder_destroy(IntPtr st);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr opus_encoder_create(int fs, int channels, int application, out int error);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_encoder_ctl(IntPtr st, int request, int value);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int opus_encode(IntPtr st, short[] pcm, int frameSize, byte[] data, int max);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern void opus_encoder_destroy(IntPtr st);

        public const int ApplicationAudio = 2049;
        public const int SetBitrate = 4002, SetVbr = 4006, SetComplexity = 4010, SetExpertFrameDuration = 4040;
        public const int Framesize10ms = 5003;

        // The library ships beside EmulationStation; loading it by full path
        // first lets the DllImport above resolve, along with its neighbours.
        public static void Load(string path)
        {
            if (!File.Exists(path)) { throw new FileNotFoundException("Opus library not found; pass -OpusDll with the path to libopus-0.dll", path); }
            IntPtr h = Native.LoadLibraryEx(path, IntPtr.Zero, 0x8);
            if (h == IntPtr.Zero) { throw new InvalidOperationException("could not load " + path + " (Win32 error " + Marshal.GetLastWin32Error() + "); a 64-bit PowerShell needs the 64-bit library"); }
            Log.Write("info", "opus_loaded", "path", path);
        }
    }

    public static class Wav
    {
        public static void Write(string path, short[] pcm, int count, int rate, int channels)
        {
            using (BinaryWriter w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write)))
            {
                int bytes = count * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2);
                w.Write((short)(channels * 2)); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
                for (int i = 0; i < count; i++) { w.Write(pcm[i]); }
            }
        }

        // Returns interleaved 16-bit samples; refuses what it cannot play as is.
        public static short[] Read(string path, out int rate, out int channels)
        {
            short[] pcm = ReadAny(path, out rate, out channels);
            if (rate != 48000)
            {
                throw new InvalidDataException(path + ": need 16-bit PCM at 48000 Hz, mono or stereo; got " + rate + " Hz");
            }
            return pcm;
        }

        // 48 kHz is what the pad plays; a voice program may write another rate.
        // Linear interpolation, channel by channel: enough for speech on a
        // speaker this small.
        public static short[] Resample(short[] pcm, int channels, int from, int to)
        {
            int frames = pcm.Length / channels;
            int outFrames = (int)((long)frames * to / from);
            short[] y = new short[outFrames * channels];
            for (int i = 0; i < outFrames; i++)
            {
                double pos = (double)i * from / to;
                int a = (int)pos;
                int b = Math.Min(a + 1, frames - 1);
                double f = pos - a;
                for (int ch = 0; ch < channels; ch++)
                {
                    y[i * channels + ch] = (short)Math.Round(pcm[a * channels + ch] * (1 - f) + pcm[b * channels + ch] * f);
                }
            }
            return y;
        }

        // Interleaved 16-bit samples at whatever rate the file holds.
        public static short[] ReadAny(string path, out int rate, out int channels)
        {
            using (BinaryReader r = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read)))
            {
                if (new string(r.ReadChars(4)) != "RIFF") { throw new InvalidDataException(path + " is not a WAV file"); }
                r.ReadInt32();
                if (new string(r.ReadChars(4)) != "WAVE") { throw new InvalidDataException(path + " is not a WAV file"); }
                rate = 0; channels = 0; int bits = 0; int format = 0;
                while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
                {
                    string id = new string(r.ReadChars(4));
                    int size = r.ReadInt32();
                    if (id == "fmt ")
                    {
                        format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                        r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                        if (size > 16) { r.ReadBytes(size - 16); }
                    }
                    else if (id == "data")
                    {
                        if (format != 1 || bits != 16 || (channels != 1 && channels != 2))
                        {
                            throw new InvalidDataException(path + ": need 16-bit PCM, mono or stereo; got format " + format + ", " + bits + " bits, " + rate + " Hz, " + channels + " channels");
                        }
                        int n = (int)Math.Min(size, r.BaseStream.Length - r.BaseStream.Position) / 2;
                        short[] pcm = new short[n];
                        for (int i = 0; i < n; i++) { pcm[i] = r.ReadInt16(); }
                        return pcm;
                    }
                    else { r.ReadBytes(size + (size & 1)); }
                }
                throw new InvalidDataException(path + " has no data chunk");
            }
        }
    }

    // Holds other programs still while the microphone is on. SDL reads the
    // pad's microphone reports as button state, so a program reading the pad
    // through it (EmulationStation) receives random input: on 2026-09-28 three
    // seconds of it made EmulationStation open its menu and quit. A suspended
    // process reads nothing; Windows keeps only the latest reports for it, so
    // once the microphone is off and ordinary reports have flowed again, it
    // wakes up to ordinary reports only.
    public static class Hold
    {
        [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        const uint SuspendResume = 0x0800;

        public static string[] Names = new string[0];
        // While one of these runs, a game is on screen: RetroBat's launcher
        // stays alive for as long as the emulator it started.
        public static string[] Busy = new string[] { "emulatorLauncher" };

        public static string Running(string[] names)
        {
            foreach (string name in names)
            {
                if (Process.GetProcessesByName(name).Length > 0) { return name; }
            }
            return null;
        }
        static readonly object gate = new object();
        static readonly List<IntPtr> held = new List<IntPtr>();
        static int depth;

        // Throws, holding nothing, if any of the processes cannot be suspended.
        public static void Enter()
        {
            lock (gate)
            {
                if (depth++ > 0) { return; }
                try
                {
                    foreach (string name in Names)
                    {
                        foreach (Process p in Process.GetProcessesByName(name))
                        {
                            IntPtr h = OpenProcess(SuspendResume, false, p.Id);
                            if (h == IntPtr.Zero) { throw new InvalidOperationException("cannot open " + name + " (process " + p.Id + ") to hold it still, Win32 error " + Marshal.GetLastWin32Error() + "; the microphone stays off"); }
                            int status = NtSuspendProcess(h);
                            if (status != 0) { CloseHandle(h); throw new InvalidOperationException("cannot suspend " + name + " (process " + p.Id + "), status 0x" + status.ToString("x8") + "; the microphone stays off"); }
                            held.Add(h);
                            Log.Write("info", "hold", "process", name, "pid", p.Id);
                        }
                    }
                    if (held.Count == 0) { Log.Write("info", "hold_none", "looked_for", string.Join(",", Names)); }
                }
                catch { Release(); depth = 0; throw; }
            }
        }

        public static void Exit()
        {
            lock (gate)
            {
                if (depth == 0 || --depth > 0) { return; }
                Release();
            }
        }

        static void Release()
        {
            foreach (IntPtr h in held)
            {
                int status = NtResumeProcess(h);
                Log.Write(status == 0 ? "info" : "error", "release", "status", "0x" + status.ToString("x8"));
                CloseHandle(h);
            }
            held.Clear();
        }

        // For a run that died between Enter and Exit: resumes every process of
        // that name once. Harmless on a process that is not suspended.
        public static int ReleaseByName(string name)
        {
            int n = 0;
            foreach (Process p in Process.GetProcessesByName(name))
            {
                IntPtr h = OpenProcess(SuspendResume, false, p.Id);
                if (h == IntPtr.Zero) { throw new InvalidOperationException("cannot open " + name + " (process " + p.Id + "), Win32 error " + Marshal.GetLastWin32Error()); }
                int status = NtResumeProcess(h);
                CloseHandle(h);
                Log.Write("info", "release_by_name", "process", name, "pid", p.Id, "status", "0x" + status.ToString("x8"));
                n++;
            }
            return n;
        }
    }

    // What push to talk hands a recording to (Assistant.cs). Warm is called
    // at the press that starts a recording, so that what answers can start
    // while the person speaks; Respond with the recording, playing each piece
    // of the answer through play as soon as it is ready; Cancel when the press
    // led to no recording, or after a failure, to let go of what Warm started.
    public interface IResponder
    {
        void Warm(string who);
        void Respond(string recording, string who, Action<string> play);
        void Cancel();
    }

    public sealed class RecordResult
    {
        public bool StoppedByButton;
        public int MicPackets, StatePackets, OtherReports, BadCrc, Lost, DecodeErrors, Samples, Peak;
        public double Rms;
    }

    public sealed class Pad : IDisposable
    {
        readonly PadInfo info;
        readonly FileStream input;
        readonly FileStream output;
        int seq;
        int packetCounter;

        public PadInfo Info { get { return info; } }

        public static List<PadInfo> Find()
        {
            return Find(true);
        }

        public static List<PadInfo> Find(bool log)
        {
            List<PadInfo> pads = new List<PadInfo>();
            Guid g; Native.HidD_GetHidGuid(out g);
            IntPtr set = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // present | device interface
            if (set == new IntPtr(-1)) { throw new InvalidOperationException("SetupDiGetClassDevs failed, Win32 error " + Marshal.GetLastWin32Error()); }
            try
            {
                for (int i = 0; ; i++)
                {
                    Native.SP_DEVICE_INTERFACE_DATA d = new Native.SP_DEVICE_INTERFACE_DATA();
                    d.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) { break; }
                    int needed;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(needed);
                    string path;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref d, buf, needed, out needed, IntPtr.Zero)) { continue; }
                        path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }

                    using (SafeFileHandle h = Native.CreateFile(path, 0, Native.SHARE_RW, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        if (h.IsInvalid) { continue; }
                        Native.HIDD_ATTRIBUTES a = new Native.HIDD_ATTRIBUTES();
                        a.Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                        if (!Native.HidD_GetAttributes(h, ref a)) { continue; }
                        if (a.VendorID != Protocol.VendorSony) { continue; }
                        if (a.ProductID != Protocol.ProductDualSense && a.ProductID != Protocol.ProductDualSenseEdge) { continue; }
                        IntPtr pre;
                        if (!Native.HidD_GetPreparsedData(h, out pre)) { continue; }
                        Native.HIDP_CAPS caps;
                        Native.HidP_GetCaps(pre, out caps);
                        Native.HidD_FreePreparsedData(pre);
                        StringBuilder serial = new StringBuilder(128);
                        Native.HidD_GetSerialNumberString(h, serial, 256);
                        PadInfo p = new PadInfo();
                        p.Path = path; p.ProductId = a.ProductID;
                        p.InputLength = caps.InputReportByteLength; p.OutputLength = caps.OutputReportByteLength;
                        p.Serial = serial.ToString();
                        pads.Add(p);
                    }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            if (log) { Log.Write("info", "pads_found", "count", pads.Count); }
            return pads;
        }

        // Over USB the state block travels in output report 0x02, without
        // header or CRC. Used to find out which setting does what to the
        // microphone, since over USB that microphone is an ordinary Windows
        // recording device.
        public static void SendUsbState(PadInfo p, string stateHex)
        {
            if (p.Bluetooth) { throw new InvalidOperationException("this pad is on Bluetooth; usbstate is for a pad on the USB cable"); }
            byte[] b = new byte[p.OutputLength];
            b[0] = 0x02;
            for (int i = 0; i + 1 < stateHex.Length && 1 + i / 2 < b.Length; i += 2) { b[1 + i / 2] = Convert.ToByte(stateHex.Substring(i, 2), 16); }
            using (FileStream o = Open(p.Path, Native.GENERIC_WRITE, p.OutputLength))
            {
                o.Write(b, 0, b.Length);
                o.Flush();
            }
            Log.Write("info", "usb_state", "hex", stateHex);
        }

        public Pad(PadInfo p)
        {
            info = p;
            if (!p.Bluetooth) { throw new InvalidOperationException("this pad is on the USB cable; unplug it so that it connects over Bluetooth (over USB its audio is an ordinary Windows sound device)"); }
            if (p.OutputLength < Protocol.AudioReportLength) { throw new InvalidOperationException("the pad declares output reports of " + p.OutputLength + " bytes, fewer than the " + Protocol.AudioReportLength + " the audio report needs"); }
            input = Open(p.Path, Native.GENERIC_READ, p.InputLength);
            output = Open(p.Path, Native.GENERIC_WRITE, p.OutputLength);
            Native.HidD_SetNumInputBuffers(input.SafeFileHandle, 512);
            Log.Write("info", "pad_open", "serial", p.Serial, "input_length", p.InputLength, "output_length", p.OutputLength);
        }

        static FileStream Open(string path, uint access, int length)
        {
            SafeFileHandle h = Native.CreateFile(path, access, Native.SHARE_RW, IntPtr.Zero, Native.OPEN_EXISTING, Native.OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { throw new IOException("could not open the pad, Win32 error " + Marshal.GetLastWin32Error()); }
            return new FileStream(h, access == Native.GENERIC_READ ? FileAccess.Read : FileAccess.Write, length, true);
        }

        // Windows wants every output report padded to the longest one declared.
        void Send(byte[] report)
        {
            byte[] b = new byte[info.OutputLength];
            Buffer.BlockCopy(report, 0, b, 0, report.Length);
            output.Write(b, 0, b.Length);
            output.Flush();
        }

        public void SetMic(bool on)
        {
            Send(Protocol.MicSwitch(seq++, on, 64));
            Log.Write("info", "mic_switch", "on", on);
        }

        // Reads what the pad sends without writing anything to it: which
        // reports arrive, how many, and whether their CRC holds. Tells whether
        // some other program already has the microphone switched on.
        public string Listen(double seconds)
        {
            byte[] buf = new byte[info.InputLength];
            SortedDictionary<string, int> seen = new SortedDictionary<string, int>();
            int bad = 0, total = 0;
            Log.Write("info", "listen_start", "seconds", seconds);
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                IAsyncResult ar = input.BeginRead(buf, 0, buf.Length, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(2000)) { throw new TimeoutException("the pad sent nothing for 2 seconds; is it still connected?"); }
                int n = input.EndRead(ar);
                total++;
                string key = buf[0] == 0x31 && n > 1 ? string.Format("0x31 type {0}", buf[1] & 0x0F) : string.Format("0x{0:x2}", buf[0]);
                int c; seen.TryGetValue(key, out c); seen[key] = c + 1;
                if (buf[0] == 0x31 && n == Protocol.BtInputLength && !Protocol.InputCrcOk(buf, n)) { bad++; }
            }
            StringBuilder kinds = new StringBuilder();
            foreach (KeyValuePair<string, int> k in seen) { kinds.AppendFormat("{0} x{1}; ", k.Key, k.Value); }
            string summary = string.Format("{0} reports in {1:0.0} s, {2} with a bad CRC: {3}", total, clock.Elapsed.TotalSeconds, bad, kinds.ToString().Trim());
            Log.Write("info", "listen_done", "reports", total, "bad_crc", bad, "kinds", kinds.ToString().Trim());
            return summary;
        }

        public int SpeakerVolume = 100;
        // Measured 2026-09-27 with a voice at one to two metres: 64 clips, 32
        // peaks at 70 % of full scale, 16 at 4 %. 0x31 = internal microphone,
        // no noise or echo cancelling, speaker-only route.
        public int MicVolume = 32;
        public int AudioControl = 0x31;
        // Audio reports carry the microphone switch too: playing with this off
        // stops a recording that is running on the same pad.
        public bool KeepMic;
        public int MuteLight;

        public void SetAudio()
        {
            Send(Protocol.AudioState(seq++, SpeakerVolume, MicVolume, AudioControl, MuteLight));
            Log.Write("info", "audio_state", "speaker_volume", SpeakerVolume, "mic_volume", MicVolume, "audio_control", "0x" + AudioControl.ToString("x2"), "mute_light", MuteLight);
        }

        // Push to talk, as a toggle: a press of the microphone button starts a
        // recording, the next press ends it, and the recording is played back
        // through the pad. The button's orange light is on while recording.
        // Runs for windowSeconds, or for good when that is 0; turn keeps two
        // pads from talking at once. respond answers the recording through the
        // pad; without it the recording itself is played back.
        public int Talk(double windowSeconds, double maxSeconds, string folder, double intervalMs, int bufferLength, object turn, IResponder respond)
        {
            byte[] buf = new byte[info.InputLength];
            bool known = false, was = false;
            int talks = 0;
            Stopwatch window = Stopwatch.StartNew();
            Log.Write("info", "talk_start", "serial", info.Serial, "window_seconds", windowSeconds, "max_seconds", maxSeconds);
            MuteLight = 0;
            SetAudio();
            while (windowSeconds <= 0 || window.Elapsed.TotalSeconds < windowSeconds)
            {
                IAsyncResult ar = input.BeginRead(buf, 0, buf.Length, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(2000)) { throw new TimeoutException("the pad sent nothing for 2 seconds; is it still connected?"); }
                int n = input.EndRead(ar);
                if (n <= Protocol.ButtonsByte || buf[0] != 0x31 || (buf[1] & 0x0F) != 1) { continue; }
                bool down = (buf[Protocol.ButtonsByte] & Protocol.MicButton) != 0;
                bool pressed = known && down && !was;
                known = true; was = down;
                if (!pressed) { continue; }
                string busy = Hold.Running(Hold.Busy);
                if (busy != null)
                {
                    // A game reads the pad by its own means and nothing holds it still.
                    Log.Write("info", "talk_ignored", "serial", info.Serial, "because_running", busy);
                    continue;
                }

                // One pad talks at a time. A press while another pad has the turn
                // is refused on the spot, not queued: queued, it started late and
                // was stopped by presses read late. The refused pad's light pulses
                // briefly; it makes no sound, because audio sent to a second pad
                // costs the recording pad packets (measured 2026-09-27).
                if (!Monitor.TryEnter(turn))
                {
                    Log.Write("info", "talk_busy", "serial", info.Serial);
                    Console.WriteLine("pad " + info.Serial + ": another pad is talking, press refused");
                    Refuse(buf);
                    known = false;
                    continue;
                }
                try
                {
                    talks++;
                    string wav = System.IO.Path.Combine(folder, "talk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + info.Serial + ".wav");
                    Console.WriteLine("pad " + info.Serial + ": recording, press the microphone button again to stop");
                    Log.Write("info", "talk_recording", "serial", info.Serial, "wav", wav);
                    MuteLight = 1;
                    // What answers starts now, while the person speaks.
                    if (respond != null)
                    {
                        try { respond.Warm(info.Serial); }
                        catch (Exception e) { Log.Write("warn", "warm_failed", "serial", info.Serial, "error", e.Message); }
                    }
                    RecordResult r;
                    // The light pulses from the end of the recording until the
                    // answer is ready, and between its pieces.
                    try { r = Record(maxSeconds, wav, null, true); }
                    finally { MuteLight = respond != null ? 2 : 0; SetAudio(); }
                    Console.WriteLine("pad " + info.Serial + ": recorded " + (r.Samples / 48000.0).ToString("0.0") + " s" + (r.StoppedByButton ? "" : " (stopped at the longest allowed)") + " -> " + wav);
                    if (respond != null)
                    {
                        if (r.Samples > 0)
                        {
                            respond.Respond(wav, info.Serial, delegate(string piece)
                            {
                                MuteLight = 0;
                                Console.WriteLine("pad " + info.Serial + ": playing " + piece);
                                Log.Write("info", "talk_playing", "serial", info.Serial, "wav", piece, "gain", 1);
                                Play(piece, intervalMs, bufferLength, 1);
                                MuteLight = 2;
                                SetAudio();
                            });
                        }
                        MuteLight = 0;
                        SetAudio();
                    }
                    else if (r.Samples > 0)
                    {
                        double gain = r.Peak > 0 ? Math.Min(20, 29000.0 / r.Peak) : 1;
                        Console.WriteLine("pad " + info.Serial + ": playing " + wav);
                        Log.Write("info", "talk_playing", "serial", info.Serial, "wav", wav, "gain", Math.Round(gain, 1));
                        Play(wav, intervalMs, bufferLength, gain);
                    }
                    else { SetAudio(); }
                }
                finally
                {
                    // A recording that failed leaves what Warm started: let it go.
                    if (respond != null) { respond.Cancel(); }
                    Monitor.Exit(turn);
                }
                // What was pressed during the playback is not a request: skip
                // what queued up, then take the button as it is now.
                Stopwatch skip = Stopwatch.StartNew();
                while (skip.ElapsedMilliseconds < 300)
                {
                    IAsyncResult old = input.BeginRead(buf, 0, buf.Length, null, null);
                    if (!old.AsyncWaitHandle.WaitOne(500)) { break; }
                    input.EndRead(old);
                }
                known = false;
            }
            Log.Write("info", "talk_done", "serial", info.Serial, "talks", talks);
            return talks;
        }

        // The light pulses for 1.5 s, then goes off. Input keeps being read
        // meanwhile, so that nothing pressed now is acted on later.
        void Refuse(byte[] buf)
        {
            MuteLight = 2;
            SetAudio();
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1500)
            {
                IAsyncResult ar = input.BeginRead(buf, 0, buf.Length, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(500)) { break; }
                input.EndRead(ar);
            }
            MuteLight = 0;
            SetAudio();
        }

        // After the microphone is switched off: reads until 200 ordinary reports
        // in a row have arrived, more than any reader's queue holds (SDL asks
        // Windows for 64), or gives up after 3 seconds.
        void Drain(byte[] buf)
        {
            int ordinary = 0, mic = 0;
            Stopwatch clock = Stopwatch.StartNew();
            while (ordinary < 200 && clock.ElapsedMilliseconds < 3000)
            {
                IAsyncResult ar = input.BeginRead(buf, 0, buf.Length, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(1000)) { break; }
                int n = input.EndRead(ar);
                if (n > 1 && buf[0] == 0x31 && (buf[1] & 0x0F) == 2) { mic++; ordinary = 0; } else { ordinary++; }
            }
            Log.Write(ordinary >= 200 ? "info" : "warn", "drain_done", "ordinary_in_a_row", ordinary, "late_mic_reports", mic, "ms", clock.ElapsedMilliseconds);
        }

        public RecordResult Record(double seconds, string wavPath, string rawPath)
        {
            return Record(seconds, wavPath, rawPath, false);
        }

        // With stopOnMicButton, a press of the microphone button ends the
        // recording and seconds is only the longest it may last. The button is
        // expected to be down when this starts (its press started the
        // recording), so it must be seen released before a press counts.
        public RecordResult Record(double seconds, string wavPath, string rawPath, bool stopOnMicButton)
        {
            Hold.Enter();
            RecordResult r = new RecordResult();
            int err;
            IntPtr dec = Opus.opus_decoder_create(48000, 1, out err);
            if (err != 0 || dec == IntPtr.Zero) { throw new InvalidOperationException("opus_decoder_create failed: " + err); }
            short[] pcm = new short[(int)(seconds * 48000) + 48000];
            short[] frame = new short[5760];
            byte[] buf = new byte[info.InputLength];
            byte[] packet = new byte[Protocol.MicOpusSize];
            int last = -1;
            bool released = false, stopped = false;
            int releasedAt = 0;
            Dictionary<int, int> seen = new Dictionary<int, int>();
            FileStream raw = rawPath == null ? null : new FileStream(rawPath, FileMode.Create, FileAccess.Write);
            Log.Write("info", "record_start", "seconds", seconds, "wav", wavPath);
            SetAudio();
            SetMic(true);
            Stopwatch clock = Stopwatch.StartNew();
            Stopwatch again = Stopwatch.StartNew();
            try
            {
                while (clock.Elapsed.TotalSeconds < seconds && !stopped)
                {
                    IAsyncResult ar = input.BeginRead(buf, 0, buf.Length, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(2000))
                    {
                        throw new TimeoutException("the pad sent nothing for 2 seconds; is it still connected?");
                    }
                    int n = input.EndRead(ar);
                    if (n < 3) { r.OtherReports++; continue; }
                    int key = buf[0] << 8 | (buf[0] == 0x31 ? buf[1] & 0x0F : 0);
                    int c; seen.TryGetValue(key, out c); seen[key] = c + 1;
                    if (buf[0] != 0x31) { r.OtherReports++; continue; }
                    int type = buf[1] & 0x0F;
                    if (type != 2)
                    {
                        r.StatePackets++;
                        if (stopOnMicButton && type == 1 && n > Protocol.ButtonsByte)
                        {
                            bool down = (buf[Protocol.ButtonsByte] & Protocol.MicButton) != 0;
                            if (!down) { if (!released) { releasedAt = r.Samples; } released = true; }
                            else if (released) { stopped = true; }
                        }
                    }
                    else
                    {
                        if (!Protocol.InputCrcOk(buf, n)) { r.BadCrc++; continue; }
                        if (last >= 0) { r.Lost += ((buf[2] - last - 1) & 0xFF); }
                        last = buf[2];
                        Buffer.BlockCopy(buf, 3, packet, 0, Protocol.MicOpusSize);
                        if (raw != null) { raw.Write(BitConverter.GetBytes((int)clock.ElapsedMilliseconds), 0, 4); raw.Write(buf, 0, Protocol.BtInputLength); }
                        int got = Opus.opus_decode(dec, packet, packet.Length, frame, frame.Length, 0);
                        if (got <= 0) { r.DecodeErrors++; continue; }
                        if (r.Samples + got > pcm.Length) { break; }
                        Array.Copy(frame, 0, pcm, r.Samples, got);
                        r.Samples += got;
                        r.MicPackets++;
                    }
                    // The switch is repeated: a pad that reconnected, or that another
                    // program wrote to, would otherwise fall silent without a trace.
                    if (r.MicPackets == 0 && again.ElapsedMilliseconds > 500) { SetMic(true); again.Restart(); }
                }
            }
            finally
            {
                try
                {
                    try { SetMic(false); } catch (Exception e) { Log.Write("warn", "mic_off_failed", "error", e.Message); }
                    try { Drain(buf); } catch (Exception e) { Log.Write("warn", "drain_failed", "error", e.Message); }
                }
                finally { Hold.Exit(); }
                if (raw != null) { raw.Dispose(); }
                Opus.opus_decoder_destroy(dec);
            }
            if (stopOnMicButton)
            {
                // The microphone sits beside its button and records the button
                // itself: measured 2026-09-28, clicks of 30 to 70 % of full scale
                // up to 0.36 s into the recording (press and release), louder
                // than the voice, and the closing press about half a second
                // before the end. Both ends are cut, then faded over 20 ms.
                int head = Math.Min(r.Samples, releasedAt + 48000 * 35 / 100);
                int tail = stopped ? 48000 * 65 / 100 : 0;
                int kept = Math.Max(0, r.Samples - head - tail);
                Array.Copy(pcm, head, pcm, 0, kept);
                int fade = Math.Min(960, kept / 2);
                for (int i = 0; i < fade; i++)
                {
                    pcm[i] = (short)(pcm[i] * i / fade);
                    pcm[kept - 1 - i] = (short)(pcm[kept - 1 - i] * i / fade);
                }
                Log.Write("info", "trimmed", "head_samples", head, "tail_samples", tail, "kept_samples", kept);
                r.Samples = kept;
            }
            double sum = 0;
            for (int i = 0; i < r.Samples; i++)
            {
                int a = Math.Abs((int)pcm[i]);
                if (a > r.Peak) { r.Peak = a; }
                sum += (double)pcm[i] * pcm[i];
            }
            r.Rms = r.Samples > 0 ? Math.Sqrt(sum / r.Samples) : 0;
            Wav.Write(wavPath, pcm, r.Samples, 48000, 1);
            StringBuilder kinds = new StringBuilder();
            foreach (KeyValuePair<int, int> k in seen) { kinds.AppendFormat("{0:x2}/{1}x{2} ", k.Key >> 8, k.Key & 0xFF, k.Value); }
            r.StoppedByButton = stopped;
            Log.Write("info", "record_done", "stopped_by_button", stopped, "mic_packets", r.MicPackets, "state_packets", r.StatePackets, "other", r.OtherReports,
                "bad_crc", r.BadCrc, "lost", r.Lost, "decode_errors", r.DecodeErrors, "samples", r.Samples,
                "audio_seconds", r.Samples / 48000.0, "peak", r.Peak, "rms", Math.Round(r.Rms, 1), "reports", kinds.ToString().Trim());
            return r;
        }

        // intervalMs is the pace of the audio reports, each carrying two 10 ms
        // Opus frames. DS5Dongle feeds the pad one report per 1024 frames of a
        // 48 kHz stream (21.333 ms) and squeezes 512 frames into each Opus frame;
        // speed does the same squeeze here so that pitch and pace stay true.
        public void Play(string wavPath, double intervalMs, int bufferLength, double gain)
        {
            SetAudio();
            int rate, channels;
            short[] src = Wav.Read(wavPath, out rate, out channels);
            int frames = src.Length / channels;
            double speed = intervalMs / 20.0;
            int outFrames = (int)(frames / speed);
            short[] pcm = new short[(outFrames / 960 + 1) * 960 * 2];
            for (int i = 0; i < outFrames; i++)
            {
                double pos = i * speed;
                int a = (int)pos; int b = Math.Min(a + 1, frames - 1);
                double f = pos - a;
                for (int ch = 0; ch < 2; ch++)
                {
                    int s = channels == 2 ? ch : 0;
                    double v = (src[a * channels + s] * (1 - f) + src[b * channels + s] * f) * gain;
                    pcm[i * 2 + ch] = (short)Math.Max(-32768, Math.Min(32767, v));
                }
            }

            int err;
            IntPtr enc = Opus.opus_encoder_create(48000, 2, Opus.ApplicationAudio, out err);
            if (err != 0 || enc == IntPtr.Zero) { throw new InvalidOperationException("opus_encoder_create failed: " + err); }
            Opus.opus_encoder_ctl(enc, Opus.SetExpertFrameDuration, Opus.Framesize10ms);
            Opus.opus_encoder_ctl(enc, Opus.SetBitrate, Protocol.SpeakerOpusSize * 8 * 100);
            Opus.opus_encoder_ctl(enc, Opus.SetVbr, 0);
            int reports = pcm.Length / 2 / 960;
            byte[][] encoded = new byte[reports * 2][];
            short[] one = new short[960];
            int wrong = 0;
            for (int i = 0; i < encoded.Length; i++)
            {
                Array.Copy(pcm, i * 960, one, 0, 960);
                encoded[i] = new byte[Protocol.SpeakerOpusSize];
                int n = Opus.opus_encode(enc, one, 480, encoded[i], Protocol.SpeakerOpusSize);
                if (n != Protocol.SpeakerOpusSize) { wrong++; }
            }
            Opus.opus_encoder_destroy(enc);
            Log.Write("info", "play_start", "wav", wavPath, "source_seconds", frames / 48000.0, "reports", reports,
                "interval_ms", intervalMs, "buffer_length", bufferLength, "gain", gain, "frames_not_200_bytes", wrong);

            double ticksPerReport = intervalMs * Stopwatch.Frequency / 1000.0;
            double worst = 0; int late = 0;
            Native.timeBeginPeriod(1);
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < reports; i++)
                {
                    long due = (long)(i * ticksPerReport);
                    while (true)
                    {
                        long left = due - clock.ElapsedTicks;
                        if (left <= 0) { break; }
                        if (left > Stopwatch.Frequency / 500) { Thread.Sleep(1); } else { Thread.SpinWait(50); }
                    }
                    double lateMs = (clock.ElapsedTicks - due) * 1000.0 / Stopwatch.Frequency;
                    if (lateMs > worst) { worst = lateMs; }
                    if (lateMs > 5) { late++; }
                    packetCounter = (packetCounter + 2) & 0xFF;
                    Send(Protocol.AudioReport(seq++, packetCounter, KeepMic, bufferLength, encoded[i * 2], encoded[i * 2 + 1]));
                }
            }
            finally { Native.timeEndPeriod(1); }
            Log.Write("info", "play_done", "reports", reports, "elapsed_seconds", Math.Round(clock.Elapsed.TotalSeconds, 3),
                "late_over_5ms", late, "worst_late_ms", Math.Round(worst, 2));
        }

        public void Dispose()
        {
            input.Dispose();
            output.Dispose();
        }
    }

    // The same action on several pads at once, one thread per pad, for when
    // nobody knows which pad the person is holding.
    public static class Group
    {
        static void Each(Pad[] pads, Action<int> work)
        {
            Exception[] failed = new Exception[pads.Length];
            Thread[] threads = new Thread[pads.Length];
            for (int i = 0; i < pads.Length; i++)
            {
                int n = i;
                threads[i] = new Thread(delegate()
                {
                    try { work(n); }
                    catch (Exception e) { failed[n] = e; Log.Write("error", "pad_failed", "serial", pads[n].Info.Serial, "error", e.Message); }
                });
                threads[i].Start();
            }
            foreach (Thread t in threads) { t.Join(); }
            foreach (Exception e in failed) { if (e != null) { throw new InvalidOperationException(e.Message, e); } }
        }

        public static void Play(Pad[] pads, string wavPath, double intervalMs, int bufferLength, double gain)
        {
            Each(pads, delegate(int i) { pads[i].Play(wavPath, intervalMs, bufferLength, gain); });
        }

        public static void Talk(Pad[] pads, double windowSeconds, double maxSeconds, string folder, double intervalMs, int bufferLength, IResponder respond)
        {
            object turn = new object();
            Each(pads, delegate(int i) { pads[i].Talk(windowSeconds, maxSeconds, folder, intervalMs, bufferLength, turn, respond); });
        }

        // One WAV per pad, named <prefix>-<serial>.wav; returns the path of the loudest.
        public static string Record(Pad[] pads, double seconds, string prefix, out RecordResult best)
        {
            RecordResult[] results = new RecordResult[pads.Length];
            string[] paths = new string[pads.Length];
            Each(pads, delegate(int i)
            {
                paths[i] = prefix + "-" + pads[i].Info.Serial + ".wav";
                results[i] = pads[i].Record(seconds, paths[i], null);
            });
            int top = 0;
            for (int i = 1; i < pads.Length; i++) { if (results[i].Rms > results[top].Rms) { top = i; } }
            best = results[top];
            Log.Write("info", "loudest_pad", "serial", pads[top].Info.Serial, "rms", Math.Round(best.Rms, 1), "peak", best.Peak, "wav", paths[top]);
            return paths[top];
        }
    }

    // The permanent form of Talk: watches every pad connected over Bluetooth,
    // takes up the pads that connect later and lets go of those that leave.
    public static class Service
    {
        public static void Run(double maxSeconds, string folder, double intervalMs, int bufferLength, int micVolume, int audioControl, int speakerVolume, IResponder respond)
        {
            bool first;
            using (Mutex one = new Mutex(true, "mypc-padaudio-service", out first))
            {
                if (!first) { throw new InvalidOperationException("the pad audio service is already running"); }
                object turn = new object();
                Dictionary<string, Thread> watched = new Dictionary<string, Thread>();
                Log.Write("info", "service_start", "max_seconds", maxSeconds);
                bool warned = false;
                while (true)
                {
                    bool sony = Hold.Running(new string[] { "PlayStationAccessories" }) != null;
                    if (sony && !warned) { Log.Write("warn", "playstation_accessories_running", "effect", "the microphone sound may come in fragments"); }
                    warned = sony;

                    List<string> gone = new List<string>();
                    foreach (KeyValuePair<string, Thread> w in watched) { if (!w.Value.IsAlive) { gone.Add(w.Key); } }
                    foreach (string path in gone) { watched.Remove(path); }

                    foreach (PadInfo p in Pad.Find(false))
                    {
                        if (!p.Bluetooth || watched.ContainsKey(p.Path)) { continue; }
                        PadInfo found = p;
                        Thread t = new Thread(delegate()
                        {
                            try
                            {
                                using (Pad pad = new Pad(found))
                                {
                                    pad.MicVolume = micVolume; pad.AudioControl = audioControl; pad.SpeakerVolume = speakerVolume;
                                    pad.Talk(0, maxSeconds, folder, intervalMs, bufferLength, turn, respond);
                                }
                            }
                            catch (Exception e) { Log.Write("info", "pad_left", "serial", found.Serial, "reason", e.Message); }
                        });
                        t.IsBackground = true;
                        watched[p.Path] = t;
                        t.Start();
                    }
                    Thread.Sleep(3000);
                }
            }
        }
    }

    public static class Setup
    {
        public static void Init(string logPath, string opusDll)
        {
            Log.Open(logPath);
            Log.Write("info", "start", "log", logPath, "bits", IntPtr.Size * 8);
            Opus.Load(opusDll);
        }
    }
}
