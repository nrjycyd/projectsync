# SiteLift

[![Release](https://img.shields.io/github/v/release/nrjycyd/sitelift?display_name=tag)](https://github.com/nrjycyd/sitelift/releases/latest)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20%2B%20WSL-0078D4)](https://learn.microsoft.com/windows/wsl/)
[![Frameworks](https://img.shields.io/badge/Static%20Sites-14%2B%20Frameworks-10B981)](#支持-14-种主流静态站点框架)

**写完内容，点几下就上线。**

**SiteLift** 是一款面向 Windows 10 / 11 + WSL 用户的静态站点发布工作台。如果你习惯在 WSL 中构建博客、知识库或产品文档，再在 Windows 下用 Git 推送到 GitHub，SiteLift 可以帮你把整套流程收进一个清爽的原生桌面窗口里——告别来回切换终端、重复敲命令、手动拷贝文件，也不再担心同步时误删或推错内容。

---

## 你能从 SiteLift 得到什么

### 1. 一条龙发布，多站点批量搞定
- **按步骤推进**：勾选一个或多个站点，沿着 **构建 → 预览 → 同步 → 推送** 顺序推进；主按钮会自动高亮下一步操作，不用记命令。
- **所见即所得**：支持在构建通过后**自动打开浏览器预览**，确认排版和样式没问题再同步上线。
- **稳妥可控**：推送遇到网络波动可直接一键 **重试**，运行过程中随时可 **停止**，实时日志清晰标注成功、警告与错误。

### 2. 同步前先看清单，告别误删焦虑
- **变更一目了然**：写入 Windows 仓库前，先列出全部 **新增 / 修改 / 删除 / 待清理** 文件，核对无误后再确认同步。
- **精准控范围**：自由指定要同步的目录与文件、排除临时产物或特定后缀，避免把缓存或杂项带进仓库。
- **安全护栏**：未跟踪文件清理默认关闭；即使手动开启，也会单独标红提示并要求二次确认。

### 3. 告别折腾 WSL 环境，开箱即用
- **一键配好开发环境**：内置 **WSL 环境管理**，可根据你添加的站点项目**自动推荐**所需工具，或按框架预设一键安装 Git、Node.js、pnpm、Python、Hugo、Ruby/Bundler、mdBook 等运行环境。
- **一站式管理 WSL**：直观查看各发行版状态、占用空间与位置，支持打开终端、设为默认、备份导出、磁盘扩容和在线安装新发行版。
- **Windows ↔ WSL 双向传文件**：内置文件复制工具，复制前先预览清单，自动跳过同名文件与 Windows 下载标记。

### 4. 多账号 GitHub 轻松切换，日常维护更顺手
- **多账号互不干扰**：针对 GitHub 优化，支持多账号 HTTPS 独立授权与自定义 SSH，同一台电脑维护个人博客和工作文档也不串号。
- **仓库零门槛接入**：自动识别已有 Git 仓库的分支与远端；面对空目录或普通目录，一键即可完成远端测试与克隆连接。
- **常用操作一键直达**：列表行内直达 Windows 目录、WSL 目录、WSL 终端与远端仓库页面，还能绑定 **重装依赖**、**升级主题** 快捷按钮。
- **配置便携复用**：支持表单配置与 JSON 双向同步，一键导入导出配置模板，换机器或加新站点几秒搞定。
- **原生体验与自动更新**：Windows 11 Fluent 风格界面，支持浅色 / 深色 / 跟随系统主题，并内置**版本检查与一键原地升级**（失败自动回滚）。

---

## 支持 14 种主流静态站点框架

添加项目后，SiteLift 会**自动识别站点框架**并展示专属图标；在「WSL 环境 → 配置开发环境」中也可直接按框架一键配齐对应环境。

| 生态 | 支持的框架与模板 | 常见适用场景 | WSL 一键环境预设 |
| :--- | :--- | :--- | :--- |
| **Node.js** | **VitePress**、**VuePress** | 技术文档、产品手册、个人知识库 | Node.js（nvm + pnpm） |
| **Node.js** | **Astro**、**Hexo** | 个人博客、内容站、作品集 | Node.js（nvm + pnpm） |
| **Node.js** | **Docusaurus**、**Rspress** | 开源项目文档、团队文档中心 | Node.js（nvm + pnpm） |
| **Node.js** | **Quartz** | Obsidian 笔记发布为数字花园 | Node.js（nvm + pnpm） |
| **Node.js** | **Nuxt**、**Next.js**（含 Nextra） | 现代静态导出站点与文档站 | Node.js（nvm + pnpm） |
| **Node.js** | **Docsify** | 轻量免编译 Markdown 文档站 | Node.js（nvm + pnpm） |
| **Go** | **Hugo** | 极速静态博客、个人主页、官网 | Hugo Extended（+ 可选 Go） |
| **Python** | **MkDocs**（含 Material 主题） | 技术文档、Wiki、团队手册 | Python 3（+ pip / venv / pipx） |
| **Ruby** | **Jekyll**（含 Chirpy 等主题） | GitHub Pages 博客与技术写作 | Ruby + Bundler |
| **Rust** | **mdBook** | 类电子书技术教程与项目手册 | mdBook（+ 可选 Rust） |
| **通用** | **任意自定义静态站点** | 自建构建脚本或其他静态生成器 | 按需自由组合安装 |

---

## 快速上手（3 步开始发布）

### 准备工作
- Windows 10 / 11（64 位）
- 已安装 [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)
- 已安装 [Git for Windows](https://git-scm.com/install/windows)
- 已启用并安装至少一个 [WSL](https://learn.microsoft.com/windows/wsl/install) 发行版（如 Ubuntu）

### 第 1 步：下载运行
1. 前往 [GitHub Releases](https://github.com/nrjycyd/sitelift/releases/latest) 下载最新的 `SiteLift-vX.Y.Z-win-x64.zip`。
2. 解压到任意目录，双击 `SiteLift.App.exe` 即可使用（无需安装，后续可在软件内一键升级）。

### 第 2 步：添加第一个站点
1. 点击首页 **新建项目**（或通过下拉菜单 **导入 JSON**，可参考 [examples/edusprout.build-sync.json](examples/edusprout.build-sync.json)）。
   > 导入他人分享的 JSON 配置时，请先核对里面的构建、预览和维护命令再执行。
2. 在配置页依次确认三件事：
   - **项目与 Git 仓库**：选择 Windows 本地仓库目录，点 **测试连接并获取分支**。
   - **本地构建环境**：填写 WSL 项目路径，点 **检测环境**，再填好构建 / 预览命令（如 WSL 尚未装运行环境，可先通过右上角 **工具 → WSL 环境** 一键安装）。
   - **同步与清理**：填写构建通过后要同步的目录或文件，保存返回首页。

### 第 3 步：构建并发布
勾选你的站点，依次点击 **构建 → 预览 → 同步 → 推送**：
- **构建**通过后，下方会自动列出本次将同步的文件变动；
- 点击 **预览** 可在浏览器中检查页面效果；
- 核对清单无误后点击 **同步**，最后一键 **推送** 完成上线。

---

## 常见问题

- **为什么推送时提示权限或登录失败？**
  - 使用 **HTTPS** 时，请确认已按 Git Credential Manager 提示登录 GitHub；如果你有多个 GitHub 账号，可在项目配置的「连接选项」中填写对应的 GitHub 用户名。
  - 使用 **SSH** 时，可在 Windows 终端运行 `git -C "<仓库路径>" ls-remote origin` 检查密钥与仓库权限。
- **为什么 WSL 里提示找不到命令（如 `pnpm`、`bundle`、`hugo`）？**
  - 先通过右上角 **工具 → WSL 环境 → 配置开发环境** 检查所需工具是否已装好；如果是通过用户级目录安装的命令，可在配置页核对「额外 PATH 目录」或在构建命令前补全环境变量。
- **配置和日志保存在哪里？**
  - 项目配置保存在 `%LOCALAPPDATA%\SiteLift`，运行日志按天存放在该目录的 `logs` 文件夹中。
  - 遇到问题可点击底栏 **导出诊断**，一键打包最近 7 天的日志与配置摘要（对外分享前请注意脱敏个人路径与敏感信息）。

---

<details>
<summary><strong>开发者参考（安装包校验、源码结构与本地编译）</strong></summary>

### 校验发布包完整性（可选）
下载同版本的 `SHA256SUMS` 后，可通过以下任一命令校验压缩包：

```powershell
# Windows PowerShell
Get-FileHash .\SiteLift-vX.Y.Z-win-x64.zip -Algorithm SHA256
```

```bash
# WSL
sha256sum -c SHA256SUMS
```

### 同步、清理与提交范围说明
- **同步**：严格按配置的「同步路径」「排除路径」与「排除文件名后缀」写入 Windows 目标仓库；已被 Git 跟踪的文件即使加入排除项，也不会在同步阶段被删除。
- **清理（默认关闭）**：开启「同步前永久清理未跟踪文件」后，「变更清单」会单独以危险语义列出待清理项并要求二次确认；清理会永久删除 Windows 工作区内未跟踪且未被 `.gitignore` 忽略的文件（保留配置中的排除项）。
- **推送与提交**：**推送**会提交整个 Windows 工作区的新增、修改和删除（遵循 `.gitignore`），再推送到目标远端分支。

### 项目结构

```text
sitelift/
├── src/
│   ├── SiteLift.Core/       # 核心同步计划、Git / WSL 探测、框架识别与文件复制引擎（.NET 8 类库）
│   └── SiteLift.App/        # Windows WPF + WPF-UI 桌面客户端
├── tests/
│   └── SiteLift.Tests/      # SiteLift.Core 单元测试与真实 Git 集成测试
├── tools/
│   └── Publish-Windows.ps1  # Windows x64 打包与 SHA256SUMS 生成脚本
├── examples/                # 项目配置示例
├── DESIGN.md                # Windows 11 Fluent UI 界面与光学对齐规范
└── SiteLift.sln
```

### 从源码构建与测试
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

</details>
