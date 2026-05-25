using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public interface IVsCodeClientConfigApplyAdapter
{
    Task<ClientAppApplyResult> ApplyAsync(
        ClientApplyEndpoint endpoint,
        IReadOnlyList<ClientApplyTargetSelection> targetSelections,
        CancellationToken cancellationToken = default);
}
