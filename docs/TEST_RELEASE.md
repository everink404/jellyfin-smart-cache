# v0.1.0-test.1 — JellyCache 测试插件

这是实验预发布版，供独立测试实例验证加载和播放事件桥接，不是生产稳定版本。

- 插件：JellyCache experimental bridge。
- 程序集版本：0.1.0.0。
- 固定开发目标：Jellyfin 12.1.0 / .NET 10；其他服务器版本未验证。
- 插件默认禁用，通过环境变量启用。完整配置见 [插件说明](JELLYFIN_PLUGIN.md)。
- 依赖独立 JellyCache Worker 和稳定媒体入口；回环控制要求共享网络命名空间。单装插件不自动完成缓存部署。
- 未在真实 Jellyfin/fnOS 中加载，未验证 Linux FUSE、Docker 可见性、真实播放、续播、拖动或直放／转码。不要修改生产 `/video` 映射。

本机证据：46 项 Python 测试、146 项桥接协议断言通过；.NET 插件编译零警告、零错误；使用固定 Jellyfin.Model 12.1.0 解析分发清单，ZIP 根目录内容与 MD5 一致性通过。

安装包仅含 JellyCache.Plugin.dll 和 JellyCache.Plugin.deps.json。SHA256SUMS.txt 是单独提供的文件摘要；Jellyfin 清单 checksum 使用 ZIP 的 MD5。

测试仓库地址与安装步骤见 [分发说明](GITHUB_DISTRIBUTION.md)。远程上传和匿名下载检查是否完成，以 [状态记录](STATUS.md) 为准。
