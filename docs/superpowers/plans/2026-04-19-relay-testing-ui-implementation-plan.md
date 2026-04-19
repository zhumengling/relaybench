# Relay Testing Workbench UI 重构实现计划

> **面向 AI 代理的工作者：** 必须子技能：使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。  
> **目标：** 把当前 `H:\nettest\NetTest.App` 的多 `TabItem` 技术模块界面，重构为左侧导航的“中转站测试工作台”；应用启动后直接进入“单站测试”，一级导航固定为“单站测试 / 批量对比 / 网络复核 / 历史报告”，并将深度测试并入单站与批量流程。  
> **架构：** 保留单一 `MainWindowViewModel` 作为诊断状态、命令和结果汇总中心；新增导航 / 工作流 partial 与页面级 `UserControl`；将 `H:\nettest\NetTest.App\MainWindow.xaml` 缩减为“左侧导航壳层 + 顶部状态条 + 页面宿主 + 现有弹层宿主”。现有诊断服务、结果文本、报告导出与图表渲染尽量复用。  
> **技术栈：** WPF / XAML、`.NET 10`（`net10.0-windows`）、现有 `ObservableObject` + partial ViewModel、现有 `.artifacts\smoke` 控制台烟雾项目。

---

## 1. 范围与约束

- 本轮只做 **UI / 信息架构 / 交互流程重组**，不新增新的网络探测能力，也不新增新的中转站诊断能力。
- 启动默认页固定为 **单站测试**，**不恢复上次导航页**；这样可以保证每次打开应用都进入主任务入口。
- “深度测试”不再作为一级页出现；它只存在于：
  - `单站测试` 的深度模式；
  - `批量对比` 中快速对比之后的候选深测阶段。
- 批量页必须满足：**先入口组 → 再快速对比 → 默认排行榜视图 → 图表只读 → 列表手动勾选 → 对勾选项做深度测试**。
- 当前工作区 `H:\nettest` 不是 Git 仓库，因此计划中的 `commit` 步骤仅保留建议 message，不执行真实 Git 提交。
- 为降低风险，`DashboardCards`、`RunQuickSuiteCommand`、现有各诊断命令和现有结果汇总字段本轮继续保留，**只取消旧总览/旧 Tab UI 暴露，不做内部大清洗**。

## 2. 当前基线（制定计划时已验证）

以下命令当前已可通过，后续实施过程中都应继续保持通过：

```powershell
dotnet build "H:\nettest\NetTestSuite.slnx" -c Debug -v minimal
dotnet run --project "H:\nettest\.artifacts\smoke\MainWindowViewModelCoreSmoke\MainWindowViewModelCoreSmoke.csproj" -c Debug
dotnet run --project "H:\nettest\.artifacts\smoke\ProxyBatchCommandsSmoke\ProxyBatchCommandsSmoke.csproj" -c Debug
dotnet run --project "H:\nettest\.artifacts\smoke\ProxyBatchEditorSmoke\ProxyBatchEditorSmoke.csproj" -c Debug
dotnet run --project "H:\nettest\.artifacts\smoke\ReportingSmoke\ReportingSmoke.csproj" -c Debug
```

当前基线输出要点：

- `MainWindowViewModelCoreSmoke`：`dashboard=8`、`runCommand=ok`
- `ProxyBatchCommandsSmoke`：`masked=sk-123...cdef`、`command=ok`
- `ProxyBatchEditorSmoke`：`groupName=api.example.com`、`mode=SharedKeyGroup`
- `ReportingSmoke`：`sections=16`、`textArtifacts=11`

## 3. 文件结构与职责

### 3.1 新建文件

| 文件 | 职责 |
|---|---|
| `H:\nettest\NetTest.App\Resources\WorkbenchTheme.xaml` | 承载从 `MainWindow.xaml` 抽出的全局样式、色板、紧凑密度尺寸、导航样式、卡片样式。 |
| `H:\nettest\NetTest.App\Views\Pages\SingleStationPage.xaml` | “单站测试”页面，三段式：站点配置 / 模式参数与执行 / 结果与统一输出。 |
| `H:\nettest\NetTest.App\Views\Pages\SingleStationPage.xaml.cs` | `UserControl` 代码后置，仅负责 `InitializeComponent()`。 |
| `H:\nettest\NetTest.App\Views\Pages\BatchComparisonPage.xaml` | “批量对比”页面，入口组管理、快速对比、排行榜图表、排行榜列表勾选、候选深测。 |
| `H:\nettest\NetTest.App\Views\Pages\BatchComparisonPage.xaml.cs` | `UserControl` 代码后置，仅负责 `InitializeComponent()`。 |
| `H:\nettest\NetTest.App\Views\Pages\NetworkReviewPage.xaml` | “网络复核”页面，异常类型优先、模块推荐在后。 |
| `H:\nettest\NetTest.App\Views\Pages\NetworkReviewPage.xaml.cs` | `UserControl` 代码后置，仅负责 `InitializeComponent()`。 |
| `H:\nettest\NetTest.App\Views\Pages\HistoryReportsPage.xaml` | “历史报告”页面，承载历史摘要与导出归档。 |
| `H:\nettest\NetTest.App\Views\Pages\HistoryReportsPage.xaml.cs` | `UserControl` 代码后置，仅负责 `InitializeComponent()`。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Navigation.cs` | 一级导航状态、页面选项、页面可见性布尔值、页面标题摘要。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.SingleStationWorkflow.cs` | 单站模式状态（快速 / 稳定性 / 深度）、单站页面派生文案、单站统一执行命令。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.BatchWorkflow.cs` | 批量流程状态、排行榜列表集合、候选勾选状态、批量深测执行入口。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.NetworkReview.cs` | 异常类型选项、推荐模块摘要、网络复核页面派生说明。 |
| `H:\nettest\NetTest.App\ViewModels\ProxyBatchRankingRowViewModel.cs` | 排行榜列表项视图模型，负责勾选、排名、快速对比指标、深测状态。 |

### 3.2 修改文件

| 文件 | 当前关注点 | 本轮职责 |
|---|---|---|
| `H:\nettest\NetTest.App\App.xaml` | 当前资源区为空 | 合并 `WorkbenchTheme.xaml` 资源字典。 |
| `H:\nettest\NetTest.App\MainWindow.xaml` | 资源定义在 `1-380`，主 `TabControl` 在 `638-2792`，弹层在 `2798+` | 改成壳层窗口：左侧导航 + 顶部状态条 + 页面宿主；保留图表 / 模型选择 / 入口组编辑弹层。 |
| `H:\nettest\NetTest.App\Infrastructure\AppStateSnapshot.cs` | 仅保存旧模块状态 | 新增单站模式、网络复核异常类型等耐久状态；明确不保存当前导航页。 |
| `H:\nettest\NetTest.App\Infrastructure\AppStateStore.cs` | 读写 `AppStateSnapshot` 与代理目录配置 | 兼容读写新增字段。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.CommandBindings.cs` | 当前只有旧集合 / 旧命令 | 增加页面选项、单站模式选项、网络复核选项、排行榜行集合、新执行命令。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Construction.cs` | 初始化 `DashboardCards`、旧命令 | 初始化新页面集合 / 新命令；保留旧命令以降低风险。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Operations.cs` | `LoadState()` / `SaveState()` 在 `399-452` | 接入新增 UI 状态字段；保留现有结果状态保存逻辑。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyModelPicker.cs` | 代理模型校验与带校验执行入口 | 被单站统一执行命令复用；保持模型校验逻辑只写一份。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyAdvanced.cs` | 深度测试配置与说明 | 深度测试从独立页迁回单站页；保留现有配置字段与派生摘要。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyBatch.Execution.cs` | 运行快速对比并生成摘要 | 在快速对比完成后同步排行榜列表；新增对勾选候选执行深测的入口。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyBatchAggregation.cs` | 汇总排序指标 | 给排行榜列表项生成稳定性、TTFT、速率、综合能力等展示字段。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyChartView.cs` | 图表弹层快照 | 暴露批量排行榜图表专用只读属性，避免页面误用“上一张单站图”。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Reporting.Sections.cs` | 报告章节与原始 artifact 输出 | 对齐“单站测试 / 批量对比 / 网络复核 / 历史报告”术语。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Results.cs` | 单站 / 网络结果摘要写回 | 为单站页面提供统一展示字段，不改诊断本身。 |
| `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ReportArchive.cs` | 历史归档摘要 | 调整页面文案，保持归档逻辑不变。 |
| `H:\nettest\.artifacts\smoke\MainWindowViewModelCoreSmoke\Program.cs` | 当前仅验证 dashboard / command 初始化 | 增加新导航默认状态、单站默认模式、网络复核默认异常类型断言。 |
| `H:\nettest\.artifacts\smoke\ProxyBatchCommandsSmoke\Program.cs` | 当前验证批量命令与 key mask | 增加批量深测命令与排行榜列表项默认状态断言。 |

## 4. 实施任务

### 任务 1：建立导航状态与耐久化骨架

**文件：**
- 创建：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Navigation.cs`
- 创建：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.NetworkReview.cs`
- 修改：`H:\nettest\NetTest.App\Infrastructure\AppStateSnapshot.cs`
- 修改：`H:\nettest\NetTest.App\Infrastructure\AppStateStore.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.CommandBindings.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Construction.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Operations.cs`
- 测试：`H:\nettest\.artifacts\smoke\MainWindowViewModelCoreSmoke\Program.cs`

- [ ] **步骤 1：新增页面 / 模式 / 复核异常类型的状态字段与选项集合**

```csharp
private const string WorkbenchPageSingleStation = "single-station";
private const string WorkbenchPageBatchComparison = "batch-comparison";
private const string WorkbenchPageNetworkReview = "network-review";
private const string WorkbenchPageHistoryReports = "history-reports";

private const string SingleStationModeQuick = "quick";
private const string SingleStationModeStability = "stability";
private const string SingleStationModeDeep = "deep";

private const string NetworkIssueRelayUnavailable = "relay-unavailable";
private const string NetworkIssueHighTtft = "high-ttft";
private const string NetworkIssueHighJitter = "high-jitter";
private const string NetworkIssueGeoUnlock = "geo-unlock";
private const string NetworkIssueDnsRouting = "dns-routing";

private string _selectedWorkbenchPageKey = WorkbenchPageSingleStation;
private string _selectedSingleStationModeKey = SingleStationModeQuick;
private string _selectedNetworkReviewIssueKey = NetworkIssueRelayUnavailable;
```

同一任务里同时补齐：

- `ObservableCollection<SelectionOption> WorkbenchPageOptions`
- `ObservableCollection<SelectionOption> SingleStationModeOptions`
- `ObservableCollection<SelectionOption> NetworkReviewIssueOptions`
- `bool IsSingleStationPageActive / IsBatchComparisonPageActive / IsNetworkReviewPageActive / IsHistoryReportsPageActive`
- `string CurrentPageTitle`
- `string CurrentPageSubtitle`

- [ ] **步骤 2：把耐久状态接入 `LoadState()` / `SaveState()`，但不保存当前导航页**

```csharp
public sealed class AppStateSnapshot
{
    public string SingleStationModeKey { get; set; } = "quick";
    public string NetworkReviewIssueKey { get; set; } = "relay-unavailable";
}
```

在 `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Operations.cs` 中只保存：

- `SingleStationModeKey`
- `NetworkReviewIssueKey`

不要保存：

- `SelectedWorkbenchPageKey`
- 批量排行榜勾选项
- 快速对比完成态

原因：这些都属于瞬时页面态，恢复它们会让应用下次启动不再稳定落到“单站测试”的默认入口。

- [ ] **步骤 3：更新构造函数初始化，保证新属性与现有命令共存**

在 `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Construction.cs` 中完成：

- 初始化 `WorkbenchPageOptions`
- 初始化 `SingleStationModeOptions`
- 初始化 `NetworkReviewIssueOptions`
- 保留 `DashboardCards` 与旧命令初始化，不在本任务删除

- [ ] **步骤 4：扩展 `MainWindowViewModelCoreSmoke`，锁定默认状态**

```csharp
using NetTest.App.ViewModels;

var viewModel = new MainWindowViewModel();
Console.WriteLine($"page={viewModel.SelectedWorkbenchPageKey}");
Console.WriteLine($"mode={viewModel.SelectedSingleStationModeKey}");
Console.WriteLine($"issue={viewModel.SelectedNetworkReviewIssueKey}");

if (viewModel.SelectedWorkbenchPageKey != "single-station")
{
    throw new InvalidOperationException("Expected single-station as startup page.");
}

if (viewModel.SelectedSingleStationModeKey != "quick")
{
    throw new InvalidOperationException("Expected quick mode as startup mode.");
}

if (viewModel.SelectedNetworkReviewIssueKey != "relay-unavailable")
{
    throw new InvalidOperationException("Expected relay-unavailable as default review issue.");
}
```

- [ ] **步骤 5：执行验证**

```powershell
dotnet run --project "H:\nettest\.artifacts\smoke\MainWindowViewModelCoreSmoke\MainWindowViewModelCoreSmoke.csproj" -c Debug
dotnet build "H:\nettest\NetTestSuite.slnx" -c Debug -v minimal
```

预期：新增三行 `page=single-station` / `mode=quick` / `issue=relay-unavailable` 输出，且整个 smoke 与 solution build 继续通过。

- [ ] **步骤 6：提交节点（当前工作区跳过真实提交）**

建议 commit message：`feat(ui): 建立工作台导航与页面状态骨架`

### 任务 2：抽出主题资源并把主窗口改成左侧导航壳层

**文件：**
- 创建：`H:\nettest\NetTest.App\Resources\WorkbenchTheme.xaml`
- 修改：`H:\nettest\NetTest.App\App.xaml`
- 修改：`H:\nettest\NetTest.App\MainWindow.xaml`
- 创建：`H:\nettest\NetTest.App\Views\Pages\SingleStationPage.xaml`
- 创建：`H:\nettest\NetTest.App\Views\Pages\SingleStationPage.xaml.cs`
- 创建：`H:\nettest\NetTest.App\Views\Pages\BatchComparisonPage.xaml`
- 创建：`H:\nettest\NetTest.App\Views\Pages\BatchComparisonPage.xaml.cs`
- 创建：`H:\nettest\NetTest.App\Views\Pages\NetworkReviewPage.xaml`
- 创建：`H:\nettest\NetTest.App\Views\Pages\NetworkReviewPage.xaml.cs`
- 创建：`H:\nettest\NetTest.App\Views\Pages\HistoryReportsPage.xaml`
- 创建：`H:\nettest\NetTest.App\Views\Pages\HistoryReportsPage.xaml.cs`

- [ ] **步骤 1：把 `MainWindow.xaml` 里的公共样式迁到 `WorkbenchTheme.xaml`**

至少迁出以下资源：

- 颜色与画刷：`PanelBrush`、`AccentBrush`、`MutedBrush` 等
- 文字样式：`FieldLabelTextStyle`、`SectionTitleTextStyle`、`SectionHintTextStyle`
- 卡片样式：`SectionPanelBorderStyle`、`InsetPanelBorderStyle`
- 输入控件样式：`CompactInputTextBoxStyle`、`ReadOnlyOutputTextBoxStyle`
- 新导航样式：`NavigationRailBorderStyle`、`NavigationListBoxStyle`、`NavigationListBoxItemStyle`

同时在这一步把全局密度初值统一收紧：

```xml
<Setter Property="FontSize" Value="10.5" />
<Setter Property="Padding" Value="10" />
<Setter Property="Height" Value="28" />
<Setter Property="Margin" Value="0,0,0,6" />
```

- [ ] **步骤 2：在 `App.xaml` 合并主题资源字典**

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="Resources/WorkbenchTheme.xaml" />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

- [ ] **步骤 3：把 `MainWindow.xaml` 从主 `TabControl` 改成“导航壳层 + 页面宿主”**

目标骨架：

```xml
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="220" />
        <ColumnDefinition Width="12" />
        <ColumnDefinition Width="*" />
    </Grid.ColumnDefinitions>

    <Border Grid.Column="0" Style="{StaticResource NavigationRailBorderStyle}">
        <ListBox ItemsSource="{Binding WorkbenchPageOptions}"
                 SelectedValuePath="Key"
                 SelectedValue="{Binding SelectedWorkbenchPageKey, Mode=TwoWay}"
                 Style="{StaticResource NavigationListBoxStyle}" />
    </Border>

    <Grid Grid.Column="2">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <Border Grid.Row="0" Style="{StaticResource SectionPanelBorderStyle}">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>

                <StackPanel Grid.Column="0">
                    <TextBlock Style="{StaticResource SectionTitleTextStyle}"
                               Text="{Binding CurrentPageTitle}" />
                    <TextBlock Margin="0,2,0,0"
                               Style="{StaticResource SectionHintTextStyle}"
                               Text="{Binding CurrentPageSubtitle}" />
                </StackPanel>

                <StackPanel Grid.Column="1"
                            HorizontalAlignment="Right">
                    <TextBlock Text="{Binding StatusMessage}" />
                    <TextBlock Margin="0,2,0,0"
                               Foreground="{StaticResource AccentBrush}"
                               Text="{Binding LastRunAt, StringFormat=上次运行：{0}}" />
                </StackPanel>
            </Grid>
        </Border>

        <Grid Grid.Row="1">
            <views:SingleStationPage Visibility="{Binding IsSingleStationPageActive, Converter={StaticResource BoolToVisibilityConverter}}" />
            <views:BatchComparisonPage Visibility="{Binding IsBatchComparisonPageActive, Converter={StaticResource BoolToVisibilityConverter}}" />
            <views:NetworkReviewPage Visibility="{Binding IsNetworkReviewPageActive, Converter={StaticResource BoolToVisibilityConverter}}" />
            <views:HistoryReportsPage Visibility="{Binding IsHistoryReportsPageActive, Converter={StaticResource BoolToVisibilityConverter}}" />
        </Grid>
    </Grid>
</Grid>
```

- [ ] **步骤 4：保留现有弹层宿主，不要把图表 / 模型选择 / 入口组编辑弹层拆走**

`H:\nettest\NetTest.App\MainWindow.xaml` 中 `2798+` 的弹层区域继续挂在 `MainWindow` 根节点下，避免本轮引入新的弹窗宿主机制。

- [ ] **步骤 5：执行验证**

```powershell
dotnet build "H:\nettest\NetTestSuite.slnx" -c Debug -v minimal
dotnet run --project "H:\nettest\NetTest.App\NetTest.App.csproj" -c Debug
```

人工验收点：

1. 启动后左侧只出现 4 个一级入口；
2. 不再出现“总览”或“中转站深度检测”一级标签；
3. 应用默认停在“单站测试”；
4. 图表、模型选择、入口组编辑弹层仍可打开。

- [ ] **步骤 6：提交节点（当前工作区跳过真实提交）**

建议 commit message：`feat(ui): 重建主窗口为左侧导航工作台壳层`

### 任务 3：重做“单站测试”页面与统一执行入口

**文件：**
- 创建：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.SingleStationWorkflow.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.CommandBindings.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Construction.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyModelPicker.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyAdvanced.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Results.cs`
- 修改：`H:\nettest\NetTest.App\Views\Pages\SingleStationPage.xaml`
- 测试：`H:\nettest\.artifacts\smoke\MainWindowViewModelCoreSmoke\Program.cs`

- [ ] **步骤 1：新增“按当前单站模式执行”的统一命令**

在 `CommandBindings.cs` / `Construction.cs` 中补齐：

- `AsyncRelayCommand RunSelectedSingleStationModeCommand`

在 `SingleStationWorkflow.cs` 中实现：

```csharp
private Task RunSelectedSingleStationModeAsync()
    => SelectedSingleStationModeKey switch
    {
        "stability" => RunProxySeriesWithValidationAsync(),
        "deep" => RunProxyDeepWithValidationAsync(),
        _ => RunProxyWithValidationAsync()
    };
```

这样页面只保留一个主 CTA，避免旧 UI 里“基础 / 深度 / 稳定性”多个按钮并排让交互显得拖沓。

- [ ] **步骤 2：补齐单站页面需要的派生摘要字段**

在 `SingleStationWorkflow.cs` 中新增：

```csharp
public bool IsSingleStationQuickMode => SelectedSingleStationModeKey == "quick";
public bool IsSingleStationStabilityMode => SelectedSingleStationModeKey == "stability";
public bool IsSingleStationDeepMode => SelectedSingleStationModeKey == "deep";

public string SingleStationModeDescription => SelectedSingleStationModeKey switch
{
    "stability" => "多轮观察成功率、波动与连续失败，适合判断某个站点稳不稳。",
    "deep" => "验证协议兼容、流式完整性、多模态、缓存与官方对照等高级能力。",
    _ => "快速判断当前站点是否可用、首字快不快、输出稳不稳。"
};

public string SingleStationPrimaryButtonText => SelectedSingleStationModeKey switch
{
    "stability" => "开始稳定性测试",
    "deep" => "开始深度测试",
    _ => "开始快速测试"
};

public string SingleStationResultSummary
    => IsSingleStationStabilityMode ? ProxyStabilitySummary : ProxySummary;

public string SingleStationResultDetail
    => IsSingleStationStabilityMode ? ProxyStabilityDetail : ProxyDetail;
```

要求：

- 快速模式读 `ProxySummary` / `ProxyDetail`
- 稳定性模式读 `ProxyStabilitySummary` / `ProxyStabilityDetail`
- 深度模式继续读 `ProxySummary` / `ProxyDetail`

- [ ] **步骤 3：在 `SingleStationPage.xaml` 落地三段式布局**

页面固定三块：

1. `当前站点配置`
2. `当前模式参数与执行`
3. `结果与统一输出`

关键结构：

```xml
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="Auto" />
        <RowDefinition Height="*" />
    </Grid.RowDefinitions>

    <Border Grid.Row="0" Style="{StaticResource SectionPanelBorderStyle}">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="2*" />
                <ColumnDefinition Width="8" />
                <ColumnDefinition Width="1.6*" />
                <ColumnDefinition Width="8" />
                <ColumnDefinition Width="1.4*" />
                <ColumnDefinition Width="8" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>

            <TextBox Grid.Column="0"
                     Text="{Binding ProxyBaseUrl, UpdateSourceTrigger=PropertyChanged}" />
            <TextBox Grid.Column="2"
                     Text="{Binding ProxyApiKey, UpdateSourceTrigger=PropertyChanged}" />
            <TextBox Grid.Column="4"
                     Text="{Binding ProxyModel, UpdateSourceTrigger=PropertyChanged}" />
            <Button Grid.Column="6"
                    Command="{Binding FetchProxyModelsCommand}"
                    Content="拉取模型" />
        </Grid>
    </Border>

    <Border Grid.Row="1" Margin="0,6,0,0" Style="{StaticResource SectionPanelBorderStyle}">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="12" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>

            <ListBox Grid.Column="0"
                     ItemsSource="{Binding SingleStationModeOptions}"
                     SelectedValuePath="Key"
                     SelectedValue="{Binding SelectedSingleStationModeKey, Mode=TwoWay}" />

            <Button Grid.Column="2"
                    Command="{Binding RunSelectedSingleStationModeCommand}"
                    Content="{Binding SingleStationPrimaryButtonText}" />
        </Grid>
    </Border>

    <Border Grid.Row="2" Margin="0,6,0,0" Style="{StaticResource SectionPanelBorderStyle}">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>

            <TextBlock Style="{StaticResource SectionTitleTextStyle}"
                       Text="{Binding SingleStationResultSummary}" />
            <TextBlock Grid.Row="1"
                       Margin="0,4,0,0"
                       Style="{StaticResource SectionHintTextStyle}"
                       Text="{Binding SingleStationResultDetail}" />
            <TextBox Grid.Row="2"
                     Margin="0,6,0,0"
                     Style="{StaticResource ReadOnlyConsoleTextBoxStyle}"
                     Text="{Binding ProxyUnifiedOutput, Mode=OneWay}" />
        </Grid>
    </Border>
</Grid>
```

- [ ] **步骤 4：把旧“中转站深度检测”页里的配置迁回深度模式区，但默认折叠**

迁回内容：

- `SelectedProxyDiagnosticPresetKey`
- 协议兼容 / 错误透传 / 流式完整性 / 多模态 / 缓存机制 / 缓存隔离 / 官方对照

显示规则：

- 只有 `IsSingleStationDeepMode == true` 时显示深度配置区
- 深度配置区默认使用 `Expander`，`IsExpanded="False"`
- 快速模式与稳定性模式下不显示深度配置区

- [ ] **步骤 5：扩展 smoke，锁定默认模式与新执行命令**

```csharp
Console.WriteLine($"singleMode={viewModel.SelectedSingleStationModeKey}");
Console.WriteLine($"singleCommand={(viewModel.RunSelectedSingleStationModeCommand is null ? "null" : "ok")}");

if (viewModel.RunSelectedSingleStationModeCommand is null)
{
    throw new InvalidOperationException("Expected RunSelectedSingleStationModeCommand.");
}
```

- [ ] **步骤 6：执行验证**

```powershell
dotnet run --project "H:\nettest\.artifacts\smoke\MainWindowViewModelCoreSmoke\MainWindowViewModelCoreSmoke.csproj" -c Debug
dotnet build "H:\nettest\NetTestSuite.slnx" -c Debug -v minimal
dotnet run --project "H:\nettest\NetTest.App\NetTest.App.csproj" -c Debug
```

人工验收点：

1. 单站默认模式是“快速测试”；
2. 站点配置区不再重复深度测试参数；
3. 稳定性模式只展示轮次 / 间隔相关参数；
4. 深度模式里高级项默认折叠；
5. 页面纵向高度明显比旧版更紧凑。

- [ ] **步骤 7：提交节点（当前工作区跳过真实提交）**

建议 commit message：`feat(ui): 重做单站测试三段式页面`

### 任务 4：重做“批量对比”页面并实现候选勾选后的深度测试

**文件：**
- 创建：`H:\nettest\NetTest.App\ViewModels\ProxyBatchRankingRowViewModel.cs`
- 创建：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.BatchWorkflow.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.CommandBindings.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Construction.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyBatch.Execution.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyBatchAggregation.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyChartView.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyModelPicker.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Operations.cs`
- 修改：`H:\nettest\NetTest.App\Views\Pages\BatchComparisonPage.xaml`
- 测试：`H:\nettest\.artifacts\smoke\ProxyBatchCommandsSmoke\Program.cs`

- [ ] **步骤 1：新增排行榜列表项视图模型与批量流程状态**

`H:\nettest\NetTest.App\ViewModels\ProxyBatchRankingRowViewModel.cs` 采用与 `PortScanBatchRowViewModel` 类似的简单属性风格：

```csharp
public sealed class ProxyBatchRankingRowViewModel : ObservableObject
{
    public bool IsSelected { get; set; }
    public int Rank { get; set; }
    public string EntryName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string QuickVerdict { get; set; } = "待对比";
    public string QuickMetrics { get; set; } = "--";
    public string CapabilitySummary { get; set; } = "--";
    public string DeepStatus { get; set; } = "未开始";
    public string DeepSummary { get; set; } = "完成快速对比后，可手动勾选并发起深度测试。";
    internal string ApiKey { get; set; } = string.Empty;
}
```

在 `MainWindowViewModel.BatchWorkflow.cs` 中新增：

- `ObservableCollection<ProxyBatchRankingRowViewModel> ProxyBatchRankingRows`
- `bool ProxyBatchQuickCompareCompleted`
- `bool CanRunSelectedBatchDeepTests`
- `string BatchSelectionSummary`
- `string BatchDeepTestSummary`
- `AsyncRelayCommand RunSelectedBatchDeepTestsCommand`

- [ ] **步骤 2：快速对比完成后，把聚合结果投影成排行榜列表，并清空所有勾选**

在 `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyBatch.Execution.cs` 的 `ApplyProxyBatchResults(IReadOnlyList<IReadOnlyList<ProxyBatchProbeRow>> batchRuns, ProxyBatchPlan plan)` 末尾新增：

```csharp
RefreshProxyBatchRankingRows(aggregateRows);
ProxyBatchQuickCompareCompleted = true;
```

`RefreshProxyBatchRankingRows(IReadOnlyList<ProxyBatchAggregateRow> aggregateRows)` 要做三件事：

1. 清空旧列表；
2. 按当前排行榜顺序写入 `Rank / EntryName / BaseUrl / QuickVerdict / QuickMetrics / CapabilitySummary`；
3. **强制把所有 `IsSelected` 设为 `false`**。

这样才能满足“快速对比完成后默认不自动勾选任何站点”。

- [ ] **步骤 3：让批量深测复用单站深度逻辑，但不覆盖当前单站配置**

先把 `RunSingleProxyDiagnosticsAsync(ProxyEndpointSettings settings, IProgress<ProxyDiagnosticsLiveProgress>? progress, ProxySingleExecutionPlan executionPlan)` 重构成可接受显式 `ProxyEndpointSettings` 的版本：

```csharp
private async Task<ProxyDiagnosticsResult> RunSingleProxyDiagnosticsAsync(
    ProxyEndpointSettings settings,
    IProgress<ProxyDiagnosticsLiveProgress>? progress,
    ProxySingleExecutionPlan executionPlan)
{
    var result = await _proxyDiagnosticsService.RunAsync(settings, progress);
    // 后续补充探针、长流逻辑沿用现有实现
}
```

现有单站路径继续传入 `BuildProxySettings()`；批量深测则使用：

```csharp
var settings = new ProxyEndpointSettings(
    row.BaseUrl,
    row.ApiKey,
    row.Model,
    ProxyIgnoreTlsErrors,
    ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 120));
```

`RunSelectedBatchDeepTestsAsync()` 顺序执行被勾选项，并写回每一项的：

- `DeepStatus`
- `DeepSummary`
- 最后更新时间（可拼到 `DeepSummary`）

不修改：

- `ProxyBaseUrl`
- `ProxyApiKey`
- `ProxyModel`

- [ ] **步骤 4：暴露批量专用图表属性，避免页面误用“上一次单站图”**

在 `H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ProxyChartView.cs` 中新增：

```csharp
public BitmapSource? BatchComparisonChartImage => _proxyBatchChartSnapshot?.Image;
public bool HasBatchComparisonChart => BatchComparisonChartImage is not null;
public string BatchComparisonChartStatusSummary =>
    _proxyBatchChartSnapshot?.StatusSummary ?? "完成快速对比后，这里显示排行榜图表。";
```

这样 `BatchComparisonPage.xaml` 可以稳定绑定批量图，而不是复用 `ProxyTrendChartImage` 的“最后一张任意图”。

- [ ] **步骤 5：在 `BatchComparisonPage.xaml` 落地“排行榜优先”界面**

页面结构：

1. 顶部：入口组导入 / 管理与“开始快速对比”按钮
2. 中部左侧：排行榜图表（只读）
3. 中部右侧：排行榜列表（唯一可勾选区域）
4. 底部：勾选结果摘要 + “对勾选项做深度测试”按钮

关键 XAML：

```xml
<Button Command="{Binding RunProxyBatchCommand}" Content="开始快速对比" />

<Image Source="{Binding BatchComparisonChartImage}"
       Visibility="{Binding HasBatchComparisonChart, Converter={StaticResource BoolToVisibilityConverter}}" />

<ListBox ItemsSource="{Binding ProxyBatchRankingRows}">
    <ListBox.ItemTemplate>
        <DataTemplate>
            <Grid>
                <CheckBox IsChecked="{Binding IsSelected, Mode=TwoWay}" />
                <StackPanel Margin="22,0,0,0">
                    <TextBlock Text="{Binding EntryName}" />
                    <TextBlock Style="{StaticResource SectionHintTextStyle}"
                               Text="{Binding QuickMetrics}" />
                    <TextBlock Style="{StaticResource SectionHintTextStyle}"
                               Text="{Binding CapabilitySummary}" />
                    <TextBlock Foreground="{StaticResource AccentBrush}"
                               Text="{Binding DeepStatus}" />
                </StackPanel>
            </Grid>
        </DataTemplate>
    </ListBox.ItemTemplate>
</ListBox>

<Button Command="{Binding RunSelectedBatchDeepTestsCommand}"
        IsEnabled="{Binding CanRunSelectedBatchDeepTests}"
        Content="对勾选项做深度测试" />
```

明确禁止在图表区绑定任何勾选或点击即选中逻辑。

- [ ] **步骤 6：扩展 smoke，锁定批量深测入口与列表项默认态**

```csharp
using NetTest.App.ViewModels;

var viewModel = new MainWindowViewModel();
var row = new ProxyBatchRankingRowViewModel();
Console.WriteLine($"deepCommand={(viewModel.RunSelectedBatchDeepTestsCommand is null ? "null" : "ok")}");
Console.WriteLine($"rowSelected={row.IsSelected}");
Console.WriteLine($"rowDeepStatus={row.DeepStatus}");

if (viewModel.RunSelectedBatchDeepTestsCommand is null)
{
    throw new InvalidOperationException("Expected RunSelectedBatchDeepTestsCommand.");
}

if (row.IsSelected)
{
    throw new InvalidOperationException("Expected ranking row to start unchecked.");
}

if (row.DeepStatus != "未开始")
{
    throw new InvalidOperationException("Unexpected default deep status.");
}
```

- [ ] **步骤 7：执行验证**

```powershell
dotnet run --project "H:\nettest\.artifacts\smoke\ProxyBatchCommandsSmoke\ProxyBatchCommandsSmoke.csproj" -c Debug
dotnet build "H:\nettest\NetTestSuite.slnx" -c Debug -v minimal
dotnet run --project "H:\nettest\NetTest.App\NetTest.App.csproj" -c Debug
```

人工验收点：

1. 批量页默认就是排行榜视图；
2. 快速对比前，“对勾选项做深度测试”按钮不可用；
3. 快速对比后不会自动勾选任何列表项；
4. 只能勾选排行榜列表项，图表区纯展示；
5. 勾选后才能触发批量深测；
6. 批量深测不会把单站页当前的地址 / key / model 偷偷改掉。

- [ ] **步骤 8：提交节点（当前工作区跳过真实提交）**

建议 commit message：`feat(ui): 重做批量对比排行榜与候选深测流程`

### 任务 5：重做“网络复核”页面为异常类型驱动

**文件：**
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.NetworkReview.cs`
- 修改：`H:\nettest\NetTest.App\Views\Pages\NetworkReviewPage.xaml`
- 测试：`H:\nettest\.artifacts\smoke\MainWindowViewModelCoreSmoke\Program.cs`

- [ ] **步骤 1：建立“异常类型 → 推荐模块”的派生摘要**

在 `MainWindowViewModel.NetworkReview.cs` 中实现：

```csharp
public string NetworkReviewRecommendationSummary => SelectedNetworkReviewIssueKey switch
{
    "high-ttft" => "优先做性能复核：先测速，再看路由 / MTR。",
    "high-jitter" => "优先做性能复核：关注波动、抖动与链路不稳定。",
    "geo-unlock" => "优先做出口复核：看出口 IP、DNS 与分流路径。",
    "dns-routing" => "优先做出口复核，其次做基础复核确认本机网络状态。",
    _ => "优先做基础复核：先确认本机网络与官方链路，再判断是不是中转站本身不可用。"
};
```

同时提供布尔派生属性：

- `ShowBasicReview`
- `ShowPerformanceReview`
- `ShowExitReview`
- `ShowAdvancedReview`

这些属性只决定默认强调顺序，不屏蔽任何模块按钮。

- [ ] **步骤 2：把网络页面改成“先选异常，再看模块卡组”**

`NetworkReviewPage.xaml` 顶部先放异常类型选择：

```xml
<ListBox ItemsSource="{Binding NetworkReviewIssueOptions}"
         SelectedValuePath="Key"
         SelectedValue="{Binding SelectedNetworkReviewIssueKey, Mode=TwoWay}" />
```

下方固定四组卡片：

1. 基础复核：`RunNetworkCommand`、`RunChatGptTraceCommand`
2. 性能复核：`RunSpeedTestCommand`、`RunRouteCommand`
3. 出口复核：`RunSplitRoutingCommand`
4. 高级复核：`RunStunCommand`、`RunPortScanCommand`

每组卡片先展示“建议用途”，再展示结果摘要。

- [ ] **步骤 3：在页面中把“先判断，再看细节”落地**

顶部摘要卡必须优先展示：

- `NetworkReviewRecommendationSummary`
- `CurrentPageSubtitle` 或独立 `NetworkReviewNextActionSummary`
- 当前更像“本地网络问题”还是“中转站问题”的提示文案

原始明细（如 `ChatGptRawTrace`、`RouteRawOutput`、`PortScanRawOutput`）不作为首屏默认主内容。

- [ ] **步骤 4：扩展 smoke，锁定异常类型切换后的派生文案**

```csharp
viewModel.SelectedNetworkReviewIssueKey = "high-ttft";
Console.WriteLine($"review={viewModel.NetworkReviewRecommendationSummary}");

if (!viewModel.NetworkReviewRecommendationSummary.Contains("性能复核"))
{
    throw new InvalidOperationException("Expected performance review guidance for high-ttft.");
}
```

- [ ] **步骤 5：执行验证**

```powershell
dotnet run --project "H:\nettest\.artifacts\smoke\MainWindowViewModelCoreSmoke\MainWindowViewModelCoreSmoke.csproj" -c Debug
dotnet build "H:\nettest\NetTestSuite.slnx" -c Debug -v minimal
dotnet run --project "H:\nettest\NetTest.App\NetTest.App.csproj" -c Debug
```

人工验收点：

1. 网络复核页不再平铺 6 个一级标签；
2. 用户先看到异常类型选择，再看到对应模块推荐；
3. 首屏先给结论与建议，不先堆原始文本；
4. 所有现有网络命令仍可从页面触发。

- [ ] **步骤 6：提交节点（当前工作区跳过真实提交）**

建议 commit message：`feat(ui): 重做网络复核页为异常导向流程`

### 任务 6：整理“历史报告”页面、统一术语，并做总回归

**文件：**
- 修改：`H:\nettest\NetTest.App\Views\Pages\HistoryReportsPage.xaml`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Reporting.Sections.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.Operations.cs`
- 修改：`H:\nettest\NetTest.App\ViewModels\MainWindowViewModel.ReportArchive.cs`
- 测试：`H:\nettest\.artifacts\smoke\ReportingSmoke\Program.cs`
- 测试：`H:\nettest\.artifacts\smoke\MainWindowViewModelCoreSmoke\Program.cs`
- 测试：`H:\nettest\.artifacts\smoke\ProxyBatchCommandsSmoke\Program.cs`

- [ ] **步骤 1：把“历史记录”页整理为“历史报告”页**

页面只保留两列：

- 左：`HistorySummary`
- 右：`ReportArchiveSummary` + `ExportCurrentReportCommand`

关键标题与说明文案统一改为：

- `历史报告`
- `运行历史`
- `报告归档`
- `导出当前报告`

- [ ] **步骤 2：统一代理相关术语，不再继续使用旧一级页名称**

在 `Operations.cs` 与 `Reporting.Sections.cs` 中把新增历史 / 报告标题统一为：

- `单站测试`
- `批量对比`
- `网络复核`
- `历史报告`

建议最少完成以下替换：

- `中转站单次测试` → `单站测试`
- `中转站入口组对比` → `批量对比`
- 新增历史项的 `category` 不再写 `中转站`，而是按 `单站测试` / `批量对比` 写入

旧历史数据不做迁移；只保证新生成的记录和新导出的报告术语一致。

- [ ] **步骤 3：做一次全局密度收尾**

在 `WorkbenchTheme.xaml` 与四个页面 XAML 中统一检查并收紧：

```xml
<Button Height="28" />
<TextBox Height="28" />
<Setter Property="Padding" Value="10,8" />
<Setter Property="Margin" Value="0,0,0,6" />
```

特别关注：

- 卡片上下间距从旧版 `8-12` 压到 `6-8`
- 标题和正文之间的空白减少
- 深度模式与高级复核默认折叠
- 页面首屏避免出现大块空白

- [ ] **步骤 4：执行完整回归**

```powershell
dotnet build "H:\nettest\NetTestSuite.slnx" -c Debug -v minimal
dotnet run --project "H:\nettest\.artifacts\smoke\MainWindowViewModelCoreSmoke\MainWindowViewModelCoreSmoke.csproj" -c Debug
dotnet run --project "H:\nettest\.artifacts\smoke\ProxyBatchCommandsSmoke\ProxyBatchCommandsSmoke.csproj" -c Debug
dotnet run --project "H:\nettest\.artifacts\smoke\ProxyBatchEditorSmoke\ProxyBatchEditorSmoke.csproj" -c Debug
dotnet run --project "H:\nettest\.artifacts\smoke\ReportingSmoke\ReportingSmoke.csproj" -c Debug
```

- [ ] **步骤 5：执行人工 UI 验收清单**

```text
[ ] 启动即进入“单站测试”
[ ] 左侧一级导航只有 4 项
[ ] 不存在首页 / 总览页
[ ] 单站默认模式是“快速测试”
[ ] 单站页面不重复站点配置
[ ] 批量页面默认是排行榜视图
[ ] 批量页面快速对比后默认不勾选任何列表项
[ ] 图表区不承载勾选交互
[ ] 网络复核先问异常，再给模块
[ ] 历史报告页只负责回看与导出
[ ] 全局字体、卡片、空白比旧版更紧凑
```

- [ ] **步骤 6：提交节点（当前工作区跳过真实提交）**

建议 commit message：`feat(ui): 完成中转站测试工作台 UI 重构`

## 5. 规格覆盖对照

| 设计稿要求 | 对应任务 |
|---|---|
| 去掉首页 / 总览，改成左侧 4 个一级入口 | 任务 1、任务 2 |
| 应用启动后直接进入单站测试 | 任务 1、任务 2 |
| 单站测试三段式，默认快速测试 | 任务 3 |
| 深度测试并入单站，不再单独成页 | 任务 2、任务 3 |
| 批量页默认排行榜视图 | 任务 4 |
| 图表只读、列表可勾选、默认不自动勾选 | 任务 4 |
| 快速对比后，对勾选候选站点做深度测试 | 任务 4 |
| 网络复核先选异常类型，再推荐模块 | 任务 5 |
| 历史报告页只负责回看与导出 | 任务 6 |
| 全局密度压缩 | 任务 2、任务 6 |
| 新增并持久化必要页面状态 | 任务 1 |

## 6. 风险与控制点

- **风险 1：`MainWindow.xaml` 改动过大导致 XAML 编译错误。**  
  控制：先抽主题资源，再替换壳层；每完成一个页面后立即 `dotnet build`。

- **风险 2：批量深测直接复用单站逻辑时，误覆盖单站当前配置。**  
  控制：先把 `RunSingleProxyDiagnosticsAsync` 改为接收显式 `ProxyEndpointSettings`；批量路径只传局部 settings，不写 `ProxyBaseUrl / ProxyApiKey / ProxyModel`。

- **风险 3：排行榜图表绑定到错误的“最后一张图”。**  
  控制：暴露 `BatchComparisonChartImage` 专用属性，不直接绑定 `ProxyTrendChartImage`。

- **风险 4：把历史术语全部重命名可能影响旧数据展示。**  
  控制：不迁移旧历史数据，只保证新写入记录和新导出报告名称统一。

- **风险 5：为了清理旧结构而删除 `DashboardCards` / `RunQuickSuiteCommand`，引入大面积回归。**  
  控制：本轮只下线 UI 暴露，不删除内部兼容层；等 UI 重构稳定后再考虑二次清理计划。
