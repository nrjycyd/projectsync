# SiteLift

[![Release](https://img.shields.io/github/v/release/nrjycyd/sitelift?display_name=tag)](https://github.com/nrjycyd/sitelift/releases/latest)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20%2B%20WSL-0078D4)](https://learn.microsoft.com/windows/wsl/)
[![UI](https://img.shields.io/badge/UI-WPF--UI%20(Fluent)-0067C0)](https://github.com/lepoco/wpfui)

**SiteLift** 是一款面向 Windows + WSL 站点开发者的桌面工作台：在 WSL 中执行项目构建与本地预览，在 Windows 侧可视化核对文件变更并安全同步，再通过 Windows 本地 Git 仓库一键提交和推送（远端连接与多账号授权已针对 GitHub 优化）。

界面基于 [WPF-UI](https://github.com/lepoco/wpfui) 构建，遵循 Windows 11 Fluent Design 规范（Mica 材质、浅色 / 深色 / 跟随系统主题），详见 [DESIGN.md](DESIGN.md)。

---

## 核心功能

### 1. 多项目批处理工作流
- **全流程流水线**：勾选一个或多个项目后，按 **构建 → 预览 → 同步 → 推送** 顺序推进；工具栏主操作按当前状态自动高亮，推送失败时提供 **重试** 入口，运行中随时支持 **停止**。
- **变更清单核对**：同步前按 **同步变更** 与 **目标清理（永久删除）** 分组展示全部文件变动，支持新增 / 修改 / 类型变更 / 源端删除 / 目标清理状态徽章，以及「目录次色 + 文件名主色」路径分段高亮。
- **语义化运行日志**：实时输出构建、预览、同步与推送命令日志，支持动态主题语义着色（项目标题、成功、警告、错误）、自由划选复制、一键复制与清空。
- **快捷访问与维护**：表格行内一键打开 Git 本地目录、WSL 项目目录、WSL 终端、Git 远端仓库，以及执行自定义的 **重装依赖** / **升级主题** 命令。

### 2. 一体化项目配置
双击项目行或通过行右键菜单即可打开配置页，包含 4 个功能模块：
- **项目与 Git 仓库**：统一配置项目名称、Windows 本地目录与远端分支。自动探测已有本地 Git 仓库的当前分支与远端列表；支持一键测试远端连接并拉取分支；普通目录可执行 **连接 Git 仓库**，已有仓库可在折叠维护区执行 **重置 Git 数据**（保留工作区文件并将原 `.git` 自动备份至目录旁）；支持配置 HTTPS 多账号 GitHub 授权用户名与自定义 SSH 命令。
- **本地构建环境**：一键检测 WSL 发行版、Linux 用户与 `nvm` / Node.js 版本；配置构建前从远端拉入 WSL 的目录、构建命令、预览命令、**构建成功后自动打开浏览器预览**，以及快捷维护命令。
- **同步与清理**：配置构建通过后要同步的路径、排除路径与排除文件名后缀；可选开启 **同步前永久清理未跟踪文件**（默认关闭）。
- **快速配置（JSON）**：直接查看、复制、导入、清空或编辑 JSON 配置，实时显示与表单的同步状态并一键 **应用到表单**。

### 3. 内置实用工具
通过首页右上角 **工具** 菜单打开：
- **文件复制**：支持 Windows 与 WSL 发行版任意方向的文件 / 目录互传；先预览复制清单再执行，默认跳过同名文件与 `Zone.Identifier` 下载标记。
- **WSL 环境管理**：集中查看已安装发行版的运行状态、WSL 版本、默认标记、虚拟磁盘容量与安装位置；支持启动终端、设为默认、导出 `.tar` 备份、调整 WSL 2 虚拟磁盘上限、在线安装发行版、注销删除，以及在终端中一键 **配置开发环境**（Git、Python 3、Node.js via `nvm`、pnpm）。

---

## 快速开始

### 环境要求
- Windows 10 / 11（x64）
- [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Git for Windows](https://git-scm.com/install/windows)（安装后建议在新开的 PowerShell 中运行 `git --version` 确认可用）
- 已启用并安装至少一个发行版的 [WSL](https://learn.microsoft.com/windows/wsl/install)

### 安装与校验
1. 从 [GitHub Releases](https://github.com/nrjycyd/sitelift/releases/latest) 下载 `SiteLift-vX.Y.Z-win-x64.zip`（以及同版本的 `SHA256SUMS`）。
2. （可选）校验压缩包完整性：
   ```powershell
   # Windows PowerShell
   Get-FileHash .\SiteLift-vX.Y.Z-win-x64.zip -Algorithm SHA256
   ```
   ```bash
   # 或在 WSL 中使用 SHA256SUMS 直接校验
   sha256sum -c SHA256SUMS
   ```
3. 解压后运行 `SiteLift.App.exe` 即可使用。

### 首次使用流程
1. **添加项目**：点击首页 **新建项目** 从空白表单配置，或通过旁边的下拉菜单 **导入 JSON**（可参考 [examples/edusprout.build-sync.json](examples/edusprout.build-sync.json)）。
   > 导入包含构建、预览或维护命令的外部 JSON 配置时，请逐条核对命令内容后再执行。
2. **核对环境与连接**：在「项目与 Git 仓库」中选择 Windows 本地目录并点击 **测试连接并获取分支**；在「本地构建环境」中填写 WSL 项目路径并点击 **检测环境**；在「同步与清理」中设置要同步的目录或文件，保存返回首页。
3. **执行发布**：勾选目标项目，依次执行 **构建 → 同步 → 推送**。同步前请在下方「变更清单」核对文件变化；配置了预览命令时，可随时点击 **预览** 或开启构建后自动在浏览器预览。

---

## 同步、清理与提交范围说明

- **同步**：严格按配置的「同步路径」「排除路径」与「排除文件名后缀」写入 Windows 目标仓库；已被 Git 跟踪的文件即使加入排除项，也不会被同步阶段删除。
- **清理（默认关闭）**：开启「同步前永久清理未跟踪文件」后，「变更清单」会单独以危险语义列出 **目标清理（永久删除）** 项，应用同步前需再次确认。清理会永久删除整个 Windows 工作区内未跟踪且未被 `.gitignore` 忽略的文件（保留配置中的排除项）。
- **推送与提交**：**推送**会先提交整个 Windows 工作区的新增、修改和删除（遵循 `.gitignore`），再推送到目标远端分支。推送前请确认同步范围外没有误改文件。

---

## 项目结构

```text
sitelift/
├── src/
│   ├── SiteLift.Core/       # 核心同步计划、Git / WSL 探测与文件复制引擎（纯 .NET 8 类库）
│   └── SiteLift.App/        # Windows WPF + WPF-UI 桌面客户端
├── tests/
│   └── SiteLift.Tests/      # SiteLift.Core 单元测试与真实 Git 集成测试
├── tools/
│   └── Publish-Windows.ps1  # Windows x64 打包、目录结构与 SHA256SUMS 校验脚本
├── examples/                # 项目配置示例
├── DESIGN.md                # Windows 11 Fluent UI 界面与光学对齐规范
└── SiteLift.sln
```

---

## 从源码构建与测试

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)；打包脚本需在 PowerShell 7 中运行。

```powershell
# 编译解决方案（Release）
dotnet build SiteLift.sln --configuration Release

# 运行核心单元测试与 Git 集成测试
dotnet run --project tests/SiteLift.Tests --configuration Release

# 启动桌面应用
dotnet run --project src/SiteLift.App

# 打包 Windows x64 发布包并生成 SHA256SUMS
./tools/Publish-Windows.ps1 -OutputDirectory dist
```

---

## 常见问题排查

- **SSH 连接失败**：在 Windows 终端运行 `git -C "<仓库路径>" ls-remote origin`，检查 SSH Host、密钥路径、passphrase 与仓库权限。
- **HTTPS 授权失败**：在 Windows 终端对同一仓库运行 `git fetch origin`，按 Git Credential Manager 提示完成登录，或检查「连接选项」中的 GitHub 授权用户名后回到 SiteLift 重试。
- **WSL 无法进入或找不到命令**：运行 `wsl --list --verbose` 核对发行版名称，再运行 `wsl -d <发行版> -u <用户>` 验证项目路径、Node.js 与构建命令是否可用。
- **数据目录与诊断日志**：
  - 项目配置保存在 `%LOCALAPPDATA%\SiteLift`；
  - 运行日志按天写入配置目录下的 `logs` 文件夹；
  - 点击底栏 **导出诊断** 可打包最近 7 天的日志与配置摘要（分享前请先检查并脱敏路径、命令或其他敏感信息）。
