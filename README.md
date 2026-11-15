# ProjectSync

ProjectSync 是一款 Windows 桌面应用。它在 WSL 中构建项目，在 Windows 中预览并应用文件变更，再由 Windows Git 仓库提交和推送。适合源码在 WSL、发布仓库在 Windows 的工作流。

## 下载与运行

1. 从 [GitHub Releases](https://github.com/nrjycyd/projectsync/releases/latest) 下载 `ProjectSync-v*-win-x64.zip`。
2. 将 ZIP 解压到新目录，双击 `ProjectSync/ProjectSync.App.exe`。如系统提示缺少运行时，安装 [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)。
3. 准备 Windows Git 和用于构建的 WSL 发行版。在对应的 WSL 实例内安装 Git，以及项目需要的 Node.js、包管理器和其他依赖。

解压目录中的 `scripts/` 存放执行脚本，`examples/` 提供示例配置。应用保存的项目位于 `%LOCALAPPDATA%\ProjectSync`。

## 首次配置项目

在首页点击「新建项目」，也可以从旁边的下拉菜单导入 JSON。双击项目行可以重新打开设置。

| 设置页 | 填写内容 |
| --- | --- |
| Git 远端仓库 | 填写 HTTPS 或 SSH 地址和目标分支，点击「测试远端连接」。已有 Windows Git 仓库时，也可以使用其中配置的远端名称，例如 `origin`。 |
| Git 本地仓库 | 选择 Windows 发布仓库的根目录。如果目录还没有 Git 仓库，先填好远端地址和分支，再使用「初始化 Git…」。空目录可克隆远端，有文件的目录可在保留文件的情况下初始化，并查看本地差异。 |
| 本地构建环境 | 填写 WSL 项目目录，例如 `/home/blog/site`，或粘贴 WSL 网络路径后点击「检测环境」。选择发行版、用户和 Node.js 版本，填写构建命令，例如 `pnpm docs:build`。 |
| 同步与清理 | 按行填写需要同步的文件或目录，例如 `docs` 和 `package.json`。可单独填写排除路径和文件名后缀，例如 `docs/.vitepress/dist` 和 `:Zone.Identifier`。 |
| 快速配置（JSON） | 可导入文件或粘贴 JSON，再应用到当前表单。配置完成后点击「保存项目」，也可以用「导出 JSON」备份配置。 |

如果需要在构建前将远端内容拉入 WSL，在「本地构建环境」逐行填写拉取目录。ProjectSync 会从同一个远端提交更新这些目录。目录内的本地改动有覆盖风险时，操作会停下并在日志中列出相关路径。

GitHub HTTPS 登录可在项目中指定账号，首次授权由 Git Credential Manager 打开浏览器。提交作者使用 Windows 仓库的 Git 配置，推送账号需有目标分支的写权限。

## 构建、同步和推送

1. **构建并预览**。在首页勾选项目。ProjectSync 在 WSL 执行构建并生成变更清单，这一步可能更新配置的 WSL 拉取目录，Windows 工作区不写入同步文件。需要看站点效果时，可填入预览命令并勾选「构建成功后在浏览器打开预览」。
2. **应用同步**。核对新增、修改、删除和清理项后应用。ProjectSync 复用已验证的构建快照，复核来源、目标和远端状态，再把同步范围内的文件写入 Windows。计划失效时，从「构建并预览」重新开始。
3. **提交并推送**。填写提交摘要并确认。提交暂存整个 Windows 工作区的新增、修改和删除，遵循 Git 的忽略规则。推送前可在 VS Code 或 `git status` 中检查 Windows 工作区，尤其是同步范围外的本地改动。

**排除路径与清理的作用不同。** 排除项控制从 WSL 同步的内容。若文件已被 Windows Git 跟踪，后来加入排除列表不会把它从仓库移除。启用「同步前清理目标仓库中未跟踪且未忽略的文件」时，清理范围是整个 Windows 工作区，受当前排除项保护的路径会跳过。应用前请先核对预览中的删除项。

如果提交已创建、推送失败，保持程序打开并重新勾选项目，可重试原提交。若远端已更新，先检查本地提交与远端的差异。

## 首页工具

- **文件复制**。在「工具」菜单打开。来源和目标各自选择 Windows 或 WSL，可在 Windows 和 WSL 的四种来源与目标组合中复制文件或目录。可预览复制清单，同名文件默认跳过，默认跳过 `Zone.Identifier` 下载标记。
- **WSL 环境**。查看发行版版本、安装位置和开发工具。支持安装发行版、更新 WSL、启动终端、配置开发环境，以及在支持的 WSL 2 实例上调整虚拟磁盘上限。磁盘容量显示根文件系统的剩余空间和总容量。实例停止时只显示可读取的虚拟磁盘上限。
- **项目快捷访问**。项目行可打开 Windows 仓库、WSL 项目目录、WSL 项目终端及远端仓库，也可以运行在项目设置中配置的重装依赖和升级主题命令。

## 从源码构建

在 Windows 安装 .NET 8 SDK，然后从仓库根目录运行：

```powershell
dotnet build ProjectSync.sln --configuration Release
dotnet run --project src/ProjectSync.App
```

使用 PowerShell 7 生成 Windows x64 ZIP。GitHub Actions 也使用同一个脚本。

```powershell
./tools/Publish-Windows.ps1 -OutputDirectory dist
```

| 路径 | 内容 |
| --- | --- |
| [src/ProjectSync.App](src/ProjectSync.App) | WPF 桌面应用 |
| [src/ProjectSync.Core](src/ProjectSync.Core) | 同步脚本和共享代码 |
| [src/ProjectSync.Cli](src/ProjectSync.Cli) | 命令行入口 |
| [examples](examples) | EduSprout 配置示例 |
| [立项说明](docs/立项说明.md) | 项目最初的范围和验收场景 |
