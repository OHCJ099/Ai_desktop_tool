# 桌面搜题工具

本仓库发布两个桌面版本，均不内置 API Key。默认 API URL 为
`https://api.deepseek.com/v1`，默认模型为 `deepseek-flash`；请在设置中填写自己的 Key。

| 版本 | 源码 | Release 文件 |
| --- | --- | --- |
| 原极简版 | `main` 分支 | `mini-nokey.zip` |
| S / D / Z / X 版 | `codex/regular-desktop` 分支 | `normal-nokey.zip` |

解压后的程序分别为 `mini-nokey.exe` 和 `normal-nokey.exe`。

## 版本区别

- 原极简版保留原界面、盲文输出和两分钟未操作自动退出清理逻辑。
- S / D / Z / X 版保留原透明小悬浮窗和悬停显隐；S 搜索输入内容，D 读取题目并检索本地题库，Z 设置，X 退出。
- S / D / Z / X 版答案显示在下方可选中复制的文本框中并自动复制到剪贴板，字母正常显示；取消两分钟自动退出，只在正常退出时清理自身 EXE 和同目录配置。

注意：原极简版的退出操作也会清理自身 EXE 和配置。需要保留程序时，请保留下载的压缩包。

## 客户端快捷方式

将考试客户端快捷方式放到工具 EXE 所在目录，支持 `考试客户端.lnk`、`CXExame.exe.lnk` 和 `CXExam.exe.lnk`。启动工具后，同目录会生成名称带“（带启动项）”的快捷方式，保留原快捷方式的目标、工作目录和已有参数，并按需添加 9222 调试参数。客户端需要手动通过新快捷方式启动。

## 构建

使用 .NET 8 SDK，在对应分支项目目录执行：

```powershell
dotnet publish .\AI_desktop_tool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\win-x64-standard
```

不要提交或发布含私人 Key 的配置、源码和构建产物。
