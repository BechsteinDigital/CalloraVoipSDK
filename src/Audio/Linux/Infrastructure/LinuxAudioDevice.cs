using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.Codecs;
using PortAudioSharp;
using CalloraVoipSdk.Audio.Abstractions.Domain.Devices;
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

    private readonly object _sync = new();
    private readonly ConcurrentQueue<byte[]> _playbackQueue = new();

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
    private OpusDeviceCodec? _opusCodec;

    /// <summary>
    /// Creates a Linux audio device with optional startup options.
    /// </summary>
    public LinuxAudioDevice(AudioDeviceOptions? options = null)
    {
        options ??= new AudioDeviceOptions();

        PortAudio.Initialize();

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
            _payloadTypeCodecMap = parameters.PayloadTypeCodecMap ?? EmptyPayloadTypeCodecMap;
            _activeCodec = ResolveActiveCodec(
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
        PortAudio.Initialize();

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
        PortAudio.Initialize();

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
                });
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
        PortAudio.Initialize();

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
        PortAudio.Initialize();

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
            PortAudio.Terminate();
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

        var playbackPcm = ConvertPcmSampleRate(
            decodedPcm,
            GetCodecSampleRate(inboundCodec),
            playbackSampleRate);
        var adjustedPlayback = ApplyGain(playbackPcm, outputMuted, outputVolume);
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
        if (pcm is not null && pcm.Length >= bytes)
        {
            Marshal.Copy(pcm, 0, output, bytes);
        }
        else
        {
            Marshal.Copy(new byte[bytes], 0, output, bytes);
        }

        return StreamCallbackResult.Continue;
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

        var adjustedCapture = ApplyGain(pcm, inputMuted, inputVolume);

        var outboundSampleRate = GetCodecSampleRate(outboundCodec);
        var outboundPcm = ConvertPcmSampleRate(
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
                _ = localSender.SendAsync(opusFrame, CancellationToken.None);
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
        _ = localSender.SendAsync(frame, CancellationToken.None);

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
            framesPerBuffer: ComputeFramesPerBuffer(_activeSampleRate),
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
            framesPerBuffer: ComputeFramesPerBuffer(_activeSampleRate),
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
        _payloadTypeCodecMap = EmptyPayloadTypeCodecMap;
        _activeCodec = ActiveCodec.Pcmu;
        _outboundCodec = ActiveCodec.Pcmu;
        _connected = false;
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
        if (inboundPayloadType is < 0 or > 127)
            return;

        lock (_sync)
        {
            if (_outboundCodec == inboundCodec && _outboundPayloadType == inboundPayloadType)
                return;

            _outboundCodec = inboundCodec;
            _outboundPayloadType = inboundPayloadType;
        }
    }

    private static ActiveCodec ResolveActiveCodec(
        int payloadType,
        int sampleRate,
        string codecName,
        IReadOnlyDictionary<int, string> payloadTypeCodecMap)
    {
        if (MapCodecNameToActiveCodec(codecName) is { } named)
            return named;

        if (payloadTypeCodecMap.TryGetValue(payloadType, out var mapped)
            && MapCodecNameToActiveCodec(mapped) is { } mappedCodec)
        {
            return mappedCodec;
        }

        if (payloadType == 9 || sampleRate >= 16000)
            return ActiveCodec.G722;
        if (payloadType == 8)
            return ActiveCodec.Pcma;
        return ActiveCodec.Pcmu;
    }

    private bool TryResolveCodecFromMap(int payloadType, out ActiveCodec codec)
    {
        if (_payloadTypeCodecMap.TryGetValue(payloadType, out var codecName)
            && MapCodecNameToActiveCodec(codecName) is { } mappedCodec)
        {
            codec = mappedCodec;
            return true;
        }

        codec = default;
        return false;
    }

    private static ActiveCodec? MapCodecNameToActiveCodec(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
            return null;

        return codecName.Trim().ToUpperInvariant() switch
        {
            "G722" or "G.722" => ActiveCodec.G722,
            "PCMA" or "A-LAW" or "A_LAW" => ActiveCodec.Pcma,
            "PCMU" or "MU-LAW" or "MU_LAW" => ActiveCodec.Pcmu,
            "OPUS" => ActiveCodec.Opus,
            _ => null
        };
    }

    private static int GetCodecSampleRate(ActiveCodec codec) => codec switch
    {
        ActiveCodec.Opus => OpusPayloadCodec.RtpClockRate, // 48 kHz
        ActiveCodec.G722 => 16_000,
        _ => 8_000
    };

    private byte[] Decode(byte[] payload, ActiveCodec codec)
    {
        return codec switch
        {
            ActiveCodec.G722 => DecodeG722(payload),
            ActiveCodec.Opus => _opusCodec?.Decode(payload) ?? Array.Empty<byte>(),
            ActiveCodec.Pcma => LinuxG711Codec.Decode(payload, payloadType: 8),
            _ => LinuxG711Codec.Decode(payload, payloadType: 0)
        };
    }

    private byte[] Encode(byte[] pcm, ActiveCodec codec)
    {
        return codec switch
        {
            ActiveCodec.G722 => EncodeG722(pcm),
            ActiveCodec.Pcma => LinuxG711Codec.Encode(pcm, payloadType: 8),
            _ => LinuxG711Codec.Encode(pcm, payloadType: 0)
        };
    }

    private byte[] EncodeG722(byte[] pcm)
    {
        var state = _g722EncodeState;
        if (state is null)
            return Array.Empty<byte>();

        var sampleCount = pcm.Length / 2;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);

        var encoded = new byte[Math.Max(1, sampleCount / 2)];
        new G722Codec().Encode(state, encoded, samples, sampleCount);
        return encoded;
    }

    private byte[] DecodeG722(byte[] payload)
    {
        var state = _g722DecodeState;
        if (state is null)
            return Array.Empty<byte>();

        var samples = new short[payload.Length * 2];
        new G722Codec().Decode(state, samples, payload, payload.Length);

        var pcm = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return pcm;
    }

    private static byte[] ConvertPcmSampleRate(byte[] pcm, int sourceSampleRate, int targetSampleRate)
    {
        if (pcm.Length == 0)
            return pcm;
        if (sourceSampleRate <= 0 || targetSampleRate <= 0)
            return pcm;
        if (sourceSampleRate == targetSampleRate)
            return pcm;

        var sourceSamples = pcm.Length / 2;
        if (sourceSamples == 0)
            return Array.Empty<byte>();

        var targetSamples = Math.Max(
            1,
            (int)Math.Round(
                sourceSamples * (double)targetSampleRate / sourceSampleRate,
                MidpointRounding.AwayFromZero));

        var converted = new byte[targetSamples * 2];
        for (var i = 0; i < targetSamples; i++)
        {
            var sourceIndex = (int)Math.Min(sourceSamples - 1, (long)i * sourceSampleRate / targetSampleRate);
            converted[i * 2] = pcm[sourceIndex * 2];
            converted[i * 2 + 1] = pcm[sourceIndex * 2 + 1];
        }

        return converted;
    }

    private static byte[] ApplyGain(byte[] pcm, bool muted, float volume)
    {
        if (pcm.Length == 0)
            return pcm;

        if (muted || volume <= 0f)
            return new byte[pcm.Length];

        if (Math.Abs(volume - 1f) < 0.0001f)
            return pcm;

        var adjusted = new byte[pcm.Length];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var scaled = (int)Math.Round(sample * volume, MidpointRounding.AwayFromZero);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
            adjusted[i] = (byte)(scaled & 0xFF);
            adjusted[i + 1] = (byte)(scaled >> 8);
        }

        return adjusted;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LinuxAudioDevice));
    }

    private enum ActiveCodec
    {
        Pcmu,
        Pcma,
        G722,
        Opus
    }
}
