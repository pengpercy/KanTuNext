# Changelog

## 0.1.2

- 主窗口记住上次的大小、位置和最大化状态；保存的位置已不在任何屏幕上时（如拔掉了外接显示器），按原大小居中打开。

## 0.1.1

- Linux 支持 TIFF：安装包自带 libtiff（内含 libjpeg-turbo），支持 LZW、Deflate、PackBits、JPEG、传真等常见压缩，以及调色板、灰度、16 位和带透明的图片；多页 TIFF 显示第一页。

## 0.1.0

首个版本：离线看图与图片整理，不联网，不收集任何数据。

**浏览与整理**
- 按月份分组的缩略图时间线，有小 / 中 / 大 / 最大四档尺寸，可以用键盘选图。
- 常用文件夹，可展开子文件夹；文件夹内容变化后自动刷新。
- 筛选：图片、视频、大图片、屏幕快照；「重复照片」可以一键找出内容完全相同的文件。
- 标签：好 / 中 / 差。「最近删除」可以把移到废纸篓的图片恢复到原位置。
- 找图：按文件名或日期（如 2016、2016年6月、6月）筛选。

**看图**
- 适应窗口、实际大小、缩放、旋转，支持播放 GIF / WebP 动图。先按屏幕尺寸解码，放大后才载入原图，所以占用内存少。
- 实况照片（HEIC/JPG + MOV）：在缩略图上有标记；macOS 上可以在窗口内播放动态部分。
- 识别文字（OCR），完全离线：macOS 用系统 Vision，Windows 用系统 OCR，Linux 安装包自带 tesseract（中文 + 英文）。
- 简介：名称、类型、尺寸、大小、拍摄时间、位置。

**格式**
- JPEG、PNG、GIF、WebP、BMP、ICO、HEIC/HEIF、AVIF；macOS 和 Windows 还支持 TIFF，macOS 上另外支持 JPEG XL、PSD 和相机 RAW。
- Windows 和 Linux 安装包自带 libheif（内含 libde265、dav1d），不用另装系统解码扩展就能打开 HEIC/AVIF。
- 视频缩略图（macOS、Windows）。

**平台**
- Windows 10/11 x64、arm64，另有 Windows 7 x64 版本；macOS 12 及以上（Intel / Apple Silicon）；Linux x64 / arm64（AppImage、deb、rpm，需要 glibc 2.31 及以上）。
- macOS 版没有经过公证。第一次打开时，请在 Finder 里右键点 KanTuNext.app →「打开」，或者执行 `xattr -dr com.apple.quarantine /Applications/KanTuNext.app`。
