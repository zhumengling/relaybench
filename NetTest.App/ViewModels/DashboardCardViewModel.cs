using NetTest.App.Infrastructure;

namespace NetTest.App.ViewModels;

public sealed class DashboardCardViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _status = string.Empty;
    private string _detail = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }
}
