using System.Text;
using Censor.Core;
using MpvNet;
using static MpvNet.Native.LibMpv;

namespace Censor.MpvNet.Extension;

public sealed class Extension : IExtension, IDisposable
{
    private const long DurationToleranceMs = 2_000;
    private const string LogModule = "CensorExtension";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _filterGate = new(1, 1);
    private CancellationTokenSource _sessionCancellation = new();
    private IReadOnlyList<string> _activeLabels = [];
    private int _disposed;
    private long _sessionId;

    public Extension()
    {
        Player = Global.Player.CreateNewPlayer("censor");
        Player.StartFile += OnStartFile;
        Player.FileLoaded += OnFileLoaded;
        Player.EndFile += OnEndFile;
    }

    public MpvClient Player { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Player.StartFile -= OnStartFile;
        Player.FileLoaded -= OnFileLoaded;
        Player.EndFile -= OnEndFile;

        _sessionCancellation.Cancel();
        _filterGate.Wait();
        try
        {
            RemoveFilters(_activeLabels);
            _activeLabels = [];
        }
        finally
        {
            _filterGate.Release();
            _filterGate.Dispose();
            _sessionCancellation.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void OnStartFile() => RunSafely(BeginSession);

    private void OnFileLoaded() => RunSafely(LoadCurrentSidecar);

    private void OnEndFile(mpv_end_file_reason _) => RunSafely(BeginSession);

    private void BeginSession()
    {
        CancellationTokenSource previous;
        lock (_stateLock)
        {
            _sessionId++;
            previous = _sessionCancellation;
            _sessionCancellation = new();
        }

        previous.Cancel();
        previous.Dispose();

        _filterGate.Wait();
        try
        {
            RemoveFilters(_activeLabels);
            _activeLabels = [];
        }
        finally
        {
            _filterGate.Release();
        }
    }

    private void LoadCurrentSidecar()
    {
        long sessionId;
        CancellationToken token;
        lock (_stateLock)
        {
            sessionId = _sessionId;
            token = _sessionCancellation.Token;
        }

        var mediaPath = Player.GetPropertyString("path");
        _ = LoadCurrentSidecarAsync(sessionId, mediaPath, token);
    }

    private async Task LoadCurrentSidecarAsync(
        long sessionId,
        string mediaPath,
        CancellationToken token)
    {
        try
        {
            if (!File.Exists(mediaPath))
                return;

            var sidecars = SidecarLocator.Find(mediaPath);
            if (sidecars.Count == 0)
                return;
            if (sidecars.Count > 1)
            {
                Show("Multiple sidecars found; select one manually.");
                return;
            }

            var sidecarPath = sidecars[0];
            var text = await File.ReadAllTextAsync(sidecarPath, StrictUtf8, token).ConfigureAwait(false);
            var parsed = Parse(sidecarPath, text);
            if (!parsed.IsSuccess)
            {
                Show($"Schedule error: {parsed.Diagnostics.First(item => item.Severity == DiagnosticSeverity.Error).Message}");
                return;
            }

            var document = parsed.Document!;
            if (!MatchesCurrentDuration(document.Metadata.MediaDurationMs))
            {
                Show("Sidecar duration differs from the current media; automatic apply blocked.");
                return;
            }

            var defaults = new NormalizationOptions();
            var normalized = ScheduleNormalizer.Normalize(
                document.Intervals,
                new(
                    document.Metadata.LeadInMs ?? defaults.LeadInMs,
                    document.Metadata.LeadOutMs ?? defaults.LeadOutMs,
                    document.Metadata.OffsetMs ?? defaults.OffsetMs,
                    defaults.MergeGapMs));
            var plan = FilterCompiler.Compile(normalized, new());
            if (plan.Chunks.Count == 0)
            {
                Show("Schedule contains no active intervals.");
                return;
            }

            await ApplyAsync(sessionId, plan, token).ConfigureAwait(false);
            if (IsCurrent(sessionId))
                Show($"Applied {plan.IntervalCount} intervals from {Path.GetFileName(sidecarPath)}.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            if (IsCurrent(sessionId))
                Show("Failed to load or apply the sidecar. See the terminal log.");
        }
    }

    private async Task ApplyAsync(
        long sessionId,
        FilterPlan plan,
        CancellationToken token)
    {
        await _filterGate.WaitAsync(token).ConfigureAwait(false);
        var labels = plan.Chunks.Select(chunk => chunk.Label).ToArray();
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(sessionId))
                return;

            foreach (var chunk in plan.Chunks)
            {
                Player.CommandV("vf", "add", chunk.Filter);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(sessionId))
                    throw new OperationCanceledException(token);
            }

            var filters = Player.GetPropertyString("vf");
            if (labels.Any(label => !filters.Contains(label, StringComparison.Ordinal)))
                throw new InvalidOperationException("mpv did not report every generated filter after apply.");

            _activeLabels = labels;
        }
        catch
        {
            RemoveFilters(labels);
            throw;
        }
        finally
        {
            _filterGate.Release();
        }
    }

    private bool MatchesCurrentDuration(long? expectedMs)
    {
        if (expectedMs is null)
            return true;

        var durationSeconds = Player.GetPropertyDouble("duration", handleError: false);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            return false;

        var actualMs = checked((long)Math.Round(durationSeconds * 1_000, MidpointRounding.AwayFromZero));
        return Math.Abs(actualMs - expectedMs.Value) <= DurationToleranceMs;
    }

    private bool IsCurrent(long sessionId)
    {
        lock (_stateLock)
            return _sessionId == sessionId;
    }

    private static ParseResult Parse(string path, string text)
    {
        if (path.EndsWith(".censor.srt", StringComparison.OrdinalIgnoreCase))
            return SubtitleScheduleText.Import(text, SubtitleFormat.Srt);
        if (path.EndsWith(".censor.vtt", StringComparison.OrdinalIgnoreCase))
            return SubtitleScheduleText.Import(text, SubtitleFormat.WebVtt);
        return ScheduleText.Parse(text);
    }

    private void RemoveFilters(IEnumerable<string> labels)
    {
        foreach (var label in labels)
            Player.CommandV("vf", "remove", label);
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            Show("Censor extension error. See the terminal log.");
        }
    }

    private void Show(string message) =>
        Player.CommandV("show-text", $"Censor: {message}", "5000");
}
