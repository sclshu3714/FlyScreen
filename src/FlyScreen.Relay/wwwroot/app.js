const view = document.getElementById("view");
const modal = document.getElementById("modal");
const toastHost = document.getElementById("toast");
const PAGE_SIZE = 20;

const LANGS = Array.isArray(window.FLY_LANGS) ? window.FLY_LANGS : [
  { id: "zh", native: "简体中文", locale: "zh-CN" },
  { id: "en", native: "English", locale: "en-US" },
];
const I18N = window.FLY_I18N || {};

function langMeta(id) {
  return LANGS.find((x) => x.id === id) || LANGS[0];
}

let lang = "zh";
try {
  const saved = localStorage.getItem("flyrelay.lang");
  if (saved && (I18N[saved] || LANGS.some((x) => x.id === saved))) lang = saved;
} catch { /* ignore */ }

function t(key, vars) {
  const dict = I18N[lang] || I18N.zh || I18N.en || {};
  let s = dict[key] ?? I18N.en?.[key] ?? I18N.zh?.[key] ?? key;
  if (vars) {
    for (const [k, v] of Object.entries(vars))
      s = s.replaceAll(`{${k}}`, String(v));
  }
  return s;
}

function currentTheme() {
  return document.documentElement.getAttribute("data-theme") === "light" ? "light" : "dark";
}

function applyChromeLabels() {
  const meta = langMeta(lang);
  const langBtn = document.getElementById("btn-lang-label");
  const themeBtn = document.getElementById("btn-theme-label");
  const themeIco = document.getElementById("btn-theme-ico");
  const drop = document.getElementById("lang-dropdown");
  if (langBtn) langBtn.textContent = meta.native;
  if (themeBtn) themeBtn.textContent = currentTheme() === "light" ? t("theme.light") : t("theme.dark");
  if (themeIco) {
    themeIco.innerHTML = currentTheme() === "light"
      ? `<path fill="currentColor" d="M12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9M12,2L14.39,5.42C13.65,5.15 12.84,5 12,5C11.16,5 10.35,5.15 9.61,5.42L12,2M3.34,7L7.5,6.65C6.9,7.16 6.36,7.78 5.94,8.5C5.5,9.24 5.25,10 5.11,10.79L3.34,7M3.36,17L5.12,13.23C5.26,14 5.53,14.78 5.95,15.5C6.37,16.24 6.91,16.86 7.5,17.37L3.36,17M20.65,7L18.88,10.79C18.74,10 18.47,9.23 18.05,8.5C17.63,7.78 17.1,7.15 16.5,6.64L20.65,7M20.64,17L16.5,17.36C17.09,16.85 17.62,16.22 18.04,15.5C18.46,14.77 18.73,14 18.87,13.21L20.64,17M12,22L9.59,18.56C10.33,18.83 11.14,19 12,19C12.82,19 13.63,18.83 14.37,18.56L12,22Z"/>`
      : `<path fill="currentColor" d="M12,18C11.11,18 10.26,17.8 9.5,17.45C11.56,16.5 13,14.42 13,12C13,9.58 11.56,7.5 9.5,6.55C10.26,6.2 11.11,6 12,6A6,6 0 0,1 18,12A6,6 0 0,1 12,18M20,8.69V4H15.31L12,0.69L8.69,4H4V8.69L0.69,12L4,15.31V20H8.69L12,23.31L15.31,20H20V15.31L23.31,12L20,8.69Z"/>`;
  }
  if (drop) {
    drop.innerHTML = LANGS.map((x) => {
      const id = String(x.id).replace(/"/g, "");
      const name = String(x.native).replace(/[<>&]/g, "");
      return `<button type="button" role="option" data-set-lang="${id}" class="${x.id === lang ? "active" : ""}">${name}<span class="lang-code">${id}</span></button>`;
    }).join("");
  }
  document.querySelectorAll("[data-i18n]").forEach((el) => {
    const key = el.getAttribute("data-i18n");
    if (key) el.textContent = t(key);
  });
  document.documentElement.lang = meta.locale || lang;
  document.documentElement.dir = meta.rtl ? "rtl" : "ltr";
  document.title = lang.startsWith("zh") ? "玄鸟 · 管理" : `${t("brand.name")} · Admin`;
}

function setLangDropdown(open) {
  const drop = document.getElementById("lang-dropdown");
  const btn = document.getElementById("btn-lang");
  if (!drop || !btn) return;
  drop.hidden = !open;
  btn.setAttribute("aria-expanded", open ? "true" : "false");
}

function setLang(next) {
  if (!LANGS.some((x) => x.id === next)) return;
  lang = next;
  try { localStorage.setItem("flyrelay.lang", lang); } catch { /* ignore */ }
  setLangDropdown(false);
  applyChromeLabels();
  mountedKey = "";
  if (lastSnap) applySnap(lastSnap);
  else showLoading();
}

function setTheme(next) {
  const theme = next === "light" ? "light" : "dark";
  document.documentElement.setAttribute("data-theme", theme);
  try { localStorage.setItem("flyrelay.theme", theme); } catch { /* ignore */ }
  applyChromeLabels();
}

function noticeIcon(kind) {
  const color = kind === "error" ? "var(--notice-error)" : kind === "warn" ? "var(--notice-warn)" : "var(--notice-info)";
  return `<span class="notice-ico" aria-hidden="true"><svg viewBox="0 0 24 24"><path fill="${color}" d="M13,9H11V7H13M13,17H11V11H13M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z"/></svg></span>`;
}

/** 附件1样式：说明 / 消息条。textHtml 可为已转义或含安全 DOM 的片段。 */
function noticeHtml(kind, lead, textHtml) {
  return `<div class="notice notice-${esc(kind)}">${noticeIcon(kind)}<p class="notice-body"><span class="notice-lead">${esc(lead)}</span><span class="notice-text">${textHtml}</span></p></div>`;
}

function emptyNotice(text) {
  return `<div class="empty">${noticeHtml("info", t("lead.empty"), esc(text))}</div>`;
}

function showToast(message, kind = "error") {
  if (!toastHost) return;
  const lead = kind === "error" ? t("lead.error") : t("lead.note");
  toastHost.innerHTML = noticeHtml(kind, lead + ":", esc(message));
  toastHost.hidden = false;
  clearTimeout(showToast._timer);
  showToast._timer = setTimeout(() => {
    toastHost.hidden = true;
    toastHost.innerHTML = "";
  }, 5200);
}

document.getElementById("btn-lang")?.addEventListener("click", (ev) => {
  ev.stopPropagation();
  const drop = document.getElementById("lang-dropdown");
  setLangDropdown(!!drop?.hidden);
});
document.getElementById("lang-dropdown")?.addEventListener("click", (ev) => {
  const btn = ev.target.closest("[data-set-lang]");
  if (!btn) return;
  ev.preventDefault();
  setLang(btn.dataset.setLang);
});
document.getElementById("btn-theme")?.addEventListener("click", () => setTheme(currentTheme() === "dark" ? "light" : "dark"));
document.addEventListener("click", () => setLangDropdown(false));
document.addEventListener("keydown", (ev) => {
  if (ev.key === "Escape") setLangDropdown(false);
});
applyChromeLabels();

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
  if (h > 0) return t("dur.h", { h, m });
  if (m > 0) return t("dur.m", { m, s });
  return t("dur.s", { s });
}

function fmtTime(s) {
  if (!s) return "-";
  const d = new Date(s);
  const locale = langMeta(lang).locale || "zh-CN";
  return Number.isNaN(d.getTime()) ? String(s) : d.toLocaleString(locale, { hour12: false });
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
  return `<span class="pill ${online ? "on" : "off"}">${online ? t("status.online") : t("status.offline")}</span>`;
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
  const batchLabel = opts.batchLabel || t("batch.delete");
  const clearLabel = opts.clearLabel || t("clear.data");
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
  let summary = t("pager.summary", { total, size: PAGE_SIZE, page, pages });
  if (total) summary += t("pager.range", { from: start + 1, to: end });
  return `<div class="pager" data-pager="${esc(key)}">
    <span>${esc(summary)}</span>
    <div class="pager-btns">
      <button type="button" class="ghost" data-page="${esc(key)}" data-to="1" ${page <= 1 ? "disabled" : ""}>${esc(t("pager.first"))}</button>
      <button type="button" class="ghost" data-page="${esc(key)}" data-to="prev" ${page <= 1 ? "disabled" : ""}>${esc(t("pager.prev"))}</button>
      <button type="button" class="ghost" data-page="${esc(key)}" data-to="next" ${page >= pages ? "disabled" : ""}>${esc(t("pager.next"))}</button>
      <button type="button" class="ghost" data-page="${esc(key)}" data-to="last" ${page >= pages ? "disabled" : ""}>${esc(t("pager.last"))}</button>
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
    lang,
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
  return matchText(q, d.name, d.id, d.platform, d.remoteAddress, ips, d.online ? t("status.online") : t("status.offline"));
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
    s.ok ? t("status.ok") : s.error || t("status.fail"),
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
    <td class="ops"><button type="button" data-delete-room="${esc(r.roomId)}" data-room-no="${esc(r.roomNo)}">${esc(t("act.delete"))}</button></td>
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
      ${d.online ? `<button type="button" data-kick="${esc(d.id)}">${esc(t("act.kick"))}</button>` : ""}
      <button type="button" data-delete-device="${esc(d.id)}" data-device-name="${esc(d.name)}">${esc(t("act.delete"))}</button>
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
    <td class="ops"><button type="button" data-close="${esc(s.sessionId)}">${esc(t("act.close"))}</button></td>
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
    <td>${s.ok ? `<span class="pill ok">${esc(t("status.ok"))}</span>` : `<span class="pill bad">${esc(s.error || t("status.fail"))}</span>`}</td>
    <td class="actions">
      <button type="button" class="ghost" data-signal-detail="${esc(s.id)}">${esc(t("act.detail"))}</button>
      <button type="button" class="ghost" data-signal-content="${esc(s.id)}" ${s.hasPayload ? "" : "disabled"}>${esc(t("act.content"))}</button>
    </td>
  </tr>`;
}

function setText(sel, text) {
  const node = view.querySelector(sel);
  if (node && node.textContent !== text) node.textContent = text;
}

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
  return `${lang}:${r.name === "room" ? `room:${r.id}` : r.name}`;
}

function homeShell(data) {
  const lead = data.server || "flyscreen-relay";
  const text = `${esc(t("home.uptime"))} <span data-uptime-line>${esc(fmtDur(data.uptimeSec))}</span> · ${esc(t("home.hint"))}`;
  return `
    <div class="page page-home">
      <div class="page-h">
        <h1>${esc(t("home.title"))}</h1>
        ${noticeHtml("info", lead, text)}
      </div>
      <section class="kpis">
        ${kpi("a", t("home.kpi.rooms"), data.registeredRooms ?? 0, t("home.kpi.roomsSub"), IcoHome, "#F59E0B", "", "rooms")}
        ${kpi("e", t("home.kpi.registered"), data.registeredDevices ?? 0, t("home.kpi.registeredSub"), IcoNet, "#7DD3FC", "", "registered")}
        ${kpi("b", t("home.kpi.online"), data.onlineDevices ?? 0, t("home.kpi.onlineSub"), IcoNet, "#93C5FD", "", "online")}
        ${kpi("g", t("home.kpi.uptime"), fmtDur(data.uptimeSec), data.server || "", IcoHome, "#FDE68A", "data-uptime", "uptime")}
        ${kpi("f", t("home.kpi.signals"), data.signals || 0, t("home.kpi.signalsSub"), IcoShare, "#C4B5FD", "", "signals")}
        ${kpi("c", t("home.kpi.sessions"), data.sessions ?? 0, t("home.kpi.sessionsSub"), IcoShare, "#6EE7B7", "", "sessions")}
        ${kpi("d", t("home.kpi.bytes"), fmtBytes(data.relayBytes), t("home.kpi.bytesSub"), IcoDown, "#FDBA74", "", "bytes")}
        ${kpi("h", t("home.kpi.latency"), t("home.kpi.latencyValue"), t("home.kpi.latencySub"), IcoNet, "#94A3B8")}
      </section>
      <div class="page-blocks">
        <section class="block">
          <h2>${esc(t("home.block.rooms"))}</h2>
          <div class="panel" data-home-rooms></div>
        </section>
        <section class="block">
          <h2>${esc(t("home.block.sessions"))}</h2>
          <div class="panel" data-home-sessions></div>
        </section>
      </div>
    </div>`;
}

function patchHome(data) {
  const rooms = data.rooms || [];
  const sessions = data.sessionList || [];
  const lead = view.querySelector(".page-home .notice-lead");
  if (lead) lead.textContent = data.server || "flyscreen-relay";
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
    headHtml: `<th>${esc(t("col.roomName"))}</th><th>${esc(t("col.nodes"))}</th><th>${esc(t("col.connections"))}</th><th>${esc(t("col.lastSeen"))}</th><th>${esc(t("col.registeredAt"))}</th><th>${esc(t("col.ops"))}</th>`,
    rowHtml: roomRow,
    emptyHtml: emptyNotice(t("empty.rooms.home")),
    placeholder: t("filter.rooms"),
    clearLabel: t("clear.rooms"),
  });

  const stSessions = tableState("homeSessions");
  renderPanel("[data-home-sessions]", {
    key: "homeSessions",
    items: sessions,
    idOf: (s) => s.sessionId,
    filter: (s) => sessionFilter(s, stSessions.q),
    headHtml: `<th>${esc(t("col.room"))}</th><th>${esc(t("col.peers"))}</th><th>${esc(t("col.traffic"))}</th><th>${esc(t("col.activity"))}</th><th>${esc(t("col.ops"))}</th>`,
    rowHtml: sessionRow,
    emptyHtml: emptyNotice(t("empty.sessions")),
    placeholder: t("filter.sessions"),
    batchLabel: t("batch.close"),
    clearLabel: t("clear.sessions"),
  });
  startUptime(data.startedAt);
}

function roomsShell() {
  return `
    <div class="page">
      <div class="page-h">
        <h1>${esc(t("rooms.title"))}</h1>
        ${noticeHtml("info", t("lead.note"), esc(t("rooms.hint")))}
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
    headHtml: `<th>${esc(t("col.roomName"))}</th><th>${esc(t("col.nodes"))}</th><th>${esc(t("col.connections"))}</th><th>${esc(t("col.lastSeen"))}</th><th>${esc(t("col.registeredAt"))}</th><th>${esc(t("col.ops"))}</th>`,
    rowHtml: roomRow,
    emptyHtml: emptyNotice(t("empty.rooms")),
    placeholder: t("filter.rooms"),
    clearLabel: t("clear.rooms"),
  });
}

function roomShell(room) {
  return `
    <div class="page">
      <div class="crumb"><a href="#/rooms">${esc(t("nav.rooms"))}</a><span>/</span><span data-room-title>${esc(room.roomNo)}</span></div>
      <div class="page-h">
        <h1 data-room-title>${esc(room.roomNo)}</h1>
        ${noticeHtml("info", t("lead.note"), esc(t("room.sub", {
          online: room.onlineCount,
          registered: room.deviceCount,
          created: fmtTime(room.createdAt),
        })))}
      </div>
      <div class="panel" data-room-devices></div>
    </div>`;
}

function patchRoom(room) {
  view.querySelectorAll("[data-room-title]").forEach((n) => {
    if (n.textContent !== room.roomNo) n.textContent = room.roomNo;
  });
  const text = view.querySelector(".page-h .notice-text");
  if (text) {
    text.textContent = t("room.sub", {
      online: room.onlineCount,
      registered: room.deviceCount,
      created: fmtTime(room.createdAt),
    });
  }
  const devices = room.devices || [];
  const key = `roomDevices:${room.roomId}`;
  const st = tableState(key);
  renderPanel("[data-room-devices]", {
    key,
    items: devices,
    idOf: (d) => d.id,
    filter: (d) => deviceFilter(d, st.q),
    headHtml: `<th>${esc(t("col.device"))}</th><th>${esc(t("col.status"))}</th><th>${esc(t("col.platform"))}</th><th>${esc(t("col.address"))}</th><th>${esc(t("col.deviceIp"))}</th><th>${esc(t("col.lastSeen"))}</th><th>${esc(t("col.ops"))}</th>`,
    rowHtml: deviceRow,
    emptyHtml: emptyNotice(t("empty.devices")),
    placeholder: t("filter.devices"),
    clearLabel: t("clear.devices"),
  });
}

function signalsShell() {
  return `
    <div class="page">
      <div class="page-h">
        <h1>${esc(t("signals.title"))}</h1>
        ${noticeHtml("info", t("lead.note"), esc(t("signals.hint")))}
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
    headHtml: `<th>${esc(t("col.time"))}</th><th>${esc(t("col.room"))}</th><th>${esc(t("col.from"))}</th><th>${esc(t("col.to"))}</th><th>${esc(t("col.type"))}</th><th>${esc(t("col.result"))}</th><th>${esc(t("col.ops"))}</th>`,
    rowHtml: signalRow,
    emptyHtml: emptyNotice(t("empty.signals")),
    placeholder: t("filter.signals"),
    clearLabel: t("clear.signals"),
  });
}

function prettyJson(raw) {
  if (!raw) return t("signal.noPayload");
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
  modal.innerHTML = `<div class="modal-card"><div class="modal-h"><div><h2>${esc(t("signal.title"))}</h2>${noticeHtml("info", t("lead.loading"), esc(t("signal.loading")))}</div>
    <button type="button" class="ghost" data-modal-close>${esc(t("act.closeModal"))}</button></div></div>`;
  try {
    const row = await api(`/api/signals/${encodeURIComponent(id)}`);
    const title = focusContent ? t("signal.content") : t("signal.detail");
    modal.innerHTML = `
      <div class="modal-card">
        <div class="modal-h">
          <div>
            <h2>${esc(title)}</h2>
            ${noticeHtml("info", t("lead.note"), `${esc(row.msgType || "signal")} · ${esc(fmtTime(row.at))}`)}
          </div>
          <button type="button" class="ghost" data-modal-close>${esc(t("act.closeModal"))}</button>
        </div>
        <div class="modal-b">
          <dl class="kv">
            <dt>${esc(t("kv.result"))}</dt><dd>${row.ok ? `<span class="pill ok">${esc(t("status.ok"))}</span>` : `<span class="pill bad">${esc(row.error || t("status.fail"))}</span>`}</dd>
            <dt>${esc(t("kv.room"))}</dt><dd>${esc(row.roomNo || "—")}<div class="subid mono">${esc(row.roomId || "")}</div></dd>
            <dt>${esc(t("kv.from"))}</dt><dd>${esc(row.fromName || row.fromId)}<div class="subid mono">${esc(row.fromId || "")}</div></dd>
            <dt>${esc(t("kv.to"))}</dt><dd>${esc(row.toName || row.toId || "—")}<div class="subid mono">${esc(row.toId || "")}</div></dd>
            <dt>${esc(t("kv.type"))}</dt><dd>${esc(row.msgType || "—")}</dd>
            <dt>${esc(t("kv.session"))}</dt><dd class="mono">${esc(row.sessionId || "—")}</dd>
            <dt>${esc(t("kv.size"))}</dt><dd>${esc(row.payloadBytes || 0)} B</dd>
            <dt>${esc(t("kv.id"))}</dt><dd class="mono">${esc(row.id)}</dd>
          </dl>
          <h3 style="margin:0 0 0.45rem;font-size:0.92rem;">${esc(t("signal.payload"))}</h3>
          <pre class="payload">${esc(prettyJson(row.payloadJson))}</pre>
        </div>
      </div>`;
    if (focusContent) {
      const pre = modal.querySelector(".payload");
      if (pre) pre.scrollIntoView({ block: "nearest" });
    }
  } catch (e) {
    modal.innerHTML = `<div class="modal-card"><div class="modal-h"><div><h2>${esc(t("signal.title"))}</h2>
      ${noticeHtml("error", t("lead.error") + ":", esc(e.message))}</div>
      <button type="button" class="ghost" data-modal-close>${esc(t("act.closeModal"))}</button></div></div>`;
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
          <div class="crumb"><a href="#/rooms">${esc(t("nav.rooms"))}</a></div>
          <div class="page-h"><h1>${esc(t("rooms.title"))}</h1>
            ${noticeHtml("error", t("lead.missing") + ":", esc(t("msg.roomMissing")))}
          </div>
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
  const title = r.name === "signals" ? t("signals.title") : (r.name === "rooms" || r.name === "room" ? t("rooms.title") : t("home.title"));
  view.innerHTML = `<div class="page"><div class="page-h"><h1>${esc(title)}</h1>${noticeHtml("info", t("lead.loading"), esc(t("msg.loading")))}</div></div>`;
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
      if (!confirm(t("confirm.batchRooms", { n: ids.length }))) return;
      await api("/api/rooms/batch-delete", { method: "POST", body: JSON.stringify({ ids }) });
    } else if (key.startsWith("roomDevices:")) {
      if (!confirm(t("confirm.batchDevices", { n: ids.length }))) return;
      await api("/api/devices/batch-delete", { method: "POST", body: JSON.stringify({ ids }) });
    } else if (key === "homeSessions") {
      if (!confirm(t("confirm.batchSessions", { n: ids.length }))) return;
      await api("/api/sessions/batch-close", { method: "POST", body: JSON.stringify({ ids }) });
    } else if (key === "signals") {
      if (!confirm(t("confirm.batchSignals", { n: ids.length }))) return;
      await api("/api/signals/batch-delete", { method: "POST", body: JSON.stringify({ ids }) });
    }
    clearSelection(key);
  } catch (e) {
    showToast(e.message);
  }
}

async function runClear(key) {
  try {
    if (key === "homeRooms" || key === "rooms") {
      if (!confirm(t("confirm.clearRooms"))) return;
      await api("/api/rooms/clear", { method: "POST", body: "{}" });
      clearSelection("homeRooms");
      clearSelection("rooms");
    } else if (key.startsWith("roomDevices:")) {
      const roomId = key.slice("roomDevices:".length);
      const room = (lastSnap?.rooms || []).find((x) => x.roomId === roomId);
      const ids = (room?.devices || []).map((d) => d.id);
      if (!ids.length) return;
      if (!confirm(t("confirm.clearRoomDevices", { name: room?.roomNo || roomId, n: ids.length }))) return;
      await api("/api/devices/batch-delete", { method: "POST", body: JSON.stringify({ ids }) });
      clearSelection(key);
    } else if (key === "homeSessions") {
      if (!confirm(t("confirm.clearSessions"))) return;
      await api("/api/sessions/clear", { method: "POST", body: "{}" });
      clearSelection(key);
    } else if (key === "signals") {
      if (!confirm(t("confirm.clearSignals"))) return;
      await api("/api/signals/clear", { method: "POST", body: "{}" });
      clearSelection(key);
    }
  } catch (e) {
    showToast(e.message);
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
    if (!confirm(t("confirm.deleteRoom", { name }))) return;
    try {
      await api(`/api/rooms/${encodeURIComponent(delRoom.dataset.deleteRoom)}`, { method: "DELETE" });
      if (route().name === "room") location.hash = "#/rooms";
    } catch (e) {
      showToast(e.message);
    }
    return;
  }

  const delDev = ev.target.closest("[data-delete-device]");
  if (delDev && view.contains(delDev)) {
    ev.preventDefault();
    ev.stopPropagation();
    const name = delDev.dataset.deviceName || delDev.dataset.deleteDevice;
    if (!confirm(t("confirm.deleteDevice", { name }))) return;
    try {
      await api(`/api/devices/${encodeURIComponent(delDev.dataset.deleteDevice)}`, { method: "DELETE" });
    } catch (e) {
      showToast(e.message);
    }
    return;
  }

  const kick = ev.target.closest("[data-kick]");
  if (kick && view.contains(kick)) {
    ev.preventDefault();
    ev.stopPropagation();
    if (!confirm(t("confirm.kick", { id: kick.dataset.kick }))) return;
    try {
      await api(`/api/devices/${encodeURIComponent(kick.dataset.kick)}/kick`, { method: "POST" });
    } catch (e) {
      showToast(e.message);
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
      showToast(e.message);
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
