'use strict';

const $ = s => document.querySelector(s);

// h builds DOM nodes; text always goes through textContent, so domain names
// from the query log can never inject markup.
function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else if (v === true) el.setAttribute(k, '');
    else if (v !== false && v != null) el.setAttribute(k, v);
  }
  for (const kid of kids.flat()) {
    if (kid != null && kid !== false) el.append(kid.nodeType ? kid : String(kid));
  }
  return el;
}

const PROTO_LABEL = { https: 'DoH', h3: 'DoH3', tls: 'DoT', quic: 'DoQ', udp: 'Plain' };
const protoOf = url => PROTO_LABEL[(url.match(/^(\w+):/) || [])[1]] || '?';

// Provider templates. id = profile/resolver ID, dev = optional device name.
const slug = s => s.trim().replace(/\s+/g, '-');
const PROVIDERS = {
  custom: { label: 'Custom server' },
  nextdns: {
    label: 'NextDNS', id: 'Profile ID (from my.nextdns.io → Setup)', device: true,
    doh3: (id, dev) => `h3://dns.nextdns.io/${id}${dev ? '/' + encodeURIComponent(dev) : ''}`,
    doh: (id, dev) => `https://dns.nextdns.io/${id}${dev ? '/' + encodeURIComponent(dev) : ''}`,
    dot: (id, dev) => `tls://${dev ? slug(dev).replace(/-/g, '--') + '-' : ''}${id}.dns.nextdns.io`,
    doq: (id, dev) => `quic://${dev ? slug(dev).replace(/-/g, '--') + '-' : ''}${id}.dns.nextdns.io`,
  },
  controld: {
    label: 'Control D', id: 'Resolver ID (from controld.com → Endpoints)', device: true,
    doh3: (id, dev) => `h3://dns.controld.com/${id}${dev ? '/' + slug(dev) : ''}`,
    doh: (id, dev) => `https://dns.controld.com/${id}${dev ? '/' + slug(dev) : ''}`,
    dot: (id, dev) => `tls://${dev ? slug(dev) + '-' : ''}${id}.dns.controld.com`,
    doq: (id, dev) => `quic://${dev ? slug(dev) + '-' : ''}${id}.dns.controld.com`,
  },
  quad9: {
    label: 'Quad9 (malware blocking, Swiss non-profit)',
    doh: () => 'https://dns.quad9.net/dns-query', doh3: () => 'h3://dns.quad9.net/dns-query',
    dot: () => 'tls://dns.quad9.net', doq: () => 'quic://dns.quad9.net',
  },
  cloudflare: {
    label: 'Cloudflare 1.1.1.1 (unfiltered)',
    doh: () => 'https://cloudflare-dns.com/dns-query', doh3: () => 'h3://cloudflare-dns.com/dns-query',
    dot: () => 'tls://one.one.one.one',
  },
  adguard: {
    label: 'AdGuard DNS (ad blocking)',
    doh: () => 'https://dns.adguard-dns.com/dns-query', doh3: () => 'h3://dns.adguard-dns.com/dns-query',
    dot: () => 'tls://dns.adguard-dns.com', doq: () => 'quic://dns.adguard-dns.com',
  },
  mullvad: {
    label: 'Mullvad DNS (unfiltered)',
    doh: () => 'https://dns.mullvad.net/dns-query', dot: () => 'tls://dns.mullvad.net',
  },
};

let cfg = null;        // configuration as last confirmed by the service
let state = null;      // live status
let tests = {};        // server id → last manual test result
let entries = [];      // query log, oldest first
let logSeq = 0;
let logPaused = false;
let logFilter = '';
let tab = 'overview';
let serviceDown = false;

function toast(msg, isErr) {
  const t = $('#toast');
  t.textContent = msg;
  t.className = isErr ? 'err' : '';
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { t.hidden = true; }, isErr ? 6000 : 2500);
}

async function api(method, path, body) {
  const res = await fetch(path, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    const err = new Error(data.error || res.statusText);
    err.serviceDown = !!data.serviceDown;
    throw err;
  }
  return data;
}

// save applies a change to a copy of the configuration and sends it. Every
// edit takes effect immediately; there is no separate "apply" step.
async function save(change, okMsg) {
  const next = structuredClone(cfg);
  change(next);
  try {
    cfg = await api('PUT', '/api/config', next);
    if (okMsg) toast(okMsg);
    await refreshState();
    return true;
  } catch (e) {
    toast(e.message, true);
    return false;
  } finally {
    render();
  }
}

const newId = () => Math.random().toString(36).slice(2, 10);
const fmtMs = v => (v >= 100 ? v.toFixed(0) : v.toFixed(1)) + ' ms';
const serverState = id => (state?.servers || []).find(s => s.id === id);

// ---------- Overview ----------

function renderOverview() {
  const st = state || { stats: {}, servers: [] };
  const enabled = cfg.upstreams.filter(u => u.enabled);
  const healthy = enabled.filter(u => serverState(u.id)?.healthy);
  const current = healthy[0];
  let cls = '', title = 'Protection is off', sub = 'DNS queries go to whatever server Windows or your VPN has configured.';
  if (st.intercepting && current) {
    cls = 'on';
    title = `Protected — using ${current.name}`;
    sub = `${st.detail}. Every DNS query on this PC is answered over ${protoOf(current.url)}` +
      (current !== enabled[0] ? ` (backup; ${enabled[0].name} is unreachable).` : '.');
  } else if (st.intercepting) {
    cls = 'warn';
    title = 'No encrypted DNS server reachable';
    sub = cfg.settings.onFailure === 'passthrough'
      ? 'Queries are passed to the original DNS server until a server recovers.'
      : 'Queries fail until a server recovers (strict mode).';
  } else if (cfg.active) {
    cls = 'warn';
    title = 'Protection could not start';
    sub = st.detail || '';
  }
  const s = st.stats || {};
  const pct = (n) => s.total ? ` (${Math.round(100 * n / s.total)}%)` : '';
  const tile = (n, l) => h('div', { class: 'tile' }, h('div', { class: 'n' }, n ?? 0), h('div', { class: 'l' }, l));

  $('#overview').replaceChildren(
    h('div', { class: 'card hero ' + cls },
      h('span', { class: 'dot' }),
      h('div', { class: 'grow' }, h('div', { class: 'title' }, title), h('p', { class: 'hint' }, sub)),
      h('button', { class: 'btn' + (cfg.active ? '' : ' primary'), onclick: () => setActive(!cfg.active) },
        cfg.active ? 'Turn off' : 'Turn on')),
    h('div', { class: 'tiles' },
      tile(s.total, 'Queries'),
      tile((s.resolved ?? 0), 'Resolved encrypted' + pct(s.resolved)),
      tile((s.cached ?? 0), 'From cache' + pct(s.cached)),
      tile(s.blocked, 'Blocked'),
      tile(s.passthrough, 'Passed to original DNS'),
      tile(s.failed, 'Failed')),
    h('div', { class: 'card' },
      h('h2', {}, 'Servers, in failover order'),
      enabled.length ? enabled.map((u, i) => serverRow(u, i, false)) : h('div', { class: 'empty' }, 'No server enabled. Add one under Servers.')),
  );
}

function serverRow(u, i, editable) {
  const ss = serverState(u.id);
  const t = tests[u.id];
  let num = '';
  if (t) num = t.ok ? `test: ${fmtMs(t.queryMs)}` : 'test failed';
  else if (ss?.queries) num = `${fmtMs(ss.avgMs)} avg · ${ss.queries} queries`;
  let sub = addressOf(u.url);
  const proto = splitUrl(u.url).proto;
  if (ss && !ss.healthy) sub = 'Unreachable. ' + explainError(proto, ss.lastError || 'timeout');
  if (t && !t.ok) sub = 'Test failed. ' + explainError(proto, t.error);
  const n = cfg.upstreams.length;
  const idx = cfg.upstreams.indexOf(u);
  return h('div', { class: 'row' + (u.enabled ? '' : ' off') },
    h('span', { class: 'prio' }, u.enabled ? i + 1 : ''),
    h('span', { class: 'status ' + (!u.enabled || !ss ? '' : ss.healthy ? 'ok' : 'bad') }),
    h('div', { class: 'grow' },
      h('div', { class: 'name' }, u.name, ' ', h('span', { class: 'badge' }, protoOf(u.url)),
        i === 0 && u.enabled ? h('span', { class: 'badge' }, 'Primary') : null),
      h('div', { class: 'sub', title: sub }, sub)),
    h('span', { class: 'num' }, num),
    editable ? [
      h('button', { class: 'btn icon', title: 'Higher priority', disabled: idx === 0, onclick: () => moveServer(idx, -1) }, '↑'),
      h('button', { class: 'btn icon', title: 'Lower priority', disabled: idx === n - 1, onclick: () => moveServer(idx, 1) }, '↓'),
      h('button', { class: 'btn', onclick: () => serverDialog(u) }, 'Edit'),
      h('label', { class: 'switch small', title: 'Enable or disable this server' },
        h('input', { type: 'checkbox', checked: u.enabled, onchange: e => save(c => { c.upstreams[idx].enabled = e.target.checked; }) }),
        h('span', {})),
    ] : null);
}

async function setActive(active) {
  try {
    await api('POST', '/api/active', { active });
    cfg.active = active;
  } catch (e) {
    toast(e.message, true);
  }
  await refreshState();
  render();
}

// ---------- Servers ----------

function renderServers() {
  let i = 0;
  $('#servers').replaceChildren(
    h('div', { class: 'bar' },
      h('div', { class: 'grow' },
        h('h2', {}, 'DNS servers'),
        h('p', { class: 'hint' }, 'The first enabled server answers everything. If it stops responding, the next one takes over within seconds and hands back as soon as it recovers.')),
      h('button', { class: 'btn', onclick: testAll }, 'Test all'),
      h('button', { class: 'btn primary', onclick: () => serverDialog(null) }, 'Add server')),
    h('div', { class: 'card' },
      cfg.upstreams.length
        ? cfg.upstreams.map(u => serverRow(u, u.enabled ? i++ : 0, true))
        : h('div', { class: 'empty' }, 'No servers yet.')),
  );
}

function moveServer(idx, dir) {
  save(c => {
    const [u] = c.upstreams.splice(idx, 1);
    c.upstreams.splice(idx + dir, 0, u);
  });
}

async function testAll() {
  toast('Testing servers…');
  try {
    const results = await api('POST', '/api/test', { upstreams: cfg.upstreams });
    tests = Object.fromEntries(results.map(r => [r.id, r]));
    $('#toast').hidden = true;
  } catch (e) {
    toast(e.message, true);
  }
  render();
}

const SCHEME = { doh: 'https', doh3: 'h3', dot: 'tls', doq: 'quic', plain: 'udp' };
const PROTO_SHORT = { doh: 'DoH', doh3: 'DoH3', dot: 'DoT', doq: 'DoQ', plain: 'Plain DNS' };
const PROTO_HINT = {
  doh: 'DNS over HTTPS (TCP 443). Works almost everywhere, including through VPNs.',
  doh3: 'DNS over HTTP/3 (UDP 443). Fast, but some VPNs and networks block it.',
  dot: 'DNS over TLS (TCP 853).',
  doq: 'DNS over QUIC (UDP 853). Fast, but some VPNs and networks block it.',
  plain: 'Unencrypted DNS (UDP 53). Only for a resolver on your own network.',
};
const ADDRESS_HINT = {
  doh: 'e.g. dns.example.com/dns-query', doh3: 'e.g. dns.example.com/dns-query',
  dot: 'e.g. dns.example.com', doq: 'e.g. dns.example.com', plain: 'e.g. 192.168.1.1',
};

// A server is stored as one URL; the UI shows it as protocol + address.
const addressOf = url => url.replace(/^\w+:\/\//, '');
function splitUrl(url) {
  const scheme = (url.match(/^(\w+):/) || [])[1];
  const proto = Object.keys(SCHEME).find(k => SCHEME[k] === scheme) || 'doh';
  return { proto, address: addressOf(url) };
}
function joinUrl(proto, address) {
  address = addressOf(address.trim()).replace(/^\/+/, '');
  if (!address) return '';
  if ((proto === 'doh' || proto === 'doh3') && !address.includes('/')) address += '/dns-query';
  if (proto !== 'doh' && proto !== 'doh3') address = address.replace(/\/.*$/, '');
  return `${SCHEME[proto]}://${address}`;
}

// explainError turns a transport error into something actionable; the raw
// text stays available as a tooltip.
const PROTO_PORT = { doh: 'TCP port 443', doh3: 'UDP port 443', dot: 'TCP port 853', doq: 'UDP port 853', plain: 'UDP port 53' };
function explainError(proto, error) {
  if (/access permissions|unzulässig|forbidden by its access/i.test(error)) {
    return `A firewall on this PC blocked the connection (${PROTO_PORT[proto]}) for Nax-DNSManager.`;
  }
  if (/deadline exceeded|timeout|timed out/i.test(error)) {
    return `No answer on ${PROTO_PORT[proto]}. The server, a VPN or a firewall is not letting this protocol through; try another protocol.`;
  }
  if (/certificate|x509|tls:/i.test(error)) return 'The server\'s certificate was not accepted. Check the address.';
  if (/no such host|no addresses|bootstrap/i.test(error)) return 'The server name could not be resolved. Check the address.';
  return error.split('\n')[0].slice(0, 200);
}

function serverDialog(existing) {
  const d = $('#dialog');
  const provider = h('select', {}, Object.entries(PROVIDERS).map(([k, p]) => h('option', { value: k }, p.label)));
  const id = h('input', { type: 'text', spellcheck: 'false' });
  const dev = h('input', { type: 'text', placeholder: 'Optional, shown in your provider\'s logs' });
  const name = h('input', { type: 'text' });
  const address = h('input', { type: 'text', spellcheck: 'false' });
  const boot = h('input', { type: 'text', spellcheck: 'false', placeholder: 'Optional. Leave empty to resolve automatically.' });
  const idField = h('label', { class: 'field' }, h('span', {}, ''), id);
  const devField = h('label', { class: 'field' }, h('span', {}, 'Device name'), dev);
  const addressField = h('label', { class: 'field' }, h('span', {}, 'Server address'), address);
  const protoHint = h('p', { class: 'hint' });
  const result = h('div', { class: 'result' });
  let proto = 'doh';
  let nameTouched = !!existing;

  const protoButtons = Object.keys(PROTO_SHORT).map(k =>
    h('button', { type: 'button', 'data-proto': k, onclick: () => { proto = k; sync(); } }, PROTO_SHORT[k]));
  const protoField = h('div', { class: 'field' },
    h('span', {}, 'Protocol'), h('div', { class: 'segmented' }, protoButtons), protoHint);

  const isCustom = () => !!existing || provider.value === 'custom';
  const sync = () => {
    const p = PROVIDERS[provider.value];
    const custom = isCustom();
    for (const b of protoButtons) {
      const k = b.dataset.proto;
      b.disabled = custom ? false : !p[k];
      b.hidden = k === 'plain' && !custom;
    }
    if (!custom && !p[proto]) proto = Object.keys(PROTO_SHORT).find(k => p[k]);
    for (const b of protoButtons) b.classList.toggle('on', b.dataset.proto === proto);
    protoHint.textContent = PROTO_HINT[proto];
    idField.hidden = custom || !p.id;
    devField.hidden = custom || !p.device;
    addressField.hidden = !custom;
    address.placeholder = ADDRESS_HINT[proto];
    if (!custom && p.id) idField.firstChild.textContent = p.id;
    if (!nameTouched && !custom) name.value = `${p.label.split(' (')[0]} (${PROTO_SHORT[proto]})`;
  };
  provider.onchange = sync;
  name.oninput = () => { nameTouched = true; };

  if (existing) {
    const parts = splitUrl(existing.url);
    proto = parts.proto;
    address.value = parts.address;
    name.value = existing.name;
    boot.value = (existing.bootstrap || []).join(', ');
  }
  sync();

  const collect = () => {
    const p = PROVIDERS[provider.value];
    let url;
    if (isCustom()) url = joinUrl(proto, address.value);
    else url = p.id && !id.value.trim() ? '' : p[proto](id.value.trim(), dev.value.trim());
    return {
      id: existing?.id || newId(),
      name: name.value.trim() || `${addressOf(url)} (${PROTO_SHORT[proto]})`,
      url,
      bootstrap: boot.value.split(/[\s,]+/).filter(Boolean),
      enabled: existing ? existing.enabled : true,
    };
  };
  const missing = () => isCustom() ? 'Enter the server address first.' : 'Enter your ID first.';

  const showResult = (text, detail) => { result.textContent = text; result.title = detail || ''; };

  d.replaceChildren(
    h('h2', {}, existing ? 'Edit server' : 'Add server'),
    existing ? '' : h('label', { class: 'field' }, h('span', {}, 'Provider'), provider),
    idField,
    protoField,
    addressField,
    devField,
    h('label', { class: 'field' }, h('span', {}, 'Name'), name),
    h('details', {}, h('summary', {}, 'Advanced'),
      h('label', { class: 'field' }, h('span', {}, 'Bootstrap IP addresses'), boot)),
    result,
    h('div', { class: 'actions' },
      existing ? h('button', { class: 'btn danger', onclick: async () => {
        const used = cfg.rules.some(r => r.action === 'upstream' && r.upstream === existing.id);
        if (used) { result.textContent = 'A rule still uses this server. Change or delete that rule first.'; return; }
        if (await save(c => { c.upstreams = c.upstreams.filter(u => u.id !== existing.id); })) d.close();
      } }, 'Delete') : null,
      h('span', { style: 'flex:1' }),
      h('button', { class: 'btn', onclick: async () => {
        const u = collect();
        if (!u.url) { result.textContent = missing(); return; }
        showResult(`Testing ${PROTO_SHORT[proto]}…`);
        try {
          const [r] = await api('POST', '/api/test', { upstreams: [u] });
          if (r.ok) showResult(`${PROTO_SHORT[proto]} works. ${fmtMs(r.queryMs)} per query (first connection ${fmtMs(r.connectMs)}).`);
          else showResult(`${PROTO_SHORT[proto]} failed. ${explainError(proto, r.error)}`, r.error);
        } catch (e) { showResult(e.message); }
      } }, 'Test'),
      h('button', { class: 'btn', onclick: () => d.close() }, 'Cancel'),
      h('button', { class: 'btn primary', onclick: async () => {
        const u = collect();
        if (!u.url) { result.textContent = missing(); return; }
        const ok = await save(c => {
          const i = c.upstreams.findIndex(x => x.id === u.id);
          if (i >= 0) c.upstreams[i] = u; else c.upstreams.unshift(u);
        }, existing ? 'Server saved' : 'Added as primary server. Reorder with the arrows.');
        if (ok) d.close();
      } }, existing ? 'Save' : 'Add')),
  );
  d.showModal();
}

// ---------- Rules ----------

const ACTIONS = { block: 'Block', passthrough: 'Use the original DNS server', upstream: 'Use a specific server' };

function renderRules() {
  $('#rules').replaceChildren(
    h('div', { class: 'bar' },
      h('div', { class: 'grow' },
        h('h2', {}, 'Rules'),
        h('p', { class: 'hint' }, 'Rules decide what happens to matching domains before the server chain is used. The first matching rule wins.')),
      h('button', { class: 'btn primary', onclick: () => ruleDialog(null) }, 'Add rule')),
    h('div', { class: 'card' },
      cfg.rules.length ? cfg.rules.map((r, idx) => {
        let what = ACTIONS[r.action];
        if (r.action === 'upstream') what = 'Use ' + (cfg.upstreams.find(u => u.id === r.upstream)?.name || '?');
        return h('div', { class: 'row' + (r.enabled ? '' : ' off') },
          h('div', { class: 'grow' },
            h('div', { class: 'name' }, r.name, ' ', h('span', { class: 'badge ' + (r.action === 'block' ? 'blocked' : r.action === 'passthrough' ? 'passthrough' : '') }, what)),
            h('div', { class: 'sub' }, r.domains.join(', '))),
          h('button', { class: 'btn', onclick: () => ruleDialog(r) }, 'Edit'),
          h('label', { class: 'switch small' },
            h('input', { type: 'checkbox', checked: r.enabled, onchange: e => save(c => { c.rules[idx].enabled = e.target.checked; }) }),
            h('span', {})));
      }) : h('div', { class: 'empty' }, 'No rules. Everything goes to your servers.')),
  );
}

function ruleDialog(existing, presetDomain) {
  const d = $('#dialog');
  const name = h('input', { type: 'text', value: existing?.name || '' });
  const domains = h('textarea', { spellcheck: 'false' });
  domains.value = existing ? existing.domains.join('\n') : (presetDomain || '');
  const action = h('select', {}, Object.entries(ACTIONS).map(([k, v]) => h('option', { value: k }, v)));
  action.value = existing?.action || 'block';
  const server = h('select', {}, cfg.upstreams.map(u => h('option', { value: u.id }, u.name)));
  if (existing?.upstream) server.value = existing.upstream;
  const serverField = h('label', { class: 'field' }, h('span', {}, 'Server (falls back to the normal chain if it is down)'), server);
  const result = h('div', { class: 'result' });
  const sync = () => { serverField.hidden = action.value !== 'upstream'; };
  action.onchange = sync;
  sync();

  d.replaceChildren(
    h('h2', {}, existing ? 'Edit rule' : 'Add rule'),
    h('label', { class: 'field' }, h('span', {}, 'Name'), name),
    h('label', { class: 'field' },
      h('span', {}, 'Domains, one per line. "example.com" also matches its subdomains; "*.example.com" matches only subdomains.'), domains),
    h('label', { class: 'field' }, h('span', {}, 'Action'), action),
    serverField,
    result,
    h('div', { class: 'actions' },
      existing ? h('button', { class: 'btn danger', onclick: async () => {
        if (await save(c => { c.rules = c.rules.filter(r => r.id !== existing.id); })) d.close();
      } }, 'Delete') : null,
      h('span', { style: 'flex:1' }),
      h('button', { class: 'btn', onclick: () => d.close() }, 'Cancel'),
      h('button', { class: 'btn primary', onclick: async () => {
        const list = domains.value.split(/[\s,]+/).filter(Boolean);
        if (!list.length) { result.textContent = 'Enter at least one domain.'; return; }
        if (action.value === 'upstream' && !server.value) { result.textContent = 'Add a server first.'; return; }
        const r = {
          id: existing?.id || newId(), name: name.value.trim() || list[0], enabled: existing ? existing.enabled : true,
          domains: list, action: action.value, upstream: action.value === 'upstream' ? server.value : '',
        };
        const ok = await save(c => {
          const i = c.rules.findIndex(x => x.id === r.id);
          if (i >= 0) c.rules[i] = r; else c.rules.push(r);
        }, 'Rule saved');
        if (ok) d.close();
      } }, 'Save')),
  );
  d.showModal();
}

// ---------- Activity ----------

const ACTION_LABEL = { resolved: 'Resolved', cached: 'Cached', stale: 'Cached (stale)', blocked: 'Blocked', passthrough: 'Original DNS', failed: 'Failed' };

function renderActivity() {
  const root = $('#activity');
  if (!root.firstChild) {
    const lookup = h('input', { type: 'text', placeholder: 'Look up a domain…', style: 'width:220px', spellcheck: 'false' });
    const doLookup = async () => {
      if (!lookup.value.trim()) return;
      try {
        const e = await api('POST', '/api/resolve', { name: lookup.value.trim(), type: 'A' });
        toast(`${e.name}: ${ACTION_LABEL[e.action] || e.action}${e.server ? ' via ' + e.server : ''} — ${e.answer || e.rcode || e.detail}`);
      } catch (err) { toast(err.message, true); }
    };
    lookup.onkeydown = e => { if (e.key === 'Enter') doLookup(); };
    root.append(
      h('div', { class: 'bar' },
        h('input', { type: 'text', placeholder: 'Filter by domain, server or result…', style: 'width:300px', oninput: e => { logFilter = e.target.value.toLowerCase(); drawLog(); } }),
        h('span', { class: 'grow' }),
        lookup, h('button', { class: 'btn', onclick: doLookup }, 'Look up'),
        h('button', { class: 'btn', id: 'pause', onclick: e => { logPaused = !logPaused; e.target.textContent = logPaused ? 'Resume' : 'Pause'; drawLog(); } }, 'Pause'),
        h('button', { class: 'btn', onclick: () => { entries = []; drawLog(); } }, 'Clear')),
      h('div', { class: 'tablewrap' },
        h('table', {},
          h('thead', {}, h('tr', {}, ['Time', 'Domain', 'Type', 'Result', 'Server', 'Answer', 'Time'].map((t, i) => h('th', i === 6 ? { style: 'text-align:right' } : {}, t)))),
          h('tbody', { id: 'logbody' }))),
      h('p', { class: 'hint' }, 'Click a row to create a rule for that domain.'));
  }
  drawLog();
}

function drawLog() {
  const body = $('#logbody');
  if (!body || logPaused && body.firstChild) return;
  const rows = [];
  for (let i = entries.length - 1; i >= 0 && rows.length < 400; i--) {
    const e = entries[i];
    if (logFilter && !`${e.name} ${e.server} ${e.action} ${e.answer} ${e.rule}`.toLowerCase().includes(logFilter)) continue;
    const via = e.server ? `${e.server}` : e.rule ? `Rule: ${e.rule}` : e.action === 'passthrough' ? e.dest : '';
    rows.push(h('tr', { title: e.detail || '', onclick: () => ruleDialog(null, e.name) },
      h('td', { class: 't' }, new Date(e.time).toLocaleTimeString()),
      h('td', {}, e.name),
      h('td', { class: 't' }, e.type),
      h('td', {}, h('span', { class: 'badge ' + e.action }, ACTION_LABEL[e.action] || e.action)),
      h('td', {}, via),
      h('td', { class: 't' }, e.answer || (e.rcode !== 'NOERROR' ? e.rcode : '')),
      h('td', { class: 'ms' }, fmtMs(e.ms))));
  }
  body.replaceChildren(...(rows.length ? rows : [h('tr', {}, h('td', { colspan: 7, class: 'empty' }, 'No queries yet.'))]));
}

// ---------- Settings ----------

function renderSettings() {
  const s = cfg.settings;
  const set = (key, value, msg) => save(c => { c.settings[key] = value; }, msg || 'Saved');
  const row = (title, hint, control) => h('div', { class: 'setting' },
    h('div', { class: 'grow' }, h('div', {}, title), h('p', { class: 'hint' }, hint)), control);
  const toggle = key => h('label', { class: 'switch small' },
    h('input', { type: 'checkbox', checked: s[key], onchange: e => set(key, e.target.checked) }), h('span', {}));
  const select = (key, options) => {
    const el = h('select', { onchange: e => set(key, e.target.value) }, Object.entries(options).map(([k, v]) => h('option', { value: k }, v)));
    el.value = s[key];
    return el;
  };
  const number = (key, min, max) => h('input', { type: 'number', min, max, value: s[key], onchange: e => {
    const v = Math.min(max, Math.max(min, parseInt(e.target.value, 10) || min));
    set(key, v);
  } });
  const boot = h('input', { type: 'text', style: 'width:420px', value: s.bootstrapDoh.join(', '), spellcheck: 'false',
    onchange: e => set('bootstrapDoh', e.target.value.split(/[\s,]+/).filter(Boolean)) });

  $('#settings').replaceChildren(
    h('div', { class: 'card' },
      h('h2', {}, 'Reliability'),
      row('When no encrypted server is reachable', 'For example while a VPN is still connecting or its firewall blocks everything else.',
        select('onFailure', { passthrough: 'Use the original DNS server', servfail: 'Fail the query (strict, no plaintext)' })),
      row('Try the next server after', 'Milliseconds without an answer before the backup is asked as well. An error switches immediately.', number('hedgeMs', 100, 10000)),
      row('Query timeout', 'Milliseconds before a server attempt counts as failed.', number('timeoutMs', 500, 15000)),
      row('Mark a server as down after', 'Consecutive failures. A down server is skipped and re-checked in the background.', number('failThreshold', 1, 20))),
    h('div', { class: 'card' },
      h('h2', {}, 'Interception'),
      row('TCP DNS (port 53)', 'Rarely used. Proxy answers it through your servers; Block refuses it; Allow lets it pass unencrypted.',
        select('tcpMode', { proxy: 'Proxy through Nax-DNSManager', block: 'Block', allow: 'Allow (not intercepted)' })),
      row('Intercept queries to local resolvers', 'Also capture DNS sent to 127.0.0.1, e.g. a VPN client\'s built-in DNS proxy.', toggle('interceptLoopback')),
      row('Keep Windows from switching to its own encrypted DNS', 'Answers resolver discovery (DDR) with "not found" so queries stay on port 53 where Nax-DNSManager sees them.', toggle('blockDdr')),
      row('Tell Firefox not to use its own DNS-over-HTTPS', 'Answers Firefox\'s canary domain so it keeps using system DNS.', toggle('firefoxCanary'))),
    h('div', { class: 'card' },
      h('h2', {}, 'Cache'),
      row('Cache answers', `Repeat lookups are answered instantly. ${state?.stats?.cacheSize ?? 0} entries now.`, toggle('cache')),
      row('Maximum cache time', 'Seconds. Longer server TTLs are shortened to this.', number('cacheMaxTtl', 10, 86400)),
      row('Clear the cache', 'Windows keeps its own cache too; run "ipconfig /flushdns" to clear that one.',
        h('button', { class: 'btn', onclick: async () => { await api('POST', '/api/cache/flush'); toast('Cache cleared'); } }, 'Clear cache'))),
    h('div', { class: 'card' },
      h('h2', {}, 'Advanced'),
      row('Bootstrap resolvers', 'DoH endpoints addressed by IP, used only to find your servers\' addresses.', boot),
      h('p', { class: 'hint' }, `Nax-DNSManager ${state?.version || ''}`)),
  );
}

// ---------- Shell ----------

function render() {
  if (!cfg) return;
  $('#power').checked = cfg.active;
  const enabled = cfg.upstreams.filter(u => u.enabled);
  const anyHealthy = enabled.some(u => serverState(u.id)?.healthy);
  document.body.className = state?.intercepting ? (anyHealthy ? 'active' : 'degraded') : '';
  // Do not rebuild a form the user is typing in.
  if ($('#dialog').open) return;
  const active = document.activeElement;
  if (active && active.closest('main') && ['INPUT', 'SELECT', 'TEXTAREA'].includes(active.tagName) && tab !== 'overview') {
    if (tab === 'activity') drawLog();
    return;
  }
  ({ overview: renderOverview, servers: renderServers, rules: renderRules, activity: renderActivity, settings: renderSettings })[tab]();
}

function showBanner() {
  const b = $('#banner');
  b.hidden = !serviceDown;
  if (!serviceDown || b.firstChild) return;
  b.replaceChildren(
    h('div', { class: 'grow' }, 'The Nax-DNSManager service is not installed or not running. DNS is not being intercepted.'),
    h('button', { class: 'btn', onclick: async () => {
      try { await api('POST', '/local/install'); toast('Installing… confirm the Windows prompt.'); }
      catch (e) { toast(e.message, true); }
    } }, 'Install and start service'));
}

async function refreshState() {
  try {
    state = await api('GET', '/api/state');
    if (serviceDown || !cfg) cfg = await api('GET', '/api/config');
    serviceDown = false;
    cfg.active = state.active;
  } catch (e) {
    serviceDown = true;
  }
  showBanner();
}

async function refreshLog() {
  if (serviceDown) return;
  try {
    const fresh = await api('GET', '/api/log?since=' + logSeq);
    if (fresh.length) {
      // A restarted service starts numbering again.
      if (fresh[0].seq <= logSeq) entries = [];
      entries.push(...fresh);
      logSeq = fresh[fresh.length - 1].seq;
      if (entries.length > 3000) entries = entries.slice(-2000);
      if (tab === 'activity') drawLog();
    }
  } catch (e) { /* next tick */ }
}

$('#nav').addEventListener('click', e => {
  const t = e.target.dataset.tab;
  if (!t) return;
  tab = t;
  for (const b of $('#nav').children) b.classList.toggle('on', b === e.target);
  for (const s of document.querySelectorAll('main > section')) s.hidden = s.id !== t;
  document.activeElement.blur();
  render();
});
$('#power').addEventListener('change', e => setActive(e.target.checked));

(async function start() {
  await refreshState();
  render();
  setInterval(async () => { await refreshState(); render(); }, 2000);
  setInterval(refreshLog, 1500);
  refreshLog();
})();
