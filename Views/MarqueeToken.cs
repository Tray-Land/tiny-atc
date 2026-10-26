using TinyAtc.Tray;

namespace TinyAtc.Views;

/// <summary>One token of the marquee message as the settings editor shows it: a field or literal text.</summary>
public sealed record MarqueeToken(string Value, string Label)
{
    public static MarqueeToken FromValue(string value) => new(value, MarqueeTemplate.DisplayText(value));

    public static IReadOnlyList<MarqueeToken> AllFields() =>
        (MarqueeToken[])[.. Enum.GetValues<MarqueeField>().Select(f => new MarqueeToken(MarqueeTemplate.Token(f), MarqueeTemplate.Label(f)))];
}
