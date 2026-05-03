namespace RelayBench.App.Infrastructure;

public sealed record SelectionOption(string Key, string DisplayName, string IconGlyph = "")
{
    public override string ToString() => DisplayName;
}
