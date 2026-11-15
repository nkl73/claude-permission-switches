# Claude 权限开关

一个 Windows 上的 Claude Code 权限面板。七个开关，改完自动保存。适合平时限制 Claude 的访问范围，偶尔放行电脑操作或命令的用法。

面板直接编辑当前用户的 Claude Code 设置。它在本机运行，无需管理员权限，也不要求你登录或输入 API 密钥。

## 开始使用

1. 双击发布包里的 **ClaudePermissions.exe**，或按下文从源码构建。
2. 面板读取 `%USERPROFILE%\.claude\settings.json`。如果你设置了 `CLAUDE_CONFIG_DIR`，则读取那个目录里的 `settings.json`。
3. 如果还没有配置文件，可以将 [示例配置](settings.example.json) 复制为 settings.json。已有配置时直接打开面板。
4. 切换后新建一个 Claude Code 会话。第一次保存时，面板会在原文件旁保留 settings.json.switchboard-backup。

| 开关 | 开启时 |
| --- | --- |
| 放行电脑操作工具 | 放行 Claude Desktop 的电脑操作工具。你仍需在 Claude 内启用电脑操作，并在会话里按应用授权。 |
| 限制项目外读写 | 限制 Claude Code 的文件工具，使其只能访问已选或手动添加的工作目录。 |
| 保护凭据文件 | 文件工具屏蔽 .env、.env.* 和 .npmrc。 |
| 禁止运行命令 | 禁用 PowerShell、Bash 和后台监控工具。 |
| 禁止网页工具 | 禁用 Claude Code 内置的 WebSearch 和 WebFetch。 |
| 关闭非必要遥测 | 关闭 Claude Code 的非必要网络请求，包括遥测、错误报告及部分反馈功能。 |
| 关闭自动记忆 | 关闭 Claude Code 自动记忆的读取与写入。 |

## 权限范围

- 面板显示当前用户设置。项目或组织的策略也可能影响实际权限。电脑操作需要 Claude Desktop 支持，放行开关还要求用户配置已禁用 Claude 账号连接器且 MCP 允许列表为空。示例配置包含这两个条件。 [Claude Code 权限规则](https://code.claude.com/docs/en/permissions) · [桌面版电脑操作](https://code.claude.com/docs/en/desktop#let-claude-use-your-computer)
- Windows 原生 Code 的命令以你自己的用户权限运行。电脑操作开启后，Claude 可以看到你授权的应用里显示的内容。项目目录和凭据文件开关约束的是 Code 文件工具。 [Claude Code 命令隔离](https://code.claude.com/docs/en/sandboxing)
- **代理状态**：面板从 Claude Code 用户配置的 `env` 读取 `https_proxy`、`HTTPS_PROXY`、`http_proxy` 或 `HTTP_PROXY`，按该顺序取第一个非空值。面板显示主机和端口，省略 URL 中的凭据。对于 `localhost` 和回环 IP，面板会检查本机 TCP 端口是否在监听；其他地址只显示配置。会话级环境变量、项目配置和组织策略可能影响实际使用的代理。 [Claude Code 代理配置](https://code.claude.com/docs/en/network-config#proxy-configuration)
- **防火墙状态**：面板检查当前网络的 Windows 防火墙是否开启，以及是否存在规则组 `Claude proxy-only outbound` 中已启用的出站阻止规则。面板不会安装防火墙规则，也不会验证监听进程、实际出口或断线时能否阻止直连。
- 遥测开关控制可选请求。使用云端 Claude 模型时，模型仍会收到你的提示词以及完成任务所需的上下文。 [Claude Code 数据说明](https://code.claude.com/docs/en/data-usage)

## 从源码构建

在 64 位 Windows 上，用 PowerShell 运行以下命令。构建使用系统已有的 .NET Framework 编译器，不需要安装 NuGet 包。程序生成在 dist\ClaudePermissions.exe。测试使用示例配置的副本。

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
~~~
