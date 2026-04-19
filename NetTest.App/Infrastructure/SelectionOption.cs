namespace NetTest.App.Infrastructure;

public sealed record SelectionOption(string Key, string DisplayName)
{
    public override string ToString() => DisplayName;
}
