using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Censor.Core;

public enum ExtensionLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record ExtensionLogEvent(
    DateTimeOffset Timestamp,
    string Event,
    string Level,
    long MediaSessionId,
    long OperationRevision,
    IReadOnlyDictionary<string, object?> Fields);

public sealed class ExtensionLog : IDisposable
{
    public const string FilePattern = "censor-extension-*.log";
    public const int PathHashKeySize = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Channel<ExtensionLogEvent> _channel =
        Channel.CreateBounded<ExtensionLogEvent>(
            new BoundedChannelOptions(1_024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly ExtensionLogLevel _minimumLevel;
    private readonly Task _writer;
    private DateOnly _lastPrunedDate;
    private int _disposed;

    public ExtensionLog(
        string directory,
        int retentionDays,
        string minimumLevel = "info")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (retentionDays is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));

        _directory = Path.GetFullPath(directory);
        _retentionDays = retentionDays;
        _minimumLevel = ParseLevel(minimumLevel);
        _lastPrunedDate = DateOnly.FromDateTime(DateTime.UtcNow);
        try
        {
            Directory.CreateDirectory(_directory);
            DeleteExpired(retentionDays);
            _writer = Task.Run(WriteLoopAsync);
            IsEnabled = true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.Security.SecurityException or
                TaskSchedulerException)
        {
            _writer = Task.CompletedTask;
        }
    }

    public bool IsEnabled { get; }

    public void Write(
        string eventName,
        OperationTicket ticket,
        IReadOnlyDictionary<string, object?>? fields = null,
        ExtensionLogLevel level = ExtensionLogLevel.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0 || level < _minimumLevel)
            return;
        _channel.Writer.TryWrite(new(
            DateTimeOffset.UtcNow,
            eventName,
            level.ToString().ToLowerInvariant(),
            ticket.MediaSessionId,
            ticket.OperationRevision,
            fields ?? new Dictionary<string, object?>()));
    }

    public static byte[] LoadOrCreatePathHashKey(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == PathHashKeySize)
                    return existing;
            }

            var key = RandomNumberGenerator.GetBytes(PathHashKeySize);
            AtomicFile.Write(path, key);
            return key;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.Security.SecurityException)
        {
            // Privacy must not prevent playback when LocalAppData is read-only.
            return RandomNumberGenerator.GetBytes(PathHashKeySize);
        }
    }

    public static string ProtectPath(
        string? path,
        bool includePath,
        byte[] pathHashKey)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        if (includePath)
            return path;
        ArgumentNullException.ThrowIfNull(pathHashKey);
        if (pathHashKey.Length != PathHashKeySize)
            throw new ArgumentException(
                $"Ключ защиты пути должен содержать {PathHashKeySize} байта.",
                nameof(pathHashKey));

        var hash = HMACSHA256.HashData(pathHashKey, Encoding.UTF8.GetBytes(path));
        return $"hmac-sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static IReadOnlyList<string> FindFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, FilePattern)
            : [];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _channel.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Logging must never prevent player shutdown.
        }
    }

    private async Task WriteLoopAsync()
    {
        FileStream? stream = null;
        DateOnly? streamDate = null;
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    var currentDate = DateOnly.FromDateTime(DateTime.UtcNow);
                    if (currentDate != _lastPrunedDate)
                    {
                        DeleteExpired(_retentionDays);
                        _lastPrunedDate = currentDate;
                    }

                    var entryDate = DateOnly.FromDateTime(entry.Timestamp.UtcDateTime);
                    if (stream is null || streamDate != entryDate)
                    {
                        await DisposeQuietlyAsync(stream).ConfigureAwait(false);
                        var path = Path.Combine(
                            _directory,
                            "censor-extension-" +
                            entry.Timestamp.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                            ".log");
                        stream = new(
                            path,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete,
                            4_096,
                            FileOptions.Asynchronous);
                        streamDate = entryDate;
                    }

                    var bytes = Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    await DisposeQuietlyAsync(stream).ConfigureAwait(false);
                    stream = null;
                    streamDate = null;
                    // A single disk or serialization failure must not stop later log events.
                }
            }
        }
        finally
        {
            await DisposeQuietlyAsync(stream).ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeQuietlyAsync(FileStream? stream)
    {
        if (stream is null)
            return;
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Logging must never surface a close failure.
        }
    }

    private void DeleteExpired(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var path in Directory.EnumerateFiles(_directory, FilePattern))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static ExtensionLogLevel ParseLevel(string level) =>
        (string.IsNullOrWhiteSpace(level) ? null : level.ToLowerInvariant()) switch
        {
            "debug" => ExtensionLogLevel.Debug,
            "info" => ExtensionLogLevel.Info,
            "warning" => ExtensionLogLevel.Warning,
            "error" => ExtensionLogLevel.Error,
            _ => throw new ArgumentException("Неизвестный уровень журнала.", nameof(level)),
        };
}
