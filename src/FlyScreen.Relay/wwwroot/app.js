const $ = (id) => document.getElementById(id);

function token() {
  const t = ($("token").value || "").trim();
  if (t) localStorage.setItem("flyscreen.relay.adminToken", t);
  return localStorage.getItem("flyscreen.relay.adminToken") || "";
}

function headers(write) {
  const h = { Accept: "application/json" };
  const t = token();
  if (t) h["X-Admin-Token"] = t;
  return h;
}

async function api(path, opts = {}) {
  const res = await fetch(path, { ...opts, headers: { ...headers(), ...(opts.headers || {}) } });
  if (!res.ok) {
    const body = await res.text();
    throw new Error(`${res.status} ${body}`);
  }
  return res.json();
}

function fmtBytes(n) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

async function refresh() {
  try {
    const health = await api("/api/health");
    $("serverMeta").textContent =
      `${health.server} · 设备 ${health.devices} · 房间 ${health.rooms} · 会话 ${health.sessions} · 运行 ${health.uptimeSec}s`;

    $("stats").innerHTML = [
      ["在线设备", health.devices],
      ["房间", health.rooms],
      ["中继会话", health.sessions],
      ["转发字节", fmtBytes(health.relayBytes || 0)],
      ["信令次数", health.signals || 0],
    ].map(([k, v]) => `<div class="card"><div class="k">${k}</div><div class="v">${esc(v)}</div></div>`).join("");

    const rooms = await api("/api/rooms");
    const rows = [];
    for (const room of rooms.rooms || []) {
      for (const d of room.devices || []) {
        rows.push(`<tr>
          <td>${esc(d.name)}<div class="mono">${esc(d.id)}</div></td>
          <td class="mono">${esc(room.roomId)}</td>
          <td><span class="pill ${d.online ? "on" : "off"}">${d.online ? "在线" : "离线"}</span></td>
          <td>${esc(d.platform || "-")}</td>
          <td class="mono">${esc(d.remoteAddress || "-")}</td>
          <td class="mono">${esc((d.candidates || []).map(c => c.address).join(", ") || "-")}</td>
          <td><button class="danger" data-kick="${esc(d.id)}">踢出</button></td>
        </tr>`);
      }
    }
    $("devices").innerHTML = rows.length
      ? `<table><thead><tr><th>设备</th><th>房间</th><th>状态</th><th>平台</th><th>远端</th><th>候选</th><th></th></tr></thead><tbody>${rows.join("")}</tbody></table>`
      : `<div class="empty">暂无在线设备。客户端连边缘口 /ws 并发送 hello 后会出现在这里。</div>`;

    $("devices").querySelectorAll("[data-kick]").forEach((btn) => {
      btn.addEventListener("click", async () => {
        if (!confirm(`踢出设备 ${btn.dataset.kick}？`)) return;
        try {
          await api(`/api/devices/${encodeURIComponent(btn.dataset.kick)}`, { method: "DELETE" });
          await refresh();
        } catch (e) {
          alert(e.message);
        }
      });
    });

    const sessions = await api("/api/sessions");
    const srows = (sessions.sessions || []).map((s) => `<tr>
      <td class="mono">${esc(s.sessionId)}</td>
      <td class="mono">${esc(s.deviceA)} ↔ ${esc(s.deviceB)}</td>
      <td class="mono">${esc(s.roomId)}</td>
      <td>${fmtBytes((s.bytesAtoB || 0) + (s.bytesBtoA || 0))}</td>
      <td>${esc(s.lastActivity)}</td>
      <td><button class="danger" data-close="${esc(s.sessionId)}">关闭</button></td>
    </tr>`);
    $("sessions").innerHTML = srows.length
      ? `<table><thead><tr><th>Session</th><th>对端</th><th>房间</th><th>流量</th><th>最近活动</th><th></th></tr></thead><tbody>${srows.join("")}</tbody></table>`
      : `<div class="empty">暂无中继会话。</div>`;

    $("sessions").querySelectorAll("[data-close]").forEach((btn) => {
      btn.addEventListener("click", async () => {
        try {
          await api(`/api/sessions/${encodeURIComponent(btn.dataset.close)}/close`, { method: "POST" });
          await refresh();
        } catch (e) {
          alert(e.message);
        }
      });
    });
  } catch (e) {
    $("serverMeta").textContent = "无法加载：" + e.message;
  }
}

$("token").value = localStorage.getItem("flyscreen.relay.adminToken") || "";
$("btnRefresh").addEventListener("click", refresh);
refresh();
setInterval(refresh, 5000);
