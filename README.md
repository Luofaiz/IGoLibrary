# IGoLibrary

IGoLibrary 是一个围绕“我去图书馆”预约流程重建的桌面客户端项目。当前仓库的主线是 `IGoLibrary-Ex`：基于 Avalonia 的新版桌面应用，已经接入 Windows 安装包、GitHub Release 更新清单、本地 SQLite 持久化、凭据存储和较完整的测试覆盖。

> 本项目仅用于学习、研究和个人自动化实验，不隶属于“我去图书馆”平台、学校或图书馆运营方。请在遵守所在学校、场馆和平台规则的前提下了解与使用。

## 下载安装

推荐 Windows 用户直接安装最新 Release 中的安装包：

- 最新版本：<https://github.com/Luofaiz/IGoLibrary/releases/latest>
- Windows 安装包：<https://github.com/Luofaiz/IGoLibrary/releases/latest/download/IGoLibrarySetup.exe>
- Windows 便携压缩包：<https://github.com/Luofaiz/IGoLibrary/releases/latest/download/IGoLibrary-Windows-x64.zip>

安装后的主程序名为 `IGoLibrary.exe`。

## 当前版本重点

这个 README 按当前仓库状态重新编写，不再沿用原始项目的旧版说明。`IGoLibrary-Ex` 目前包含：

- 通过微信登录回调链接获取会话 Cookie。
- 自动加载账号可用场馆，并支持场馆预览、锁定和刷新。
- 座位布局浏览，支持搜索、隐藏已占座位、收藏常用座位。
- 多目标座位监控与预约尝试。
- 直接预约、先查空座再预约、随机空座预约等抢座策略。
- 定时启动监控任务。
- 当前预约信息刷新、取消预约和重约辅助流程。
- Cookie 失效、抢座结果、任务失败的本地 Toast、提示音和 SMTP 邮件提醒。
- 设置、收藏座位、自定义接口模板等本地持久化。
- Windows Credential Manager / macOS Keychain 会话存储。
- 高级用户可覆盖 API 地址和 GraphQL 模板。
- 基于 GitHub Releases 的程序内更新检查与安装器下载。

## 截图

![IGoLibrary desktop home](docs/images/ex/主页.png)

## 自动更新机制

桌面端会检查这个更新清单：

```text
https://github.com/Luofaiz/IGoLibrary/releases/latest/download/latest.json
```

`latest.json` 会声明最新版本号、安装器下载地址和 SHA256。程序发现新版本后，会下载 `IGoLibrarySetup.exe`，校验 SHA256，通过后启动安装器。

每个正式 Release 建议包含：

- `IGoLibrarySetup.exe`
- `latest.json`
- `IGoLibrary-Windows-x64.zip`

## 仓库结构

```text
IGoLibrary-Ex/
  src/
    IGoLibrary.Ex.Domain/           领域模型、枚举、基础业务类型
    IGoLibrary.Ex.Application/      应用服务、任务协调器、运行时状态
    IGoLibrary.Ex.Infrastructure/   HTTP API、SQLite、凭据存储、邮件发送
    IGoLibrary.Ex.Desktop/          Avalonia UI、ViewModel、桌面交互服务
  tests/
    IGoLibrary.Ex.Tests/            单元测试和 ViewModel 测试
  build/                            发布、安装器、Release 上传脚本

IGoLibrary-Winform/                 旧 WinForms 源码，仅作为历史参考保留
README_Winform.md                   旧 WinForms 文档归档
```

`ConsoleDemo/`、`I_Goto_Library-main/` 等本地实验目录不会进入公开仓库，避免把历史测试数据、临时配置或旧 Cookie 示例误提交。

## 本地开发

环境要求：

- .NET SDK `10.0.201` 或兼容的 roll-forward 版本
- Windows 开发可使用 Visual Studio 2022 或 `dotnet` CLI
- 构建 Windows 安装器需要 Inno Setup 6

运行桌面端：

```powershell
cd .\IGoLibrary-Ex
dotnet restore
dotnet run --project .\src\IGoLibrary.Ex.Desktop\IGoLibrary.Ex.Desktop.csproj
```

运行测试：

```powershell
cd .\IGoLibrary-Ex
dotnet test .\IGoLibrary-Ex.sln -c Release -p:UsedAvaloniaProducts=
```

## 构建与发布

生成 Windows 发布目录和 zip：

```powershell
cd .\IGoLibrary-Ex
.\build\publish-windows.ps1 -Version "1.0.0"
```

生成 Windows 安装包和更新清单：

```powershell
cd .\IGoLibrary-Ex
.\build\publish-installer.ps1 -Version "1.0.0" -Notes "Initial release."
```

上传或覆盖 GitHub Release 资产：

```powershell
cd .\IGoLibrary-Ex
.\build\publish-github-release.ps1 -Version "1.0.0" -Repo "Luofaiz/IGoLibrary" -Notes "Initial release."
```

生成产物会写入 `IGoLibrary-Ex/artifacts/`，该目录已被 Git 忽略。

## 本地数据位置

运行时数据不会写入仓库目录：

- Windows：`%LOCALAPPDATA%\IGoLibrary-Ex`
- macOS：`~/Library/Application Support/IGoLibrary-Ex`
- 覆盖默认位置：设置环境变量 `IGOLIBRARY_EX_DATA_DIR`

SQLite 数据库文件名为 `igolibrary-ex.db`，日志位于数据目录下的 `logs/`。

## 安全注意

- 不要提交 `.env`、安装包、数据库、日志、Cookie 或个人配置。
- Cookie 和 Authorization 值应视作密码处理。
- Release 更新清单包含 SHA256，用于在启动安装器前校验下载文件。

## 许可证

本项目基于 [MIT License](LICENSE.txt) 开源。
