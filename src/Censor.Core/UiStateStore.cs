using System.Text.Json;

namespace Censor.Core;

public sealed record UiState(int Left, int Top, int Width, int Height);

public static class UiStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static UiState? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (!File.Exists(path))
                return null;
            var state = JsonSerializer.Deserialize<UiState>(File.ReadAllText(path), JsonOptions);
            return state is { Width: >= 640, Height: >= 360 } ? state : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string path, UiState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));
    }
}
