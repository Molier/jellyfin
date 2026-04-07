#pragma warning disable CS1591

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Subtitles.Streaming
{
    /// <summary>
    /// Pull-based <see cref="Stream"/> adapter that turns a
    /// (parser -> writer) async pipeline into a <see cref="Stream"/> the
    /// ASP.NET machinery can copy progressively into the HTTP response body.
    ///
    /// Internal layout:
    ///
    ///     source Stream (TailingFileStream around the ffmpeg output file)
    ///         -> IAsyncSubtitleParser.ParseAsync  (yields SubtitleTrackEvent)
    ///             -> IStreamingSubtitleWriter.WriteAsync
    ///                 -> PipeWriter  (flushed per cue)
    ///                     -> PipeReader
    ///                         -> Read / ReadAsync  (what the caller sees)
    ///
    /// The writer pumps into a <see cref="System.IO.Pipelines.Pipe"/>'s writer
    /// end on a background task; <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>
    /// pulls from the reader end. Backpressure, cancellation, and exceptions
    /// all flow through the pipe primitives; no manual buffering.
    ///
    /// Lifetime: the adapter owns the source stream and disposes it when the
    /// adapter is disposed. The background pump terminates on source-EOF,
    /// exception, or cancellation.
    /// </summary>
    public sealed class CueStreamAdapter : Stream
    {
        private readonly Stream _source;
        private readonly IAsyncSubtitleParser _parser;
        private readonly IStreamingSubtitleWriter _writer;
        private readonly string _inputFormat;
        private readonly ILogger? _logger;
        private readonly Pipe _pipe;
        private readonly CancellationTokenSource _pumpCts;
        private Task? _pumpTask;
        private bool _pumpStarted;
        private ReadOnlySequence<byte> _currentBuffer;
        private SequencePosition _currentBufferConsumedTo;
        private bool _completed;
        private bool _disposed;
        private ExceptionDispatchInfoBox? _pumpException;

        private sealed class ExceptionDispatchInfoBox
        {
            public System.Runtime.ExceptionServices.ExceptionDispatchInfo Info { get; set; } = null!;
        }

        public CueStreamAdapter(
            Stream source,
            IAsyncSubtitleParser parser,
            IStreamingSubtitleWriter writer,
            string inputFormat,
            ILogger? logger = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _inputFormat = inputFormat ?? throw new ArgumentNullException(nameof(inputFormat));
            _logger = logger;

            // Default PipeOptions: ~64KB pause threshold. Plenty for subtitles.
            _pipe = new Pipe();
            _pumpCts = new CancellationTokenSource();
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Sync path: delegate to async and block. ASP.NET's CopyToAsync will
            // call ReadAsync instead, so this is rarely hit.
            return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            EnsurePumpStarted();

            if (_completed && _currentBuffer.IsEmpty)
            {
                SurfacePumpException();
                return 0;
            }

            if (_currentBuffer.IsEmpty)
            {
                ReadResult result;
                try
                {
                    result = await _pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    SurfacePumpException();
                    throw;
                }

                _currentBuffer = result.Buffer;
                _currentBufferConsumedTo = result.Buffer.Start;

                if (result.IsCompleted && result.Buffer.IsEmpty)
                {
                    _completed = true;
                    _pipe.Reader.AdvanceTo(result.Buffer.End);
                    SurfacePumpException();
                    return 0;
                }
            }

            // Copy as much as fits.
            var toCopy = _currentBuffer;
            if (toCopy.Length > buffer.Length)
            {
                toCopy = toCopy.Slice(0, buffer.Length);
            }

            toCopy.CopyTo(buffer.Span);
            int copied = (int)toCopy.Length;

            var newStart = _currentBuffer.GetPosition(copied);
            _currentBufferConsumedTo = newStart;
            _currentBuffer = _currentBuffer.Slice(newStart);

            if (_currentBuffer.IsEmpty)
            {
                // Release the buffer back to the pipe so the writer can continue.
                _pipe.Reader.AdvanceTo(_currentBufferConsumedTo);
            }

            return copied;
        }

        private void EnsurePumpStarted()
        {
            if (_pumpStarted)
            {
                return;
            }

            _pumpStarted = true;
            _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token));
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            var writerStream = _pipe.Writer.AsStream(leaveOpen: true);
            try
            {
                var events = _parser.ParseAsync(_source, _inputFormat, cancellationToken);
                await _writer.WriteAsync(events, writerStream, cancellationToken).ConfigureAwait(false);

                await writerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Streaming subtitle pump failed for format {Format}", _inputFormat);
                _pumpException = new ExceptionDispatchInfoBox
                {
                    Info = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex)
                };

                try
                {
                    await _pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
                }
                catch
                {
                    // ignore secondary failure
                }
            }
            finally
            {
                await writerStream.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void SurfacePumpException()
        {
            var box = _pumpException;
            if (box is not null)
            {
                _pumpException = null;
                box.Info.Throw();
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (disposing)
            {
                try
                {
                    _pumpCts.Cancel();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    _pipe.Reader.Complete();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    _source.Dispose();
                }
                catch
                {
                    // ignore
                }

                // Wait for the pump to wind down so background IO doesn't
                // outlive the stream disposal. Guarded by a short timeout to
                // avoid pathological hangs if the source stream misbehaves.
                try
                {
                    _pumpTask?.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // ignore
                }

                _pumpCts.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
