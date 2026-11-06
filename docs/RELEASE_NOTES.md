# TagEverything 发布说明

## V1.2.1

- 修复切换库文件夹后设置窗口无法关闭的问题。
- 更新主窗口、Windows 程序和安装器图标。安装向导和 Windows「已安装的应用」统一显示名称 TagEverything，版本号显示在单独的版本字段。
- 卸载时可勾选「删除配置文件」，以删除 `%LocalAppData%\TagEverything` 目录及 FFmpeg 缓存。复选框默认不勾选，静默卸载会保留该目录。

## V1.2

- 按钮和拖拽导入使用统一队列。底部显示解析进度，可取消剩余任务；确认窗口显示复制进度，可取消复制。失败文件可在底部重试。
- 外部直接放入库目录的文件不会自动入库。点击“待入库”后逐个确认，文件保留原位置并建立记录。
- 目录重命名和文件内容修改会自动同步。找不到原文件时保留记录和标签，并标为缺失。可在卡片或列表右键选择“重新关联缺失文件”，关联库内 SHA-256 相同的文件。移除缺失记录只清理数据库记录和标签关联。
- 手动刷新会重新核对整个库并清理预览缓存。大文件库重新计算哈希或视频特征可能耗时较长。
- 画面相似算法为 dHash；视频采样 10%、50%、90% 三帧，默认阈值 10。已有视频可点击刷新升级特征。设置支持调整阈值，比较窗口支持记录误报；完全重复仍按 SHA-256 判断。
- 普通导入复制源文件。删除已入库的实际文件前会核对路径和内容，Windows 上还会核对文件标识。原地纳管、删除和恢复会拒绝通过库内的目录联接或符号链接访问文件。
- 图片预览改用 Magick.NET，SQLite 升级至 3.53.4，构建会审计直接和传递依赖。

## 数据恢复

`TagEverythingData/.operations` 保存进行中的操作，`.trash` 保存删除暂存。重启/切库会恢复未完成操作。恢复报告中的 `recovered` 文件保留在 `TagEverythingData/recovered`，可手动选择重新导入。请不要在运行时手工改动内部目录。

若恢复报告显示“待处理”，导入/删除会被阻止，修复日志或文件访问问题后重新打开库。无法确认的内容不会静默删除。

## 性能与交付

文件目录查询不读取缩略图 BLOB；只有可见卡片加载预览，离屏卡片被回收。查询基线及其测量边界见 [性能记录](performance-baseline.json)。这不是整体应用性能保证。

运行需要 Windows x64 和 .NET 9 Desktop Runtime。安装器和 `scripts/check-runtime.ps1` 检查该运行时。发布目录的 `release-info.json` 记录版本、时间、Git 版本/未提交状态和主程序 SHA-256。发布脚本不生成安装器；安装器另由 `scripts/build-installer.ps1` 编译。

## 验证命令

```powershell
dotnet build TagEverything.sln -c Release
dotnet restore TagEverything.sln --force -p:NuGetAuditMode=all -warnaserror:NU1900,NU1901,NU1902,NU1903,NU1904,NU1905
dotnet test TagEverything.Tests/TagEverything.Tests.csproj -c Release --no-build
$env:TAGEVERYTHING_FFMPEG_DIR = "$PWD/TagEverything.UI"
dotnet run --project TagEverything.UI.SmokeTests -c Release --no-build -- --media
dotnet run --project TagEverything.Benchmarks -c Release -- 10000 artifacts/performance.json
./build.ps1
./scripts/test-publish.ps1
# 需要 Inno Setup 6；冒烟编译模式不注册文件关联和全局卸载项。
./scripts/build-installer.ps1 -SmokeTest
```

WPF/视频冒烟需要桌面会话；`--media` 需要完整 ffmpeg/ffprobe 组件对。不传 `--media` 可单独验证 WPF。测试使用临时库，不修改用户库。

Windows CI 验证应用构建、核心测试、WPF 与视频冒烟测试、发布包启动和安装卸载。
