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
    /// Anything else (ass, ssa, vtt source, exotic formats) falls through to
    /// the existing batch path in SubtitleEncoder.ConvertSubtitles.
    /// </summary>
    public static class StreamingConverterFactory
    {
        public static Stream? TryCreate(
            Stream source,
            string inputFormat,
            string outputFormat,
            ILogger? logger = null)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

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

            return new CueStreamAdapter(source, parser, writer, inputFormat, logger);
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

        private static bool IsSrt(string format)
            => string.Equals(format, "srt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, ".srt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "subrip", StringComparison.OrdinalIgnoreCase);

        private static bool IsVtt(string format)
            => string.Equals(format, "vtt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, ".vtt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "webvtt", StringComparison.OrdinalIgnoreCase);

        private static bool IsJson(string format)
            => string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "js", StringComparison.OrdinalIgnoreCase);
    }
}
