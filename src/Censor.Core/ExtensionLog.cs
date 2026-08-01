using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Censor.Core;

public sealed record ExtensionLogEvent(
    DateTimeOffset Timestamp,
    string Event,
    long MediaSessionId,
    long OperationRevision,
    IReadOnlyDictionary<string, object?> Fields);

public sealed class ExtensionLog : IDisposable
{
    public const string FilePattern = "censor-extension-*.log";

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
    private readonly Task _writer;
    private DateOnly _lastPrunedDate;
    private int _disposed;

    public ExtensionLog(string directory, int retentionDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (retentionDays is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));

        _directory = Path.GetFullPath(directory);
        _retentionDays = retentionDays;
        _lastPrunedDate = DateOnly.FromDateTime(DateTime.UtcNow);
        try
        {
            Directory.CreateDirectory(_directory);
            DeleteExpired(retentionDays);
            IsEnabled = true;
            _writer = Task.Run(WriteLoopAsync);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.Security.SecurityException)
        {
            _writer = Task.CompletedTask;
        }
    }

    public bool IsEnabled { get; }

    public void Write(
        string eventName,
        OperationTicket ticket,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0)
            return;
        _channel.Writer.TryWrite(new(
            DateTimeOffset.UtcNow,
            eventName,
            ticket.MediaSessionId,
            ticket.OperationRevision,
            fields ?? new Dictionary<string, object?>()));
    }

    public static string ProtectPath(string? path, bool includePath)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        if (includePath)
            return path;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
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
                var path = Path.Combine(
                    _directory,
                    "censor-extension-" +
                    entry.Timestamp.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                    ".log");
                var bytes = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
                await using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete,
                    4_096,
                    FileOptions.Asynchronous);
                await stream.WriteAsync(bytes).ConfigureAwait(false);
            }
            catch
            {
                // A single disk or serialization failure must not stop later log events.
            }
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
}
