using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace XboxGamingBarHelper.RTSS.Interop
{
    // Plain C# access to RivaTuner Statistics Server's shared memory (1.9.9). Replaces the
    // RTSSSharedMemoryNET.dll fork ("RTSSSharedMemoryNET-EmbedGraph"), a C++/CLI .NET Framework
    // assembly that .NET 10 cannot load. Same API surface as that DLL's parts GoTweaks used:
    // OSD (own one OSD slot, write its text, embedded graphs via <G=...> tags), OSD.GetAppEntries,
    // AppEntry and AppFlags. (Finding RTSS is Shared.Utilities.RTSSHelper.)
    //
    // Layout and protocol follow RTSS's SDK: SDK\Include\RTSSSharedMemory.h (v2.x memory layout)
    // and SDK\Samples\SharedMemory\RTSSSharedMemorySample (UpdateOSD / EmbedGraph / ReleaseOSD).

    [Flags]
    public enum AppFlags : uint
    {
        None = 0,
        // API usage (2.10+ layout: a value, not bits, in the low word)
        OpenGL = 0x00000001,
        DirectDraw = 0x00000002,
        Direct3D8 = 0x00000003,
        Direct3D9 = 0x00000004,
        Direct3D9Ex = 0x00000005,
        Direct3D10 = 0x00000006,
        Direct3D11 = 0x00000007,
        Direct3D12 = 0x00000008,
        Direct3D12AFR = 0x00000009,
        Vulkan = 0x0000000A,
        /// <summary>APPFLAG_API_USAGE_MASK: matches every app that uses any 3D API.</summary>
        MASK = 0x0000FFFF,
        ArchitectureX64 = 0x00010000,
        ArchitectureUWP = 0x00020000,
    }

    /// <summary>One 3D application RTSS is hooked into (RTSS_SHARED_MEMORY_APP_ENTRY).</summary>
    public sealed class AppEntry
    {
        public int ProcessId { get; internal set; }
        public string Name { get; internal set; }
        public AppFlags Flags { get; internal set; }

        /// <summary>Frames per second over RTSS's last measurement period (1000 * frames / period).</summary>
        public uint InstantaneousFrames { get; internal set; }

        /// <summary>Last frame time in microseconds.</summary>
        public uint InstantaneousFrameTime { get; internal set; }

        /// <summary>Frame time history in microseconds (ring buffer of 1024), or null on old RTSS.</summary>
        public uint[] StatFrameTimeBuf { get; internal set; }

        /// <summary>Next write position in <see cref="StatFrameTimeBuf"/>.</summary>
        public uint StatFrameTimeBufPos { get; internal set; }

        public override string ToString() => $"{ProcessId}:{Name}, {Flags}";
    }

    /// <summary>
    /// Owns one RTSS OSD slot (named after <c>owner</c>) and writes its text. Text may contain
    /// RTSS formatting tags plus graph tags, which are turned into embedded graph objects:
    /// <c>&lt;G=&lt;FT&gt;&gt;</c> / <c>&lt;G=%Frametime%&gt;</c> (frametime graph) and
    /// <c>&lt;G=&lt;FR&gt;&gt;</c> / <c>&lt;G=%Framerate%&gt;</c> (framerate graph), drawn by RTSS
    /// from the hooked game's own data, 32 characters wide and 2 lines tall.
    /// </summary>
    public sealed unsafe class OSD : IDisposable
    {
        private const string MappingName = "RTSSSharedMemoryV2";
        private const uint Signature = 0x52545353;           // 'RTSS'
        private const uint GraphSignature = 0x47523030;      // 'GR00'
        private const uint GraphFlagFramerate = 2;           // RTSS_EMBEDDED_OBJECT_GRAPH_FLAG_FRAMERATE
        private const uint GraphFlagFrametime = 4;           // RTSS_EMBEDDED_OBJECT_GRAPH_FLAG_FRAMETIME
        private const uint VersionTextEx = 0x00020007;       // szOSDEx (4096 chars) supported
        private const uint VersionEmbedded = 0x0002000C;     // embedded objects supported
        private const uint VersionBusyLock = 0x0002000E;     // dwBusy OSD lock supported

        // RTSS_SHARED_MEMORY_OSD_ENTRY field offsets
        private const int OsdTextOffset = 0;                 // szOSD[256]
        private const int OsdOwnerOffset = 256;              // szOSDOwner[256]
        private const int OsdOwnerSize = 256;
        private const int OsdTextExOffset = 512;             // szOSDEx[4096]
        private const int OsdTextExSize = 4096;
        private const int OsdBufferOffset = 4608;            // buffer[262144]
        private const int OsdBufferSize = 262144;

        private static readonly Regex GraphTag = new Regex(@"<G=(<FT>|<FR>|%Frametime%|%Framerate%)>", RegexOptions.Compiled);

        private readonly string owner;
        private bool disposed;

        public OSD(string owner)
        {
            if (string.IsNullOrEmpty(owner) || owner.Length >= OsdOwnerSize)
                throw new ArgumentException("OSD owner must be 1-255 characters", nameof(owner));
            this.owner = owner;

            // Claim a slot now, like the old DLL: throws while RTSS's memory isn't there yet.
            using (var view = SharedView.Open())
            {
                if (FindSlot(view, claim: true) == null)
                    throw new InvalidOperationException("No free RTSS OSD slot");
            }
        }

        /// <summary>Replaces this slot's OSD text (graph tags are embedded first).</summary>
        public void Update(string text)
        {
            if (disposed) throw new ObjectDisposedException(nameof(OSD));
            using (var view = SharedView.Open())
            {
                byte* entry = FindSlot(view, claim: true);
                if (entry == null) throw new InvalidOperationException("No free RTSS OSD slot");

                string processed = EmbedGraphs(view, entry, text ?? string.Empty);
                byte[] bytes = ToAnsi(processed);

                bool useEx = view.Version >= VersionTextEx;
                int offset = useEx ? OsdTextExOffset : OsdTextOffset;
                int size = useEx ? OsdTextExSize : 256;
                int length = Math.Min(bytes.Length, size - 1);

                if (useEx && view.Version >= VersionBusyLock)
                {
                    // Bit 0 of dwBusy is set while the renderer reads the OSD; skip this refresh then.
                    int* busy = (int*)(view.Base + 36);
                    if ((Interlocked.Or(ref *busy, 1) & 1) != 0) return;
                    try { WriteString(entry + offset, size, bytes, length); }
                    finally { Volatile.Write(ref *busy, 0); }
                }
                else
                {
                    WriteString(entry + offset, size, bytes, length);
                }

                IncrementFrame(view);
            }
        }

        /// <summary>Frees this slot (clears its text and objects).</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                using (var view = SharedView.Open())
                {
                    for (uint i = 1; i < view.OsdArrSize; i++)
                    {
                        byte* entry = view.OsdEntry(i);
                        if (OwnerIs(entry, owner))
                        {
                            new Span<byte>(entry, (int)view.OsdEntrySize).Clear();
                            IncrementFrame(view);
                        }
                    }
                }
            }
            catch
            {
                // RTSS gone: nothing to release.
            }
        }

        /// <summary>Every app RTSS is hooked into whose API flags match <paramref name="flags"/>.</summary>
        public static AppEntry[] GetAppEntries(AppFlags flags)
        {
            var list = new List<AppEntry>();
            using (var view = SharedView.Open())
            {
                for (uint i = 0; i < view.AppArrSize; i++)
                {
                    byte* p = view.AppEntry(i);
                    var raw = (AppEntryHead*)p;
                    if (raw->dwProcessID == 0) continue;
                    if (((uint)flags & raw->dwFlags) == 0) continue;

                    var entry = new AppEntry
                    {
                        ProcessId = (int)raw->dwProcessID,
                        Name = ReadAnsi(raw->szName, 260),
                        Flags = (AppFlags)raw->dwFlags,
                        InstantaneousFrameTime = raw->dwFrameTime,
                    };

                    uint period = raw->dwTime1 - raw->dwTime0;
                    entry.InstantaneousFrames = period > 0
                        ? (uint)Math.Round(1000.0 * raw->dwFrames / period)
                        : raw->dwFrames;

                    // The frame time history exists in 2.11+ entries; check the entry is big enough.
                    if (view.AppEntrySize >= (uint)sizeof(AppEntryHead))
                    {
                        var buf = new uint[1024];
                        for (int k = 0; k < buf.Length; k++) buf[k] = raw->dwStatFrameTimeBuf[k];
                        entry.StatFrameTimeBuf = buf;
                        entry.StatFrameTimeBufPos = raw->dwStatFrameTimeBufPos % 1024;
                    }

                    list.Add(entry);
                }
            }
            return list.ToArray();
        }

        // ---- internals ----

        private byte* FindSlot(SharedView view, bool claim)
        {
            // 1st pass: the slot we already own. 2nd pass: claim the first free one. Slot 0 is
            // left to primary clients (MSI Afterburner etc.), as the SDK sample does.
            for (int pass = 0; pass < (claim ? 2 : 1); pass++)
            {
                for (uint i = 1; i < view.OsdArrSize; i++)
                {
                    byte* entry = view.OsdEntry(i);
                    if (pass == 1 && entry[OsdOwnerOffset] == 0)
                    {
                        byte[] name = ToAnsi(owner);
                        WriteString(entry + OsdOwnerOffset, OsdOwnerSize, name, Math.Min(name.Length, OsdOwnerSize - 1));
                    }
                    if (OwnerIs(entry, owner)) return entry;
                }
            }
            return null;
        }

        private string EmbedGraphs(SharedView view, byte* entry, string text)
        {
            if (text.IndexOf("<G=", StringComparison.Ordinal) < 0) return text;
            if (view.Version < VersionEmbedded) return GraphTag.Replace(text, string.Empty);

            uint offset = 0;
            return GraphTag.Replace(text, match =>
            {
                bool frametime = match.Groups[1].Value == "<FT>" || match.Groups[1].Value == "%Frametime%";
                uint size = (uint)sizeof(EmbeddedGraph);
                if (offset + size > OsdBufferSize) return string.Empty;

                var graph = (EmbeddedGraph*)(entry + OsdBufferOffset + offset);
                graph->dwSignature = GraphSignature;
                graph->dwSize = size;
                graph->dwWidth = -32;   // 32 characters
                graph->dwHeight = -2;   // 2 lines
                graph->dwMargin = 1;
                graph->dwFlags = frametime ? GraphFlagFrametime : GraphFlagFramerate;
                graph->fltMin = 0f;
                graph->fltMax = frametime ? 50000f : 200f; // frametime in microseconds
                graph->dwDataCount = 0;                   // RTSS supplies the data itself

                string tag = $"<OBJ={offset:X8}>";
                offset += size;
                return tag;
            });
        }

        private static void IncrementFrame(SharedView view)
        {
            Interlocked.Increment(ref *(int*)(view.Base + 32));
        }

        private static bool OwnerIs(byte* entry, string owner)
        {
            byte* name = entry + OsdOwnerOffset;
            if (name[0] == 0) return false;
            return ReadAnsi(name, OsdOwnerSize) == owner;
        }

        private static byte[] ToAnsi(string s)
        {
            // Same conversion as the old C++/CLI DLL (Marshal::StringToHGlobalAnsi): the system
            // ANSI code page, which is what RTSS renders (e.g. ° in temperatures).
            IntPtr p = Marshal.StringToHGlobalAnsi(s);
            try
            {
                int len = 0;
                while (((byte*)p)[len] != 0) len++;
                var bytes = new byte[len];
                Marshal.Copy(p, bytes, 0, len);
                return bytes;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static string ReadAnsi(byte* p, int max)
        {
            int len = 0;
            while (len < max && p[len] != 0) len++;
            return Marshal.PtrToStringAnsi((IntPtr)p, len);
        }

        private static void WriteString(byte* dest, int size, byte[] bytes, int length)
        {
            var span = new Span<byte>(dest, size);
            bytes.AsSpan(0, length).CopyTo(span);
            span[length] = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EmbeddedGraph
        {
            // RTSS_EMBEDDED_OBJECT header
            public uint dwSignature;
            public uint dwSize;
            public int dwWidth;
            public int dwHeight;
            public int dwMargin;
            // RTSS_EMBEDDED_OBJECT_GRAPH
            public uint dwFlags;
            public float fltMin;
            public float fltMax;
            public uint dwDataCount;
        }

        // RTSS_SHARED_MEMORY_APP_ENTRY up to dwStatFrameTimeBufFramerate (all GoTweaks reads).
        [StructLayout(LayoutKind.Sequential)]
        private struct AppEntryHead
        {
            public uint dwProcessID;
            public fixed byte szName[260];
            public uint dwFlags;
            public uint dwTime0;
            public uint dwTime1;
            public uint dwFrames;
            public uint dwFrameTime;
            public uint dwStatFlags;
            public uint dwStatTime0;
            public uint dwStatTime1;
            public uint dwStatFrames;
            public uint dwStatCount;
            public uint dwStatFramerateMin;
            public uint dwStatFramerateAvg;
            public uint dwStatFramerateMax;
            public uint dwOSDX;
            public uint dwOSDY;
            public uint dwOSDPixel;
            public uint dwOSDColor;
            public uint dwOSDFrame;
            public uint dwScreenCaptureFlags;
            public fixed byte szScreenCapturePath[260];
            public uint dwOSDBgndColor;
            public uint dwVideoCaptureFlags;
            public fixed byte szVideoCapturePath[260];
            public uint dwVideoFramerate;
            public uint dwVideoFramesize;
            public uint dwVideoFormat;
            public uint dwVideoQuality;
            public uint dwVideoCaptureThreads;
            public uint dwScreenCaptureQuality;
            public uint dwScreenCaptureThreads;
            public uint dwAudioCaptureFlags;
            public uint dwVideoCaptureFlagsEx;
            public uint dwAudioCaptureFlags2;
            public uint dwStatFrameTimeMin;
            public uint dwStatFrameTimeAvg;
            public uint dwStatFrameTimeMax;
            public uint dwStatFrameTimeCount;
            public fixed uint dwStatFrameTimeBuf[1024];
            public uint dwStatFrameTimeBufPos;
            public uint dwStatFrameTimeBufFramerate;
        }

        /// <summary>A mapped view of RTSS's shared memory, validated ('RTSS', v2.x).</summary>
        private sealed class SharedView : IDisposable
        {
            private const uint FILE_MAP_ALL_ACCESS = 0xF001F;

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool UnmapViewOfFile(IntPtr address);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);

            private IntPtr mapping;
            private IntPtr address;

            public byte* Base => (byte*)address;
            public uint Version => ((uint*)Base)[1];
            public uint AppEntrySize => ((uint*)Base)[2];
            public uint AppArrOffset => ((uint*)Base)[3];
            public uint AppArrSize => ((uint*)Base)[4];
            public uint OsdEntrySize => ((uint*)Base)[5];
            public uint OsdArrOffset => ((uint*)Base)[6];
            public uint OsdArrSize => ((uint*)Base)[7];

            public byte* OsdEntry(uint index) => Base + OsdArrOffset + (long)index * OsdEntrySize;
            public byte* AppEntry(uint index) => Base + AppArrOffset + (long)index * AppEntrySize;

            public static SharedView Open()
            {
                var view = new SharedView();
                view.mapping = OpenFileMapping(FILE_MAP_ALL_ACCESS, false, MappingName);
                if (view.mapping == IntPtr.Zero)
                    throw new FileNotFoundException("RTSS shared memory is not available (RTSS not running?)");

                view.address = MapViewOfFile(view.mapping, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
                if (view.address == IntPtr.Zero)
                {
                    view.Dispose();
                    throw new IOException($"Could not map RTSS shared memory (error {Marshal.GetLastWin32Error()})");
                }

                uint* header = (uint*)view.address;
                if (header[0] != Signature || header[1] < 0x00020000)
                {
                    view.Dispose();
                    throw new InvalidDataException("Failed to validate RTSS Shared Memory structure");
                }
                return view;
            }

            public void Dispose()
            {
                if (address != IntPtr.Zero) { UnmapViewOfFile(address); address = IntPtr.Zero; }
                if (mapping != IntPtr.Zero) { CloseHandle(mapping); mapping = IntPtr.Zero; }
            }
        }
    }
}
