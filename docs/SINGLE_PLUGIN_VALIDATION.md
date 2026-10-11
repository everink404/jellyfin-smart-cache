# 单插件媒体读取接入验证

日期：2026-10-11。用户要求优先验证单插件接入点，暂不继续 Worker/FUSE 容器部署测试。

后续进展：用户已要求制作最小测试插件，现已实现独立 `JellyCache.ReadProbe` 和网页配置；操作见 [读取测试版](READ_PROBE_TEST.md)，批次见 STATUS B-20261011-04。以下 17 项是前一批接口研究的历史证据；新批扩大了检查范围，仍未取得真实播放验收。

## 结论及证据等级

找到一个在固定 Jellyfin 12.1.0 上可继续验证的单插件接入点：通过 `IPluginServiceRegistrator` 包装 `IMediaSourceManager`，在播放协商与服务端取流再次解析来源时，返回保留身份和媒体信息的缓存路径副本。缓存选择、下载、队列可以放在同一插件进程内，技术上不必依赖 Python Worker 或 FUSE。

**本批只通过官方源码与接口夹具实验，尚未通过真实 Jellyfin 插件加载、客户端播放或 NAS 验收。** 包装核心服务是基于公开注册接口的工程方案，并不是官方专门承诺兼容的“缓存过滤接口”。不能从本批结果推定单插件完整系统已经可用。

已发布的 v0.1.0-test.1 仍是旧事件桥接插件。本批未修改其源码、版本、Release、清单或 NAS。

## 固定来源

本地固定 NuGet `Jellyfin.Controller 12.1.0` 的 nuspec 标记官方仓库提交 `ee91c75e777da41a9c4f4855e70adc604fbf2ef8`。本批使用该提交，未用 master 代替版本证据。

- [注册顺序与媒体源服务](https://github.com/jellyfin/jellyfin/blob/ee91c75e777da41a9c4f4855e70adc604fbf2ef8/Emby.Server.Implementations/ApplicationHost.cs)：内置服务先注册，随后调用插件 RegisterServices；IMediaSourceManager 为 singleton，之后通过 AddParts 加入来源提供器。
- [媒体源管理器](https://github.com/jellyfin/jellyfin/blob/ee91c75e777da41a9c4f4855e70adc604fbf2ef8/Emby.Server.Implementations/Library/MediaSourceManager.cs)：静态来源与扩展动态来源合并；原条目来源优先；GetMediaSource 按源 ID 再取来源。
- [播放协商](https://github.com/jellyfin/jellyfin/blob/ee91c75e777da41a9c4f4855e70adc604fbf2ef8/Jellyfin.Api/Helpers/MediaInfoHelper.cs)：PlaybackInfo 调用 GetPlaybackMediaSources，允许探测和路径替换。
- [取流解析](https://github.com/jellyfin/jellyfin/blob/ee91c75e777da41a9c4f4855e70adc604fbf2ef8/Jellyfin.Api/Helpers/StreamingHelpers.cs)：普通视频流重新调用 GetPlaybackMediaSources；已有转码任务复用其既有 MediaSource。
- [编码器输入](https://github.com/jellyfin/jellyfin/blob/ee91c75e777da41a9c4f4855e70adc604fbf2ef8/MediaBrowser.Controller/MediaEncoding/EncodingHelper.cs)：AttachMediaSourceInfo 将 Path/Protocol 写入编码状态；EncoderPath/EncoderProtocol 可覆盖它，必须一起处理。
- [视频直读](https://github.com/jellyfin/jellyfin/blob/ee91c75e777da41a9c4f4855e70adc604fbf2ef8/Jellyfin.Api/Controllers/VideosController.cs)及[文件响应](https://github.com/jellyfin/jellyfin/blob/ee91c75e777da41a9c4f4855e70adc604fbf2ef8/Jellyfin.Api/Helpers/FileStreamResponseHelpers.cs)：静态播放使用 state.MediaPath，PhysicalFileResult 启用 Range。这里只核查源码，未运行 HTTP Range 测试。

## 已运行实验

新增 `plugin/SinglePluginChecks`，独立于发布插件。将官方 MediaSourceManager、LiveStreamHelper、MediaStreamSelector 三个未修改源文件与固定 NuGet 包一起编译，执行注册接口包装及选择实验。外部源码保存在被忽略的 `.tools/`，脚本核查三个源文件 SHA-256。

夹具使用 Episode 子类提供合成媒体描述、16 KiB 二进制文件、空 provider 集合及未使用依赖的空值。不是可播放视频，不启动 Jellyfin 或 ffmpeg，也未测试带用户权限的真实媒体库查询。没有将夹具伪装成服务器集成测试。

执行 `powershell -File scripts/check-single-plugin.ps1`；首次缺少源码时加 `-FetchSource`。需要 .NET 10 和 NuGet；不会安装插件或部署 NAS。运行日志为 `.tools/single-plugin-probe/checks.log`。

17 项检查通过：singleton 注册、播放协商调用标志、取流再次解析、条目及源 ID 和媒体库路径不变、原描述不被修改、静态 DTO 不变、源 ID 查找、媒体参数保留、官方 EncodingHelper 绑定缓存输入、偏移文件读取、不完整缓存回源、损坏回源、协商后删除缓存在下一次解析时回源、源变化失效、禁用回源、独立 provider ID 和重复 provider ID 均不能覆盖原始默认源。

普通 `IMediaSourceProvider` 单独增加本地来源不足以透明切换：实际官方实现中的独立 ID 仍为额外版本；使用重复 ID 也不会覆盖排在前面的原始来源。因此不采用“添加一个 provider 就完成缓存”的推论。

## 尚须验证和解决

1. 真正的测试插件在 12.1 容器中加载；插件注册时原服务可包装、全局消费者实际取得包装实例；与其他修改该服务的插件相容。
2. 在原媒体库 Episode 上分别测试客户端默认播放、续播、拖动、自动下一集、服务器直读、重封装、转码及外置字幕；记录 I/O 或 ffmpeg 输入证据，不能仅看 PlaybackInfo 的 Path。
3. 实验当前保持静态 DTO 原样。直接使用 SMB/文件路径或绕过 PlaybackInfo 的客户端不保证缓存命中；实际用户客户端必须验证。路径替换、符号链接及多版本不能按夹具推定支持。
4. 调用底层管理器仍可能先探测或读取原来源，已有转码任务复用来源；不能承诺缓存命中后完全不访问远端，不能在旧任务中随意切换。
5. 必须实现缓存租约，保护从协商到打开文件、转码持续期间的占用。实验只证明“下一次来源解析”发现缓存缺失时回源；解析后、文件打开前的删除竞态尚未解决，不承诺自动重试。
6. 实验每次计算小文件 SHA-256，源身份仅长度及修改时间；不能直接用于大文件性能或可靠身份结论。需要完整性索引、原子公布及适当验证策略。
7. 研究用 DispatchProxy 及注册包装未处理生产级生命周期、所有异常旁路、跨插件顺序、全接口版本保护或后台资源管理。实际实现应明确原服务释放责任和禁用/卸载恢复行为。

## 下一阶段通过条件

只做单插件最小读取原型：测试配置指定一集与一份完整缓存；默认关闭，不运行预取，不改数据库路径。用同一条目验证命中、未命中、不完整、失效、插件关闭及移除后的原来源读取，并覆盖用户实际使用的客户端与播放模式。

此阶段通过后，再将预取、容量和占用管理迁入同一 C# 插件，并增加网页配置。安装方式目标可以是一个插件，但 Docker 仍需给插件进程可写的本地缓存空间；这不等于需要额外 Worker 容器。
