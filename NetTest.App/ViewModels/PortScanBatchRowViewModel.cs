using NetTest.App.Infrastructure;

namespace NetTest.App.ViewModels;

public sealed class PortScanBatchRowViewModel : ObservableObject
{
    private string _target = string.Empty;
    private string _status = "待运行";
    private int _openEndpointCount;
    private int _openPortCount;
    private string _resolvedAddresses = string.Empty;
    private string _summary = "等待开始";
    private string _error = string.Empty;
    private string _checkedAt = "--";

    public string Target
    {
        get => _target;
        set => SetProperty(ref _target, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public int OpenEndpointCount
    {
        get => _openEndpointCount;
        set => SetProperty(ref _openEndpointCount, value);
    }

    public int OpenPortCount
    {
        get => _openPortCount;
        set => SetProperty(ref _openPortCount, value);
    }

    public string ResolvedAddresses
    {
        get => _resolvedAddresses;
        set => SetProperty(ref _resolvedAddresses, value);
    }

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public string Error
    {
        get => _error;
        set => SetProperty(ref _error, value);
    }

    public string CheckedAt
    {
        get => _checkedAt;
        set => SetProperty(ref _checkedAt, value);
    }
}
