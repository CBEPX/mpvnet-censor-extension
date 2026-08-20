using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Censor.Core;

public sealed record MovieSearchResult(
    string Id,
    string Title,
    string? Year = null,
    long? SourceDurationMs = null)
{
    public const long SourceDurationPrecisionMs = 60_000;

    public bool DurationDiffersFrom(long mediaDurationMs) =>
        SourceDurationMs is { } sourceDurationMs &&
        Math.Abs(sourceDurationMs - mediaDurationMs) > SourceDurationPrecisionMs;
}

public sealed record MovieSearchResponse(
    int SchemaVersion,
    string Source,
    IReadOnlyList<MovieSearchResult> Movies);

public sealed record TimingPackage(
    int SchemaVersion,
    string Source,
    string SourceMovieId,
    string SourceUrl,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<TimingCandidate> Entries);

public static class OnlineTimingsJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static byte[] SerializeToUtf8Bytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);
}

public sealed class OnlineTimingsClient
{
    public const int MaxQueryLength = 200;
    public const int MaxResponseBytes = 1_048_576;
    public const int MaxSearchResults = 20;
    public const int MaxTimingCandidates = 500;

    private const int MaxSourceLength = 100;
    private const int MaxTitleLength = 512;
    private const int MaxMetadataLength = 100;

    private static readonly Uri RteBaseUri = new("https://timings.rte.net.ru/api/");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = OnlineTimingsJson.Options;

    private readonly HttpClient _http;
    private readonly Uri _rteBaseUri;
    private readonly TimeProvider _timeProvider;

    public OnlineTimingsClient(HttpClient http, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        if (http.BaseAddress is not null)
        {
            if (!OnlineSourceModes.TryNormalizeBaseUrl(
                    http.BaseAddress.AbsoluteUri,
                    out var normalizedBaseUrl))
            {
                throw new ArgumentException(
                    "Базовый адрес прямого источника должен использовать HTTPS или локальный HTTP.",
                    nameof(http));
            }
            _rteBaseUri = new(normalizedBaseUrl + "/", UriKind.Absolute);
        }
        else
        {
            _rteBaseUri = RteBaseUri;
        }
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<MovieSearchResult>> SearchAsync(
        string sourceMode,
        string? aggregatorBaseUrl,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMode);
        var normalizedQuery = NormalizeSearchQuery(query);
        var uri = BuildUri(
            sourceMode,
            aggregatorBaseUrl,
            directPath: "search/" + Uri.EscapeDataString(normalizedQuery),
            aggregatorPath: "v1/movies/search?q=" + Uri.EscapeDataString(normalizedQuery));
        var bytes = await GetBytesAsync(uri, cancellationToken).ConfigureAwait(false);

        if (sourceMode.Equals(OnlineSourceModes.DirectRteId, StringComparison.OrdinalIgnoreCase))
        {
            var response = JsonSerializer.Deserialize<RteSearchItem?[]>(bytes, JsonOptions) ?? [];
            return response
                .OfType<RteSearchItem>()
                .Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.Title))
                .Take(MaxSearchResults)
                .Select(item => new MovieSearchResult(
                    item.Id.ToString(CultureInfo.InvariantCulture),
                    TruncateSingleLine(item.Title!, MaxTitleLength),
                    TruncateNullableSingleLine(item.Year, MaxMetadataLength),
                    ParseFilmLength(item.RawData?.FilmLength)))
                .ToArray();
        }

        var aggregator = JsonSerializer.Deserialize<MovieSearchResponse>(bytes, JsonOptions) ??
            throw new InvalidDataException("Агрегатор вернул пустой ответ поиска.");
        if (aggregator.SchemaVersion != 1 ||
            !IsSafeSourceName(aggregator.Source) ||
            aggregator.Movies is null ||
            aggregator.Movies.Count > MaxSearchResults)
        {
            throw new InvalidDataException("Агрегатор вернул несовместимый ответ поиска.");
        }

        // The self-hosted schema is our strict contract; unlike the loose RTE payload,
        // one malformed entry invalidates the response instead of being silently hidden.
        var movies = new List<MovieSearchResult>();
        foreach (var movie in aggregator.Movies)
        {
            if (movie is null ||
                !long.TryParse(
                    movie.Id,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var numericId) || numericId <= 0 ||
                string.IsNullOrWhiteSpace(movie.Title) ||
                movie.Title.Length > MaxTitleLength ||
                movie.Year is { Length: > MaxMetadataLength } ||
                movie.SourceDurationMs is <= 0 or > ScheduleText.MaxTimestampMs)
            {
                throw new InvalidDataException("Агрегатор вернул неполную запись фильма.");
            }
            movies.Add(movie with
            {
                Id = numericId.ToString(CultureInfo.InvariantCulture),
                Title = NormalizeSingleLine(movie.Title),
                Year = NormalizeNullableSingleLine(movie.Year),
            });
        }
        return movies.AsReadOnly();
    }

    public async Task<TimingPackage> GetTimingsAsync(
        string sourceMode,
        string? aggregatorBaseUrl,
        string kinopoiskIdOrUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMode);
        var id = KinopoiskIdParser.Parse(kinopoiskIdOrUrl);
        var uri = BuildUri(
            sourceMode,
            aggregatorBaseUrl,
            directPath: "public/timings/" + id,
            aggregatorPath: "v1/movies/kp/" + id + "/timings");
        var bytes = await GetBytesAsync(uri, cancellationToken).ConfigureAwait(false);

        if (!sourceMode.Equals(OnlineSourceModes.DirectRteId, StringComparison.OrdinalIgnoreCase))
        {
            var package = JsonSerializer.Deserialize<TimingPackage>(bytes, JsonOptions) ??
                throw new InvalidDataException("Агрегатор вернул пустой ответ таймингов.");
            if (package.SchemaVersion != 1 ||
                package.SourceMovieId != id ||
                !IsSafeSourceName(package.Source) ||
                !IsSafeSourceUrl(package.SourceUrl) ||
                package.Entries is null ||
                package.Entries.Count > MaxTimingCandidates ||
                package.Entries.Any(entry => !IsValidCandidate(entry)) ||
                package.Entries
                    .Select(entry => (entry!.SourceEntryId, entry.FragmentIndex))
                    .Distinct()
                    .Count() != package.Entries.Count)
            {
                throw new InvalidDataException("Агрегатор вернул несовместимый ответ таймингов.");
            }
            return package with
            {
                Source = NormalizeSingleLine(package.Source),
                Entries = Array.AsReadOnly(package.Entries.Select(candidate => candidate! with
                {
                    RawText = NormalizeSingleLine(candidate.RawText),
                    Note = NormalizeNullableSingleLine(candidate.Note),
                    Status = NormalizeNullableSingleLine(candidate.Status),
                }).ToArray()),
            };
        }

        var response = JsonSerializer.Deserialize<RteTimingsResponse>(bytes, JsonOptions) ??
            throw new InvalidDataException("timings.rte вернул пустой ответ таймингов.");
        if (!string.Equals(response.KinopoiskId, id, StringComparison.Ordinal))
            throw new InvalidDataException("timings.rte вернул тайминги другого фильма.");

        var sourceEntries = response.Timings ?? [];
        if (sourceEntries.Length > MaxTimingCandidates)
            throw new InvalidDataException("timings.rte вернул слишком много записей таймингов.");

        var entries = new List<TimingCandidate>();
        foreach (var item in sourceEntries)
        {
            if (item is null || item.Id <= 0 || item.TimingText is null)
                throw new InvalidDataException("timings.rte вернул неполную запись тайминга.");
            var entryId = item.Id.ToString(CultureInfo.InvariantCulture);
            var status = TruncateNullableSingleLine(item.Status, MaxMetadataLength);
            if (item.TimingText.Length > TimingTextParser.MaxTextLength)
            {
                entries.Add(new(
                    entryId,
                    0,
                    TimingCandidateKind.Unparsed,
                    null,
                    null,
                    TruncateSingleLine(item.TimingText, TimingTextParser.MaxTextLength),
                    "Запись слишком длинная и показана без разбора.",
                    status,
                    item.VoteScore));
                continue;
            }

            var parsed = TimingTextParser.Parse(
                entryId,
                item.TimingText,
                status,
                item.VoteScore);
            var remaining = MaxTimingCandidates - entries.Count;
            entries.AddRange(parsed.Take(remaining));
            if (parsed.Count >= remaining)
                break;
        }

        return new(
            1,
            "timings.rte",
            id,
            uri.AbsoluteUri,
            _timeProvider.GetUtcNow(),
            entries.AsReadOnly());
    }

    private async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } responseUri &&
                !uri.Equals(responseUri))
                throw new InvalidDataException("Онлайн-источник перенаправил запрос на другой адрес.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw ResponseTooLarge();

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16_384];
            while (true)
            {
                var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > MaxResponseBytes)
                    throw ResponseTooLarge();
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested &&
            timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Онлайн-источник не ответил за 10 секунд.", exception);
        }
    }

    private Uri BuildUri(
        string sourceMode,
        string? aggregatorBaseUrl,
        string directPath,
        string aggregatorPath)
    {
        if (sourceMode.Equals(OnlineSourceModes.DirectRteId, StringComparison.OrdinalIgnoreCase))
            return new(_rteBaseUri, directPath);
        if (!sourceMode.Equals(OnlineSourceModes.AggregatorId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Неизвестный онлайн-источник.");
        if (!OnlineSourceModes.TryNormalizeBaseUrl(aggregatorBaseUrl, out var normalized))
            throw new ArgumentException(
                "Укажите корректный адрес в разделе «Настройки → Адрес агрегатора».");
        return new(normalized + "/" + aggregatorPath, UriKind.Absolute);
    }

    public static string NormalizeSearchQuery(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var normalized = string.Join(
            ' ',
            query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // Relative Uri resolves "." and ".." as path navigation before BuildUri sees them.
        if (normalized.All(character => character is '.' or ' '))
            throw new ArgumentException("Введите название фильма, а не только точки.");
        if (normalized.Length > MaxQueryLength)
            throw new ArgumentException($"Поисковый запрос длиннее {MaxQueryLength} символов.");
        return normalized;
    }

    private static long? ParseFilmLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var parts = value.Split(':');
        long totalMinutes;
        if (parts.Length == 1)
        {
            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out totalMinutes))
                return null;
        }
        else if (parts.Length == 2 &&
                 int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) &&
                 int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) &&
                 hours is >= 0 and <= 99 &&
                 minutes is >= 0 and <= 59)
        {
            totalMinutes = (long)hours * 60 + minutes;
        }
        else
        {
            return null;
        }
        return totalMinutes is > 0 and <= ScheduleText.MaxTimestampMs / 60_000
            ? totalMinutes * 60_000
            : null;
    }

    private static string NormalizeSingleLine(string value) =>
        TimingTextParser.RemoveBidiControls(value)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    private static string? NormalizeNullableSingleLine(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeSingleLine(value);

    private static string TruncateSingleLine(string value, int maxLength) =>
        Truncate(NormalizeSingleLine(value), maxLength);

    private static string? TruncateNullableSingleLine(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : TruncateSingleLine(value, maxLength);

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;
        var length = maxLength;
        if (char.IsHighSurrogate(value[length - 1]) &&
            char.IsLowSurrogate(value[length]))
        {
            length--;
        }
        return value[..length];
    }

    private static bool IsSafeSourceUrl(string? value) =>
        value is { Length: <= OnlineSourceModes.MaxBaseUrlLength } &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback);

    private static bool IsSafeSourceName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxSourceLength &&
        value.All(character =>
            char.IsLetterOrDigit(character) || character is ' ' or '.' or '-' or '_');

    private static bool IsValidCandidate(TimingCandidate? candidate) =>
        candidate is not null &&
        TimingDraftImporter.IsSafeSourceEntryId(candidate.SourceEntryId) &&
        candidate.FragmentIndex >= 0 &&
        Enum.IsDefined(candidate.Kind) &&
        candidate.RawText is not null &&
        candidate.RawText.Length <= TimingTextParser.MaxTextLength &&
        candidate.RawText == TimingTextParser.RemoveBidiControls(candidate.RawText) &&
        candidate.Note is not { Length: > TimingTextParser.MaxTextLength } &&
        (candidate.Note is null || candidate.Note == TimingTextParser.RemoveBidiControls(candidate.Note)) &&
        candidate.Status is not { Length: > MaxMetadataLength } &&
        (candidate.Status is null || candidate.Status == TimingTextParser.RemoveBidiControls(candidate.Status)) &&
        candidate.StartMs is null or >= 0 and <= ScheduleText.MaxTimestampMs &&
        candidate.EndMs is null or >= 0 and <= ScheduleText.MaxTimestampMs &&
        HasValidShape(candidate);

    private static bool HasValidShape(TimingCandidate candidate) =>
        candidate.Kind switch
        {
            TimingCandidateKind.Interval =>
                candidate.StartMs is { } startMs &&
                candidate.EndMs is { } endMs &&
                endMs > startMs,
            TimingCandidateKind.Point =>
                candidate.StartMs is not null && candidate.EndMs is null,
            TimingCandidateKind.Unparsed or TimingCandidateKind.CleanClaim =>
                candidate.StartMs is null && candidate.EndMs is null,
            _ => false,
        };

    private static InvalidDataException ResponseTooLarge() =>
        new("Ответ онлайн-источника превышает лимит 1 МиБ.");

    private sealed record RteSearchItem(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("year")] string? Year,
        [property: JsonPropertyName("raw_data")] RteRawData? RawData);

    private sealed record RteRawData(
        [property: JsonPropertyName("film_length")] string? FilmLength);

    private sealed record RteTimingsResponse(
        [property: JsonPropertyName("kp_id")] string? KinopoiskId,
        [property: JsonPropertyName("timings")] RteTimingEntry?[]? Timings);

    private sealed record RteTimingEntry(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("timing_text")] string? TimingText,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("vote_score")] int? VoteScore);
}

public static class KinopoiskIdParser
{
    public static string Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (long.TryParse(
                trimmed,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var numeric) && numeric > 0)
            return numeric.ToString(CultureInfo.InvariantCulture);

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            !(uri.Host.Equals("kinopoisk.ru", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("www.kinopoisk.ru", StringComparison.OrdinalIgnoreCase)))
        {
            throw Invalid();
        }
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not ["film" or "series", var id] ||
            !long.TryParse(
                id,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out numeric) ||
            numeric <= 0)
        {
            throw Invalid();
        }
        return numeric.ToString(CultureInfo.InvariantCulture);
    }

    private static ArgumentException Invalid() =>
        new("Укажите положительный ID Кинопоиска или полную ссылку вида https://www.kinopoisk.ru/film/ID/.");
}
