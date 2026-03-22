# TagEverything 文件管理应用

## 简介
TagEverything 是一个基于 Windows 桌面（WPF + .NET 9）开发的文件管理与打标签工具。它允许用户集中管理各种文件，并支持针对视频格式（.mp4, .mkv 等）自动提取时长、分辨率、码率等元数据。非视频文件支持哈希与基本属性提取。通过多维度标签与搜索功能，您可以轻松找回您的资产。

## 特性
- 首次运行选择库文件夹，随后按 `年/月` 结构自动存储。
- 采用 Entity Framework Core + SQLite 本地存储数据与标签关系。
- 视频文件支持 FFmpeg 解析（自动下载依赖）。
- 多文件拖拽导入与多维度（Tag 包含 / 文件名模糊）组合搜索。
- FileSystemWatcher 实时同步文件系统变动（重命名、删除等）。
- 现代化 WPF 界面（MaterialDesignThemes）。

## 使用说明

### 安装与运行
1. 下载 `TagEverything_Setup.exe` 安装包并运行。
2. 安装后首次启动，应用将提示您选择一个“库文件夹”（Library Folder）。
3. 随后，应用会自动在该目录下生成 `library.db`，并初始化必要组件（首次导入视频时，将自动下载 FFmpeg 环境组件）。

### 导入文件
- **点击导入**：在主界面顶部点击“导入文件”按钮，在文件对话框中选择。
- **拖拽导入**：直接从资源管理器拖拽文件到主窗口区域。
- **详情弹窗**：导入时会弹出文件详情（非阻塞）。在其中可输入标签（使用 `,` 或回车分隔），点击“确认导入”后将复制文件到库中。

### 搜索与打开
- **搜索栏**：
  - 输入 `文件名` 可进行模糊搜索。
  - 输入 `#标签名` 可进行特定标签过滤。
  - 例如输入 `#holiday vacation` 表示：包含 holiday 标签且文件名包含 vacation 的文件。
- **打开文件**：双击任意文件卡片，即可使用系统默认程序打开该文件。

### 快捷键与操作
- `Enter`：在标签输入框内按下可快速应用当前输入的标签。
- `双击左键`：在卡片列表区双击文件可直接调用系统默认应用打开源文件。
- `.library 文件关联`：双击系统内的 `.library` 文件也可直接唤起应用。

## 测试报告
- **测试框架**: xUnit
- **覆盖范围**: 
  - `HashService`（计算 SHA-256）
  - `MetadataExtractionService`（识别视频与扩展名）
  - `SearchAlgorithm`（基于内存数据库模拟包含标签与名字的搜索查询）
- **测试结果**: 3/3 均通过 (总耗时: 3.1s)。

## 技术栈与交付物
- **目标框架**: .NET 9.0-windows
- **UI 框架**: WPF / MaterialDesignInXamlToolkit
- **数据库**: SQLite / EF Core 9
- **打包**: Inno Setup 6 / PublishSingleFile
- **其他**: Xabe.FFmpeg, Ookii.Dialogs.Wpf, CommunityToolkit.Mvvm
