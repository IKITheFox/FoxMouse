# Security policy

English | [简体中文](SECURITY.zh-CN.md)

## Scope

Please report security problems in the current 1.0.0 Beta code and main branch. Historical 0.x builds are development snapshots; fixes target the current code. There is no guaranteed response or remediation deadline.

## Private reporting

If available, use GitHub's **Report a vulnerability** action on this repository's [Security page](https://github.com/IKITheFox/FoxMouse/security). Private reporting availability depends on repository settings; this document does not enable it. If the action is unavailable, request a private contact channel without publishing exploit details or sensitive data in a public issue.

Include the version, Windows version, affected component, impact, and minimal reproduction using a disposable test environment. Relevant areas include unsafe install paths, arbitrary deletion, privilege boundaries, IPC validation, dependency download integrity, and disclosure through logs.

Do not include keys, tokens, personal files, or unredacted logs. Do not test against another person's system or data. Coordinate disclosure privately. No bounty or dedicated security email is promised by this policy.

## Non-security defects

Use [Issues](https://github.com/IKITheFox/FoxMouse/issues) for ordinary visual, animation, compatibility, or installation defects without a security impact.
