using System.Runtime.InteropServices;

namespace MyRemote.Desktop.Services;

/// <summary>
/// Lightweight Win32 winmm.dll 16kHz 16-bit mono PCM voice capture and playback service.
/// </summary>
public sealed class VoiceAudioService : IDisposable
{
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int BufferBytes = 640; // 20ms at 16kHz 16-bit mono
    private const int BufferCount = 4;
    private const int WimData = 0x3C0;
    private const int WomDone = 0x3BD;
    private const int CallbackFunction = 0x00030000;

    private IntPtr _hWaveIn;
    private IntPtr _hWaveOut;
    private WaveDelegate? _inCallback;
    private WaveDelegate? _outCallback;
    private readonly List<GCHandle> _pinnedHandles = [];
    private volatile bool _running;
    private volatile bool _isMuted = true;

    public event Action<byte[]>? AudioCaptured;

    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    public void StartPlayback()
    {
        if (_hWaveOut != IntPtr.Zero) return;
        var fmt = CreateWaveFormat();
        _outCallback = WaveOutProc;
        if (waveOutOpen(out _hWaveOut, -1, ref fmt, _outCallback, IntPtr.Zero, CallbackFunction) != 0)
        {
            _hWaveOut = IntPtr.Zero;
        }
    }

    public void StartCapture()
    {
        if (_running) return;
        var fmt = CreateWaveFormat();
        _inCallback = WaveInProc;
        if (waveInOpen(out _hWaveIn, -1, ref fmt, _inCallback, IntPtr.Zero, CallbackFunction) != 0)
        {
            _hWaveIn = IntPtr.Zero;
            return;
        }

        _running = true;
        for (int i = 0; i < BufferCount; i++)
        {
            var buf = new byte[BufferBytes];
            var bufHandle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            var hdr = new WaveHdr
            {
                lpData = bufHandle.AddrOfPinnedObject(),
                dwBufferLength = BufferBytes
            };
            var hdrHandle = GCHandle.Alloc(hdr, GCHandleType.Pinned);
            _pinnedHandles.Add(bufHandle);
            _pinnedHandles.Add(hdrHandle);

            var hdrPtr = hdrHandle.AddrOfPinnedObject();
            waveInPrepareHeader(_hWaveIn, hdrPtr, Marshal.SizeOf<WaveHdr>());
            waveInAddBuffer(_hWaveIn, hdrPtr, Marshal.SizeOf<WaveHdr>());
        }

        waveInStart(_hWaveIn);
    }

    public void PlayAudio(byte[] pcmBytes)
    {
        if (_hWaveOut == IntPtr.Zero || pcmBytes.Length == 0) return;

        var bufHandle = GCHandle.Alloc(pcmBytes, GCHandleType.Pinned);
        var hdr = new WaveHdr
        {
            lpData = bufHandle.AddrOfPinnedObject(),
            dwBufferLength = pcmBytes.Length,
            dwUser = GCHandle.ToIntPtr(bufHandle)
        };
        var hdrHandle = GCHandle.Alloc(hdr, GCHandleType.Pinned);
        var hdrPtr = hdrHandle.AddrOfPinnedObject();

        // Store hdrHandle in dwFlags? Better: track both via GCHandle in a tuple
        var pairHandle = GCHandle.Alloc((bufHandle, hdrHandle));
        Marshal.WriteIntPtr(hdrPtr, Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwUser)).ToInt32(), GCHandle.ToIntPtr(pairHandle));

        waveOutPrepareHeader(_hWaveOut, hdrPtr, Marshal.SizeOf<WaveHdr>());
        waveOutWrite(_hWaveOut, hdrPtr, Marshal.SizeOf<WaveHdr>());
    }

    private void WaveInProc(IntPtr hwi, int uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        if (uMsg != WimData || !_running || dwParam1 == IntPtr.Zero) return;

        var hdr = Marshal.PtrToStructure<WaveHdr>(dwParam1);
        if (!_isMuted && hdr.dwBytesRecorded > 0)
        {
            var data = new byte[hdr.dwBytesRecorded];
            Marshal.Copy(hdr.lpData, data, 0, data.Length);
            AudioCaptured?.Invoke(data);
        }

        if (_running && _hWaveIn != IntPtr.Zero)
        {
            waveInAddBuffer(_hWaveIn, dwParam1, Marshal.SizeOf<WaveHdr>());
        }
    }

    private void WaveOutProc(IntPtr hwo, int uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        if (uMsg != WomDone || dwParam1 == IntPtr.Zero) return;
        try
        {
            var hdr = Marshal.PtrToStructure<WaveHdr>(dwParam1);
            if (_hWaveOut != IntPtr.Zero)
            {
                waveOutUnprepareHeader(_hWaveOut, dwParam1, Marshal.SizeOf<WaveHdr>());
            }
            if (hdr.dwUser != IntPtr.Zero)
            {
                var pairHandle = GCHandle.FromIntPtr(hdr.dwUser);
                if (pairHandle.Target is ValueTuple<GCHandle, GCHandle> pair)
                {
                    if (pair.Item1.IsAllocated) pair.Item1.Free();
                    if (pair.Item2.IsAllocated) pair.Item2.Free();
                }
                pairHandle.Free();
            }
        }
        catch { /* ignore teardown race */ }
    }

    public void Stop()
    {
        _running = false;
        if (_hWaveIn != IntPtr.Zero)
        {
            try { waveInStop(_hWaveIn); waveInReset(_hWaveIn); waveInClose(_hWaveIn); } catch { /* ignore */ }
            _hWaveIn = IntPtr.Zero;
        }
        if (_hWaveOut != IntPtr.Zero)
        {
            try { waveOutReset(_hWaveOut); waveOutClose(_hWaveOut); } catch { /* ignore */ }
            _hWaveOut = IntPtr.Zero;
        }
        foreach (var h in _pinnedHandles)
        {
            if (h.IsAllocated) h.Free();
        }
        _pinnedHandles.Clear();
    }

    public void Dispose() => Stop();

    private static WaveFormatEx CreateWaveFormat() => new()
    {
        wFormatTag = 1, // PCM
        nChannels = Channels,
        nSamplesPerSec = SampleRate,
        nAvgBytesPerSec = SampleRate * Channels * (BitsPerSample / 8),
        nBlockAlign = (short)(Channels * (BitsPerSample / 8)),
        wBitsPerSample = BitsPerSample,
        cbSize = 0
    };

    private delegate void WaveDelegate(IntPtr hdrvr, int uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WaveFormatEx pwfx, WaveDelegate dwCallback, IntPtr dwInstance, int fdwOpen);
    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr hwi);
    [DllImport("winmm.dll")]
    private static extern int waveInStop(IntPtr hwi);
    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr hwi);
    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr phwo, int uDeviceID, ref WaveFormatEx pwfx, WaveDelegate dwCallback, IntPtr dwInstance, int fdwOpen);
    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public int dwBufferLength;
        public int dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags;
        public int dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }
}
