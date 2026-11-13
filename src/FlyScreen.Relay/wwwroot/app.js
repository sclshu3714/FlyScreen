const view = document.getElementById("view");

function route() {
  const parts = (location.hash || "#/").replace(/^#/, "").split("/").filter(Boolean);
  if (parts[0] === "rooms" && parts[1]) return { name: "room", id: decodeURIComponent(parts[1]) };
  if (parts[0] === "rooms") return { name: "rooms" };
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
  const res = await fetch(path, opts);
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

function kpi(cls, label, value, sub, path, color, valueAttr) {
  const attr = valueAttr ? ` ${valueAttr}` : "";
  return `<article class="kpi ${cls}">${ico(path, color)}<div class="kpi-k">${esc(label)}</div><div class="kpi-v"${attr}>${esc(value)}</div><div class="kpi-s">${esc(sub)}</div></article>`;
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

function roomRows(rooms) {
  return rooms.map((r) => `<tr class="link" data-room="${esc(r.roomId)}">
    <td>${esc(r.roomNo)}</td>
    <td class="num">${esc(r.deviceCount)}</td>
    <td>${countCell(r.onlineCount, true)}</td>
    <td>${esc(fmtTime(r.lastSeen))}</td>
    <td>${esc(fmtTime(r.createdAt))}</td>
  </tr>`).join("");
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

function deviceRows(devices) {
  return (devices || []).map((d) => `<tr>
    <td>${esc(d.name)}<div class="subid mono">${esc(d.id)}</div></td>
    <td>${pill(d.online)}</td>
    <td>${esc(d.platform || "-")}</td>
    <td class="mono">${esc(d.remoteAddress || "-")}</td>
    <td class="mono">${deviceIps(d)}</td>
    <td>${esc(fmtTime(d.lastSeen))}</td>
    <td>${d.online ? `<button data-kick="${esc(d.id)}">踢出</button>` : ""}</td>
  </tr>`).join("");
}

function bindKick(root, after) {
  root.querySelectorAll("[data-kick]").forEach((btn) => {
    btn.addEventListener("click", async (ev) => {
      ev.stopPropagation();
      if (!confirm(`踢出设备 ${btn.dataset.kick}？注册记录仍会保留。`)) return;
      try {
        await api(`/api/devices/${encodeURIComponent(btn.dataset.kick)}`, { method: "DELETE" });
        await after();
      } catch (e) {
        alert(e.message);
      }
    });
  });
}

function bindClose(root, after) {
  root.querySelectorAll("[data-close]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      try {
        await api(`/api/sessions/${encodeURIComponent(btn.dataset.close)}/close`, { method: "POST" });
        await after();
      } catch (e) {
        alert(e.message);
      }
    });
  });
}

async function renderHome() {
  const data = await api("/api/dashboard");
  const rooms = data.rooms || [];
  const sessions = data.sessionList || [];
  view.innerHTML = `
    <div class="page-h">
      <h1>首页</h1>
      <p>${esc(data.server)} · 已运行 <span data-uptime-line>${esc(fmtDur(data.uptimeSec))}</span> · 房间号取该房间第一台注册电脑的名称</p>
    </div>
    <section class="kpis">
      ${kpi("a", "房间", data.registeredRooms ?? 0, "注册房间", IcoHome, "#F59E0B")}
      ${kpi("e", "注册设备", data.registeredDevices ?? 0, "含离线记录", IcoNet, "#7DD3FC")}
      ${kpi("b", "在线设备", data.onlineDevices ?? 0, "当前在线", IcoNet, "#93C5FD")}
      ${kpi("g", "运行时间", fmtDur(data.uptimeSec), data.server || "", IcoHome, "#FDE68A", "data-uptime")}
      ${kpi("f", "信令次数", data.signals || 0, "累计转发", IcoShare, "#C4B5FD")}
      ${kpi("c", "中继会话", data.sessions ?? 0, "进行中", IcoShare, "#6EE7B7")}
      ${kpi("d", "转发字节", fmtBytes(data.relayBytes), "累计转发", IcoDown, "#FDBA74")}
      ${kpi("h", "延迟统计", "—", "暂无样本", IcoNet, "#94A3B8")}
    </section>
    <section class="block">
      <h2>房间</h2>
      ${rooms.length ? `<div class="table-wrap"><table>
        <thead><tr><th>房间名称</th><th>节点</th><th>连接数</th><th>最后连接</th><th>注册时间</th></tr></thead>
        <tbody>${roomRows(rooms)}</tbody>
      </table></div>` : `<div class="table-wrap"><div class="empty">还没有注册房间。设备连上边缘口并完成 hello 后，会以第一台电脑的名称建立房间。</div></div>`}
    </section>
    <section class="block">
      <h2>中继会话</h2>
      ${sessions.length ? `<div class="table-wrap"><table>
        <thead><tr><th>房间</th><th>对端</th><th>流量</th><th>最近活动</th><th></th></tr></thead>
        <tbody>${sessions.map((s) => `<tr>
          <td>${esc(s.roomNo)}</td>
          <td>${esc(s.deviceAName)} ↔ ${esc(s.deviceBName)}</td>
          <td>${esc(fmtBytes((s.bytesAtoB || 0) + (s.bytesBtoA || 0)))}</td>
          <td>${esc(fmtTime(s.lastActivity))}</td>
          <td><button data-close="${esc(s.sessionId)}">关闭</button></td>
        </tr>`).join("")}</tbody>
      </table></div>` : `<div class="table-wrap"><div class="empty">暂无中继会话。</div></div>`}
    </section>`;

  view.querySelectorAll("[data-room]").forEach((tr) => {
    tr.addEventListener("click", () => { location.hash = `#/rooms/${encodeURIComponent(tr.dataset.room)}`; });
  });
  bindClose(view, render);
  startUptime(data.startedAt);
}

async function renderRooms() {
  const data = await api("/api/rooms");
  const rooms = data.rooms || [];
  view.innerHTML = `
    <div class="page-h">
      <h1>房间</h1>
      <p>同一安全密钥的设备在同一房间。房间号是该房间第一台注册电脑的名称，之后不再改变。</p>
    </div>
    ${rooms.length ? `<div class="table-wrap"><table>
      <thead><tr><th>房间名称</th><th>节点</th><th>连接数</th><th>最后连接</th><th>注册时间</th></tr></thead>
      <tbody>${roomRows(rooms)}</tbody>
    </table></div>` : `<div class="table-wrap"><div class="empty">还没有注册房间。</div></div>`}`;

  view.querySelectorAll("[data-room]").forEach((tr) => {
    tr.addEventListener("click", () => { location.hash = `#/rooms/${encodeURIComponent(tr.dataset.room)}`; });
  });
}

async function renderRoom(id) {
  let room;
  try {
    room = await api(`/api/rooms/${encodeURIComponent(id)}`);
  } catch (e) {
    view.innerHTML = `
      <div class="crumb"><a href="#/rooms">房间</a></div>
      <div class="page-h"><h1>房间</h1></div>
      <div class="err">${esc(e.message)}</div>`;
    return;
  }
  const devices = room.devices || [];
  view.innerHTML = `
    <div class="crumb"><a href="#/rooms">房间</a><span>/</span><span>${esc(room.roomNo)}</span></div>
    <div class="page-h">
      <h1>${esc(room.roomNo)}</h1>
      <p>在线 ${esc(room.onlineCount)} · 注册 ${esc(room.deviceCount)} · 建立于 ${esc(fmtTime(room.createdAt))}</p>
    </div>
    ${devices.length ? `<div class="table-wrap"><table>
      <thead><tr><th>设备</th><th>状态</th><th>平台</th><th>地址</th><th>设备 IP</th><th>最后连接</th><th></th></tr></thead>
      <tbody>${deviceRows(devices)}</tbody>
    </table></div>` : `<div class="table-wrap"><div class="empty">这个房间还没有设备记录。</div></div>`}`;
  bindKick(view, () => renderRoom(id));
}

async function render() {
  stopUptime();
  const r = route();
  setNav(r.name);
  if (r.name === "rooms") await renderRooms();
  else if (r.name === "room") await renderRoom(r.id);
  else await renderHome();
}

let rendering = false;
async function tick() {
  if (rendering) return;
  rendering = true;
  try { await render(); }
  catch (e) {
    view.innerHTML = `<div class="page-h"><h1>首页</h1></div><div class="err">${esc(e.message)}</div>`;
  } finally {
    rendering = false;
  }
}

window.addEventListener("hashchange", tick);
tick();
