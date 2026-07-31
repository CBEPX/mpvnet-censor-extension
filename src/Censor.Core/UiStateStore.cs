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
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ??
            throw new ArgumentException(
                "Путь к файлу состояния интерфейса должен включать каталог.",
                nameof(path));
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".UiState.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Preserve the original UI-state write failure.
            }
            throw;
        }
    }
}
