const view = document.getElementById("view");
const modal = document.getElementById("modal");
const PAGE_SIZE = 20;

function route() {
  const parts = (location.hash || "#/").replace(/^#/, "").split("/").filter(Boolean);
  if (parts[0] === "rooms" && parts[1]) return { name: "room", id: decodeURIComponent(parts[1]) };
  if (parts[0] === "rooms") return { name: "rooms" };
  if (parts[0] === "signals") return { name: "signals" };
  return { name: "home" };
}

function setNav(name) {
  const key = name === "room" ? "rooms" : name;
  document.querySelectorAll("[data-nav]").forEach((a) => a.classList.toggle("active", a.dataset.nav === key));
}

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function fmtBytes(n) {
  n = Number(n) || 0;
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function fmtDur(sec) {
  sec = Math.max(0, Number(sec) || 0);
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = sec % 60;
  if (h > 0) return `${h} 小时 ${m} 分`;
  if (m > 0) return `${m} 分 ${s} 秒`;
  return `${s} 秒`;
}

function fmtTime(s) {
  if (!s) return "-";
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? String(s) : d.toLocaleString("zh-CN", { hour12: false });
}

async function api(path, opts) {
  const res = await fetch(path, {
    headers: { "Content-Type": "application/json", ...(opts && opts.headers) },
    ...opts,
  });
  const text = await res.text();
  let body = null;
  if (text) {
    try { body = JSON.parse(text); } catch { body = text; }
  }
  if (!res.ok) {
    const msg = body && body.message ? body.message : text || res.statusText;
    throw new Error(msg);
  }
  return body;
}

function pill(online) {
  return `<span class="pill ${online ? "on" : "off"}">${online ? "在线" : "离线"}</span>`;
}

function countCell(n, emphasize) {
  const v = Number(n) || 0;
  return `<span class="num${emphasize && v > 0 ? " on" : ""}">${esc(v)}</span>`;
}

function ico(path, color) {
  return `<svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><path fill="${color}" d="${path}"/></svg>`;
}

const IcoHome = "M12,5.69L17,10.19V18H15V12H9V18H7V10.19L12,5.69M12,3L2,12H5V20H11V14H13V20H19V12H22L12,3Z";
const IcoNet = "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,19.93C7.05,19.44 4,16.08 4,12C4,11.38 4.08,10.78 4.21,10.21L9,15V16A2,2 0 0,0 11,18M17.9,17.39C17.64,16.59 16.89,16 16,16H15V13A1,1 0 0,0 14,12H8V10H10A1,1 0 0,0 11,9V7H13A2,2 0 0,0 15,5V4.59C17.93,5.77 20,8.64 20,12C20,14.08 19.2,15.97 17.9,17.39Z";
const IcoShare = "M18,16.08C17.24,16.08 16.56,16.38 16.04,16.85L8.91,12.7C8.96,12.47 9,12.24 9,12C9,11.76 8.96,11.53 8.91,11.3L15.96,7.19C16.5,7.69 17.21,8 18,8A3,3 0 0,0 21,5A3,3 0 0,0 18,2A3,3 0 0,0 15,5C15,5.24 15.04,5.47 15.09,5.7L8.04,9.81C7.5,9.31 6.79,9 6,9A3,3 0 0,0 3,12A3,3 0 0,0 6,15C6.79,15 7.5,14.69 8.04,14.19L15.16,18.34C15.11,18.55 15.08,18.77 15.08,19C15.08,20.61 16.39,21.91 18,21.91C19.61,21.91 20.92,20.61 20.92,19A2.92,2.92 0 0,0 18,16.08Z";
const IcoDown = "M5,20H19V18H5M19,9H15V3H9V9H5L12,16L19,9Z";

function kpi(cls, label, value, sub, path, color, valueAttr, dataKey) {
  const attr = valueAttr ? ` ${valueAttr}` : "";
  const key = dataKey ? ` data-kpi="${esc(dataKey)}"` : "";
  return `<article class="kpi ${cls}">${ico(path, color)}<div class="kpi-k">${esc(label)}</div><div class="kpi-v"${attr}${key}>${esc(value)}</div><div class="kpi-s">${esc(sub)}</div></article>`;
}

let uptimeTimer = 0;
function stopUptime() {
  if (uptimeTimer) clearInterval(uptimeTimer);
  uptimeTimer = 0;
}
function startUptime(startedAt) {
  stopUptime();
  const origin = Date.parse(startedAt);
  if (!Number.isFinite(origin)) return;
  const paint = () => {
    const node = document.querySelector("[data-uptime]");
    if (!node) { stopUptime(); return; }
    const text = fmtDur(Math.floor((Date.now() - origin) / 1000));
    node.textContent = text;
    const line = document.querySelector("[data-uptime-line]");
    if (line) line.textContent = text;
  };
  paint();
  uptimeTimer = setInterval(paint, 1000);
}

// ---- 表格：筛选 / 分页 / 多选 ----
const tableUi = Object.create(null);

function tableState(key) {
  if (!tableUi[key]) {
    tableUi[key] = { q: "", page: 1, size: PAGE_SIZE, selected: new Set() };
  }
  return tableUi[key];
}

function matchText(q, ...parts) {
  const needle = String(q || "").trim().toLowerCase();
  if (!needle) return true;
  return parts.some((p) => String(p ?? "").toLowerCase().includes(needle));
}

function pageSlice(items, st) {
  const total = items.length;
  const pages = Math.max(1, Math.ceil(total / st.size) || 1);
  if (st.page > pages) st.page = pages;
  if (st.page < 1) st.page = 1;
  const start = (st.page - 1) * st.size;
  return {
    total,
    pages,
    start,
    end: Math.min(start + st.size, total),
    rows: items.slice(start, start + st.size),
  };
}

function pruneSelected(st, ids) {
  const keep = new Set(ids);
  for (const id of [...st.selected]) {
    if (!keep.has(id)) st.selected.delete(id);
  }
}

function toolbarHtml(key, placeholder, opts = {}) {
  const st = tableState(key);
  const selected = st.selected.size;
  const batchLabel = opts.batchLabel || "批量删除";
  const clearLabel = opts.clearLabel || "清除数据";
  return `<div class="table-bar" data-table-bar="${esc(key)}">
    <div class="table-bar-l">
      <input type="search" data-filter="${esc(key)}" value="${esc(st.q)}" placeholder="${esc(placeholder)}" />
      <span class="table-meta" data-table-meta="${esc(key)}"></span>
    </div>
    <div class="table-bar-r">
      <button type="button" class="ghost" data-batch="${esc(key)}" ${selected ? "" : "disabled"}>${esc(batchLabel)}${selected ? ` (${selected})` : ""}</button>
      <button type="button" data-clear="${esc(key)}">${esc(clearLabel)}</button>
    </div>
  </div>`;
}

function pagerHtml(key, pageInfo) {
  const { total, pages, start, end, page } = pageInfo;
  return `<div class="pager" data-pager="${esc(key)}">
    <span>共 ${total} 条 · 每页 ${PAGE_SIZE} 条 · 第 ${page}/${pages} 页${total ? ` · ${start + 1}-${end}` : ""}</span>
    <div class="pager-btns">
      <button type="button" class="ghost" data-page="${esc(key)}" data-to="1" ${page <= 1 ? "disabled" : ""}>首页</button>
      <button type="button" class="ghost" data-page="${esc(key)}" data-to="prev" ${page <= 1 ? "disabled" : ""}>上一页</button>
      <button type="button" class="ghost" data-page="${esc(key)}" data-to="next" ${page >= pages ? "disabled" : ""}>下一页</button>
      <button type="button" class="ghost" data-page="${esc(key)}" data-to="last" ${page >= pages ? "disabled" : ""}>末页</button>
    </div>
  </div>`;
}

function checkCell(key, id) {
  const on = tableState(key).selected.has(id);
  return `<td class="check"><input type="checkbox" data-check="${esc(key)}" value="${esc(id)}" ${on ? "checked" : ""}></td>`;
}

function checkAllHead(key, pageIds) {
  const st = tableState(key);
  const all = pageIds.length > 0 && pageIds.every((id) => st.selected.has(id));
  return `<th class="check"><input type="checkbox" data-check-all="${esc(key)}" ${all ? "checked" : ""} ${pageIds.length ? "" : "disabled"}></th>`;
}

function renderPanel(sel, {
  key,
  items,
  idOf,
  filter,
  headHtml,
  rowHtml,
  emptyHtml,
  placeholder,
  batchLabel,
  clearLabel,
}) {
  const wrap = view.querySelector(sel);
  if (!wrap) return;
  const st = tableState(key);
  const filtered = (items || []).filter(filter);
  const ids = filtered.map(idOf);
  pruneSelected(st, ids);
  const page = pageSlice(filtered, st);
  const pageIds = page.rows.map(idOf);
  const thead = `<tr>${checkAllHead(key, pageIds)}${headHtml}</tr>`;
  const tbody = page.rows.map((row) => rowHtml(row, key)).join("");
  const table = filtered.length
    ? `<div class="table-scroll"><table><thead>${thead}</thead><tbody>${tbody}</tbody></table></div>`
    : emptyHtml;
  const html = `${toolbarHtml(key, placeholder, { batchLabel, clearLabel })}${table}${pagerHtml(key, {
    total: page.total,
    pages: page.pages,
    start: page.start,
    end: page.end,
    page: st.page,
  })}`;
  const sig = [
    key,
    st.q,
    st.page,
    st.size,
    [...st.selected].sort().join(","),
    filtered.map((x) => idOf(x)).join("\n"),
    page.rows.map((x) => rowHtml(x, key)).join(""),
  ].join("||");
  if (wrap._sig === sig) return;
  const prevScroll = wrap.querySelector(".table-scroll");
  const scrollTop = prevScroll ? prevScroll.scrollTop : 0;
  const scrollLeft = prevScroll ? prevScroll.scrollLeft : 0;
  wrap._sig = sig;
  wrap.innerHTML = html;
  const nextScroll = wrap.querySelector(".table-scroll");
  if (nextScroll) {
    nextScroll.scrollTop = scrollTop;
    nextScroll.scrollLeft = scrollLeft;
  }
}

function roomFilter(r, q) {
  return matchText(q, r.roomNo, r.roomId, r.deviceCount, r.onlineCount);
}

function deviceFilter(d, q) {
  const ips = (d.candidates || []).map((c) => c && c.address).filter(Boolean).join(" ");
  return matchText(q, d.name, d.id, d.platform, d.remoteAddress, ips, d.online ? "在线" : "离线");
}

function sessionFilter(s, q) {
  return matchText(q, s.roomNo, s.sessionId, s.deviceAName, s.deviceBName, s.deviceA, s.deviceB);
}

function signalFilter(s, q) {
  return matchText(
    q,
    s.roomNo,
    s.roomId,
    s.fromName,
    s.fromId,
    s.toName,
    s.toId,
    s.msgType,
    s.ok ? "成功" : s.error || "失败",
    s.id
  );
}

function roomRow(r, key) {
  return `<tr class="link" data-room="${esc(r.roomId)}">
    ${checkCell(key, r.roomId)}
    <td>${esc(r.roomNo)}</td>
    <td class="num">${esc(r.deviceCount)}</td>
    <td>${countCell(r.onlineCount, true)}</td>
    <td>${esc(fmtTime(r.lastSeen))}</td>
    <td>${esc(fmtTime(r.createdAt))}</td>
    <td class="ops"><button type="button" data-delete-room="${esc(r.roomId)}" data-room-no="${esc(r.roomNo)}">删除</button></td>
  </tr>`;
}

function deviceIps(d) {
  const seen = new Set();
  const lines = [];
  for (const c of d.candidates || []) {
    const kind = String(c?.kind || "").toLowerCase();
    if (kind === "relay") continue;
    const address = String(c?.address || "").trim();
    if (!address || seen.has(address)) continue;
    seen.add(address);
    lines.push(address);
  }
  if (!lines.length) return `<span class="subid">—</span>`;
  return `<div class="ips">${lines.map((a) => `<span>${esc(a)}</span>`).join("")}</div>`;
}

function deviceRow(d, key) {
  return `<tr data-device="${esc(d.id)}">
    ${checkCell(key, d.id)}
    <td>${esc(d.name)}<div class="subid mono">${esc(d.id)}</div></td>
    <td>${pill(d.online)}</td>
    <td>${esc(d.platform || "-")}</td>
    <td class="mono">${esc(d.remoteAddress || "-")}</td>
    <td class="mono">${deviceIps(d)}</td>
    <td>${esc(fmtTime(d.lastSeen))}</td>
    <td class="ops">
      ${d.online ? `<button type="button" data-kick="${esc(d.id)}">踢出</button>` : ""}
      <button type="button" data-delete-device="${esc(d.id)}" data-device-name="${esc(d.name)}">删除</button>
    </td>
  </tr>`;
}

function sessionRow(s, key) {
  return `<tr data-session="${esc(s.sessionId)}">
    ${checkCell(key, s.sessionId)}
    <td>${esc(s.roomNo)}</td>
    <td>${esc(s.deviceAName)} ↔ ${esc(s.deviceBName)}</td>
    <td>${esc(fmtBytes((s.bytesAtoB || 0) + (s.bytesBtoA || 0)))}</td>
    <td>${esc(fmtTime(s.lastActivity))}</td>
    <td class="ops"><button type="button" data-close="${esc(s.sessionId)}">关闭</button></td>
  </tr>`;
}

function signalRow(s, key) {
  return `<tr data-signal="${esc(s.id)}">
    ${checkCell(key, s.id)}
    <td>${esc(fmtTime(s.at))}</td>
    <td>${esc(s.roomNo || "—")}</td>
    <td>${esc(s.fromName || s.fromId)}<div class="subid mono">${esc(s.fromId)}</div></td>
    <td>${esc(s.toName || s.toId || "—")}<div class="subid mono">${esc(s.toId || "")}</div></td>
    <td><span class="pill soft">${esc(s.msgType || "—")}</span></td>
    <td>${s.ok ? `<span class="pill ok">成功</span>` : `<span class="pill bad">${esc(s.error || "失败")}</span>`}</td>
    <td class="actions">
      <button type="button" class="ghost" data-signal-detail="${esc(s.id)}">详情</button>
      <button type="button" class="ghost" data-signal-content="${esc(s.id)}" ${s.hasPayload ? "" : "disabled"}>内容</button>
    </td>
  </tr>`;
}

function setText(sel, text) {
  const node = view.querySelector(sel);
  if (node && node.textContent !== text) node.textContent = text;
}

// ---- SSE ----
let stream = null;
let lastSnap = null;
let mountedKey = "";

function stopStream() {
  if (stream) {
    stream.close();
    stream = null;
  }
}

function startStream() {
  if (stream || document.visibilityState === "hidden") return;
  stream = new EventSource("/api/dashboard/stream");
  stream.addEventListener("dashboard", (ev) => {
    try {
      lastSnap = JSON.parse(ev.data);
      applySnap(lastSnap);
    } catch (e) {
      console.warn("dashboard SSE parse failed", e);
    }
  });
}

function syncStream() {
  if (document.visibilityState === "hidden") stopStream();
  else startStream();
}

function mountKeyOf(r) {
  return r.name === "room" ? `room:${r.id}` : r.name;
}

function homeShell(data) {
  return `
    <div class="page page-home">
      <div class="page-h">
        <h1>首页</h1>
        <p><span data-server>${esc(data.server)}</span> · 已运行 <span data-uptime-line>${esc(fmtDur(data.uptimeSec))}</span> · 房间号取该房间第一台注册电脑的名称</p>
      </div>
      <section class="kpis">
        ${kpi("a", "房间", data.registeredRooms ?? 0, "注册房间", IcoHome, "#F59E0B", "", "rooms")}
        ${kpi("e", "注册设备", data.registeredDevices ?? 0, "含离线记录", IcoNet, "#7DD3FC", "", "registered")}
        ${kpi("b", "在线设备", data.onlineDevices ?? 0, "当前在线", IcoNet, "#93C5FD", "", "online")}
        ${kpi("g", "运行时间", fmtDur(data.uptimeSec), data.server || "", IcoHome, "#FDE68A", "data-uptime", "uptime")}
        ${kpi("f", "信令次数", data.signals || 0, "累计转发次数", IcoShare, "#C4B5FD", "", "signals")}
        ${kpi("c", "中继会话", data.sessions ?? 0, "进行中", IcoShare, "#6EE7B7", "", "sessions")}
        ${kpi("d", "转发字节", fmtBytes(data.relayBytes), "累计转发", IcoDown, "#FDBA74", "", "bytes")}
        ${kpi("h", "延迟统计", "—", "暂无样本", IcoNet, "#94A3B8")}
      </section>
      <div class="page-blocks">
        <section class="block">
          <h2>房间</h2>
          <div class="panel" data-home-rooms></div>
        </section>
        <section class="block">
          <h2>中继会话</h2>
          <div class="panel" data-home-sessions></div>
        </section>
      </div>
    </div>`;
}

function patchHome(data) {
  const rooms = data.rooms || [];
  const sessions = data.sessionList || [];
  setText("[data-server]", data.server || "");
  setText('[data-kpi="rooms"]', String(data.registeredRooms ?? 0));
  setText('[data-kpi="registered"]', String(data.registeredDevices ?? 0));
  setText('[data-kpi="online"]', String(data.onlineDevices ?? 0));
  setText('[data-kpi="signals"]', String(data.signals || 0));
  setText('[data-kpi="sessions"]', String(data.sessions ?? 0));
  setText('[data-kpi="bytes"]', fmtBytes(data.relayBytes));
  const sub = view.querySelector(".kpi.g .kpi-s");
  if (sub) sub.textContent = data.server || "";

  const stRooms = tableState("homeRooms");
  renderPanel("[data-home-rooms]", {
    key: "homeRooms",
    items: rooms,
    idOf: (r) => r.roomId,
    filter: (r) => roomFilter(r, stRooms.q),
    headHtml: `<th>房间名称</th><th>节点</th><th>连接数</th><th>最后连接</th><th>注册时间</th><th>操作</th>`,
    rowHtml: roomRow,
    emptyHtml: `<div class="empty">还没有注册房间。设备连上边缘口并完成 hello 后，会以第一台电脑的名称建立房间。</div>`,
    placeholder: "筛选房间名称 / ID…",
    clearLabel: "清除全部房间",
  });

  const stSessions = tableState("homeSessions");
  renderPanel("[data-home-sessions]", {
    key: "homeSessions",
    items: sessions,
    idOf: (s) => s.sessionId,
    filter: (s) => sessionFilter(s, stSessions.q),
    headHtml: `<th>房间</th><th>对端</th><th>流量</th><th>最近活动</th><th>操作</th>`,
    rowHtml: sessionRow,
    emptyHtml: `<div class="empty">暂无中继会话。</div>`,
    placeholder: "筛选房间 / 设备…",
    batchLabel: "批量关闭",
    clearLabel: "清除全部会话",
  });
  startUptime(data.startedAt);
}

function roomsShell() {
  return `
    <div class="page">
      <div class="page-h">
        <h1>房间</h1>
        <p>同一安全密钥的设备在同一房间。房间号是该房间第一台注册电脑的名称，之后不再改变。</p>
      </div>
      <div class="panel" data-rooms-list></div>
    </div>`;
}

function patchRooms(data) {
  const rooms = data.rooms || [];
  const st = tableState("rooms");
  renderPanel("[data-rooms-list]", {
    key: "rooms",
    items: rooms,
    idOf: (r) => r.roomId,
    filter: (r) => roomFilter(r, st.q),
    headHtml: `<th>房间名称</th><th>节点</th><th>连接数</th><th>最后连接</th><th>注册时间</th><th>操作</th>`,
    rowHtml: roomRow,
    emptyHtml: `<div class="empty">还没有注册房间。</div>`,
    placeholder: "筛选房间名称 / ID…",
    clearLabel: "清除全部房间",
  });
}

function roomShell(room) {
  return `
    <div class="page">
      <div class="crumb"><a href="#/rooms">房间</a><span>/</span><span data-room-title>${esc(room.roomNo)}</span></div>
      <div class="page-h">
        <h1 data-room-title>${esc(room.roomNo)}</h1>
        <p data-room-sub>在线 ${esc(room.onlineCount)} · 注册 ${esc(room.deviceCount)} · 建立于 ${esc(fmtTime(room.createdAt))}</p>
      </div>
      <div class="panel" data-room-devices></div>
    </div>`;
}

function patchRoom(room) {
  view.querySelectorAll("[data-room-title]").forEach((n) => {
    if (n.textContent !== room.roomNo) n.textContent = room.roomNo;
  });
  setText(
    "[data-room-sub]",
    `在线 ${room.onlineCount} · 注册 ${room.deviceCount} · 建立于 ${fmtTime(room.createdAt)}`
  );
  const devices = room.devices || [];
  const key = `roomDevices:${room.roomId}`;
  const st = tableState(key);
  renderPanel("[data-room-devices]", {
    key,
    items: devices,
    idOf: (d) => d.id,
    filter: (d) => deviceFilter(d, st.q),
    headHtml: `<th>设备</th><th>状态</th><th>平台</th><th>地址</th><th>设备 IP</th><th>最后连接</th><th>操作</th>`,
    rowHtml: deviceRow,
    emptyHtml: `<div class="empty">这个房间还没有设备记录。</div>`,
    placeholder: "筛选设备名称 / ID / IP…",
    clearLabel: "清除本房间设备",
  });
}

function signalsShell() {
  return `
    <div class="page">
      <div class="page-h">
        <h1>信令</h1>
        <p>边缘口转发的信令（内存环形缓冲，最多 500 条）。展示谁到谁，可查看详情与内容。</p>
      </div>
      <div class="panel" data-signals-list></div>
    </div>`;
}

function patchSignals(data) {
  const rows = data.signalList || [];
  const st = tableState("signals");
  renderPanel("[data-signals-list]", {
    key: "signals",
    items: rows,
    idOf: (s) => s.id,
    filter: (s) => signalFilter(s, st.q),
    headHtml: `<th>时间</th><th>房间</th><th>发送方</th><th>接收方</th><th>类型</th><th>结果</th><th>操作</th>`,
    rowHtml: signalRow,
    emptyHtml: `<div class="empty">暂无信令记录。设备互发 signal / 中继请求后会出现在这里。</div>`,
    placeholder: "筛选房间 / 设备 / 类型…",
    clearLabel: "清除信令",
  });
}

function prettyJson(raw) {
  if (!raw) return "（无内容）";
  try { return JSON.stringify(JSON.parse(raw), null, 2); }
  catch { return String(raw); }
}

function closeModal() {
  if (!modal) return;
  modal.hidden = true;
  modal.innerHTML = "";
}

async function openSignalModal(id, focusContent) {
  if (!modal) return;
  modal.hidden = false;
  modal.innerHTML = `<div class="modal-card"><div class="modal-h"><div><h2>信令</h2><p>加载中…</p></div>
    <button type="button" class="ghost" data-modal-close>关闭</button></div></div>`;
  try {
    const row = await api(`/api/signals/${encodeURIComponent(id)}`);
    const title = focusContent ? "信令内容" : "信令详情";
    modal.innerHTML = `
      <div class="modal-card">
        <div class="modal-h">
          <div>
            <h2>${esc(title)}</h2>
            <p>${esc(row.msgType || "signal")} · ${esc(fmtTime(row.at))}</p>
          </div>
          <button type="button" class="ghost" data-modal-close>关闭</button>
        </div>
        <div class="modal-b">
          <dl class="kv">
            <dt>结果</dt><dd>${row.ok ? `<span class="pill ok">成功</span>` : `<span class="pill bad">${esc(row.error || "失败")}</span>`}</dd>
            <dt>房间</dt><dd>${esc(row.roomNo || "—")}<div class="subid mono">${esc(row.roomId || "")}</div></dd>
            <dt>发送方</dt><dd>${esc(row.fromName || row.fromId)}<div class="subid mono">${esc(row.fromId || "")}</div></dd>
            <dt>接收方</dt><dd>${esc(row.toName || row.toId || "—")}<div class="subid mono">${esc(row.toId || "")}</div></dd>
            <dt>类型</dt><dd>${esc(row.msgType || "—")}</dd>
            <dt>会话</dt><dd class="mono">${esc(row.sessionId || "—")}</dd>
            <dt>载荷大小</dt><dd>${esc(row.payloadBytes || 0)} B</dd>
            <dt>记录 ID</dt><dd class="mono">${esc(row.id)}</dd>
          </dl>
          <h3 style="margin:0 0 0.45rem;font-size:0.92rem;">内容</h3>
          <pre class="payload">${esc(prettyJson(row.payloadJson))}</pre>
        </div>
      </div>`;
    if (focusContent) {
      const pre = modal.querySelector(".payload");
      if (pre) pre.scrollIntoView({ block: "nearest" });
    }
  } catch (e) {
    modal.innerHTML = `<div class="modal-card"><div class="modal-h"><div><h2>信令</h2>
      <p class="err" style="margin-top:0.5rem">${esc(e.message)}</p></div>
      <button type="button" class="ghost" data-modal-close>关闭</button></div></div>`;
  }
}

function applySnap(data) {
  const r = route();
  setNav(r.name);
  const key = mountKeyOf(r);

  if (r.name === "home") {
    if (mountedKey !== key) {
      mountedKey = key;
      view.innerHTML = homeShell(data);
    }
    patchHome(data);
    return;
  }

  if (r.name === "rooms") {
    stopUptime();
    if (mountedKey !== key) {
      mountedKey = key;
      view.innerHTML = roomsShell();
    }
    patchRooms(data);
    return;
  }

  if (r.name === "signals") {
    stopUptime();
    if (mountedKey !== key) {
      mountedKey = key;
      view.innerHTML = signalsShell();
    }
    patchSignals(data);
    return;
  }

  if (r.name === "room") {
    stopUptime();
    const room = (data.rooms || []).find((x) => x.roomId === r.id);
    if (!room) {
      mountedKey = key;
      view.innerHTML = `
        <div class="page">
          <div class="crumb"><a href="#/rooms">房间</a></div>
          <div class="page-h"><h1>房间</h1></div>
          <div class="err">房间不存在</div>
        </div>`;
      return;
    }
    if (mountedKey !== key) {
      mountedKey = key;
      view.innerHTML = roomShell(room);
    }
    patchRoom(room);
  }
}

function showLoading() {
  const r = route();
  setNav(r.name);
  mountedKey = "";
  stopUptime();
  const title = r.name === "signals" ? "信令" : (r.name === "rooms" || r.name === "room" ? "房间" : "首页");
  view.innerHTML = `<div class="page"><div class="page-h"><h1>${title}</h1><p>加载中…</p></div></div>`;
}

function onRoute() {
  if (lastSnap) applySnap(lastSnap);
  else showLoading();
  syncStream();
}

function refreshUi() {
  const active = document.activeElement;
  const filterKey = active && active.matches && active.matches("[data-filter]") ? active.dataset.filter : null;
  const selStart = filterKey != null ? active.selectionStart : null;
  const selEnd = filterKey != null ? active.selectionEnd : null;
  if (lastSnap) applySnap(lastSnap);
  if (filterKey) {
    const el = view.querySelector(`[data-filter="${CSS.escape(filterKey)}"]`);
    if (el) {
      el.focus();
      if (selStart != null && selEnd != null) {
        try { el.setSelectionRange(selStart, selEnd); } catch { /* ignore */ }
      }
    }
  }
}

function selectedIds(key) {
  return [...tableState(key).selected];
}

function clearSelection(key) {
  tableState(key).selected.clear();
}

async function runBatch(key) {
  const ids = selectedIds(key);
  if (!ids.length) return;

  try {
    if (key === "homeRooms" || key === "rooms") {
      if (!confirm(`批量删除选中的 ${ids.length} 个房间？将踢出在线设备并清除注册记录。`)) return;
      await api("/api/rooms/batch-delete", { method: "POST", body: JSON.stringify({ ids }) });
    } else if (key.startsWith("roomDevices:")) {
      if (!confirm(`批量删除选中的 ${ids.length} 台设备？`)) return;
      await api("/api/devices/batch-delete", { method: "POST", body: JSON.stringify({ ids }) });
    } else if (key === "homeSessions") {
      if (!confirm(`批量关闭选中的 ${ids.length} 个中继会话？`)) return;
      await api("/api/sessions/batch-close", { method: "POST", body: JSON.stringify({ ids }) });
    } else if (key === "signals") {
      if (!confirm(`批量删除选中的 ${ids.length} 条信令？`)) return;
      await api("/api/signals/batch-delete", { method: "POST", body: JSON.stringify({ ids }) });
    }
    clearSelection(key);
  } catch (e) {
    alert(e.message);
  }
}

async function runClear(key) {
  try {
    if (key === "homeRooms" || key === "rooms") {
      if (!confirm("清除全部房间与设备注册记录？在线设备将被踢出。此操作不可恢复。")) return;
      await api("/api/rooms/clear", { method: "POST", body: "{}" });
      clearSelection("homeRooms");
      clearSelection("rooms");
    } else if (key.startsWith("roomDevices:")) {
      const roomId = key.slice("roomDevices:".length);
      const room = (lastSnap?.rooms || []).find((x) => x.roomId === roomId);
      const ids = (room?.devices || []).map((d) => d.id);
      if (!ids.length) return;
      if (!confirm(`清除房间「${room?.roomNo || roomId}」内全部 ${ids.length} 台设备？`)) return;
      await api("/api/devices/batch-delete", { method: "POST", body: JSON.stringify({ ids }) });
      clearSelection(key);
    } else if (key === "homeSessions") {
      if (!confirm("关闭并清除全部中继会话？")) return;
      await api("/api/sessions/clear", { method: "POST", body: "{}" });
      clearSelection(key);
    } else if (key === "signals") {
      if (!confirm("清除全部信令记录？")) return;
      await api("/api/signals/clear", { method: "POST", body: "{}" });
      clearSelection(key);
    }
  } catch (e) {
    alert(e.message);
  }
}

view.addEventListener("input", (ev) => {
  const input = ev.target.closest("[data-filter]");
  if (!input || !view.contains(input)) return;
  const key = input.dataset.filter;
  const st = tableState(key);
  st.q = input.value;
  st.page = 1;
  refreshUi();
});

view.addEventListener("change", (ev) => {
  const all = ev.target.closest("[data-check-all]");
  if (all && view.contains(all)) {
    const key = all.dataset.checkAll;
    const st = tableState(key);
    view.querySelectorAll(`[data-check="${key}"]`).forEach((box) => {
      box.checked = all.checked;
      if (all.checked) st.selected.add(box.value);
      else st.selected.delete(box.value);
    });
    refreshUi();
    return;
  }

  const one = ev.target.closest("[data-check]");
  if (one && view.contains(one)) {
    const key = one.dataset.check;
    const st = tableState(key);
    if (one.checked) st.selected.add(one.value);
    else st.selected.delete(one.value);
    refreshUi();
  }
});

view.addEventListener("click", async (ev) => {
  const pageBtn = ev.target.closest("[data-page]");
  if (pageBtn && view.contains(pageBtn) && !pageBtn.disabled) {
    ev.preventDefault();
    const key = pageBtn.dataset.page;
    const st = tableState(key);
    const to = pageBtn.dataset.to;
    if (to === "1") st.page = 1;
    else if (to === "prev") st.page = Math.max(1, st.page - 1);
    else if (to === "next") st.page += 1;
    else if (to === "last") st.page = 1e9;
    refreshUi();
    return;
  }

  const batch = ev.target.closest("[data-batch]");
  if (batch && view.contains(batch) && !batch.disabled) {
    ev.preventDefault();
    await runBatch(batch.dataset.batch);
    return;
  }

  const clear = ev.target.closest("[data-clear]");
  if (clear && view.contains(clear)) {
    ev.preventDefault();
    await runClear(clear.dataset.clear);
    return;
  }

  const delRoom = ev.target.closest("[data-delete-room]");
  if (delRoom && view.contains(delRoom)) {
    ev.preventDefault();
    ev.stopPropagation();
    const name = delRoom.dataset.roomNo || delRoom.dataset.deleteRoom;
    if (!confirm(`删除房间「${name}」？将踢出在线设备并清除该房间全部注册记录。`)) return;
    try {
      await api(`/api/rooms/${encodeURIComponent(delRoom.dataset.deleteRoom)}`, { method: "DELETE" });
      if (route().name === "room") location.hash = "#/rooms";
    } catch (e) {
      alert(e.message);
    }
    return;
  }

  const delDev = ev.target.closest("[data-delete-device]");
  if (delDev && view.contains(delDev)) {
    ev.preventDefault();
    ev.stopPropagation();
    const name = delDev.dataset.deviceName || delDev.dataset.deleteDevice;
    if (!confirm(`删除设备「${name}」？将踢出（若在线）并清除注册记录。`)) return;
    try {
      await api(`/api/devices/${encodeURIComponent(delDev.dataset.deleteDevice)}`, { method: "DELETE" });
    } catch (e) {
      alert(e.message);
    }
    return;
  }

  const kick = ev.target.closest("[data-kick]");
  if (kick && view.contains(kick)) {
    ev.preventDefault();
    ev.stopPropagation();
    if (!confirm(`踢出设备 ${kick.dataset.kick}？注册记录仍会保留。`)) return;
    try {
      await api(`/api/devices/${encodeURIComponent(kick.dataset.kick)}/kick`, { method: "POST" });
    } catch (e) {
      alert(e.message);
    }
    return;
  }

  const room = ev.target.closest("[data-room]");
  if (room && view.contains(room) && !ev.target.closest("button,input")) {
    location.hash = `#/rooms/${encodeURIComponent(room.dataset.room)}`;
    return;
  }

  const detail = ev.target.closest("[data-signal-detail]");
  if (detail && view.contains(detail)) {
    ev.preventDefault();
    await openSignalModal(detail.dataset.signalDetail, false);
    return;
  }

  const content = ev.target.closest("[data-signal-content]");
  if (content && view.contains(content) && !content.disabled) {
    ev.preventDefault();
    await openSignalModal(content.dataset.signalContent, true);
    return;
  }

  const close = ev.target.closest("[data-close]");
  if (close && view.contains(close)) {
    ev.preventDefault();
    try {
      await api(`/api/sessions/${encodeURIComponent(close.dataset.close)}/close`, { method: "POST" });
    } catch (e) {
      alert(e.message);
    }
  }
});

if (modal) {
  modal.addEventListener("click", (ev) => {
    if (ev.target === modal || ev.target.closest("[data-modal-close]")) closeModal();
  });
}
document.addEventListener("keydown", (ev) => {
  if (ev.key === "Escape") closeModal();
});

document.addEventListener("visibilitychange", syncStream);
window.addEventListener("hashchange", onRoute);
onRoute();
