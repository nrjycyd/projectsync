# ProjectSync

在 WSL 中构建项目，在 Windows 中预览并同步文件变更，再通过 Windows Git 仓库提交和推送。

## 快速开始

1. 从 [GitHub Releases](https://github.com/nrjycyd/projectsync/releases/latest) 下载 Windows x64 ZIP，解压后运行 `ProjectSync.App.exe`。需要 [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)、Windows Git 和 WSL。
2. 点击「新建项目」，或从旁边的下拉菜单导入 JSON。填写远端地址和分支、Windows 仓库路径、WSL 项目路径、构建命令和同步范围。Windows 目录还没有 Git 时，先配置远端，再点「初始化 Git…」。
3. 保存后勾选项目，依次执行 **构建并预览 → 应用同步 → 提交并推送**。应用前先核对变更清单。双击项目行可以修改设置。

## 同步和提交范围

- **同步**按配置的路径和排除项写入 Windows。已被 Git 跟踪的文件，加入排除项后仍然保留在仓库中。
- **清理**是可选操作，范围覆盖整个 Windows 工作区内未跟踪且未被 Git 忽略的文件，配置的排除项会受到保护。
- **提交**包含整个 Windows 工作区的新增、修改和删除，遵循 Git 忽略规则。推送前请核对同步范围外的改动。

## 工具

- **文件复制**支持 Windows 与 WSL 任意组合，默认跳过同名文件和 `Zone.Identifier` 下载标记。
- **WSL 环境**可管理发行版、查看磁盘容量和配置开发工具。

## 从源码构建

需要 .NET 8 SDK。打包命令在 PowerShell 7 中运行。

```powershell
dotnet build ProjectSync.sln --configuration Release
dotnet run --project src/ProjectSync.App
./tools/Publish-Windows.ps1 -OutputDirectory dist
```

可参考 [EduSprout 配置示例](examples)。项目配置保存在 `%LOCALAPPDATA%\ProjectSync`。
