using Censor.Core;

namespace Censor.Core.Tests;

public sealed class CensorClientMessageTests
{
    [Theory]
    [InlineData("censor-open", CensorClientCommandKind.Open)]
    [InlineData("CENSOR-PICK", CensorClientCommandKind.Pick)]
    [InlineData("censor-reload", CensorClientCommandKind.Reload)]
    [InlineData("censor-apply", CensorClientCommandKind.Apply)]
    [InlineData("censor-disable", CensorClientCommandKind.Disable)]
    public void ParsesRuntimeCommandsCaseInsensitively(
        string input,
        CensorClientCommandKind expected)
    {
        var command = CensorClientMessage.Parse([input]);

        Assert.Equal(expected, command?.Kind);
        Assert.Null(command?.Argument);
    }

    [Fact]
    public void PreservesLoadPathAndHandlesMissingArgument()
    {
        const string Path = @"D:\Video\film.censor.txt";

        Assert.Equal(
            new(CensorClientCommandKind.Load, Path),
            CensorClientMessage.Parse(["censor-load", Path]));
        Assert.Equal(
            new(CensorClientCommandKind.Load),
            CensorClientMessage.Parse(["censor-load"]));
    }

    [Theory]
    [InlineData("mark-start")]
    [InlineData("mark-end")]
    [InlineData("set-start")]
    [InlineData("set-end")]
    [InlineData("previous")]
    [InlineData("next")]
    [InlineData("save")]
    public void ParsesEveryAuthoringCommand(string name)
    {
        Assert.Equal(
            new(CensorClientCommandKind.Authoring, name),
            CensorClientMessage.Parse(["CENSOR-" + name.ToUpperInvariant()]));
    }

    [Theory]
    [MemberData(nameof(UnsupportedMessages))]
    public void IgnoresEmptyAndUnknownMessages(string[] input)
    {
        Assert.Null(CensorClientMessage.Parse(input));
    }

    public static TheoryData<string[]> UnsupportedMessages => new()
    {
        { Array.Empty<string>() },
        { [""] },
        { ["censor-unknown"] },
        { ["foreign-open"] },
    };
}
