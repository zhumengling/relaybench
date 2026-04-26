# RelayBench

RelayBench 是一个面向中转站和 OpenAI 兼容接口的 Windows 桌面跑分工具，用来对比接口速度、兼容性、稳定性和本机网络状态，帮助筛选更适合接入的入口。

- 对单个接口做快速测试、稳定性测试和深度测试。
- 对多个入口做批量快速对比、排行榜排序和候选深测。
- 批量深测按站点排队，同站点不同入口不会同时抢占额度。
- 可把当前入口直接应用到 Codex 系列软件，并按需整理聊天记录显示。
- 在接口不可用、延迟异常或能力异常时，借助网络复核区分本地网络、出口 IP、路由和目标接口问题。

## 界面预览

### 单站深度测试

![单站深度测试](docs/images/single-station-deep-test.png)

### 批量深测队列

![批量深测队列](docs/images/batch-deep-queue.png)

### 入口组累计对比图

![入口组累计对比图](docs/images/batch-comparison-chart.png)

### 应用接入

![应用接入](docs/images/application-integration.png)

### 出口 IP 风险复核

![出口 IP 风险复核](docs/images/ip-risk-review.png)

## 主要功能

### 1. 单站测试

用于测试单个接口当前是否可用、是否稳定，以及是否适合继续接入。

当前支持：

- 快速测试、稳定性测试和深度测试。
- 模型拉取与基础兼容性验证。
- 基础 5 项能力检查。
- Responses API、结构化输出、协议兼容和错误透传验证。
- 流式首字延迟（TTFT）、流式完整性和流式响应表现观察。
- 多模态、非聊天 API 能力矩阵与缓存机制验证。
- 多模型串行 tok/s 对比。
- 独立吞吐 3 轮均值统计。

### 2. 批量评测

用于对多个入口进行快速筛选、排序和选优。

当前支持：

- 入口组导入、编辑和分组展示。
- 批量快速对比。
- 排行榜图表与列表展示。
- 手动勾选候选入口。
- 对勾选入口继续发起批量深度测试。
- 批量深测最多 5 个线程并发。
- 同站点不同入口遵循排队原则，避免同时测试同一站点。
- 只有正在测试的行显示流光效果，排队或未运行的入口不显示运行态特效。
- 入口组累计对比图，用于观察不同入口组的长期表现。
- 排行榜入口一键应用到 Codex 系列软件。

### 3. 应用接入

用于检查本机 AI 客户端的接入状态，并把当前入口快速写入到支持的客户端。

当前支持：

- Codex CLI、Codex Desktop、VSCode Codex 接入检测。
- Antigravity、Claude CLI 本地接入状态扫描。
- 识别安装状态、配置状态和 API 接入状态。
- 一键应用当前接口到 Codex 系列。
- 原始 Trace 查看。
- 一键还原 Codex 系列默认配置。
- 切换官方 / 第三方时按需整理聊天记录显示。
- 统一风格的确认弹窗与状态提示。

### 4. 网络复核

用于在测试结果异常时辅助排查问题来源。

当前支持：

- 基础网络检查。
- 网页 API Trace 与地区信息观察。
- 客户端 API 接入状态复核。
- Cloudflare 风格下载、上传与延迟测速。
- 路由与 MTR 风格链路检查。
- OpenStreetMap 路由地图渲染。
- 出口 IP、DNS 与分流路径复核。
- 出口 IP 风险复核。
- NAT / STUN 观察。
- 本地端口扫描。

### 5. 历史报告

用于查看历史结果、归档诊断信息，并导出结构化报告。

当前支持：

- 最近测试历史回看。
- 报告归档浏览。
- 报告导出。
- 原始输出与结果摘要打包。

## 当前版本已实现能力

### 接口测试相关

- OpenAI 兼容接口的基础可用性测试。
- `GET /models` 探测。
- 小体积非流式请求测试。
- 流式请求测试与 TTFT 采样。
- Responses API、结构化输出、协议兼容、错误透传、流式完整性与多模态检查。
- 非聊天 API 能力矩阵（embeddings / images / audio / moderation）。
- 缓存机制观察。
- 多模型单流 tok/s 对比。
- 独立吞吐 3 轮均值。
- 多轮稳定性测试、成功率统计与连续失败统计。
- 批量候选入口对比与综合评分排序。
- 同站点多入口顺序调度。
- 单站、稳定性、批量结果的本地历史记录。

### 网络复核相关

- 本机网络基础信息采集。
- `chatgpt.com/cdn-cgi/trace` 解析。
- 常见 AI 服务可访问性检查。
- 本地客户端 API / 接入状态检查。
- STUN 映射地址与 NAT 类型的尽力判断。
- `tracert` 与逐跳延迟 / 丢包采样。
- OpenStreetMap 路由地图渲染。
- Cloudflare 风格下载 / 上传测速。
- 出口 IP、DNS 与分流路径复核。
- 多源出口 IP 风险复核。
- 内置异步 TCP 端口扫描引擎。

### 客户端接入相关

- Codex CLI、Codex Desktop、VSCode Codex 接入识别。
- Antigravity、Claude CLI 接入状态扫描。
- Codex 系列入口应用与默认配置还原。
- 原始 Trace 查看。
- 统一风格确认弹窗。
- 官方 / 第三方切换时按需整理聊天记录。

### 界面与交互相关

- 左侧导航栏底部关于入口。
- 全局滚动条样式优化。
- 嵌套滚轮区域滚动修正。
- 批量深测运行态流光效果。
- 用户提示、确认弹窗与状态文案统一。

## 技术栈

- .NET 10
- WPF
- C#
- Windows 桌面应用

## 环境要求

### 从源码构建与运行

- Windows 10 / 11
- .NET SDK 10

### 运行发布版

- Windows 10 / 11
- `framework-dependent` 包：需要预先安装 .NET Desktop Runtime 10，体积最小。
- `self-contained` 包：无需预装运行时，下载后可直接运行，但体积更大。

## 从源码运行

在仓库根目录执行：

```powershell
dotnet build .\RelayBenchSuite.slnx -c Debug -v minimal
dotnet run --project .\RelayBench.App\RelayBench.App.csproj -c Debug
```

## 构建发布版

在仓库根目录执行：

```cmd
publish.cmd
```

脚本会自动读取 `Directory.Build.props` 中的版本号，并在 `release\` 目录下同时生成两套发布产物：

- `relaybench-v<版本号>-win-x64-framework-dependent.zip`
- `relaybench-v<版本号>-win-x64-framework-dependent.sha256.txt`
- `relaybench-v<版本号>-win-x64-self-contained.zip`
- `relaybench-v<版本号>-win-x64-self-contained.sha256.txt`

命名说明：

- `framework-dependent`：依赖本机 .NET Desktop Runtime 10，适合追求小体积的场景。
- `self-contained`：内置运行时，适合直接分发给未安装运行时的机器。

例如当前版本会生成：

```text
release\relaybench-v0.1.6-win-x64-framework-dependent.zip
release\relaybench-v0.1.6-win-x64-self-contained.zip
```

## 目录说明

- `RelayBench.App`：WPF UI、页面、ViewModel 与本地状态管理。
- `RelayBench.Core`：网络诊断、测速、STUN、路由、端口扫描与接口测试核心逻辑。

## 当前依赖的在线数据源

当前版本在部分功能中会访问以下在线服务：

- OpenStreetMap：地图瓦片背景。
- `chatgpt.com/cdn-cgi/trace`：出口信息与地区观察。
- Cloudflare Speed Test：下载、上传与延迟测量。
- iprisk.top：当前出口 IP 识别。
- ipapi.is：IP 风险与 ASN 信息。
- proxycheck.io：代理 / VPN 检查。
- ip-api.com：出口地区与基础网络信息。
- ipwho.is：地理与 ASN 信息补充。
- country.is：轻量地理与 ASN 信息补充。
- IP2Location.io：IP 类型与风险补充。
- GreyNoise Community：噪声 / 扫描情报。
- Spamhaus DROP / ASN-DROP：威胁情报名单。
- AlienVault OTX：威胁情报补充。
- Shodan InternetDB：暴露面补充。
- abuse.ch Feodo Tracker：恶意基础设施名单。
- Tor Project：Tor 出口校验。

应用会对部分结果进行本地缓存，以减少重复请求。

## License

本项目基于 [MIT License](LICENSE) 开源发布。
