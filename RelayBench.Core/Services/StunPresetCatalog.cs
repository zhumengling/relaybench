namespace RelayBench.Core.Services;

public static class StunPresetCatalog
{
    public static IReadOnlyList<StunPreset> Presets { get; } = new[]
    {
        new StunPreset("Google", "stun.l.google.com:19302"),
        new StunPreset("Cloudflare", "stun.cloudflare.com:3478"),
        new StunPreset("Twilio", "global.stun.twilio.com:3478"),
        new StunPreset("Mozilla", "stun.services.mozilla.com:3478"),
        new StunPreset("Stunprotocol.org", "stun.stunprotocol.org:3478"),
    };
}
