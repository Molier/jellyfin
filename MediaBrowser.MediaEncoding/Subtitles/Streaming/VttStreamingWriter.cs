#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.Subtitles.Streaming
{
    /// <summary>
    /// Streaming WebVTT writer. Emits the header once then writes each cue
    /// progressively, flushing the destination after every cue.
    ///
    /// Semantics match the existing batch <see cref="VttWriter"/> output so
    /// clients see byte-identical (or near-identical) responses regardless of
    /// which path is taken.
    /// </summary>
    public partial class VttStreamingWriter : IStreamingSubtitleWriter
    {
        [GeneratedRegex(@"\\n", RegexOptions.IgnoreCase)]
        private static partial Regex NewlineEscapeRegex();

        /// <inheritdoc />
        public async Task WriteAsync(
            IAsyncEnumerable<SubtitleTrackEvent> events,
            Stream destination,
            CancellationToken cancellationToken)
        {
            await using var writer = new StreamWriter(destination, Encoding.UTF8, bufferSize: 1024, leaveOpen: true);
            writer.NewLine = "\n";

            await writer.WriteLineAsync("WEBVTT").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("Region: id:subtitle width:80% lines:3 regionanchor:50%,100% viewportanchor:50%,90%").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

            await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var startTime = TimeSpan.FromTicks(evt.StartPositionTicks);
                var endTime = TimeSpan.FromTicks(evt.EndPositionTicks);

                if (endTime.TotalMilliseconds <= startTime.TotalMilliseconds)
                {
                    endTime = startTime.Add(TimeSpan.FromMilliseconds(1));
                }

                var timeLine = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    @"{0:hh\:mm\:ss\.fff} --> {1:hh\:mm\:ss\.fff} region:subtitle line:90%",
                    startTime,
                    endTime);

                await writer.WriteLineAsync(timeLine).ConfigureAwait(false);

                var text = evt.Text ?? string.Empty;
                text = NewlineEscapeRegex().Replace(text, " ");
                await writer.WriteLineAsync(text).ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
