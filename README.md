# QuotaWidget · 额度小挂件

A small Windows desktop widget for Claude and Codex quota, usage trends, and local chat activity.

一个常驻桌面的 Windows 小挂件：看剩余额度、重置倒计时和消耗趋势，不用来回打开用量页面。

**[下载 Windows 版](https://github.com/HailiangLoo/QuotaWidget/releases/latest)** · [隐私说明](docs/PRIVACY.md) · [开发与测试](CONTRIBUTING.md)

## 界面

截图全部使用合成演示数据。支持深浅主题、完整/精简模式，以及仅 Claude、仅 Codex或同时监听。

<img src="docs/images/both-compact.png" width="264" alt="精简模式：按平台显示额度和 chat 活动" />
<img src="docs/images/codex-compact.png" width="264" alt="仅 Codex 精简模式" />

<details>
<summary>查看完整模式</summary>

<img src="docs/images/both-expanded.png" width="324" alt="Claude 与 Codex 完整模式" />
<img src="docs/images/codex-expanded.png" width="324" alt="仅 Codex 完整模式" />

</details>

## 能做什么

- Claude：5 小时、总周额度、Fable 周额度。
- Codex：周额度；接口返回 5 小时窗口时自动显示。
- 两个平台各自选择速率或累计，查看 1h、5h、12h、24h、3d 或全部历史。
- chat 活动、缓存计时提示、本机 token 汇总及子代理归属。
- 置顶、托盘、精简模式，窄窗口自动调整布局。

## 开始使用

1. 在 [Releases](https://github.com/HailiangLoo/QuotaWidget/releases/latest) 下载 `QuotaWidget-v0.11.10-win-x64.zip`，解压到自己的文件夹。
2. 双击 `QuotaWidget.exe`，或者 `启动小挂件.cmd`。发布包包含 .NET 运行时，无需另外安装。
3. 设置中选择需要监听的平台。

**Codex**：先安装并登录官方 Codex Windows 应用。挂件通过应用附带、经签名校验的 CLI 查询额度，使用现有登录状态。

**Claude**：需要受支持的官方 Claude Code CLI，或 Claude Windows 应用附带的 CLI。双击 `登录Claude.cmd` 完成挂件专用登录；它使用独立配置目录。找不到兼容 CLI 时，在设置中指定路径或更新官方应用。

只想先看效果，双击 `演示模式.cmd`。演示数据与真实数据分开存放。

系统要求：Windows 10/11，x64。当前界面以中文为主。初版二进制尚未签名。

## 数据与精度

数据保存在当前 Windows 用户的 `%USERPROFILE%\.quotawidget\data`，不会随程序目录上传到 GitHub。可用 `--data-dir "你的目录"` 指定其他位置。程序不自动添加开机启动项。

额度以官方读数为准。**速率是根据离散额度读数和本机活动估算的趋势**；累计取有效原始读数，缺口不补造。其他设备或网页版活动可能不在本机记录内；token 统计不是账号账单，也不换算成精确的单 chat 周额度。

两个平台的额度单位不同，不能相加。Fable 折算只在已识别的支持套餐上启用。平台接口、套餐和窗口可能变化，缺失的窗口不会显示成 0。

详细的数据访问范围见 [隐私说明](docs/PRIVACY.md)。提交问题时，请使用演示截图或脱敏信息，避免上传整个数据目录。

## 从源码构建

Windows 上安装 .NET 10 SDK，然后运行：

```powershell
dotnet run --project tests/QuotaWidget.Tests -c Release
dotnet run --project tests/QuotaWidget.UiTests -c Release
powershell -File scripts/Build-Release.ps1
```

发布文件位于 `artifacts/`。测试默认不需要登录账号；依赖已安装官方 CLI 的检查须显式开启，见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 许可

项目源码及原创图标采用 [MIT](LICENSE)。本项目独立维护，非 Anthropic 或 OpenAI 官方产品，也未获得其背书。相关名称和商标归各自所有者所有；平台图标从用户本机安装中读取，不随源码或发布包分发。运行时及商标说明见 [NOTICE](NOTICE.md)。
