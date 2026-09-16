# Contributing to FoxMouse

English | [简体中文](CONTRIBUTING.zh-CN.md)

## Scope and setup

Check existing issues before proposing changes. Discuss significant behavior or architecture changes first. Submit work you own or are authorized to contribute under the repository's GPL v3 license; retain third-party notices.

Use an interactive Windows x64 development environment with .NET 10 and the Windows tooling required by the project. Fork the repository, create a focused branch, and run:

```powershell
dotnet restore FoxMouse.slnx
dotnet test FoxMouse.slnx -c Release
```

For installer changes, run `scripts/New-Release.ps1 -ExpectedVersion 1.0.0 -CandidateOnly` and `scripts/New-OnlinePackage.ps1` sequentially. Keep dependency lockfiles consistent. Never run destructive deployment tests against a real installation; use the provided isolated lifecycle checks.

## Review checklist

- Add regression coverage and explain any unrun tests.
- Update Chinese and English strings together; check theme, DPI, keyboard focus, and text clipping.
- Verify native cursor recovery when an effect ends, fails, or the application exits.
- Treat secure desktops and elevated applications as security boundaries; do not bypass them.
- Preserve installer rollback, path validation, integrity checks, and user settings choices.
- Exclude credentials, private logs, build outputs, and signing keys from commits.

Describe the original issue, implementation, validation environment, results, and remaining limitations in the pull request. Provide sanitized screenshots for visual changes. Report vulnerabilities through [SECURITY.md](SECURITY.md), not public issues. Do not move an existing release tag to publish a later fix.
