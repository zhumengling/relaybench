# RelayBench 全项目 UI / 动画 / 交互重置施工文档

> 日期：2026-05-03  
> 适用范围：RelayBench.App 全部 WPF 界面、全局主题、透明代理、数据安全测试、模型对话、批量评测、网络复核、应用接入、历史报告、托盘与悬浮 Token 窗口。  
> 参考规范：根目录 `DESIGN.md`、`VoltAgent/awesome-design-md` 本地参考仓库 `da068674dbe2f7073059d0c38c0ac60aa83c1660`、`ui-ux-pro-max` 生成的 Enterprise Gateway / Data-Dense Dashboard 建议。  
> 目标：将 RelayBench 从“功能堆叠的测试工具界面”重置为“精致、克制、可长时间工作的 AI 接口测试与本地入口调度工作台”。

## 0. 施工结论

RelayBench 不做营销页、不做大面积暗色海报、不做装饰性渐变，不用大字英雄区。它是桌面工程工具，第一屏应该直接是可操作工作台。

整体风格采用“浅色运营驾驶舱 + 少量深色开发者井”的方向：

- 从 `awesome-design-md/linear.app` 借鉴克制、精确、低噪声的软件质感。
- 从 `awesome-design-md/vercel` 借鉴白底、细边界、严谨层级和影子即边界的节制。
- 从 `awesome-design-md/raycast` 借鉴命令面板密度、快捷入口、低干扰深色浮层。
- 从 `awesome-design-md/supabase`、`voltagent` 借鉴开发者工具中的代码/日志可信感，但只用于日志井、协议探测、Token 悬浮窗等局部。
- 从 `awesome-design-md/sentry` 借鉴监控态语义、错误/告警的可读状态，而不是照搬暗紫风格。

当前项目必须重点解决：

- 字体叠加、窗口化时文字互压、中文乱码或错误编码。
- hover/press 大量使用 `ScaleTransform` 导致视觉跳动和压缩。
- 页面间组件密度、圆角、阴影、标题层级不统一。
- 表格列、按钮组、长 URL、长模型名、长中文提示没有统一避让策略。
- 透明代理、模型对话、数据安全等高频页面缺少统一的状态生命周期和可恢复交互。
- 悬浮 Token 窗口必须精致小巧，只在用户启动后出现，不常驻打扰。

## 1. 施工边界

### 1.1 必改范围

- `RelayBench.App/Resources/WorkbenchTheme.xaml`
- `RelayBench.App/Resources/Motion.xaml`
- `RelayBench.App/Resources/AdvancedTestLabTheme.xaml`
- `RelayBench.App/MainWindow.xaml`
- `RelayBench.App/MainWindow.xaml.cs`
- `RelayBench.App/Views/FloatingTokenMeterWindow.xaml`
- `RelayBench.App/Views/FloatingTokenMeterWindow.xaml.cs`
- `RelayBench.App/Views/Pages/SingleStationPage.xaml`
- `RelayBench.App/Views/Pages/BatchComparisonPage.xaml`
- `RelayBench.App/Views/Pages/AdvancedTestLabPage.xaml`
- `RelayBench.App/Views/Pages/ModelChatPage.xaml`
- `RelayBench.App/Views/Pages/TransparentProxyPage.xaml`
- `RelayBench.App/Views/Pages/ApplicationCenterPage.xaml`
- `RelayBench.App/Views/Pages/NetworkReviewPage.xaml`
- `RelayBench.App/Views/Pages/HistoryReportsPage.xaml`
- 与上述页面绑定强相关的 ViewModel 文案、状态、命令可用性、悬浮窗/托盘状态字段。

### 1.2 不在本轮强制改动

- Core 层探测算法、评分算法、代理转发算法只在 UI 需要状态字段时补充最小 ViewModel 映射。
- 不重写业务逻辑，不改变测试判定结果。
- 不引入大型第三方 UI 框架，除非后续确认收益大于打包体积和迁移风险。

### 1.3 施工原则

- UI 重置先统一主题和组件，再改页面。不要在每个页面复制一套样式。
- WPF 布局使用 `Grid`、`DockPanel`、`ScrollViewer`、`DataGrid` 的稳定尺寸能力，避免用 `WrapPanel` 承担核心命令栏布局。
- 所有能变化的文字必须有容器预算：`MinWidth`、`MaxWidth`、`TextTrimming`、`ToolTip`、`LineHeight`、`MaxHeight`。
- 所有表格必须可横向滚动，不允许用压缩列宽把中文挤重叠。
- 所有动效必须服务状态理解，不做装饰性弹跳。

## 2. 产品定位与信息架构

RelayBench 的核心定位是：

- 单站接口质量测试。
- 批量上游对比评测。
- 数据安全测试套件。
- 模型对话与多模型实测。
- 本地透明代理入口调度。
- 应用接入写入与还原。
- 网络出口、路由、DNS、IP 风险复核。
- 历史报告与复盘。

信息架构应保持 8 个一级页面，但重做页面内部结构：

1. 单站测试：从“表单 + 结果堆叠”变成“接口配置 + 测试计划 + 实时判定 + 图表证据”。
2. 模型对话：从“聊天室 + 侧边参数”变成“会话流 + 多模型回答 + 可折叠参数抽屉”。
3. 数据安全：从“测试实验室”变成“安全套件控制台”，突出 Prompt injection、RAG 数据污染、恶意 URL / 命令诱导、Jailbreak。
4. 批量评测：从“快测/深测卡片区”变成“候选池 + 排行 + 深测队列 + 证据图表”。
5. 透明代理：从“路由文本 + 表格”变成“本地入口调度器控制台”。
6. 应用接入：从“扫描结果 + 当前接口”变成“目标应用目录 + 写入计划 + 回滚保障”。
7. 网络复核：从“长滚动工具集合”变成“工具索引 + 当前工具工作区 + 结果证据”。
8. 历史报告：从“列表 + 文本详情”变成“时间线 + 报告阅读器 + 导出动作”。

## 3. 视觉系统

### 3.1 色彩 Token

使用根目录 `DESIGN.md` 的 IBM Carbon 灵感浅色基线，并减少旧主题中过多蓝色渐变。

| Token | Hex | 用途 |
| --- | --- | --- |
| `Rb.Canvas` | `#FFFFFF` | 主窗口内容底 |
| `Rb.CanvasSoft` | `#F6F8FA` | 页面背景、工作区底 |
| `Rb.Surface` | `#FFFFFF` | 面板、表格、输入控件 |
| `Rb.SurfaceMuted` | `#F4F4F4` | 次级面板、空状态 |
| `Rb.SurfaceRaised` | `#FBFCFE` | 弹窗、抽屉、悬浮层 |
| `Rb.Console` | `#0F172A` | 日志井、代码预览、悬浮 Token 深色模式 |
| `Rb.Text` | `#161616` | 主文本 |
| `Rb.TextMuted` | `#525252` | 次级文本 |
| `Rb.TextSubtle` | `#6F6F6F` | 提示、元信息 |
| `Rb.Border` | `#E0E0E0` | 1px 默认边界 |
| `Rb.BorderStrong` | `#C6C6C6` | 激活/可编辑边界 |
| `Rb.Primary` | `#0F62FE` | 主操作、焦点、选中态 |
| `Rb.PrimaryHover` | `#0050E6` | 主操作 hover |
| `Rb.PrimarySoft` | `#E8F1FF` | 选中行、轻强调背景 |
| `Rb.Success` | `#24A148` | 运行、健康、通过 |
| `Rb.Warning` | `#F1C21B` | 降级、注意、半开 |
| `Rb.Danger` | `#DA1E28` | 失败、停止、阻断错误 |
| `Rb.Info` | `#4589FF` | 探测中、信息提示 |
| `Rb.TokenLive` | `#0E9F6E` | Token 实时流动 |
| `Rb.TokenIdle` | `#64748B` | Token 空闲累计 |

规则：

- 蓝色只表示交互和选中，不用作大面积背景。
- 绿色只表示健康/运行/Token 流，不用作普通装饰。
- 黄色必须有文字或图标说明，不可只靠颜色表示风险。
- 红色只用于阻断错误、失败、危险动作。
- 删除旧主题中过多的 `#2563EB -> #0EA5E9` 渐变。进度条可以保留轻微线性渐变，但不得成为页面主视觉。
- 不使用紫蓝大渐变、光斑、装饰球、背景插画。

### 3.2 字体

WPF 字体栈：

- UI：`Segoe UI`, `Microsoft YaHei UI`, `IBM Plex Sans`, `Inter`, sans-serif。
- 数字、URL、模型名、日志、代码：`Cascadia Mono`, `Consolas`, `JetBrains Mono`, monospace。
- 图标：优先系统 `Segoe Fluent Icons` / `Segoe MDL2 Assets`；如果后续引入 Lucide WPF 图标库，再统一迁移，不在页面内手写散乱 SVG。

字号：

| Role | Size | Weight | LineHeight | 用途 |
| --- | --- | --- | --- | --- |
| `PageTitle` | 18-20 | 600 | 24 | 页面标题 |
| `SectionTitle` | 14-16 | 600 | 20 | 面板标题 |
| `Body` | 12.5-13.5 | 400 | 18 | 默认正文 |
| `BodyStrong` | 12.5-13.5 | 600 | 18 | 标签和值 |
| `Caption` | 11-12 | 400 | 16 | 元信息、说明 |
| `Micro` | 10.5-11 | 500/600 | 14 | 徽标、紧凑表头 |
| `Metric` | 20-28 | 600 | 30 | 核心指标 |
| `TokenMeter` | 15-18 | 600 | 20 | 小悬浮窗主数值 |
| `Mono` | 12-13 | 400 | 17 | URL、模型、日志 |

强制规则：

- `LetterSpacing` 保持 0，不使用负字距。
- 不根据视口宽度缩放字体。
- 长中文最多 2 行，长英文/URL/模型名默认单行省略。
- 表格内文字行高固定，不能让换行撑开行高，除非该表格明确是详情阅读表。
- 禁止同一个容器中两个 `TextBlock` 共享同一行且都 `HorizontalAlignment=Stretch`，必须用 Grid 列宽预算。

### 3.3 间距、圆角、边界

| Token | Value |
| --- | --- |
| `Space.2` | 2 |
| `Space.4` | 4 |
| `Space.8` | 8 |
| `Space.12` | 12 |
| `Space.16` | 16 |
| `Space.24` | 24 |
| `PagePadding` | 10-14 |
| `PanelPadding` | 10-14 |
| `DenseRowHeight` | 34-40 |
| `CommandHeight` | 48-56 |
| `IconButton` | 34-40 |
| `Radius.Control` | 6 |
| `Radius.Panel` | 8 |
| `Radius.Dialog` | 10 |
| `Radius.Pill` | 999 |

规则：

- 普通页面面板半径不超过 8px，现有 10-14px 统一收敛。
- 导航栏、弹窗、悬浮窗可以 8-10px。
- 大面积阴影只用于弹窗、抽屉、悬浮 Token、菜单、Popup。
- 页面普通面板不用重阴影，以 1px 边界和浅底区分。
- 不允许卡片套卡片。重复项卡片可以存在，但页面分区不能都做成漂浮卡。

### 3.4 图标和按钮

按钮分 5 类：

1. 主操作：蓝底白字或蓝底图标，只有每个区域 1 个。
2. 次操作：白底/浅灰底 + 1px 边界。
3. 图标操作：34/36/40 正方形，必须有 Tooltip。
4. 破坏操作：红色，只用于退出、删除、清空等。
5. 状态按钮：运行/停止等根据状态切换，但仍遵守语义色。

规范：

- 常用工具动作优先图标，不用长文字按钮挤占命令栏。
- 危险动作不可只用图标，至少 Tooltip 和二次确认要明确。
- 同一命令栏内按钮高度一致。
- 禁止 hover 放大按钮导致周围控件挤压。
- Press 状态用背景变深、边界变强、轻微内阴影，不用 `Scale=0.948` 这种明显收缩。

## 4. 全局布局系统

### 4.1 主窗口 Shell

当前 `MainWindow.xaml` 结构保留无边框窗口和自定义标题栏，但重置为更稳定的三层结构：

```text
Window
  TitleBar 48px
  Workspace Grid
    NavigationRail 216-224px
    MainArea
      PageCommandBar 48-56px
      PageHost *
  GlobalOverlayLayer
```

标题栏：

- 左侧：品牌图标 28x28 + `RelayBench` + 当前运行状态小点。
- 中部：全局任务进度，仅在有任务时出现；宽度上限 520，文字省略。
- 右侧：最小化、最大化/还原、关闭。
- 关闭按钮：如果代理运行、后台模式启用或 Token 悬浮窗显示，则隐藏到托盘；否则正常退出。

导航栏：

- 宽度 216-224，固定，不随窗口化挤压。
- 每个导航项固定高 36-40。
- 选中态：浅蓝背景 + 左侧 2px 蓝条。
- hover：背景色变化，不缩放。
- 底部“关于”收敛为小按钮，不占大块空间。
- 导航栏说明文案减少到 1 行，不使用长段提示。

页面命令栏：

- 每页只保留页面标题、1 行状态摘要、右侧高频动作。
- 页面内部不再重复大标题。
- 命令栏高度固定，文本溢出省略。
- 右侧按钮组使用 `Grid` 或 `StackPanel` 固定尺寸；小窗口时低频按钮进入 `更多` 菜单。

### 4.2 响应式桌面断点

WPF 桌面最小宽度当前约 1120，仍需适配窗口化：

| 宽度 | 布局 |
| --- | --- |
| `< 1200` | 导航栏保持 216；页面右侧次要栏折叠为抽屉；表格横向滚动 |
| `1200-1365` | 双栏为主，减少指标卡数量或压缩为横向滚动 |
| `1366-1599` | 标准工作台布局 |
| `>= 1600` | 可显示三栏或更多详情，不扩大字体 |

所有页面必须通过：

- `1120x700`
- `1280x720`
- `1366x768`
- `1440x900`
- `1600x1000`
- `1920x1080`
- 125%、150% DPI

### 4.3 防叠字规则

这是本次重置的硬门槛：

- 命令栏标题与按钮之间必须有 `ColumnDefinition Width="*"` 和按钮组 `Auto`，标题列最小宽度不足时省略。
- 不能让两个会增长的文本同处一个 `DockPanel LastChildFill=True` 且都无 `MaxWidth`。
- 所有中文提示 `TextWrapping=Wrap` 时必须设置 `MaxHeight` 或明确区域可滚动。
- 所有 `WrapPanel` 只用于标签/徽标，不用于关键命令栏。
- 所有 `DataGridTextColumn` 需要 `MinWidth` 和 `ElementStyle`，长字符串省略并提供 Tooltip。
- 所有输入标签与输入框使用 `Grid` 两列或上下布局，不允许小宽度时标签压进输入框。
- 所有 `Popup`、`ComboBox` 下拉最大高度固定，宽度至少等于触发器宽度。
- 所有图标字体必须指定 `FontFamily`，避免中文字体渲染图标码位。

## 5. 组件重置规范

### 5.1 面板

新增或统一：

- `RbPanelStyle`
- `RbInsetPanelStyle`
- `RbCommandBarStyle`
- `RbDialogPanelStyle`
- `RbDrawerPanelStyle`
- `RbConsolePanelStyle`

面板规则：

- 普通面板 `Background=Surface`、`BorderBrush=Border`、`BorderThickness=1`、`CornerRadius=8`。
- 内嵌面板 `Background=SurfaceMuted`、`CornerRadius=6`。
- 命令栏 `Height=Auto MinHeight=48 Padding=10,8`。
- 弹窗最大宽度 920，最大高度不超过窗口高度减 64。
- 详情抽屉宽度 420-560。

### 5.2 文本

统一：

- `RbPageTitleTextStyle`
- `RbSectionTitleTextStyle`
- `RbFieldLabelTextStyle`
- `RbHintTextStyle`
- `RbMonoTextStyle`
- `RbMetricTextStyle`
- `RbTableCellTextStyle`

要求：

- 所有标题都显式 `TextTrimming=CharacterEllipsis`。
- 所有辅助文案默认 `Foreground=TextMuted`，不使用过淡灰。
- 表格单元格 `VerticalAlignment=Center`，行高固定。
- 日志/URL 使用 Mono 样式。

### 5.3 按钮

统一：

- `RbButtonStyle`
- `RbPrimaryButtonStyle`
- `RbQuietButtonStyle`
- `RbIconButtonStyle`
- `RbPrimaryIconButtonStyle`
- `RbDangerButtonStyle`
- `RbComposerIconButtonStyle`

状态：

- Normal：边界清晰。
- Hover：背景/边界 120-150ms 过渡。
- Press：背景加深或边界加强。
- Disabled：透明度降低但文字仍可读。
- Focus：2px 蓝色焦点环，不移除默认可访问焦点。

取消：

- 默认按钮 `ScaleTransform To=1.028`。
- press `ScaleTransform To=0.948`。
- nav item `ScaleTransform To=1.01`。
- tab item `ScaleTransform To=1.02`。

### 5.4 输入控件

适用：

- `TextBox`
- `PasswordBox`
- `ComboBox`
- `CheckBox`
- `ToggleButton`
- 数字输入字段

要求：

- 控件高度 32-36。
- `Padding=8,5`。
- 输入错误必须显示字段级错误文本，不只弹 MessageBox。
- API Key 默认隐藏，显示预览片段，如 `sk-...9f3a`。
- 数字输入要有范围提示和格式错误。
- 运行中不可修改的字段显示锁图标和 Tooltip。
- 所有 `TextBox` 多行输入必须有最小高度、最大高度或外层滚动。

### 5.5 表格

DataGrid 是 RelayBench 的核心组件，必须统一：

- Header 高 32-36。
- Row 高 36-40。
- `EnableRowVirtualization=True`。
- `EnableColumnVirtualization=True`。
- `VirtualizingPanel.VirtualizationMode=Recycling`。
- 水平滚动 `Auto`，不压缩核心列。
- 选中行使用 `PrimarySoft`，左侧可加 2px 蓝条。
- hover 只变背景。
- 状态列使用 Badge，不用长句。
- 数值列右对齐，Mono/tabular。
- 每个长字符串列有 Tooltip。

所有数据列表必须有四态：

- Empty：一句话 + 一个主要动作。
- Loading：稳定高度的骨架/小进度。
- Error：错误摘要 + 重试/查看详情。
- Disabled：说明为什么不能操作。

### 5.6 徽标与状态 Pill

统一标签：

- `Running`
- `Stopped`
- `Starting`
- `Stopping`
- `Probing`
- `Healthy`
- `Degraded`
- `Down`
- `Cached`
- `Fallback`
- `Responses`
- `Anthropic`
- `Chat`
- `OpenAI`
- `Blocked`
- `Passed`
- `Risk`

规则：

- 高度 20-22。
- Padding 7x2。
- 圆角 999。
- 文本 10.5-11.5。
- 状态不只靠颜色，必须有文字。

### 5.7 图表

图表区域要像仪表而不是海报：

- 内联图表默认高度 180-260。
- 放大图表用 overlay，不重排主页面。
- 图表空状态要有固定高度，避免加载后页面跳动。
- 图表刷新时只更新数据，不重复执行大面积闪烁动画。
- Live 图表可用细微 sweep，但只在运行中显示。
- tooltip/命中区域必须不遮挡图表主体。

### 5.8 弹窗、抽屉、Toast

弹窗：

- 适合创建、编辑、确认、危险操作。
- 主按钮右下角。
- Escape 关闭非危险弹窗。
- 点击遮罩关闭只用于非编辑弹窗；有未保存内容必须确认。

抽屉：

- 适合查看请求链、日志详情、报告明细、协议探测结果。
- 宽度 420-560。
- 从右侧进入，主页面不重排。
- 抽屉内容必须可复制，但默认脱敏。

Toast：

- 只用于复制成功、后台运行提醒、代理错误、导出完成。
- 2.5-4 秒自动消失。
- 重要错误提供“查看详情”入口。

## 6. 动画规范

### 6.1 统一 Motion Token

在 `Motion.xaml` 统一：

| Token | Duration | 用途 |
| --- | --- | --- |
| `Motion.Instant` | 80ms | Press、微反馈 |
| `Motion.Fast` | 120ms | hover、表格行 |
| `Motion.Normal` | 180ms | 状态切换、轻量显隐 |
| `Motion.Panel` | 220ms | 折叠、抽屉 |
| `Motion.Dialog` | 180-220ms | 弹窗进入 |
| `Motion.Slow` | 280ms | 仅用于大型 overlay |

缓动：

- 进入：`CubicEase EaseOut`。
- 退出：`CubicEase EaseIn`。
- 数值更新：淡入淡出或颜色过渡。
- 禁止弹簧/反弹/过度 overshoot。

### 6.2 允许的动效

- hover 背景色、边框色、文本色。
- focus ring 显示。
- 页面首次出现：opacity 0 -> 1，Y 4-6px -> 0。
- 弹窗：opacity + Y 6px。
- 抽屉：X 12px + opacity。
- 新日志行：opacity 0 -> 1。
- Token 数字：淡入或轻微数字滚动。
- 运行态小点：低频呼吸，周期 1.2-1.8 秒，仅运行中。
- 图表实时 sweep：只在用户开启实时图表时显示。

### 6.3 禁止的动效

- 按钮 hover 放大。
- Tab hover 放大。
- 导航项 hover 放大。
- 卡片 hover 抬起导致布局视觉抖动。
- 无限装饰动画。
- 大面积背景扫光。
- 多个区域同时闪烁。
- 状态更新导致行高变化。

### 6.4 减少动画模式

增加应用级设置：

- 默认跟随系统可访问性设置。
- 用户可在设置或隐藏配置中关闭动效。

关闭后：

- 保留颜色变化。
- 禁止位移、缩放、高度动画。
- 弹窗直接淡入或直接显示。
- Token 数字直接切换。
- 图表 sweep 和 pulse 不显示。

## 7. 页面级施工方案

### 7.1 MainWindow / Shell

目标：

- 主窗口不再出现乱码文案、说明段落占位、重复状态栏。
- 标题栏、导航栏、页面命令栏三层清晰。
- 关闭行为与托盘一致。

施工：

- 清理 `MainWindow.xaml` 中已隐藏但仍存在的乱码占位 Border。
- 标题栏品牌区去掉大渐变 logo 质感，使用项目图标或简洁 R 标记。
- 全局进度条改为稳定 pill，不使用强渐变。
- 导航栏文案全部修复为 UTF-8 可读中文。
- 导航项增加图标列，文字列固定省略。
- `WorkbenchPageHost` 加页面切换淡入，禁止页面之间同时占位重叠。
- Overlay 层统一为 `GlobalOverlayHost`，所有弹窗、图表放大、确认框走同一层级。

验收：

- `1120x700` 下导航、标题、关闭按钮不重叠。
- 最大化、还原、窗口化切换 20 次无字体叠加。
- 切换 8 个页面时页面内容不会残留。
- 标题栏拖动区域不会吞掉按钮点击。

### 7.2 单站测试页

现有能力：

- 接口地址、API Key、模型配置。
- 测试模式、能力模型、并发压测计划。
- KPI、图表、测试结果、分档结果。

重置布局：

```text
CommandBar
  Endpoint summary + model + run actions
Content
  Left Rail 320-360: endpoint config, test mode, advanced models
  Main Area:
    KPI Strip
    Live Chart
    Result Matrix
    Evidence / Detail Drawer
```

施工：

- 顶部只保留当前接口、模型、运行按钮、历史接口、图表放大。
- 左侧配置分组折叠：基础接口、测试模式、能力模型、并发压测。
- KPI 从大卡改为横向小指标带：总判定、普通延迟、TTFT、吞吐、协议支持。
- 检测项结果改为 DataGrid，列宽固定，证据列省略 + Tooltip。
- 分档结果使用紧凑表或图，不使用大块卡片堆叠。
- 结果详情用右侧抽屉，避免主页面无限变长。

交互：

- 修改接口配置后，结果区标记为“待重新测试”。
- 运行中锁定 Base URL、API Key、模型。
- 失败项提供重试单项。
- 点击 KPI 过滤下方结果。

动画：

- 运行开始：KPI 数值淡入更新。
- 新结果行：120ms 淡入。
- 图表刷新：不闪屏。

验收：

- 长 Base URL、长模型名、长错误信息不重叠。
- 并发压测计划在 1120 宽度下可滚动。
- API Key 不出现在 UI 明文、日志和 Tooltip 中。

### 7.3 模型对话页

现有能力：

- 会话记录。
- 消息列表。
- 附件。
- 多模型回答。
- 对话参数、预设、模型选择。

重置布局：

```text
CommandBar
  当前会话 + 模型摘要 + 发送/停止/导出
Content
  Session Rail 260-300
  Chat Stream *
  Inspector Drawer 320-360 collapsed by default
Composer
  Attach / preset / input / send
```

施工：

- 右侧参数面板默认折叠为抽屉，避免挤压聊天内容。
- 会话记录列表固定宽度，标题 1 行，摘要 2 行。
- 消息气泡不做大圆角聊天软件风，改为工程阅读卡：角色、时间、token、耗时、内容。
- 多模型回答做横向分栏或 tabs，窗口窄时横向滚动。
- Composer 固定底部，高度随输入增长但设最大高度 160。
- 附件 chips 固定高度，文件名省略。

交互：

- `Enter` 发送、`Shift+Enter` 换行。
- 发送中按钮变停止。
- 模型流式输出时显示 tok/s 和累计 tokens。
- 多模型回答可 pin 一个为主回答。
- 右侧参数修改后显示“未应用到当前生成”或立即绑定规则。

动画：

- 新消息淡入，不从底部大幅滑入。
- 流式输出只更新文本，不逐字闪烁。
- 回到底部按钮 150ms 淡入。

验收：

- 长代码块、Markdown 表格、长 URL 不撑破页面。
- Composer 在窗口化时不遮挡最后一条消息。
- 多模型回答列在 1280 宽度下不重叠。

### 7.4 数据安全页

原“高级测试实验室”已包含安全套件，本轮 UI 命名统一为“数据安全”。

核心套件：

- Prompt injection
- RAG 数据污染
- 恶意 URL / 命令诱导
- Jailbreak
- 系统提示泄露
- Tool Calling 越权
- 隐私数据回显

重置布局：

```text
CommandBar
  Endpoint + model picker + run/stop/retry/export
Content
  Suite Rail 260-280
  Test Case Table *
  Risk Summary Rail 300-320
Bottom
  Event Log 110-140
```

施工：

- 页面标题改“数据安全”，副标题“红队提示与数据泄露风险测试”。
- 安全警示条使用浅黄，不占大面积。
- 套件列表改为紧凑列表：勾选、名称、风险等级、测试数。
- 测试项表格列：选择、测试项、类型、状态、分数、耗时、风险、详情。
- 右侧风险摘要固定宽度，展示总体风险、命中 canary、失败原因、建议。
- 原始请求/响应查看必须在弹窗或抽屉中脱敏展示。

交互：

- 勾选套件后测试项表即时过滤。
- 运行中禁用 endpoint/model 修改。
- 点击测试项打开详情抽屉：目的、输入、判定、证据、建议。
- 导出报告包含安全声明：结果是风险探测，不等同完整安全审计。

动画：

- 套件切换只淡入列表。
- 进度条平滑更新，不使用条纹扫光常驻。
- 风险状态变化颜色 150ms 过渡。

验收：

- “数据安全风险”等中文长词不叠加。
- 安全警示条在 1120 宽度下最多 2 行，超过省略并 Tooltip。
- 原始请求/响应中密钥、Cookie、Authorization、URL token 参数全部脱敏。

### 7.5 批量评测页

现有能力：

- 入口组流程。
- 快速对比。
- 候选列表/排名。
- 深度测试。
- 图表和摘要。

重置布局：

```text
CommandBar
  Stepper: 入口组 -> 快测 -> 候选 -> 深测
Content
  Top: compact quick chart + ranking table
  Bottom: selected deep test queue + evidence chart
  Right Drawer: candidate detail
```

施工：

- 顶部 stepper 使用稳定 pill，不使用箭头大文本挤压。
- 快测排行榜改 DataGrid，列：排名、名称、URL、状态、综合、对话、TTFT、吞吐、结论、操作。
- 候选列表与排名不要同时使用大卡列表造成重复；保留一个主表，一个详情抽屉。
- 深测区域固定高度，结果表和图表并列或上下。
- 批量编辑器弹窗重排，避免底部按钮过多挤到一起。

交互：

- 批量导入后进入校验态：有效、重复、缺 Key、URL 错误。
- 快测完成后自动排序，但用户手动排序不被下一次局部更新打断。
- 勾选候选后深测按钮显示数量。
- 一键应用最佳接口要二次确认并显示回滚入口。

动画：

- 排名变化只高亮变动行 1 秒，不做行飞行动画。
- 图表加载稳定占位。

验收：

- 批量编辑器底部 6 个按钮在 1366 宽度下不重叠；低频动作进入更多菜单。
- 长 URL 和 Key 预览不撑破表格。

### 7.6 透明代理页

现有能力：

- 本地入口端口、并发、限速、缓存。
- 路由队列。
- 协议探测：Responses、Anthropic、OpenAI Chat。
- 脱敏日志。
- fallback、缓存、健康状态。

重置布局：

```text
CommandBar
  透明代理 / 本地入口 / 状态 / start-stop / copy / probe / more
Content
  Left Rail 360-392:
    Listen
    Protection
    Route policy
    Cache & log
  Main:
    Metric Strip
    Route Queue DataGrid
    Sanitized Log DataGrid
  Right Drawer:
    Route edit / request detail / protocol probe
```

施工：

- 左侧保留结构化配置，原始路由文本移入高级折叠，默认收起。
- 后续必须实现结构化路由编辑器：名称、Base URL、API Key、默认模型、优先级、协议能力、启用、别名、排除模型、自定义 headers。
- 路由表列保持：优先级、启用、名称、Base URL、模型、首选协议、协议能力、健康、熔断、延迟、成功率、密钥、操作。
- 日志表列保持：时间、级别、方法、路径、路由、协议、模型、状态、耗时、消息。
- Metrics Strip 保持 6 项：总请求、成功率、活跃、Fallback、缓存命中、P95。
- 命令栏按钮低频动作进入 More：刷新路由、清空日志、导入、导出。

交互：

- Start：验证端口、路由、Key 脱敏状态，启动成功后锁定监听字段。
- Stop：进入 stopping，停止后释放端口。
- Close：运行中关闭主窗口隐藏到托盘。
- Protocol Probe：先尝试 Responses 和 Anthropic；都不可用时回退 OpenAI Chat 兼容，并将协议能力存数据库。
- 日志点击打开详情抽屉，显示尝试链、fallback 原因、脱敏摘要。
- 点击指标过滤日志。

动画：

- 运行状态 pill 颜色过渡。
- 新日志行淡入。
- 悬浮 Token 由用户启动后出现，代理停止后不强制出现。

验收：

- 1120 宽度下左栏不小于 360，主表横向滚动。
- 无请求时日志区显示空状态，不显示空白。
- 有数据经过时悬浮窗显示 tok/s；无数据 5 秒后显示阶段累计。

### 7.7 应用接入页

现有能力：

- 扫描本机支持应用。
- 查看当前入口。
- 应用到 Codex 等目标。
- 写入预览、还原默认配置。

重置布局：

```text
CommandBar
  应用接入 + scan/apply actions
Content
  App Directory *
  Current Endpoint / Apply Plan 340-380
Bottom or Drawer
  Trace details
```

施工：

- 应用列表改为 DataGrid/紧凑列表，避免每个应用大卡占空间。
- 每个应用显示：图标、名称、Provider、当前入口、状态、配置来源、操作。
- 右侧“当前接口”显示 Base URL、Model、Key 预览、写入目标、预览。
- 原始 Trace 放抽屉，不占主布局。
- 扫描结果支持过滤：已接入、可接入、需确认、失败。

交互：

- 开始扫描时按钮 loading，应用列表 skeleton。
- 写入前显示计划：将改哪些文件、如何回滚。
- 还原默认配置必须二次确认。
- 写入成功后显示 toast，并将该应用状态更新为已接入。

验收：

- Key 预览不明文。
- 长配置路径省略 + Tooltip。
- 写入预览可复制但不会显示密钥。

### 7.8 网络复核页

现有能力多且页面长：

- 基础网络、网页/API 目录、测速、路由、地图、分流、IP 风险、STUN、端口扫描等。

重置方向：

```text
Content
  Tool Rail 240-260
  Current Tool Workspace *
  Evidence Drawer optional
```

施工：

- 不再把所有工具完整内容放在一个长滚动页里。
- 左侧工具索引：基础网络、API 可达、测速、路由追踪、分流复核、IP 风险、STUN/NAT、端口扫描。
- 中间只显示当前工具。
- 每个工具内部遵循：参数行、运行按钮、结果摘要、证据表/日志。
- IP 风险矩阵保留横向滚动和 `MinWidth=1160`，但表头固定、行高固定。
- 原始输出全部放折叠区或抽屉。

交互：

- 切换工具不清空已有结果。
- 每个工具有独立运行状态。
- 参数修改后结果标记为“参数已改变，建议重跑”。
- 大型原始输出支持复制、导出、搜索。

动画：

- 工具切换淡入，不整体滑动。
- 地图/图像加载使用稳定占位。

验收：

- 页面不再需要滚动 1600 行 XAML 式的长体验。
- 小窗口时参数行自动换为两行，但按钮不重叠。
- IP 风险矩阵横向滚动，不压缩到不可读。

### 7.9 历史报告页

现有能力：

- 历史列表。
- 运行详情。
- 报告导出。

重置布局：

```text
CommandBar
  历史报告 + export/delete/open folder
Content
  Timeline 300-340
  Report Reader *
  Export Panel 300 collapsed/drawer
```

施工：

- 左侧列表改时间线，按日期分组。
- 每条记录显示类型、标题、时间、摘要、状态。
- 右侧报告阅读器使用结构化章节，不只是一大段 TextBox。
- 导出入口固定在命令栏或右侧抽屉。
- 支持按类型筛选：单站、批量、数据安全、网络、代理。

交互：

- 默认选中最新记录。
- 删除历史要二次确认。
- 导出完成后 toast + 打开目录按钮。
- 没有历史时显示空状态和“去运行一次测试”。

验收：

- 长报告标题、长摘要不挤压时间列。
- 大报告滚动不卡顿。

### 7.10 悬浮 Token 窗口

用户要求：

- 不要一直出现，要启动才出现。
- 占位更小。
- 精致小巧。
- 有数据经过显示每秒 Token；无数据时显示当前阶段累计 Token；轮换显示。

默认规格：

- `Width=136-160`
- `Height=42-48`
- `CornerRadius=8`
- 主数值 14.8-16.5
- 副文本 9.2-10
- 左侧状态点 5-6
- 背景深色半透明 `#F2101820` 或浅色可选。
- 不放表格、不放按钮、不放说明段落。

显示逻辑：

- 默认不显示。
- 用户在 UI 或托盘点“显示 Token 悬浮窗”后显示。
- 代理启动时如果用户之前开启过悬浮窗，则恢复显示。
- 用户隐藏后记住隐藏状态，不因新请求自动弹出。

数据逻辑：

- Streaming 中：主显示 `42.8 tok/s`。
- 非流式完成后 3 秒：主显示 `+861 tokens`。
- 空闲 5 秒后：主显示 `12.4k tokens`。
- 副显示每 4 秒轮换：阶段累计、输入/输出、最近请求耗时、当前路由。
- hover 暂停轮换并显示精确值 Tooltip。

交互：

- 拖动移动。
- 松手后轻微吸边。
- 双击打开主窗口并切到透明代理。
- 右键菜单：打开 RelayBench、锁定位置、鼠标穿透、重置阶段计数、隐藏悬浮窗。
- 锁定后不拖动。
- 不抢焦点，`ShowActivated=False`。

验收：

- 100%、125%、150% DPI 清晰。
- 多显示器不跑出屏幕。
- 数字变化不闪烁。
- 字体不叠加，副标题不贴底裁切。

### 7.11 托盘和后台模式

规则：

- 透明代理运行中、后台模式开启、Token 悬浮窗显示时，关闭主窗口等于隐藏到托盘。
- 真正退出只能通过托盘右键“退出 RelayBench”或明确的退出命令。
- 首次隐藏到托盘显示一次通知。

托盘菜单：

- 打开 RelayBench
- 启动/停止透明代理
- 显示/隐藏 Token 悬浮窗
- 后台运行：开/关
- 打开日志目录
- 退出 RelayBench

退出流程：

1. 禁止重复退出。
2. 停止透明代理。
3. flush 日志和状态。
4. 关闭悬浮窗。
5. 释放托盘图标。
6. 释放端口。
7. 退出进程。

验收：

- 点击 UI 关闭按钮不直接杀掉运行中的代理。
- 托盘退出后进程消失、端口释放。
- 再次启动不会出现多个托盘图标。

## 8. 状态生命周期

所有页面任务统一：

```text
Idle -> Validating -> Running -> Completed
                 \-> Failed
Running -> Cancelling -> Cancelled
Running -> PartialCompleted
```

UI 显示：

| 状态 | 主按钮 | 次按钮 | 状态色 | 文案 |
| --- | --- | --- | --- | --- |
| Idle | 开始 | 可配置 | 灰 | 准备就绪 |
| Validating | 禁用 | 可取消 | 蓝 | 正在检查参数 |
| Running | 停止 | 禁用破坏配置 | 绿/蓝 | 运行中 |
| Completed | 重新运行 | 导出 | 绿 | 已完成 |
| Failed | 重试 | 查看详情 | 红 | 失败 |
| Cancelling | 禁用 | 等待 | 黄 | 正在停止 |
| Cancelled | 重新运行 | 导出部分结果 | 灰/黄 | 已停止 |
| PartialCompleted | 继续/重试失败 | 导出 | 黄 | 部分完成 |

规则：

- 运行中不可修改会影响请求的字段。
- 停止中不可重复停止。
- 失败必须有“发生了什么”和“下一步怎么做”。
- 状态文本不要把异常堆栈直接暴露给用户。
- 所有可能含密钥的错误进入脱敏器。

## 9. 文案与编码

### 9.1 编码要求

- 所有 `.xaml`、`.cs`、`.md` 使用 UTF-8。
- 对 XAML 中中文可使用直接中文或 XML entity，但同一文件尽量统一。
- 禁止出现乱码片段，如 `閫忔槑`、`涓婃`、`鎵撳紑`、`鐨勬`。
- 新增文案必须在运行界面截图检查。

### 9.2 文案风格

- 中文短句，动词明确。
- 不写长段使用说明在页面上。
- 工具提示说明动作，不解释产品理念。
- 错误提示包含原因和下一步。

示例：

- 好：`端口被占用，请换一个端口或停止占用程序。`
- 不好：`启动失败。`
- 好：`复制本地入口`
- 不好：`点击这里可以复制当前透明代理本地服务的入口地址`

## 10. 可访问性

必须做到：

- 所有可点击控件可键盘访问。
- Tab 顺序与视觉顺序一致。
- 焦点环可见。
- Icon-only 按钮有 Tooltip。
- 状态不只靠颜色。
- 文本对比度满足 WCAG AA。
- 禁止键盘焦点陷阱。
- 弹窗打开后焦点进入弹窗，关闭后回到触发按钮。
- DataGrid 支持键盘上下移动。
- 重要 toast 不抢焦点。

WPF 落地：

- 为关键按钮设置 `AutomationProperties.Name`。
- 为数据表设置可读列名。
- 自定义图标按钮的 `Content` 不可只暴露码位，要有自动化名称。
- 弹窗和抽屉处理 Escape。

## 11. 性能要求

### 11.1 渲染

- 避免页面内大量 DropShadowEffect。DropShadowEffect 在 WPF 中成本高，只给弹窗/悬浮层使用。
- 表格和日志开启虚拟化。
- 日志列表保留上限，UI 只渲染最近 N 条，可查询完整日志文件。
- 图表刷新节流，建议 250-500ms 合并一次 UI 更新。
- Token 悬浮窗数字更新节流到 4-10Hz，不随每个 token 触发布局。

### 11.2 布局

- 避免在高频数据更新区域使用会重算复杂布局的 `WrapPanel`。
- 运行态数据用固定宽度数字容器。
- DataGrid 不自动根据内容无限测量。
- 复杂页面减少嵌套 ScrollViewer，尤其网络复核页。

### 11.3 内存

- 大文本日志使用分段和最大长度。
- Markdown/报告阅读器延迟渲染。
- 图片图表释放旧 BitmapSource。
- 弹窗关闭后清理临时绑定和事件。

## 12. 文件级施工顺序

### Phase 0：审计与冻结

文件：

- `RelayBench.App/Resources/WorkbenchTheme.xaml`
- `RelayBench.App/Resources/Motion.xaml`
- `RelayBench.App/Resources/AdvancedTestLabTheme.xaml`
- 全部 `Views/Pages/*.xaml`
- `MainWindow.xaml`
- `FloatingTokenMeterWindow.xaml`

任务：

- 全量搜索乱码。
- 全量搜索 `ScaleTransform`、`RepeatBehavior=Forever`、重阴影。
- 全量搜索无 `ToolTip` 的 icon-only Button。
- 记录所有页面最小可用尺寸问题。

交付：

- UI 问题清单。
- 不改业务逻辑。

### Phase 1：Token 与主题

文件：

- `WorkbenchTheme.xaml`
- `Motion.xaml`
- `AdvancedTestLabTheme.xaml`

任务：

- 建立 `Rb.*` 资源命名。
- 收敛颜色、圆角、阴影。
- 统一文字样式。
- 删除/替换默认按钮缩放动效。
- 统一 DataGrid 样式。
- 统一输入、ComboBox、CheckBox、Tab、Expander。

验收：

- 示例页不编译报错。
- 默认控件在 125% DPI 不糊、不挤。

### Phase 2：Shell

文件：

- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `MainWindowViewModel.Navigation.cs`
- `MainWindowViewModel.GlobalTaskProgress*.cs`

任务：

- 清理乱码占位。
- 重排标题栏、导航栏、页面命令栏。
- 整理 overlay 层。
- 确认关闭到托盘状态逻辑。
- 添加页面切换动效。

验收：

- 8 个页面切换无残影。
- 窗口化无标题/状态叠加。

### Phase 3：高风险页面

优先顺序：

1. `TransparentProxyPage.xaml`
2. `AdvancedTestLabPage.xaml`
3. `ModelChatPage.xaml`
4. `SingleStationPage.xaml`

原因：

- 用户已指出透明代理和悬浮窗问题。
- 数据安全是新加入的重点功能。
- 模型对话和单站测试高频使用，最容易暴露叠字。

任务：

- 重排页面结构。
- 抽离重复局部样式到主题。
- 强化空/加载/错误态。
- 添加详情抽屉模式。

### Phase 4：批量、应用、网络、历史

文件：

- `BatchComparisonPage.xaml`
- `ApplicationCenterPage.xaml`
- `NetworkReviewPage.xaml`
- `HistoryReportsPage.xaml`

任务：

- 批量页表格化、减少重复卡片。
- 应用接入页列表化、写入计划清晰化。
- 网络复核页工具索引化，避免长滚动页。
- 历史报告页时间线化。

### Phase 5：悬浮窗与托盘

文件：

- `FloatingTokenMeterWindow.xaml`
- `FloatingTokenMeterWindow.xaml.cs`
- `App.xaml.cs`
- `MainWindow.xaml.cs`
- `MainWindowViewModel.TransparentProxy.cs`

任务：

- 小型精致悬浮窗。
- 用户启动后显示，记住隐藏状态。
- 右键菜单补齐。
- 吸边、锁定、鼠标穿透。
- Token/tok/s 轮换逻辑。

### Phase 6：QA 与发布

每次修改后：

- 编译。
- 发布覆盖 `H:\relaybench-v0.1.4-win-x64-framework-dependent`。
- 打开发布目录中的程序，不要自动关闭。
- 做截图检查。

建议命令按现有项目脚本确认后执行，若脚本已支持发布路径则优先使用 `publish.ps1`。

## 13. 验收矩阵

### 13.1 编译

- `dotnet build RelayBenchSuite.slnx`
- 单元测试能跑则跑核心测试。
- XAML 编译无缺失资源。
- 发布目录可启动。

### 13.2 截图尺寸

每个页面截图：

- 1120x700
- 1280x720
- 1366x768
- 1440x900
- 1920x1080

每个尺寸检查：

- 标题不重叠。
- 按钮不挤出。
- 表格列不压扁到不可读。
- 长 URL/模型名省略并 Tooltip。
- 中文不乱码。
- 没有空白大洞。
- 弹窗不超出屏幕。

### 13.3 交互路径

必须人工走通：

- 单站测试：配置 -> 运行 -> 停止 -> 失败重试 -> 查看证据。
- 模型对话：发送 -> 停止 -> 多模型 -> 附件 -> 导出。
- 数据安全：选择套件 -> 运行 -> 查看详情 -> 导出报告。
- 批量评测：导入 -> 快测 -> 选候选 -> 深测 -> 应用最佳。
- 透明代理：启动 -> 请求经过 -> fallback -> 缓存命中 -> 停止。
- 应用接入：扫描 -> 写入预览 -> 应用 -> 还原。
- 网络复核：切工具 -> 运行 -> 查看原始输出。
- 历史报告：选择记录 -> 导出 -> 删除确认。
- 托盘：关闭隐藏 -> 双击恢复 -> 右键退出。
- 悬浮窗：显示 -> 拖动 -> 锁定 -> 隐藏 -> 重新显示。

### 13.4 数据与安全

- API Key 不出现在任何普通 UI、日志、Tooltip、导出预览中。
- Authorization/Cookie 脱敏。
- URL query 中 token/key/password 脱敏。
- 数据安全原始请求/响应默认脱敏。
- 错误堆栈不直接展示给普通用户，进入详情需确认或脱敏。

### 13.5 动效

- hover 不改变元素尺寸。
- press 不造成布局跳动。
- 表格高频更新不卡顿。
- 连续运行 10 分钟无明显内存增长。
- 减少动画模式下无位移动画。

## 14. Bug 预防清单

### 14.1 XAML

- 新增资源名唯一。
- 所有 `StaticResource` 存在。
- `BasedOn` 不循环引用。
- `DataTrigger` 绑定路径正确。
- Popup 不因 DataContext 丢失而空白。
- `PasswordBoxAssistant` 不造成明文泄漏。
- `ScrollViewer` 不套太多导致滚轮失效。

### 14.2 ViewModel

- 所有 UI 状态属性触发 `OnPropertyChanged`。
- 命令 CanExecute 随运行状态刷新。
- 运行中字段只读状态可恢复。
- Cancel 不吞异常。
- Toast 不在后台线程直接更新 UI。
- ObservableCollection 更新在 UI 线程。

### 14.3 文案

- 中文全量可读。
- Tooltip 与按钮动作一致。
- 错误提示无密钥。
- “测试实验室”全部改成“数据安全”，但代码内部类名可后续渐进迁移，不为改名破坏业务。

### 14.4 发布

- 发布前关闭旧进程或确认覆盖不失败。
- 覆盖 `H:\relaybench-v0.1.4-win-x64-framework-dependent` 后启动新版本。
- 启动后不自动退出。
- 托盘没有残留旧实例。

## 15. UI 走查清单

每个页面完成后逐项勾选：

- 页面第一屏就是实际工具，不是介绍页。
- 没有卡片套卡片。
- 没有大面积渐变背景。
- 没有装饰光斑。
- 没有 emoji 当图标。
- 图标按钮都有 Tooltip。
- 按钮高度一致。
- 输入框高度一致。
- 表格行高一致。
- 表格长文本省略 + Tooltip。
- 空状态有动作。
- Loading 不改变布局。
- Error 有下一步。
- 禁用态解释原因。
- 焦点环可见。
- 键盘可操作。
- 125% DPI 无裁切。
- 150% DPI 可用。
- 1120x700 无重叠。
- 最大化/还原无重叠。
- 中文无乱码。

## 16. 设计红线

本次重置中明确禁止：

- 为了“好看”增加营销 hero。
- 大面积暗色整页改造。
- 页面背景渐变、光斑、装饰球。
- 把所有功能做成大卡片墙。
- 用动画掩盖状态不清。
- hover 放大导致布局跳。
- 用长说明文案替代清晰控件。
- 表格列强行压缩导致不可读。
- API Key 明文出现在 UI。
- 关闭主窗口直接杀掉运行中的透明代理。
- 悬浮窗默认常驻挡屏幕。

## 17. 最终交付标准

完成后 RelayBench 应该达到：

- 视觉：浅色、精致、克制、工程感强，像一个专用桌面仪表。
- 结构：8 个页面职责清晰，每页命令栏、内容区、详情层一致。
- 交互：任务状态完整，运行/停止/失败/重试/导出可预测。
- 动效：轻、快、稳，不跳、不闪、不挤。
- 数据：长文本、长 URL、长模型名、长中文都不会重叠。
- 安全：密钥和敏感内容默认脱敏。
- 性能：日志和表格可长时间运行，悬浮窗不拖慢主界面。
- 发布：每次施工后编译并覆盖指定发布目录，打开新版本保持运行。

