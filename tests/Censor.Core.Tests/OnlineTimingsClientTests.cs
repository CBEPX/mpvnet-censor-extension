using System.Net;
using System.Text;
using Censor.Core;

namespace Censor.Core.Tests;

public sealed class OnlineTimingsClientTests
{
    [Fact]
    public void SharedJsonKeepsUnicodeCompact()
    {
        var json = Encoding.UTF8.GetString(
            OnlineTimingsJson.SerializeToUtf8Bytes(new MovieSearchResult("301", "Матрица")));

        Assert.Contains("Матрица", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u041c", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectSearchUsesRtePathAndNormalizesOnlyNeededMetadata()
    {
        var handler = new RecordingHandler(_ => JsonFixture("rte-search-matrix.json"));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        var result = await client.SearchAsync(
            OnlineSourceModes.DirectRteId,
            null,
            " Матрица ",
            CancellationToken.None);

        Assert.Equal(
            "https://timings.rte.net.ru/api/search/%D0%9C%D0%B0%D1%82%D1%80%D0%B8%D1%86%D0%B0",
            Assert.Single(handler.Requests).AbsoluteUri);
        Assert.Equal(
            new MovieSearchResult("301", "Матрица (1999)", "1999", 8_160_000),
            Assert.Single(result));
    }

    [Fact]
    public async Task DirectSearchUsesAnExplicitHttpClientBaseAddress()
    {
        var handler = new RecordingHandler(_ => Json("[]"));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new("http://127.0.0.1:18081/api/"),
        };
        var client = new OnlineTimingsClient(http);

        await client.SearchAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "probe",
            CancellationToken.None);

        Assert.Equal(
            "http://127.0.0.1:18081/api/search/probe",
            Assert.Single(handler.Requests).AbsoluteUri);
    }

    [Fact]
    public async Task RejectsAResponseThatWasRedirectedByTheHttpHandler()
    {
        var handler = new RecordingHandler(request =>
        {
            request.RequestUri = new("http://example.test/redirected");
            var response = Json("[]");
            response.RequestMessage = request;
            return response;
        });
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "Матрица",
            CancellationToken.None));
    }

    [Fact]
    public async Task DirectSearchKeepsTheFirstResultsWithinTheLimit()
    {
        var items = string.Join(",", Enumerable.Range(1, 21).Select(index =>
            $$"""{"id":{{index}},"title":"Movie {{index}}"}"""));
        var handler = new RecordingHandler(_ => Json($$"""[{{items}}]"""));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        var result = await client.SearchAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "Matrix",
            CancellationToken.None);

        Assert.Equal(OnlineTimingsClient.MaxSearchResults, result.Count);
        Assert.Equal("20", result[^1].Id);
    }

    [Fact]
    public async Task DirectSearchAcceptsFilmLengthAsBareMinutes()
    {
        var handler = new RecordingHandler(_ => Json(
            """[{"id":301,"title":"Матрица","raw_data":{"film_length":"136"}}]"""));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        var result = await client.SearchAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "Матрица",
            CancellationToken.None);

        Assert.Equal(8_160_000, Assert.Single(result).SourceDurationMs);
    }

    [Theory]
    [InlineData(8_160_000, 8_219_999, false)]
    [InlineData(8_160_000, 8_220_001, true)]
    public void DurationComparisonRespectsTheSourcesMinutePrecision(
        long sourceDurationMs,
        long mediaDurationMs,
        bool expected)
    {
        var movie = new MovieSearchResult("301", "Матрица", SourceDurationMs: sourceDurationMs);

        Assert.Equal(expected, movie.DurationDiffersFrom(mediaDurationMs));
    }

    [Fact]
    public async Task DirectSearchSkipsNullEntriesFromRte()
    {
        var handler = new RecordingHandler(_ => Json(
            """[null,{"id":301,"title":"Матрица","year":"1999"}]"""));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        var result = await client.SearchAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "Матрица",
            CancellationToken.None);

        Assert.Equal("301", Assert.Single(result).Id);
    }

    [Fact]
    public async Task DirectTimingsPreservesPointAndIntervalWithSourceMetadata()
    {
        var handler = new RecordingHandler(_ => JsonFixture("rte-timings-301.json"));
        using var http = new HttpClient(handler);
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var client = new OnlineTimingsClient(http, new FixedTimeProvider(now));

        var package = await client.GetTimingsAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "301",
            CancellationToken.None);

        Assert.Equal("https://timings.rte.net.ru/api/public/timings/301", Assert.Single(handler.Requests).AbsoluteUri);
        Assert.Equal(1, package.SchemaVersion);
        Assert.Equal("timings.rte", package.Source);
        Assert.Equal("301", package.SourceMovieId);
        Assert.Equal(now, package.FetchedAtUtc);
        Assert.Collection(
            package.Entries,
            point =>
            {
                Assert.Equal(TimingCandidateKind.Point, point.Kind);
                Assert.Null(point.EndMs);
                Assert.Equal(1, point.VoteScore);
            },
            interval => Assert.Equal(TimingCandidateKind.Interval, interval.Kind));
    }

    [Fact]
    public async Task DirectTimingsKeepsAnOversizedEntryAsTruncatedUnparsedText()
    {
        var oversized = new string('x', TimingTextParser.MaxTextLength + 1);
        var handler = new RecordingHandler(_ => Json($$"""
            {
              "kp_id": "301",
              "timings": [
                {"id": 1, "timing_text": "{{oversized}}", "status": "approved", "vote_score": 0}
              ]
            }
            """));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        var package = await client.GetTimingsAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "301",
            CancellationToken.None);

        var candidate = Assert.Single(package.Entries);
        Assert.Equal(TimingCandidateKind.Unparsed, candidate.Kind);
        Assert.Equal(TimingTextParser.MaxTextLength, candidate.RawText.Length);
        Assert.Contains("слишком длинная", candidate.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectTimingsKeepsTheFirstParsedFragmentsWithinTheLimit()
    {
        var entries = string.Join(",", Enumerable.Range(1, 251).Select(index =>
            $$"""{"id":{{index}},"timing_text":"00:01-00:02; 00:03-00:04"}"""));
        var handler = new RecordingHandler(_ => Json(
            $$"""{"kp_id":"301","timings":[{{entries}}]}"""));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        var package = await client.GetTimingsAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "301",
            CancellationToken.None);

        Assert.Equal(OnlineTimingsClient.MaxTimingCandidates, package.Entries.Count);
        Assert.Equal("250", package.Entries[^1].SourceEntryId);
        Assert.Equal(1, package.Entries[^1].FragmentIndex);
    }

    [Fact]
    public async Task DirectTimingsRejectsMoreCandidatesThanTheLimit()
    {
        var items = string.Join(",", Enumerable.Range(1, 501).Select(index =>
            $$"""{"id":{{index}},"timing_text":"00:01","status":"approved","vote_score":0}"""));
        var handler = new RecordingHandler(_ => Json(
            $$"""{"kp_id":"301","timings":[{{items}}]}"""));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTimingsAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "301",
            CancellationToken.None));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"kp_id\":null,\"timings\":[]}")]
    public async Task DirectTimingsRejectsAMissingMovieIdentity(string json)
    {
        var handler = new RecordingHandler(_ => Json(json));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTimingsAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "301",
            CancellationToken.None));
    }

    [Fact]
    public async Task DirectTimingsRejectsANullEntry()
    {
        var handler = new RecordingHandler(_ => Json(
            """{"kp_id":"301","timings":[null]}"""));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTimingsAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "301",
            CancellationToken.None));
    }

    [Fact]
    public async Task AggregatorUsesStableApiAndNeverFallsBackToRte()
    {
        var handler = new RecordingHandler(_ => new(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream failed", Encoding.UTF8, "text/plain"),
        });
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test/root",
            "Matrix",
            CancellationToken.None));

        Assert.Equal(
            "https://aggregator.example.test/root/v1/movies/search?q=Matrix",
            Assert.Single(handler.Requests).AbsoluteUri);
    }

    [Fact]
    public async Task AggregatorAcceptsTheSharedSearchAndTimingContract()
    {
        var searchJson = Encoding.UTF8.GetString(
            OnlineTimingsJson.SerializeToUtf8Bytes(new MovieSearchResponse(
                1,
                "timings.rte",
                [new("301", "Матрица (1999)", "1999", 8_160_000)])));
        var timingJson = Encoding.UTF8.GetString(
            OnlineTimingsJson.SerializeToUtf8Bytes(new TimingPackage(
                1,
                "timings.rte",
                "301",
                "https://aggregator.example.test/v1/movies/kp/301/timings",
                new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
                [new("1", 0, TimingCandidateKind.CleanClaim, null, null, "Чисто")])));
        var handler = new RecordingHandler(request => Json(
            request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? searchJson
                : timingJson));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        var movies = await client.SearchAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test",
            "Матрица",
            CancellationToken.None);
        var package = await client.GetTimingsAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test",
            "301",
            CancellationToken.None);

        Assert.Equal("301", Assert.Single(movies).Id);
        Assert.Equal(TimingCandidateKind.CleanClaim, Assert.Single(package.Entries).Kind);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"source\":\"custom\",\"movies\":null}")]
    [InlineData("{\"schemaVersion\":1,\"source\":\"custom\",\"movies\":[{\"id\":\"\",\"title\":\"\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"source\":\"custom\",\"movies\":[{\"id\":\"+301\",\"title\":\"Movie\"}]}")]
    public async Task AggregatorRejectsMalformedSearchPayload(string json)
    {
        var handler = new RecordingHandler(_ => Json(json));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test",
            "Matrix",
            CancellationToken.None));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    public async Task AggregatorRejectsNullTimingEntries(string entries)
    {
        var handler = new RecordingHandler(_ => Json($$"""
            {
              "schemaVersion": 1,
              "source": "custom",
              "sourceMovieId": "301",
              "sourceUrl": "https://aggregator.example.test/v1/movies/kp/301/timings",
              "fetchedAtUtc": "2026-08-19T12:00:00Z",
              "entries": {{entries}}
            }
            """));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTimingsAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test",
            "301",
            CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"sourceEntryId\":\"1\",\"fragmentIndex\":0,\"kind\":\"interval\",\"startMs\":null,\"endMs\":2000,\"rawText\":\"bad\"}")]
    [InlineData("{\"sourceEntryId\":\"1\",\"fragmentIndex\":0,\"kind\":\"interval\",\"startMs\":3000,\"endMs\":2000,\"rawText\":\"bad\"}")]
    [InlineData("{\"sourceEntryId\":\"1\",\"fragmentIndex\":0,\"kind\":\"point\",\"startMs\":1000,\"endMs\":2000,\"rawText\":\"bad\"}")]
    [InlineData("{\"sourceEntryId\":\"1] fake\",\"fragmentIndex\":0,\"kind\":\"interval\",\"startMs\":1000,\"endMs\":2000,\"rawText\":\"bad\"}")]
    [InlineData("{\"sourceEntryId\":\"0\",\"fragmentIndex\":0,\"kind\":\"interval\",\"startMs\":1000,\"endMs\":2000,\"rawText\":\"bad\"}")]
    [InlineData("{\"sourceEntryId\":\"007\",\"fragmentIndex\":0,\"kind\":\"interval\",\"startMs\":1000,\"endMs\":2000,\"rawText\":\"bad\"}")]
    [InlineData("{\"sourceEntryId\":\"1\",\"fragmentIndex\":0,\"kind\":\"interval\",\"startMs\":1000,\"endMs\":2000,\"rawText\":\"bad\\u202e\"}")]
    public async Task AggregatorRejectsCandidatesWithAnInvalidShape(string entry)
    {
        var handler = new RecordingHandler(_ => Json(AggregatorPackage(
            "301",
            "https://aggregator.example.test/v1/movies/kp/301/timings",
            "custom",
            "[" + entry + "]")));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTimingsAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test",
            "301",
            CancellationToken.None));
    }

    [Fact]
    public async Task AggregatorRejectsDuplicateFragmentIdentity()
    {
        const string DuplicateEntries = """
            [
              {"sourceEntryId":"1","fragmentIndex":0,"kind":"interval","startMs":1000,"endMs":2000,"rawText":"first"},
              {"sourceEntryId":"1","fragmentIndex":0,"kind":"interval","startMs":3000,"endMs":4000,"rawText":"second"}
            ]
            """;
        var handler = new RecordingHandler(_ => Json(AggregatorPackage(
            "301",
            "https://aggregator.example.test/source",
            "custom",
            DuplicateEntries)));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTimingsAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test",
            "301",
            CancellationToken.None));
    }

    [Theory]
    [InlineData("302", "https://aggregator.example.test/v1/movies/kp/301/timings")]
    [InlineData("301", "file:///tmp/poisoned-timings.json")]
    public async Task AggregatorRejectsPoisonedTimingIdentity(
        string sourceMovieId,
        string sourceUrl)
    {
        var handler = new RecordingHandler(_ => Json(AggregatorPackage(
            sourceMovieId,
            sourceUrl,
            "custom",
            "[]")));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTimingsAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test",
            "301",
            CancellationToken.None));
    }

    [Fact]
    public async Task AggregatorRejectsAnOversizedSourceUrl()
    {
        var sourceUrl = "https://aggregator.example.test/" + new string('x', 2_048);
        var handler = new RecordingHandler(_ => Json(AggregatorPackage(
            "301",
            sourceUrl,
            "custom",
            "[]")));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTimingsAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test",
            "301",
            CancellationToken.None));
    }

    [Fact]
    public async Task AggregatorCapsSourceNameAndCandidateCount()
    {
        var oversizedSource = new string('s', 101);
        var oneEntry = """
            {"sourceEntryId":"1","fragmentIndex":0,"kind":"unparsed","startMs":null,"endMs":null,"rawText":"text"}
            """;
        var tooManyEntries = "[" + string.Join(",", Enumerable.Repeat(oneEntry, 501)) + "]";

        foreach (var json in new[]
        {
            AggregatorPackage("301", "https://aggregator.example.test/source", oversizedSource, "[]"),
            AggregatorPackage("301", "https://aggregator.example.test/source", "custom", tooManyEntries),
        })
        {
            var handler = new RecordingHandler(_ => Json(json));
            using var http = new HttpClient(handler);
            var client = new OnlineTimingsClient(http);

            await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTimingsAsync(
                OnlineSourceModes.AggregatorId,
                "https://aggregator.example.test",
                "301",
                CancellationToken.None));
        }
    }

    [Fact]
    public async Task AggregatorRejectsASourceNameThatCanBreakNoteAttribution()
    {
        var handler = new RecordingHandler(_ => Json(AggregatorPackage(
            "301",
            "https://aggregator.example.test/source",
            "source] #fake",
            "[]")));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTimingsAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test",
            "301",
            CancellationToken.None));
    }

    [Fact]
    public async Task AggregatorRejectsMoreSearchResultsThanTheContractAllows()
    {
        var movies = Enumerable.Range(1, OnlineTimingsClient.MaxSearchResults + 1)
            .Select(index => new MovieSearchResult(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"Movie {index}"))
            .ToArray();
        var handler = new RecordingHandler(_ => Json(
            Encoding.UTF8.GetString(OnlineTimingsJson.SerializeToUtf8Bytes(new MovieSearchResponse(
                1,
                "custom",
                movies)))));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(
            OnlineSourceModes.AggregatorId,
            "https://aggregator.example.test",
            "Matrix",
            CancellationToken.None));
    }

    [Theory]
    [InlineData("301", "301")]
    [InlineData("https://www.kinopoisk.ru/film/301/", "301")]
    [InlineData("https://kinopoisk.ru/series/123456/", "123456")]
    public void KinopoiskIdAcceptsIdOrOfficialUrl(string input, string expected)
    {
        Assert.Equal(expected, KinopoiskIdParser.Parse(input));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("+301")]
    [InlineData("https://example.test/film/301")]
    [InlineData("https://kinopoisk.ru/name/301")]
    public void KinopoiskIdRejectsAmbiguousInput(string input)
    {
        var exception = Assert.Throws<ArgumentException>(() => KinopoiskIdParser.Parse(input));

        Assert.DoesNotContain("Parameter", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://user@example.test/api")]
    [InlineData("https://example.test/api?token=secret")]
    [InlineData("https://example.test/api#fragment")]
    [InlineData("http://example.test/api")]
    public void AggregatorBaseUrlRejectsCredentialsAndAmbiguousSuffixes(string input)
    {
        Assert.False(OnlineSourceModes.TryNormalizeBaseUrl(input, out _));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/api/", "http://127.0.0.1:8080/api")]
    [InlineData("http://[::1]:8080/api", "http://[::1]:8080/api")]
    public void AggregatorBaseUrlAllowsLoopbackHttp(string input, string expected)
    {
        Assert.True(OnlineSourceModes.TryNormalizeBaseUrl(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public async Task ResponseLargerThanLimitIsRejectedBeforeJsonParsing()
    {
        var oversized = new string('x', OnlineTimingsClient.MaxResponseBytes + 1);
        var content = new UnknownLengthContent(oversized);
        Assert.Null(content.Headers.ContentLength);
        var handler = new RecordingHandler(_ => new(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(
            OnlineSourceModes.DirectRteId,
            null,
            "Matrix",
            CancellationToken.None));

        Assert.Contains("1 МиБ", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" . . ")]
    public async Task SearchRejectsDotOnlyPathSegments(string query)
    {
        var handler = new RecordingHandler(_ => Json("[]"));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(
            OnlineSourceModes.DirectRteId,
            null,
            query,
            CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PublicMethodsRejectAMissingSourceMode()
    {
        var handler = new RecordingHandler(_ => Json("[]"));
        using var http = new HttpClient(handler);
        var client = new OnlineTimingsClient(http);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SearchAsync(
            null!,
            null,
            "Matrix",
            CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetTimingsAsync(
            null!,
            null,
            "301",
            CancellationToken.None));
    }

    private static HttpResponseMessage JsonFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(File.ReadAllText(path), Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private static string AggregatorPackage(
        string sourceMovieId,
        string sourceUrl,
        string source,
        string entries) => $$"""
        {
          "schemaVersion": 1,
          "source": "{{source}}",
          "sourceMovieId": "{{sourceMovieId}}",
          "sourceUrl": "{{sourceUrl}}",
          "fetchedAtUtc": "2026-08-19T12:00:00Z",
          "entries": {{entries}}
        }
        """;

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class UnknownLengthContent(string value) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
