# Jellyfin 12.1 事件桥接原型

2026-10-06，批次 B-20261006-05。插件源文件位于 `plugin/JellyCache.Plugin/`，构建产物为 `plugin/JellyCache.Plugin/bin/Release/net10.0/JellyCache.Plugin.dll`。默认禁用，通过环境变量在独立测试实例中启用；没有写入现有 NAS 或 Jellyfin。

## 行为和兼容性

订阅 Jellyfin 的 PlaybackStart、PlaybackProgress、PlaybackStopped；播放回调仅更新有界内存缓冲，不查询媒体库或发送 HTTP。后台每 5 秒取最新会话事件，从媒体库取得同季剧集元数据后提交 Worker。窗口默认 Ahead 2。停止或电影事件清除剧集需求；不支持的元数据提交 stop，避免继续旧窗口。网络失败记录无隐私的错误类型，后续进度事件重新提交；会话断开依赖 Worker 超时释放。

每会话事件合并且序号递增，上一播放的迟到 stop/progress 不覆盖新 start；最多记录 128 个会话。当前原型拒绝多版本、堆叠文件、虚拟集、特别篇和合并集；单一来源的当前 MediaSourceId 必须能解析并对应条目 ID。任一同季条目不明确会保守暂停该会话预取，不自行跳过猜测。

已使用固定 NuGet 包 Jellyfin.Controller / Jellyfin.Model 12.1.0、.NET SDK 10.0.401 编译通过，零警告、零错误；146 项协议断言验证路径边界、端点限制、合并、序号、迟到事件和缓冲上限。未在真实 Jellyfin 加载，未验证服务器依赖注入、事件触发、媒体源 ID 或客户端行为。

开发依据：[Jellyfin 官方插件模板](https://github.com/jellyfin/jellyfin-plugin-template)、[官方会话事件接口](https://github.com/jellyfin/jellyfin/blob/master/MediaBrowser.Controller/Session/ISessionManager.cs)、[12.1.0 开发包](https://www.nuget.org/packages/Jellyfin.Controller/12.1.0)。master 源码仅用于接口调查，实际编译以固定发布包为准。

## 构建

需要 .NET 10 SDK 和 NuGet。Windows 运行 `powershell -File scripts/build-plugin.ps1`：优先使用项目内 `.tools/dotnet/dotnet.exe`，否则使用已安装的 dotnet，按 packages.lock.json 恢复、编译并运行协议检查。不自动下载工具链，也不发布或安装插件。

本批项目内工具链来自 Microsoft 官方 ZIP，SHA-512 已核对：

- [SDK 10.0.401](https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip)
- 摘要：`24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430`

`.tools/` 保存本地工具与 NuGet 缓存，bin/obj 是生成目录，已加入忽略规则。源码与锁文件是项目资料；不要把本地依赖目录当作可发布插件目录。

## 测试配置与连接前提

先核验正在运行的 Jellyfin 完整版本，再准备独立测试实例和插件加载方案。以下配置须进入该测试 Jellyfin 进程的环境：

| 环境变量 | 值／说明 |
|---|---|
| `JELLYCACHE_BRIDGE_ENABLED` | `1` 才启用 |
| `JELLYCACHE_CONTROL_TOKEN` | 与 Worker 相同的至少 32 字符随机秘密值 |
| `JELLYCACHE_CONTROL_URL` | 默认 `http://127.0.0.1:8765/v1/events`；仅接受此回环 IP 的 HTTP events 路径 |
| `JELLYCACHE_MEDIA_ROOT` | 默认 `/video`，与 Jellyfin 实际媒体根目录一致 |
| `JELLYCACHE_STRATEGY` | 默认 `ahead`，另支持 `next`、`season` |
| `JELLYCACHE_AHEAD` | 默认 `2`，1–50 |

Worker 只监听回环。**插件所在 Jellyfin 与 Worker 必须共享网络命名空间**：普通独立容器的 127.0.0.1 不指向 NAS 宿主或另一个容器。当前尚未设计部署配置；不要直接把这套回环配置用于现有生产容器，也不要为连通性随意对外开放控制端口。NAS 诊断后再落实独立测试的进程／容器布局。

事件桥接只触发预取。实际命中还需要 [同进程 Worker 与 FUSE 测试入口](WORKER_SERVICE.md)，将测试视图保持映射为相同 `/video`；单独安装 DLL 不改变媒体读取来源。

多会话控制请求当前串行处理且单次 HTTP 超时 3 秒，大量失败会拖慢心跳；原型不具备生产吞吐保证。桥接错误隔离于后台，不记录路径、用户身份、token 或原始 DTO；尚未获得真实实例稳定性证据。完成测试环境加载、窗口推进、同条目本地命中、续播、拖动、直放／转码和回源证据后，才评估生产接入。
