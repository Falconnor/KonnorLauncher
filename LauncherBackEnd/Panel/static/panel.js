// Panel de control del backend: consulta el estado cada 2 s y el registro cada segundo.
"use strict";

const $ = (id) => document.getElementById(id);
const STATE_INTERVAL = 2000;
const LOG_INTERVAL = 1000;
const MAX_LOG_LINES = 1500;

// ── Utilidades ───────────────────────────────────────────────────────────

/** Petición a la API del panel. Las acciones llevan la cabecera que exige el panel. */
async function api(path, { method = "GET", body } = {}) {
  const options = { method, headers: {} };
  if (method !== "GET") options.headers["X-Konnor-Panel"] = "1";
  if (body !== undefined) {
    options.headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(body);
  }
  const response = await fetch(path, options);
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.detail || `Error ${response.status}`);
  return data;
}

const number = new Intl.NumberFormat("es-ES");
const clock = new Intl.DateTimeFormat("es-ES", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
const dateTime = new Intl.DateTimeFormat("es-ES", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });

function bytes(value) {
  if (!value) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const i = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  return `${(value / 1024 ** i).toLocaleString("es-ES", { maximumFractionDigits: i ? 2 : 0 })} ${units[i]}`;
}

function duration(seconds) {
  seconds = Math.max(0, Math.floor(seconds));
  const d = Math.floor(seconds / 86400), h = Math.floor(seconds % 86400 / 3600);
  const m = Math.floor(seconds % 3600 / 60), s = seconds % 60;
  if (d) return `${d} d ${h} h`;
  if (h) return `${h} h ${m} min`;
  if (m) return `${m} min ${s} s`;
  return `${s} s`;
}

function ago(timestamp, now) {
  const seconds = now - timestamp;
  if (seconds < 10) return "ahora mismo";
  return `hace ${duration(seconds)}`;
}

let toastTimer;
function toast(message, tone = "ok") {
  const el = $("toast");
  el.textContent = message;
  el.dataset.tone = tone;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (el.hidden = true), tone === "error" ? 6000 : 3000);
}

/** Ejecuta una acción con el botón bloqueado mientras dura. */
async function withButton(button, action) {
  button.disabled = true;
  try {
    return await action();
  } catch (error) {
    toast(error.message, "error");
  } finally {
    button.disabled = false;
    refreshState();
  }
}

// ── Estado ───────────────────────────────────────────────────────────────

const STATUS = {
  running: { chip: "En línea", big: "En línea", power: "Detener servidor" },
  starting: { chip: "Iniciando…", big: "Iniciando", power: "Iniciando…" },
  stopping: { chip: "Deteniendo…", big: "Deteniendo", power: "Deteniendo…" },
  stopped: { chip: "Detenido", big: "Detenido", power: "Iniciar servidor" },
  crashed: { chip: "Se cerró con error", big: "Caído", power: "Iniciar servidor" },
  external: { chip: "En línea fuera del panel", big: "En línea", power: "Iniciar servidor" },
  offline: { chip: "El panel no responde", big: "Sin conexión", power: "Iniciar servidor" },
};

let current = null;

async function refreshState() {
  try {
    current = await api("/api/state");
    renderState(current);
  } catch {
    renderOffline();
  }
}

function setStatus(status) {
  for (const el of [document.body, $("statusChip")]) el.dataset.status = status;
}

function renderOffline() {
  setStatus("offline");
  $("statusText").textContent = STATUS.offline.chip;
  $("bigState").textContent = STATUS.offline.big;
  $("stateDetail").textContent = "Comprueba que la ventana de iniciar_panel.bat sigue abierta.";
  $("btnPower").disabled = true;
  $("btnRestart").disabled = true;
}

function renderState(s) {
  const server = s.server;
  const status = server.status;
  const labels = STATUS[status];
  setStatus(status);

  // Barra superior y botón de encendido.
  $("statusText").textContent = labels.chip;
  const power = $("btnPower");
  power.textContent = labels.power;
  power.classList.toggle("btn-primary", status !== "running");
  power.classList.toggle("btn-danger", status === "running");
  power.disabled = ["starting", "stopping", "external"].includes(status) || s.local_generation.running;
  power.dataset.action = status === "running" ? "stop" : "start";
  $("btnRestart").disabled = status !== "running";
  $("restartBanner").hidden = !server.restart_required;

  // Servidor.
  $("bigState").textContent = labels.big;
  $("stateDetail").textContent = stateDetail(s);
  const uptime = server.started_at ? `Activo desde hace ${duration(s.now - server.started_at)}` : "";
  $("serverUptime").textContent = uptime;
  const lanUrl = server.lan_urls[0] || server.local_url;
  $("launcherLine").textContent = `server.url=${lanUrl}`;
  renderUrls(server);
  $("factHost").textContent = server.host === "0.0.0.0" ? "Todas las interfaces" : server.host;
  $("factPort").textContent = server.port;
  $("factPid").textContent = server.pid ?? "—";
  $("factStarted").textContent = server.started_at ? clock.format(server.started_at * 1000) : "—";

  // Actividad.
  const a = s.activity;
  $("actVerifications").textContent = number.format(a.verifications);
  $("actDownloads").textContent = number.format(a.downloads);
  $("actClients").textContent = number.format(a.clients);
  $("actErrors").textContent = number.format(a.errors);
  $("actErrors").classList.toggle("has-errors", a.errors > 0);
  $("actLast").textContent = a.last_request
    ? `Última petición de un launcher ${ago(a.last_request, s.now)}.`
    : "Sin peticiones de launchers desde el arranque.";

  renderManifest(s);
}

function stateDetail(s) {
  const server = s.server;
  switch (server.status) {
    case "running":
      return s.client.exists
        ? "Publicando el manifest y los archivos del cliente a los launchers."
        : "En marcha, pero la carpeta del cliente no existe: los launchers recibirán errores 404.";
    case "starting": return "Arrancando el servidor y revisando la carpeta del cliente.";
    case "stopping": return "Cerrando las conexiones y el vigilante de la carpeta.";
    case "crashed": return `El proceso terminó con el código ${server.exit_code}. El registro muestra el motivo.`;
    case "external": return "Lo arrancó otro proceso (por ejemplo iniciar_servidor.bat). Ciérralo desde su ventana para controlarlo aquí.";
    default: return "Los launchers con VERIFICAR ARCHIVOS activado mostrarán “servidor no responde”.";
  }
}

function renderUrls(server) {
  const list = $("urlList");
  const urls = [{ label: "Este equipo", url: server.local_url }, ...server.lan_urls.map((url) => ({ label: "Red local", url }))];
  const html = urls.map(({ label, url }) => `<li>${label}: <a href="${url}/" target="_blank" rel="noopener">${url}</a></li>`).join("");
  if (list.innerHTML !== html) list.innerHTML = html;
}

// ── Manifest ─────────────────────────────────────────────────────────────

let versionDirty = false;

function renderManifest(s) {
  const m = s.manifest;
  const versionInput = $("versionInput");
  if (!versionDirty && document.activeElement !== versionInput) versionInput.value = s.client.version || "";

  if (m.exists && !m.error) {
    $("mfPublished").textContent = m.client_version === s.client.version
      ? `Publicada en el manifest · actualizado ${ago(m.modified, s.now)} (${dateTime.format(m.modified * 1000)})`
      : `El manifest todavía anuncia ${m.client_version || "sin versión"}; se actualizará al regenerar.`;
    $("mfFiles").textContent = number.format(m.files);
    $("mfFilesSize").textContent = bytes(m.files_size);
    $("mfBlocks").textContent = number.format(m.blocks);
    $("mfBlocksSize").textContent = bytes(m.blocks_size);
    $("mfAssets").textContent = number.format(m.assets);
    $("mfTotal").textContent = `Total ${bytes(m.files_size + m.blocks_size)}`;
    renderFolders(m.folders, m.files);
  } else {
    $("mfPublished").textContent = m.error || "Todavía no hay manifest: se crea al regenerar o al iniciar el servidor.";
    for (const id of ["mfFiles", "mfBlocks", "mfAssets"]) $(id).textContent = "—";
    for (const id of ["mfFilesSize", "mfBlocksSize", "mfTotal"]) $(id).textContent = "";
    renderFolders({}, 0);
  }

  // Estado de la generación: del servidor si está en línea; si no, la del panel.
  const pill = $("genPill");
  const result = $("mfResult");
  const au = s.auto_update;
  const local = s.local_generation;
  let tone = "idle", text = "Servidor detenido", last = null, lastTime = null;

  if (local.running || au?.state === "generating") {
    tone = "busy"; text = "Generando…";
  } else if (au) {
    tone = au.state === "error" ? "error" : "ok";
    text = au.state === "error" ? "Error al generar" : au.watching ? "Al día · vigilando" : "Al día";
    last = au.last_result; lastTime = au.last_run;
  } else if (local.result) {
    tone = local.result.ok ? "ok" : "error";
    text = local.result.ok ? "Generado desde el panel" : "Error al generar";
  }
  if (!last && local.result) { last = local.result; lastTime = local.finished; }
  pill.dataset.tone = tone;
  pill.textContent = text;

  result.classList.toggle("error", !!last && !last.ok);
  if (!last) result.textContent = "";
  else if (!last.ok) result.textContent = `Última generación fallida: ${last.error}`;
  else result.textContent = `Última generación ${ago(lastTime, s.now)}: ${last.changed ? "con cambios" : "sin cambios"}, ${number.format(last.files)} archivos y ${number.format(last.blocks)} bloques en ${last.seconds.toLocaleString("es-ES", { maximumFractionDigits: 1 })} s.`;

  const busy = tone === "busy" || ["starting", "stopping"].includes(s.server.status);
  $("btnRegen").disabled = busy;
  $("btnForce").disabled = busy;
}

function renderFolders(folders, total) {
  const container = $("mfFolders");
  const entries = Object.entries(folders);
  const key = JSON.stringify(entries);
  if (container.dataset.key === key) return;
  container.dataset.key = key;
  container.innerHTML = entries.map(([name, count]) => `
    <div class="folder-row">
      <span>${escapeHtml(name)}</span>
      <span class="folder-bar"><i style="transform: scaleX(${total ? count / total : 0})"></i></span>
      <span>${number.format(count)}</span>
    </div>`).join("");
}

function escapeHtml(text) {
  return String(text).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

$("versionInput").addEventListener("input", () => {
  versionDirty = $("versionInput").value.trim() !== (current?.client.version || "");
  $("btnVersion").disabled = !versionDirty;
});

$("versionForm").addEventListener("submit", (event) => {
  event.preventDefault();
  withButton($("btnVersion"), async () => {
    const result = await api("/api/client-version", { method: "PUT", body: { version: $("versionInput").value } });
    versionDirty = false;
    toast(result.regenerating
      ? `Versión ${result.version} guardada; el servidor está regenerando el manifest.`
      : `Versión ${result.version} guardada. Se publicará al regenerar o al iniciar el servidor.`);
  }).then(() => ($("btnVersion").disabled = !versionDirty));
});

function regenerate(forceBlocks) {
  const button = forceBlocks ? $("btnForce") : $("btnRegen");
  if (forceBlocks && !confirm("Se rehacen los ZIP de todos los bloques de assets. Puede tardar varios minutos y los launchers volverán a descargarlos. ¿Continuar?")) return;
  withButton(button, async () => {
    const result = await api("/api/manifest/regenerate", { method: "POST", body: { force_blocks: forceBlocks } });
    toast(result.where === "server" ? "Regeneración en marcha en el servidor." : "Regenerando desde el panel (servidor detenido).");
  });
}
$("btnRegen").addEventListener("click", () => regenerate(false));
$("btnForce").addEventListener("click", () => regenerate(true));

// ── Servidor ─────────────────────────────────────────────────────────────

$("btnPower").addEventListener("click", () => {
  const action = $("btnPower").dataset.action || "start";
  withButton($("btnPower"), async () => {
    await api(`/api/server/${action}`, { method: "POST" });
    toast(action === "stop" ? "Servidor detenido." : "Servidor iniciado.");
  });
});

function restart(button) {
  withButton(button, async () => {
    await api("/api/server/restart", { method: "POST" });
    toast("Servidor reiniciado con la configuración actual.");
  });
}
$("btnRestart").addEventListener("click", () => restart($("btnRestart")));
$("btnRestartBanner").addEventListener("click", () => restart($("btnRestartBanner")));

document.addEventListener("click", async (event) => {
  const button = event.target.closest("[data-copy]");
  if (!button) return;
  try {
    await navigator.clipboard.writeText($(button.dataset.copy).textContent);
    toast("Copiado al portapapeles.");
  } catch {
    toast("No se pudo copiar; selecciona el texto y usa Ctrl+C.", "error");
  }
});

// ── Configuración ────────────────────────────────────────────────────────

const form = $("configForm");
let savedConfig = null;
let defaults = null;

function fillForm(values) {
  const host = form.elements.host;
  if (![...host.options].some((o) => o.value === values.host)) host.add(new Option(`${values.host} (personalizada)`, values.host));
  for (const [key, value] of Object.entries(values)) {
    const field = form.elements[key];
    if (!field) continue;
    if (field.type === "checkbox") field.checked = !!value;
    else field.value = value;
    field.removeAttribute("aria-invalid");
  }
  updateDirty();
}

function readForm() {
  const values = {};
  for (const key of Object.keys(savedConfig)) {
    const field = form.elements[key];
    if (!field) continue;
    values[key] = field.type === "checkbox" ? field.checked : field.type === "number" ? Number(field.value) : field.value.trim();
  }
  return values;
}

function updateDirty() {
  if (!savedConfig) return;
  const dirty = JSON.stringify(readForm()) !== JSON.stringify(savedConfig);
  $("btnSave").disabled = !dirty;
  $("btnDiscard").disabled = !dirty;
  const msg = $("configMsg");
  msg.className = "form-msg" + (dirty ? " dirty" : "");
  msg.textContent = dirty ? "Cambios sin guardar" : "";
}

async function loadConfig() {
  const config = await api("/api/config");
  savedConfig = config.values;
  defaults = config.defaults;
  fillForm(savedConfig);
  const overrides = Object.values(config.env_overrides);
  $("envNote").hidden = overrides.length === 0;
  $("envNote").textContent = `Variables de entorno activas (${overrides.join(", ")}): tienen prioridad al usar los .bat; el servidor que arranca este panel usa los valores de aquí.`;
}

form.addEventListener("input", updateDirty);
form.addEventListener("change", updateDirty);
$("btnDiscard").addEventListener("click", () => fillForm(savedConfig));
$("btnDefaults").addEventListener("click", () => { fillForm({ ...savedConfig, ...defaults }); updateDirty(); });

form.addEventListener("submit", (event) => {
  event.preventDefault();
  // Validación del navegador primero: marca el campo y lo enfoca.
  const invalid = [...form.elements].find((el) => el.willValidate && !el.checkValidity());
  for (const el of form.elements) if (el.willValidate) el.setAttribute("aria-invalid", String(!el.checkValidity()));
  if (invalid) {
    invalid.focus();
    $("configMsg").className = "form-msg error";
    $("configMsg").textContent = `${form.querySelector(`label[for="${invalid.id}"]`)?.textContent || invalid.name}: ${invalid.validationMessage}`;
    return;
  }
  withButton($("btnSave"), async () => {
    try {
      const result = await api("/api/config", { method: "PUT", body: { values: readForm() } });
      savedConfig = result.values;
      fillForm(savedConfig);
      toast(result.restart_required ? "Configuración guardada. Reinicia el servidor para aplicarla." : "Configuración guardada.");
    } catch (error) {
      $("configMsg").className = "form-msg error";
      $("configMsg").textContent = error.message;
      throw error;
    }
  }).then(updateDirty);
});

$("btnOpenClient").addEventListener("click", () =>
  withButton($("btnOpenClient"), () => api("/api/open", { method: "POST", body: { target: "client" } })));

// ── Registro ─────────────────────────────────────────────────────────────

const log = $("log");
let lastSeq = 0;

function lineText(text) {
  // Quita el prefijo de nivel de uvicorn ("INFO:     ") y la hora que añade el backend
  // ("13:28:23  "): el color ya indica el nivel y la hora está en su propia columna.
  return text.replace(/^(INFO|WARNING|ERROR|DEBUG):\s+/, "").replace(/^\d{2}:\d{2}:\d{2}\s+/, "");
}

async function refreshLogs() {
  try {
    const { lines, last } = await api(`/api/logs?after=${lastSeq}`);
    if (last < lastSeq) { log.innerHTML = ""; lastSeq = 0; return; } // el panel se reinició
    if (!lines.length) { renderLogEmpty(); return; }
    const follow = $("autoscroll").checked;
    const fragment = document.createDocumentFragment();
    for (const line of lines) {
      const row = document.createElement("div");
      row.className = "log-line new";
      row.dataset.level = line.level;
      row.dataset.source = line.source;
      row.innerHTML = `<time>${clock.format(line.time * 1000)}</time><span class="text"></span>`;
      row.lastChild.textContent = lineText(line.text);
      fragment.append(row);
    }
    log.querySelector(".log-empty")?.remove();
    log.append(fragment);
    while (log.childElementCount > MAX_LOG_LINES) log.firstElementChild.remove();
    lastSeq = last;
    if (follow) log.scrollTop = log.scrollHeight;
    $("logCount").textContent = `${number.format(log.childElementCount)} líneas`;
  } catch {
    /* el estado ya muestra que el panel no responde */
  }
}

function renderLogEmpty() {
  if (log.childElementCount === 0) log.innerHTML = `<p class="log-empty">Sin mensajes todavía. Al iniciar el servidor aparecerán aquí sus mensajes y las peticiones de los launchers.</p>`;
}

// Si el usuario sube para leer, deja de seguir el final; al volver abajo, sigue de nuevo.
log.addEventListener("scroll", () => {
  $("autoscroll").checked = log.scrollHeight - log.scrollTop - log.clientHeight < 24;
});

for (const button of document.querySelectorAll(".segmented button")) {
  button.addEventListener("click", () => {
    for (const other of document.querySelectorAll(".segmented button")) other.setAttribute("aria-checked", String(other === button));
    log.dataset.filter = button.dataset.filter;
  });
}

$("btnClearLog").addEventListener("click", () =>
  withButton($("btnClearLog"), async () => {
    await api("/api/logs/clear", { method: "POST" });
    log.innerHTML = "";
    $("logCount").textContent = "";
    renderLogEmpty();
  }));

// ── Arranque ─────────────────────────────────────────────────────────────

loadConfig().catch((error) => toast(error.message, "error"));
refreshState();
refreshLogs();
setInterval(refreshState, STATE_INTERVAL);
setInterval(refreshLogs, LOG_INTERVAL);
