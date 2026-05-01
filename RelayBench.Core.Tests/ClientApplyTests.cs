using RelayBench.Core.Services;
using RelayBench.Core.Models;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.App.Services;
using RelayBench.App.ViewModels;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class ClientApplyTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("client apply planner exposes codex targets as openai compatible for chat fallback", () =>
    {
        var planner = new ClientAppApplyPlanner();
        var targets = planner.BuildTargets(new ClientAppApplyPlanContext(
            "https://relay.example.com/v1",
            "sk-test",
            "plain-chat-model",
            ResponsesSupported: false,
            OpenAiCompatibleSupported: true,
            AnthropicSupported: false,
            InstalledClientNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        var codexTarget = targets.First(target => target.Id == "codex-cli");

        AssertTrue(codexTarget.IsProtocolSupported, "Codex should be selectable when chat fallback is the only supported protocol.");
        AssertTrue(codexTarget.IsDefaultSelected, "Codex chat fallback should be selected by default.");
        AssertTrue(codexTarget.Protocol == ClientApplyProtocolKind.OpenAiCompatible, $"Expected OpenAiCompatible, got {codexTarget.Protocol}.");
        });

        yield return new TestCase("client apply planner keeps anthropic only models off codex defaults", () =>
    {
        var planner = new ClientAppApplyPlanner();
        var targets = planner.BuildTargets(new ClientAppApplyPlanContext(
            "https://relay.example.com/anthropic",
            "sk-test",
            "claude-model",
            ResponsesSupported: false,
            OpenAiCompatibleSupported: false,
            AnthropicSupported: true,
            InstalledClientNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        var codexTarget = targets.First(target => target.Id == "codex-cli");
        var claudeTarget = targets.First(target => target.Id == "claude-cli");

        AssertFalse(codexTarget.IsProtocolSupported, "Codex should not be marked compatible for Anthropic-only models.");
        AssertFalse(codexTarget.IsDefaultSelected, "Anthropic-only models should not default-select Codex.");
        AssertTrue(claudeTarget.IsProtocolSupported, "Claude CLI should support Anthropic models.");
        AssertTrue(claudeTarget.IsDefaultSelected, "Claude CLI should be selected by default for Anthropic-capable models.");
        });

        yield return new TestCase("client apply planner honors installed client filtering", () =>
    {
        var planner = new ClientAppApplyPlanner();
        var targets = planner.BuildTargets(new ClientAppApplyPlanContext(
            "https://relay.example.com/v1",
            "sk-test",
            "gpt-test",
            ResponsesSupported: true,
            OpenAiCompatibleSupported: true,
            AnthropicSupported: false,
            InstalledClientNames: new HashSet<string>(["Claude CLI"], StringComparer.OrdinalIgnoreCase)));
        var codexTarget = targets.First(target => target.Id == "codex-cli");
        var claudeTarget = targets.First(target => target.Id == "claude-cli");

        AssertFalse(codexTarget.IsInstalled, "Codex CLI should not be treated as installed when the installed list excludes it.");
        AssertFalse(codexTarget.IsSelectable, "Uninstalled targets should not be selectable by default.");
        AssertTrue(claudeTarget.IsInstalled, "Claude CLI should be marked installed from the filtered list.");
        AssertFalse(claudeTarget.IsProtocolSupported, "Claude CLI should still be protocol-gated when Anthropic is unavailable.");
        });

        yield return new TestCase("codex wire api accepts chat fallback preference", () =>
    {
        var wireApi = CodexFamilyConfigApplyService.ResolveCodexWireApiPreference(
            "https://relay.example.com/v1",
            "plain-chat-model",
            "chat");

        AssertEqual(wireApi, "chat");
        });

        yield return new TestCase("codex apply service writes chat wire api for openai compatible fallback targets", () =>
    {
        var environment = new InMemoryClientApiConfigMutationEnvironment();
        var service = new ClientAppConfigApplyService(environment);
        var result = service.ApplyAsync(
            new ClientApplyEndpoint(
                "https://relay.example.com/v1",
                "sk-test",
                "plain-chat-model",
                "Relay Test",
                null,
                null),
            [new ClientApplyTargetSelection("codex-cli", ClientApplyProtocolKind.OpenAiCompatible)])
            .GetAwaiter()
            .GetResult();
        var configPath = Path.Combine(environment.UserProfilePath, ".codex", "config.toml");
        var config = environment.ReadFileText(configPath) ?? string.Empty;

        AssertTrue(result.TargetResults.Count == 1, $"Expected one Codex target result, got {result.TargetResults.Count}.");
        AssertTrue(result.TargetResults[0].Protocol == ClientApplyProtocolKind.OpenAiCompatible, $"Expected OpenAiCompatible, got {result.TargetResults[0].Protocol}.");
        AssertContains(config, "wire_api = \"chat\"");
        });

        yield return new TestCase("codex apply service honors forced chat selection over anthropic cache preference", () =>
    {
        var environment = new InMemoryClientApiConfigMutationEnvironment();
        var service = new ClientAppConfigApplyService(environment);
        var result = service.ApplyAsync(
            new ClientApplyEndpoint(
                "https://relay.example.com/v1",
                "sk-test",
                "forced-chat-model",
                "Relay Test",
                null,
                "anthropic"),
            [new ClientApplyTargetSelection("codex-cli", ClientApplyProtocolKind.OpenAiCompatible)])
            .GetAwaiter()
            .GetResult();
        var configPath = Path.Combine(environment.UserProfilePath, ".codex", "config.toml");
        var config = environment.ReadFileText(configPath) ?? string.Empty;

        AssertTrue(result.Succeeded, result.Error ?? result.Summary);
        AssertContains(config, "wire_api = \"chat\"");
        });

        yield return new TestCase("codex apply service honors forced responses selection over chat cache preference", () =>
    {
        var environment = new InMemoryClientApiConfigMutationEnvironment();
        var service = new ClientAppConfigApplyService(environment);
        var result = service.ApplyAsync(
            new ClientApplyEndpoint(
                "https://relay.example.com/v1",
                "sk-test",
                "forced-responses-model",
                "Relay Test",
                null,
                "chat"),
            [new ClientApplyTargetSelection("codex-cli", ClientApplyProtocolKind.Responses)])
            .GetAwaiter()
            .GetResult();
        var configPath = Path.Combine(environment.UserProfilePath, ".codex", "config.toml");
        var config = environment.ReadFileText(configPath) ?? string.Empty;

        AssertTrue(result.Succeeded, result.Error ?? result.Summary);
        AssertContains(config, "wire_api = \"responses\"");
        });

        yield return new TestCase("anthropic only apply writes claude settings without codex config", () =>
    {
        var environment = new InMemoryClientApiConfigMutationEnvironment();
        var service = new ClientAppConfigApplyService(environment);
        var result = service.ApplyAsync(
            new ClientApplyEndpoint(
                "https://relay.example.com/anthropic",
                "sk-test",
                "claude-model",
                "Relay Anthropic",
                null,
                "anthropic"),
            [new ClientApplyTargetSelection("claude-cli", ClientApplyProtocolKind.Anthropic)])
            .GetAwaiter()
            .GetResult();
        var codexConfigPath = Path.Combine(environment.UserProfilePath, ".codex", "config.toml");
        var claudeSettingsPath = Path.Combine(environment.UserProfilePath, ".claude", "settings.json");
        var claudeSettings = environment.ReadFileText(claudeSettingsPath) ?? string.Empty;

        AssertTrue(result.Succeeded, result.Error ?? result.Summary);
        AssertTrue(environment.ReadFileText(codexConfigPath) is null, "Anthropic-only apply must not write Codex config.");
        AssertContains(claudeSettings, "ANTHROPIC_BASE_URL");
        AssertContains(claudeSettings, "https://relay.example.com/anthropic");
        AssertContains(claudeSettings, "claude-model");
        });

        yield return new TestCase("mixed apply writes codex responses and claude anthropic configs separately", () =>
    {
        var environment = new InMemoryClientApiConfigMutationEnvironment();
        var service = new ClientAppConfigApplyService(environment);
        var result = service.ApplyAsync(
            new ClientApplyEndpoint(
                "https://relay.example.com/v1",
                "sk-test",
                "multi-protocol-model",
                "Relay Mixed",
                128000,
                "chat"),
            [
                new ClientApplyTargetSelection("codex-cli", ClientApplyProtocolKind.Responses),
                new ClientApplyTargetSelection("claude-cli", ClientApplyProtocolKind.Anthropic)
            ])
            .GetAwaiter()
            .GetResult();
        var codexConfig = environment.ReadFileText(Path.Combine(environment.UserProfilePath, ".codex", "config.toml")) ?? string.Empty;
        var claudeSettings = environment.ReadFileText(Path.Combine(environment.UserProfilePath, ".claude", "settings.json")) ?? string.Empty;

        AssertTrue(result.Succeeded, result.Error ?? result.Summary);
        AssertTrue(result.TargetResults.Count == 2, $"Expected two target results, got {result.TargetResults.Count}.");
        AssertContains(codexConfig, "wire_api = \"responses\"");
        AssertContains(codexConfig, "multi-protocol-model");
        AssertContains(claudeSettings, "ANTHROPIC_BASE_URL");
        AssertContains(claudeSettings, "multi-protocol-model");
        AssertTrue(
            result.TargetResults.Any(static target => target.TargetId == "codex-cli" && target.Protocol == ClientApplyProtocolKind.Responses),
            "Codex target should keep the selected Responses protocol.");
        AssertTrue(
            result.TargetResults.Any(static target => target.TargetId == "claude-cli" && target.Protocol == ClientApplyProtocolKind.Anthropic),
            "Claude target should keep the Anthropic protocol.");
        });
    }
}
