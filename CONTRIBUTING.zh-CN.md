# 参与 FoxMouse 开发

[English](CONTRIBUTING.md) | 简体中文

## 开始之前

先搜索已有 Issue；较大的功能和架构调整请先讨论范围。提交内容必须是你有权贡献的作品，遵循仓库现有 GPL v3 许可证并保留第三方声明。

在交互式 Windows x64 环境准备 .NET 10 SDK 和项目所需 Windows 构建工具。Fork 后建立专注于单一问题的分支，执行：

```powershell
dotnet restore FoxMouse.slnx
dotnet test FoxMouse.slnx -c Release
```

安装器改动还需依次执行 `scripts/New-Release.ps1 -ExpectedVersion 1.0.0 -CandidateOnly` 和 `scripts/New-OnlinePackage.ps1`。两者共用构建输出，不应同时运行。破坏性安装测试只使用隔离测试目录，不能针对真实安装目录。

## 提交要求

- 添加回归测试，注明无法执行的检查，不把计划写成已通过。
- 同步中英文文案，检查浅深色、高对比度、DPI、键盘焦点与遮挡。
- 验证效果结束、异常和退出时系统光标能恢复。
- 不绕过安全桌面、权限或其他应用的安全限制。
- 保留路径校验、下载完整性校验、回滚及用户设置保留逻辑。
- 不提交密钥、签名证书、个人日志或构建产物。

PR 应说明问题、修改、验证环境、结果和剩余限制；界面截图须脱敏。漏洞按[安全政策](SECURITY.zh-CN.md)私下报告。发布后不移动原有版本标签。
