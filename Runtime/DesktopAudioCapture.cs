/*
                                     *=--
                                     =@@@%%#*%@*
                                     -@@@@@@@%=:
         =+                      .+#@@@@@@@@@*+:
        +@@+                  .-*@@@@@@@@@@#.
       .@@@#                 =@@@@@@@@@@@%-
       =@@@#             .-*@@@@@@@@@@@@@:
       -@@@#         .-+%@@@@@@@@@@@@@@@%
        :%@@@*=::-=*#@@@@@@@@@@@@@@@@@@@.
          -+*@@@@@@@@@@@@@@@@@@@@@@@@@@@%#*:
            =%@@@@@@@@@@@@@@@@@@@%#+--=+*#%@@+.
          -%@@@@@@@@@@@******+=:.          :@@@+
         +@@@@@@@@@+=+-                     .::+:
      :*@@@@@@@@#=
    :#@@#+-----.
  :*@%@:
 -%*..:

  ___ _  _ ___ ___   ___ _  _ ___ ___
 / __| || | __| _ \ / __| || | __| _ \
 \__ \ __ | _||  _/ \__ \ __ | _||  _/
 |___/_||_|___|_|   |___/_||_|___|_|

  ================ SHEP SHEP =================
  PRODUCT: AudioLink Desktop Audio
  SOURCE: DesktopAudioCapture.cs
  ROLE: WASAPI loopback capture, ring buffer, carrier feed and tone detector
  =============================================
  (c) 2026 Shep Shep. MIT License, see LICENSE.md.
  SHEP SHEP is a registered trademark in Germany.
  =============================================
*/

// Captures what Windows plays on the default output device (WASAPI loopback)
// and converts it to interleaved stereo float. Pure C#, no Unity dependency,
// so it can be tested outside the editor.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShepShep.AudioTools
{
    public sealed class DesktopAudioCapture : IDisposable
    {
        public delegate void BlockHandler(float[] stereo, int frames);

        readonly int outputRate;
        readonly object gate = new object();
        readonly float[] ring;
        readonly int ringFrames, minCushion, maxCushion, cushionStep, windowLength, hardCapFrames;
        int ringHead, ringCount, minSlack = int.MaxValue, windowFrames, windowShort, cleanWindows;
        volatile int cushionFrames;
        volatile float avgBacklog;
        long trimmedFrames;

        Thread thread;
        volatile bool stopRequested;
        volatile string deviceName, format, lastError, stageName = "Not started";
        volatile int sourceRate, sourceChannels, lastPacketTick, stageTick;
        volatile bool running, everReceived, gaveUp;

        string stage
        {
            get { return stageName; }
            set { stageName = value; stageTick = Environment.TickCount; }
        }
        long capturedFrames, underrunFrames;
        float peakHold;

        // Scratch buffers, capture thread only.
        float[] samples, inStereo, outStereo;
        short[] s16;
        int[] s32;
        byte[] b24;
        double resamplePos;
        float prevL, prevR;

        /// <summary>Optional, invoked on the capture thread with each converted block.</summary>
        public BlockHandler Tap;

        public DesktopAudioCapture(int outputSampleRate)
        {
            if (outputSampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
            outputRate = outputSampleRate;
            ringFrames = outputSampleRate; // one second
            ring = new float[ringFrames * 2];
            minCushion = outputSampleRate * 4 / 1000;    // start with 4 ms against packet timing jitter
            maxCushion = outputSampleRate * 60 / 1000;   // bursty readers may need more, never above 60 ms
            cushionStep = outputSampleRate / 100;        // grow by up to 10 ms after a window with gaps
            cushionFrames = minCushion;
            windowLength = outputSampleRate / 2;         // judge the cushion over half a second
            hardCapFrames = outputSampleRate / 20;       // never serve audio more than ~50 ms beyond the cushion
        }

        public int OutputSampleRate => outputRate;
        public string DeviceName => deviceName;
        public string Format => format;
        public string LastError => lastError;
        /// <summary>Last step the capture thread entered; useful when it stalls.</summary>
        public string Stage => stage;
        public int StageMilliseconds => unchecked(Environment.TickCount - stageTick);
        /// <summary>True after access was denied or repeated failures; this instance will not retry.</summary>
        public bool GaveUp => gaveUp;
        /// <summary>
        /// Stuck inside a Windows call that opens the capture stream. Antivirus microphone
        /// protection (for example Kaspersky) holds this call until its prompt is answered.
        /// </summary>
        public bool WaitingForPermission
        {
            get
            {
                string s = stageName;
                return !running && ThreadAlive && StageMilliseconds > 1500
                    && (s == "Open audio client" || s == "Initialize loopback");
            }
        }
        public int SourceSampleRate => sourceRate;
        public int SourceChannels => sourceChannels;
        public bool Running => running;
        public bool ThreadAlive { get { var t = thread; return t != null && t.IsAlive; } }
        public long CapturedFrames => Interlocked.Read(ref capturedFrames);
        public long UnderrunFrames => Interlocked.Read(ref underrunFrames);
        public int BufferedFrames => Volatile.Read(ref ringCount);
        /// <summary>Audio the buffer keeps back against uneven delivery and reads, in frames.</summary>
        public int CushionFrames => cushionFrames;
        /// <summary>Smoothed audio left in the buffer after each read, in frames.</summary>
        public float AverageBacklogFrames => avgBacklog;
        public long TrimmedFrames => Interlocked.Read(ref trimmedFrames);
        public int MillisecondsSinceLastPacket => everReceived ? unchecked(Environment.TickCount - lastPacketTick) : int.MaxValue;
        public float TakePeak() => Interlocked.Exchange(ref peakHold, 0f);

        /// <summary>Single use: after Stop, create a new instance.</summary>
        public void Start()
        {
            if (thread != null || stopRequested) return;
            thread = new Thread(ThreadMain) { IsBackground = true, Name = "Desktop Audio Capture", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public void Stop()
        {
            stopRequested = true;
            var t = thread;
            if (t != null && !t.Join(1000)) lastError = "Capture thread is still inside '" + stageName + "'; it will exit once Windows returns.";
            thread = null;
            running = false;
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Reads <paramref name="frames"/> stereo frames for the audio thread. Keeps only a small,
        /// self-adjusting cushion of buffered audio, so it adds no more delay than packet timing needs.
        /// Missing frames are zero.
        /// </summary>
        public int Read(float[] stereo, int frames)
        {
            int got;
            lock (gate)
            {
                // After a stall or a mode switch, jump straight to recent audio.
                if (ringCount - frames > cushionFrames + hardCapFrames) Drop(ringCount - frames - cushionFrames);
                got = Math.Min(frames, ringCount);
                int tail = (ringHead - ringCount + ringFrames) % ringFrames;
                int first = Math.Min(got, ringFrames - tail);
                Array.Copy(ring, tail * 2, stereo, 0, first * 2);
                if (got > first) Array.Copy(ring, 0, stereo, first * 2, (got - first) * 2);
                ringCount -= got;
                if (got < frames && running && MillisecondsSinceLastPacket < 100) windowShort += frames - got;

                // Track the lowest leftover over half a second, then drop anything above the cushion.
                // The cushion grows after a window with gaps and shrinks by 1 ms after ten clean seconds,
                // so bursty readers (the live clip carrier reads once per editor frame) stop running dry.
                minSlack = got < frames ? 0 : Math.Min(minSlack, ringCount);
                windowFrames += frames;
                if (windowFrames >= windowLength)
                {
                    if (windowShort > 0)
                    {
                        cushionFrames = Math.Min(maxCushion, cushionFrames + Math.Min(windowShort, cushionStep));
                        cleanWindows = 0;
                    }
                    else if (++cleanWindows >= 20)
                    {
                        cushionFrames = Math.Max(minCushion, cushionFrames - outputRate / 1000);
                        cleanWindows = 0;
                    }
                    if (minSlack > cushionFrames) Drop(minSlack - cushionFrames);
                    minSlack = int.MaxValue;
                    windowFrames = 0;
                    windowShort = 0;
                }
                avgBacklog += (ringCount - avgBacklog) * 0.05f;
            }
            if (got < frames)
            {
                Array.Clear(stereo, got * 2, (frames - got) * 2);
                if (running && MillisecondsSinceLastPacket < 100) Interlocked.Add(ref underrunFrames, frames - got);
            }
            return got;
        }

        // Drops the oldest buffered frames. Caller holds the lock.
        void Drop(int n)
        {
            n = Math.Min(n, ringCount);
            ringCount -= n;
            Interlocked.Add(ref trimmedFrames, n);
        }

        internal void Push(float[] stereo, int frames)
        {
            lock (gate)
            {
                int start = 0;
                if (frames > ringFrames) { start = frames - ringFrames; frames = ringFrames; }
                int first = Math.Min(frames, ringFrames - ringHead);
                Array.Copy(stereo, start * 2, ring, ringHead * 2, first * 2);
                if (frames > first) Array.Copy(stereo, (start + first) * 2, ring, 0, (frames - first) * 2);
                ringHead = (ringHead + frames) % ringFrames;
                ringCount = Math.Min(ringFrames, ringCount + frames);
            }
        }

        // ---------------------------------------------------------------- capture thread

        void ThreadMain()
        {
            int hrInit = Native.CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            bool uninit = hrInit >= 0;
            bool fineTimer = Native.timeBeginPeriod(1) == 0; // 1 ms sleeps, so packets are picked up promptly
            int failures = 0;
            try
            {
                while (!stopRequested)
                {
                    try { RunSession(); failures = 0; }
                    catch (UnauthorizedAccessException e) { lastError = e.Message; gaveUp = true; break; }
                    catch (Exception e) { lastError = e.Message; failures++; }
                    running = false;
                    // Back off so a failing device or a security prompt is not hammered.
                    if (failures >= 5) { gaveUp = true; break; }
                    int waitMs = failures == 0 ? 500 : 500 << failures;
                    stage = "Waiting to retry";
                    for (int i = 0; i < waitMs / 50 && !stopRequested; i++) Thread.Sleep(50);
                }
            }
            finally
            {
                running = false;
                stage = gaveUp ? "Gave up" : "Stopped";
                if (fineTimer) Native.timeEndPeriod(1);
                if (uninit) Native.CoUninitialize();
            }
        }

        void RunSession()
        {
            IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero, client = IntPtr.Zero, capture = IntPtr.Zero, mix = IntPtr.Zero;
            bool started = false;
            try
            {
                Guid clsid = ClsidDeviceEnumerator, iidEnum = IidDeviceEnumerator;
                stage = "Create device enumerator";
                Check(Native.CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iidEnum, out enumerator), stage);
                var getDefault = Fn<GetDefaultAudioEndpointFn>(enumerator, 4);
                stage = "Find default output device";
                Check(getDefault(enumerator, DataFlowRender, RoleMultimedia, out device), stage);
                stage = "Read device id";
                string deviceId = GetId(device);
                stage = "Read device name";
                deviceName = GetFriendlyName(device) ?? "Default output";

                Guid iidClient = IidAudioClient;
                stage = "Open audio client";
                Check(Fn<ActivateFn>(device, 3)(device, ref iidClient, ClsCtxAll, IntPtr.Zero, out client), stage);
                stage = "Read mix format";
                Check(Fn<GetMixFormatFn>(client, 8)(client, out mix), stage);
                SourceFormat f = ReadFormat(mix);
                stage = "Initialize loopback";
                Check(Fn<InitializeFn>(client, 3)(client, ShareModeShared, StreamFlagsLoopback, BufferDuration100ns, 0, mix, IntPtr.Zero), stage);
                Native.CoTaskMemFree(mix);
                mix = IntPtr.Zero;

                Guid iidCapture = IidCaptureClient;
                stage = "Open capture client";
                Check(Fn<GetServiceFn>(client, 14)(client, ref iidCapture, out capture), stage);
                var getBuffer = Fn<GetBufferFn>(capture, 3);
                var releaseBuffer = Fn<ReleaseBufferFn>(capture, 4);
                var getNext = Fn<GetNextPacketSizeFn>(capture, 5);

                resamplePos = 0; prevL = prevR = 0f;
                sourceRate = f.rate; sourceChannels = f.channels; format = f.text;
                stage = "Start capture";
                Check(Fn<StartStopFn>(client, 10)(client), stage);
                started = true;
                stage = "Capturing";
                running = true;
                lastError = null;

                int lastDeviceCheck = Environment.TickCount;
                while (!stopRequested)
                {
                    int packet;
                    Check(getNext(capture, out packet), "Read packet size");
                    while (packet > 0 && !stopRequested)
                    {
                        IntPtr data; int frames, flags;
                        int hr = getBuffer(capture, out data, out frames, out flags, IntPtr.Zero, IntPtr.Zero);
                        if (hr == BufferEmpty) break;
                        Check(hr, "Read packet");
                        try { Consume(data, frames, flags, f); }
                        finally { releaseBuffer(capture, frames); }
                        Check(getNext(capture, out packet), "Read packet size");
                    }
                    if (unchecked(Environment.TickCount - lastDeviceCheck) > 1000)
                    {
                        lastDeviceCheck = Environment.TickCount;
                        if (DefaultDeviceChanged(getDefault, enumerator, deviceId)) return;
                    }
                    Thread.Sleep(1);
                }
            }
            finally
            {
                running = false;
                stage = "Closing";
                if (started) Fn<StartStopFn>(client, 11)(client);
                if (mix != IntPtr.Zero) Native.CoTaskMemFree(mix);
                Release(ref capture);
                Release(ref client);
                Release(ref device);
                Release(ref enumerator);
            }
        }

        void Consume(IntPtr data, int frames, int flags, SourceFormat f)
        {
            if (frames <= 0) return;
            EnsureSize(ref inStereo, frames * 2);
            if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero) Array.Clear(inStereo, 0, frames * 2);
            else ConvertToStereo(data, frames, f);

            float blockPeak = 0f;
            for (int i = 0; i < frames * 2; i++)
            {
                float a = Math.Abs(inStereo[i]);
                if (a > blockPeak) blockPeak = a;
            }
            if (blockPeak > peakHold) peakHold = blockPeak;

            int outFrames = Resample(frames, f.rate);
            Push(outStereo, outFrames);
            Interlocked.Add(ref capturedFrames, frames);
            lastPacketTick = Environment.TickCount;
            everReceived = true;

            var tap = Tap;
            if (tap != null)
            {
                try { tap(outStereo, outFrames); }
                catch (Exception e) { lastError = "Tap: " + e.Message; }
            }
        }

        void ConvertToStereo(IntPtr data, int frames, SourceFormat f)
        {
            int ch = f.channels, n = frames * ch;
            EnsureSize(ref samples, n);
            switch (f.kind)
            {
                case SampleKind.Float32:
                    Marshal.Copy(data, samples, 0, n);
                    break;
                case SampleKind.Pcm16:
                    EnsureSize(ref s16, n);
                    Marshal.Copy(data, s16, 0, n);
                    for (int i = 0; i < n; i++) samples[i] = s16[i] * (1f / 32768f);
                    break;
                case SampleKind.Pcm24:
                    EnsureSize(ref b24, n * 3);
                    Marshal.Copy(data, b24, 0, n * 3);
                    for (int i = 0, j = 0; i < n; i++, j += 3)
                        samples[i] = ((b24[j] << 8) | (b24[j + 1] << 16) | (b24[j + 2] << 24)) * (1f / 2147483648f);
                    break;
                case SampleKind.Pcm32:
                    EnsureSize(ref s32, n);
                    Marshal.Copy(data, s32, 0, n);
                    for (int i = 0; i < n; i++) samples[i] = s32[i] * (1f / 2147483648f);
                    break;
            }
            for (int fr = 0; fr < frames; fr++)
            {
                float l = samples[fr * ch];
                float r = ch > 1 ? samples[fr * ch + 1] : l;
                inStereo[fr * 2] = l;
                inStereo[fr * 2 + 1] = r;
            }
        }

        // Linear resampler from the device rate to Unity's output rate.
        int Resample(int frames, int inRate)
        {
            if (inRate == outputRate)
            {
                EnsureSize(ref outStereo, frames * 2);
                Array.Copy(inStereo, outStereo, frames * 2);
                return frames;
            }
            double step = (double)inRate / outputRate;
            EnsureSize(ref outStereo, ((int)(frames / step) + 4) * 2);
            int n = 0, last = frames - 1;
            double t = resamplePos;
            while (t < last)
            {
                int i0 = (int)Math.Floor(t);
                float frac = (float)(t - i0);
                float aL, aR;
                if (i0 < 0) { aL = prevL; aR = prevR; }
                else { aL = inStereo[i0 * 2]; aR = inStereo[i0 * 2 + 1]; }
                float bL = inStereo[(i0 + 1) * 2], bR = inStereo[(i0 + 1) * 2 + 1];
                outStereo[n * 2] = aL + (bL - aL) * frac;
                outStereo[n * 2 + 1] = aR + (bR - aR) * frac;
                n++;
                t += step;
            }
            resamplePos = t - frames;
            prevL = inStereo[last * 2];
            prevR = inStereo[last * 2 + 1];
            return n;
        }

        static void EnsureSize<T>(ref T[] array, int n)
        {
            if (array == null || array.Length < n) array = new T[Math.Max(n, 1024)];
        }

        // ---------------------------------------------------------------- format

        enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

        struct SourceFormat
        {
            public SampleKind kind;
            public int channels, rate;
            public string text;
        }

        static SourceFormat ReadFormat(IntPtr wfx)
        {
            int tag = Marshal.ReadInt16(wfx, 0) & 0xFFFF;
            int channels = Marshal.ReadInt16(wfx, 2) & 0xFFFF;
            int rate = Marshal.ReadInt32(wfx, 4);
            int blockAlign = Marshal.ReadInt16(wfx, 12) & 0xFFFF;
            int bits = Marshal.ReadInt16(wfx, 14) & 0xFFFF;
            int cbSize = Marshal.ReadInt16(wfx, 16) & 0xFFFF;
            bool isFloat = tag == 3, isPcm = tag == 1;
            if (tag == 0xFFFE && cbSize >= 22)
            {
                var g = new byte[16];
                Marshal.Copy(IntPtr.Add(wfx, 24), g, 0, 16);
                var sub = new Guid(g);
                isFloat = sub == SubtypeFloat;
                isPcm = sub == SubtypePcm;
            }
            string text = rate + " Hz, " + channels + " ch, " + (isFloat ? "float " : isPcm ? "PCM " : "tag 0x" + tag.ToString("X") + " ") + bits + "-bit";
            if (channels < 1 || rate < 8000 || bits < 8 || blockAlign != channels * bits / 8)
                throw new NotSupportedException("Unsupported output format: " + text);
            SampleKind kind;
            if (isFloat && bits == 32) kind = SampleKind.Float32;
            else if (isPcm && bits == 16) kind = SampleKind.Pcm16;
            else if (isPcm && bits == 24) kind = SampleKind.Pcm24;
            else if (isPcm && bits == 32) kind = SampleKind.Pcm32;
            else throw new NotSupportedException("Unsupported output format: " + text);
            return new SourceFormat { kind = kind, channels = channels, rate = rate, text = text };
        }

        // ---------------------------------------------------------------- COM (raw vtable calls)

        static bool DefaultDeviceChanged(GetDefaultAudioEndpointFn getDefault, IntPtr enumerator, string deviceId)
        {
            IntPtr current;
            if (getDefault(enumerator, DataFlowRender, RoleMultimedia, out current) < 0) return true;
            try { return GetId(current) != deviceId; }
            finally { Release(ref current); }
        }

        static string GetId(IntPtr device)
        {
            IntPtr str;
            Check(Fn<GetIdFn>(device, 5)(device, out str), "Read device id");
            try { return Marshal.PtrToStringUni(str); }
            finally { Native.CoTaskMemFree(str); }
        }

        static string GetFriendlyName(IntPtr device)
        {
            IntPtr store = IntPtr.Zero, pv = IntPtr.Zero;
            try
            {
                if (Fn<OpenPropertyStoreFn>(device, 4)(device, StgmRead, out store) < 0) return null;
                pv = Marshal.AllocCoTaskMem(32);
                for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(pv, i, 0);
                var key = new PropertyKey { fmtid = PkeyDeviceFriendlyName, pid = 14 };
                if (Fn<PropStoreGetValueFn>(store, 5)(store, ref key, pv) < 0) return null;
                string name = Marshal.ReadInt16(pv, 0) == VtLpwstr ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8)) : null;
                Native.PropVariantClear(pv);
                return name;
            }
            catch { return null; }
            finally
            {
                if (pv != IntPtr.Zero) Marshal.FreeCoTaskMem(pv);
                Release(ref store);
            }
        }

        static T Fn<T>(IntPtr comObject, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(comObject);
            IntPtr fp = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fp, typeof(T));
        }

        static void Release(ref IntPtr comObject)
        {
            if (comObject == IntPtr.Zero) return;
            try { Fn<ReleaseFn>(comObject, 2)(comObject); } catch { }
            comObject = IntPtr.Zero;
        }

        static void Check(int hr, string what)
        {
            if (hr >= 0) return;
            if (hr == DeviceInvalidated) throw new InvalidOperationException("The output device changed or was removed.");
            if (hr == AccessDenied) throw new UnauthorizedAccessException(what + " was denied. Allow Unity to capture audio in Windows or your antivirus, then re-enable the component.");
            throw new InvalidOperationException(what + " failed (0x" + hr.ToString("X8") + ").");
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid fmtid; public int pid; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDefaultAudioEndpointFn(IntPtr self, int dataFlow, int role, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ActivateFn(IntPtr self, ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenPropertyStoreFn(IntPtr self, int access, out IntPtr store);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetIdFn(IntPtr self, out IntPtr id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PropStoreGetValueFn(IntPtr self, ref PropertyKey key, IntPtr propVariant);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int InitializeFn(IntPtr self, int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetMixFormatFn(IntPtr self, out IntPtr format);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetServiceFn(IntPtr self, ref Guid iid, out IntPtr service);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StartStopFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBufferFn(IntPtr self, out IntPtr data, out int frames, out int flags, IntPtr devicePosition, IntPtr qpcPosition);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseBufferFn(IntPtr self, int frames);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetNextPacketSizeFn(IntPtr self, out int frames);

        static class Native
        {
            [DllImport("ole32.dll")] public static extern int CoInitializeEx(IntPtr reserved, int coInit);
            [DllImport("ole32.dll")] public static extern void CoUninitialize();
            [DllImport("ole32.dll")] public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsCtx, ref Guid iid, out IntPtr ppv);
            [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);
            [DllImport("ole32.dll")] public static extern int PropVariantClear(IntPtr propVariant);
            [DllImport("winmm.dll")] public static extern int timeBeginPeriod(int milliseconds);
            [DllImport("winmm.dll")] public static extern int timeEndPeriod(int milliseconds);
        }

        static readonly Guid ClsidDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        static readonly Guid IidDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        static readonly Guid IidCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        static readonly Guid PkeyDeviceFriendlyName = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00AA00389B71");
        static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00AA00389B71");

        const int ClsCtxAll = 0x17, CoInitMultithreaded = 0, StgmRead = 0, VtLpwstr = 31;
        const int DataFlowRender = 0, RoleMultimedia = 1, ShareModeShared = 0;
        const int StreamFlagsLoopback = 0x00020000, BufferFlagsSilent = 0x2;
        const long BufferDuration100ns = 2000000; // 200 ms
        const int BufferEmpty = 0x08890001;
        const int DeviceInvalidated = unchecked((int)0x88890004);
        const int AccessDenied = unchecked((int)0x80070005);
    }

    /// <summary>
    /// Feeds an AudioSource from the capture, or a quiet test tone for the safety check.
    /// Fill runs on Unity's audio thread.
    /// </summary>
    public sealed class CarrierFeed
    {
        public const float ToneAmplitude = 0.0316f; // -30 dBFS
        public const float LeakThreshold = ToneAmplitude * 0.25f; // loop gain -12 dB decays instead of howling
        public const double ToneHz = 997.0;
        public enum Mode { Silence = 0, Tone = 1, Desktop = 2 }

        // Auto level: lifts quiet desktop audio so its loudest recent peak sits near full scale.
        // Instant attack, slow release, never below unity gain; silence holds the current level.
        public const float TargetPeak = 0.9f;
        public const float MaxBoost = 100f; // +40 dB
        const float SilenceFloor = 0.001f; // -60 dBFS
        const float ReleaseSeconds = 10f;

        readonly double phaseStep;
        readonly int sampleRate;
        volatile int mode;
        double phase;
        float[] scratch = new float[8192];
        float envelope;
        volatile float gain = 1f;

        public DesktopAudioCapture Capture;
        /// <summary>Set from the main thread; read by whichever thread fills the carrier.</summary>
        public volatile bool AutoLevel;
        /// <summary>Highest gain auto level may use. Set from the safety check's feedback margin; 1 until then.</summary>
        public volatile float MaxGain = 1f;
        /// <summary>Gain applied to the last desktop block.</summary>
        public float Gain => gain;

        public CarrierFeed(int sampleRate)
        {
            this.sampleRate = sampleRate;
            phaseStep = 2.0 * Math.PI * ToneHz / sampleRate;
        }

        public Mode Current { get { return (Mode)mode; } set { mode = (int)value; } }

        public void Fill(float[] data, int channels)
        {
            if (channels < 1) return;
            int frames = data.Length / channels;
            int m = mode;
            if (m == (int)Mode.Tone)
            {
                for (int f = 0; f < frames; f++)
                {
                    float s = (float)(ToneAmplitude * Math.Sin(phase));
                    phase += phaseStep;
                    if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                    WriteFrame(data, f, channels, s, s);
                }
                return;
            }
            var capture = Capture;
            if (m != (int)Mode.Desktop || capture == null)
            {
                Array.Clear(data, 0, data.Length);
                return;
            }
            if (scratch.Length < frames * 2) scratch = new float[frames * 2];
            capture.Read(scratch, frames);
            float g = AutoLevel ? NextGain(frames) : ResetGain();
            for (int f = 0; f < frames; f++) WriteFrame(data, f, channels, scratch[f * 2] * g, scratch[f * 2 + 1] * g);
        }

        // The envelope never drops below the current block's peak, so gain * peak stays at or under TargetPeak.
        float NextGain(int frames)
        {
            float peak = 0f;
            for (int i = 0; i < frames * 2; i++) { float a = Math.Abs(scratch[i]); if (a > peak) peak = a; }
            if (peak > envelope) envelope = peak;
            else if (peak > SilenceFloor)
                envelope = Math.Max(peak, envelope * (float)Math.Exp(-frames / (ReleaseSeconds * sampleRate)));
            float g = envelope > SilenceFloor ? TargetPeak / envelope : 1f;
            g = Math.Max(1f, Math.Min(g, MaxGain));
            gain = g;
            return g;
        }

        float ResetGain()
        {
            envelope = 0f;
            gain = 1f;
            return 1f;
        }

        static void WriteFrame(float[] data, int frame, int channels, float l, float r)
        {
            int o = frame * channels;
            if (channels == 1) { data[o] = 0.5f * (l + r); return; }
            data[o] = l;
            data[o + 1] = r;
            for (int c = 2; c < channels; c++) data[o + c] = 0f;
        }
    }

    /// <summary>
    /// Records a mono window, then measures one frequency with a Hann-windowed Goertzel filter,
    /// so strong music a few hertz away does not read as the test tone.
    /// Add runs on the capture thread; Amplitude on the main thread.
    /// </summary>
    public sealed class ToneDetector
    {
        readonly object gate = new object();
        readonly int sampleRate;
        readonly float[] window;
        int count, skip;
        volatile bool armed;

        public ToneDetector(int sampleRate, int maxFrames)
        {
            this.sampleRate = sampleRate;
            window = new float[Math.Max(64, maxFrames)];
        }

        public void Arm(int skipFrames)
        {
            lock (gate) { count = 0; skip = Math.Max(0, skipFrames); armed = true; }
        }

        public void Disarm() { armed = false; }

        public int Frames { get { lock (gate) return count; } }

        public void Add(float[] stereo, int frames)
        {
            if (!armed) return;
            lock (gate)
            {
                for (int i = 0; i < frames && count < window.Length; i++)
                {
                    if (skip > 0) { skip--; continue; }
                    window[count++] = 0.5f * (stereo[i * 2] + stereo[i * 2 + 1]);
                }
            }
        }

        /// <summary>Estimated sine amplitude at <paramref name="hz"/> over the recorded window.</summary>
        public double Amplitude(double hz)
        {
            lock (gate)
            {
                int n = count;
                if (n < 64) return 0;
                double c = 2.0 * Math.Cos(2.0 * Math.PI * hz / sampleRate);
                double s1 = 0, s2 = 0, wsum = 0;
                for (int i = 0; i < n; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
                    wsum += w;
                    double s0 = window[i] * w + c * s1 - s2;
                    s2 = s1;
                    s1 = s0;
                }
                double power = s1 * s1 + s2 * s2 - c * s1 * s2;
                return 2.0 * Math.Sqrt(Math.Max(power, 0)) / wsum;
            }
        }
    }
}
