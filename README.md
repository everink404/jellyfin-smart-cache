# jelly cache

本项目从零开发 Jellyfin 剧集智能预取缓存系统：用户观看当前集时预取同季后续集，在 NAS 有限空间内改善连续观看体验。后续播放通过稳定媒体入口优先读取完整有效的本地缓存，未命中时尝试原远端来源；保留原媒体条目、续播和播放历史。

2026-10-06 已取得目标文档 v0.1 草案。已有缓存核心、只读文件入口、后台预取队列、控制服务与 Jellyfin 12.1 桥接原型；随后优先推进单插件路线。2026-10-12 用户容器已验证单插件缓存转码播放、拖动与回源；0.3已连接事件、预取、存储、媒体源包装和配置页，167项C#检查通过，自动预取仍待真实容器验收。各批检查与验收分别见 STATUS。

## 测试插件安装

公开仓库：[everink404/jellyfin-smart-cache](https://github.com/everink404/jellyfin-smart-cache)。[v0.1.0-test.1 测试安装包](https://github.com/everink404/jellyfin-smart-cache/releases/tag/v0.1.0-test.1) 已发布。

在独立 Jellyfin 测试实例的插件仓库中添加以下地址。自动预取实验请选择 **`JellyCache single-plugin read test` 0.3.0.0**，升级后重启；保留原插件名与GUID以支持升级。旧的 `JellyCache experimental bridge` 是独立的事件桥接版。

**0.3.0.0 自动预取测试版已通过原 Pages 插件库发布**，公开清单与安装包校验通过，测试步骤见 [自动预取验收](docs/PREFETCH_TEST.md)。它沿用原插件身份，新增自动模式；新安装默认关闭，升级保留手动模式。0.2旧包与版本记录保留。

```text
https://everink404.github.io/jellyfin-smart-cache/manifest.json
```

清单与 ZIP 均通过同一 GitHub Pages 站点提供。若之前添加过 Raw 地址，请停用旧条目后使用新地址；否则 Jellyfin 仍会请求旧站点。NAS 是否可访问新站点需实际安装验证。

此仓库清单专供实验测试，目标 Jellyfin 12.1.0 / .NET 10，默认禁用。读取测试版无需 Worker/FUSE，通过网页配置指定一集与完整本地副本，没有自动预取；当前用户容器已验证缓存转码和回源，直放未验证，见 [读取测试步骤](docs/READ_PROBE_TEST.md) 与 STATUS。旧桥接版的依赖见 [桥接说明](docs/JELLYFIN_PLUGIN.md)。暂不添加许可证。

## 资料导航

- [项目目标](docs/Jellyfin剧集智能预取缓存系统-项目目标文档.md)：产品范围、设计基线和 v0.1 验收要求。
- [首批接入验证方案](docs/INTEGRATION_VALIDATION.md)：当前环境信息缺口、最小验证步骤与通过条件。
- [来源诊断脚本](scripts/probe-source.sh)：在 NAS 核对现有文件夹映射和读取前提，使用方法见验证方案。
- [缓存核心原型](docs/CACHE_CORE.md)：已实现能力、运行与测试方法及当前限制。
- [稳定文件入口测试](docs/FILESYSTEM_TEST.md)：Linux 可选 FUSE 入口、独立测试步骤与证据限制。
- [后台队列与控制服务](docs/WORKER_SERVICE.md)：窗口、限速、暂停、重试及事件契约。
- [Jellyfin 事件桥接插件](docs/JELLYFIN_PLUGIN.md)：12.1 插件、构建、测试配置与连接前提。
- [单插件接入验证](docs/SINGLE_PLUGIN_VALIDATION.md)：优先调查插件内部缓存切换，记录源码／接口证据及真实播放缺口。
- [单插件读取测试版](docs/READ_PROBE_TEST.md)：独立测试插件的配置、同条目读取与回源验证。
- [自动预取测试版](docs/PREFETCH_TEST.md)：0.3.0.0 配置、E01→E02/E03 预取和实际命中验收。
- [单插件预取演进](docs/SINGLE_PLUGIN_PREFETCH.md)：已验证版本的保留检查点、进程内预取实现顺序与当前代码边界。
- [单插件存储核心](docs/SINGLE_PLUGIN_STORAGE.md)：受限复制、容量预留、完整校验、持久恢复与占用租约的实现和接入限制。
- [GitHub 测试分发](docs/GITHUB_DISTRIBUTION.md)：源码上传、插件打包、测试 Release 与 Jellyfin 清单。
- [项目协作约定](docs/REVIEW_WORKFLOW.md)：本项目采用的最小流程、授权边界与整理规则。
- [当前状态与批次记录](docs/STATUS.md)：当前任务、检查证据、授权、暂停、验收及剩余事项。
- [执行入口说明](AGENTS.md)：后续协作先读取哪些项目资料。

## 目录与使用

根目录是资料入口；`docs/` 保存目标、设计与状态，`jellycache/` 是 Python Worker 和文件入口，`plugin/` 是 C# 插件与协议检查，`tests/` 是本机测试，`scripts/` 是诊断和构建脚本。`.tools/` 是本地开发工具及依赖缓存，bin/obj 是构建产物；这些生成目录不作为项目资料入口。

缓存和队列使用 Python 3.11+ 标准库；可选 Linux FUSE 绑定单独列在 `requirements-fuse.txt`。根目录运行 `python -m unittest discover -s tests -v`；手工命令为 `python -m jellycache --help`，队列服务为 `python -m jellycache.serve --help`。插件需要 .NET 10 SDK，Windows 使用 `powershell -File scripts/build-plugin.ps1`。实际配置和验证步骤见对应文档；正式部署待 NAS 证据。

历史授权、暂停与验收记录未在当前目录中发现；这不代表这些记录不存在。以后取得记录时保留原件和来源，核对有效范围后补充状态，不凭本次流程启用恢复开发或推定验收通过。
