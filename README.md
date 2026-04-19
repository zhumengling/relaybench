# RelayBench

RelayBench 是一个面向 Windows 桌面的中转站测试工作台，用来测试、对比和筛选一个或多个中转站，并在结果异常时辅助判断问题究竟来自本地网络还是中转站本身。

## 项目定位

RelayBench 的核心任务不是“检测”，而是“测试与对比”：

- 对单个中转站做快速测试、稳定性测试和深度测试。
- 对多个中转站做批量快速对比、排行榜排序和候选深测。
- 在中转站不可用、延迟异常或解锁异常时，借助网络复核能力区分是本地网络问题还是中转站问题。

## 主要功能

### 1. 单站测试

用于测试单个中转站当前是否可用、是否稳定，以及是否适合继续接入。

当前支持：

- 快速测试
- 稳定性测试
- 深度测试
- 模型拉取与基础兼容性验证
- 流式首字延迟（TTFT）与流式响应表现观察

### 2. 批量对比

用于对多个中转站进行快速筛选、排序和选优。

当前支持：

- 入口组导入与编辑
- 批量快速对比
- 排行榜图表与列表展示
- 手动勾选候选站点
- 对勾选站点继续发起批量深度测试

### 3. 网络复核

用于在测试结果异常时辅助排查问题来源。

当前支持：

- 基础网络检查
- ChatGPT Trace 与地区信息观察
- 官方 API 可访问性检查
- NAT / STUN 观察
- 路由与 MTR 风格链路检查
- Cloudflare 风格测速
- 分流与出口复核
- 本地端口扫描

### 4. 历史报告

用于查看历史结果、归档诊断信息，并导出结构化报告。

当前支持：

- 最近测试历史回看
- 报告归档浏览
- 报告导出
- 原始输出与结果摘要打包

## 当前版本已实现能力

### 中转站测试相关

- OpenAI 兼容接口的基础可用性测试
- `GET /models` 探测
- 小体积非流式请求测试
- 流式请求测试与 TTFT 采样
- 多轮稳定性测试、成功率统计与连续失败统计
- 批量候选站点对比与综合评分排序
- 单站 / 稳定性 / 批量结果的本地历史记录

### 网络复核相关

- 本机网络基础信息采集
- `chatgpt.com/cdn-cgi/trace` 解析
- OpenAI 支持地区快照对照
- 常见 AI 服务可访问性检查
- STUN 映射地址与 NAT 类型的尽力判断
- `tracert` 与逐跳延迟/丢包采样
- OpenStreetMap 路由地图渲染
- Cloudflare 风格下载/上传测速
- 出口 IP、DNS 与分流路径复核
- 内置异步 TCP 端口扫描引擎

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
- .NET Desktop Runtime 10

## 从源码运行

在仓库根目录执行：

```powershell
dotnet build .\NetTestSuite.slnx -c Debug -v minimal
dotnet run --project .\NetTest.App\NetTest.App.csproj -c Debug
```

## 构建发布版

在仓库根目录执行：

```powershell
dotnet publish .\NetTest.App\NetTest.App.csproj -c Release -r win-x64 --self-contained false --configfile .\NuGet.Config -p:UseAppHost=true -o .\release\relaybench-win-x64
```

发布完成后，可执行文件位于：

```text
release\relaybench-win-x64\NetTest.App.exe
```

## 目录说明

- `NetTest.App`：WPF UI、页面、ViewModel 与本地状态管理
- `NetTest.Core`：网络诊断、测速、STUN、路由、端口扫描与中转站测试核心逻辑
- `docs`：设计与实现计划文档
- `release`：本地发布产物目录（已在 Git 中忽略）

## 运行时数据说明

以下目录是应用运行过程中按需生成的本地状态，不应提交到 Git：

- `config/`
- `data/`

它们通常包含：

- 本地保存的中转站配置
- 应用状态快照
- 历史报告与导出文件
- 地图瓦片缓存
- 其他运行时缓存数据

## 当前依赖的在线数据源

当前版本在部分功能中会访问以下在线服务：

- OpenStreetMap：地图瓦片背景
- ipwho.is：公网路由节点地理信息查询
- Cloudflare Speed Test：下载、上传与延迟测量
- `chatgpt.com/cdn-cgi/trace`：出口信息与地区观察

应用会对部分结果进行本地缓存，以减少重复请求。

## 仓库说明

本仓库保留源码、文档与构建配置；日志、缓存、发布产物以及本地运行状态均不会提交到 GitHub。
