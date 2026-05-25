using System.Net.Http;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public interface IClientApiProbeTransport
{
    Task<ClientApiProbeResponse> ProbeAsync(
        Uri url,
        HttpMethod method,
        string provider,
        CancellationToken cancellationToken);
}
