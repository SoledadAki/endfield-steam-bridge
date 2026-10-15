// Run with Node.js after CoreTests.exe --export-scripts <absolute-json-path>.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const scripts = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
let count = 0;
function run(script, channel, options = {}) {
    let clicks = 0;
    const label = {
        textContent: options.label || '开始游戏', offsetWidth: 100, offsetHeight: 30,
        getClientRects: () => [1], getBoundingClientRect: () => ({ x: 0, y: 0, width: 100, height: 30 }),
        click: () => clicks++, closest: () => target,
    };
    const target = { closest: () => options.disabled ? {} : null, contains: e => e === label };
    const number = channel === 'bilibili' ? '2' : '1';
    const params = new URLSearchParams({ app_code: '6LL0KJuqHBVz33WK', region: 'cn', is_game: 'true', channel: number, sub_channel: number, launcher_channel_name: channel });
    if (options.region) params.set('region', options.region);
    const state = vm.runInNewContext(script, {
        URLSearchParams, location: { hash: '#?' + params },
        document: { querySelectorAll: () => options.duplicate ? [label, label] : [label], elementFromPoint: () => options.covered ? {} : label },
    });
    return { state, clicks };
}
for (const channel of ['official', 'bilibili']) {
    for (const test of [
        [{}, 'clicked', 1],
        [{ disabled: true }, 'disabled', 0],
        [{ covered: true }, 'covered', 0],
        [{ label: '下载游戏' }, 'not-ready:', 0],
        [{ label: '更新游戏' }, 'not-ready:', 0],
        [{ duplicate: true }, 'not-ready:', 0],
        [{ region: 'sg' }, 'wrong-page', 0],
    ]) {
        const result = run(scripts[channel], channel, test[0]);
        assert.ok(result.state.startsWith(test[1]), JSON.stringify(result));
        assert.equal(result.clicks, test[2]); count++;
    }
    const wrong = channel === 'bilibili' ? 'official' : 'bilibili';
    assert.deepEqual(run(scripts[channel], wrong), { state: 'wrong-page', clicks: 0 }); count++;
}
console.log(`PASS ${count} launcher button scenarios`);
