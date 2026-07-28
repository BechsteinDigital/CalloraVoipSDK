using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.Codecs;
using PortAudioSharp;
using CalloraVoipSdk.Audio.Abstractions.Domain.Devices;
using CalloraVoipSdk.Audio.Abstractions.Processing;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Application.Ports.Audio;

namespace CalloraVoipSdk.Audio.Linux;

/// <summary>
/// Linux audio device using PortAudio (ALSA / PulseAudio).
/// Supports G.711 (PCMU/PCMA), G.722 (wideband, 16 kHz) and Opus (RFC 7587, 48 kHz).
/// Provides runtime controls for hot-switch, mute, volume, and format updates.
/// </summary>
public sealed class LinuxAudioDevice : IAudioDeviceProvider, IAudioDeviceRuntimeControl, IDisposable
{
    private static readonly IReadOnlyDictionary<int, string> EmptyPayloadTypeCodecMap =
        new ReadOnlyDictionary<int, string>(new Dictionary<int, string>());

    // Bounds the decoded-PCM playback buffer at 1 second (50 × 20 ms frames). The RX path
    // (OnFrameReceived, network-paced) can burst ahead of the PortAudio playback callback
    // (hardware-paced, one frame per invocation); an unbounded queue would grow without limit
    // under jitter or a stalled output stream, inflating both memory and mouth-to-ear latency
    // (HARD-F4). DropOldest is the jitter-buffer-correct policy: on overflow the stalest frames
    // are discarded so playback stays fresh and latency stays bounded.
    private const int PlaybackQueueCapacity = 50;

    private readonly object _sync = new();
    private readonly BoundedPlaybackBuffer _playbackQueue = new(PlaybackQueueCapacity);

    // Holds one PortAudio acquisition for the whole device lifetime so Initialize/Terminate stay
    // balanced with the process-wide refcount (issue #18, A7); released once in Dispose.
    private readonly PortAudioLease _portAudioLease = PortAudioLifetime.Acquire();

    // Reused silence buffer for the playback callback so a starved output stream does not allocate a
    // fresh zero-filled array on every hardware tick on the audio hotpath (issue #18, A6 / HARD-F1).
    // Sized lazily to the largest observed callback and only ever read from (never written after
    // creation), so a single instance is safe to hand to the single-reader playback callback.
    private byte[] _silenceBuffer = Array.Empty<byte>();

    // Explicit optional override for the PortAudio callback buffer size (issue #18, A1). Zero means
    // "derive from the sample rate" via ComputeFramesPerBuffer; a positive value is used verbatim.
    private readonly uint _framesPerBufferOverride;

    private PortAudioSharp.Stream? _inputStream;
    private PortAudioSharp.Stream? _outputStream;
    private IMediaReceiver? _receiver;
    private IMediaSender? _sender;

    private bool _disposed;
    private bool _connected;

    private string _name;
    private int _inputDeviceIndex;
    private int _outputDeviceIndex;

    private int _outboundPayloadType;
    private int _negotiatedPayloadType;
    private IReadOnlyDictionary<int, string> _payloadTypeCodecMap = EmptyPayloadTypeCodecMap;
    private ActiveCodec _activeCodec = ActiveCodec.Pcmu;
    private ActiveCodec _outboundCodec = ActiveCodec.Pcmu;
    private int _activeSampleRate;
    private int _bitsPerSample = 16;
    private int _channels = 1;

    private float _inputVolume = 1f;
    private float _outputVolume = 1f;
    private bool _inputMuted;
    private bool _outputMuted;

    private G722CodecState? _g722DecodeState;
    private G722CodecState? _g722EncodeState;

    // Cached stateless G722 codec instances (NAudio's G722Codec keeps no per-instance state — the
    // codec state lives in G722CodecState), reused per frame instead of allocating one per
    // encode/decode call on the audio hotpath (HARD-F1). Separate encode/decode instances keep the
    // capture thread and the receive thread off a shared instance.
    private readonly G722Codec _g722EncodeCodec = new();
    private readonly G722Codec _g722DecodeCodec = new();
    private OpusDeviceCodec? _opusCodec;

    /// <summary>
    /// Creates a Linux audio device with optional startup options.
    /// </summary>
    public LinuxAudioDevice(AudioDeviceOptions? options = null)
    {
        options ??= new AudioDeviceOptions();

        // PortAudio is now initialized via the process-wide refcount lease acquired in the field
        // initializer above (issue #18, A7) — no bare Initialize() here.
        _framesPerBufferOverride = options.FramesPerBuffer;
        _inputDeviceIndex = options.InputDeviceIndex;
        _outputDeviceIndex = options.OutputDeviceIndex;
        _activeSampleRate = options.SampleRate > 0 ? options.SampleRate : 8000;
        _name = GetDeviceName(_inputDeviceIndex);
    }

    /// <inheritdoc />
    public string Name
    {
        get
        {
            lock (_sync)
            {
                return _name;
            }
        }
    }

    /// <inheritdoc />
    public void Connect(IMediaReceiver receiver, IMediaSender sender, AudioConnectionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(parameters);

        ThrowIfDisposed();

        lock (_sync)
        {
            DisconnectInternalLocked();

            _receiver = receiver;
            _sender = sender;
            _outboundPayloadType = parameters.PayloadType;
            _negotiatedPayloadType = parameters.PayloadType;
            _payloadTypeCodecMap = parameters.PayloadTypeCodecMap ?? EmptyPayloadTypeCodecMap;
            _activeCodec = AudioCodecResolver.ResolveActiveCodec(
                parameters.PayloadType,
                parameters.SampleRate,
                parameters.CodecName,
                _payloadTypeCodecMap);
            _outboundCodec = _activeCodec;

            if (parameters.SampleRate > 0)
                _activeSampleRate = parameters.SampleRate;

            _g722DecodeState = new G722CodecState(64000, G722Flags.None);
            _g722EncodeState = new G722CodecState(64000, G722Flags.None);
            _opusCodec = _activeCodec == ActiveCodec.Opus ? new OpusDeviceCodec() : null;

            _receiver.FrameReceived += OnFrameReceived;

            StartOutputStreamLocked();
            var inputStarted = false;
            try
            {
                StartInputStreamLocked();
                inputStarted = true;
            }
            finally
            {
                if (!inputStarted)
                    StopOutputStreamLocked();
            }

            _connected = true;
        }
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        lock (_sync)
        {
            DisconnectInternalLocked();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioDeviceDescriptor> GetAvailableInputDevices()
    {
        // Acquire/release around the enumeration so this Initialize is paired with a Terminate
        // (issue #18, A7). The lease is a no-op init when a device already holds one.
        using var lease = PortAudioLifetime.Acquire();

        var result = new List<AudioDeviceDescriptor>
        {
            new("-1", "Default Input", isDefault: true)
        };

        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels <= 0)
                continue;

            result.Add(new AudioDeviceDescriptor(
                i.ToString(CultureInfo.InvariantCulture),
                info.name,
                isDefault: false));
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioDeviceDescriptor> GetAvailableOutputDevices()
    {
        // Paired acquire/release around the enumeration (issue #18, A7).
        using var lease = PortAudioLifetime.Acquire();

        var result = new List<AudioDeviceDescriptor>
        {
            new("-1", "Default Output", isDefault: true)
        };

        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxOutputChannels <= 0)
                continue;

            result.Add(new AudioDeviceDescriptor(
                i.ToString(CultureInfo.InvariantCulture),
                info.name,
                isDefault: false));
        }

        return result;
    }

    /// <inheritdoc />
    public AudioDeviceRuntimeSnapshot GetRuntimeSnapshot()
    {
        lock (_sync)
        {
            return new AudioDeviceRuntimeSnapshot(
                isConnected: _connected,
                inputDeviceId: _inputDeviceIndex.ToString(CultureInfo.InvariantCulture),
                outputDeviceId: _outputDeviceIndex.ToString(CultureInfo.InvariantCulture),
                inputMuted: _inputMuted,
                outputMuted: _outputMuted,
                inputVolume: _inputVolume,
                outputVolume: _outputVolume,
                format: new AudioDeviceFormat
                {
                    SampleRate = _activeSampleRate,
                    BitsPerSample = _bitsPerSample,
                    Channels = _channels
                },
                playbackQueueDepth: _playbackQueue.Depth,
                droppedPlaybackFrames: _playbackQueue.DroppedFrames);
        }
    }

    /// <inheritdoc />
    public void SwitchInputDevice(string? deviceId)
    {
        ThrowIfDisposed();

        var parsedDevice = ParseDeviceIndex(deviceId);
        ValidateInputDeviceIndex(parsedDevice);

        lock (_sync)
        {
            if (_inputDeviceIndex == parsedDevice)
                return;

            _inputDeviceIndex = parsedDevice;
            _name = GetDeviceName(_inputDeviceIndex);

            if (_connected)
                RebuildInputStreamLocked();
        }
    }

    /// <inheritdoc />
    public void SwitchOutputDevice(string? deviceId)
    {
        ThrowIfDisposed();

        var parsedDevice = ParseDeviceIndex(deviceId);
        ValidateOutputDeviceIndex(parsedDevice);

        lock (_sync)
        {
            if (_outputDeviceIndex == parsedDevice)
                return;

            _outputDeviceIndex = parsedDevice;
            if (_connected)
                RebuildOutputStreamLocked();
        }
    }

    /// <inheritdoc />
    public void SetInputVolume(float volume)
    {
        ThrowIfDisposed();
        ValidateVolume(volume);

        lock (_sync)
        {
            _inputVolume = volume;
        }
    }

    /// <inheritdoc />
    public void SetOutputVolume(float volume)
    {
        ThrowIfDisposed();
        ValidateVolume(volume);

        lock (_sync)
        {
            _outputVolume = volume;
        }
    }

    /// <inheritdoc />
    public void SetInputMuted(bool isMuted)
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            _inputMuted = isMuted;
        }
    }

    /// <inheritdoc />
    public void SetOutputMuted(bool isMuted)
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            _outputMuted = isMuted;
        }
    }

    /// <inheritdoc />
    public void UpdateFormat(AudioDeviceFormat format)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(format);

        if (format.SampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(format), "SampleRate must be > 0.");
        if (format.BitsPerSample != 16)
            throw new NotSupportedException("Only 16-bit PCM is supported.");
        if (format.Channels != 1)
            throw new NotSupportedException("Only mono audio (Channels=1) is supported.");

        lock (_sync)
        {
            var changed = _activeSampleRate != format.SampleRate
                || _bitsPerSample != format.BitsPerSample
                || _channels != format.Channels;

            if (!changed)
                return;

            _activeSampleRate = format.SampleRate;
            _bitsPerSample = format.BitsPerSample;
            _channels = format.Channels;

            if (_connected)
            {
                RebuildOutputStreamLocked();
                RebuildInputStreamLocked();
            }
        }
    }

    /// <summary>
    /// Returns available input device names for compatibility with existing samples.
    /// </summary>
    public static IReadOnlyList<string> GetInputDevices()
    {
        // Paired acquire/release around the enumeration (issue #18, A7).
        using var lease = PortAudioLifetime.Acquire();

        var result = new List<string>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels > 0)
                result.Add($"[{i}] {info.name}");
        }

        return result;
    }

    /// <summary>
    /// Returns available output device names for compatibility with existing samples.
    /// </summary>
    public static IReadOnlyList<string> GetOutputDevices()
    {
        // Paired acquire/release around the enumeration (issue #18, A7).
        using var lease = PortAudioLifetime.Acquire();

        var result = new List<string>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxOutputChannels > 0)
                result.Add($"[{i}] {info.name}");
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            DisconnectInternalLocked();

            // Release this device's lifetime acquisition; PortAudio terminates once the last
            // outstanding acquisition across the process is released (issue #18, A7).
            _portAudioLease.Dispose();
        }
    }

    private void OnFrameReceived(object? sender, MediaFrameReceivedEventArgs e)
    {
        var payload = e.Frame.Payload.ToArray();
        var inboundCodec = ResolveInboundCodec(e.Frame.PayloadType);

        if (TryResolveInboundCodec(e.Frame.PayloadType, out var knownInboundCodec))
            TryAdaptOutboundCodec(knownInboundCodec, e.Frame.PayloadType);

        var decodedPcm = Decode(payload, inboundCodec);

        int playbackSampleRate;
        bool outputMuted;
        float outputVolume;
        lock (_sync)
        {
            playbackSampleRate = _activeSampleRate;
            outputMuted = _outputMuted;
            outputVolume = _outputVolume;
        }

        var playbackPcm = PcmSampleRateConverter.ConvertPcmSampleRate(
            decodedPcm,
            AudioCodecResolver.GetCodecSampleRate(inboundCodec),
            playbackSampleRate);
        var adjustedPlayback = PcmGain.ApplyInPlace(playbackPcm, outputMuted, outputVolume);
        _playbackQueue.Enqueue(adjustedPlayback);
    }

    private StreamCallbackResult PlaybackCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags flags,
        IntPtr userData)
    {
        _playbackQueue.TryDequeue(out var pcm);

        var bytes = (int)(frameCount * 2);
        var silence = EnsureSilenceBuffer(bytes);

        if (pcm is null || pcm.Length == 0)
        {
            // Underrun: write pre-allocated silence instead of allocating a fresh array per tick
            // on the audio hotpath (issue #18, A6 / HARD-F1).
            Marshal.Copy(silence, 0, output, bytes);
        }
        else if (pcm.Length >= bytes)
        {
            Marshal.Copy(pcm, 0, output, bytes);
        }
        else
        {
            // Short frame: copy what we have, then pad the tail with silence rather than discarding
            // the frame wholesale (issue #18, A6). The pointer arithmetic keeps a single P/Invoke
            // copy per region without a temporary managed buffer.
            Marshal.Copy(pcm, 0, output, pcm.Length);
            Marshal.Copy(silence, 0, output + pcm.Length, bytes - pcm.Length);
        }

        return StreamCallbackResult.Continue;
    }

    private byte[] EnsureSilenceBuffer(int bytes)
    {
        // The playback callback is the single reader/writer of this field; grow-only, so a larger
        // request replaces the array and never shrinks a buffer another read might still touch.
        var buffer = _silenceBuffer;
        if (buffer.Length >= bytes)
            return buffer;

        buffer = new byte[bytes];
        _silenceBuffer = buffer;
        return buffer;
    }

    private StreamCallbackResult CaptureCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags flags,
        IntPtr userData)
    {
        IMediaSender? localSender;
        ActiveCodec outboundCodec;
        int outboundPayloadType;
        int captureSampleRate;
        bool inputMuted;
        float inputVolume;

        lock (_sync)
        {
            localSender = _sender;
            outboundCodec = _outboundCodec;
            outboundPayloadType = _outboundPayloadType;
            captureSampleRate = _activeSampleRate;
            inputMuted = _inputMuted;
            inputVolume = _inputVolume;
        }

        if (input == IntPtr.Zero || localSender is null)
            return StreamCallbackResult.Continue;

        var pcmBytes = checked((int)frameCount * 2);
        var pcm = new byte[pcmBytes];
        Marshal.Copy(input, pcm, 0, pcmBytes);

        var adjustedCapture = PcmGain.ApplyInPlace(pcm, inputMuted, inputVolume);

        var outboundSampleRate = AudioCodecResolver.GetCodecSampleRate(outboundCodec);
        var outboundPcm = PcmSampleRateConverter.ConvertPcmSampleRate(
            adjustedCapture,
            captureSampleRate,
            outboundSampleRate);

        if (outboundCodec == ActiveCodec.Opus)
        {
            // Opus needs whole 20 ms frames; the codec buffers partial captures and emits 0..n packets.
            foreach (var opusPayload in _opusCodec?.Encode(outboundPcm) ?? [])
            {
                var opusFrame = new MediaFrame(
                    opusPayload,
                    PayloadType: outboundPayloadType,
                    DurationRtpUnits: (uint)OpusDeviceCodec.FrameSamples);
                // Fire-and-forget, but observe the fault so send failures are not silently lost
                // to the finalizer (issue #18, A5 / K3).
                AudioTaskFaultObserver.Observe(
                    localSender.SendAsync(opusFrame, CancellationToken.None),
                    "linux-capture-opus");
            }

            return StreamCallbackResult.Continue;
        }

        var encoded = Encode(outboundPcm, outboundCodec);

        var rtpClockRate = outboundCodec == ActiveCodec.G722 ? 8000d : outboundSampleRate;
        var outboundSamples = Math.Max(1, outboundPcm.Length / 2);
        var durationRtpUnits = (uint)Math.Max(
            1,
            (int)Math.Round(outboundSamples * rtpClockRate / outboundSampleRate));

        var frame = new MediaFrame(encoded, PayloadType: outboundPayloadType, durationRtpUnits);
        // Fire-and-forget with fault observation (issue #18, A5 / K3).
        AudioTaskFaultObserver.Observe(
            localSender.SendAsync(frame, CancellationToken.None),
            "linux-capture");

        return StreamCallbackResult.Continue;
    }

    private void StartInputStreamLocked()
    {
        var inputDevice = ResolveInputDeviceIndex(_inputDeviceIndex);
        var inputInfo = PortAudio.GetDeviceInfo(inputDevice);

        var inParams = new StreamParameters
        {
            device = inputDevice,
            channelCount = _channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = inputInfo.defaultLowInputLatency
        };

        _inputStream = new PortAudioSharp.Stream(
            inParams: inParams,
            outParams: null,
            sampleRate: _activeSampleRate,
            framesPerBuffer: ResolveFramesPerBuffer(),
            streamFlags: StreamFlags.ClipOff,
            callback: CaptureCallback,
            userData: IntPtr.Zero);

        _inputStream.Start();
    }

    private void StartOutputStreamLocked()
    {
        var outputDevice = ResolveOutputDeviceIndex(_outputDeviceIndex);
        var outputInfo = PortAudio.GetDeviceInfo(outputDevice);

        var outParams = new StreamParameters
        {
            device = outputDevice,
            channelCount = _channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = outputInfo.defaultLowOutputLatency
        };

        _outputStream = new PortAudioSharp.Stream(
            inParams: null,
            outParams: outParams,
            sampleRate: _activeSampleRate,
            framesPerBuffer: ResolveFramesPerBuffer(),
            streamFlags: StreamFlags.ClipOff,
            callback: PlaybackCallback,
            userData: IntPtr.Zero);

        _outputStream.Start();
    }

    private void RebuildInputStreamLocked()
    {
        StopInputStreamLocked();
        StartInputStreamLocked();
    }

    private void RebuildOutputStreamLocked()
    {
        StopOutputStreamLocked();
        StartOutputStreamLocked();
    }

    private void StopInputStreamLocked()
    {
        if (_inputStream is null)
            return;

        _inputStream.Stop();
        _inputStream.Dispose();
        _inputStream = null;
    }

    private void StopOutputStreamLocked()
    {
        if (_outputStream is not null)
        {
            _outputStream.Stop();
            _outputStream.Dispose();
            _outputStream = null;
        }

        // Drain the buffer and reset its drop metric so a reconnect starts with an empty queue.
        _playbackQueue.Clear();
    }

    private void DisconnectInternalLocked()
    {
        if (_receiver is not null)
        {
            _receiver.FrameReceived -= OnFrameReceived;
            _receiver = null;
        }

        _sender = null;

        StopInputStreamLocked();
        StopOutputStreamLocked();

        _g722DecodeState = null;
        _g722EncodeState = null;
        _opusCodec = null;
        _outboundPayloadType = 0;
        _negotiatedPayloadType = 0;
        _payloadTypeCodecMap = EmptyPayloadTypeCodecMap;
        _activeCodec = ActiveCodec.Pcmu;
        _outboundCodec = ActiveCodec.Pcmu;
        _connected = false;
    }

    private uint ResolveFramesPerBuffer()
    {
        // An explicit FramesPerBuffer option overrides the sample-rate-derived default (issue #18,
        // A1); zero (the default) means "derive a 20 ms buffer from the active sample rate".
        return _framesPerBufferOverride > 0
            ? _framesPerBufferOverride
            : ComputeFramesPerBuffer(_activeSampleRate);
    }

    private static uint ComputeFramesPerBuffer(int sampleRate = 8000)
    {
        var safeSampleRate = sampleRate > 0 ? sampleRate : 8000;
        var frames = safeSampleRate * 20 / 1000;
        return (uint)Math.Max(1, frames);
    }

    private static int ResolveInputDeviceIndex(int requestedIndex)
    {
        return requestedIndex < 0 ? PortAudio.DefaultInputDevice : requestedIndex;
    }

    private static int ResolveOutputDeviceIndex(int requestedIndex)
    {
        return requestedIndex < 0 ? PortAudio.DefaultOutputDevice : requestedIndex;
    }

    private static int ParseDeviceIndex(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return -1;

        if (!int.TryParse(deviceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException("Device id must be a numeric string.", nameof(deviceId));

        return parsed;
    }

    private static void ValidateVolume(float volume)
    {
        if (float.IsNaN(volume) || float.IsInfinity(volume) || volume < 0f || volume > 2f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(volume),
                "Volume must be finite and in range 0..2.");
        }
    }

    private static void ValidateInputDeviceIndex(int deviceIndex)
    {
        if (deviceIndex == -1)
            return;

        if (deviceIndex < 0 || deviceIndex >= PortAudio.DeviceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceIndex),
                $"Input device index must be -1 or in range [0..{PortAudio.DeviceCount - 1}].");
        }

        var info = PortAudio.GetDeviceInfo(deviceIndex);
        if (info.maxInputChannels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceIndex),
                "The selected device does not provide input channels.");
        }
    }

    private static void ValidateOutputDeviceIndex(int deviceIndex)
    {
        if (deviceIndex == -1)
            return;

        if (deviceIndex < 0 || deviceIndex >= PortAudio.DeviceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceIndex),
                $"Output device index must be -1 or in range [0..{PortAudio.DeviceCount - 1}].");
        }

        var info = PortAudio.GetDeviceInfo(deviceIndex);
        if (info.maxOutputChannels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceIndex),
                "The selected device does not provide output channels.");
        }
    }

    private static string GetDeviceName(int index)
    {
        if (index < 0)
            return "Default Input";

        if (index >= PortAudio.DeviceCount)
            return "Unknown";

        return PortAudio.GetDeviceInfo(index).name;
    }

    private ActiveCodec ResolveInboundCodec(int payloadType)
    {
        if (TryResolveInboundCodec(payloadType, out var resolved))
            return resolved;

        return _activeCodec;
    }

    private bool TryResolveInboundCodec(int payloadType, out ActiveCodec codec)
    {
        if (payloadType == 0)
        {
            codec = ActiveCodec.Pcmu;
            return true;
        }

        if (payloadType == 8)
        {
            codec = ActiveCodec.Pcma;
            return true;
        }

        if (payloadType == 9)
        {
            codec = ActiveCodec.G722;
            return true;
        }

        return TryResolveCodecFromMap(payloadType, out codec);
    }

    private void TryAdaptOutboundCodec(ActiveCodec inboundCodec, int inboundPayloadType)
    {
        lock (_sync)
        {
            // Only adapt to a codec the far end negotiated for this leg — never echo an
            // unnegotiated inbound payload type back at it (issue #18, A2; RFC 3264 §5.1). The
            // negotiated set is the SDP payload-type→codec map keys plus the primary negotiated PT.
            var decision = OutboundCodecAdaptationPolicy.Evaluate(
                inboundPayloadType,
                _outboundPayloadType,
                _negotiatedPayloadType,
                _payloadTypeCodecMap.Keys);

            if (!decision.ShouldAdapt)
                return;

            _outboundCodec = inboundCodec;
            _outboundPayloadType = decision.TargetPayloadType;
        }
    }

    private bool TryResolveCodecFromMap(int payloadType, out ActiveCodec codec)
    {
        if (_payloadTypeCodecMap.TryGetValue(payloadType, out var codecName)
            && AudioCodecResolver.MapCodecNameToActiveCodec(codecName) is { } mappedCodec)
        {
            codec = mappedCodec;
            return true;
        }

        codec = default;
        return false;
    }

    private byte[] Decode(byte[] payload, ActiveCodec codec)
    {
        return codec switch
        {
            ActiveCodec.G722 => G722Frame.Decode(_g722DecodeCodec, _g722DecodeState, payload),
            ActiveCodec.Opus => _opusCodec?.Decode(payload) ?? Array.Empty<byte>(),
            ActiveCodec.Pcma => LinuxG711Codec.Decode(payload, payloadType: 8),
            _ => LinuxG711Codec.Decode(payload, payloadType: 0)
        };
    }

    private byte[] Encode(byte[] pcm, ActiveCodec codec)
    {
        return codec switch
        {
            ActiveCodec.G722 => G722Frame.Encode(_g722EncodeCodec, _g722EncodeState, pcm),
            ActiveCodec.Pcma => LinuxG711Codec.Encode(pcm, payloadType: 8),
            _ => LinuxG711Codec.Encode(pcm, payloadType: 0)
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LinuxAudioDevice));
    }
}
