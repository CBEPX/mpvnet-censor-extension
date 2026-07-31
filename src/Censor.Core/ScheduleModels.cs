namespace Censor.Core;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

public sealed record ParseDiagnostic(
    DiagnosticSeverity Severity,
    int Line,
    int Column,
    string Message);

public sealed record CensorInterval(long StartMs, long EndMs, string? Note = null);

public sealed record ScheduleMetadata(
    int SchemaVersion = 1,
    string? Title = null,
    long? MediaDurationMs = null,
    long? LeadInMs = null,
    long? LeadOutMs = null,
    long? OffsetMs = null);

public sealed record ScheduleDocument(
    ScheduleMetadata Metadata,
    IReadOnlyList<CensorInterval> Intervals,
    IReadOnlyList<string> PreservedHeaderLines);

public sealed record ParseResult(
    ScheduleDocument? Document,
    IReadOnlyList<ParseDiagnostic> Diagnostics)
{
    public bool IsSuccess =>
        Document is not null &&
        Diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}
