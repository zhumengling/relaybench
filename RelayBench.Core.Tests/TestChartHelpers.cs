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

namespace RelayBench.Core.Tests;

internal static partial class TestSupport
{
    internal static IReadOnlyList<ProxySingleCapabilityChartItem> BuildSupplementalPhaseSingleCapabilityChartItems(
        ProxyDiagnosticsResult result)
    {
        var viewModel = new MainWindowViewModel();
        return BuildSupplementalPhaseSingleCapabilityChartItemsForViewModel(viewModel, result);
    }

    internal static IReadOnlyList<ProxySingleCapabilityChartItem> BuildDeepSupplementalPhaseSingleCapabilityChartItems(
        ProxyDiagnosticsResult result)
    {
        var viewModel = new MainWindowViewModel
        {
            ProxyEnableProtocolCompatibilityTest = false,
            ProxyEnableErrorTransparencyTest = false,
            ProxyEnableStreamingIntegrityTest = false,
            ProxyEnableMultiModalTest = false,
            ProxyEnableCacheMechanismTest = false,
            ProxyEnableInstructionFollowingTest = false,
            ProxyEnableDataExtractionTest = false,
            ProxyEnableStructuredOutputEdgeTest = true,
            ProxyEnableToolCallDeepTest = true,
            ProxyEnableReasonMathConsistencyTest = true,
            ProxyEnableCodeBlockDisciplineTest = true
        };
        var buildPlan = typeof(MainWindowViewModel).GetMethod(
            "BuildDeepProxySingleExecutionPlan",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var planField = typeof(MainWindowViewModel).GetField(
            "_currentProxySingleExecutionPlan",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (buildPlan is null || planField is null)
        {
            throw new InvalidOperationException("Deep execution plan members were not found.");
        }

        planField.SetValue(viewModel, buildPlan.Invoke(viewModel, []));
        return BuildSupplementalPhaseSingleCapabilityChartItemsForViewModel(viewModel, result);
    }

    internal static IReadOnlyList<ProxySingleCapabilityChartItem> BuildSupplementalPhaseSingleCapabilityChartItemsForViewModel(
        MainWindowViewModel viewModel,
        ProxyDiagnosticsResult result)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "BuildSupplementalPhaseSingleCapabilityChartItems",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildSupplementalPhaseSingleCapabilityChartItems was not found.");
        }

        return (IReadOnlyList<ProxySingleCapabilityChartItem>)method.Invoke(viewModel, [result])!;
    }

    internal static int ResolveSingleCapabilityChartItemRank(ProxySingleCapabilityChartItem item)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "ResolveSingleCapabilityChartItemRank",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("ResolveSingleCapabilityChartItemRank was not found.");
        }

        return (int)method.Invoke(null, [item])!;
    }
}
