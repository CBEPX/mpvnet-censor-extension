using System.Text;
using Censor.Core;
using FsCheck;
using FsCheck.Xunit;

namespace Censor.Core.Tests;

public sealed class ScheduleTextTests
{
    [Theory]
    [InlineData("00:00:01.250", 1_250)]
    [InlineData("1:02:03.000", 3_723_000)]
    [InlineData("-00:00:01.250", -1_250)]
    [InlineData("100:00:00.000", 360_000_000)]
    public void ParsesTimestampsAcceptedByTheEditor(string text, long expected)
    {
        Assert.True(ScheduleText.TryParseDraftTimestamp(text, out var actual));
        Assert.Equal(expected, actual);
        if (text.Length != 12 || expected < 0 || expected >= 360_000_000)
            Assert.False(ScheduleText.TryParseTimestamp(text, out _));
    }

    [Fact]
    public void ParsesCanonicalFixture()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "example.censor.txt"));

        var result = ScheduleText.Parse(text);

        Assert.True(result.IsSuccess);
        Assert.Equal("Example Film", result.Document!.Metadata.Title);
        Assert.Equal(7_200_123, result.Document.Metadata.MediaDurationMs);
        Assert.Equal(3, result.Document.Intervals.Count);
        Assert.Equal(new CensorInterval(723_250, 727_900, "Сцена 1"), result.Document.Intervals[0]);
    }

    [Fact]
    public void SaveAndReloadKeepsRawTimestampsAndOffsetSeparate()
    {
        const string text = """
            # censor-timeline: 1
            # offset-ms: -250
            # future-key: keep me
            # a comment

            00:00:01.000 --> 00:00:02.000 | sample
            """;

        var parsed = ScheduleText.Parse(text);
        var serialized = ScheduleText.Serialize(parsed.Document!);
        var reloaded = ScheduleText.Parse(serialized);

        Assert.True(reloaded.IsSuccess);
        Assert.Equal(-250, reloaded.Document!.Metadata.OffsetMs);
        Assert.Equal(new CensorInterval(1_000, 2_000, "sample"), reloaded.Document.Intervals.Single());
        Assert.Equal(["# future-key: keep me", "# a comment"], reloaded.Document.PreservedHeaderLines);
        Assert.Contains(
            parsed.Diagnostics,
            diagnostic => diagnostic.Message.Contains("future-key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# offset-ms: -86400001")]
    [InlineData("# offset-ms: 86400001")]
    [InlineData("# censor-timeline: 2")]
    [InlineData("00:00:02.000 --> 00:00:01.000")]
    public void RejectsInvalidInput(string line)
    {
        var result = ScheduleText.Parse(line);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ParseRejectsInvalidSafetyLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScheduleText.Parse("", maxTextFileBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScheduleText.Parse("", maxIntervals: 0));
    }

    [Fact]
    public void RejectsDuplicateKnownMetadata()
    {
        const string text = """
            # offset-ms: 10
            # offset-ms: 20
            00:00:01.000 --> 00:00:02.000
            """;

        var result = ScheduleText.Parse(text);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("несколько раз", StringComparison.Ordinal));
    }

    [Fact]
    public void WarnsAboutWrongCaseInKnownMetadataWithoutApplyingIt()
    {
        const string text = """
            # media-duration-ms: 2000
            # Offset-ms: 500
            00:00:01.000 --> 00:00:02.000
            """;

        var result = ScheduleText.Parse(text);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Document!.Metadata.OffsetMs);
        Assert.Contains("# Offset-ms: 500", result.Document.PreservedHeaderLines);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Message.Contains("неверный регистр", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("offset-ms", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidMediaDurationIsNotAlsoReportedAsMissing()
    {
        var result = ScheduleText.Parse("# media-duration-ms: abc");

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("положительным целым числом", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("не задано", StringComparison.Ordinal));
    }

    [Fact]
    public void BareNoteSeparatorProducesNoNote()
    {
        var parsed = ScheduleText.Parse("00:00:01.000 --> 00:00:02.000 |   ");

        Assert.True(parsed.IsSuccess);
        Assert.Null(parsed.Document!.Intervals.Single().Note);
    }

    [Fact]
    public void PreservesCommentWithColonWithoutMetadataWarning()
    {
        const string text = """
            # media-duration-ms: 2000
            # см. https://example.com
            # note: handwritten comment
            00:00:01.000 --> 00:00:02.000
            """;

        var result = ScheduleText.Parse(text);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["# см. https://example.com", "# note: handwritten comment"],
            result.Document!.PreservedHeaderLines);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Неизвестное необязательное поле metadata", StringComparison.Ordinal));
    }

    [Fact]
    public void WritesUtf8WithoutBomAtomicallyAndKeepsBackup()
    {
        var directory = Directory.CreateTempSubdirectory("censor-core-tests-");
        try
        {
            var path = Path.Combine(directory.FullName, "sample.censor.txt");
            File.WriteAllText(path, "old");
            var document = new ScheduleDocument(
                new ScheduleMetadata(Title: "Фильм"),
                [new CensorInterval(1_000, 2_000)],
                []);

            var writtenBytes = AtomicScheduleWriter.Write(path, document);

            Assert.Equal("old", File.ReadAllText(path + ".bak"));
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(writtenBytes, bytes);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.True(ScheduleText.Parse(Encoding.UTF8.GetString(bytes)).IsSuccess);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AtomicWriteDeletesOnlyStaleTempsForTheSameTarget()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "sample.censor.txt");
        var stale = Path.Combine(directory.Path, ".sample.censor.txt.stale.tmp");
        var fresh = Path.Combine(directory.Path, ".sample.censor.txt.fresh.tmp");
        var unrelated = Path.Combine(directory.Path, ".other.censor.txt.stale.tmp");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(fresh, "fresh");
        File.WriteAllText(unrelated, "unrelated");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-2));

        AtomicFile.WriteUtf8Text(path, "saved");

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));
        Assert.Equal("saved", File.ReadAllText(path));
    }

    [Fact]
    public void SerializesCanonicalLfBytes()
    {
        var document = new ScheduleDocument(
            new ScheduleMetadata(Title: "Film", OffsetMs: -5),
            [new CensorInterval(1_000, 2_000, "note")],
            ["# future-key: value"]);

        var text = ScheduleText.Serialize(document);

        Assert.Equal(
            "# censor-timeline: 1\n# title: Film\n# offset-ms: -5\n# future-key: value\n\n00:00:01.000 --> 00:00:02.000 | note\n",
            text);
        Assert.DoesNotContain('\r', text);
    }

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("line one\rline two")]
    public void RejectsMultilineTitle(string title)
    {
        var document = new ScheduleDocument(new ScheduleMetadata(Title: title), [], []);

        Assert.Throws<ArgumentException>(() => ScheduleText.Serialize(document));
    }

    [Theory]
    [InlineData(" Film")]
    [InlineData("Film ")]
    public void RejectsTitleWhitespaceThatParserWouldDiscard(string title)
    {
        var document = new ScheduleDocument(new ScheduleMetadata(Title: title), [], []);

        Assert.Throws<ArgumentException>(() => ScheduleText.Serialize(document));
    }

    [Fact]
    public void RejectsMultilineIntervalNote()
    {
        var document = new ScheduleDocument(
            new(),
            [new(1_000, 2_000, "line one\nline two")],
            []);

        Assert.Throws<ArgumentException>(() => ScheduleText.Serialize(document));
    }

    [Fact]
    public void RejectsTimestampAtOneHundredHours()
    {
        var document = new ScheduleDocument(
            new ScheduleMetadata(),
            [new CensorInterval(359_999_999, 360_000_000)],
            []);

        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleText.Serialize(document));
    }

    [Fact]
    public void FailedReplaceKeepsOriginalAndCleansTemporaryFile()
    {
        var directory = Directory.CreateTempSubdirectory("censor-core-tests-");
        try
        {
            var path = Path.Combine(directory.FullName, "sample.censor.txt");
            File.WriteAllText(path, "old");
            Directory.CreateDirectory(path + ".bak");
            var document = new ScheduleDocument(
                new ScheduleMetadata(),
                [new CensorInterval(1_000, 2_000)],
                []);

            var exception = Record.Exception(() => AtomicScheduleWriter.Write(path, document));

            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.Equal("old", File.ReadAllText(path));
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, ".sample.censor.txt.*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Property(MaxTest = 100)]
    public bool SerializeThenParsePreservesGeneratedInterval(
        NonNegativeInt startValue,
        PositiveInt lengthValue,
        string? note)
    {
        var start = startValue.Get % 359_000_000;
        var length = (lengthValue.Get % 999_999) + 1L;
        var end = Math.Min(start + length, 359_999_999);
        var normalizedNote = string.IsNullOrWhiteSpace(note)
            ? null
            : note.Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        var document = new ScheduleDocument(
            new ScheduleMetadata(Title: "Фильм"),
            [new CensorInterval(start, end, normalizedNote)],
            ["# future-key: keep"]);

        var parsed = ScheduleText.Parse(ScheduleText.Serialize(document));

        return parsed.IsSuccess &&
            parsed.Document!.Intervals.Single() == new CensorInterval(start, end, normalizedNote) &&
            parsed.Document.PreservedHeaderLines.SequenceEqual(["# future-key: keep"]);
    }
}
