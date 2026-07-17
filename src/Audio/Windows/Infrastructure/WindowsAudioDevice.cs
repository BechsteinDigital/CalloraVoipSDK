using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.Codecs;
using NAudio.Wave;
using CalloraVoipSdk.Audio.Abstractions.Domain.Devices;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Application.Ports.Audio;

namespace CalloraVoipSdk.Audio.Windows;

/// <summary>
/// Windows audio device using NAudio WaveIn/WaveOut.
/// Supports G.711 (PCMU/PCMA), G.722 (wideband, 16 kHz) and Opus (RFC 7587, 48 kHz).
/// Provides runtime controls for device switching, mute, volume, and format updates.
/// </summary>
public sealed class WindowsAudioDevice : IAudioDeviceProvider, IAudioDeviceRuntimeControl, IDisposable
{
    private static readonly IReadOnlyDictionary<int, string> EmptyPayloadTypeCodecMap =
        new ReadOnlyDictionary<int, string>(new Dictionary<int, string>());

    private readonly object _sync = new();

    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playbackBuffer;
    private IMediaReceiver? _receiver;
    private IMediaSender? _sender;

    private bool _disposed;
    private bool _connected;

    private string _name;
    private int _inputDeviceNumber;
    private int _outputDeviceNumber;

    private int _payloadType;
    private IReadOnlyDictionary<int, string> _payloadTypeCodecMap = EmptyPayloadTypeCodecMap;
    private ActiveCodec _activeCodec = ActiveCodec.Pcmu;
    private int _activeSampleRate;
    private int _bitsPerSample;
    private int _channels;

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
    /// Creates a Windows audio device with optional startup options.
    /// </summary>
    public WindowsAudioDevice(AudioDeviceOptions? options = null)
    {
        options ??= new AudioDeviceOptions();

        _inputDeviceNumber = options.InputDeviceNumber;
        _outputDeviceNumber = options.OutputDeviceNumber;
        _activeSampleRate = options.SampleRate > 0 ? options.SampleRate : 8000;
        _bitsPerSample = options.BitsPerSample > 0 ? options.BitsPerSample : 16;
        _channels = options.Channels > 0 ? options.Channels : 1;
        _name = GetInputDeviceName(_inputDeviceNumber);
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
            _payloadType = parameters.PayloadType;
            _payloadTypeCodecMap = parameters.PayloadTypeCodecMap ?? EmptyPayloadTypeCodecMap;
            _activeCodec = ResolveActiveCodec(
                parameters.PayloadType,
                parameters.SampleRate,
                parameters.CodecName,
                _payloadTypeCodecMap);

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
        var result = new List<AudioDeviceDescriptor>
        {
            new("-1", "Default Microphone", isDefault: true)
        };

        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            result.Add(new AudioDeviceDescriptor(
                i.ToString(CultureInfo.InvariantCulture),
                WaveInEvent.GetCapabilities(i).ProductName,
                isDefault: false));
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioDeviceDescriptor> GetAvailableOutputDevices()
    {
        var result = new List<AudioDeviceDescriptor>
        {
            new("-1", "Default Speaker", isDefault: true)
        };

        var count = WaveInterop.waveOutGetNumDevs();
        var capsSize = Marshal.SizeOf<WaveOutCapabilities>();
        for (var i = 0; i < count; i++)
        {
            WaveInterop.waveOutGetDevCaps((IntPtr)i, out var capabilities, capsSize);
            result.Add(new AudioDeviceDescriptor(
                i.ToString(CultureInfo.InvariantCulture),
                capabilities.ProductName,
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
                inputDeviceId: _inputDeviceNumber.ToString(CultureInfo.InvariantCulture),
                outputDeviceId: _outputDeviceNumber.ToString(CultureInfo.InvariantCulture),
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

        var parsedDevice = ParseDeviceNumber(deviceId);
        ValidateInputDeviceNumber(parsedDevice);

        lock (_sync)
        {
            if (_inputDeviceNumber == parsedDevice)
                return;

            _inputDeviceNumber = parsedDevice;
            _name = GetInputDeviceName(_inputDeviceNumber);

            if (_connected)
                RebuildInputStreamLocked();
        }
    }

    /// <inheritdoc />
    public void SwitchOutputDevice(string? deviceId)
    {
        ThrowIfDisposed();

        var parsedDevice = ParseDeviceNumber(deviceId);
        ValidateOutputDeviceNumber(parsedDevice);

        lock (_sync)
        {
            if (_outputDeviceNumber == parsedDevice)
                return;

            _outputDeviceNumber = parsedDevice;
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

            if (_waveOut is not null)
                _waveOut.Volume = ClampWaveOutVolume(volume);
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
            if (_waveOut is not null)
                _waveOut.Volume = isMuted ? 0f : ClampWaveOutVolume(_outputVolume);
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
    /// Returns available microphone names for compatibility with existing samples.
    /// </summary>
    public static IReadOnlyList<string> GetInputDevices()
    {
        var result = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            result.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return result;
    }

    /// <summary>
    /// Returns available speaker names for compatibility with existing samples.
    /// </summary>
    public static IReadOnlyList<string> GetOutputDevices()
    {
        var result = new List<string>();
        var count = WaveInterop.waveOutGetNumDevs();
        var capsSize = Marshal.SizeOf<WaveOutCapabilities>();
        for (var i = 0; i < count; i++)
        {
            WaveInterop.waveOutGetDevCaps((IntPtr)i, out var capabilities, capsSize);
            result.Add(capabilities.ProductName);
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
        }
    }

    private void OnFrameReceived(object? sender, MediaFrameReceivedEventArgs e)
    {
        var payload = e.Frame.Payload.ToArray();
        var inboundCodec = ResolveInboundCodec(e.Frame.PayloadType);
        var decoded = Decode(payload, inboundCodec);

        int playbackSampleRate;
        bool outputMuted;
        float outputVolume;
        BufferedWaveProvider? playbackBuffer;
        lock (_sync)
        {
            playbackSampleRate = _activeSampleRate;
            outputMuted = _outputMuted;
            outputVolume = _outputVolume;
            playbackBuffer = _playbackBuffer;
        }

        var playbackPcm = ConvertPcmSampleRate(
            decoded,
            GetCodecSampleRate(inboundCodec),
            playbackSampleRate);
        var adjustedPcm = ApplyGain(playbackPcm, outputMuted, outputVolume);
        playbackBuffer?.AddSamples(adjustedPcm, 0, adjustedPcm.Length);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
            return;

        IMediaSender? localSender;
        ActiveCodec outboundCodec;
        int outboundPayloadType;
        int deviceSampleRate;
        bool inputMuted;
        float inputVolume;

        lock (_sync)
        {
            localSender = _sender;
            outboundCodec = _activeCodec;
            outboundPayloadType = _payloadType;
            deviceSampleRate = _activeSampleRate;
            inputMuted = _inputMuted;
            inputVolume = _inputVolume;
        }

        if (localSender is null)
            return;

        var capturedPcm = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, capturedPcm, 0, e.BytesRecorded);
        var adjustedCapture = ApplyGain(capturedPcm, inputMuted, inputVolume);

        var outboundSampleRate = GetCodecSampleRate(outboundCodec);
        var outboundPcm = ConvertPcmSampleRate(
            adjustedCapture,
            deviceSampleRate,
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

            return;
        }

        var encoded = Encode(outboundPcm, outboundCodec);

        var rtpClockRate = outboundCodec == ActiveCodec.G722 ? 8000d : outboundSampleRate;
        var sampleCount = Math.Max(1, outboundPcm.Length / 2);
        var durationRtpUnits = (uint)Math.Max(
            1,
            (int)Math.Round(sampleCount * rtpClockRate / outboundSampleRate));

        var frame = new MediaFrame(encoded, PayloadType: outboundPayloadType, durationRtpUnits);
        _ = localSender.SendAsync(frame, CancellationToken.None);
    }

    private void StartInputStreamLocked()
    {
        var waveFormat = new WaveFormat(_activeSampleRate, _bitsPerSample, _channels);

        _waveIn = new WaveInEvent
        {
            DeviceNumber = _inputDeviceNumber,
            WaveFormat = waveFormat,
            BufferMilliseconds = 20
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
    }

    private void StartOutputStreamLocked()
    {
        var waveFormat = new WaveFormat(_activeSampleRate, _bitsPerSample, _channels);

        _playbackBuffer = new BufferedWaveProvider(waveFormat)
        {
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent
        {
            DeviceNumber = _outputDeviceNumber,
            Volume = _outputMuted ? 0f : ClampWaveOutVolume(_outputVolume)
        };

        _waveOut.Init(_playbackBuffer);
        _waveOut.Play();
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
        if (_waveIn is null)
            return;

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _waveIn = null;
    }

    private void StopOutputStreamLocked()
    {
        if (_waveOut is not null)
        {
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
        }

        _playbackBuffer = null;
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
        _payloadType = 0;
        _payloadTypeCodecMap = EmptyPayloadTypeCodecMap;
        _activeCodec = ActiveCodec.Pcmu;
        _connected = false;
    }

    private static float ClampWaveOutVolume(float volume) => Math.Clamp(volume, 0f, 1f);

    private static void ValidateVolume(float volume)
    {
        if (float.IsNaN(volume) || float.IsInfinity(volume) || volume < 0f || volume > 2f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(volume),
                "Volume must be finite and in range 0..2.");
        }
    }

    private static int ParseDeviceNumber(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return -1;

        if (!int.TryParse(deviceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException("Device id must be a numeric string.", nameof(deviceId));

        return parsed;
    }

    private static void ValidateInputDeviceNumber(int deviceNumber)
    {
        if (deviceNumber == -1)
            return;

        if (deviceNumber < 0 || deviceNumber >= WaveInEvent.DeviceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceNumber),
                $"Input device index must be -1 or in range [0..{WaveInEvent.DeviceCount - 1}].");
        }
    }

    private static void ValidateOutputDeviceNumber(int deviceNumber)
    {
        if (deviceNumber == -1)
            return;

        var deviceCount = WaveInterop.waveOutGetNumDevs();
        if (deviceNumber < 0 || deviceNumber >= deviceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceNumber),
                $"Output device index must be -1 or in range [0..{deviceCount - 1}].");
        }
    }

    private static string GetInputDeviceName(int index)
    {
        if (index < 0 || index >= WaveInEvent.DeviceCount)
            return "Default Microphone";

        return WaveInEvent.GetCapabilities(index).ProductName;
    }

    private ActiveCodec ResolveInboundCodec(int payloadType)
    {
        if (payloadType == 0)
            return ActiveCodec.Pcmu;

        if (payloadType == 8)
            return ActiveCodec.Pcma;

        if (payloadType == 9)
            return ActiveCodec.G722;

        if (TryResolveCodecFromMap(payloadType, out var mappedCodec))
            return mappedCodec;

        return _activeCodec;
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
            ActiveCodec.Pcma => DecodeG711(payload, payloadType: 8),
            _ => DecodeG711(payload, payloadType: 0)
        };
    }

    private byte[] Encode(byte[] pcm, ActiveCodec codec)
    {
        return codec switch
        {
            ActiveCodec.G722 => EncodeG722(pcm),
            ActiveCodec.Pcma => EncodeG711(pcm, payloadType: 8),
            _ => EncodeG711(pcm, payloadType: 0)
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
        _g722EncodeCodec.Encode(state, encoded, samples, sampleCount);
        return encoded;
    }

    private byte[] DecodeG722(byte[] payload)
    {
        var state = _g722DecodeState;
        if (state is null)
            return Array.Empty<byte>();

        var samples = new short[payload.Length * 2];
        _g722DecodeCodec.Decode(state, samples, payload, payload.Length);

        var pcm = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return pcm;
    }

    private static byte[] EncodeG711(byte[] pcm, int payloadType)
    {
        var sampleCount = pcm.Length / 2;
        var encoded = new byte[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            encoded[i] = payloadType == 0
                ? MuLawEncoder.LinearToMuLawSample(sample)
                : ALawEncoder.LinearToALawSample(sample);
        }

        return encoded;
    }

    private static byte[] DecodeG711(byte[] payload, int payloadType)
    {
        var pcm = new byte[payload.Length * 2];
        for (var i = 0; i < payload.Length; i++)
        {
            var sample = payloadType == 0
                ? MuLawDecoder.MuLawToLinearSample(payload[i])
                : ALawDecoder.ALawToLinearSample(payload[i]);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }

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
            throw new ObjectDisposedException(nameof(WindowsAudioDevice));
    }

    private enum ActiveCodec
    {
        Pcmu,
        Pcma,
        G722,
        Opus
    }
}
