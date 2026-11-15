# ProjectSync

在 WSL 中构建项目，在 Windows 中预览并同步文件变更，再通过 Windows Git 仓库提交和推送。远端访问和账号配置目前针对 GitHub 优化。

## 快速开始

1. 从 [GitHub Releases](https://github.com/nrjycyd/projectsync/releases/latest) 下载 Windows x64 ZIP，解压后运行 `ProjectSync.App.exe`。需要 [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)、Windows Git 和 WSL。
2. 点击「新建项目」，或从旁边的下拉菜单导入 JSON。填写远端地址和分支、Windows 仓库路径、WSL 项目路径、构建命令和同步范围。Windows 目录还没有 Git 时，先配置远端，再点「初始化 Git…」。
3. 保存后勾选项目，依次执行 **构建并预览 → 应用同步 → 提交并推送**。应用前先核对变更清单。导入含可执行命令的配置时，请逐条核对命令。双击项目行可以修改设置。

下载 ZIP 和同一版本的 `SHA256SUMS` 后，可在 WSL 中运行 `sha256sum -c SHA256SUMS` 校验下载完整性。

## 同步和提交范围

- **同步**按配置的路径和排除项写入 Windows。已被 Git 跟踪的文件，加入排除项后仍然保留在仓库中。
- **清理**默认关闭。开启后，变更清单会单独列出目标清理项，应用前需再次确认。清理会永久删除整个 Windows 工作区内符合条件的未跟踪文件，保留 Git 忽略的文件和配置中的排除项。
- **提交**包含整个 Windows 工作区的新增、修改和删除，遵循 Git 忽略规则。推送前请核对同步范围外的改动。

## 工具

- **文件复制**支持 Windows 与 WSL 任意组合，默认跳过同名文件和 `Zone.Identifier` 下载标记。
- **WSL 环境**是附带管理工具，可管理发行版、查看磁盘容量和配置开发工具。删除发行版会移除该发行版中的数据。

## 排查问题

- **SSH**：在 Windows 终端运行 `git -C <仓库路径> ls-remote origin` 检查 SSH Host、密钥和仓库权限。路径含空格时加引号。
- **HTTPS 登录**：在 Windows 终端对同一个仓库运行 `git fetch origin`，按 Git Credential Manager 的提示完成登录，然后回到 ProjectSync 重试。
- **WSL**：运行 `wsl --list --verbose` 核对发行版名称，再运行 `wsl -d <发行版> -u <用户>` 检查能否进入环境，以及项目路径和构建命令是否可用。
- 运行日志按天写入配置目录下的 `logs`。可通过「导出诊断」打包最近七个日志文件和配置摘要。分享前先检查日志中的命令、路径和其他敏感信息。

## 从源码构建

需要 .NET 8 SDK。打包命令在 PowerShell 7 中运行。CLI 同步命令尚未实现。

```powershell
dotnet build ProjectSync.sln --configuration Release
dotnet run --project src/ProjectSync.App
./tools/Publish-Windows.ps1 -OutputDirectory dist
```

可参考 [EduSprout 配置示例](examples)。项目配置保存在 `%LOCALAPPDATA%\ProjectSync`。
