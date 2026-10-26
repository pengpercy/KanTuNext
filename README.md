# 看图 KanTuNext

一款离线的看图与图片整理工具：本地浏览、看图、整理图片，外加 OCR。完全离线，不发任何网络请求。

支持常见格式（JPEG、PNG、GIF/WebP 动图、BMP、TIFF 等），以及 HEIC/HEIF、AVIF 和实况照片：macOS 用系统 ImageIO
解码，Windows 和 Linux 安装包自带 libheif（内含 libde265、dav1d），不需要另装系统解码扩展；Linux 安装包另带 libtiff
（内含 libjpeg-turbo）解码 TIFF。

- 设计稿：`design/KanTuNext.figcraft`（FigCraft，仅本地保留，不进仓库：含宣传图素材）

## 开发

```bash
dotnet test tests/KanTuNext.Tests/KanTuNext.Tests.csproj
dotnet run --project src/KanTuNext.Desktop            # Debug（无 AOT，可用预览器）
dotnet publish src/KanTuNext.Desktop -c Release -r osx-arm64 --self-contained -o artifacts/publish/osx-arm64   # NativeAOT
```

打包：`build/scripts/package.{osx-app.sh,linux.sh,win.ps1}`（输入为 `build/KanTuNext/` 下的发布目录）。
发布：写好 `CHANGELOG.md` 对应版本段落后运行 `tools/local/release.sh --bump patch --push`。
解码库：`build/native/build-codecs.{sh,ps1}` 按 RID 编译到 `.cache/native/<rid>/`，CI 会缓存；本地 Debug 运行时有就复制。
图标：改 `build/resources/icon/*.svg` 后运行 `tools/make_icons.sh`；MDI 图标清单在 `tools/gen_icons.py`。

## 许可

MIT，见 `LICENSE`。Windows / Linux 安装包附带的 libheif、libde265 为 LGPL-3.0，dav1d 为 BSD-2-Clause；Linux 安装包附带的 libtiff 为 libtiff 许可，libjpeg-turbo 为 IJG/BSD-3-Clause。许可全文和源码地址在安装目录的 `licenses/` 下。
