# Changelog

## 0.1.0

- 工程骨架：Avalonia 12 + NativeAOT，7 个发布目标（Windows x64/arm64/Win7、macOS Intel/Apple Silicon、Linux x64/arm64）。
- HEIC/HEIF、AVIF：Windows 和 Linux 自带 libheif（内含 libde265、dav1d），不依赖系统解码扩展；macOS 仍用 ImageIO。
- 主窗口外壳：侧栏（常用文件夹 / 筛选 / 标签）、工具栏（找图、小/中/大/最大）、空状态，跟随系统深浅色。
