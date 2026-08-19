using System.Reflection;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Media;
using CalloraVoipSdk.Core.Infrastructure.Media;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #16 media-file codec hardening: MP3 passthrough ID3v2/junk resync, WAV header partial-read
/// tolerance, and the MP3 transcoding writer lifecycle (async construction, non-throwing dispose).
/// </summary>
public sealed class MediaFileCodecHardeningTests
{
    // MPEG-1 Layer III, 128 kbps, 44100 Hz, no padding: sync=0x7FF, version=11, layer=01,
    // protection=1 -> 0xFF 0xFB; bitrateIdx=1001 (128), srIdx=00 (44100) -> 0x90; rest 0x00.
    // FrameLength = 144 * 128000 / 44100 = 417 bytes.
    private const int Mpeg1L3FrameLength = 417;

    // ---- Media #16.1: MP3 passthrough resync ----

    [Fact]
    public async Task Mp3_passthrough_reads_a_bare_frame()
    {
        var frame = BuildMp3Frame();
        var path = await WriteTempAsync(frame);
        try
        {
            await using var reader = new Mp3PassthroughReader(path, PassthroughContext());
            var read = await reader.ReadNextFrameAsync();

            Assert.NotNull(read);
            Assert.Equal(frame.Length, read!.Value.Frame.Payload.Length);
            Assert.True(read.Value.Frame.Payload.Span.SequenceEqual(frame));
            Assert.Null(await reader.ReadNextFrameAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Mp3_passthrough_skips_a_leading_id3v2_tag()
    {
        var frame = BuildMp3Frame();
        var stream = Combine(BuildId3v2Tag(64), frame);
        var path = await WriteTempAsync(stream);
        try
        {
            await using var reader = new Mp3PassthroughReader(path, PassthroughContext());
            var read = await reader.ReadNextFrameAsync();

            Assert.NotNull(read);
            Assert.Equal(frame.Length, read!.Value.Frame.Payload.Length);
            Assert.True(read.Value.Frame.Payload.Span.SequenceEqual(frame));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Mp3_passthrough_resyncs_past_junk_bytes()
    {
        var frame = BuildMp3Frame();
        var junk = new byte[] { 0x00, 0x13, 0x37, 0xFF, 0x00, 0xAB };
        var stream = Combine(junk, frame);
        var path = await WriteTempAsync(stream);
        try
        {
            await using var reader = new Mp3PassthroughReader(path, PassthroughContext());
            var read = await reader.ReadNextFrameAsync();

            Assert.NotNull(read);
            Assert.True(read!.Value.Frame.Payload.Span.SequenceEqual(frame));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Media #16.5: WAV header partial reads ----

    [Fact]
    public void Wav_header_parses_when_delivered_one_byte_per_read()
    {
        var wav = BuildMinimalWav([0x01, 0x00, 0x02, 0x00]);

        // ChunkedReadStream returns at most one byte per Read, which is exactly the partial-read
        // pattern that pre-fix ParseHeader rejected. With the ReadExactly loop it must succeed.
        using var stream = new ChunkedReadStream(wav, maxBytesPerRead: 1);
        var (dataStart, dataLength, sampleRate) = WavAudioFileReader.ParseHeader(stream, fallbackSampleRate: 0);

        Assert.Equal(44, dataStart);
        Assert.Equal(4, dataLength);
        Assert.Equal(8000, sampleRate);
    }

    [Fact]
    public async Task Wav_reader_reads_a_normal_file_end_to_end()
    {
        var wav = BuildMinimalWav([0x01, 0x00, 0x02, 0x00]);
        var path = Path.Combine(Path.GetTempPath(), $"voipsdk-wav-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(path, wav);
        try
        {
            var context = new AudioFileCodecContext(
                PayloadType: 11, ClockRate: 8000, SampleRate: 8000, SamplesPerFrame: 160, CodecName: "L16");

            await using var reader = new WavAudioFileReader(path, context);
            var frame = await reader.ReadNextFrameAsync();
            Assert.NotNull(frame);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Media #16.3: ffmpeg process lifecycle ----

    [Fact]
    public async Task Ffmpeg_run_is_cancellable_and_does_not_leak_the_process()
    {
        if (!FfmpegProcessRunner.IsAvailable())
            return; // ffmpeg unavailable: the cancellation/kill path cannot be exercised here.

        var before = CountFfmpegProcesses();
        var output = Path.Combine(Path.GetTempPath(), $"voipsdk-ffmpeg-kill-{Guid.NewGuid():N}.mp4");

        using var cts = new CancellationTokenSource();
        // Long-running (60s) CPU-bound ffmpeg writing to a *file*, not to the redirected stdout.
        // That matters: if it wrote to stdout, abandoning the reader would break the pipe and let
        // ffmpeg self-terminate, masking whether our explicit Kill fired. Writing to a file means
        // only KillProcessTree can stop it within the poll window.
        var run = FfmpegProcessRunner.RunAsync(
            psi =>
            {
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("error");
                // -re paces the synthetic source to wall-clock so the job runs for real seconds
                // (otherwise lavfi encodes 60s of video in well under a second and finishes before
                // the test can cancel it).
                psi.ArgumentList.Add("-re");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("lavfi");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add("testsrc=duration=60:size=320x240:rate=30");
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("ultrafast");
                psi.ArgumentList.Add(output);
            },
            cts.Token);

        try
        {
            // Give ffmpeg a moment to actually spawn, then cancel.
            await Task.Delay(600);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run)
                ;

            // The killed process must drain; poll briefly so we do not race the OS reaping it.
            for (var i = 0; i < 60 && CountFfmpegProcesses() > before; i++)
                await Task.Delay(50);

            Assert.True(
                CountFfmpegProcesses() <= before,
                "ffmpeg process was not killed on cancellation (leaked child process).");
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    private static int CountFfmpegProcesses()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("ffmpeg").Length;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // Process enumeration unavailable; treat as inconclusive-but-zero for the assertion.
            _ = ex;
            return 0;
        }
    }

    // ---- Media #16.4: transcoding writer lifecycle ----

    [Fact]
    public void Mp3_transcoding_writer_has_no_public_constructor_and_an_async_factory()
    {
        // Media #16(a): construction goes through an async factory, not a blocking constructor.
        var factory = typeof(Mp3TranscodingWriter).GetMethod(
            nameof(Mp3TranscodingWriter.CreateAsync),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(factory);

        var publicCtors = typeof(Mp3TranscodingWriter)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.Empty(publicCtors);
    }

    [Fact]
    public async Task Mp3_transcoding_writer_dispose_does_not_throw_when_encode_fails()
    {
        if (!FfmpegProcessRunner.IsAvailable())
            return; // ffmpeg unavailable: encode-failure path cannot be exercised on this machine.

        // Media #16(b): DisposeAsync must log (not throw) when the ffmpeg encode fails. We force a
        // failure by pointing the output path at an existing *directory*, which ffmpeg cannot open
        // as an output file.
        var outputDir = Path.Combine(Path.GetTempPath(), $"voipsdk-mp3-outdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        try
        {
            var context = new AudioFileCodecContext(
                PayloadType: 0, ClockRate: 8000, SampleRate: 8000, SamplesPerFrame: 160, CodecName: "MP3");

            var writer = await Mp3TranscodingWriter
                .CreateAsync(outputDir, context, new WavAudioFileCodec(), logger: null, CancellationToken.None)
                ;

            await writer.WriteFrameAsync(new MediaFrame(new byte[320], 0, 160));

            // Encode targets a directory -> ffmpeg fails, but DisposeAsync must swallow it.
            await writer.DisposeAsync();

            // Idempotent second dispose must also not throw.
            await writer.DisposeAsync();
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Mp3_transcoding_writer_encodes_a_valid_mp3_on_normal_dispose()
    {
        if (!FfmpegProcessRunner.IsAvailable())
            return; // ffmpeg unavailable: happy-path encode cannot be exercised on this machine.

        var output = Path.Combine(Path.GetTempPath(), $"voipsdk-mp3-ok-{Guid.NewGuid():N}.mp3");
        try
        {
            var context = new AudioFileCodecContext(
                PayloadType: 0, ClockRate: 8000, SampleRate: 8000, SamplesPerFrame: 160, CodecName: "MP3");

            var writer = await Mp3TranscodingWriter
                .CreateAsync(output, context, new WavAudioFileCodec(), logger: null, CancellationToken.None)
                ;

            // ~200 ms of silence.
            for (var i = 0; i < 10; i++)
                await writer.WriteFrameAsync(new MediaFrame(new byte[320], 0, 160));

            await writer.DisposeAsync();

            Assert.True(File.Exists(output));
            Assert.True(new FileInfo(output).Length > 0);
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    // ---- helpers ----

    private static AudioFileCodecContext PassthroughContext() =>
        new(PayloadType: 96, ClockRate: 90000, SampleRate: 44100, SamplesPerFrame: 1152, CodecName: "MP3-PASSTHROUGH");

    private static byte[] BuildMp3Frame()
    {
        var frame = new byte[Mpeg1L3FrameLength];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x00;
        for (var i = 4; i < frame.Length; i++)
            frame[i] = (byte)(i & 0xFF);

        return frame;
    }

    private static byte[] BuildId3v2Tag(int payloadSize)
    {
        var tag = new byte[10 + payloadSize];
        tag[0] = (byte)'I';
        tag[1] = (byte)'D';
        tag[2] = (byte)'3';
        tag[3] = 0x03;
        tag[4] = 0x00;
        tag[5] = 0x00;
        tag[6] = (byte)((payloadSize >> 21) & 0x7F);
        tag[7] = (byte)((payloadSize >> 14) & 0x7F);
        tag[8] = (byte)((payloadSize >> 7) & 0x7F);
        tag[9] = (byte)(payloadSize & 0x7F);
        for (var i = 0; i < payloadSize; i++)
            tag[10 + i] = (byte)'X';

        return tag;
    }

    private static async Task<string> WriteTempAsync(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"voipsdk-mp3-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static byte[] Combine(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }

    private static byte[] BuildMinimalWav(byte[] sampleData)
    {
        var wav = new byte[44 + sampleData.Length];
        void Ascii(int offset, string s)
        {
            for (var i = 0; i < s.Length; i++)
                wav[offset + i] = (byte)s[i];
        }
        void U32(int offset, uint v)
        {
            wav[offset] = (byte)v;
            wav[offset + 1] = (byte)(v >> 8);
            wav[offset + 2] = (byte)(v >> 16);
            wav[offset + 3] = (byte)(v >> 24);
        }
        void U16(int offset, ushort v)
        {
            wav[offset] = (byte)v;
            wav[offset + 1] = (byte)(v >> 8);
        }

        Ascii(0, "RIFF");
        U32(4, (uint)(36 + sampleData.Length));
        Ascii(8, "WAVE");
        Ascii(12, "fmt ");
        U32(16, 16);
        U16(20, 1);     // PCM
        U16(22, 1);     // mono
        U32(24, 8000);  // sample rate
        U32(28, 8000 * 1 * 2);
        U16(32, 2);     // block align
        U16(34, 16);    // bits per sample
        Ascii(36, "data");
        U32(40, (uint)sampleData.Length);
        Buffer.BlockCopy(sampleData, 0, wav, 44, sampleData.Length);
        return wav;
    }
}
