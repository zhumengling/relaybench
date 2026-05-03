# 应用接入目标勾选与协议分流实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让「应用接入」和「批量测试」里的「应用到软件」弹窗列出所有可应用软件，由用户勾选本次要写入的目标，并按 Anthropic、OpenAI 兼容、Responses 3 类协议正确写入配置。

**架构：** 保持「单站测试」和「应用接入」共用当前接口状态，新增一个专用的应用目标选择弹窗。ViewModel 负责构建待应用目标、保存用户勾选状态和发起应用；Core 层负责按目标软件和协议执行配置写入。

**技术栈：** WPF、MVVM、`ObservableCollection<T>`、现有 `ClientApiDiagnosticsService`、`CodexFamilyConfigApplyService`、`ClientApiConfigRestoreService`、xUnit 或现有测试项目。

---

## 范围与成功标准

本次改造覆盖 2 个入口：

- 「应用接入」右侧「应用当前接口」按钮。
- 「批量评测」排行或结果中的「应用到软件」按钮。

必须达到以下效果：

- 「应用接入」中「当前接口」下方的 Base URL、API Key、模型始终跟随「单站测试」当前输入变化。
- 2 个「应用到软件」入口使用同一套目标选择弹窗。
- 弹窗中列出本机已发现且当前接口协议可支持的软件，每行包含勾选框、软件名称、协议类型、配置路径摘要和风险提示。
- 用户可以全选、反选、取消，至少选择 1 个目标后才能确认应用。
- 应用结果按目标逐项返回：成功、跳过、失败、备份路径、修改文件。
- Anthropic 协议、OpenAI 兼容协议、Responses 协议不能混写配置。

## 现状

当前代码路径：

- 「应用接入」入口：`RelayBench.App/ViewModels/MainWindowViewModel.ApplicationCenter.cs`
- 「批量评测」入口：`RelayBench.App/ViewModels/MainWindowViewModel.BatchApply.cs`
- 普通确认弹窗：`RelayBench.App/ViewModels/MainWindowViewModel.ConfirmationDialog.cs`
- 客户端扫描：`RelayBench.App/ViewModels/MainWindowViewModel.ClientApiCatalog.cs`
- 客户端识别服务：`RelayBench.Core/Services/ClientApiDiagnosticsService.cs`
- Codex 写入服务：`RelayBench.Core/Services/CodexFamilyConfigApplyService.cs`
- 应用结果模型：`RelayBench.Core/Models/ClientAppApplyResult.cs`

现在 `CodexFamilyConfigApplyService.ApplyAsync(...)` 会把当前入口一次性应用到 Codex CLI、Codex Desktop、VSCode Codex，共用 `~/.codex/config.toml` 和 `~/.codex/auth.json`。这不满足用户逐个勾选软件的需求，也无法承载 Claude CLI 这类 Anthropic 协议软件。

## 协议应用规则

### 1. Responses

适用目标：

- Codex CLI
- Codex Desktop
- VSCode Codex

写入规则：

- `~/.codex/config.toml`
  - `model_provider = "custom"`
  - `model = "<当前模型>"`
  - `[model_providers.custom]`
  - `base_url = "<当前 Base URL>"`
  - `wire_api = "responses"`
  - `experimental_bearer_token = "<当前 API Key>"`
  - `http_headers = { "Content-Type" = "application/json" }`
- `~/.codex/auth.json`
  - 清理 `auth_mode = "apikey"` 和旧 `OPENAI_API_KEY` 接管字段。
  - 保留 OAuth 登录态，不把官方登录切成 API Key 模式。

前置校验：

- 必须有 Base URL、API Key、模型。
- 必须通过现有 `ProbeCodexResponsesCompatibilityBeforeApplyAsync(...)` 或缓存结果判断。
- 如果接口不支持 Responses API，不在弹窗中默认勾选 Codex 目标；仍可显示为「不可选：Responses 不支持」。
- 点击「应用当前接口」时必须强制执行一次实时协议探测，并把 OpenAI Chat Completions、OpenAI Responses、Anthropic Messages 的探测结果展示在确认弹窗中。
- 目标选择 Planner 应优先复用本次实时探测结果，不再对同一 Base URL / API Key / 模型重复探测。

### 2. OpenAI 兼容

适用目标：

- 支持 OpenAI Chat Completions 或 OpenAI SDK 兼容入口的软件。
- 现阶段可先作为目标能力枚举保留，Codex 目标只有在明确使用 `wire_api = "chat"` 且对应版本支持时才允许。

写入规则：

- Base URL 统一标准化到 OpenAI 兼容根地址，例如 `https://host/v1`。
- API Key 写入目标软件支持的 OpenAI Key 字段，例如 `OPENAI_API_KEY`、`api_key` 或目标配置中的 bearer token 字段。
- 模型写入目标软件的模型字段。
- 请求协议按 `/v1/chat/completions` 处理，不写 Responses 专用字段。

前置校验：

- 必须通过 `/v1/models` 或一次最小 chat completions 探测。
- 如果目标软件只支持 Responses，不允许把 OpenAI 兼容配置写进去。

### 3. Anthropic

适用目标：

- Claude CLI。
- 后续可扩展到其他 Claude / Anthropic SDK 兼容客户端。

写入规则：

- Base URL 标准化到 Anthropic Messages API 根地址，例如 `https://host` 或目标软件要求的 Anthropic Base URL。
- API Key 写入 Anthropic Key 字段，例如 `ANTHROPIC_API_KEY`。
- 如目标支持模型字段，写入当前模型；不支持时只写 Base URL 和 API Key。
- 请求协议按 `/v1/messages` 处理，不写 OpenAI 的 `/v1/chat/completions` 或 Responses 配置。

前置校验：

- 必须通过 Anthropic Messages 最小探测，或确认当前入口被标记为 Anthropic 协议。
- Anthropic Messages 探测请求必须带 `anthropic-version: 2023-06-01` 和 `x-api-key`。
- Anthropic 目标不能接受 OpenAI 兼容模型配置，除非接口明确提供 Anthropic 兼容转换层。

## 文件结构

### 新增文件

- `RelayBench.Core/Models/ClientApplyProtocolKind.cs`
  - 定义 `Responses`、`OpenAiCompatible`、`Anthropic`。
- `RelayBench.Core/Models/ClientApplyTarget.cs`
  - 描述一个可写入目标：目标 ID、名称、协议、是否已安装、是否可选、配置摘要、不可选原因。
- `RelayBench.Core/Models/ClientApplyTargetSelection.cs`
  - ViewModel 与 Core 之间传递用户勾选结果。
- `RelayBench.Core/Services/ClientAppApplyPlanner.cs`
  - 根据扫描结果和当前接口能力生成可应用目标列表。
- `RelayBench.Core/Services/IClientAppConfigApplyAdapter.cs`
  - 各协议写入适配器接口。
- `RelayBench.Core/Services/CodexResponsesConfigApplyAdapter.cs`
  - 承接现有 Codex 写入逻辑，按勾选目标记录应用结果。
- `RelayBench.Core/Services/AnthropicClientConfigApplyAdapter.cs`
  - 写 Claude CLI / Anthropic 兼容客户端配置。
- `RelayBench.App/ViewModels/ClientApplyTargetItemViewModel.cs`
  - 弹窗列表每一行的状态。
- `RelayBench.App/ViewModels/MainWindowViewModel.ClientApplyTargetDialog.cs`
  - 专用应用目标选择弹窗的状态、命令和等待逻辑。

### 修改文件

- `RelayBench.App/ViewModels/MainWindowViewModel.ApplicationCenter.cs`
  - 将纯确认弹窗替换为目标选择弹窗。
- `RelayBench.App/ViewModels/MainWindowViewModel.BatchApply.cs`
  - 复用同一目标选择弹窗和应用服务。
- `RelayBench.App/ViewModels/MainWindowViewModel.StateBindings.cs`
  - 确认 `ProxyBaseUrl`、`ProxyApiKey`、`ProxyModel` 变化时继续通知应用接入预览刷新。
- `RelayBench.App/MainWindow.xaml`
  - 新增应用目标选择 Overlay。
- `RelayBench.App/ViewModels/MainWindowViewModel.CommandBindings.cs`
  - 新增全选、反选、确认应用、取消应用命令。
- `RelayBench.Core/Services/CodexFamilyConfigApplyService.cs`
  - 拆出 Codex 单目标写入能力，避免始终返回 3 个固定目标。
- `RelayBench.Core/Models/ClientAppApplyResult.cs`
  - 扩展为包含逐目标结果。
- `RelayBench.Core/Services/ClientApiDiagnosticsService.cs`
  - 输出每个客户端支持的协议能力，供 Planner 使用。

## 任务 1：固化当前接口同步

**文件：**

- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.StateBindings.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ApplicationCenter.cs`
- 测试：`RelayBench.App.Tests/ViewModels/ApplicationCenterProxyContextTests.cs`

- [ ] **步骤 1：编写失败测试**

```csharp
[Fact]
public void ApplicationCenterPreviewChangesWhenSingleStationProxyContextChanges()
{
    var vm = new MainWindowViewModel();
    var changed = new List<string>();
    vm.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is not null)
        {
            changed.Add(e.PropertyName);
        }
    };

    vm.ProxyBaseUrl = "https://relay.example/v1";
    vm.ProxyApiKey = "sk-test";
    vm.ProxyModel = "gpt-4.1-mini";

    Assert.Contains(nameof(vm.ApplicationCenterApplyTargetSummary), changed);
    Assert.Contains(nameof(vm.ApplicationCenterApplyPreviewDetail), changed);
    Assert.Contains(nameof(vm.ApplicationCenterProxyApiKeyPreview), changed);
    Assert.Contains("https://relay.example/v1", vm.ApplicationCenterApplyPreviewDetail);
    Assert.Contains("gpt-4.1-mini", vm.ApplicationCenterApplyPreviewDetail);
}
```

- [ ] **步骤 2：运行测试验证失败或覆盖现状**

运行：

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ApplicationCenterProxyContextTests
```

预期：如果缺少测试项目或通知不完整，失败；如果现有行为已经满足，测试通过并保留为回归保护。

- [ ] **步骤 3：补齐通知**

确保 `ProxyBaseUrl`、`ProxyApiKey`、`ProxyModel` setter 中都调用：

```csharp
NotifyApplicationCenterProxyContextChanged();
```

确保 `NotifyApplicationCenterProxyContextChanged()` 包含：

```csharp
OnPropertyChanged(nameof(ApplicationCenterApplyTargetSummary));
OnPropertyChanged(nameof(ApplicationCenterApplyPreviewDetail));
OnPropertyChanged(nameof(ApplicationCenterProxyApiKeyPreview));
ApplyCurrentInterfaceToCodexAppsCommand?.RaiseCanExecuteChanged();
```

- [ ] **步骤 4：运行测试验证通过**

运行：

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ApplicationCenterProxyContextTests
```

预期：测试通过。

- [ ] **步骤 5：Commit**

```powershell
git add RelayBench.App/ViewModels/MainWindowViewModel.StateBindings.cs RelayBench.App/ViewModels/MainWindowViewModel.ApplicationCenter.cs RelayBench.App.Tests/ViewModels/ApplicationCenterProxyContextTests.cs
git commit -m "test(应用接入): 覆盖当前接口同步"
```

## 任务 2：建立协议和目标模型

**文件：**

- 创建：`RelayBench.Core/Models/ClientApplyProtocolKind.cs`
- 创建：`RelayBench.Core/Models/ClientApplyTarget.cs`
- 创建：`RelayBench.Core/Models/ClientApplyTargetSelection.cs`
- 修改：`RelayBench.Core/Models/ClientAppApplyResult.cs`
- 测试：`RelayBench.Core.Tests/ClientApplyTargetModelTests.cs`

- [ ] **步骤 1：编写失败测试**

```csharp
[Fact]
public void ClientApplyTargetMarksUnsupportedProtocolAsNotSelectable()
{
    var target = new ClientApplyTarget(
        Id: "claude-cli",
        DisplayName: "Claude CLI",
        Protocol: ClientApplyProtocolKind.Anthropic,
        IsInstalled: true,
        IsSelectable: false,
        IsDefaultSelected: false,
        ConfigSummary: "~/.claude/settings.json",
        DisabledReason: "当前接口不是 Anthropic 协议");

    Assert.False(target.IsSelectable);
    Assert.Equal(ClientApplyProtocolKind.Anthropic, target.Protocol);
    Assert.Equal("当前接口不是 Anthropic 协议", target.DisabledReason);
}
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ClientApplyTargetModelTests
```

预期：类型不存在导致失败。

- [ ] **步骤 3：新增模型**

```csharp
namespace RelayBench.Core.Models;

public enum ClientApplyProtocolKind
{
    Responses,
    OpenAiCompatible,
    Anthropic
}
```

```csharp
namespace RelayBench.Core.Models;

public sealed record ClientApplyTarget(
    string Id,
    string DisplayName,
    ClientApplyProtocolKind Protocol,
    bool IsInstalled,
    bool IsSelectable,
    bool IsDefaultSelected,
    string ConfigSummary,
    string? DisabledReason);
```

```csharp
namespace RelayBench.Core.Models;

public sealed record ClientApplyTargetSelection(
    string TargetId,
    ClientApplyProtocolKind Protocol);
```

扩展 `ClientAppApplyResult`：

```csharp
public sealed record ClientAppTargetApplyResult(
    string TargetId,
    string DisplayName,
    ClientApplyProtocolKind Protocol,
    bool Succeeded,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> BackupFiles,
    string? Error);
```

保留旧字段，新增：

```csharp
IReadOnlyList<ClientAppTargetApplyResult> TargetResults
```

为了兼容现有调用，可提供一个旧构造函数或静态工厂，把 `AppliedTargets` 映射到 `TargetResults`。

- [ ] **步骤 4：运行测试验证通过**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ClientApplyTargetModelTests
```

预期：测试通过。

- [ ] **步骤 5：Commit**

```powershell
git add RelayBench.Core/Models RelayBench.Core.Tests
git commit -m "feat(应用接入): 添加应用目标协议模型"
```

## 任务 3：构建待应用目标 Planner

**文件：**

- 创建：`RelayBench.Core/Services/ClientAppApplyPlanner.cs`
- 修改：`RelayBench.Core/Services/ClientApiDiagnosticsService.cs`
- 测试：`RelayBench.Core.Tests/ClientAppApplyPlannerTests.cs`

- [ ] **步骤 1：编写失败测试**

```csharp
[Fact]
public void PlannerReturnsCodexAsResponsesTargetsWhenResponsesSupported()
{
    var planner = new ClientAppApplyPlanner();
    var targets = planner.BuildTargets(new ClientAppApplyPlanContext(
        BaseUrl: "https://relay.example/v1",
        ApiKey: "sk-test",
        Model: "gpt-4.1-mini",
        ResponsesSupported: true,
        OpenAiCompatibleSupported: true,
        AnthropicSupported: false,
        InstalledClientNames: ["Codex CLI", "Codex Desktop", "VSCode Codex", "Claude CLI"]));

    Assert.Contains(targets, target => target.Id == "codex-cli" && target.Protocol == ClientApplyProtocolKind.Responses && target.IsSelectable);
    Assert.Contains(targets, target => target.Id == "claude-cli" && target.Protocol == ClientApplyProtocolKind.Anthropic && !target.IsSelectable);
}
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ClientAppApplyPlannerTests
```

预期：Planner 类型不存在。

- [ ] **步骤 3：实现 Planner**

Planner 输入：

```csharp
public sealed record ClientAppApplyPlanContext(
    string BaseUrl,
    string ApiKey,
    string Model,
    bool ResponsesSupported,
    bool OpenAiCompatibleSupported,
    bool AnthropicSupported,
    IReadOnlySet<string> InstalledClientNames);
```

Planner 输出规则：

- Codex CLI、Codex Desktop、VSCode Codex：
  - 协议：`Responses`
  - 默认选中：`ResponsesSupported == true`
  - 不可选原因：`当前接口未通过 Responses API 探测`
- Claude CLI：
  - 协议：`Anthropic`
  - 默认选中：`AnthropicSupported == true`
  - 不可选原因：`当前接口不是 Anthropic 协议`
- OpenAI 兼容目标：
  - 协议：`OpenAiCompatible`
  - 默认选中：`OpenAiCompatibleSupported == true`
  - 先只在 Planner 中定义能力，不急着接入不明确的软件写入。

- [ ] **步骤 4：运行测试验证通过**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ClientAppApplyPlannerTests
```

预期：测试通过。

- [ ] **步骤 5：Commit**

```powershell
git add RelayBench.Core/Services/ClientAppApplyPlanner.cs RelayBench.Core/Services/ClientApiDiagnosticsService.cs RelayBench.Core.Tests
git commit -m "feat(应用接入): 生成可应用软件目标"
```

## 任务 4：新增目标选择弹窗 ViewModel

**文件：**

- 创建：`RelayBench.App/ViewModels/ClientApplyTargetItemViewModel.cs`
- 创建：`RelayBench.App/ViewModels/MainWindowViewModel.ClientApplyTargetDialog.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.CommandBindings.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.Construction.cs`
- 测试：`RelayBench.App.Tests/ViewModels/ClientApplyTargetDialogTests.cs`

- [ ] **步骤 1：编写失败测试**

```csharp
[Fact]
public async Task DialogReturnsOnlyCheckedSelectableTargets()
{
    var vm = new MainWindowViewModel();
    var task = vm.ShowClientApplyTargetDialogForTestAsync([
        new ClientApplyTarget("codex-cli", "Codex CLI", ClientApplyProtocolKind.Responses, true, true, true, "~/.codex/config.toml", null),
        new ClientApplyTarget("claude-cli", "Claude CLI", ClientApplyProtocolKind.Anthropic, true, false, false, "~/.claude/settings.json", "当前接口不是 Anthropic 协议")
    ]);

    vm.ClientApplyTargetItems.Single(item => item.TargetId == "codex-cli").IsSelected = true;
    await vm.ConfirmClientApplyTargetDialogCommand.ExecuteAsync(null);

    var selected = await task;
    Assert.Single(selected);
    Assert.Equal("codex-cli", selected[0].TargetId);
}
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ClientApplyTargetDialogTests
```

预期：弹窗 ViewModel 不存在。

- [ ] **步骤 3：实现 ViewModel**

关键属性：

```csharp
public ObservableCollection<ClientApplyTargetItemViewModel> ClientApplyTargetItems { get; } = [];
public bool IsClientApplyTargetDialogOpen { get; private set; }
public string ClientApplyTargetDialogTitle { get; private set; } = "应用到软件";
public string ClientApplyTargetDialogSummary { get; private set; } = string.Empty;
public bool HasSelectableClientApplyTargets => ClientApplyTargetItems.Any(item => item.IsSelectable);
public bool HasSelectedClientApplyTargets => ClientApplyTargetItems.Any(item => item.IsSelectable && item.IsSelected);
```

关键命令：

```csharp
public AsyncRelayCommand SelectAllClientApplyTargetsCommand { get; }
public AsyncRelayCommand InvertClientApplyTargetsCommand { get; }
public AsyncRelayCommand ConfirmClientApplyTargetDialogCommand { get; }
public AsyncRelayCommand CancelClientApplyTargetDialogCommand { get; }
```

确认时返回：

```csharp
ClientApplyTargetItems
    .Where(item => item.IsSelectable && item.IsSelected)
    .Select(item => new ClientApplyTargetSelection(item.TargetId, item.Protocol))
    .ToList();
```

- [ ] **步骤 4：运行测试验证通过**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ClientApplyTargetDialogTests
```

预期：测试通过。

- [ ] **步骤 5：Commit**

```powershell
git add RelayBench.App/ViewModels RelayBench.App.Tests
git commit -m "feat(应用接入): 添加软件目标选择弹窗状态"
```

## 任务 5：新增目标选择弹窗 UI

**文件：**

- 修改：`RelayBench.App/MainWindow.xaml`
- 测试：手动 UI 验证

- [ ] **步骤 1：添加 Overlay**

在现有 Overlay 区域新增：

```xml
<Grid x:Name="ClientApplyTargetOverlay"
      Panel.ZIndex="20"
      Background="#660F172A"
      Visibility="Collapsed">
    <Border x:Name="ClientApplyTargetOverlayPanel"
            Width="760"
            MaxHeight="680"
            Padding="14"
            Background="{StaticResource PanelBrushStrong}"
            BorderBrush="{StaticResource PanelBorderBrush}"
            BorderThickness="1.5"
            CornerRadius="18"
            HorizontalAlignment="Center"
            VerticalAlignment="Center">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>

            <StackPanel>
                <TextBlock Style="{StaticResource DialogTitleTextStyle}"
                           Text="{Binding ClientApplyTargetDialogTitle}" />
                <TextBlock Margin="0,6,0,0"
                           Style="{StaticResource SectionHintTextStyle}"
                           TextWrapping="Wrap"
                           Text="{Binding ClientApplyTargetDialogSummary}" />
            </StackPanel>

            <ListBox Grid.Row="1"
                     Margin="0,12,0,0"
                     ItemsSource="{Binding ClientApplyTargetItems}"
                     BorderThickness="0"
                     Background="Transparent"
                     HorizontalContentAlignment="Stretch">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <Border Margin="0,0,0,8"
                                Padding="10"
                                Background="{StaticResource PanelBrush}"
                                BorderBrush="{StaticResource PanelBorderBrush}"
                                BorderThickness="1"
                                CornerRadius="10">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                </Grid.ColumnDefinitions>
                                <CheckBox VerticalAlignment="Top"
                                          IsEnabled="{Binding IsSelectable}"
                                          IsChecked="{Binding IsSelected, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
                                <StackPanel Grid.Column="1" Margin="10,0,0,0">
                                    <TextBlock FontWeight="SemiBold"
                                               Foreground="{StaticResource TextBrush}"
                                               Text="{Binding DisplayName}" />
                                    <TextBlock Margin="0,4,0,0"
                                               Style="{StaticResource SectionHintTextStyle}"
                                               Text="{Binding ProtocolText}" />
                                    <TextBlock Margin="0,4,0,0"
                                               Style="{StaticResource SectionHintTextStyle}"
                                               Text="{Binding ConfigSummary}" />
                                    <TextBlock Margin="0,4,0,0"
                                               Foreground="#B54708"
                                               TextWrapping="Wrap"
                                               Text="{Binding DisabledReason}" />
                                </StackPanel>
                            </Grid>
                        </Border>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>

            <WrapPanel Grid.Row="2"
                       Margin="0,12,0,0"
                       HorizontalAlignment="Right">
                <Button MinWidth="88"
                        Command="{Binding SelectAllClientApplyTargetsCommand}"
                        Content="全选" />
                <Button MinWidth="88"
                        Margin="8,0,0,0"
                        Command="{Binding InvertClientApplyTargetsCommand}"
                        Content="反选" />
                <Button MinWidth="108"
                        Margin="8,0,0,0"
                        Command="{Binding ConfirmClientApplyTargetDialogCommand}"
                        Style="{StaticResource PrimaryActionButtonStyle}"
                        Content="应用到所选" />
                <Button MinWidth="88"
                        Margin="8,0,0,0"
                        Command="{Binding CancelClientApplyTargetDialogCommand}"
                        Content="取消" />
            </WrapPanel>
        </Grid>
    </Border>
</Grid>
```

- [ ] **步骤 2：注册动画**

在 `MainWindow.xaml.cs` 的 `InitializeOverlayAnimations()` 中注册：

```csharp
_overlayAnimations[nameof(MainWindowViewModel.IsClientApplyTargetDialogOpen)] =
    new OverlayAnimationState(ClientApplyTargetOverlay, ClientApplyTargetOverlayPanel, static viewModel => viewModel.IsClientApplyTargetDialogOpen);
```

- [ ] **步骤 3：手动验证**

运行：

```powershell
dotnet build .\RelayBenchSuite.slnx -c Debug -v minimal
```

预期：XAML 编译通过，打开弹窗时列表不重叠，窗口化和全屏均可滚动。

- [ ] **步骤 4：Commit**

```powershell
git add RelayBench.App/MainWindow.xaml RelayBench.App/MainWindow.xaml.cs
git commit -m "feat(应用接入): 添加应用目标选择弹窗"
```

## 任务 6：重构 Codex Responses 写入为可选目标

**文件：**

- 创建：`RelayBench.Core/Services/CodexResponsesConfigApplyAdapter.cs`
- 修改：`RelayBench.Core/Services/CodexFamilyConfigApplyService.cs`
- 测试：`RelayBench.Core.Tests/CodexResponsesConfigApplyAdapterTests.cs`

- [ ] **步骤 1：编写失败测试**

```csharp
[Fact]
public async Task AdapterWritesCodexConfigOnlyForSelectedCodexTargets()
{
    var environment = new FakeClientApiConfigMutationEnvironment();
    var adapter = new CodexResponsesConfigApplyAdapter(environment);

    var result = await adapter.ApplyAsync(
        new ClientApplyEndpoint("https://relay.example/v1", "sk-test", "gpt-4.1-mini", "RelayBench", 128000, "responses"),
        [new ClientApplyTargetSelection("codex-cli", ClientApplyProtocolKind.Responses)]);

    Assert.True(result.Succeeded);
    Assert.Contains(result.TargetResults, item => item.TargetId == "codex-cli" && item.Succeeded);
    Assert.Contains("wire_api = \"responses\"", environment.ReadText("%USERPROFILE%/.codex/config.toml"));
}
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter CodexResponsesConfigApplyAdapterTests
```

预期：Adapter 类型不存在。

- [ ] **步骤 3：拆出 Codex 写入**

保持现有 `CodexFamilyConfigApplyService.ApplyAsync(...)` 对外兼容，但内部调用新 Adapter。Adapter 需要接受目标选择，按以下规则产出逐目标结果：

- Codex CLI、Codex Desktop、VSCode Codex 当前仍写同一个 `~/.codex/config.toml`，但结果中只记录用户勾选的软件。
- 如果多个 Codex 目标同时勾选，只写一次文件，`TargetResults` 中分别标记成功。
- 如果没有勾选任何 Codex 目标，Adapter 返回跳过。

- [ ] **步骤 4：运行测试验证通过**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter CodexResponsesConfigApplyAdapterTests
```

预期：测试通过。

- [ ] **步骤 5：Commit**

```powershell
git add RelayBench.Core/Services RelayBench.Core.Tests
git commit -m "refactor(应用接入): 支持按目标应用 Codex 配置"
```

## 任务 7：新增 Anthropic 写入适配器

**文件：**

- 创建：`RelayBench.Core/Services/AnthropicClientConfigApplyAdapter.cs`
- 测试：`RelayBench.Core.Tests/AnthropicClientConfigApplyAdapterTests.cs`

- [ ] **步骤 1：编写失败测试**

```csharp
[Fact]
public async Task AdapterWritesClaudeSettingsWithAnthropicEndpoint()
{
    var environment = new FakeClientApiConfigMutationEnvironment();
    var adapter = new AnthropicClientConfigApplyAdapter(environment);

    var result = await adapter.ApplyAsync(
        new ClientApplyEndpoint("https://anthropic-relay.example", "sk-ant-test", "claude-3-5-sonnet", "RelayBench Claude", null, null),
        [new ClientApplyTargetSelection("claude-cli", ClientApplyProtocolKind.Anthropic)]);

    Assert.True(result.Succeeded);
    var settings = environment.ReadText("%USERPROFILE%/.claude/settings.json");
    Assert.Contains("ANTHROPIC_API_KEY", settings);
    Assert.Contains("ANTHROPIC_BASE_URL", settings);
}
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter AnthropicClientConfigApplyAdapterTests
```

预期：Adapter 类型不存在。

- [ ] **步骤 3：实现 Claude CLI 写入**

建议写入 `.claude/settings.json` 的 `env` 区域：

```json
{
  "env": {
    "ANTHROPIC_API_KEY": "<当前 API Key>",
    "ANTHROPIC_BASE_URL": "<当前 Base URL>",
    "ANTHROPIC_MODEL": "<当前模型>"
  }
}
```

注意：

- 如果现有文件有其他设置，必须保留。
- 修改前创建备份。
- 如果目标软件不支持 `ANTHROPIC_MODEL`，该字段可作为可选项；第一版建议写入，因为用户当前接口已经明确选择模型。
- 不写 `OPENAI_API_KEY`，不写 `/v1/chat/completions`。

- [ ] **步骤 4：运行测试验证通过**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter AnthropicClientConfigApplyAdapterTests
```

预期：测试通过。

- [ ] **步骤 5：Commit**

```powershell
git add RelayBench.Core/Services RelayBench.Core.Tests
git commit -m "feat(应用接入): 支持 Anthropic 客户端写入"
```

## 任务 8：接入「应用接入」按钮

**文件：**

- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ApplicationCenter.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.CodexApplyGuards.cs`
- 测试：`RelayBench.App.Tests/ViewModels/ApplicationCenterApplyFlowTests.cs`

- [ ] **步骤 1：编写失败测试**

```csharp
[Fact]
public async Task ApplyCurrentInterfaceUsesSelectedTargets()
{
    var vm = new MainWindowViewModel();
    vm.ProxyBaseUrl = "https://relay.example/v1";
    vm.ProxyApiKey = "sk-test";
    vm.ProxyModel = "gpt-4.1-mini";

    vm.SetClientApplyTargetDialogSelectionForTest([
        new ClientApplyTargetSelection("codex-cli", ClientApplyProtocolKind.Responses)
    ]);

    await vm.ApplyCurrentInterfaceToCodexAppsCommand.ExecuteAsync(null);

    Assert.Contains("Codex CLI", vm.StatusMessage);
    Assert.DoesNotContain("VSCode Codex", vm.StatusMessage);
}
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ApplicationCenterApplyFlowTests
```

预期：现有逻辑仍固定写入 Codex 系列，测试失败。

- [ ] **步骤 3：替换确认流程**

把现有：

```csharp
var confirmed = await ShowConfirmationDialogAsync(...);
```

替换为：

```csharp
var targets = await BuildClientApplyTargetsForCurrentInterfaceAsync(settings);
var selectedTargets = await ShowClientApplyTargetDialogAsync(
    "应用当前接口到软件",
    BuildCurrentInterfaceApplySummary(settings),
    targets);
if (selectedTargets.Count == 0)
{
    StatusMessage = "已取消本次应用。";
    return;
}
```

然后调用统一应用服务：

```csharp
var result = await _clientAppConfigApplyService.ApplyAsync(
    BuildClientApplyEndpointFromCurrentInterface(cachedApplyInfo),
    selectedTargets);
```

- [ ] **步骤 4：保留聊天合并确认**

只有当 `selectedTargets` 中包含 Codex Responses 目标时，才弹出「是否合并所有 Codex 聊天记录」：

```csharp
var containsCodexTarget = selectedTargets.Any(target => target.Protocol == ClientApplyProtocolKind.Responses);
var shouldMergeChats = containsCodexTarget &&
    await ConfirmCodexChatMergeAsync(CodexChatMergeTarget.ThirdPartyCustom, "切到第三方");
```

- [ ] **步骤 5：运行测试验证通过**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ApplicationCenterApplyFlowTests
```

预期：只应用用户勾选目标。

- [ ] **步骤 6：Commit**

```powershell
git add RelayBench.App/ViewModels
git commit -m "feat(应用接入): 当前接口按勾选目标应用"
```

## 任务 9：接入「批量测试」应用按钮

**文件：**

- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.BatchApply.cs`
- 测试：`RelayBench.App.Tests/ViewModels/BatchApplyFlowTests.cs`

- [ ] **步骤 1：编写失败测试**

```csharp
[Fact]
public async Task BatchRankingApplyUsesSameTargetDialog()
{
    var vm = new MainWindowViewModel();
    var row = new ProxyBatchRankingRowViewModel
    {
        EntryName = "入口 A",
        BaseUrl = "https://relay.example/v1",
        ApiKey = "sk-test",
        Model = "gpt-4.1-mini"
    };

    vm.SetClientApplyTargetDialogSelectionForTest([
        new ClientApplyTargetSelection("codex-desktop", ClientApplyProtocolKind.Responses)
    ]);

    await vm.ApplyRankingRowToCodexAppsCommand.ExecuteAsync(row);

    Assert.Contains("Codex Desktop", vm.StatusMessage);
}
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter BatchApplyFlowTests
```

预期：现有逻辑仍固定应用 Codex 系列。

- [ ] **步骤 3：复用任务 8 的选择流程**

批量入口和当前接口入口必须调用同一组方法：

```csharp
var targets = await BuildClientApplyTargetsForEndpointAsync(row.BaseUrl, row.ApiKey, row.Model);
var selectedTargets = await ShowClientApplyTargetDialogAsync(
    $"应用“{row.EntryName}”到软件",
    BuildBatchRankingApplySummary(row),
    targets);
```

应用成功后仍更新当前接口：

```csharp
ProxyBaseUrl = row.BaseUrl;
ProxyApiKey = row.ApiKey;
ProxyModel = row.Model;
SaveState();
```

- [ ] **步骤 4：运行测试验证通过**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter BatchApplyFlowTests
```

预期：批量入口复用同一目标弹窗，并只应用勾选目标。

- [ ] **步骤 5：Commit**

```powershell
git add RelayBench.App/ViewModels/MainWindowViewModel.BatchApply.cs RelayBench.App.Tests
git commit -m "feat(批量评测): 入口按勾选软件应用"
```

## 任务 10：结果展示和错误处理

**文件：**

- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ApplicationCenter.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.BatchApply.cs`
- 修改：`RelayBench.Core/Models/ClientAppApplyResult.cs`
- 测试：`RelayBench.App.Tests/ViewModels/ClientApplyResultFormattingTests.cs`

- [ ] **步骤 1：编写失败测试**

```csharp
[Fact]
public void ResultSummaryListsEachTargetStatus()
{
    var result = new ClientAppApplyResult(
        Succeeded: false,
        Summary: "部分目标应用失败",
        ChangedFiles: [],
        BackupFiles: [],
        AppliedTargets: ["Codex CLI"],
        Error: "Claude CLI 写入失败",
        TargetResults:
        [
            new("codex-cli", "Codex CLI", ClientApplyProtocolKind.Responses, true, ["config.toml"], ["config.toml.bak"], null),
            new("claude-cli", "Claude CLI", ClientApplyProtocolKind.Anthropic, false, [], [], "权限不足")
        ]);

    var summary = MainWindowViewModel.BuildClientApplyResultSummaryForTest(result);

    Assert.Contains("Codex CLI：成功", summary);
    Assert.Contains("Claude CLI：失败", summary);
}
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ClientApplyResultFormattingTests
```

预期：格式化方法不存在。

- [ ] **步骤 3：实现结果格式化**

摘要格式：

```text
应用结果：
- Codex CLI：成功
- Claude CLI：失败（权限不足）

更新文件：
...

备份文件：
...
```

状态栏：

- 全部成功：`已应用到：Codex CLI、Claude CLI`
- 部分失败：`部分软件应用失败：Claude CLI`
- 全部失败：`应用失败：没有软件写入成功`

- [ ] **步骤 4：运行测试验证通过**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug --filter ClientApplyResultFormattingTests
```

预期：测试通过。

- [ ] **步骤 5：Commit**

```powershell
git add RelayBench.App/ViewModels RelayBench.Core/Models RelayBench.App.Tests
git commit -m "feat(应用接入): 展示逐软件应用结果"
```

## 任务 11：完整验证

**文件：**

- 无新增文件。

- [ ] **步骤 1：运行单元测试**

```powershell
dotnet test .\RelayBenchSuite.slnx -c Debug
```

预期：全部测试通过。

- [ ] **步骤 2：运行 Debug 编译**

```powershell
dotnet build .\RelayBenchSuite.slnx -c Debug -v minimal
```

预期：0 错误。

- [ ] **步骤 3：运行 Release 编译**

```powershell
dotnet build .\RelayBenchSuite.slnx -c Release -v minimal
```

预期：0 错误。

- [ ] **步骤 4：手动验证 UI**

验证清单：

- 在「单站测试」修改 Base URL、API Key、模型，「应用接入」当前接口区域同步变化。
- 点击「应用接入」的「应用当前接口」，弹窗列出 Codex CLI、Codex Desktop、VSCode Codex、Claude CLI 等已发现软件。
- 取消全部勾选时，「应用到所选」不可用或点击后提示至少选择一个软件。
- 勾选 Codex CLI 后只写入 Codex 相关配置。
- 勾选 Claude CLI 时只写入 Anthropic 配置，不改 OpenAI / Responses 配置。
- 从「批量评测」某个入口点击「应用到软件」，弹出同一个选择弹窗，并使用该入口的 Base URL、API Key、模型。
- 应用完成后，结果区展示每个软件的成功或失败状态。

- [ ] **步骤 5：提交收尾**

```powershell
git status --short
git add RelayBench.Core RelayBench.App RelayBench.Core.Tests RelayBench.App.Tests
git commit -m "feat(应用接入): 支持勾选软件并按协议应用"
```

## 风险与边界

- Codex CLI、Codex Desktop、VSCode Codex 当前共用 `~/.codex/config.toml`，即使弹窗展示为 3 个软件，底层文件仍可能是同一份。实现时必须在 UI 上提示「共用配置」。
- OpenAI 兼容目标如果没有明确软件配置格式，第一版只做协议能力和 Planner 支持，不强行写入未知软件。
- Anthropic 目标必须先做最小探测或明确标记，否则容易把 OpenAI 兼容接口误写到 Claude CLI。
- 所有写入操作必须复用现有备份机制，不允许直接覆盖配置文件。
- 如果目标软件未安装，可以显示但不默认勾选；如果用户看不到未安装软件更清爽，也可以只显示已安装项，但文案要说明「只列出本机已发现软件」。

## 自检

- 需求 1「应用接入当前接口跟随单站测试变化」由任务 1 覆盖。
- 需求 2「两个应用按钮弹窗列出所有待应用软件并可勾选」由任务 4、5、8、9 覆盖。
- Anthropic、OpenAI 兼容、Responses 3 种应用方式由「协议应用规则」和任务 3、6、7 覆盖。
- 未写入未定义软件的 OpenAI 兼容配置，避免把未知配置格式做坏。
- 每个实现任务都有文件路径、测试命令和预期结果。
