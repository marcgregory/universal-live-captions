using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using System.Text;

namespace UniversalCaptions.Speech.Tests;

/// <summary>
/// Deterministic protocol-contract tests for <see cref="LineProtocolFasterWhisperProcess"/>. A fake
/// worker fixture emits exactly the production wire format over an in-memory stdout stream, and the
/// real production reader is exercised unchanged. No Python/venv/model is required. Guards the two
/// Slice 9 wire bugs (magic byte order <c>0x46574355</c> and the 20-byte segment header) against
/// regression.
/// </summary>
public sealed class LineProtocolFasterWhisperProcessProtocolTests
{
    private const int Magic = 0x46574355; // "UCWF" read as a little-endian int32
    private const int WrongMagic = 0x55435746; // byte-swapped regression value
    private const int Version = 1;

    private readonly record struct SegmentFrame(double Start, double End, string Text);

    [Fact]
    public async Task StartAsync_And_TranscribeAsync_ValidFrame_20ByteHeader_ParsesExactly()
    {
        var fullResponse = BuildResponse(0, Magic, new SegmentFrame(0.5, 1.25, "Kumusta"));
        var stdout = new FakeWorkerStream(BuildPingResponse().Concat(fullResponse).ToArray());
        var fixture = new FakeWorkerFixture(stdout);

        await using (fixture.Process)
        {
            await fixture.Process.StartAsync(CancellationToken.None);
            var segments = await fixture.Process.TranscribeAsync(
                new short[] { 1, 2, 3 }, "tl", CancellationToken.None);

            var segment = Assert.Single(segments);
            Assert.Equal("Kumusta", segment.Text);
            Assert.Equal(0.5, segment.Start.TotalSeconds);
            Assert.Equal(1.25, segment.End.TotalSeconds);

            // The reader must consume exactly the whole frame: 16 (response header) + 20 (segment
            // header) + 7 (text). Nothing left over, nothing read past the frame.
            Assert.Equal(fullResponse.Length, stdout.ReadPosition - 16);
            Assert.Equal(fullResponse.Length + 16, stdout.Length);
        }
    }

    [Fact]
    public async Task RequestHeader_WritesCorrectMagic_AndLayout()
    {
        var stdout = new FakeWorkerStream(
            BuildPingResponse()
                .Concat(BuildResponse(0, Magic, new SegmentFrame(0.0, 1.0, "Kumusta")))
                .ToArray());
        var fixture = new FakeWorkerFixture(stdout);

        await using (fixture.Process)
        {
            await fixture.Process.StartAsync(CancellationToken.None);
            var pcm = new short[] { 100, -100, 200 };
            await fixture.Process.TranscribeAsync(pcm, "tl", CancellationToken.None);

            var request = fixture.WrittenRequest;
            // Ping request = 20-byte header only (empty PCM, no language).
            AssertRequestHeader(request, 0, sampleRate: 16_000, sampleCount: 0, languageLength: 0);
            // Transcribe request = header + "tl" + 3 x int16 PCM.
            AssertRequestHeader(request, 20, sampleRate: 16_000, sampleCount: 3, languageLength: 2);
            Assert.Equal("tl", Encoding.UTF8.GetString(request.AsSpan(20 + 20, 2)));
            Assert.Equal(100, BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(20 + 22, 2)));
            Assert.Equal(-100, BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(20 + 24, 2)));
            Assert.Equal(200, BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(20 + 26, 2)));
        }
    }

    [Fact]
    public async Task WrongMagic_IsRejected()
    {
        var stdout = new FakeWorkerStream(
            BuildPingResponse()
                .Concat(BuildResponse(0, WrongMagic, new SegmentFrame(0.0, 1.0, "Kumusta")))
                .ToArray());
        var fixture = new FakeWorkerFixture(stdout);

        await using (fixture.Process)
        {
            await fixture.Process.StartAsync(CancellationToken.None);
            var ex = await Assert.ThrowsAsync<FasterWhisperProcessException>(
                () => fixture.Process.TranscribeAsync(new short[] { 1 }, null, CancellationToken.None));

            Assert.Equal(FasterWhisperErrorKind.Protocol, ex.Kind);
        }
    }

    [Fact]
    public async Task TwentyByteHeader_DoesNotConsumePayload()
    {
        // A reader that consumes only 16 bytes per segment header would treat the first 4 payload
        // bytes ("Kums") as the text length -> a huge length -> EOF -> protocol failure. The correct
        // 20-byte reader consumes exactly 20 bytes and the frame parses cleanly, fully used.
        var fullResponse = BuildResponse(0, Magic, new SegmentFrame(0.5, 1.25, "Kumusta"));
        var stdout = new FakeWorkerStream(BuildPingResponse().Concat(fullResponse).ToArray());
        var fixture = new FakeWorkerFixture(stdout);

        await using (fixture.Process)
        {
            await fixture.Process.StartAsync(CancellationToken.None);
            var segments = await fixture.Process.TranscribeAsync(
                new short[] { 1 }, null, CancellationToken.None);

            var segment = Assert.Single(segments);
            Assert.Equal("Kumusta", segment.Text);
            Assert.Equal(1.25, segment.End.TotalSeconds);
            Assert.Equal(fullResponse.Length + 16, stdout.ReadPosition);
        }
    }

    [Fact]
    public async Task TwoSegments_ParseInOrder_WithDistinctTimestamps()
    {
        var stdout = new FakeWorkerStream(
            BuildPingResponse()
                .Concat(BuildResponse(
                    0,
                    Magic,
                    new SegmentFrame(0.5, 1.25, "Kumusta"),
                    new SegmentFrame(2.0, 3.5, "Salamat")))
                .ToArray());
        var fixture = new FakeWorkerFixture(stdout);

        await using (fixture.Process)
        {
            await fixture.Process.StartAsync(CancellationToken.None);
            var segments = await fixture.Process.TranscribeAsync(
                new short[] { 1, 2, 3, 4 }, "tl", CancellationToken.None);

            Assert.Collection(
                segments,
                s =>
                {
                    Assert.Equal("Kumusta", s.Text);
                    Assert.Equal(0.5, s.Start.TotalSeconds);
                    Assert.Equal(1.25, s.End.TotalSeconds);
                },
                s =>
                {
                    Assert.Equal("Salamat", s.Text);
                    Assert.Equal(2.0, s.Start.TotalSeconds);
                    Assert.Equal(3.5, s.End.TotalSeconds);
                });
        }
    }

    [Fact]
    public async Task FragmentedPipeReads_ReconstructFrame()
    {
        // Deliberately serve the frame in small, irregular chunks: 3, 7, 1, 9, ...
        var fullResponse = BuildResponse(0, Magic, new SegmentFrame(0.5, 1.25, "Kumusta"));
        var stdout = new FakeWorkerStream(
            BuildPingResponse().Concat(fullResponse).ToArray(),
            chunkPattern: new[] { 3, 7, 1, 9 });
        var fixture = new FakeWorkerFixture(stdout);

        await using (fixture.Process)
        {
            await fixture.Process.StartAsync(CancellationToken.None);
            var segments = await fixture.Process.TranscribeAsync(
                new short[] { 1, 2, 3 }, "tl", CancellationToken.None);

            var segment = Assert.Single(segments);
            Assert.Equal("Kumusta", segment.Text);
            Assert.Equal(0.5, segment.Start.TotalSeconds);
            Assert.Equal(1.25, segment.End.TotalSeconds);
            Assert.Equal(fullResponse.Length + 16, stdout.ReadPosition);
        }
    }

    [Fact]
    public async Task TruncatedSegmentHeader_IsDeterministicProtocolError()
    {
        // Response header declares 1 segment but only 19 of the 20-byte segment header bytes follow,
        // then EOF. The reader must fail deterministically, never yield a partial TranscriptSegment.
        var fullResponse = BuildResponse(0, Magic, new SegmentFrame(0.5, 1.25, "Kumusta"));
        var truncated = fullResponse.Take(16 + 19).ToArray();
        var stdout = new FakeWorkerStream(BuildPingResponse().Concat(truncated).ToArray());
        var fixture = new FakeWorkerFixture(stdout);

        await using (fixture.Process)
        {
            await fixture.Process.StartAsync(CancellationToken.None);
            var ex = await Assert.ThrowsAsync<FasterWhisperProcessException>(
                () => fixture.Process.TranscribeAsync(new short[] { 1 }, null, CancellationToken.None));

            Assert.Equal(FasterWhisperErrorKind.EngineUnavailable, ex.Kind);
            Assert.Contains("closed the protocol stream", ex.Message);
        }
    }

    [Fact]
    public async Task TruncatedResponseHeader_IsDeterministicProtocolError()
    {
        // Only 15 of the 16 response-header bytes are available; the reader must fail, not hang.
        var fullResponse = BuildResponse(0, Magic, new SegmentFrame(0.5, 1.25, "Kumusta"));
        var truncated = fullResponse.Take(15).ToArray();
        var stdout = new FakeWorkerStream(BuildPingResponse().Concat(truncated).ToArray());
        var fixture = new FakeWorkerFixture(stdout);

        await using (fixture.Process)
        {
            await fixture.Process.StartAsync(CancellationToken.None);
            var ex = await Assert.ThrowsAsync<FasterWhisperProcessException>(
                () => fixture.Process.TranscribeAsync(new short[] { 1 }, null, CancellationToken.None));

            Assert.Equal(FasterWhisperErrorKind.EngineUnavailable, ex.Kind);
        }
    }

    [Fact]
    public async Task PayloadBoundary_ConsumesExactlyDeclaredBytes()
    {
        // Multi-byte UTF-8 text: the reader must consume exactly the declared byte length (not the
        // char count), leaving the cursor exactly at the end of the frame.
        var fullResponse = BuildResponse(0, Magic, new SegmentFrame(0.0, 1.0, "Kumustañ"));
        var stdout = new FakeWorkerStream(BuildPingResponse().Concat(fullResponse).ToArray());
        var fixture = new FakeWorkerFixture(stdout);

        await using (fixture.Process)
        {
            await fixture.Process.StartAsync(CancellationToken.None);
            var segments = await fixture.Process.TranscribeAsync(
                new short[] { 1 }, "tl", CancellationToken.None);

            var segment = Assert.Single(segments);
            Assert.Equal("Kumustañ", segment.Text);
            Assert.Equal(fullResponse.Length + 16, stdout.ReadPosition);
        }
    }

    private static byte[] BuildPingResponse() => BuildResponseHeader(0, 0, Magic);

    private static byte[] BuildResponseHeader(int status, int segmentCount, int magic)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), status);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), segmentCount);
        return bytes;
    }

    private static byte[] BuildResponse(int status, int magic, params SegmentFrame[] segments)
    {
        var ms = new MemoryStream();
        ms.Write(BuildResponseHeader(status, segments.Length, magic));
        foreach (var s in segments)
        {
            var textBytes = Encoding.UTF8.GetBytes(s.Text);
            var segment = new byte[20 + textBytes.Length];
            BinaryPrimitives.WriteDoubleLittleEndian(segment.AsSpan(0, 8), s.Start);
            BinaryPrimitives.WriteDoubleLittleEndian(segment.AsSpan(8, 8), s.End);
            BinaryPrimitives.WriteInt32LittleEndian(segment.AsSpan(16, 4), textBytes.Length);
            textBytes.CopyTo(segment.AsSpan(20));
            ms.Write(segment);
        }

        return ms.ToArray();
    }

    private static void AssertRequestHeader(
        byte[] request,
        int offset,
        int sampleRate,
        int sampleCount,
        int languageLength)
    {
        Assert.Equal(Magic, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(offset, 4)));
        Assert.Equal(Version, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(offset + 4, 4)));
        Assert.Equal(sampleRate, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(offset + 8, 4)));
        Assert.Equal(sampleCount, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(offset + 12, 4)));
        Assert.Equal(languageLength, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(offset + 16, 4)));
    }

    /// <summary>
    /// In-memory stdout for the fake worker. Serves the preloaded frame bytes, optionally in small
    /// irregular chunks to simulate real pipe reads (which are not message boundaries), and tracks
    /// how many bytes the production reader has consumed.
    /// </summary>
    private sealed class FakeWorkerStream : Stream
    {
        private readonly byte[] _data;
        private readonly int[] _chunkPattern;
        private int _readPosition;
        private int _chunkIndex;

        public FakeWorkerStream(byte[] data, int[]? chunkPattern = null)
        {
            _data = data;
            _chunkPattern = chunkPattern ?? new[] { Math.Max(1, data.Length) };
        }

        public int ReadPosition => _readPosition;
        public override long Length => _data.Length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Position
        {
            get => _readPosition;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_readPosition >= _data.Length)
            {
                return 0;
            }

            int chunk = _chunkPattern[_chunkIndex % _chunkPattern.Length];
            _chunkIndex++;
            int n = Math.Min(count, Math.Min(_data.Length - _readPosition, chunk));
            Buffer.BlockCopy(_data, _readPosition, buffer, offset, n);
            _readPosition += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var temp = new byte[buffer.Length];
            int n = Read(temp, 0, temp.Length);
            temp.AsSpan(0, n).CopyTo(buffer.Span);
            return ValueTask.FromResult(n);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    public void BuildWorkerArguments_IncludesThreadsKnob_InContractPosition(int threads)
    {
        var process = new LineProtocolFasterWhisperProcess(
            new FasterWhisperEngineOptions { ServerScriptPath = "fake_worker.py", Threads = threads });

        var args = process.BuildWorkerArguments();

        Assert.Equal(10, args.Count);
        Assert.Equal(new[] { "-u", "fake_worker.py", "--model", "small", "--compute", "int8" }, args.Take(6).ToArray());
        Assert.Equal("--threads", args[6]);
        Assert.Equal(threads.ToString(CultureInfo.InvariantCulture), args[7]);
        Assert.Equal(new[] { "--beam-size", "5" }, args.Skip(8).ToArray());
    }

    /// <summary>
    /// Wires a fake-worker stdout stream and a capture stdin stream into the real
    /// <see cref="LineProtocolFasterWhisperProcess"/> through its internal test seam.
    /// </summary>
    private sealed class FakeWorkerFixture
    {
        private readonly MemoryStream _stdin = new();

        public FakeWorkerFixture(FakeWorkerStream stdout)
        {
            Stdout = stdout;
            Process = new LineProtocolFasterWhisperProcess(
                new FasterWhisperEngineOptions { ServerScriptPath = "fake_worker.py" },
                _stdin,
                stdout);
        }

        public FakeWorkerStream Stdout { get; }

        public LineProtocolFasterWhisperProcess Process { get; }

        public byte[] WrittenRequest => _stdin.ToArray();
    }
}
