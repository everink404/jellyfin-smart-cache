# 文件来源缓存核心原型

日期：2026-10-06。核心使用 Python 3.11+ 标准库，无第三方依赖。已另建 [可选稳定文件入口](FILESYSTEM_TEST.md)、[后台队列](WORKER_SERVICE.md) 与 [插件桥接](JELLYFIN_PLUGIN.md) 原型，尚未接入真实 NAS 播放链路。

## 已实现

- 从来源目录按相对文件路径预取，一次一个复制任务；路径包含空格可正常处理。
- SQLite 保存来源大小、修改时间、缓存摘要、完成状态和最近读取时间。
- 完整复制、同步文件、检查大小与来源信息后原子公布；未完成文件不参与命中。
- 缓存容量包含完整文件与在途文件预留；检查磁盘安全余量。空间不足拒绝预取，读取仍可选择原来源。
- 读取前验证来源信息与本地摘要，未命中、失效、校验失败或缓存校验读错误则尝试原来源。
- 读取句柄支持 seek；句柄关闭前保护对应缓存不被 LRU 淘汰。
- 单实例缓存所有权锁；重启清理由本项目索引记录的未完成文件。不清理不明文件，拒绝接管非空且无所有权标记的目录。

## 手工运行

在项目根目录执行。`--source` 可指 NAS 原来源路径，若在容器中运行则使用实际可见的来源映射；`--cache` 必须是独立的本地测试目录，不能放在来源树中，也不能使用 fnOS 内部缓存目录。

以下使用占位路径，执行前替换。容量与安全余量由测试磁盘可用空间决定，示例分别为 10 GiB 和 2 GiB。

```bash
python3 -m jellycache --source '/path/to/test-source' --cache '/path/to/dedicated-test-cache' --capacity-bytes 10737418240 --safety-bytes 2147483648 probe 'Series/Season 01/E02.mkv'
python3 -m jellycache --source '/path/to/test-source' --cache '/path/to/dedicated-test-cache' --capacity-bytes 10737418240 --safety-bytes 2147483648 prefetch 'Series/Season 01/E02.mkv'
python3 -m jellycache --source '/path/to/test-source' --cache '/path/to/dedicated-test-cache' --capacity-bytes 10737418240 --safety-bytes 2147483648 probe 'Series/Season 01/E02.mkv' --offset 1048576
```

第一次 probe 期望 `origin=source`；prefetch 成功后期望 `origin=cache`。probe 只输出来源、偏移、读取字节数和该段摘要，不输出视频内容。prefetch 会读取完整来源文件并写入测试缓存。手工命令不限制速率；后台服务默认限速 8 MiB/s，但不能限制来源挂载内部预读，实际播放竞争需测试。

测试：`python3 -m unittest discover -s tests -v`。测试自行生成很小的虚构媒体文件，不访问 NAS；专用测试目录生成于项目根目录，测试完成后清理。

## 明确限制与后续

- `open_media` 是库接口，Jellyfin 尚不会调用它。原 `/video` 映射不自动得到缓存能力，不能据此宣称端到端播放通过。
- B-20261006-04 已把本地整文件校验移到复制完成、缓存公布之前；同一进程中经验证且本地信息未变化的后续打开跳过整文件校验。重启或文件信息变化后的首次命中仍需全文件校验。起播改善尚未实测，元数据不变的静默磁盘损坏仍需后续周期校验策略。
- 来源大小与修改时间只是临时版本提示；若远端内容变化但两者不变，无法可靠发现。必须核实 fnOS 挂载可提供的身份信息或采用明确的再验证策略。
- 来源不可 stat 或已消失时保守拒绝使用缓存；原来源也不可用时读取可能失败。当前不提供离线播放保证。
- 容量计算针对受管理媒体与预留，索引等少量元数据通过磁盘安全余量留空间。B-20261006-05 已增加块级限速与取消、重试退避、窗口、播放文件逻辑保护和管理服务；仍无法强制取消挂载的阻塞读取，未实现根据播放带宽自动调速。
- 不完整文件重启后清理重新下载，尚不支持断点续传。只验证 Windows 本机运行；Linux 所有权锁分支、NAS 挂载和客户端直放／转码仍待验证。
- 目录假定由该进程独占管理；不抵御外部进程同时替换缓存或来源路径。禁止多个缓存进程、Jellyfin 或 fnOS 向内部缓存文件写入。

下一步结合 [接入验证方案](INTEGRATION_VALIDATION.md) 收集 NAS 读取证据，运行已建立的独立测试入口。原型成果不改变目标文档设计基线，也不替代该验证。
