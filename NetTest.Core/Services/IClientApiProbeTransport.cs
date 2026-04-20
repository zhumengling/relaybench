using System.Net.Http;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public interface IClientApiProbeTransport
{
    Task<ClientApiProbeResponse> ProbeAsync(
        Uri url,
        HttpMethod method,
        string provider,
        CancellationToken cancellationToken);
}
