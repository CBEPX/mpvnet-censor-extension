using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Censor.Core;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);
var trustedProxies = ParseTrustedProxies(
    builder.Configuration["CENSORPLAYER_TRUSTED_PROXIES"]);
var rteBaseUri = ResolveRteBaseUri(
    builder.Configuration["CENSORPLAYER_RTE_BASE_URL"]);
// Search terms are part of request paths. Keep routine hosting logs disabled;
// scripts/smoke-timings-api.sh verifies this with a private-query sentinel.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
if (trustedProxies.Count > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var address in trustedProxies)
            options.KnownProxies.Add(address);
    });
}
builder.Services.AddMemoryCache(options => options.SizeLimit = 64L * 1024 * 1024);
builder.Services.AddHttpClient<OnlineTimingsClient>(client =>
{
    client.BaseAddress = rteBaseUri;
    client.Timeout = Timeout.InfiniteTimeSpan;
    var version = typeof(OnlineTimingsClient).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"CensorPlayer-Timings/{version}");
}).ConfigurePrimaryHttpMessageHandler(() =>
    new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<InflightRequests>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        var seconds = context.Lease.TryGetMetadata(
            MetadataName.RetryAfter,
            out var retryAfter)
                ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                : 60;
        context.HttpContext.Response.Headers["Retry-After"] =
            seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };
    // Address-less internal requests share one fail-closed partition instead of bypassing limits.
    options.AddPolicy("public", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new()
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});

var app = builder.Build();
if (trustedProxies.Count > 0)
    app.UseForwardedHeaders();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/v1/movies/search", SearchAsync)
    .RequireRateLimiting("public");
app.MapGet("/v1/movies/kp/{id:long:min(1)}/timings", GetTimingsAsync)
    .RequireRateLimiting("public");

app.Run();

static async Task<IResult> SearchAsync(
    string? q,
    OnlineTimingsClient client,
    IMemoryCache cache,
    InflightRequests inflight,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(q))
        return Problem("Укажите название фильма.", StatusCodes.Status400BadRequest);

    string normalized;
    try
    {
        normalized = OnlineTimingsClient.NormalizeSearchQuery(q);
    }
    catch (ArgumentException exception)
    {
        return Problem(exception.Message, StatusCodes.Status400BadRequest);
    }
    return await GetCachedAsync(
        "search:" + normalized,
        cache,
        inflight,
        async token => new MovieSearchResponse(
            1,
            "timings.rte",
            await client.SearchAsync(
                    OnlineSourceModes.DirectRteId,
                    null,
                    normalized,
                    token).ConfigureAwait(false)),
        cancellationToken).ConfigureAwait(false);
}

static async Task<IResult> GetTimingsAsync(
    long id,
    OnlineTimingsClient client,
    IMemoryCache cache,
    InflightRequests inflight,
    CancellationToken cancellationToken)
{
    var textId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var publicSourceUrl = $"https://timings.rte.net.ru/api/public/timings/{textId}";
    return await GetCachedAsync(
        "timings:" + textId,
        cache,
        inflight,
        async token => (await client.GetTimingsAsync(
                    OnlineSourceModes.DirectRteId,
                    null,
                    textId,
                    token).ConfigureAwait(false)) with
        {
            SourceUrl = publicSourceUrl,
        },
        cancellationToken).ConfigureAwait(false);
}

static async Task<IResult> GetCachedAsync<T>(
    string key,
    IMemoryCache cache,
    InflightRequests inflight,
    Func<CancellationToken, Task<T>> fetch,
    CancellationToken requestToken)
    where T : class
{
    if (cache.TryGetValue<CachedPayload>(key, out var cached) && cached is not null)
        return Results.Bytes(cached.Bytes, "application/json");
    if (cache.TryGetValue<UpstreamError>("error:" + key, out var cachedError) &&
        cachedError is not null)
    {
        return Problem(cachedError.Detail, cachedError.StatusCode);
    }

    try
    {
        var payload = await inflight.RunAsync(
            key,
            async () =>
            {
                try
                {
                    var fetched = await fetch(CancellationToken.None).ConfigureAwait(false);
                    var payload = new CachedPayload(
                        OnlineTimingsJson.SerializeToUtf8Bytes(fetched));
                    cache.Set(
                        key,
                        payload,
                        CacheOptions(
                            TimeSpan.FromMinutes(10),
                            Math.Max(512, payload.Bytes.LongLength)));
                    return payload;
                }
                catch (Exception exception)
                {
                    var error = DescribeError(exception, CancellationToken.None);
                    // UpstreamBusyException is thrown before this delegate starts,
                    // so the transient 503 overload response is never cached here.
                    if (error.StatusCode == StatusCodes.Status429TooManyRequests ||
                        error.StatusCode >= StatusCodes.Status500InternalServerError)
                    {
                        cache.Set(
                            "error:" + key,
                            error,
                            CacheOptions(TimeSpan.FromMinutes(1), 512));
                    }
                    throw;
                }
            },
            requestToken).ConfigureAwait(false);
        return Results.Bytes(payload.Bytes, "application/json");
    }
    catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception exception)
    {
        var error = DescribeError(exception, requestToken);
        return Problem(error.Detail, error.StatusCode);
    }
}

static UpstreamError DescribeError(Exception exception, CancellationToken requestToken) =>
    exception switch
    {
        UpstreamBusyException => new(
            StatusCodes.Status503ServiceUnavailable,
            "Сервис занят. Повторите запрос через несколько секунд."),
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            new(
                StatusCodes.Status429TooManyRequests,
                "timings.rte временно ограничил частоту запросов."),
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            new(StatusCodes.Status404NotFound, "Для этого фильма тайминги не найдены."),
        OperationCanceledException when !requestToken.IsCancellationRequested =>
            new(StatusCodes.Status504GatewayTimeout, "timings.rte не ответил вовремя."),
        TimeoutException =>
            new(StatusCodes.Status504GatewayTimeout, "timings.rte не ответил вовремя."),
        HttpRequestException or JsonException or InvalidDataException or RegexMatchTimeoutException =>
            new(
                StatusCodes.Status502BadGateway,
                "timings.rte временно недоступен или вернул некорректный ответ."),
        _ => new(StatusCodes.Status500InternalServerError, "Не удалось обработать запрос."),
    };

static MemoryCacheEntryOptions CacheOptions(TimeSpan duration, long size) => new()
{
    AbsoluteExpirationRelativeToNow = duration,
    Size = size,
};

static IResult Problem(string detail, int statusCode) =>
    new ApiProblem(detail, statusCode);

static IReadOnlyList<IPAddress> ParseTrustedProxies(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return [];

    var addresses = new List<IPAddress>();
    foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!IPAddress.TryParse(item, out var address))
            throw new InvalidOperationException(
                $"CENSORPLAYER_TRUSTED_PROXIES содержит некорректный IP-адрес: {item}");
        addresses.Add(address);
    }
    return addresses.AsReadOnly();
}

static Uri ResolveRteBaseUri(string? value)
{
    const string Default = "https://timings.rte.net.ru/api";
    var candidate = string.IsNullOrWhiteSpace(value) ? Default : value;
    if (!OnlineSourceModes.TryNormalizeBaseUrl(candidate, out var normalized))
    {
        throw new InvalidOperationException(
            "CENSORPLAYER_RTE_BASE_URL должен использовать HTTPS или локальный HTTP.");
    }
    return new(normalized + "/", UriKind.Absolute);
}

internal sealed record UpstreamError(int StatusCode, string Detail);

internal sealed record CachedPayload(byte[] Bytes);

internal sealed record ApiProblem(string Detail, int StatusCode) : IResult
{
    public Task ExecuteAsync(HttpContext context)
    {
        if (StatusCode == StatusCodes.Status429TooManyRequests)
            context.Response.Headers["Retry-After"] = "60";
        return Results.Problem(detail: Detail, statusCode: StatusCode).ExecuteAsync(context);
    }
}

internal sealed class InflightRequests : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _pending =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _upstreamSlots = new(4, 4);

    public async Task<T> RunAsync<T>(
        string key,
        Func<Task<T>> fetch,
        CancellationToken requestToken)
        where T : class
    {
        var created = new Lazy<Task<object>>(
            async () => (object)await RunWithSlotAsync(fetch).ConfigureAwait(false),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var pending = _pending.GetOrAdd(key, created);
        if (ReferenceEquals(pending, created))
            _ = RemoveWhenCompleteAsync(key, pending);
        return (T)await pending.Value.WaitAsync(requestToken).ConfigureAwait(false);
    }

    private async Task<T> RunWithSlotAsync<T>(Func<Task<T>> fetch)
    {
        if (!await _upstreamSlots.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            throw new UpstreamBusyException();
        try
        {
            return await fetch().ConfigureAwait(false);
        }
        finally
        {
            _upstreamSlots.Release();
        }
    }

    private async Task RemoveWhenCompleteAsync(string key, Lazy<Task<object>> pending)
    {
        try
        {
            await pending.Value.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            _pending.TryRemove(
                new KeyValuePair<string, Lazy<Task<object>>>(key, pending));
        }
    }

    public void Dispose() => _upstreamSlots.Dispose();
}

internal sealed class UpstreamBusyException : Exception;
