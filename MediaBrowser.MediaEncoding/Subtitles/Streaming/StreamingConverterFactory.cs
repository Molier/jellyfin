#pragma warning disable CS1591

using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Subtitles.Streaming
{
    /// <summary>
    /// Decides whether a given (inputFormat, outputFormat) pair can be served
    /// via the streaming pipeline, and if so constructs a
    /// <see cref="CueStreamAdapter"/> wrapping the source stream.
    ///
    /// Current coverage (Phase 2):
    ///   input: srt (covers ~80% of real library traffic)
    ///   output: srt, vtt, json
    ///
    /// Anything else (ass, ssa, vtt source, exotic formats, non-UTF-8 SRT)
    /// falls through to the existing batch path in SubtitleEncoder.ConvertSubtitles.
    /// </summary>
    public static class StreamingConverterFactory
    {
        // Sniff window: enough to catch a BOM or invalid-UTF-8 bytes in early cues.
        // SRT cues are small; 4KB covers dozens of cues in typical files.
        private const int SniffSize = 4096;

        public static Stream? TryCreate(
            Stream source,
            string inputFormat,
            string outputFormat,
            ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            if (string.IsNullOrEmpty(inputFormat) || string.IsNullOrEmpty(outputFormat))
            {
                return null;
            }

            var parser = CreateParser(inputFormat);
            if (parser is null)
            {
                return null;
            }

            var writer = CreateWriter(outputFormat);
            if (writer is null)
            {
                return null;
            }

            // Sniff the first bytes to verify the content is UTF-8 (or BOM-tagged).
            // Non-UTF-8 SRTs (Windows-1252, Latin-1, Shift-JIS etc.) must fall back
            // to the batch SubtitleEditParser path, which has encoding detection.
            // We cannot safely stream bytes decoded as UTF-8 from a non-UTF-8 source
            // because by the time we notice the decode error we have already sent
            // mojibake to the client.
            Stream effectiveSource;
            try
            {
                effectiveSource = SniffAndWrap(source);
            }
            catch (InvalidDataException ex)
            {
                logger?.LogDebug(ex, "Streaming SRT path: source is not valid UTF-8, falling back to batch parser");
                return null;
            }

            return new CueStreamAdapter(effectiveSource, parser, writer, inputFormat, logger);
        }

        /// <summary>
        /// Reads up to <see cref="SniffSize"/> bytes from the source, validates
        /// they form valid UTF-8 (accepting an incomplete multi-byte sequence at
        /// the tail), and returns a stream that replays the sniffed bytes followed
        /// by the rest of the source.
        ///
        /// Throws <see cref="InvalidDataException"/> if the bytes are not valid UTF-8.
        /// </summary>
        internal static Stream SniffAndWrap(Stream source)
        {
            var buffer = new byte[SniffSize];
            int total = 0;
            while (total < SniffSize)
            {
                int n = source.Read(buffer, total, SniffSize - total);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            if (total == 0)
            {
                // Source produced nothing during the sync read — optimistically
                // proceed (a tailing source will deliver bytes later). We trust
                // that ffmpeg's -c:s copy of a UTF-8 SRT stays UTF-8.
                return new PrefixedStream(Array.Empty<byte>(), 0, source);
            }

            ValidateUtf8(buffer, total);
            return new PrefixedStream(buffer, total, source);
        }

        /// <summary>
        /// Strict UTF-8 validator. Accepts a BOM (EF BB BF) at the start.
        /// Accepts an incomplete multi-byte sequence at the very end of the
        /// buffer (truncation from sniffing mid-stream).
        /// Rejects UTF-16 BOM explicitly (falls back to batch).
        /// Throws <see cref="InvalidDataException"/> on any invalid byte.
        /// </summary>
        internal static void ValidateUtf8(byte[] buf, int length)
        {
            int i = 0;
            if (length >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
            {
                i = 3;
            }
            else if (length >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF)))
            {
                throw new InvalidDataException("UTF-16 encoded SRT not supported in streaming path");
            }

            while (i < length)
            {
                byte b = buf[i];
                int extra;
                if (b < 0x80)
                {
                    extra = 0;
                }
                else if ((b & 0xE0) == 0xC0)
                {
                    if (b < 0xC2)
                    {
                        throw new InvalidDataException("Invalid UTF-8 overlong sequence");
                    }

                    extra = 1;
                }
                else if ((b & 0xF0) == 0xE0)
                {
                    extra = 2;
                }
                else if ((b & 0xF8) == 0xF0)
                {
                    if (b > 0xF4)
                    {
                        throw new InvalidDataException("Invalid UTF-8 start byte >0xF4");
                    }

                    extra = 3;
                }
                else
                {
                    throw new InvalidDataException("Invalid UTF-8 start byte 0x" + b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                }

                if (i + extra >= length)
                {
                    // Incomplete trailing sequence — allowed: buffer may have been
                    // cut mid-codepoint by the sniff.
                    return;
                }

                for (int k = 1; k <= extra; k++)
                {
                    if ((buf[i + k] & 0xC0) != 0x80)
                    {
                        throw new InvalidDataException("Invalid UTF-8 continuation byte");
                    }
                }

                i += 1 + extra;
            }
        }

        private static IAsyncSubtitleParser? CreateParser(string format)
        {
            if (IsSrt(format))
            {
                return new SrtStreamingParser();
            }

            return null;
        }

        private static IStreamingSubtitleWriter? CreateWriter(string format)
        {
            if (IsSrt(format))
            {
                return new SrtStreamingWriter();
            }

            if (IsVtt(format))
            {
                return new VttStreamingWriter();
            }

            if (IsJson(format))
            {
                return new JsonStreamingWriter();
            }

            return null;
        }

        private static bool IsSrt(string f)
            => string.Equals(f, "srt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f, ".srt", StringComparison.OrdinalIgnoreCase);

        private static bool IsVtt(string f)
            => string.Equals(f, "vtt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f, ".vtt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f, "webvtt", StringComparison.OrdinalIgnoreCase);

        private static bool IsJson(string f)
            => string.Equals(f, "js", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f, "json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f, ".json", StringComparison.OrdinalIgnoreCase);
    }
}
