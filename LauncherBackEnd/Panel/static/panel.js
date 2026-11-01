// Panel de control del servidor.
// Consulta cada 2 s la lista de servidores y el estado del seleccionado, y cada segundo su registro.
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

/** Ruta de la API del servidor seleccionado. */
const bk = (path) => `/api/servers/${encodeURIComponent(selectedId)}/${path}`;

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

const ago = (timestamp, now) => (now - timestamp < 10 ? "ahora mismo" : `hace ${duration(now - timestamp)}`);

function escapeHtml(text) {
  return String(text).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
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

/**
 * Diálogo propio del panel. Devuelve { confirmed, checked }.
 * checkbox: { label, onChange(checked, { extra, ok }) } añade una casilla con contenido asociado.
 */
function openDialog({ title, text, confirmLabel = "Aceptar", danger = false, checkbox = null }) {
  const dialog = $("dialog");
  const ok = $("dialogOk");
  const check = $("dialogCheck");
  const extra = $("dialogExtra");
  $("dialogTitle").textContent = title;
  $("dialogText").textContent = text;
  ok.textContent = confirmLabel;
  ok.className = `btn ${danger ? "btn-danger" : "btn-primary"}`;
  ok.disabled = false;
  $("dialogCheckRow").hidden = !checkbox;
  check.checked = false;
  check.onchange = checkbox ? () => checkbox.onChange?.(check.checked, { extra, ok }) : null;
  if (checkbox) $("dialogCheckLabel").textContent = checkbox.label;
  extra.hidden = true;
  extra.replaceChildren();
  dialog.returnValue = "";
  dialog.showModal();
  // En una acción destructiva el foco empieza en Cancelar: Intro no borra nada por accidente.
  (danger ? $("dialogCancel") : ok).focus();
  return new Promise((resolve) => dialog.addEventListener("close",
    () => resolve({ confirmed: dialog.returnValue === "ok", checked: check.checked }), { once: true }));
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
    refreshServers();
  }
}

// ── Textos de estado ─────────────────────────────────────────────────────

const STATUS = {
  running: { chip: "En línea", big: "En línea", power: "Detener servidor", short: "En línea" },
  starting: { chip: "Iniciando…", big: "Iniciando", power: "Iniciando…", short: "Iniciando" },
  stopping: { chip: "Deteniendo…", big: "Deteniendo", power: "Deteniendo…", short: "Deteniendo" },
  stopped: { chip: "Detenido", big: "Detenido", power: "Iniciar servidor", short: "Detenido" },
  crashed: { chip: "Se cerró con error", big: "Caído", power: "Iniciar servidor", short: "Caído" },
  external: { chip: "En línea fuera del panel", big: "En línea", power: "Iniciar servidor", short: "Externo" },
  offline: { chip: "El panel no responde", big: "Sin conexión", power: "Iniciar servidor", short: "—" },
};

// ── Servidores ─────────────────────────────────────────────────────────────

let selectedId = null;
let serverList = [];

async function refreshServers() {
  try {
    const data = await api("/api/servers");
    serverList = data.servers;
    if (!selectedId || !serverList.some((b) => b.id === selectedId)) await switchServer(data.selected, { remember: false });
    renderServers();
  } catch {
    /* el estado ya muestra que el panel no responde */
  }
}

/** Iniciales para la tira plegada: "Servidor principal" → "SP", "Pruebas" → "PR". */
function initials(name) {
  const words = name.trim().split(/\s+/).filter(Boolean);
  const text = words.length > 1 ? words[0][0] + words[1][0] : (words[0] || "?").slice(0, 2);
  return text.toUpperCase();
}

function renderServers() {
  const html = serverList.map((b) => {
    const label = `${b.name} · ${STATUS[b.status].short} · puerto ${b.port}`;
    return `
    <li>
      <button type="button" class="server-item" data-id="${escapeHtml(b.id)}" data-status="${b.status}"
              aria-current="${b.id === selectedId}" aria-label="${escapeHtml(label)}" title="${escapeHtml(label)}">
        <span class="mono" aria-hidden="true">${escapeHtml(initials(b.name))}<span class="dot"></span></span>
        <span class="server-text" aria-hidden="true">
          <span class="server-name">${escapeHtml(b.name)}</span>
          <span class="server-sub">${STATUS[b.status].short} · puerto ${b.port}</span>
        </span>
      </button>
    </li>`;
  }).join("");
  const list = $("serverList");
  if (list.dataset.html !== html) {
    list.dataset.html = html;
    list.innerHTML = html;
  }
}

// ── Lista plegable (se recuerda entre sesiones) ──────────────────────────

function setSidebar(collapsed) {
  document.body.classList.toggle("sidebar-collapsed", collapsed);
  const button = $("btnSidebar");
  button.setAttribute("aria-expanded", String(!collapsed));
  button.setAttribute("aria-label", collapsed ? "Desplegar la lista de servidores" : "Plegar la lista de servidores");
  localStorage.setItem("konnor.sidebarCollapsed", collapsed ? "1" : "0");
}
setSidebar(localStorage.getItem("konnor.sidebarCollapsed") === "1");
$("btnSidebar").addEventListener("click", () => setSidebar(!document.body.classList.contains("sidebar-collapsed")));
$("btnAddServer").addEventListener("click", () => {
  setSidebar(false);
  $("newServerName").focus();
});

$("serverList").addEventListener("click", (event) => {
  const item = event.target.closest(".server-item");
  if (item && item.dataset.id !== selectedId) switchServer(item.dataset.id);
});

/** Cambia el servidor que se muestra (y lo recuerda como seleccionado en servidores.json). */
async function switchServer(id, { remember = true } = {}) {
  if (configDirty()) {
    const { confirmed } = await openDialog({
      title: "Cambios sin guardar",
      text: "La configuración de este servidor tiene cambios sin guardar. Si cambias de servidor, se pierden.",
      confirmLabel: "Descartar y cambiar",
    });
    if (!confirmed) { renderServers(); return; }
  }
  selectedId = id;
  current = null;
  versionDirty = false;
  resetLog();
  renderServers();
  if (remember) api("/api/servers/selected", { method: "PUT", body: { id } }).catch(() => {});
  await Promise.all([loadConfig(), refreshState(), refreshLogs()]);
}

$("newServerForm").addEventListener("submit", (event) => {
  event.preventDefault();
  const input = $("newServerName");
  if (!input.value.trim()) { input.focus(); return; }
  withButton($("btnCreate"), async () => {
    const created = await api("/api/servers", { method: "POST", body: { name: input.value } });
    input.value = "";
    await refreshServers();
    await switchServer(created.id, { remember: false });
    toast(`Servidor «${created.name}» creado en el puerto ${created.port}. Revisa su carpeta del cliente.`);
    $("cfg-client_dir").focus();
  });
});

/** Lista de lo que se borraría y lo que se conservaría al eliminar los archivos. */
function renderFileList(container, files) {
  container.replaceChildren();
  if (files.error) { container.textContent = `No se pudo consultar: ${files.error}`; return; }
  if (!files.items.length) { container.textContent = "Este servidor no tiene archivos en el disco."; return; }
  const total = files.items.filter((i) => i.delete).reduce((sum, i) => sum + i.size, 0);
  const summary = document.createElement("p");
  summary.className = "file-total";
  summary.textContent = total ? `Se eliminarán ${bytes(total)}. No se puede deshacer.` : "Ninguno de estos archivos se puede eliminar desde aquí.";
  const list = document.createElement("ul");
  list.className = "file-list";
  for (const item of files.items) {
    const li = document.createElement("li");
    li.dataset.delete = String(item.delete);
    li.innerHTML = `<span class="file-label"></span><span class="file-size"></span><span class="file-path"></span><span class="file-state"></span>`;
    li.querySelector(".file-label").textContent = item.label;
    li.querySelector(".file-size").textContent = bytes(item.size);
    li.querySelector(".file-path").textContent = item.path;
    li.querySelector(".file-state").textContent = item.delete ? "Se elimina" : `Se conserva: ${item.reason.toLowerCase()}`;
    list.append(li);
  }
  container.append(summary, list);
}

$("btnDelete").addEventListener("click", async () => {
  const id = selectedId;
  const name = savedConfig?.name || id;
  // La lista se pide ya, mientras el usuario lee el diálogo.
  const files = api(bk("files")).catch((error) => ({ error: error.message }));
  const { confirmed, checked } = await openDialog({
    title: `Eliminar «${name}»`,
    text: "Se elimina de la lista del panel. Si no marcas la casilla, sus archivos se quedan en el disco.",
    confirmLabel: "Eliminar servidor",
    danger: true,
    checkbox: {
      label: "Eliminar archivos",
      onChange: async (on, { extra, ok }) => {
        ok.textContent = on ? "Eliminar servidor y archivos" : "Eliminar servidor";
        extra.hidden = !on;
        if (!on) return;
        extra.textContent = "Calculando…";
        renderFileList(extra, await files);
      },
    },
  });
  if (!confirmed) return;
  withButton($("btnDelete"), async () => {
    const query = checked ? "?eliminar_archivos=true" : "";
    const result = await api(`/api/servers/${encodeURIComponent(id)}${query}`, { method: "DELETE" });
    selectedId = null;
    savedConfig = null;
    await refreshServers();
    if (result.errors.length) toast(`Servidor «${name}» eliminado, pero no se pudieron borrar: ${result.errors.join("; ")}`, "error");
    else if (checked) toast(`Servidor «${name}» eliminado junto con ${result.deleted.length} ${result.deleted.length === 1 ? "elemento" : "elementos"} del disco.`);
    else toast(`Servidor «${name}» eliminado. Sus archivos siguen en el disco.`);
  });
});

// ── Estado del servidor seleccionado ──────────────────────────────────────

let current = null;

async function refreshState() {
  if (!selectedId) return;
  const id = selectedId;
  try {
    const state = await api(bk("state"));
    if (id !== selectedId) return; // llegó tarde, de un servidor que ya no se muestra
    current = state;
    renderState(state);
  } catch {
    renderOffline();
  }
}

function setStatus(status) {
  for (const el of [$("serverZone"), $("statusChip")]) el.dataset.status = status;
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

  // Barra superior: nombre del servidor, estado y encendido.
  $("serverTitle").textContent = s.profile.name;
  document.title = `${s.profile.name} · Servidor de Konnor Launcher`;
  $("statusText").textContent = labels.chip;
  const power = $("btnPower");
  power.textContent = labels.power;
  power.classList.toggle("btn-primary", status !== "running");
  power.classList.toggle("btn-danger", status === "running");
  power.disabled = ["starting", "stopping", "external"].includes(status) || s.local_generation.running;
  power.dataset.action = status === "running" ? "stop" : "start";
  $("btnRestart").disabled = status !== "running";
  $("btnDelete").disabled = !["stopped", "crashed"].includes(status) || serverList.length <= 1;
  $("deleteHint").textContent = serverList.length <= 1
    ? "Es el único servidor: tiene que quedar al menos uno."
    : ["stopped", "crashed"].includes(status)
      ? "Se elimina de la lista del panel. Al confirmar se puede elegir si se borran también sus archivos."
      : "Detén su servidor antes de eliminarlo.";

  // Servidor.
  $("bigState").textContent = labels.big;
  $("stateDetail").textContent = stateDetail(s);
  $("serverUptime").textContent = server.started_at ? `Activo desde hace ${duration(s.now - server.started_at)}` : "";
  $("launcherLine").textContent = server.public_url || server.lan_urls[0] || server.local_url;
  $("launcherLineHint").textContent = server.public_url
    ? `IP pública de la red. Para que entren jugadores de fuera, el router debe redirigir el puerto ${server.port} (TCP) a este equipo.`
    : server.host === "0.0.0.0" || server.host === "::"
      ? "Buscando la IP pública de la red… Mientras tanto se muestra la dirección de la red local."
      : "El servidor solo escucha en esta dirección: no es accesible desde Internet.";
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

  const generation = renderManifest(s);
  renderAlerts(s, generation);
  renderBlockHint();
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
  const urls = [{ label: "Este equipo", url: server.local_url }, ...server.lan_urls.map((url) => ({ label: "Red local", url }))];
  if (server.public_url) urls.push({ label: "Internet", url: server.public_url });
  const html = urls.map(({ label, url }) => `<li>${label}: <a href="${url}/" target="_blank" rel="noopener">${url}</a></li>`).join("");
  if ($("urlList").innerHTML !== html) $("urlList").innerHTML = html;
}

// ── Avisos: lo que requiere atención, de más a menos grave ───────────────

function renderAlerts(s, generation) {
  const alerts = [];
  const m = s.manifest;
  if (s.server.status === "crashed")
    alerts.push({ tone: "error", text: `El servidor se cerró inesperadamente (código ${s.server.exit_code}).`, action: "Ver errores", run: showErrors });
  if (!s.client.exists)
    alerts.push({ tone: "error", text: `No existe la carpeta del cliente: ${s.client.dir}`, action: "Cambiar carpeta", run: () => focusField("cfg-client_dir") });
  if (generation.last && !generation.last.ok)
    alerts.push({ tone: "error", text: `La última generación del manifest falló: ${generation.last.error}`, action: "Ver errores", run: showErrors });
  if (s.server.restart_required)
    alerts.push({ tone: "warn", text: "La configuración cambió después de arrancar el servidor.", action: "Reiniciar ahora", run: (b) => restart(b) });
  if (!m.exists && !generation.busy && s.client.exists)
    alerts.push({ tone: "warn", text: "Todavía no hay manifest: los launchers no pueden verificar.", action: "Regenerar", run: () => regenerate(false) });
  else if (m.exists && !m.error && s.client.version && m.client_version !== s.client.version && !generation.busy)
    alerts.push({ tone: "warn", text: `El manifest anuncia ${m.client_version || "sin versión"}, pero la versión configurada es ${s.client.version}.`, action: "Regenerar", run: () => regenerate(false) });
  if (s.activity.errors > 0)
    alerts.push({ tone: "warn", text: `${number.format(s.activity.errors)} respuestas con error a los launchers desde el arranque.`, action: "Ver errores", run: showErrors });
  if (s.server.status === "external")
    alerts.push({ tone: "info", text: "Este servidor lo arrancó otro proceso: el panel no puede detenerlo ni ver su registro." });

  const key = JSON.stringify(alerts.map((a) => [a.tone, a.text]));
  const list = $("alertList");
  $("alerts").hidden = alerts.length === 0;
  if (list.dataset.key === key) return;
  list.dataset.key = key;
  list.replaceChildren(...alerts.map((alert) => {
    const li = document.createElement("li");
    li.className = "alert";
    li.dataset.tone = alert.tone;
    const p = document.createElement("p");
    p.textContent = alert.text;
    li.append(p);
    if (alert.action) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "btn btn-small";
      button.textContent = alert.action;
      button.addEventListener("click", () => alert.run(button));
      li.append(button);
    }
    return li;
  }));
}

function showErrors() {
  setLogFilter("error");
  $("log").scrollIntoView({ behavior: "smooth", block: "center" });
}

function focusField(id) {
  $(id).scrollIntoView({ behavior: "smooth", block: "center" });
  $(id).focus({ preventScroll: true });
}

// ── Manifest ─────────────────────────────────────────────────────────────

let versionDirty = false;

/** Pinta la zona del manifest y devuelve el estado de la generación (para los avisos). */
function renderManifest(s) {
  const m = s.manifest;
  const versionInput = $("versionInput");
  if (!versionDirty && document.activeElement !== versionInput) versionInput.value = s.client.version || "";

  if (m.exists && !m.error) {
    const updated = `actualizado ${ago(m.modified, s.now)} (${dateTime.format(m.modified * 1000)})`;
    $("mfPublished").textContent = (m.client_version || "") !== (s.client.version || "")
      ? `El manifest todavía anuncia ${m.client_version || "sin versión"}; se actualizará al regenerar.`
      : s.client.version
        ? `Publicada en el manifest · ${updated}`
        : `Sin versión: el manifest no anuncia ninguna. Escribe una y publícala · ${updated}`;
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

  // Estado de la generación: la del servidor si está en línea; si no, la del panel.
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
  $("genPill").dataset.tone = tone;
  $("genPill").textContent = text;

  const result = $("mfResult");
  result.classList.toggle("error", !!last && !last.ok);
  if (!last) result.textContent = "";
  else if (!last.ok) result.textContent = `Última generación fallida: ${last.error}`;
  else result.textContent = `Última generación ${ago(lastTime, s.now)}: ${last.changed ? "con cambios" : "sin cambios"}, ${number.format(last.files)} archivos y ${number.format(last.blocks)} bloques en ${last.seconds.toLocaleString("es-ES", { maximumFractionDigits: 1 })} s.`;

  const busy = tone === "busy" || ["starting", "stopping"].includes(s.server.status);
  $("btnRegen").disabled = busy;
  $("btnForce").disabled = busy;
  return { busy: tone === "busy", last };
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

$("versionInput").addEventListener("input", () => {
  versionDirty = $("versionInput").value.trim() !== (current?.client.version || "");
  $("btnVersion").disabled = !versionDirty;
});

$("versionForm").addEventListener("submit", (event) => {
  event.preventDefault();
  withButton($("btnVersion"), async () => {
    const result = await api(bk("client-version"), { method: "PUT", body: { version: $("versionInput").value } });
    versionDirty = false;
    toast(result.regenerating
      ? `Versión ${result.version} guardada; el servidor está regenerando el manifest.`
      : `Versión ${result.version} guardada. Se publicará al regenerar o al iniciar el servidor.`);
  }).then(() => ($("btnVersion").disabled = !versionDirty));
});

async function regenerate(forceBlocks) {
  const button = forceBlocks ? $("btnForce") : $("btnRegen");
  if (forceBlocks) {
    const { confirmed } = await openDialog({
      title: "Rehacer todos los bloques",
      text: "Se rehacen los ZIP de todos los bloques de assets aunque no hayan cambiado. Puede tardar varios minutos.",
      confirmLabel: "Rehacer bloques",
    });
    if (!confirmed) return;
  }
  withButton(button, async () => {
    const result = await api(bk("manifest/regenerate"), { method: "POST", body: { force_blocks: forceBlocks } });
    toast(result.where === "server" ? "Regeneración en marcha en el servidor." : "Regenerando desde el panel (servidor detenido).");
  });
}
$("btnRegen").addEventListener("click", () => regenerate(false));
$("btnForce").addEventListener("click", () => regenerate(true));

// ── Servidor ─────────────────────────────────────────────────────────────

$("btnPower").addEventListener("click", () => {
  const action = $("btnPower").dataset.action || "start";
  withButton($("btnPower"), async () => {
    await api(bk(`server/${action}`), { method: "POST" });
    toast(action === "stop" ? "Servidor detenido." : "Servidor iniciado.");
  });
});

function restart(button) {
  withButton(button, async () => {
    await api(bk("server/restart"), { method: "POST" });
    toast("Servidor reiniciado con la configuración actual.");
  });
}
$("btnRestart").addEventListener("click", () => restart($("btnRestart")));

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
const CONFIG_KEYS = ["name", "host", "port", "client_dir", "delete_unauthorized", "always_online", "lock_server_url", "block_count", "auto_update", "watch", "watch_quiet_ms", "manifest", "version_file", "cache_dir"];

function fillForm(values) {
  const host = form.elements.host;
  for (const option of [...host.options]) if (option.dataset.custom) option.remove();
  if (![...host.options].some((o) => o.value === values.host)) {
    const option = new Option(`${values.host} (personalizada)`, values.host);
    option.dataset.custom = "1";
    host.add(option);
  }
  for (const key of CONFIG_KEYS) {
    const field = form.elements[key];
    if (field.type === "checkbox") field.checked = !!values[key];
    else field.value = values[key];
    field.removeAttribute("aria-invalid");
  }
  updateDirty();
  renderBlockHint();
}

function readForm() {
  const values = {};
  for (const key of CONFIG_KEYS) {
    const field = form.elements[key];
    values[key] = field.type === "checkbox" ? field.checked : field.type === "number" ? Number(field.value) : field.value.trim();
  }
  return values;
}

function configDirty() {
  if (!savedConfig) return false;
  const saved = Object.fromEntries(CONFIG_KEYS.map((k) => [k, savedConfig[k]]));
  return JSON.stringify(readForm()) !== JSON.stringify(saved);
}

function updateDirty() {
  const dirty = configDirty();
  $("btnSave").disabled = !dirty;
  $("btnDiscard").disabled = !dirty;
  const msg = $("configMsg");
  msg.className = "form-msg" + (dirty ? " dirty" : "");
  msg.textContent = dirty ? "Cambios sin guardar" : "";
}

/** Muestra lo que produce el número de bloques: tamaño aproximado de cada ZIP con los assets actuales. */
function renderBlockHint() {
  const n = Number(form.elements.block_count.value);
  const hint = $("hint-blocks");
  if (!Number.isInteger(n) || n < 1 || n > 256) { hint.textContent = "Entre 1 y 256."; return; }
  const m = current?.manifest;
  const size = m?.exists && m.blocks_size ? `, unos ${bytes(m.blocks_size / n)} cada uno con los assets actuales` : "";
  const changed = savedConfig && n !== savedConfig.block_count
    ? "Al guardar, los bloques se rehacen en la siguiente generación; los jugadores que ya tienen los assets no los vuelven a descargar."
    : "Más bloques: descargas más pequeñas y más paralelas. Menos bloques: menos peticiones.";
  hint.textContent = `${n} ${n === 1 ? "bloque" : "bloques"}${size}. ${changed}`;
}

/** Estado de la liberación (no forma parte del formulario: se activa con su botón y es irreversible). */
let isReleased = false;

function renderRelease(released) {
  isReleased = released;
  const pill = $("releasePill");
  pill.textContent = released ? "Liberadas" : "No liberadas";
  pill.dataset.tone = released ? "ok" : "idle";
  $("btnRelease").disabled = released;
  $("btnRelease").textContent = released ? "Liberadas" : "Liberar";
  $("releaseHint").textContent = released
    ? "Los jugadores pueden activar o desactivar la sincronización de sus instancias. No se puede deshacer."
    : "Las instancias creadas con este servidor quedan vinculadas: los jugadores no pueden desactivar la sincronización. Al liberarlas podrán hacerlo. No se puede deshacer.";
}

$("btnRelease").addEventListener("click", async () => {
  const name = savedConfig?.name || selectedId;
  const { confirmed } = await openDialog({
    title: `Liberar las instancias de «${name}»`,
    text: "Los jugadores podrán desactivar la sincronización de las instancias de este servidor y modificarlas libremente. Esta acción no se puede deshacer.",
    confirmLabel: "Liberar instancias",
    danger: true,
  });
  if (!confirmed) return;
  withButton($("btnRelease"), async () => {
    const result = await api(bk("release"), { method: "POST" });
    renderRelease(true);
    toast(result.where === "pendiente"
      ? "Instancias liberadas. Se publicará en la siguiente regeneración del manifest."
      : "Instancias liberadas y publicadas en el manifest.");
  }).then(() => renderRelease(isReleased)); // withButton reactiva el botón: se vuelve a aplicar el estado real
});

async function loadConfig() {
  const id = selectedId;
  const config = await api(bk("config"));
  if (id !== selectedId) return;
  savedConfig = config.values;
  defaults = config.defaults;
  renderRelease(config.values.released === true);
  $("serverIdMeta").textContent = `id: ${config.values.id}`;
  fillForm(savedConfig);
  const overrides = Object.values(config.env_overrides);
  $("envNote").hidden = overrides.length === 0;
  $("envNote").textContent = `Variables de entorno activas (${overrides.join(", ")}): tienen prioridad al usar los .bat; los servidores que arranca este panel usan los valores de aquí.`;
}

/** Los switches de Seguridad se guardan al moverlos, sin tocar el resto de cambios pendientes del formulario. */
const AUTOSAVE_KEYS = ["delete_unauthorized", "always_online", "lock_server_url"];

for (const key of AUTOSAVE_KEYS) {
  const field = form.elements[key];
  field.addEventListener("change", async () => {
    if (!savedConfig) return;
    const id = selectedId;
    const checked = field.checked;
    const values = { ...Object.fromEntries(CONFIG_KEYS.map((k) => [k, savedConfig[k]])), [key]: checked };
    field.disabled = true;
    try {
      const result = await api(bk("config"), { method: "PUT", body: { values } });
      if (id !== selectedId) return;
      savedConfig = result.values;
      toast("Guardado.");
      refreshState(); // muestra el aviso de reinicio si hace falta
    } catch (error) {
      field.checked = !checked;
      toast(error.message, "error");
    } finally {
      field.disabled = false;
      updateDirty();
    }
  });
}

form.addEventListener("input", () => { updateDirty(); renderBlockHint(); });
form.addEventListener("change", updateDirty);
$("btnDiscard").addEventListener("click", () => fillForm(savedConfig));
$("btnDefaults").addEventListener("click", () => fillForm({ ...savedConfig, ...defaults, name: readForm().name }));

form.addEventListener("submit", (event) => {
  event.preventDefault();
  // Validación del navegador primero: marca el campo y lo enfoca.
  const fields = [...form.elements].filter((el) => el.willValidate);
  for (const el of fields) el.setAttribute("aria-invalid", String(!el.checkValidity()));
  const invalid = fields.find((el) => !el.checkValidity());
  if (invalid) {
    invalid.closest("details")?.setAttribute("open", "");
    invalid.focus();
    $("configMsg").className = "form-msg error";
    $("configMsg").textContent = `${form.querySelector(`label[for="${invalid.id}"]`)?.textContent || invalid.name}: ${invalid.validationMessage}`;
    return;
  }
  withButton($("btnSave"), async () => {
    try {
      const result = await api(bk("config"), { method: "PUT", body: { values: readForm() } });
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
  withButton($("btnOpenClient"), () => api(bk("open"), { method: "POST" })));

// ── Registro ─────────────────────────────────────────────────────────────

const log = $("log");
let lastSeq = 0;

function lineText(text) {
  // Quita el prefijo de nivel de uvicorn ("INFO:     ") y la hora que añade el servidor
  // ("13:28:23  "): el color ya indica el nivel y la hora está en su propia columna.
  return text.replace(/^(INFO|WARNING|ERROR|DEBUG):\s+/, "").replace(/^\d{2}:\d{2}:\d{2}\s+/, "");
}

function resetLog() {
  lastSeq = 0;
  log.replaceChildren();
  $("logCount").textContent = "";
}

async function refreshLogs() {
  if (!selectedId) return;
  const id = selectedId;
  try {
    const { lines, last } = await api(bk(`logs?after=${lastSeq}`));
    if (id !== selectedId) return;
    if (last < lastSeq) { resetLog(); return; } // el panel se reinició
    if (!lines.length) { renderLogEmpty(); return; }
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
    if ($("autoscroll").checked) log.scrollTop = log.scrollHeight;
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

function setLogFilter(filter) {
  for (const button of document.querySelectorAll(".segmented button"))
    button.setAttribute("aria-checked", String(button.dataset.filter === filter));
  log.dataset.filter = filter;
}
for (const button of document.querySelectorAll(".segmented button"))
  button.addEventListener("click", () => setLogFilter(button.dataset.filter));

$("btnClearLog").addEventListener("click", () =>
  withButton($("btnClearLog"), async () => {
    await api(bk("logs/clear"), { method: "POST" });
    resetLog();
    renderLogEmpty();
  }));

// ── Arranque ─────────────────────────────────────────────────────────────

const API_VERSION = 3;

/**
 * La interfaz se lee del disco, pero la API es la del panel que está en marcha. Si ese panel es
 * de una versión anterior o su código cambió desde que arrancó, hay que reiniciarlo.
 */
async function checkApiVersion() {
  try {
    const ping = await api("/api/ping");
    if ((ping.api || 0) >= API_VERSION && !ping.outdated) return;
  } catch {
    return; // sin conexión: ya lo indica el estado
  }
  toast("El panel se actualizó mientras estaba abierto. Cierra su consola y vuelve a abrir iniciar_panel.bat.", "error");
  clearTimeout(toastTimer); // este aviso se queda fijo
}

checkApiVersion();
refreshServers();
setInterval(refreshServers, STATE_INTERVAL);
setInterval(refreshState, STATE_INTERVAL);
setInterval(refreshLogs, LOG_INTERVAL);
