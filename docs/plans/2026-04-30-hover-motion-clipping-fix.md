# Hover 放大裁切问题施工文档

## 目标

修复全项目鼠标悬停后控件放大被相邻模块边界裁切的问题，同时保留「高级、灵动、有反馈」的交互观感。

本次不建议继续给所有控件加外边距或提高 `Panel.ZIndex`。根因是 WPF 的 `RenderTransform` 放大不参与布局计算，控件视觉尺寸变大后，父容器、相邻面板、滚动区域或模板边界不会给它重新让位。局部加 `ZIndex` 只能解决同一层级遮挡，不能解决父容器裁切，也会引入新的覆盖问题。

## 当前问题定位

主要风险集中在 [WorkbenchTheme.xaml](../../RelayBench.App/Resources/WorkbenchTheme.xaml) 和少量页面局部样式中。

已发现的放大来源：

- 全局 `Button` 模板：悬停放大到 `1.038`，按下回弹仍回到 `1.038`。
- 全局 `TextBox` / `PasswordBox`：悬停放大到 `1.01`。
- 全局 `ListBoxItemStyle`：悬停放大到 `1.01`。
- `PresetCardListBoxItemStyle`：悬停放大到 `1.014`。
- `NavigationListBoxItemStyle`：悬停放大到 `1.012`，截图里的左侧导航裁切属于这一类。
- `ComboBox`、`Expander`、`TabItem`、图表按钮等局部样式存在 `1.008` 到 `1.08` 的放大。
- [BatchComparisonPage.xaml](../../RelayBench.App/Views/Pages/BatchComparisonPage.xaml) 内有局部按钮样式放大到 `1.08`，风险最高。

## 设计原则

### 1. 密集工作台控件不再外扩

导航、菜单、列表、输入框、Tab、普通按钮属于高密度工作台控件。它们的 hover 反馈应采用不改变视觉占位的方式：

- 背景变浅或变深。
- 边框变成品牌蓝。
- 左侧指示条、底部指示线或内发光。
- 阴影轻微增强。
- 内容透明度或颜色变化。

这些效果都在控件原本边界内发生，不会撞到父容器边缘。

### 2. 按下态允许缩小，不允许悬停放大

按下态可以使用 `ScaleTransform` 小幅缩小，例如 `0.96` 到 `0.99`。缩小不会溢出容器，也能保留「按下去」的手感。

悬停态统一不再使用 `ScaleX > 1` / `ScaleY > 1`，除非该控件位于独立浮层、图表画布或有明确安全外扩空间。

### 3. 视觉层级由颜色和阴影表达

替代方案：

- 普通 hover：`Background="#F8FBFF"`，`BorderBrush="#93C5FD"`。
- 选中态：保留蓝色渐变背景、左侧蓝色指示条。
- 主要按钮 hover：增强蓝色深浅或阴影，不放大。
- 卡片 hover：使用 `ShadowMd` 或边框高亮，不放大。

## 推荐施工方案

采用「全局减法 + 局部保留」方案。

核心思路：

1. 移除全局普通控件的 hover 放大动画。
2. 保留按下态缩小动画。
3. 将 hover 反馈改成边框、背景、阴影、指示线。
4. 对局部大放大样式做专项收敛。

这样一次能解决全项目大部分裁切问题，并且不会增加页面空白。

## 修改范围

### 1. 全局按钮

文件：[WorkbenchTheme.xaml](../../RelayBench.App/Resources/WorkbenchTheme.xaml)

处理对象：

- 全局 `Style TargetType="Button"`。
- `PrimaryActionButtonStyle`。
- `ToolbarIconButtonStyle` 继承链。
- 其他基于全局按钮样式的按钮。

施工动作：

- 删除 `Mouse.MouseEnter` 中把 `ButtonScale` 动画到 `1.038` 的逻辑。
- 删除 `Mouse.MouseLeave` 中恢复 `1` 的悬停配套动画。
- 保留 `IsPressed` 中缩小到 `0.948` 的逻辑，但 `Trigger.ExitActions` 回到 `1`，不要回到 `1.038`。
- hover 只修改 `ButtonBorder.Background`、`ButtonBorder.BorderBrush` 和可选阴影。

建议效果：

```xml
<Trigger Property="IsMouseOver" Value="True">
    <Setter TargetName="ButtonBorder" Property="Background" Value="#F8FBFF" />
    <Setter TargetName="ButtonBorder" Property="BorderBrush" Value="#93C5FD" />
</Trigger>
```

### 2. 输入框和密码框

文件：[WorkbenchTheme.xaml](../../RelayBench.App/Resources/WorkbenchTheme.xaml)

处理对象：

- 全局 `TextBox` 模板。
- 全局 `PasswordBox` 模板。

施工动作：

- 删除 hover 放大到 `1.01` 的动画。
- 保留聚焦边框和聚焦光环。
- hover 只变边框，不改变尺寸。

理由：

输入框通常贴近表单网格，放大后最容易压到相邻列、按钮和滚动条。

### 3. 导航项

文件：[WorkbenchTheme.xaml](../../RelayBench.App/Resources/WorkbenchTheme.xaml)

处理对象：

- `NavigationListBoxItemStyle`。

施工动作：

- 删除 `Mouse.MouseEnter` 中 `ItemScale` 到 `1.012` 的动画。
- 删除 `Mouse.MouseLeave` 中恢复动画。
- 保留 `PreviewMouseDown` 缩小到 `0.992`，但松手回到 `1`。
- hover 用浅蓝背景，选中态继续使用左侧蓝色指示条。

预期结果：

截图里的「单站测试」不会再被导航面板右侧边界裁切，蓝框仍然清晰。

### 4. 普通列表项和预设卡片

文件：[WorkbenchTheme.xaml](../../RelayBench.App/Resources/WorkbenchTheme.xaml)

处理对象：

- `ListBoxItemStyle`。
- `PresetCardListBoxItemStyle`。

施工动作：

- 删除 hover 放大到 `1.01` / `1.014`。
- 使用背景、边框和阴影表达 hover。
- 对会话列表、历史接口、模型列表、应用列表统一受益。

### 5. Tab、ComboBox、Expander

文件：[WorkbenchTheme.xaml](../../RelayBench.App/Resources/WorkbenchTheme.xaml)

处理对象：

- `TabItem` 模板。
- `ComboBox` 模板。
- `Expander` Header 模板。

施工动作：

- 将 hover 放大改为不外扩的颜色/边框反馈。
- ComboBox 下拉打开态可以保留阴影和边框加深，不做整体放大。
- Expander 按下态可以微缩，hover 不放大。

### 6. 页面局部样式专项处理

文件：[BatchComparisonPage.xaml](../../RelayBench.App/Views/Pages/BatchComparisonPage.xaml)

处理对象：

- 局部按钮样式中 `To="1.08"` 的 hover 动画。

施工动作：

- 取消 `1.08` 放大。
- 改成蓝色描边、背景变化或图标亮度变化。
- 按下态可保留 `0.9` 到 `0.96` 的缩小，但松手回到 `1`。

理由：

`1.08` 在密集表格/卡片里视觉外扩明显，是最容易被裁的局部动效。

## 可选保留范围

以下场景可以保留极小尺度变化，但需要逐项确认父容器不裁切：

- 独立图标按钮，且外层有足够留白。
- 首页品牌 Logo 或顶栏独立按钮。
- 不在滚动容器、列表容器、表格单元格里的大型展示卡片。

即使保留，也建议最大不超过 `1.006`，并优先使用阴影替代。

## 验收标准

### 视觉验收

- 左侧工作台导航悬停和选中时不被右边界裁切。
- 大模型对话会话列表、历史接口列表、应用列表悬停时不被卡片边界裁切。
- 单站测试、批量预测、应用接入、网络复核、历史报告的按钮 hover 不越界。
- 仍能明显感知 hover 状态，不出现「没反馈」。
- 选中态和 hover 态层级清楚：选中态优先于 hover。

### 技术验收

搜索以下模式，确认高风险 hover 放大已清理：

```powershell
Get-ChildItem -Path RelayBench.App -Recurse -Include *.xaml |
  Select-String -Pattern 'Mouse\.MouseEnter|To="1\.0[1-9]|ScaleX|ScaleY'
```

允许存在：

- 按下态小于 `1` 的缩小动画。
- 加载入场动画使用 `TranslateTransform`。
- 特殊展示区经过人工确认的极小安全动效。

不允许存在：

- 通用控件 hover 放大超过 `1`。
- 局部按钮 hover 放大到 `1.08`。
- 松手后回弹到大于 `1`。

### 构建验收

必须运行：

```powershell
dotnet build H:\nettest\RelayBenchSuite.slnx
dotnet build H:\nettest\RelayBenchSuite.slnx -c Release
git diff --check
```

预期结果：

- Debug 构建 0 错误。
- Release 构建 0 错误。
- `git diff --check` 无空白错误，仅允许 CRLF 提示。

## 回归检查页面

至少检查以下页面：

- 单站测试：左侧导航、顶部按钮、测试模式按钮、结果区按钮。
- 大模型对话：会话列表、发送按钮、参数按钮、右侧弹出面板。
- 批量预测：快速对比、深度测试、放大图表按钮、应用按钮。
- 应用接入：历史接口、拉取模型、应用当前接口、应用列表。
- 网络复核：工具子菜单、各检测页按钮、Tab、日志框。
- 历史报告：报告列表、导出按钮、详情区。

## 风险与规避

### 风险 1：动效变弱

规避方式：

- 用更明确的边框、背景、指示条和阴影替代几何放大。
- 主要按钮 hover 使用颜色增强，保留按下缩小。

### 风险 2：局部样式遗漏

规避方式：

- 用 `Select-String` 扫描 `ScaleTransform` 和 `To="1.0"`。
- 对所有命中的 `Mouse.MouseEnter` 逐个判断是否属于 hover 放大。

### 风险 3：选中态被 hover 态覆盖

规避方式：

- 在模板触发器中让 `IsSelected` 触发器排在 `IsMouseOver` 后面。
- 导航、列表、Tab 的选中态统一使用更强的背景和边框。

## 建议实施顺序

1. 修改全局 `Button`、`TextBox`、`PasswordBox`。
2. 修改全局 `ListBoxItemStyle` 和 `PresetCardListBoxItemStyle`。
3. 修改 `NavigationListBoxItemStyle`，优先验证截图问题。
4. 修改 `ComboBox`、`TabItem`、`Expander`。
5. 清理 `BatchComparisonPage.xaml` 的局部 `1.08` 放大。
6. 全项目扫描剩余 `ScaleTransform`，只保留低风险或非 hover 用途。
7. 构建并进行页面回归检查。

## 最终效果

整体交互从「控件外扩放大」改为「原位高亮 + 边框强化 + 阴影层级 + 按下微缩」。这样更接近成熟桌面工作台产品的交互方式，也能彻底避开边界裁切、相邻模块遮挡和滚动容器裁剪问题。
