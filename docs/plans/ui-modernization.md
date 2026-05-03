# RelayBench UI 现代化改造施工文档

> 版本: v1.0 | 日期: 2026-04-30 | 状态: 待审

---

## 一、改造目标

将 RelayBench WPF 工作台的视觉风格从"紧凑功能型"升级为"大气现代型"，在不改变功能逻辑的前提下，通过字号、间距、阴影、渐变、微交互五个维度的系统性调整，提升整体品质感。

**核心原则**：只改样式层（XAML + Style），不改 ViewModel 和 Code-Behind。

---

## 二、现状分析

### 2.1 文件清单与体量

| 文件 | 行数 | 职责 |
|------|------|------|
| `Resources/WorkbenchTheme.xaml` | 2157 | 全局主题：颜色、字号、控件模板、动画 |
| `MainWindow.xaml` | 2761 | 主窗口壳：标题栏、导航栏、页面容器、弹窗 |
| `Views/Pages/SingleStationPage.xaml` | 913 | 单站测试页 |
| `Views/Pages/ModelChatPage.xaml` | 641 | 大模型对话页 |
| `Views/Pages/BatchComparisonPage.xaml` | 1142 | 批量评测页 |
| `Views/Pages/ApplicationCenterPage.xaml` | 427 | 应用接入页 |
| `Views/Pages/NetworkReviewPage.xaml` | 1593 | 网络复核页 |
| `Views/Pages/HistoryReportsPage.xaml` | 296 | 历史报告页 |

### 2.2 现有设计体系

```
颜色系统:
  主色:    #2563EB (AccentBrush)
  背景:    #F5F7FA (Window) / 渐变 AppBackgroundBrush
  面板:    #FFFFFF (PanelBrush) / #FBFDFF (PanelBrushStrong)
  边框:    #E2E8F0 (PanelBorderBrush)
  文字:    #0F172A (TextBrush) / #64748B (MutedBrush)
  输入:    #F8FAFC (InputBrush)

字号体系:
  全局:    11px
  标题:    12.8px (SectionTitleTextStyle)
  标签:    10.3px (FieldLabelTextStyle)
  提示:    10px   (SectionHintTextStyle)
  按钮:    10.4px
  对话框:  15.5px (DialogTitleTextStyle)

圆角体系:
  按钮:    12px
  输入框:  12px
  面板:    16px (SectionPanelBorderStyle)
  导航栏:  22px (NavigationRailBorderStyle)
  卡片:    16px (PresetCardListBoxItemStyle)
  窗口:    16px (WindowChrome)

间距体系:
  面板内边距:  9px (SectionPanelBorderStyle)
  导航栏内边距: 14px
  按钮间距:    4px
  列表项间距:  5px (NavigationListBoxItemStyle)
```

### 2.3 已有优势（保留不动）

- 微交互动画：hover scale 1.03x、press scale 0.95x、CubicEase 缓动
- 自定义 ScrollBar：细胶囊样式（8px 宽、圆角 999）
- ComboBox 下拉动画：Opacity + TranslateY + 箭头旋转
- DataGrid 行 hover 横移动画
- Expander 展开/收起旋转动画
- 图表区域扫光动画（LiveChartSweepBorderStyle）

---

## 三、改造方案

### 阶段 A：字号与间距体系（影响最大，风险最低）

**目标**：让界面有"呼吸感"，文字层级更清晰

#### A1. 全局字号提升

文件：`MainWindow.xaml` 第 16 行

```xml
<!-- 改前 -->
FontSize="11"

<!-- 改后 -->
FontSize="13"
```

文件：`Resources/WorkbenchTheme.xaml` 各 Style

| Style Key | 属性 | 改前 | 改后 | 行号参考 |
|-----------|------|------|------|----------|
| (默认 TextBlock) | FontSize | 10.4 | 12 | ~222 |
| SectionTitleTextStyle | FontSize | 12.8 | 16 | ~236 |
| FieldLabelTextStyle | FontSize | 10.3 | 12 | ~229 |
| SectionHintTextStyle | FontSize | 10 | 11.5 | ~243 |
| (默认 Button) | FontSize | 10.4 | 12 | ~271 |
| (默认 TextBox) | FontSize | 10.3 | 12 | ~386 |
| CompactInputTextBoxStyle | FontSize | 10.3 | 12 | ~484 |
| (默认 PasswordBox) | FontSize | 10.3 | 12 | ~498 |
| (默认 ComboBox) | FontSize | 11.1 | 12.5 | ~1252 |
| ComboBoxItemStyle | FontSize | 11.1 | 12.5 | ~1049 |
| (默认 TabItem) | FontSize | 11 | 12.5 | ~1734 |
| (默认 CheckBox) | FontSize | 10.2 | 12 | ~667 |
| DialogTitleTextStyle | FontSize | 15.5 | 18 | ~594 |
| DialogTagTextStyle | FontSize | 10.1 | 11.5 | ~621 |
| ReadOnlyConsoleTextBoxStyle | FontSize | 9.7 | 11 | ~583 |
| DataGridColumnHeader | FontSize | 10.8 | 12 | ~1629 |

#### A2. 间距放大

文件：`Resources/WorkbenchTheme.xaml`

| Style Key | 属性 | 改前 | 改后 |
|-----------|------|------|------|
| SectionPanelBorderStyle | Padding | 9 | 16 |
| InsetPanelBorderStyle | Padding | 8 | 12 |
| NavigationRailBorderStyle | Padding | 14 | 18 |
| NavigationListBoxItemStyle | Padding | 12,10 | 14,12 |
| NavigationListBoxItemStyle | Margin | 0,0,0,5 | 0,0,0,6 |
| (默认 Button) | Padding | 9,3 | 10,5 |
| (默认 Button) | Height | 28 | 32 |
| (默认 Button) | MinWidth | 70 | 80 |
| (默认 TextBox) | Padding | 8,3 | 10,5 |
| CompactInputTextBoxStyle | Height | 30 | 34 |
| (默认 ComboBox) | Padding | 10,4 | 12,6 |
| PrimaryActionButtonStyle | Padding (隐含) | — | 14,6 |
| NavigationAboutBorderStyle | Padding | 10,9 | 12,10 |

文件：`MainWindow.xaml`

| 位置 | 属性 | 改前 | 改后 |
|------|------|------|------|
| 标题栏 Border (~L46) | Padding | 10,6,6,6 | 12,8,8,8 |
| 标题栏高度 (~L42) | RowDefinition | 44 | 48 |
| 主内容区 (~L283) | Margin | 7,6,7,7 | 10,8,10,10 |
| 导航与内容间距 (~L352) | ColumnDefinition Width="6" | 6 | 10 |
| 内容区行间距 (~L425) | RowDefinition Height="Auto" + 间距 | 4 | 8 |

#### A3. 各页面间距同步调整

所有 Page.xaml 中引用 `SectionPanelBorderStyle` 的面板会自动继承 A2 的 Padding 改动。需要手动调整的是页面内部的 `Margin` 值：

| 页面 | 位置 | 属性 | 改前 | 改后 |
|------|------|------|------|------|
| SingleStationPage.xaml | 输入区与模式区间距 (~L52) | Margin | 0,4,0,0 | 0,6,0,0 |
| ModelChatPage.xaml | 三栏间距 (~L47) | ColumnDefinition Width="8" | 8 | 10 |
| ModelChatPage.xaml | 会话列表项 (~L91) | Margin | 0,0,0,6 | 0,0,0,8 |
| BatchComparisonPage.xaml | 表格行间距 | 由 DataGrid 默认继承 | — | — |
| ApplicationCenterPage.xaml | 卡片网格间距 | 由 WrapPanel/UniformGrid 控制 | — | — |
| NetworkReviewPage.xaml | 子菜单项 (~L14) | Margin | 0,0,4,0 | 0,0,6,0 |
| HistoryReportsPage.xaml | 列表项 (~L28) | Margin | 0,0,0,6 | 0,0,0,8 |

---

### 阶段 B：阴影与层次感

**目标**：让面板有"悬浮感"，区分前景与背景

#### B1. 新增阴影资源

文件：`Resources/WorkbenchTheme.xaml`，在颜色定义区（~L2-26）后新增：

```xml
<!-- 阴影系统 -->
<DropShadowEffect x:Key="ShadowSm"
    BlurRadius="12" ShadowDepth="1" Direction="270"
    Color="#0A0F1E" Opacity="0.04" />
<DropShadowEffect x:Key="ShadowMd"
    BlurRadius="24" ShadowDepth="2" Direction="270"
    Color="#0A0F1E" Opacity="0.06" />
<DropShadowEffect x:Key="ShadowLg"
    BlurRadius="32" ShadowDepth="3" Direction="270"
    Color="#0A0F1E" Opacity="0.08" />
<DropShadowEffect x:Key="ShadowXl"
    BlurRadius="48" ShadowDepth="4" Direction="270"
    Color="#0A0F1E" Opacity="0.10" />
```

#### B2. 面板应用阴影

文件：`Resources/WorkbenchTheme.xaml`

| Style Key | 改动 | Effect |
|-----------|------|--------|
| SectionPanelBorderStyle | 新增 Effect | `{StaticResource ShadowMd}` |
| NavigationRailBorderStyle | 新增 Effect | `{StaticResource ShadowLg}` |
| NavigationAboutBorderStyle | 新增 Effect | `{StaticResource ShadowSm}` |
| InsetPanelBorderStyle | 不加阴影 | 保持内嵌感 |

#### B3. 标题栏阴影

文件：`MainWindow.xaml`，标题栏 Border (~L46)：

```xml
<!-- 新增 -->
<Border.Effect>
    <DropShadowEffect BlurRadius="16" ShadowDepth="1" Direction="270"
                      Color="#0A0F1E" Opacity="0.05" />
</Border.Effect>
```

#### B4. 弹窗/浮层阴影

文件：`MainWindow.xaml`，ProxyChartOverlayPanel (~L473) 已有 BorderThickness="1.5"，新增：

```xml
<Border.Effect>
    <DropShadowEffect BlurRadius="48" ShadowDepth="4" Direction="270"
                      Color="#0A0F1E" Opacity="0.12" />
</Border.Effect>
```

文件：`Resources/WorkbenchTheme.xaml`，ComboBox 下拉面板 (~L1335) 已有 DropShadowEffect，参数微调：

```xml
<!-- 改前 -->
BlurRadius="22" ShadowDepth="8" Opacity="0.22"

<!-- 改后 -->
BlurRadius="28" ShadowDepth="6" Opacity="0.18"
```

---

### 阶段 C：渐变与视觉焦点

**目标**：主按钮更有质感，logo 更精致

#### C1. 主按钮渐变

文件：`Resources/WorkbenchTheme.xaml`，PrimaryActionButtonStyle (~L1134)

```xml
<!-- 改前 -->
<Setter Property="Background" Value="{StaticResource AccentBrush}" />
<Setter Property="BorderBrush" Value="#1D4ED8" />

<!-- 改后 -->
<Setter Property="Background">
    <Setter.Value>
        <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
            <GradientStop Color="#3B82F6" Offset="0" />
            <GradientStop Color="#6366F1" Offset="1" />
        </LinearGradientBrush>
    </Setter.Value>
</Setter>
<Setter Property="BorderBrush" Value="#4F46E5" />
```

hover 状态 (~L1199)：

```xml
<!-- 改前 -->
<Setter TargetName="ButtonBorder" Property="Background" Value="#1D4ED8" />
<Setter TargetName="ButtonBorder" Property="BorderBrush" Value="#1E40AF" />

<!-- 改后 -->
<Setter TargetName="ButtonBorder" Property="Background">
    <Setter.Value>
        <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
            <GradientStop Color="#2563EB" Offset="0" />
            <GradientStop Color="#7C3AED" Offset="1" />
        </LinearGradientBrush>
    </Setter.Value>
</Setter>
<Setter TargetName="ButtonBorder" Property="BorderBrush" Value="#5B21B6" />
```

press 状态 (~L1228)：

```xml
<!-- 改前 -->
<Setter TargetName="ButtonBorder" Property="Background" Value="#1E3A8A" />
<Setter TargetName="ButtonBorder" Property="BorderBrush" Value="#1E3A8A" />

<!-- 改后 -->
<Setter TargetName="ButtonBorder" Property="Background">
    <Setter.Value>
        <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
            <GradientStop Color="#1E40AF" Offset="0" />
            <GradientStop Color="#5B21B6" Offset="1" />
        </LinearGradientBrush>
    </Setter.Value>
</Setter>
<Setter TargetName="ButtonBorder" Property="BorderBrush" Value="#4C1D95" />
```

#### C2. 标题栏 Logo 升级

文件：`MainWindow.xaml`，Logo Border (~L83)：

```xml
<!-- 改前 -->
<Border Width="22" Height="22"
        VerticalAlignment="Center"
        Background="{StaticResource AccentBrush}"
        CornerRadius="7">

<!-- 改后 -->
<Border Width="28" Height="28"
        VerticalAlignment="Center"
        CornerRadius="9">
    <Border.Background>
        <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
            <GradientStop Color="#3B82F6" Offset="0" />
            <GradientStop Color="#8B5CF6" Offset="1" />
        </LinearGradientBrush>
    </Border.Background>
    <Border.Effect>
        <DropShadowEffect BlurRadius="12" ShadowDepth="0"
                          Color="#3B82F6" Opacity="0.3" />
    </Border.Effect>
```

对应文字 (~L88)：

```xml
<!-- 改前 -->
FontSize="10.8"

<!-- 改后 -->
FontSize="13"
```

标题文字 (~L96)：

```xml
<!-- 改前 -->
FontSize="12.5"

<!-- 改后 -->
FontSize="14"
```

#### C3. 导航栏选中态渐变

文件：`Resources/WorkbenchTheme.xaml`，NavigationListBoxItemStyle 选中触发器 (~L1913)：

```xml
<!-- 改前 -->
<Setter TargetName="ItemBorder" Property="Background" Value="{StaticResource AccentSoftBrush}" />

<!-- 改后 -->
<Setter TargetName="ItemBorder" Property="Background">
    <Setter.Value>
        <LinearGradientBrush StartPoint="0,0.5" EndPoint="1,0.5">
            <GradientStop Color="#EFF6FF" Offset="0" />
            <GradientStop Color="#DBEAFE" Offset="1" />
        </LinearGradientBrush>
    </Setter.Value>
</Setter>
```

---

### 阶段 D：输入框聚焦光晕

**目标**：输入框聚焦时有蓝色光晕扩散效果

文件：`Resources/WorkbenchTheme.xaml`

#### D1. TextBox 聚焦光晕

在 TextBox 模板的 `IsKeyboardFocusWithin` 触发器 (~L455) 中新增：

```xml
<Trigger Property="IsKeyboardFocusWithin" Value="True">
    <Setter TargetName="InputBorder" Property="BorderBrush" Value="#1570EF" />
    <Setter TargetName="InputFocusRing" Property="Opacity" Value="1" />
    <!-- 新增：光晕效果 -->
    <Setter TargetName="InputBorder" Property="Effect">
        <Setter.Value>
            <DropShadowEffect BlurRadius="14" ShadowDepth="0"
                              Color="#3B82F6" Opacity="0.18" />
        </Setter.Value>
    </Setter>
</Trigger>
```

同时在 `IsMouseOver` 和非聚焦状态中清除 Effect：

```xml
<Trigger Property="IsMouseOver" Value="True">
    <Setter TargetName="InputBorder" Property="Background" Value="#FFFFFF" />
    <Setter TargetName="InputBorder" Property="BorderBrush" Value="#93C5FD" />
    <Setter TargetName="InputBorder" Property="Effect" Value="{x:Null}" />
</Trigger>
```

#### D2. PasswordBox 聚焦光晕

同 D1，在 PasswordBox 模板的 `IsKeyboardFocusWithin` 触发器 (~L546) 中加入相同的 Effect setter。

#### D3. ComboBox 聚焦光晕

在 ComboBox 模板的 `IsKeyboardFocusWithin` 触发器 (~L1391) 中：

```xml
<Trigger Property="IsKeyboardFocusWithin" Value="True">
    <Setter TargetName="ComboBorder" Property="BorderBrush" Value="#1570EF" />
    <!-- 新增 -->
    <Setter TargetName="ComboBorder" Property="Effect">
        <Setter.Value>
            <DropShadowEffect BlurRadius="14" ShadowDepth="0"
                              Color="#3B82F6" Opacity="0.15" />
        </Setter.Value>
    </Setter>
</Trigger>
```

---

### 阶段 E：窗口背景与全局配色微调

**目标**：背景更通透，偏蓝调

#### E1. 窗口背景色

文件：`MainWindow.xaml` 第 14 行：

```xml
<!-- 改前 -->
Background="#F5F7FA"

<!-- 改后 -->
Background="#F0F4FF"
```

#### E2. AppBackgroundBrush 渐变

文件：`Resources/WorkbenchTheme.xaml` (~L3)：

```xml
<!-- 改前 -->
<GradientStop Color="#F8FAFC" Offset="0" />
<GradientStop Color="#EEF4FF" Offset="0.55" />
<GradientStop Color="#F6F7FB" Offset="1" />

<!-- 改后 -->
<GradientStop Color="#EEF2FF" Offset="0" />
<GradientStop Color="#E0E7FF" Offset="0.55" />
<GradientStop Color="#F0F4FF" Offset="1" />
```

---

### 阶段 F：进度条与状态指示器增强

#### F1. 全局任务进度条渐变增强

文件：`MainWindow.xaml`，进度条填充 Border (~L176) 已有渐变，保持不变。
但进度条轨道背景 (~L168) 可微调：

```xml
<!-- 改前 -->
Background="#EAF0FF" BorderBrush="#D6E1FB"

<!-- 改后 -->
Background="#E0EAFF" BorderBrush="#CBD9FF"
```

#### F2. 状态文字字号同步

文件：`MainWindow.xaml`，进度条区域文字：

| 位置 | 属性 | 改前 | 改后 |
|------|------|------|------|
| 任务标题 (~L159) | FontSize | 10.6 | 12 |
| 状态文字 (~L243) | FontSize | 10.2 | 11.5 |
| 底部状态栏文字 (~L338) | FontSize | 默认(11) | 12 |
| 底部运行时间 (~L344) | FontSize | 默认(11) | 12 |

---

## 四、改动不涉及的部分

以下部分**保持原样不动**：

| 类别 | 内容 | 原因 |
|------|------|------|
| ViewModel | 所有 `MainWindowViewModel.*.cs` | 不改业务逻辑 |
| Code-Behind | 所有 `.xaml.cs` | 不改事件处理 |
| 圆角体系 | 12-22px 的 CornerRadius | 已经很现代 |
| 微交互动画 | scale 弹性动画、CubicEase | 已是行业水准 |
| ScrollBar 样式 | 细胶囊滚动条 | 已是现代风格 |
| 颜色主色 | #2563EB 蓝色 | 不换色 |
| 控件模板结构 | ControlTemplate 的 Visual Tree | 只改参数值 |
| 图表渲染服务 | Services/ 下的 ChartRender* | 不改渲染逻辑 |
| MarkdownViewer | Controls/MarkdownViewer.cs | 不改渲染控件 |

---

## 五、实施顺序与依赖

```
阶段 A (字号+间距) ──┐
                      ├── 阶段 B (阴影) ── 阶段 C (渐变) ── 阶段 D (光晕)
                      │
                      └── 阶段 E (背景色) ── 阶段 F (进度条)
```

- **A 必须最先做**：字号和间距是基础，后续阶段依赖 A 的布局结果
- **B/C/D 可并行**：阴影、渐变、光晕互不依赖
- **E/F 最后做**：背景色和细节微调在主体完成后收尾

---

## 六、验收标准

### 6.1 视觉验收

- [ ] 标题文字 ≥16px，正文 ≥12px，标签 ≥11.5px
- [ ] 面板内边距 ≥16px，导航栏内边距 ≥18px
- [ ] 主面板有可见阴影（BlurRadius ≥24）
- [ ] 导航栏阴影比内容面板更深
- [ ] PrimaryActionButton 为蓝紫渐变
- [ ] Logo 区域为渐变色 + 光晕
- [ ] 导航选中项为渐变背景
- [ ] 输入框聚焦时有蓝色光晕
- [ ] 窗口背景偏蓝调（#F0F4FF）

### 6.2 功能验收

- [ ] 所有页面可正常切换
- [ ] 输入框可正常输入和提交
- [ ] ComboBox 下拉动画正常
- [ ] DataGrid 行选择和 hover 动画正常
- [ ] 弹窗（图表浮层、参数面板）可正常打开/关闭
- [ ] 窗口可正常拖拽、最大化、最小化、关闭
- [ ] 进度条动画正常运行
- [ ] 图表区域扫光动画正常

### 6.3 兼容性验收

- [ ] 1440x900 分辨率下布局正常
- [ ] 1920x1080 分辨率下布局正常
- [ ] 窗口缩小到 MinWidth(1120) x MinHeight(700) 时不溢出
- [ ] 高 DPI (150%, 200%) 下文字不模糊（已有 UseLayoutRounding）

---

## 七、风险与回退

| 风险 | 概率 | 影响 | 缓解措施 |
|------|------|------|----------|
| 字号放大导致局部溢出 | 中 | 低 | 逐页面检查，必要时微调 Margin |
| DropShadowEffect 性能开销 | 低 | 低 | WPF 硬件加速渲染，面板数量有限 |
| 渐变在高对比度模式下不可见 | 低 | 低 | 渐变色差小，降级为纯色可接受 |
| 聚焦光晕与现有 FocusRing 冲突 | 低 | 低 | 光晕在 Border 层，FocusRing 在外层 |

**回退方案**：每个阶段完成后 git commit，如发现问题可单阶段回退。

---

## 八、文件改动汇总

| 文件 | 改动类型 | 改动量估算 |
|------|----------|-----------|
| `Resources/WorkbenchTheme.xaml` | 字号、间距、阴影资源、渐变、光晕 | ~80 处 Setter 修改 + 4 个新资源 |
| `MainWindow.xaml` | 字号、间距、Logo、背景色、阴影 | ~20 处修改 |
| `Views/Pages/SingleStationPage.xaml` | 间距微调 | ~3 处 |
| `Views/Pages/ModelChatPage.xaml` | 间距微调 | ~3 处 |
| `Views/Pages/BatchComparisonPage.xaml` | 间距微调 | ~2 处 |
| `Views/Pages/ApplicationCenterPage.xaml` | 间距微调 | ~2 处 |
| `Views/Pages/NetworkReviewPage.xaml` | 间距微调 | ~3 处 |
| `Views/Pages/HistoryReportsPage.xaml` | 间距微调 | ~2 处 |

**总改动量**：约 115 处 XAML 属性修改，0 处 C# 代码修改。

---

## 九、预览效果

HTML 效果预览文件位于 `docs/ui-preview.html`，包含 6 个页面的完整改版效果演示：

- 单站测试（仪表盘 + 图表 + 控制台）
- 大模型对话（三栏布局 + 多模型对比 + 流式生成）
- 批量评测（模型对比表格）
- 应用接入（扫描卡片网格）
- 网络复核（路由追踪 + IP 风险评估）
- 历史报告（报告列表 + 详情）
