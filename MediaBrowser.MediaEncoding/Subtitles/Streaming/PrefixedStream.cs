#pragma warning disable CS1591

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.MediaEncoding.Subtitles.Streaming
{
    /// <summary>
    /// Read-only stream that serves a byte-array prefix first, then delegates
    /// all reads to an inner stream. Used by <see cref="StreamingConverterFactory"/>
    /// to replay bytes consumed during UTF-8 sniffing without requiring the
    /// source to support Seek.
    ///
    /// Owns the inner stream: disposing this disposes the inner.
    /// </summary>
    internal sealed class PrefixedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly int _prefixLength;
        private readonly Stream _inner;
        private int _prefixPos;
        private bool _disposed;

        public PrefixedStream(byte[] prefix, int prefixLength, Stream inner)
        {
            _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
            _prefixLength = prefixLength;
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixPos < _prefixLength)
            {
                int available = _prefixLength - _prefixPos;
                int toCopy = Math.Min(available, count);
                Buffer.BlockCopy(_prefix, _prefixPos, buffer, offset, toCopy);
                _prefixPos += toCopy;
                return toCopy;
            }

            return _inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixPos < _prefixLength)
            {
                int available = _prefixLength - _prefixPos;
                int toCopy = Math.Min(available, buffer.Length);
                _prefix.AsSpan(_prefixPos, toCopy).CopyTo(buffer.Span);
                _prefixPos += toCopy;
                return toCopy;
            }

            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
