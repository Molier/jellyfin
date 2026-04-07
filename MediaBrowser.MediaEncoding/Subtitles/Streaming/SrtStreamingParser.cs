#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.Subtitles.Streaming
{
    /// <summary>
    /// Hand-rolled incremental SRT (SubRip) parser.
    ///
    /// Reads the input one line at a time via <see cref="StreamReader.ReadLineAsync(CancellationToken)"/>
    /// and emits a <see cref="SubtitleTrackEvent"/> as soon as a complete cue
    /// block (index, time range, text, blank delimiter) has been parsed. Does
    /// not depend on the Nikse.SubtitleEdit library, which is batch-only.
    /// </summary>
    public class SrtStreamingParser : IAsyncSubtitleParser
    {
        private static readonly string[] SupportedExtensions = new[] { "srt", ".srt" };

        /// <inheritdoc />
        public bool SupportsFileExtension(string fileExtension)
        {
            if (string.IsNullOrEmpty(fileExtension))
            {
                return false;
            }

            foreach (var ext in SupportedExtensions)
            {
                if (string.Equals(fileExtension, ext, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<SubtitleTrackEvent> ParseAsync(
            Stream stream,
            string fileExtension,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!SupportsFileExtension(fileExtension))
            {
                throw new ArgumentException("Unsupported file extension: " + fileExtension, nameof(fileExtension));
            }

            // leaveOpen: true — the caller owns the stream lifetime (tailing stream etc.)
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);

            var state = ParseState.AwaitingIndex;
            string? indexLine = null;
            long startTicks = 0;
            long endTicks = 0;
            var textBuilder = new StringBuilder();
            int emittedCount = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                if (line is null)
                {
                    // EOF: flush any in-progress cue (last block without trailing blank).
                    if (state == ParseState.AwaitingText && textBuilder.Length > 0)
                    {
                        yield return BuildEvent(indexLine, ref emittedCount, startTicks, endTicks, textBuilder);
                    }

                    yield break;
                }

                // Strip CR left by \r\n-on-Unix readers (StreamReader already handles \r\n
                // in the common case, but TailingFileStream mid-line reads can leave artifacts).
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line = line[..^1];
                }

                switch (state)
                {
                    case ParseState.AwaitingIndex:
                        if (line.Length == 0)
                        {
                            // Blank line(s) between cues or leading whitespace — skip.
                            continue;
                        }

                        indexLine = line;
                        state = ParseState.AwaitingTime;
                        break;

                    case ParseState.AwaitingTime:
                        if (!TryParseTimeRange(line, out startTicks, out endTicks))
                        {
                            // Recover: maybe the previous "index" line was actually stray text
                            // for a malformed block. Discard this cue and restart.
                            indexLine = null;
                            state = ParseState.AwaitingIndex;
                            continue;
                        }

                        textBuilder.Clear();
                        state = ParseState.AwaitingText;
                        break;

                    case ParseState.AwaitingText:
                        if (line.Length == 0)
                        {
                            // End of cue block — emit.
                            yield return BuildEvent(indexLine, ref emittedCount, startTicks, endTicks, textBuilder);
                            indexLine = null;
                            state = ParseState.AwaitingIndex;
                        }
                        else
                        {
                            if (textBuilder.Length > 0)
                            {
                                textBuilder.Append('\n');
                            }

                            textBuilder.Append(line);
                        }

                        break;
                }
            }
        }

        private static SubtitleTrackEvent BuildEvent(string? indexLine, ref int emittedCount, long startTicks, long endTicks, StringBuilder textBuilder)
        {
            emittedCount++;
            var id = !string.IsNullOrEmpty(indexLine)
                ? indexLine
                : emittedCount.ToString(CultureInfo.InvariantCulture);

            return new SubtitleTrackEvent(id, textBuilder.ToString())
            {
                StartPositionTicks = startTicks,
                EndPositionTicks = endTicks
            };
        }

        /// <summary>
        /// Parses an SRT time range line: "HH:MM:SS,mmm --> HH:MM:SS,mmm".
        /// Tolerant to dot instead of comma (seen in some generators) and to
        /// extra whitespace.
        /// </summary>
        internal static bool TryParseTimeRange(string line, out long startTicks, out long endTicks)
        {
            startTicks = 0;
            endTicks = 0;

            var arrowIdx = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrowIdx < 0)
            {
                return false;
            }

            var startSpan = line.AsSpan(0, arrowIdx).Trim();
            var endSpan = line.AsSpan(arrowIdx + 3).Trim();

            if (!TryParseTimestamp(startSpan, out startTicks))
            {
                return false;
            }

            if (!TryParseTimestamp(endSpan, out endTicks))
            {
                return false;
            }

            return true;
        }

        internal static bool TryParseTimestamp(ReadOnlySpan<char> span, out long ticks)
        {
            ticks = 0;

            // Expected: HH:MM:SS,mmm (srt) or HH:MM:SS.mmm (vtt-style tolerance).
            // Allow missing milliseconds as a last resort.
            int sepIdx = -1;
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i] == ',' || span[i] == '.')
                {
                    sepIdx = i;
                    break;
                }
            }

            ReadOnlySpan<char> hms;
            int millis = 0;
            if (sepIdx >= 0)
            {
                hms = span[..sepIdx];
                var millisSpan = span[(sepIdx + 1)..];

                // Cut or pad to 3 digits.
                if (millisSpan.Length > 3)
                {
                    millisSpan = millisSpan[..3];
                }

                if (!int.TryParse(millisSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out millis))
                {
                    return false;
                }

                if (millisSpan.Length == 1)
                {
                    millis *= 100;
                }
                else if (millisSpan.Length == 2)
                {
                    millis *= 10;
                }
            }
            else
            {
                hms = span;
            }

            // Parse HH:MM:SS
            int firstColon = hms.IndexOf(':');
            if (firstColon < 0)
            {
                return false;
            }

            int secondColon = hms[(firstColon + 1)..].IndexOf(':');
            if (secondColon < 0)
            {
                return false;
            }

            secondColon += firstColon + 1;

            if (!int.TryParse(hms[..firstColon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours))
            {
                return false;
            }

            if (!int.TryParse(hms[(firstColon + 1)..secondColon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
            {
                return false;
            }

            if (!int.TryParse(hms[(secondColon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return false;
            }

            if (hours < 0 || minutes < 0 || minutes >= 60 || seconds < 0 || seconds >= 60 || millis < 0 || millis >= 1000)
            {
                return false;
            }

            var ts = new TimeSpan(0, hours, minutes, seconds, millis);
            ticks = ts.Ticks;
            return true;
        }

        private enum ParseState
        {
            AwaitingIndex,
            AwaitingTime,
            AwaitingText
        }
    }
}
