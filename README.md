# jelly cache

本项目从零开发 Jellyfin 剧集智能预取缓存系统：用户观看当前集时预取同季后续集，在 NAS 有限空间内改善连续观看体验。后续播放通过稳定媒体入口优先读取完整有效的本地缓存，未命中时尝试原远端来源；保留原媒体条目、续播和播放历史。

2026-10-06 已取得目标文档 v0.1 草案。已实现缓存核心、只读文件入口、后台预取队列、受限控制服务与 Jellyfin 12.1 事件桥接原型。本机 46 项 Python 测试通过，插件编译与 146 项协议断言通过；NAS 挂载、真实插件加载、播放与部署尚未验证。

## 资料导航

- [项目目标](docs/Jellyfin剧集智能预取缓存系统-项目目标文档.md)：产品范围、设计基线和 v0.1 验收要求。
- [首批接入验证方案](docs/INTEGRATION_VALIDATION.md)：当前环境信息缺口、最小验证步骤与通过条件。
- [来源诊断脚本](scripts/probe-source.sh)：在 NAS 核对现有文件夹映射和读取前提，使用方法见验证方案。
- [缓存核心原型](docs/CACHE_CORE.md)：已实现能力、运行与测试方法及当前限制。
- [稳定文件入口测试](docs/FILESYSTEM_TEST.md)：Linux 可选 FUSE 入口、独立测试步骤与证据限制。
- [后台队列与控制服务](docs/WORKER_SERVICE.md)：窗口、限速、暂停、重试及事件契约。
- [Jellyfin 事件桥接插件](docs/JELLYFIN_PLUGIN.md)：12.1 插件、构建、测试配置与连接前提。
- [GitHub 测试分发](docs/GITHUB_DISTRIBUTION.md)：源码上传、插件打包、测试 Release 与 Jellyfin 清单。
- [项目协作约定](docs/REVIEW_WORKFLOW.md)：本项目采用的最小流程、授权边界与整理规则。
- [当前状态与批次记录](docs/STATUS.md)：当前任务、检查证据、授权、暂停、验收及剩余事项。
- [执行入口说明](AGENTS.md)：后续协作先读取哪些项目资料。

## 目录与使用

根目录是资料入口；`docs/` 保存目标、设计与状态，`jellycache/` 是 Python Worker 和文件入口，`plugin/` 是 C# 插件与协议检查，`tests/` 是本机测试，`scripts/` 是诊断和构建脚本。`.tools/` 是本地开发工具及依赖缓存，bin/obj 是构建产物；这些生成目录不作为项目资料入口。

缓存和队列使用 Python 3.11+ 标准库；可选 Linux FUSE 绑定单独列在 `requirements-fuse.txt`。根目录运行 `python -m unittest discover -s tests -v`；手工命令为 `python -m jellycache --help`，队列服务为 `python -m jellycache.serve --help`。插件需要 .NET 10 SDK，Windows 使用 `powershell -File scripts/build-plugin.ps1`。实际配置和验证步骤见对应文档；正式部署待 NAS 证据。

历史授权、暂停与验收记录未在当前目录中发现；这不代表这些记录不存在。以后取得记录时保留原件和来源，核对有效范围后补充状态，不凭本次流程启用恢复开发或推定验收通过。
