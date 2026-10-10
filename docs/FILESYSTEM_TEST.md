# 稳定文件入口测试原型

2026-10-06，批次 B-20261006-04。测试路线：只读 FUSE 文件视图。保持相对目录与文件名，将每次新打开的文件绑定到完整缓存或原来源；已打开的句柄不切换来源。

依据：[目标文档](Jellyfin剧集智能预取缓存系统-项目目标文档.md)的稳定媒体入口与新请求切换原则。[mfusepy 官方项目](https://github.com/mxmlnkn/mfusepy)提供路径式 FUSE 绑定，依赖系统 libfuse；本项目参考其回调契约实现适配器，实验依赖固定为 `mfusepy==3.1.1`。这是可测试的技术路线，尚未获得 fnOS 挂载验证。

## 当前成果与验证层次

- `jellycache/filesystem.py`：可在 Windows 或 Linux 测试的目录、属性、打开、按偏移读取与释放逻辑。
- `jellycache/mount.py`：可选 Linux FUSE 适配器与前台运行入口；挂载目录必须已经存在、为空，且与来源和缓存目录互不包含。
- 同一文件路径：先打开的来源句柄保持来源，缓存完成后的新句柄选择本地副本。文件属性由来源提供，不使用缓存哈希文件名作为媒体身份。
- 子目录与普通附属文件如字幕维持原结构；测试视图排除符号链接与特殊文件，只允许读取。
- 读取句柄持有缓存占用保护，多线程在同一句柄上按偏移读取受锁保护。已播放句柄的读错误直接报告，不尝试中途拼接不同来源。
- 本机回调测试通过不等于 Linux 内核挂载通过，更不等于 Jellyfin 播放通过。真正挂载、Docker 可见性、直放／转码、续播和拖动仍需 NAS 证据。

## 缓存校验改进

复制完成后、公布缓存前重新读取本地临时文件验证 SHA-256。当前进程保存经验证文件句柄的大小、修改时间、变更时间、inode 和设备信息；后续打开时信息不变则避免反复整文件校验。文件信息变化或进程重启后首次命中重新校验。

这减少正常重复命中的额外全盘读取，但冷启动首次命中仍可能较慢；尚无吞吐或起播耗时实测。元数据不变的静默磁盘损坏不能由此检测，后续需要周期校验策略。来源版本提示依然只是大小与修改时间，尚待 fnOS 能力核实。

## NAS 独立测试步骤

先完成 [来源诊断](INTEGRATION_VALIDATION.md)，确认 Python 3.11+、系统 libfuse、`/dev/fuse` 和挂载权限可用。可选绑定安装到项目专用虚拟环境，不替换系统 Python：

```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements-fuse.txt
.venv/bin/python -m unittest discover -s tests -v
```

此安装尚未在 NAS 或当前开发机执行。系统依赖未满足时先记录缺口，不默认修改 fnOS 系统设置。

使用独立空测试挂载目录和本地缓存目录，示例中缓存及挂载路径需替换为实际专用位置。来源路径已按用户提供值引用，保留空格。

```bash
.venv/bin/python -m jellycache.mount \
  --source '/vol02/1000-1-e66ba492/apps/Cloud Sync/video' \
  --cache '/path/to/dedicated-test-cache' \
  --mountpoint '/path/to/empty-test-view' \
  --capacity-bytes 10737418240 --safety-bytes 2147483648 \
  --warm '某剧/Season 01/E02.mkv'
```

`--warm` 可以重复指定，在挂载前完成指定文件预取；其他文件仍回源。运行日志记录每次打开的句柄、来源和相对路径。日志可验证来源选择，不单靠画面或播放速度猜测命中。

挂载进程独占缓存索引；不能另起手工 prefetch 使用同一缓存目录。B-20261006-05 已增加 [同进程后台队列与控制服务](WORKER_SERVICE.md)：需要运行中预取时改用 `python -m jellycache.serve ... --mountpoint ...`，通过受限 HTTP 接口提交事件。原 `jellycache.mount --warm` 保留作为简单测试工具。

先用普通文件读取或已有 ffprobe 工具确认随机读取，再接独立 Jellyfin 测试环境。当前生产 `/video` 映射保持原样。测试容器可将该视图映射为 `/video`，但须事先核实用户权限、挂载可见性、宿主挂载生命周期和恢复步骤；不能把新目录直接加入正式媒体库并宣称没有重复条目。

默认挂载仅限挂载用户；跨用户读取需显式使用 `--allow-other`，并核对主机 FUSE 配置与来源权限。入口启用 `default_permissions`，不默认放宽来源文件权限。

在独立测试挂载目录执行卸载，例如 `fusermount3 -u '/path/to/empty-test-view'`；只用于此测试路径。入口退出或异常时该视图无法继续服务，当前没有自动旁路。恢复使用原来源的容器映射属于后续明确恢复方案，不能以 Worker 可停止推定入口故障不影响播放。

[Docker 官方说明](https://docs.docker.com/engine/storage/bind-mounts/)列出绑定挂载及传播设置。建议先在宿主完成测试挂载，再创建独立测试容器；不推定已有容器会看到后来产生或重建的挂载。

## 测试命令

```bash
python -m unittest discover -s tests -v
python -m jellycache.mount --help
```

目前实际执行环境为 Windows / Python 3.12，测试对象为可移植回调和错误映射。未安装 FUSE 绑定、未调用真实内核挂载、未连接 NAS。
