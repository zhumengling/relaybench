using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using RelayBench.Services.Infrastructure;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class AboutDialog : ContentDialog
{
    public AboutDialog()
    {
        VersionText = ResolveVersionText();
        RuntimeText = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture}";
        ProxyText = App.TransparentProxyViewModel.IsRunning
            ? $"{App.TransparentProxyViewModel.LocalEndpoint} · {App.TransparentProxyViewModel.TokenSpeed}"
            : "透明代理未启动";
        DataDirectoryText = "系统菜单 > 目录 > 打开数据目录";
        ConfigDirectoryText = "系统菜单 > 目录 > 打开配置目录";
        DetailText = BuildDetailText();
        InitializeComponent();
    }

    public string VersionText { get; }

    public string RuntimeText { get; }

    public string ProxyText { get; }

    public string DataDirectoryText { get; }

    public string ConfigDirectoryText { get; }

    public string DetailText { get; }

    private static string ResolveVersionText()
    {
        var assembly = typeof(AboutDialog).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string BuildDetailText()
        => "用于本机代理链路评测、应用接入配置、网络复核和历史报告留存。\n" +
           $"系统环境：{RuntimeInformation.OSDescription}";
}
