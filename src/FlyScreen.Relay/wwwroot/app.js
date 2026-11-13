const view = document.getElementById("view");
const modal = document.getElementById("modal");

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

function roomSig(rooms) {
  return (rooms || []).map((r) =>
    [r.roomId, r.roomNo, r.deviceCount, r.onlineCount, r.lastSeen, r.createdAt].join("\t")
  ).join("\n");
}

function deviceSig(devices) {
  return (devices || []).map((d) =>
    [d.id, d.name, d.online ? 1 : 0, d.platform, d.remoteAddress, d.lastSeen, (d.candidates || []).length].join("\t")
  ).join("\n");
}

function sessionSig(sessions) {
  return (sessions || []).map((s) =>
    [s.sessionId, s.roomNo, s.deviceAName, s.deviceBName, s.bytesAtoB, s.bytesBtoA, s.lastActivity].join("\t")
  ).join("\n");
}

function signalSig(rows) {
  return (rows || []).map((s) =>
    [s.id, s.at, s.ok ? 1 : 0, s.fromId, s.toId, s.msgType, s.payloadBytes || 0].join("\t")
  ).join("\n");
}

function signalRows(rows) {
  return (rows || []).map((s) => `<tr data-signal="${esc(s.id)}">
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
  </tr>`).join("");
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
  return (devices || []).map((d) => `<tr data-device="${esc(d.id)}">
    <td>${esc(d.name)}<div class="subid mono">${esc(d.id)}</div></td>
    <td>${pill(d.online)}</td>
    <td>${esc(d.platform || "-")}</td>
    <td class="mono">${esc(d.remoteAddress || "-")}</td>
    <td class="mono">${deviceIps(d)}</td>
    <td>${esc(fmtTime(d.lastSeen))}</td>
    <td>${d.online ? `<button data-kick="${esc(d.id)}">踢出</button>` : ""}</td>
  </tr>`).join("");
}

function sessionRows(sessions) {
  return sessions.map((s) => `<tr data-session="${esc(s.sessionId)}">
    <td>${esc(s.roomNo)}</td>
    <td>${esc(s.deviceAName)} ↔ ${esc(s.deviceBName)}</td>
    <td>${esc(fmtBytes((s.bytesAtoB || 0) + (s.bytesBtoA || 0)))}</td>
    <td>${esc(fmtTime(s.lastActivity))}</td>
    <td><button data-close="${esc(s.sessionId)}">关闭</button></td>
  </tr>`).join("");
}

function setText(sel, text) {
  const node = view.querySelector(sel);
  if (node && node.textContent !== text) node.textContent = text;
}

function replaceBody(sel, html, emptyHtml, sig) {
  const wrap = view.querySelector(sel);
  if (!wrap) return;
  const next = html || emptyHtml;
  const key = sig ?? next;
  if (wrap._sig === key) return;
  wrap._sig = key;
  wrap.innerHTML = next;
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
    <div class="page-h">
      <h1>首页</h1>
      <p><span data-server>${esc(data.server)}</span> · 已运行 <span data-uptime-line>${esc(fmtDur(data.uptimeSec))}</span> · 房间号取该房间第一台注册电脑的名称</p>
    </div>
    <section class="kpis">
      ${kpi("a", "房间", data.registeredRooms ?? 0, "注册房间", IcoHome, "#F59E0B", "", "rooms")}
      ${kpi("e", "注册设备", data.registeredDevices ?? 0, "含离线记录", IcoNet, "#7DD3FC", "", "registered")}
      ${kpi("b", "在线设备", data.onlineDevices ?? 0, "当前在线", IcoNet, "#93C5FD", "", "online")}
      ${kpi("g", "运行时间", fmtDur(data.uptimeSec), data.server || "", IcoHome, "#FDE68A", "data-uptime", "uptime")}
      ${kpi("f", "信令次数", data.signals || 0, "累计转发", IcoShare, "#C4B5FD", "", "signals")}
      ${kpi("c", "中继会话", data.sessions ?? 0, "进行中", IcoShare, "#6EE7B7", "", "sessions")}
      ${kpi("d", "转发字节", fmtBytes(data.relayBytes), "累计转发", IcoDown, "#FDBA74", "", "bytes")}
      ${kpi("h", "延迟统计", "—", "暂无样本", IcoNet, "#94A3B8")}
    </section>
    <section class="block">
      <h2>房间</h2>
      <div class="table-wrap" data-home-rooms></div>
    </section>
    <section class="block">
      <h2>中继会话</h2>
      <div class="table-wrap" data-home-sessions></div>
    </section>`;
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

  replaceBody(
    "[data-home-rooms]",
    rooms.length
      ? `<table><thead><tr><th>房间名称</th><th>节点</th><th>连接数</th><th>最后连接</th><th>注册时间</th></tr></thead><tbody>${roomRows(rooms)}</tbody></table>`
      : "",
    `<div class="empty">还没有注册房间。设备连上边缘口并完成 hello 后，会以第一台电脑的名称建立房间。</div>`,
    roomSig(rooms)
  );
  replaceBody(
    "[data-home-sessions]",
    sessions.length
      ? `<table><thead><tr><th>房间</th><th>对端</th><th>流量</th><th>最近活动</th><th></th></tr></thead><tbody>${sessionRows(sessions)}</tbody></table>`
      : "",
    `<div class="empty">暂无中继会话。</div>`,
    sessionSig(sessions)
  );
  startUptime(data.startedAt);
}

function roomsShell() {
  return `
    <div class="page-h">
      <h1>房间</h1>
      <p>同一安全密钥的设备在同一房间。房间号是该房间第一台注册电脑的名称，之后不再改变。</p>
    </div>
    <div class="table-wrap" data-rooms-list></div>`;
}

function patchRooms(data) {
  const rooms = data.rooms || [];
  replaceBody(
    "[data-rooms-list]",
    rooms.length
      ? `<table><thead><tr><th>房间名称</th><th>节点</th><th>连接数</th><th>最后连接</th><th>注册时间</th></tr></thead><tbody>${roomRows(rooms)}</tbody></table>`
      : "",
    `<div class="empty">还没有注册房间。</div>`,
    roomSig(rooms)
  );
}

function roomShell(room) {
  return `
    <div class="crumb"><a href="#/rooms">房间</a><span>/</span><span data-room-title>${esc(room.roomNo)}</span></div>
    <div class="page-h">
      <h1 data-room-title>${esc(room.roomNo)}</h1>
      <p data-room-sub>在线 ${esc(room.onlineCount)} · 注册 ${esc(room.deviceCount)} · 建立于 ${esc(fmtTime(room.createdAt))}</p>
    </div>
    <div class="table-wrap" data-room-devices></div>`;
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
  replaceBody(
    "[data-room-devices]",
    devices.length
      ? `<table><thead><tr><th>设备</th><th>状态</th><th>平台</th><th>地址</th><th>设备 IP</th><th>最后连接</th><th></th></tr></thead><tbody>${deviceRows(devices)}</tbody></table>`
      : "",
    `<div class="empty">这个房间还没有设备记录。</div>`,
    deviceSig(devices)
  );
}

function signalsShell() {
  return `
    <div class="page-h">
      <h1>信令</h1>
      <p>边缘口转发的信令（内存环形缓冲，最多 500 条）。展示谁到谁，可查看详情与内容。</p>
    </div>
    <div class="table-wrap" data-signals-list></div>`;
}

function patchSignals(data) {
  const rows = data.signalList || [];
  replaceBody(
    "[data-signals-list]",
    rows.length
      ? `<table><thead><tr>
          <th>时间</th><th>房间</th><th>发送方</th><th>接收方</th><th>类型</th><th>结果</th><th>操作</th>
        </tr></thead><tbody>${signalRows(rows)}</tbody></table>`
      : "",
    `<div class="empty">暂无信令记录。设备互发 signal / 中继请求后会出现在这里。</div>`,
    signalSig(rows)
  );
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
        <div class="crumb"><a href="#/rooms">房间</a></div>
        <div class="page-h"><h1>房间</h1></div>
        <div class="err">房间不存在</div>`;
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
  view.innerHTML = `<div class="page-h"><h1>${title}</h1><p>加载中…</p></div>`;
}

function onRoute() {
  if (lastSnap) applySnap(lastSnap);
  else showLoading();
  syncStream();
}

view.addEventListener("click", async (ev) => {
  const room = ev.target.closest("[data-room]");
  if (room && view.contains(room)) {
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

  const kick = ev.target.closest("[data-kick]");
  if (kick && view.contains(kick)) {
    ev.preventDefault();
    if (!confirm(`踢出设备 ${kick.dataset.kick}？注册记录仍会保留。`)) return;
    try {
      await api(`/api/devices/${encodeURIComponent(kick.dataset.kick)}`, { method: "DELETE" });
    } catch (e) {
      alert(e.message);
    }
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
