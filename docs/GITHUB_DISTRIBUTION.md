# GitHub 与 Jellyfin 测试分发

公开仓库：[everink404/jellyfin-smart-cache](https://github.com/everink404/jellyfin-smart-cache)。暂不添加许可证。公开可见不代表已授予开源使用许可。

源码保存在 GitHub；测试插件 ZIP 与 SHA-256 摘要放在 GitHub Releases；根目录 `manifest.json` 供 Jellyfin 读取。源码版本、Release 和清单应指向同一批构建产物。[首次测试版 v0.1.0-test.1](https://github.com/everink404/jellyfin-smart-cache/releases/tag/v0.1.0-test.1) 于 2026-10-11 发布并标记为 prerelease。

## 打包与发布顺序

1. 运行 `powershell -File scripts/build-plugin.ps1`，构建并完成协议检查。
2. 运行 `powershell -File scripts/package-plugin.ps1`，输出到忽略的 `.tools/distribution/v0.1.0-test.1/`。只包含 ZIP 根目录下的插件 DLL 与 deps.json，不包含 Jellyfin 服务端 DLL、PDB、SDK、NuGet 缓存或运行数据。
3. 检查 ZIP 内容、插件 GUID、程序集版本和 ABI。首次程序集版本为 `0.1.0.0`，目标 ABI 为 `12.1.0.0`。Jellyfin 使用数值版本，不能在清单 version 内填写 `-test.1`；测试身份通过插件名称、说明和预发布 Release 表达。
4. 上传源码并核验远程提交；创建测试 Release，上传 ZIP 和 SHA256SUMS.txt，保留已发布文件，修复版使用新插件版本与标签。
5. 匿名下载安装包，核对 MD5、SHA-256 和 ZIP 内容。Jellyfin 清单 checksum 按其安装器要求使用 ZIP 的 MD5，另提供 SHA-256 摘要。
6. 安装包核验通过后，将本批生成的清单保存到根目录 `manifest.json` 并上传。匿名读取清单，核对 sourceUrl 可下载及 checksum 一致。

Jellyfin 测试仓库 URL：

```text
https://everink404.github.io/jellyfin-smart-cache/manifest.json
```

根清单专用于测试分发；不要把它作为生产更新源。以后稳定版与测试版并存时，分别维护分发清单，避免生产实例收到测试更新。测试安装包已完成匿名下载与 MD5、SHA-256 复核；清单远程检查结果见 [状态记录](STATUS.md)。

## GitHub Pages 备用分发

2026-10-11，用户报告安装失败；提供的日志确认 Jellyfin 在访问 raw.githubusercontent.com 清单时发生 TLS 连接重置。用户随后明确批准“允许增加 GitHub Pages 入口”。

Pages 从 `codex/plugin-distribution` 分支根目录发布，仅包含 `.nojekyll`、说明首页、manifest.json、版本化 ZIP 与摘要。清单 sourceUrl 使用同一 everink404.github.io 站点，避免安装时跳到 Releases 下载域名。原 Release 和原始包保留，插件版本、GUID、目标 ABI 和校验值不变。

使用新地址时停用旧的 Raw 仓库条目，否则 Jellyfin 仍可能请求旧站点。Pages 网络与原 Raw 网络不同，但不能据此保证 NAS 可访问；需要实际目录读取与安装确认。无需降低 TLS 校验或更改生产媒体映射。

后续新版本先完成原包检查，再把相同 ZIP、摘要和更新后的 Pages 清单提交到该分支；保留旧版本包路径。GitHub 使用内置 Pages 构建部署流程，不新增源码构建、定时发布或自动更新插件工作流。

## 安装测试

在独立 Jellyfin 测试实例的管理后台插件仓库中添加上述清单地址，在目录找到 `JellyCache experimental bridge`，安装后重启实例。目标为 Jellyfin 12.1.0 / .NET 10；较低版本可能被 ABI 过滤，较高版本显示也不代表已验证兼容。实际服务端完整版本仍需核实。

插件默认禁用，启用变量及连接要求见 [插件说明](JELLYFIN_PLUGIN.md)。单独安装插件不会自动部署 Worker 或切换媒体读取来源。未验证真实 Jellyfin/fnOS 加载、Linux FUSE、Docker 可见性、播放、续播或拖动；不能擅自修改生产 `/video` 映射。

技术格式参考：[官方清单模型](https://github.com/jellyfin/jellyfin/blob/master/MediaBrowser.Model/Updates/PackageInfo.cs)、[官方安装器](https://github.com/jellyfin/jellyfin/blob/master/Emby.Server.Implementations/Updates/InstallationManager.cs)。master 用于调查，发布验证应以构建所用的固定 12.1.0 开发包与实际服务器为准。
