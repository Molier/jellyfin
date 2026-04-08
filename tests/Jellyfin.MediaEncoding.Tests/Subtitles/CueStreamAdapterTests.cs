#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.MediaEncoding.Subtitles.Streaming;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.MediaEncoding.Subtitles.Tests
{
    /// <summary>
    /// Tests for <see cref="CueStreamAdapter"/> covering progressive streaming,
    /// partial-buffer reads, disposal, exception surfacing (P4) and
    /// multi-segment ReadOnlySequence boundary handling (P5).
    /// </summary>
    public class CueStreamAdapterTests
    {
        private static SubtitleTrackEvent MakeEvent(int n, string text = "text")
            => new SubtitleTrackEvent(n.ToString(CultureInfo.InvariantCulture), text)
            {
                StartPositionTicks = TimeSpan.FromSeconds(n).Ticks,
                EndPositionTicks = TimeSpan.FromSeconds(n + 1).Ticks,
            };

        private static async Task<byte[]> ReadAllWithBuffer(int bufferSize, string srtContent)
        {
            var srcBytes = Encoding.UTF8.GetBytes(srtContent);
            using var source = new MemoryStream(srcBytes);
            var parser = new SrtStreamingParser();
            var writer = new SrtStreamingWriter();
            using var adapter = new CueStreamAdapter(source, parser, writer, ".srt");

            var ms = new MemoryStream();
            var buf = new byte[bufferSize];
            while (true)
            {
                int n = await adapter.ReadAsync(buf.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                await ms.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
            }

            return ms.ToArray();
        }

        // -----------------------------------------------------------------------
        // P4 tests
        // -----------------------------------------------------------------------

        [Fact]
        public async Task SlowDrip_FirstBytesArriveBeforeSourceFinished()
        {
            // Parser emits 5 events with 100ms delay between each.
            var delay = TimeSpan.FromMilliseconds(100);
            var events = new[]
            {
                MakeEvent(1, "cue1"),
                MakeEvent(2, "cue2"),
                MakeEvent(3, "cue3"),
                MakeEvent(4, "cue4"),
                MakeEvent(5, "cue5"),
            };
            var parser = new FakeParser(events, delayBetween: delay);
            var source = new MemoryStream(Array.Empty<byte>());
            using var adapter = new CueStreamAdapter(source, parser, new PassthroughWriter(), ".srt");

            var sw = Stopwatch.StartNew();
            long firstByteMs = -1;
            long finishedMs = -1;

            var buf = new byte[4096];
            while (true)
            {
                int n = await adapter.ReadAsync(buf.AsMemory(), CancellationToken.None);
                if (n == 0)
                {
                    finishedMs = sw.ElapsedMilliseconds;
                    break;
                }

                if (firstByteMs < 0)
                {
                    firstByteMs = sw.ElapsedMilliseconds;
                }
            }

            Assert.True(firstByteMs >= 0, "Should have received at least some bytes");
            Assert.True(finishedMs > firstByteMs, "Should have received more data after first byte");

            // First byte arrives well before all 5 × 100ms finishes.
            // Proves progressive streaming: first-byte-time << source-finished-time.
            Assert.True(
                firstByteMs < finishedMs - 50,
                $"Expected first byte ({firstByteMs}ms) well before finish ({finishedMs}ms)");
        }

        [Fact]
        public async Task PartialBufferReadAsync_SmallBufferReadsCorrectly()
        {
            // 3 cues. Read with 16-byte buffer, compare to large-buffer read.
            var srt =
                "1\n00:00:01,000 --> 00:00:02,000\nHello World cue one\n\n" +
                "2\n00:00:03,000 --> 00:00:04,000\nSecond cue text here\n\n" +
                "3\n00:00:05,000 --> 00:00:06,000\nThird cue text here\n\n";

            var smallResult = await ReadAllWithBuffer(16, srt);
            var largeResult = await ReadAllWithBuffer(4096, srt);

            Assert.NotEmpty(smallResult);
            Assert.Equal(largeResult, smallResult);
        }

        [Fact]
        public async Task DisposeMidStream_SourceIsDisposedNoPumpLeak()
        {
            // Parser emits 10 events slowly; dispose after first read.
            var delay = TimeSpan.FromMilliseconds(50);
            var events = new SubtitleTrackEvent[10];
            for (int i = 0; i < events.Length; i++)
            {
                events[i] = MakeEvent(i + 1, $"cue{(i + 1).ToString(CultureInfo.InvariantCulture)}text");
            }

            var parser = new FakeParser(events, delayBetween: delay);
            var innerSource = new MemoryStream(Array.Empty<byte>());
            var trackingSource = new DisposeTrackingStream(innerSource);

            await using var adapter = new CueStreamAdapter(trackingSource, parser, new PassthroughWriter(), ".srt");

            // Read at least once to start the pump.
            var buf = new byte[4096];
            _ = await adapter.ReadAsync(buf.AsMemory(), CancellationToken.None);

            // Dispose before EOF.
            await adapter.DisposeAsync();

            // Give pump time to wind down.
            await Task.Delay(500);

            Assert.True(trackingSource.WasDisposed, "Source stream should be disposed when adapter is disposed");

            // Idempotent second dispose must not throw.
            await adapter.DisposeAsync();
        }

        [Fact]
        public async Task ParserThrows_ExceptionSurfacedOnSubsequentRead()
        {
            // Parser throws InvalidDataException on the 3rd event (index 2).
            var events = new[]
            {
                MakeEvent(1, "cue1"),
                MakeEvent(2, "cue2"),
                MakeEvent(3, "cue3"),
            };
            var parser = new FakeParser(events, throwOnIndex: 2);
            var source = new MemoryStream(Array.Empty<byte>());
            using var adapter = new CueStreamAdapter(source, parser, new PassthroughWriter(), ".srt");

            var buf = new byte[4096];
            Exception? surfaced = null;
            try
            {
                while (true)
                {
                    int n = await adapter.ReadAsync(buf.AsMemory(), CancellationToken.None);
                    if (n == 0)
                    {
                        break;
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                surfaced = ex;
            }

            Assert.NotNull(surfaced);
            Assert.Contains("Fake parser error at index 2", surfaced!.Message, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------------
        // P5 test: multi-segment ReadOnlySequence boundary handling
        // -----------------------------------------------------------------------

        [Fact]
        public async Task MultiSegment_LargePayload_BoundaryHandledCorrectly()
        {
            // Write 128KB in 256-byte chunks to force many pipe segments (~32+).
            const int totalBytes = 128 * 1024;
            const int chunkSize = 256;

            var singleEvent = new[] { MakeEvent(1, "trigger") };
            var parser = new FakeParser(singleEvent);
            var writer = new ChunkedPayloadWriter(totalBytes, chunkSize);
            var source = new MemoryStream(Array.Empty<byte>());

            // Read with a small buffer (64 bytes) to stress the slice logic.
            using var adapter = new CueStreamAdapter(source, parser, writer, ".srt");

            var ms = new MemoryStream();
            var smallBuf = new byte[64];
            while (true)
            {
                int n = await adapter.ReadAsync(smallBuf.AsMemory(), CancellationToken.None);
                if (n == 0)
                {
                    break;
                }

                await ms.WriteAsync(smallBuf.AsMemory(0, n));
            }

            var result = ms.ToArray();

            // Verify total length.
            Assert.Equal(totalBytes, result.Length);

            // Verify content integrity: each chunk repeats pattern (index % 251).
            for (int i = 0; i < result.Length; i++)
            {
                byte expected = (byte)((i % chunkSize) % 251);
                Assert.Equal(expected, result[i]);
            }
        }

        // -----------------------------------------------------------------------
        // Helper fakes — placed after tests per SA1201 (types after members)
        // -----------------------------------------------------------------------

        /// <summary>
        /// A fake parser that yields pre-baked events, optionally with delays.
        /// </summary>
        private sealed class FakeParser : IAsyncSubtitleParser
        {
            private readonly IReadOnlyList<SubtitleTrackEvent> _events;
            private readonly TimeSpan _delayBetween;
            private readonly int _throwOnIndex;

            public FakeParser(
                IReadOnlyList<SubtitleTrackEvent> events,
                TimeSpan delayBetween = default,
                int throwOnIndex = -1)
            {
                _events = events;
                _delayBetween = delayBetween;
                _throwOnIndex = throwOnIndex;
            }

            public bool SupportsFileExtension(string ext) => true;

            public async IAsyncEnumerable<SubtitleTrackEvent> ParseAsync(
                Stream stream,
                string fileExtension,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                for (int i = 0; i < _events.Count; i++)
                {
                    if (_throwOnIndex >= 0 && i == _throwOnIndex)
                    {
                        throw new InvalidDataException($"Fake parser error at index {i}");
                    }

                    if (_delayBetween > TimeSpan.Zero)
                    {
                        await Task.Delay(_delayBetween, cancellationToken).ConfigureAwait(false);
                    }

                    yield return _events[i];
                }
            }
        }

        /// <summary>
        /// A fake writer that writes the event's Text bytes directly (UTF-8).
        /// Flushes after each cue.
        /// </summary>
        private sealed class PassthroughWriter : IStreamingSubtitleWriter
        {
            public async Task WriteAsync(
                IAsyncEnumerable<SubtitleTrackEvent> events,
                Stream destination,
                CancellationToken cancellationToken)
            {
                await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    var bytes = Encoding.UTF8.GetBytes(evt.Text ?? string.Empty);
                    await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// A fake writer that writes a fixed payload using many small writes,
        /// forcing multiple pipe segments.
        /// </summary>
        private sealed class ChunkedPayloadWriter : IStreamingSubtitleWriter
        {
            private readonly int _totalBytes;
            private readonly int _chunkSize;

            public ChunkedPayloadWriter(int totalBytes, int chunkSize = 512)
            {
                _totalBytes = totalBytes;
                _chunkSize = chunkSize;
            }

            public async Task WriteAsync(
                IAsyncEnumerable<SubtitleTrackEvent> events,
                Stream destination,
                CancellationToken cancellationToken)
            {
                // Consume events so the parser coroutine drives to completion.
                await foreach (var unused in events.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                }

                // Write _totalBytes in small chunks to force multiple pipe segments.
                var chunk = new byte[_chunkSize];
                for (int i = 0; i < _chunkSize; i++)
                {
                    chunk[i] = (byte)(i % 251);
                }

                int remaining = _totalBytes;
                while (remaining > 0)
                {
                    int toWrite = Math.Min(_chunkSize, remaining);
                    await destination.WriteAsync(chunk.AsMemory(0, toWrite), cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    remaining -= toWrite;
                }
            }
        }

        /// <summary>
        /// Wraps a stream and tracks whether Dispose was called.
        /// </summary>
        private sealed class DisposeTrackingStream : Stream
        {
            private readonly Stream _inner;

            public DisposeTrackingStream(Stream inner) => _inner = inner;

            public bool WasDisposed { get; private set; }

            public override bool CanRead => _inner.CanRead;

            public override bool CanSeek => _inner.CanSeek;

            public override bool CanWrite => _inner.CanWrite;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

            public override void SetLength(long value) => _inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                WasDisposed = true;
                _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
