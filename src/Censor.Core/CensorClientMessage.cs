namespace Censor.Core;

public enum CensorClientCommandKind
{
    Open,
    Pick,
    Load,
    Reload,
    Apply,
    Disable,
    Authoring,
}

public readonly record struct CensorClientCommand(
    CensorClientCommandKind Kind,
    string? Argument = null);

public static class CensorClientMessage
{
    public static CensorClientCommand? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
            return null;

        var name = args[0].ToLowerInvariant();
        return name switch
        {
            "censor-open" => new(CensorClientCommandKind.Open),
            "censor-pick" => new(CensorClientCommandKind.Pick),
            "censor-load" => new(
                CensorClientCommandKind.Load,
                args.Count > 1 ? args[1] : null),
            "censor-reload" => new(CensorClientCommandKind.Reload),
            "censor-apply" => new(CensorClientCommandKind.Apply),
            "censor-disable" => new(CensorClientCommandKind.Disable),
            "censor-mark-start" or
            "censor-mark-end" or
            "censor-set-start" or
            "censor-set-end" or
            "censor-previous" or
            "censor-next" or
            "censor-save" => new(
                CensorClientCommandKind.Authoring,
                name["censor-".Length..]),
            _ => null,
        };
    }
}
