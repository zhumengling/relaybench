using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting;

public interface ISensitiveDataRedactor
{
    string Redact(string? value);

    AdvancedRawExchange Redact(AdvancedRawExchange exchange);
}
