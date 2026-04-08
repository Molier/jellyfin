#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.MediaEncoding.Subtitles.Streaming;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.MediaEncoding.Subtitles.Tests
{
    public class SrtStreamingParserTests
    {
        private static readonly string BasicSrt =
            "1\n" +
            "00:00:01,000 --> 00:00:02,500\n" +
            "Hello World\n" +
            "\n" +
            "2\n" +
            "00:00:03,000 --> 00:00:04,000\n" +
            "Second cue\n" +
            "\n" +
            "3\n" +
            "00:00:05,000 --> 00:00:06,000\n" +
            "Third cue\n" +
            "\n";

        private static Stream ToStream(string content, bool addBom = false)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            if (addBom)
            {
                var bom = new byte[] { 0xEF, 0xBB, 0xBF };
                var withBom = new byte[bom.Length + bytes.Length];
                bom.CopyTo(withBom, 0);
                bytes.CopyTo(withBom, bom.Length);
                bytes = withBom;
            }

            return new MemoryStream(bytes);
        }

        private static async Task<List<SubtitleTrackEvent>> ParseAll(string srt, bool addBom = false)
        {
            var parser = new SrtStreamingParser();
            using var stream = ToStream(srt, addBom);
            var events = new List<SubtitleTrackEvent>();
            await foreach (var evt in parser.ParseAsync(stream, ".srt"))
            {
                events.Add(evt);
            }

            return events;
        }

        private static long TicksFromTimeSpan(int hours, int minutes, int seconds, int millis)
            => new TimeSpan(0, hours, minutes, seconds, millis).Ticks;

        [Fact]
        public async Task ParseAsync_Basic_EmitsThreeCuesWithCorrectFields()
        {
            var events = await ParseAll(BasicSrt);

            Assert.Equal(3, events.Count);

            Assert.Equal("1", events[0].Id);
            Assert.Equal(TicksFromTimeSpan(0, 0, 1, 0), events[0].StartPositionTicks);
            Assert.Equal(TicksFromTimeSpan(0, 0, 2, 500), events[0].EndPositionTicks);
            Assert.Equal("Hello World", events[0].Text);

            Assert.Equal("2", events[1].Id);
            Assert.Equal(TicksFromTimeSpan(0, 0, 3, 0), events[1].StartPositionTicks);
            Assert.Equal(TicksFromTimeSpan(0, 0, 4, 0), events[1].EndPositionTicks);
            Assert.Equal("Second cue", events[1].Text);

            Assert.Equal("3", events[2].Id);
            Assert.Equal(TicksFromTimeSpan(0, 0, 5, 0), events[2].StartPositionTicks);
            Assert.Equal(TicksFromTimeSpan(0, 0, 6, 0), events[2].EndPositionTicks);
            Assert.Equal("Third cue", events[2].Text);
        }

        [Fact]
        public async Task ParseAsync_WithBom_SameOutputAsNoBom()
        {
            var withBom = await ParseAll(BasicSrt, addBom: true);
            var withoutBom = await ParseAll(BasicSrt, addBom: false);

            Assert.Equal(withoutBom.Count, withBom.Count);
            for (int i = 0; i < withoutBom.Count; i++)
            {
                Assert.Equal(withoutBom[i].Id, withBom[i].Id);
                Assert.Equal(withoutBom[i].StartPositionTicks, withBom[i].StartPositionTicks);
                Assert.Equal(withoutBom[i].EndPositionTicks, withBom[i].EndPositionTicks);
                Assert.Equal(withoutBom[i].Text, withBom[i].Text);
            }
        }

        [Fact]
        public async Task ParseAsync_CrlfLineEndings_SameOutputAsLf()
        {
            var crlf = BasicSrt.Replace("\n", "\r\n", StringComparison.Ordinal);
            var lfEvents = await ParseAll(BasicSrt);
            var crlfEvents = await ParseAll(crlf);

            Assert.Equal(lfEvents.Count, crlfEvents.Count);
            for (int i = 0; i < lfEvents.Count; i++)
            {
                Assert.Equal(lfEvents[i].StartPositionTicks, crlfEvents[i].StartPositionTicks);
                Assert.Equal(lfEvents[i].EndPositionTicks, crlfEvents[i].EndPositionTicks);
                Assert.Equal(lfEvents[i].Text, crlfEvents[i].Text);
            }
        }

        [Fact]
        public async Task ParseAsync_MixedLfCrlf_ParsesCorrectly()
        {
            // Mix: first cue CRLF, second cue LF
            var mixed =
                "1\r\n" +
                "00:00:01,000 --> 00:00:02,000\r\n" +
                "Line one\r\n" +
                "\r\n" +
                "2\n" +
                "00:00:03,000 --> 00:00:04,000\n" +
                "Line two\n" +
                "\n";

            var events = await ParseAll(mixed);
            Assert.Equal(2, events.Count);
            Assert.Equal("Line one", events[0].Text);
            Assert.Equal("Line two", events[1].Text);
        }

        [Fact]
        public async Task ParseAsync_HtmlTags_PreservedVerbatim()
        {
            var srt =
                "1\n" +
                "00:00:01,000 --> 00:00:02,000\n" +
                "<i>italic</i> <font color=\"red\">red</font>\n" +
                "\n";

            var events = await ParseAll(srt);
            Assert.Single(events);
            Assert.Equal("<i>italic</i> <font color=\"red\">red</font>", events[0].Text);
        }

        [Fact]
        public async Task ParseAsync_NonAsciiUtf8_CorrectRoundTrip()
        {
            var srt =
                "1\n" +
                "00:00:01,000 --> 00:00:02,000\n" +
                "こんにちは\n" +
                "\n" +
                "2\n" +
                "00:00:03,000 --> 00:00:04,000\n" +
                "Привет\n" +
                "\n";

            var events = await ParseAll(srt);
            Assert.Equal(2, events.Count);
            Assert.Equal("こんにちは", events[0].Text);
            Assert.Equal("Привет", events[1].Text);
        }

        [Fact]
        public async Task ParseAsync_MalformedMissingIndex_RecoversParsesSubsequentCues()
        {
            // First block: starts with a time range line where the index should be.
            // The parser treats the timecode as the "index", then the text as a failed
            // TryParseTimeRange, causing reset. The valid cue after must still be emitted.
            var srt =
                "00:00:01,000 --> 00:00:02,000\n" +
                "Bad block text\n" +
                "\n" +
                "2\n" +
                "00:00:03,000 --> 00:00:04,000\n" +
                "Good cue\n" +
                "\n";

            var events = await ParseAll(srt);

            // Good cue must be present regardless of how the malformed block was handled.
            Assert.Contains(events, e => e.Text == "Good cue");
        }

        [Fact]
        public async Task ParseAsync_EmptyTextCue_EmittedWithEmptyText()
        {
            // A block with index + time but blank text immediately follows.
            // The parser emits the cue with empty Text (does not skip empty-text cues —
            // that is a writer concern, not a parser concern).
            var srt =
                "1\n" +
                "00:00:01,000 --> 00:00:02,000\n" +
                "\n";

            var events = await ParseAll(srt);
            Assert.Single(events);
            Assert.Equal(string.Empty, events[0].Text);
        }

        [Fact]
        public async Task ParseAsync_TwoDigitMillis_ParsedCorrectly()
        {
            // "00:00:01,50" -> millis=50, padded to 500ms (millis *= 10).
            // "00:00:02,00" -> millis=0, padded to 0ms.
            var srt =
                "1\n" +
                "00:00:01,50 --> 00:00:02,00\n" +
                "Two digit millis\n" +
                "\n";

            var events = await ParseAll(srt);
            Assert.Single(events);
            Assert.Equal(TicksFromTimeSpan(0, 0, 1, 500), events[0].StartPositionTicks);
            Assert.Equal(TicksFromTimeSpan(0, 0, 2, 0), events[0].EndPositionTicks);
        }

        [Fact]
        public async Task ParseAsync_LastCueNoTrailingBlank_StillEmitted()
        {
            // No blank line after last cue's text — EOF flush path.
            var srt =
                "1\n" +
                "00:00:01,000 --> 00:00:02,000\n" +
                "No trailing blank";

            var events = await ParseAll(srt);
            Assert.Single(events);
            Assert.Equal("No trailing blank", events[0].Text);
        }
    }
}
