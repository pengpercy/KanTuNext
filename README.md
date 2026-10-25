# 看图 KanTuNext

一款离线的看图与图片整理工具：本地浏览、看图、整理图片，外加 OCR。完全离线，不发任何网络请求。

- 设计稿：`design/KanTuNext.figcraft`（FigCraft，仅本地保留，不进仓库：含宣传图素材）

## 开发

```bash
dotnet test tests/KanTuNext.Tests/KanTuNext.Tests.csproj
dotnet run --project src/KanTuNext.Desktop            # Debug（无 AOT，可用预览器）
dotnet publish src/KanTuNext.Desktop -c Release -r osx-arm64 --self-contained -o artifacts/publish/osx-arm64   # NativeAOT
```

打包：`build/scripts/package.{osx-app.sh,linux.sh,win.ps1}`（输入为 `build/KanTuNext/` 下的发布目录）。
发布：写好 `CHANGELOG.md` 对应版本段落后运行 `tools/local/release.sh --bump patch --push`。
图标：改 `build/resources/icon/*.svg` 后运行 `tools/make_icons.sh`；MDI 图标清单在 `tools/gen_icons.py`。
