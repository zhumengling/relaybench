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
    internal static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void AssertFalse(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void AssertContains(string? value, string expected)
    {
        if (value?.Contains(expected, StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException($"Expected '{expected}' in '{value ?? "<null>"}'.");
        }
    }

    internal static void AssertEqual(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    internal static void AssertOrder(string value, string earlier, string later)
    {
        var earlierIndex = value.IndexOf(earlier, StringComparison.Ordinal);
        var laterIndex = value.IndexOf(later, StringComparison.Ordinal);
        if (earlierIndex < 0 || laterIndex < 0 || earlierIndex >= laterIndex)
        {
            throw new InvalidOperationException($"Expected '{earlier}' before '{later}' in dialog content.");
        }
    }
}
