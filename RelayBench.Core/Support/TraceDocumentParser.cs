namespace RelayBench.Core.Support;

public static class TraceDocumentParser
{
    public static IReadOnlyDictionary<string, string> Parse(string rawText)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                values[key] = value;
            }
        }

        return values;
    }
}
