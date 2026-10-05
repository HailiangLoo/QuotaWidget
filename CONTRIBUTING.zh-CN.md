# 开发

[English](CONTRIBUTING.md) · **简体中文**

使用 Windows 和 .NET 10 SDK。UI 为 WPF，存储使用 Windows 自带 SQLite；核心项目无第三方 NuGet 依赖。

```powershell
dotnet run --project tests/QuotaWidget.Tests -c Release
dotnet run --project tests/QuotaWidget.UiTests -c Release
dotnet run --project src/QuotaWidget -- --demo --scenario codex-plus
```

核心测试使用临时目录和合成记录。WPF 测试调用真实布局与处理函数，不启动采集后台任务。

可选的官方 CLI 集成检查默认跳过。要在自己电脑显式运行：

```powershell
$env:QUOTAWIDGET_LIVE_TESTS = '1'
dotnet run --project tests/QuotaWidget.Tests -c Release
Remove-Item Env:QUOTAWIDGET_LIVE_TESTS
```

这些检查会定位并校验已安装官方程序，部分检查在临时空配置目录中调用 CLI。不要在自动构建中提供个人登录凭证。`--cache-probe`、`--lifecycle-probe`、`--codex-probe` 是显式的本机诊断入口，输出不得直接作为公开附件。

## 发布

```powershell
powershell -File scripts/Build-Release.ps1
powershell -File scripts/Test-PublicTree.ps1
```

构建从源码重新生成二进制，移除调试符号并映射编译路径；不要复制私人安装目录。仅发布 `artifacts/` 下新生成的 zip 与校验文件。推送前人工检查 `git diff --cached`，自动扫描并不保证能识别所有私人信息。

功能修改请补充有意义的回归测试。曲线修改应保留原始记账总量、真实工作边界和提供方隔离，并验证切换视图/监听模式不会改变同一时刻的估算依据。
