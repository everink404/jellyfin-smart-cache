# 后台队列与控制服务原型

2026-10-06，批次 B-20261006-05。Python 3.11+ 标准库实现，与缓存入口共享一个 CacheStore；运行期间无需再启动另一个缓存进程。

## 当前行为

- 仅处理 Episode，按明确的同剧、同季 ID 与集数排序；默认 Ahead 2，支持 `next`、`ahead`（1–50）和 `season`。不根据文件名猜季集，不跨季，不支持特别篇或合并集。
- 同路径任务去重，按与当前集的距离优先，同距离在会话间轮转。跳集更新窗口，无其他会话需要的未开始任务取消；正在复制的任务允许完成。
- 单复制任务，默认源读取平均上限 8 MiB/s。容量不足等待 10 秒再检查，不消耗重试次数；读取等异常最多尝试 3 次，指数退避；终止失败后需要显式重试。
- 当前播放文件有逻辑保护；文件入口打开句柄另有实际读占用保护。暂停会话不启动新任务；管理员暂停还暂停正在复制的任务，在块边界生效。
- 默认 120 秒没有新事件则释放会话需求。重启保留管理员暂停和任务记录，旧会话需求失效，收到新事件才继续预取。
- `queue.sqlite` 保存需求与任务；控制接口只监听 `127.0.0.1`，Bearer token 至少 32 字符。请求限 1 MiB；不记录 token、原始会话或异常完整文本。

## 独立运行

先在当前终端把 `JELLYCACHE_CONTROL_TOKEN` 设置为自行随机生成的秘密值，不能使用文档中的固定示例。以下路径和容量为占位示例，缓存必须是独立专用目录。

```bash
python3 -m jellycache.serve \
  --source '/path/to/test-source' --cache '/path/to/dedicated-test-cache' \
  --capacity-bytes 10737418240 --safety-bytes 2147483648 \
  --rate-bytes 8388608 --control-port 8765
```

这只启动后台预取和控制，不能让原 `/video` 自动命中。Linux 已具备 [文件入口测试](FILESYSTEM_TEST.md) 的依赖后，可在同一命令增加 `--mountpoint '/path/to/empty-test-view'`，队列和文件入口共享同一个进程；跨用户读取按需显式加 `--allow-other`。

## 控制契约

所有接口需要 `Authorization: Bearer <token>`；POST 需要 `Content-Type: application/json`。token 放请求头，不放 URL。

| 路径 | 用途 |
|---|---|
| GET `/v1/status` | 暂停、速率、会话数、任务状态；最多返回最近 500 条任务 |
| POST `/v1/events` | 标准化播放事件，桥接插件使用 |
| POST `/v1/jellyfin-events` | 提交完整的 Jellyfin 会话及同季剧集 DTO 快照；本身不订阅 Jellyfin |
| POST `/v1/pause`、`/v1/resume` | 请求体 `{}`，修改管理员暂停 |
| POST `/v1/retry` | 请求体 `{"path":"Series/Season 01/E02.mkv"}`，重置仍被需要的失败或空间等待任务 |

标准化 start 示例（虚构元数据；需要来源中实际存在对应文件）：

```json
{
  "session_id": "test-session", "sequence": 1, "phase": "start",
  "current": {"type":"Episode","id":"e1","series_id":"series","season_id":"season1","index":1,"season_number":1,"path":"S1/E01.mkv"},
  "episodes": [
    {"type":"Episode","id":"e1","series_id":"series","season_id":"season1","index":1,"season_number":1,"path":"S1/E01.mkv"},
    {"type":"Episode","id":"e2","series_id":"series","season_id":"season1","index":2,"season_number":1,"path":"S1/E02.mkv"},
    {"type":"Episode","id":"e3","series_id":"series","season_id":"season1","index":3,"season_number":1,"path":"S1/E03.mkv"}
  ], "strategy": "ahead", "ahead": 2
}
```

同一会话 sequence 必须递增且小于 2^63，重复或迟到事件忽略。`progress`、`pause` 要提供同样完整元数据；`stop` 只需要 session_id、sequence、phase。DTO 接口另需要 envelope 的 `session`、`episodes`、`sequence`、`phase`；当前条目必须唯一匹配，选中的多版本来源必须明确，未来多版本不猜测。

## 验证和限制

Windows / Python 3.12：46 项测试通过，包括真实本机 HTTP 认证、窗口调整、共享需求、暂停、重启、超时、容量等待、重试、平均限速、关闭清理及“事件 → 预取 → 同路径新句柄命中”闭环。测试只使用合成小文件；不代表云盘吞吐或 NAS 播放结果。

源挂载内部预读不受 Python 限速控制，当前没有根据播放器缓冲或带宽自动调速。原生阻塞读取无法立即取消；关闭等待 5 秒后仍阻塞会报告错误。每个原型实例最多 128 个历史会话、10,000 个任务，暂未实现记录保留与自动整理策略。队列 SQL 元数据不计入媒体容量预算，仍受磁盘余量约束。

缓存来源身份、冷启动校验、已打开句柄错误和入口故障限制继续见 [缓存核心](CACHE_CORE.md) 与 [文件入口](FILESYSTEM_TEST.md)。Jellyfin 事件订阅另见 [插件桥接](JELLYFIN_PLUGIN.md)。
