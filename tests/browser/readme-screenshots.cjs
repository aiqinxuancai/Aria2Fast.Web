// Generates the README screenshots in docs/images using the real frontend with demo data.
// Usage: dotnet build -c Release && node tests/browser/readme-screenshots.cjs
// All anime titles, posters, reviews and release groups below are fictional.
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const readline = require('node:readline');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../..');
const out = path.join(root, 'docs/images');

const MB = 1024 * 1024, GB = 1024 * MB;
const now = Date.now();
const ago = minutes => new Date(now - minutes * 60000).toISOString();

function rng(seed) { return () => { seed |= 0; seed = seed + 0x6D2B79F5 | 0; let t = Math.imul(seed ^ seed >>> 15, 1 | seed); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }

const palettes = [
  ['#ff9a62', '#c2416b', '#3b1c55', '#ffe3a3'], ['#7fd6d0', '#2a7f9e', '#173a5c', '#fff4d6'],
  ['#1f2b56', '#121a36', '#070b1a', '#ffe9a8'], ['#f29bd2', '#8b5cf6', '#2b1a5c', '#fff1fb'],
  ['#e8f0a0', '#7cbf6a', '#2f6b4f', '#fffbe0'], ['#f5c26b', '#b23a48', '#3d1424', '#fff0cf'],
  ['#a9bfd6', '#5a7493', '#26354a', '#e9f1fb'], ['#bfe6ff', '#6fb1e8', '#3765a8', '#ffffff'],
  ['#ffd29a', '#e07a3f', '#6b2f1f', '#fff3e0'], ['#7af0d0', '#2bb3a3', '#0f4c5c', '#fdfdf0'],
  ['#ffc8dd', '#f08bb0', '#7a3b69', '#fff5f9'], ['#5ef2a8', '#2b6f8f', '#121b3a', '#e8fff5'],
];

function poster(index, title, motif) {
  const [a, b, c, light] = palettes[index % palettes.length];
  const r = rng(index * 977 + 13);
  const stars = Array.from({ length: 34 }, () => `<circle cx="${(r() * 300).toFixed(1)}" cy="${(r() * 260).toFixed(1)}" r="${(r() * 1.6 + .4).toFixed(2)}" fill="${light}" opacity="${(r() * .6 + .3).toFixed(2)}"/>`).join('');
  const hills = (y, color, amp, op = 1) => { let d = `M0 450 L0 ${y}`; for (let x = 0; x <= 300; x += 50) d += ` Q${x + 25} ${y - amp * (r() + .3)} ${x + 50} ${y + (r() - .5) * amp}`; return `<path d="${d} L300 450 Z" fill="${color}" opacity="${op}"/>`; };
  const scenes = {
    dawn: `<circle cx="150" cy="215" r="82" fill="${light}" opacity=".9"/>${stars}${hills(300, b, 60, .85)}${hills(345, c, 50)}`,
    sea: `<circle cx="210" cy="140" r="46" fill="${light}"/>${[250, 285, 320, 355].map((y, i) => `<path d="M0 ${y} q37 -18 75 0 t75 0 t75 0 t75 0 V450 H0Z" fill="${i % 2 ? c : b}" opacity="${.55 + i * .12}"/>`).join('')}`,
    night: `${stars}<circle cx="215" cy="105" r="38" fill="${light}"/><circle cx="230" cy="96" r="34" fill="${a}"/><path d="M60 330 l14 -150 h18 l14 150Z" fill="${light}" opacity=".85"/><path d="M83 182 L300 110 L300 175Z" fill="${light}" opacity=".18"/>${hills(330, c, 30)}`,
    planet: `${stars}<circle cx="150" cy="190" r="70" fill="${a}"/><ellipse cx="150" cy="190" rx="122" ry="24" fill="none" stroke="${light}" stroke-width="5" opacity=".75" transform="rotate(-18 150 190)"/>${hills(360, c, 30)}`,
    field: `<circle cx="80" cy="120" r="54" fill="${light}" opacity=".9"/>${Array.from({ length: 22 }, () => `<circle cx="${(r() * 300).toFixed(0)}" cy="${(r() * 260 + 40).toFixed(0)}" r="${(r() * 4 + 2).toFixed(1)}" fill="${light}" opacity=".8"/>`).join('')}${hills(310, b, 40)}${hills(355, c, 34)}`,
    rain: `${Array.from({ length: 60 }, () => { const x = r() * 330, y = r() * 450; return `<line x1="${x.toFixed(0)}" y1="${y.toFixed(0)}" x2="${(x - 10).toFixed(0)}" y2="${(y + 34).toFixed(0)}" stroke="${light}" stroke-width="1.4" opacity=".35"/>`; }).join('')}${[30, 85, 140, 205, 250].map((x, i) => `<rect x="${x}" y="${250 - i * 17 % 60}" width="${44 + i * 3}" height="220" fill="${c}" opacity=".9"/>`).join('')}`,
    clouds: `<circle cx="225" cy="110" r="40" fill="${light}"/>${[[70, 200, 80], [210, 240, 95], [120, 300, 110], [250, 330, 80]].map(([x, y, w]) => `<ellipse cx="${x}" cy="${y}" rx="${w}" ry="${w * .32}" fill="${light}" opacity=".7"/>`).join('')}<path d="M-10 285 C80 250 200 330 310 270" stroke="${c}" stroke-width="6" fill="none"/>`,
    aurora: `${stars}${[0, 1, 2].map(i => `<path d="M-20 ${150 + i * 30} C80 ${80 + i * 40} 180 ${210 + i * 20} 320 ${120 + i * 35}" stroke="${i % 2 ? a : light}" stroke-width="${26 - i * 6}" fill="none" opacity="${.35 - i * .07}"/>`).join('')}${hills(330, b, 70, .8)}${hills(360, c, 40)}`,
    petals: `<circle cx="160" cy="170" r="88" fill="${light}" opacity=".55"/>${Array.from({ length: 26 }, () => `<ellipse cx="${(r() * 300).toFixed(0)}" cy="${(r() * 380).toFixed(0)}" rx="7" ry="4" fill="${a}" transform="rotate(${(r() * 180).toFixed(0)})" opacity=".9"/>`).join('')}<path d="M300 60 C220 90 180 140 110 150" stroke="${c}" stroke-width="6" fill="none"/>`,
    rings: `${[110, 85, 60, 35].map((rr, i) => `<circle cx="150" cy="200" r="${rr}" fill="none" stroke="${i % 2 ? light : a}" stroke-width="${8 - i}" opacity="${.5 + i * .12}"/>`).join('')}${hills(350, c, 24)}`,
  };
  const size = title.length > 6 ? 26 : 32;
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 450"><defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${a}"/><stop offset=".55" stop-color="${b}"/><stop offset="1" stop-color="${c}"/></linearGradient><linearGradient id="s" x1="0" y1="0" x2="0" y2="1"><stop offset=".55" stop-color="#000" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".55"/></linearGradient></defs><rect width="300" height="450" fill="url(#g)"/>${scenes[motif]}<rect width="300" height="450" fill="url(#s)"/><text x="22" y="322" font-family="Microsoft YaHei, PingFang SC, sans-serif" font-size="11" letter-spacing="3" fill="${light}" opacity=".85">TV ANIMATION · 2026</text><text x="20" y="${322 + size + 8}" font-family="Microsoft YaHei, PingFang SC, sans-serif" font-weight="700" font-size="${size}" fill="#fff">${title}</text></svg>`;
  return 'data:image/svg+xml;base64,' + Buffer.from(svg).toString('base64');
}

const motifs = Object.keys({ dawn: 0, sea: 0, night: 0, planet: 0, field: 0, rain: 0, clouds: 0, aurora: 0, petals: 0, rings: 0 });
const lineup = {
  星期一: ['晨星旅团', '海风邮差', '灯塔下的猫', '银河便利店', '钢铁与蒲公英', '第七书库的魔女'],
  星期二: ['雨停之后', '云端列车', '龙与面包店', '盛夏汽水与流星', '齿轮之心', '樱花信号'],
  星期三: ['极光观测部', '白夜剑舞', '像素勇者重启中', '月面农场日记'],
  星期四: ['深海来信', '狐狸神社的打工生活', '星图绘制师'],
  星期五: ['最后的夏日祭', '机械鸟与少年', '雾都侦探社'],
  星期六: ['放学后的天文台', '异世界邮政局', '纸飞机航线', '雪原上的歌'],
  星期日: ['回声电台', '琥珀色的约定', '向日葵公寓'],
};
let serial = 0;
const cards = Object.entries(lineup).flatMap(([day, names]) => names.map(name => {
  const i = serial++, id = String(3501 + i);
  return { id, name, url: 'https://mikanani.me/Home/Bangumi/' + id, image: poster(i, name, motifs[i % motifs.length]), day, hasReleases: !(day === '星期日' && i % 3 === 0) };
}));
const badges = id => { const i = Number(id) - 3501; return { hot: i % 4 === 0 ? 'pink' : i % 5 === 1 ? 'purple' : null, groupCount: 3 + i % 6, updatedGroups: i % 3 === 0 ? 1 + i % 4 : 0, latestEpisode: 5 + (i * 7) % 4 }; };

const groups = ['星尘字幕组', '喵萌奶茶屋', 'LoliHouse', '桜都字幕组', '北宇治字幕组'];
const feed = (id, group) => `https://mikanani.me/RSS/Bangumi?bangumiId=${id}&subgroupid=${380 + groups.indexOf(group)}`;
const review = {
  score: 8, model: 'claude-sonnet-5-5', version: 2, createdAt: ago(60 * 26),
  recommendation: '喜欢治愈系公路片和细腻群像的观众值得追，节奏偏慢但后劲足。',
  review: '本季少见的原创公路题材。前六集以“一站一故事”的单元结构推进，每个城镇都有独立的风土与人物，主线伏笔埋得克制而清晰。美术延续了制作组擅长的暖色光影，晨昏场景尤其出色；配乐以民谣乐器为主，与旅途氛围相得益彰。',
  overview: '讲述少女莉娜加入流浪商队“晨星旅团”，在横跨大陆的旅途中寻找失踪父亲的故事。',
  originalWork: '原创动画企划，无漫画或小说原作。',
  adaptation: '全 12 集，每周一更新，已确认第二季制作。',
  caveats: '第二季播出时间尚未公布。',
  queries: ['晨星旅团 动画 制作 公司', '晨星旅团 第二季'], warnings: [],
  references: [{ id: 1, title: '官方网站', url: 'https://example.com/morning-star' }],
};
const detail = id => {
  const card = cards.find(c => c.id === id) || cards[0];
  return {
    id, name: card.name,
    summary: '少女莉娜在十六岁生日那天收到一封没有署名的信，信中只写着一句话：“跟着晨星走。”为了寻找多年前失踪的父亲，她加入了横跨大陆的流浪商队“晨星旅团”。沙海、雪国、漂浮在湖上的城市——每一站都有新的相遇与告别。',
    tmdb: { score: 8.6, voteCount: 412, popularity: 87.3, firstAirDate: '2026-10-05', name: card.name },
    review: id === '3501' ? review : null,
    groups: groups.map(name => ({ name, url: feed(id, name) })),
  };
};

const sub = (id, name, url, extra) => ({ id, name, url, nodeId: 'nas', directory: '/downloads/Anime', namePath: '', season: 0, filter: '', excludeFilter: '', isFilterRegex: false, isExcludeFilterRegex: false, autoDir: true, enabled: true, skipExisting: false, lastError: null, ...extra });
const history = (title, n, step = 7 * 24 * 60) => Array.from({ length: n }, (_, i) => ({ title: `${title} - ${String(i + 1).padStart(2, '0')} [1080p]`, time: ago((n - i) * step), skipped: false }));
const subscriptions = [
  sub('s1', '晨星旅团 · 星尘字幕组', feed('3501', '星尘字幕组'), { filter: '1080p', excludeFilter: '繁體|BIG5', isExcludeFilterRegex: true, history: history('晨星旅团', 7), lastChecked: ago(4) }),
  sub('s2', '海风邮差 · 喵萌奶茶屋', feed('3502', '喵萌奶茶屋'), { filter: '简日双语', history: history('海风邮差', 6), lastChecked: ago(4) }),
  sub('s3', '灯塔下的猫 · LoliHouse', feed('3503', 'LoliHouse'), { filter: 'HEVC', history: history('灯塔下的猫', 5), lastChecked: ago(4) }),
  sub('s4', '云端列车 · 桜都字幕组', feed('3508', '桜都字幕组'), { filter: '1080', excludeFilter: '合集', history: history('云端列车', 6), lastChecked: ago(4) }),
  sub('s5', 'Arch Linux 月度镜像', 'https://archlinux.org/feeds/releases/', { nodeId: 'local', directory: '/downloads/ISO', autoDir: false, history: history('archlinux-2026.11.01-x86_64.iso', 3, 30 * 24 * 60), lastChecked: ago(4) }),
  sub('s6', '钢铁与蒲公英 · 北宇治字幕组', feed('3505', '北宇治字幕组'), { enabled: false, history: history('钢铁与蒲公英', 12), lastChecked: ago(60 * 24 * 3) }),
];

let gidSeed = rng(42);
const gid = () => Array.from({ length: 16 }, () => '0123456789abcdef'[Math.floor(gidSeed() * 16)]).join('');
const task = (status, name, total, ratio, down = 0, up = 0, bt = false) => ({
  gid: gid(), status, totalLength: String(Math.round(total)), completedLength: String(Math.round(total * ratio)), downloadSpeed: String(Math.round(down)), uploadSpeed: String(Math.round(up)),
  connections: '16', dir: '/downloads', files: [{ index: '1', path: '/downloads/' + name, length: String(Math.round(total)), completedLength: String(Math.round(total * ratio)), selected: 'true', uris: [] }],
  ...(bt ? { bittorrent: { info: { name } }, infoHash: gid() + gid() } : {}),
});
const tasks = {
  nodeId: 'nas',
  stats: { downloadSpeed: String(Math.round(34.8 * MB)), uploadSpeed: String(Math.round(1.7 * MB)), numActive: '3', numWaiting: '1', numStopped: '2' },
  active: [
    task('active', '[星尘字幕组] 晨星旅团 - 07 [WebRip 1080p HEVC][简繁内封]', 1.42 * GB, .724, 18.6 * MB, 1.2 * MB, true),
    task('active', 'ubuntu-26.04-desktop-amd64.iso', 6.1 * GB, .417, 9.8 * MB),
    task('active', '[喵萌奶茶屋] 海风邮差 - 06 [1080p][简日双语]', 812 * MB, .235, 6.4 * MB, .5 * MB, true),
  ],
  waiting: [task('waiting', 'debian-13.2.0-amd64-DVD-1.iso', 3.9 * GB, 0)],
  stopped: [
    task('complete', '[LoliHouse] 灯塔下的猫 - 05 [WebRip 1080p HEVC-10bit AAC]', 684 * MB, 1, 0, 0, true),
    task('complete', 'LibreOffice_26.2_Win_x86-64.msi', 348 * MB, 1),
  ],
};
tasks.waiting.push(task('paused', 'Blender-5.0-windows-x64.zip', 412 * MB, .58));

async function mock(page) {
  await page.route(url => url.pathname.startsWith('/api/'), async route => {
    const request = route.request(), p = new URL(request.url()).pathname.slice(4);
    if (request.method() !== 'GET') return route.continue();
    let m;
    if (p === '/state') {
      const response = await route.fetch(), json = await response.json();
      json.nodes = [{ ...json.nodes.find(n => n.id === 'local'), name: '本地 Aria2' }, { id: 'nas', name: '家庭 NAS', url: 'http://192.168.1.20:6800/jsonrpc', secret: '', downloadDirectory: '/downloads' }];
      json.selectedNodeId = 'nas';
      return route.fulfill({ response, json });
    }
    if (p === '/tasks') return route.fulfill({ json: tasks });
    if (p === '/subscriptions') return route.fulfill({ json: subscriptions });
    if (p === '/anime') return route.fulfill({ json: cards });
    if (p === '/anime/review-tasks') return route.fulfill({ json: [] });
    if ((m = p.match(/^\/anime\/(\d+)\/badges$/))) return route.fulfill({ json: badges(m[1]) });
    if ((m = p.match(/^\/anime\/(\d+)$/))) return route.fulfill({ json: detail(m[1]) });
    return route.continue();
  });
}

async function open(browser, info, options, scheme) {
  const context = await browser.newContext({ ...options, colorScheme: scheme });
  const page = await context.newPage();
  await mock(page);
  await page.goto(info.base);
  await page.locator('#login-form input').fill(info.password);
  await page.locator('#login-form button').click();
  await page.locator('.task-name').first().waitFor();
  await page.addStyleTag({ content: 'html{scrollbar-width:none}::-webkit-scrollbar{display:none}*{caret-color:transparent}' });
  return page;
}

async function go(page, hash, ready) {
  await page.evaluate(h => { location.hash = h; }, hash);
  await page.locator(ready).first().waitFor();
  await page.mouse.move(0, 0);
  await page.waitForTimeout(400);
}

async function badgesReady(page) {
  await page.waitForFunction(() => [...document.querySelectorAll('.anime-card:not(.unpublished)')].filter(c => c.getBoundingClientRect().top < innerHeight).every(c => c.querySelector('.anime-episode').textContent));
  // Posters are lazy-loaded, so only wait for the ones on screen.
  await page.waitForFunction(() => [...document.querySelectorAll('.poster img')].filter(i => i.getBoundingClientRect().top < innerHeight).every(i => i.complete && i.naturalWidth));
}

async function phoneComposite(browser, shots, file) {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
  const frames = shots.map(s => `<figure><img src="data:image/png;base64,${fs.readFileSync(s).toString('base64')}"></figure>`).join('');
  await page.setContent(`<style>body{margin:0;width:1440px;height:900px;display:flex;align-items:center;justify-content:center;gap:56px;background:radial-gradient(circle at 20% 15%,#2b6a5a 0,transparent 45%),radial-gradient(circle at 85% 90%,#1d4f63 0,transparent 45%),#0d151b}figure{margin:0;width:340px;height:736px;border-radius:46px;padding:10px;background:#05090c;box-shadow:0 30px 60px #0008,inset 0 0 0 1.5px #2c3a40}img{display:block;width:100%;height:100%;object-fit:cover;object-position:top;border-radius:36px}</style>${frames}`);
  await page.evaluate(() => Promise.all([...document.images].map(i => i.decode())));
  await page.screenshot({ path: file });
  await page.close();
}

(async () => {
  fs.mkdirSync(out, { recursive: true });
  const server = spawn(process.env.PYTHON || 'python', ['tests/integration.py', '--serve'], { cwd: root, stdio: ['pipe', 'pipe', 'inherit'] });
  let browser;
  try {
    const info = await new Promise((resolve, reject) => {
      readline.createInterface({ input: server.stdout }).on('line', text => { if (text.startsWith('{')) resolve(JSON.parse(text)); });
      server.on('exit', code => reject(new Error('Fixture exited ' + code)));
    });
    browser = await chromium.launch({ headless: true, ...(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {}) });
    const desktop = { viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 };
    const mobile = { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true };

    const dark = await open(browser, info, desktop, 'dark');
    await dark.screenshot({ path: path.join(out, 'downloads.png') });
    await go(dark, 'anime', '.anime-card');
    await badgesReady(dark);
    await dark.locator('.anime-card[data-anime="3501"]').click();
    await dark.locator('#ai-review').filter({ hasText: '重新调查' }).waitFor();
    await dark.waitForTimeout(300);
    await dark.screenshot({ path: path.join(out, 'anime-detail.png') });

    const light = await open(browser, info, desktop, 'light');
    await go(light, 'anime', '.anime-card');
    await badgesReady(light);
    await light.screenshot({ path: path.join(out, 'anime.png') });
    await go(light, 'subscriptions', '.sub-card');
    await light.screenshot({ path: path.join(out, 'subscriptions.png') });

    const tmp = fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'aria2fast-shots-'));
    const phoneLight = await open(browser, info, mobile, 'light');
    await phoneLight.screenshot({ path: path.join(tmp, 'm1.png') });
    await go(phoneLight, 'subscriptions', '.sub-card');
    await phoneLight.screenshot({ path: path.join(tmp, 'm3.png') });
    const phoneDark = await open(browser, info, mobile, 'dark');
    await go(phoneDark, 'anime', '.anime-card');
    await phoneDark.evaluate(() => { document.querySelector('.anime-day').scrollIntoView(); scrollBy(0, -90); });
    await badgesReady(phoneDark);
    await phoneDark.waitForTimeout(300);
    await phoneDark.screenshot({ path: path.join(tmp, 'm2.png') });
    await phoneComposite(browser, ['m1', 'm2', 'm3'].map(n => path.join(tmp, n + '.png')), path.join(out, 'mobile.png'));
    fs.rmSync(tmp, { recursive: true, force: true });
    console.log('Screenshots written to ' + path.relative(root, out));
  } finally {
    await browser?.close();
    // Closing stdin lets the fixture stop the app it launched; kill only as a fallback.
    server.stdin.end();
    if (server.exitCode === null) await Promise.race([new Promise(resolve => server.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 15000))]);
    if (server.exitCode === null) server.kill();
  }
})().then(() => process.exit(0), error => { console.error(error); process.exit(1); });
