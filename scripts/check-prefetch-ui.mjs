import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const html = fs.readFileSync(new URL('../plugin/JellyCache.ReadProbe/Configuration.html', import.meta.url), 'utf8');
const elements = new Map([...html.matchAll(/id="([^"]+)"/g)].map(m => ['#' + m[1], {
    value: '', checked: false, hidden: false, textContent: '', events: new Map(),
    addEventListener(name, handler) { this.events.set(name, handler); }
}]));
const page = elements.get('#JellyCacheReadPage');
page.querySelector = id => { assert(elements.has(id), `Unknown element ${id}`); return elements.get(id); };
let configuration = { Enabled: true, AutomaticPrefetch: true, MediaRoot: '/video', CacheDirectory: '/excache/managed',
    CapacityBytes: 10737418240, MinimumFreeBytes: 0, BytesPerSecond: 8388608, Strategy: 'ahead', Ahead: 2,
    ValidationRevision: 4 };
let updated;
let clearCalls = 0, confirmation = true;
vm.runInNewContext(html.match(/<script>([\s\S]*?)<\/script>/)[1], {
    document: { querySelector: () => page },
    window: { confirm: () => confirmation },
    ApiClient: {
        getPluginConfiguration: async () => ({ ...configuration }),
        getUrl: path => path,
        updatePluginConfiguration: async (_, value) => { updated = value; return {}; },
        ajax: async ({ url, type }) => url.endsWith('ClearUnoccupied')
            ? (assert.equal(type, 'POST'), clearCalls++, {State:'completed',DeletedFiles:2,DeletedBytes:1073741824,SkippedProtected:1})
            : url.endsWith('PrefetchStatus')
            ? { State: 'ready', RetainedSelections: 1, CacheSelections: 3, OriginSelections: 1, LifecycleRouting: true,
                Usage: { CompleteBytes: 8192, ReservedBytes: 0 }, Queue: [{ EpisodeNumber: 2, State: 'ready' }],
                LastAttempt: { Outcome: 'copy-failed', Reason: 'disk-safety-margin', ErrorType: 'CacheCopyException', Failures: 1, ElapsedMilliseconds: 1200 },
                Protections: [{Id: '12345678-fixture', Request: { Kind: 'video-request', JobState: 'matching-job-exited' }}] }
            : { State: 'disabled', CacheSelections: 0, FallbackSelections: 0 }
    },
    Dashboard: { hideLoadingMsg() {}, showLoadingMsg() {}, processPluginConfigurationUpdateResult() {} }
});
const flush = () => new Promise(resolve => setImmediate(resolve));
page.events.get('pageshow')(); await flush();
assert.equal(elements.get('#FreeGiB').value, 0, 'zero free-space setting preserved');
assert.equal(elements.get('#AutoSettings').hidden, false);
assert.match(elements.get('#ReadStatus').textContent, /第 2 集：缓存就绪/);
assert.match(elements.get('#ReadStatus').textContent, /磁盘安全余量不足/);
assert.match(elements.get('#ReadStatus').textContent, /累计失败 1 次；耗时 1.2 秒/);
assert.match(elements.get('#ReadStatus').textContent, /匹配作业已退出；仍保护至重启/);
assert.match(elements.get('#ReadStatus').textContent, /按请求和任务结束释放/);
elements.get('#ClearUnoccupied').events.get('click')(); await flush();
assert.equal(clearCalls, 0, 'unpaused cleanup is not sent');
elements.get('#CapacityGiB').value = '0.25';
elements.get('#RateMiB').value = '0.5';
elements.get('#PausePrefetch').checked = true;
elements.get('#JellyCacheReadForm').events.get('submit')({ preventDefault() {} }); await flush();
assert.equal(updated.CapacityBytes, 268435456, 'GiB converted to bytes');
assert.equal(updated.BytesPerSecond, 524288, 'MiB/s converted to bytes');
assert.equal(updated.MinimumFreeBytes, 0);
assert.equal(updated.PausePrefetch, true);
assert.equal(updated.AutomaticPrefetch, true);
assert.equal(updated.ValidationRevision, 5, 'save enables failed initialization retry');
confirmation = false;
elements.get('#ClearUnoccupied').events.get('click')(); await flush();
assert.equal(clearCalls, 0, 'declined cleanup is not sent');
confirmation = true;
elements.get('#ClearUnoccupied').events.get('click')(); await flush(); await flush();
assert.equal(clearCalls, 1);
assert.match(elements.get('#ClearStatus').textContent, /已删除 2 项，1.00 GiB；跳过占用 1 项/);
assert.equal(elements.get('#ClearUnoccupied').disabled, false);
configuration = { Enabled: false, SourcePath: '/video/E1.mkv', CachePath: '/excache/E1.mkv' };
page.events.get('pageshow')(); await flush();
assert.equal(elements.get('#ManualSettings').hidden, false, 'legacy config retains manual mode');
assert.equal(elements.get('#AutoSettings').hidden, true);
assert.equal(elements.get('#ReadSource').value, configuration.SourcePath);
assert.match(elements.get('#ReadStatus').textContent, /未启用/);
console.log('Configuration UI smoke checks passed (simulated DOM/API; no real Jellyfin browser).');
