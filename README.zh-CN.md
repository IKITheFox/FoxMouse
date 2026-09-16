<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/branding/source/foxmouse-mark-dark.png">
    <img src="assets/branding/source/foxmouse-mark-color.png" width="180" alt="FoxMouse logo">
  </picture>
</p>

# FoxMouse

简体中文 | [English](README.md)

**v1.0.0 Beta** · [问题反馈](https://github.com/IKITheFox/FoxMouse/issues) · [更新记录](CHANGELOG.md) · [贡献指南](CONTRIBUTING.md) · [安全政策](SECURITY.md)

Windows 光标定位工具：摇一摇，让光标平滑放大，再自然恢复。

## 安装包选择

| 版本 | 适合场景 | 依赖来源 |
| --- | --- | --- |
| 在线安装版 | 可以联网，希望下载包较小 | 检测缺失组件后从微软下载 |
| 离线安装版 | 断网部署或希望携带运行库 | 包内自带运行库，体积较大 |

两种安装器均支持中英文、系统浅深色主题、自定义目录、修复与卸载。源码 ZIP 不是可直接运行的安装包。以实际发布的安装包和校验信息为准；当前构建未数字签名。

优先支持 Windows 11 x64；Windows 10、混合 DPI 和多显示器等兼容情况请参阅[支持矩阵](docs/support-matrix.md)与[已知限制](docs/known-limitations.md)，不要将设计目标视为已完成的实机认证。

FoxMouse is a lightweight Windows tray utility that reproduces the macOS
"shake mouse pointer to locate" interaction: rapid back-and-forth motion makes
the current Windows cursor grow smoothly, then shrink back when motion stops.

## 快速使用

当前工作版本为 **1.0.0 Beta**。在线版和离线版使用同一套安装、修复、卸载界面；干净 Windows、断网和多显示器完整验收仍需完成，Beta 不代表正式稳定版。

1. 运行 `FoxMouse-Setup-x64.exe`，或解压 portable ZIP 后启动
   `FoxMouse\FoxMouse.exe`。FoxMouse 默认不会写入开机启动。
2. 快速来回摇动鼠标或触控板指针；单次高速直线移动不会触发。
3. 双击托盘图标打开设置，可调整灵敏度、最大倍率、显示模式、全屏
   暂停、拖拽暂停和进程排除列表。
4. “原生”模式无法安全替换光标时，FoxMouse 保留系统光标且不显示替代图形；
   定位环只会在用户明确选择“定位环”模式时出现。

安装时可点击安装位置并选择父文件夹，安装器会自动在其末尾添加一次
`FoxMouse`；例如选择 `D:\Applications` 后会安装到
`D:\Applications\FoxMouse`。安装版会出现在 Windows“已安装的应用”中。再次运行 Setup、打开安装根目录的
`FoxMouse.Uninstall.exe`，或从“已安装的应用”进入，都可修复或卸载。
安装、升级、修复或卸载成功后均显示完成页面，点击“确定”后退出；
失败时窗口会保留错误信息，并允许重试或关闭。
设置与有界诊断日志保存在 `%LOCALAPPDATA%\FoxMouse`，卸载时默认保留；只有
明确取消“保留个人设置和诊断日志”后才会删除。

无人值守安装也使用“父文件夹”语义：

```powershell
.\FoxMouse-Setup-x64.exe --install --quiet --no-launch `
  --install-parent 'D:\Applications'
```

已有安装的升级、修复和卸载会使用已登记的实际目录；当前版本不在维护界面中
迁移现有安装。若需更换位置，请先卸载（可保留设置），再选择新父文件夹安装。

## 在线精简包与离线包

- `FoxMouse-OnlineSetup-x64.exe`：首屏选择简体中文或 English；检测到缺少组件时，
  从微软下载固定版本并校验，再安装组件。组件安装可能要求管理员批准或重启。
  安装器本身较小，但缺少依赖时仍需额外下载运行库，并不减少运行库的磁盘需求。
- 完整离线安装包：包含运行所需组件，文件较大，适合无法联网的电脑。
- 便携 ZIP：解压运行，不会自动注册“已安装的应用”或建立安装器维护缓存；
  不等于“设置不落盘”，设置仍保存在上文所列用户目录。
- 当前在线候选包约 31.2 MB；干净机器首次下载安装依赖仍待实测，不能把开发机
  已有运行库环境的通过结果当作该项通过。候选包未签名，不要绕过系统安全提示。
- 首次安装会保存所选界面语言；设置中的语言选项可选择系统默认、中文或英文，
  更改后重新打开界面生效。升级和修复保留已有语言设置。

English: The online installer downloads missing Microsoft runtimes only. Internet access,
administrator approval or a restart may be required. Successful installation stays visible
until you click OK. The portable ZIP does not register an uninstaller, but still stores
settings in your Windows user profile. Version 1.0.0 Beta is unsigned; clean-machine
dependency installation acceptance is still pending.

## 界面与设置

- 托盘与光标引擎仍由 `FoxMouse.App` 托管。托盘菜单使用 Windows
  11 风格圆角弹出界面，并跟随系统浅色、深色、强调色和高对比度；
  勾选状态表示是否启用，详细引擎状态保留在托盘提示文本中。
- 首选设置界面是独立的 WinUI 3 宿主 `FoxMouse.Settings`，使用系统
  ThemeResource、NavigationView 和标准控件。在支持时使用 Mica；高对比度、
  不支持或初始化失败时使用系统实色背景。该宿主是每用户单实例。
- 托盘程序会等待 WinUI 设置宿主明确报告窗口已经就绪。宿主缺失、启动
  失败、提前退出或超时时，会打开内置 WinForms 设置/关于窗口，不影响
  光标引擎和安全恢复链路。
- 排除的应用通过“搜索后添加”管理。列表仅扫描当前登录会话，可按应用
  名、EXE 名或可见窗口标题筛选，默认将可交互应用排在前面并隐藏后台
  进程。可显式展开后台进程、刷新快照，或浏览未运行的 `.exe`。
  配置只保存去重后的可执行文件基名，不保存 PID、窗口标题或完整路径。
- 配置卡片仅显示名称和控件；详细帮助在悬停时显示，并同时暴露为 Windows
  无障碍 HelpText。最大倍率按当前区域格式显示，最多保留两位小数且不补尾零。
- FoxMouse 使用正式 Logo 的浅色、深色及高对比度变体；设置窗口、托盘和
  维护界面会跟随 Windows 主题刷新。Shell 图标使用兼容多种背景的多尺寸 ICO。

设置首先原子写入 `%LOCALAPPDATA%\FoxMouse\settings.json`，再通过仅当前
Windows 用户可访问的本地通道通知正在运行的托盘宿主热加载。通知失败
不会撤销已成功的保存；下次启动时仍会读取新配置。“预览”需要托盘
宿主正在运行；如果页面有未保存的更改，会先原子保存并热加载，再请求
预览，确保演示的是当前表单中的倍率与模式。

预览或正常放大结束时，FoxMouse 会先确认系统光标已经恢复，再撤下替代
overlay，并为静止指针触发有超时保护的光标刷新。避免依赖用户额外移动鼠标
是此恢复机制的目标，而不是对所有应用环境的绝对保证。
当前已有静止恢复状态测试，但仍需实际显示效果确认；刷新失败不会破坏 Guard
已确认的可见性状态。

放大光标的透明窗口会在每帧提交后重新确认 topmost z-order，避免被普通
窗口或应用自身的 topmost 窗口长期遮挡。Windows 安全桌面是有意保留的
系统边界：UAC、登录和锁屏界面不会显示 FoxMouse overlay。

v0.3 将运行故障改为自动恢复：光标资源切换、显示器/设备变化、Guard
退出或一次渲染失败不会再永久关闭高保真模式。故障按 250 ms 到 5 秒
退避后重新探测；Guard 也会自动重启。Guard 的后台续租只在最近 500 ms
内确实提交过有效放大帧时进行，因此 UI 卡死时会优先恢复系统光标。

光标捕获会优先请求与最终倍率匹配的 `.cur`/`.ani` 或 Windows cursor
资源，并在一个最大 2048×2048 的局部透明表面中绘制；不会创建整块 8K
屏幕大小的窗口缓冲。渲染器复用 DIB/DC，跨负坐标和 100%–300% DPI 的
热点锚定由自动测试覆盖。源光标本身只有低分辨率且没有更大资源时，
Windows 仍只能提供放大采样，诊断日志会记录对应质量等级。

## v0.4 contract

- Observes pointer input; never injects or rewrites mouse movement.
- Enlarges the current system cursor around its real hotspot.
- Uses an independent guard process whenever the native cursor is hidden.
- Falls back safely when a cursor or desktop mode cannot be represented.
- Stores settings and bounded diagnostic logs under `%LOCALAPPDATA%\FoxMouse`.
- Supports transactional per-user install, repair, upgrade, and uninstall with
  a verified repair cache and Windows Installed Apps registration.
- Supports Windows 11 x64 as the Tier 1 target.

## Build and test

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\FoxMouse.slnx -c Release
& 'C:\Program Files\dotnet\dotnet.exe' test .\FoxMouse.slnx -c Release
```

Run all milestone gates:

```powershell
pwsh -NoProfile -File .\scripts\Accept-Milestone.ps1 -Milestone M5
```

冻结正式候选时显式锁定版本，避免工作区版本与目标发布版本不一致：

```powershell
pwsh -NoProfile -File .\scripts\Accept-Milestone.ps1 `
  -Milestone M5 -Configuration Release -ExpectedVersion 1.0.0
```

Run the settings-specific tests and non-visual settings smoke independently:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test `
  .\tests\FoxMouse.Settings.Tests\FoxMouse.Settings.Tests.csproj -c Release

$settingsExe = Get-ChildItem .\src\FoxMouse.Settings\bin\Release `
  -Filter FoxMouse.Settings.exe -Recurse | Select-Object -First 1
$settingsSmoke = Start-Process -FilePath $settingsExe.FullName `
  -ArgumentList '--smoke-test' -Wait -PassThru
if ($settingsSmoke.ExitCode -ne 0) { throw "Settings smoke failed: $($settingsSmoke.ExitCode)" }
```

Real cursor hiding is never enabled by automated tests unless
`-AllowRealCursorHide` is passed on an interactive Windows desktop.

The release gate also runs a full application/Guard lifecycle smoke test and
an overlay timing/GDI/USER/handle soak. Network-derived macOS behavior and the
remaining physical-hardware parity gate are documented in
`docs/macOS-web-baseline.md`.

The historical v0.4.2 acceptance report is
`docs/validation/v0.4.2-execution-report.md`. The completed v0.4.1 and v0.4.0
reports remain in `docs/validation/v0.4.1-execution-report.md` and
`docs/validation/v0.4-execution-report.md`; v0.3.0, v0.2.0 and v0.1.0 reports
also remain available as immutable historical evidence.
Reproducible evidence is under the latest `artifacts/validation/m5-*` directory.
Preview artifacts are unsigned, so a
trusted Authenticode publisher certificate is still required before broad
public distribution.

## Project layout

- `src/FoxMouse.Core`: deterministic detector, effect model, settings and trace contract.
- `src/FoxMouse.Platform.Windows`: Raw Input, cursor capture, layered overlay and guard IPC.
- `src/FoxMouse.App`: tray application and WinForms fallback settings UI.
- `src/FoxMouse.Settings`: preferred unpackaged WinUI 3 settings/About host.
- `src/FoxMouse.Guard`: fail-safe native-cursor visibility watchdog.
- `src/FoxMouse.Deployment`: transactional per-user deployment and rollback engine.
- `src/FoxMouse.Setup`: graphical install/repair/uninstall entry point.
- `src/FoxMouse.Uninstall`: installed-root and Installed Apps maintenance entry point.
- `src/FoxMouse.TraceTool`: deterministic trace generator and verifier.
- `tests`: unit, replay and Windows adapter tests.
- `docs`: M0 product contract, architecture, validation and release material.

## 贡献与安全

提交代码前请阅读[贡献指南](CONTRIBUTING.md)。普通缺陷通过 Issues 反馈，漏洞请遵循[安全政策](SECURITY.md)私下报告。分享日志前删除用户名、文件路径及其他个人信息。

## 许可证

本仓库保留现有的 [GNU GPL v3 许可证](LICENSE)。第三方组件仍遵守各自许可证，详见 [LICENSE-NOTICE.md](LICENSE-NOTICE.md)。FoxMouse 是独立项目，与 Apple 无关联。
