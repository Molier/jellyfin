#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.Subtitles.Streaming
{
    /// <summary>
    /// Streaming SRT writer. Emits cues progressively, renumbering so the
    /// output has a dense sequence regardless of how the source cues were
    /// numbered. Matches <see cref="SrtWriter"/> output format.
    /// </summary>
    public partial class SrtStreamingWriter : IStreamingSubtitleWriter
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

            int index = 0;
            await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                index++;
                await writer.WriteLineAsync(index.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

                var timeLine = string.Format(
                    CultureInfo.InvariantCulture,
                    @"{0:hh\:mm\:ss\,fff} --> {1:hh\:mm\:ss\,fff}",
                    TimeSpan.FromTicks(evt.StartPositionTicks),
                    TimeSpan.FromTicks(evt.EndPositionTicks));
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
