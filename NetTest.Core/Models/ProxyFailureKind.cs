namespace NetTest.Core.Models;

public enum ProxyFailureKind
{
    ConfigurationInvalid,
    DnsFailure,
    TcpConnectFailure,
    TlsHandshakeFailure,
    Timeout,
    AuthRejected,
    RateLimited,
    ModelNotFound,
    UnsupportedEndpoint,
    Http4xx,
    Http5xx,
    ProtocolMismatch,
    StreamNoFirstToken,
    StreamNoDone,
    StreamBroken,
    SemanticMismatch,
    Unknown
}
