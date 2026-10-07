# 开发

修改采集、记账或归属算法前，先读[架构与证据边界](docs/ARCHITECTURE.zh-CN.md)，确认现有链路及数据能够支持的结论。

[English](CONTRIBUTING.md) · **简体中文**

使用 Windows 和 .NET 10 SDK。UI 为 WPF，存储使用 Windows 自带 SQLite；核心项目无第三方 NuGet 依赖。

```powershell
dotnet run --project tests/QuotaWidget.Tests -c Release
dotnet run --project tests/QuotaWidget.UiTests -c Release
dotnet run --project src/QuotaWidget -- --demo --scenario codex-plus
```

核心测试使用临时目录和合成记录。WPF 测试调用真实布局与处理函数，不启动采集后台任务。

更新 README 展示图时，用 PowerShell 7 运行 `pwsh -File scripts/Update-Screenshots.ps1`。脚本采用 `showcase` 演示场景：连续采样的合成数据，并展示 Codex 的两个额度窗口。其他演示场景保留模拟休眠和连接中断，供调试使用。真实界面的“休眠”只由 Windows 电源事件判定，chat 暂时没有活动不代表电脑休眠。

可选的官方 CLI 集成检查默认跳过。要在自己电脑显式运行：

```powershell
$env:QUOTAWIDGET_LIVE_TESTS = '1'
dotnet run --project tests/QuotaWidget.Tests -c Release
Remove-Item Env:QUOTAWIDGET_LIVE_TESTS
```

这些检查会定位并校验已安装官方程序，部分检查在临时空配置目录中调用 CLI。不要在自动构建中提供个人登录凭证。`--cache-probe`、`--lifecycle-probe`、`--codex-probe` 是显式的本机诊断入口，输出不得直接作为公开附件。

## 发布

```powershell
pwsh -File scripts/Build-Release.ps1
powershell -File scripts/Test-PublicTree.ps1
```

使用 PowerShell 7。本地预发布版本加 `-AllowPrerelease`；普通发布构建会拒绝预发布版本。脚本先确定实际使用的 .NET 与 Windows Desktop 运行库版本，将对应许可与第三方声明嵌入程序，再通过 `Portable` 发布配置生成压缩的独立 EXE。原生运行库在启动时自动释放到 .NET 临时缓存，用户无需整理运行库目录；不对 WPF 做裁剪。技术细节见微软的[单文件部署文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)。

脚本检查输出仅有一个 EXE，再将其改名复制到含中文和空格的空目录，使用全新运行库缓存验证许可导出、完整/精简模式、初始化与用量明细截图。截图采用合成数据，不采集真实额度或读取私人日志。

构建从源码重新生成二进制，移除调试符号并映射编译路径；不要复制私人安装目录。仅发布 `artifacts/` 下新生成的 EXE 与校验文件，不发布中间构建目录。推送前人工检查 `git diff --cached`，自动扫描并不保证能识别所有私人信息。更新采用手动退出并替换 EXE；设置和历史仍保存在独立的用户数据目录。

功能修改请补充有意义的回归测试。曲线修改应保留原始记账总量、真实工作边界和提供方隔离，并验证切换视图/监听模式不会改变同一时刻的估算依据。
