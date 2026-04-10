/* ── State ───────────────────────────────────────────────────────────────── */
let allMovies = [];
let activeMovieId = null;
let statusFilter = 'all';
let setupReady = false;
let plexLoginPollTimer = null;
let pendingPlexLogin = null;
let updatePollTimer = null;
let updateStatusTimer = null;
let syncStatusTimer = null;
let versionState = { current: '', latest: '', updateAvailable: false, updating: false };
let availableServers = [];
let availableLibrariesByServer = {};
let setupSelectedServerIds = new Set();
let setupSelectedLibraries = {};
let settingsState = null;

/* ── DOM refs ────────────────────────────────────────────────────────────── */
const movieList = document.getElementById('movie-list');
const searchInput = document.getElementById('search-input');
const btnFilter = document.getElementById('btn-filter');
const filterMenu = document.getElementById('filter-menu');
const btnSync = document.getElementById('btn-sync');
const btnSettings = document.getElementById('btn-settings');
const syncStatus = document.getElementById('sync-status');
const syncModal = document.getElementById('sync-modal');
const syncModalLogs = document.getElementById('sync-modal-logs');
const syncModalStatus = document.getElementById('sync-modal-status');
const syncModalClose = document.getElementById('sync-modal-close');
const rightEmpty = document.getElementById('right-empty');
const rightLoading = document.getElementById('right-loading');
const rightContent = document.getElementById('right-content');
const movieHeading = document.getElementById('movie-heading');
const resultsGrid = document.getElementById('results-grid');
const youtubeSearchLink = document.getElementById('youtube-search-link');
const urlDownloadForm = document.getElementById('url-download-form');
const urlDownloadInput = document.getElementById('url-download-input');
const urlDownloadSubmit = document.getElementById('url-download-submit');
const statTotal = document.getElementById('stat-total');
const statDone = document.getElementById('stat-downloaded');
const statPending = document.getElementById('stat-pending');
const toast = document.getElementById('toast');
const updateBanner = document.getElementById('update-banner');
const updateText = document.getElementById('update-text');
const btnUpdate = document.getElementById('btn-update');
const updateModal = document.getElementById('update-modal');
const updateModalLogs = document.getElementById('update-modal-logs');
const updateModalStatus = document.getElementById('update-modal-status');
const updateModalClose = document.getElementById('update-modal-close');
const setupScreen = document.getElementById('setup-screen');
const appShell = document.getElementById('app-shell');
const setupPlexLogin = document.getElementById('setup-plex-login');
const setupStatus = document.getElementById('setup-status');
const setupAccountName = document.getElementById('setup-account-name');
const setupServerName = document.getElementById('setup-server-name');
const setupSelection = document.getElementById('setup-selection');
const setupServers = document.getElementById('setup-servers');
const setupLibraries = document.getElementById('setup-libraries');
const setupSaveSelection = document.getElementById('setup-save-selection');
const setupUpdateBanner = document.getElementById('setup-update-banner');
const setupUpdateText = document.getElementById('setup-update-text');
const setupBtnUpdate = document.getElementById('setup-btn-update');
const settingsModal = document.getElementById('settings-modal');
const settingsModalClose = document.getElementById('settings-modal-close');
const settingsServers = document.getElementById('settings-servers');
const settingsLibraries = document.getElementById('settings-libraries');
const settingsPathMappings = document.getElementById('settings-path-mappings');
const settingsLibraryPaths = document.getElementById('settings-library-paths');
const settingsAdvancedSearchDepth = document.getElementById('settings-advanced-search-depth');
const settingsAdvancedMaxDirs = document.getElementById('settings-advanced-max-dirs');
const settingsSave = document.getElementById('settings-save');
const settingsReset = document.getElementById('settings-reset');

/* ── Toast ───────────────────────────────────────────────────────────────── */
let toastTimer = null;
function showToast(msg, type = 'success') {
  clearTimeout(toastTimer);
  toast.textContent = msg;
  toast.className = [
    'fixed bottom-6 right-6 z-50 rounded-lg px-4 py-3 text-sm font-medium shadow-xl',
    type === 'success' ? 'bg-green-600 text-white' : 'bg-red-600 text-white',
  ].join(' ');
  toast.classList.remove('hidden');
  toastTimer = setTimeout(() => {
    toast.classList.add('hidden');
  }, 4000);
}

function setAppVisible(isVisible) {
  if (isVisible) {
    setupScreen.classList.add('hidden');
    appShell.classList.remove('hidden');
  } else {
    appShell.classList.add('hidden');
    setupScreen.classList.remove('hidden');
  }
}

function api(method, path, body) {
  const opts = { method, headers: { 'Content-Type': 'application/json' } };
  if (body) opts.body = JSON.stringify(body);
  return fetch(path, opts).then(async (res) => {
    if (!res.ok) {
      const err = await res.json().catch(() => ({ detail: res.statusText }));
      throw new Error(err.detail || res.statusText);
    }
    return res.json();
  });
}

function esc(str) {
  return String(str ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/* ── Setup / Plex sign-in ───────────────────────────────────────────────── */
function setSetupConnection(status) {
  setupAccountName.textContent = status.plexAccountName || 'Not connected';
  setupServerName.textContent = status.plexServerName
    ? `Servers: ${status.plexServerName}`
    : 'Sign in, then choose your Plex servers and libraries';
  setupPlexLogin.textContent = status.plexConnected ? 'Reconnect with Plex' : 'Sign in with Plex';
}

function renderServerCheckboxes(container, servers, selectedIds, onChange) {
  if (!container) return;
  container.innerHTML = '';
  if (!servers.length) {
    container.innerHTML = '<p class="text-xs text-gray-500">No servers found.</p>';
    return;
  }

  servers.forEach((server) => {
    const row = document.createElement('label');
    row.className = 'flex items-center gap-2 rounded border border-white/10 bg-white/5 px-3 py-2 text-sm';
    const checked = selectedIds.has(server.id) ? 'checked' : '';
    row.innerHTML = `
      <input type="checkbox" class="server-select" data-server-id="${esc(server.id)}" ${checked} />
      <span class="font-medium text-white">${esc(server.name || server.url)}</span>
      <span class="text-xs text-gray-400">${esc(server.url)}</span>
    `;
    container.appendChild(row);
  });

  container.querySelectorAll('.server-select').forEach((input) => {
    input.addEventListener('change', () => onChange());
  });
}

function collectSelectedServerIds(container) {
  const result = new Set();
  container?.querySelectorAll('.server-select:checked').forEach((input) => {
    if (input.dataset.serverId) result.add(input.dataset.serverId);
  });
  return result;
}

function renderLibraryCheckboxes(container, librariesByServer, selectedServerIds, selectedLibraries, onChange) {
  if (!container) return;
  container.innerHTML = '';
  if (!selectedServerIds.size) {
    container.innerHTML = '<p class="text-xs text-gray-500">Select at least one server first.</p>';
    return;
  }

  Array.from(selectedServerIds).forEach((serverId) => {
    const libs = librariesByServer[serverId] || [];
    const section = document.createElement('div');
    section.className = 'rounded border border-white/10 bg-white/5 p-3';
    section.innerHTML = `<p class="mb-2 text-xs uppercase tracking-wide text-gray-400">${esc(serverId)}</p>`;

    if (!libs.length) {
      const empty = document.createElement('p');
      empty.className = 'text-xs text-gray-500';
      empty.textContent = 'No movie libraries found on this server.';
      section.appendChild(empty);
    } else {
      libs.forEach((lib) => {
        const row = document.createElement('label');
        row.className = 'mb-1 flex items-center gap-2 text-sm text-gray-200';
        const checked = (selectedLibraries[serverId] || []).includes(lib.key) ? 'checked' : '';
        row.innerHTML = `<input type="checkbox" class="library-select" data-server-id="${esc(serverId)}" data-library-key="${esc(lib.key)}" ${checked} /><span>${esc(lib.title)}</span>`;
        section.appendChild(row);
      });
    }

    container.appendChild(section);
  });

  container.querySelectorAll('.library-select').forEach((input) => {
    input.addEventListener('change', () => onChange());
  });
}

function collectSelectedLibraries(container) {
  const result = {};
  container?.querySelectorAll('.library-select:checked').forEach((input) => {
    const serverId = input.dataset.serverId;
    const libraryKey = input.dataset.libraryKey;
    if (!serverId || !libraryKey) return;
    if (!result[serverId]) result[serverId] = [];
    result[serverId].push(libraryKey);
  });
  return result;
}

async function loadSetupSelectionOptions(prefill = null) {
  const serversPayload = await api('GET', '/api/setup/plex/servers');
  availableServers = serversPayload.servers || [];

  const selectedFromPrefill = new Set((prefill?.selectedServers || []).map((s) => s.id));
  setupSelectedServerIds = selectedFromPrefill.size ? selectedFromPrefill : new Set(availableServers.map((s) => s.id));

  const selectedServersForLibraryQuery = availableServers.filter((s) => setupSelectedServerIds.has(s.id));
  const libsPayload = await api('POST', '/api/setup/plex/libraries', { servers: selectedServersForLibraryQuery });
  availableLibrariesByServer = libsPayload.libraries || {};
  setupSelectedLibraries = prefill?.selectedLibraries || {};

  setupSelection.classList.remove('hidden');
  renderServerCheckboxes(setupServers, availableServers, setupSelectedServerIds, async () => {
    setupSelectedServerIds = collectSelectedServerIds(setupServers);
    const selectedServers = availableServers.filter((s) => setupSelectedServerIds.has(s.id));
    const libs = await api('POST', '/api/setup/plex/libraries', { servers: selectedServers });
    availableLibrariesByServer = libs.libraries || {};
    renderLibraryCheckboxes(setupLibraries, availableLibrariesByServer, setupSelectedServerIds, setupSelectedLibraries, () => {
      setupSelectedLibraries = collectSelectedLibraries(setupLibraries);
    });
  });
  renderLibraryCheckboxes(setupLibraries, availableLibrariesByServer, setupSelectedServerIds, setupSelectedLibraries, () => {
    setupSelectedLibraries = collectSelectedLibraries(setupLibraries);
  });
}

function clearPlexLoginPolling() {
  if (plexLoginPollTimer) {
    clearInterval(plexLoginPollTimer);
    plexLoginPollTimer = null;
  }
}

function getReturnLoginParams() {
  const params = new URLSearchParams(window.location.search);
  const pinIdRaw = (params.get('plexPinId') || '').trim();
  const code = (params.get('plexCode') || '').trim();

  if (!/^\d+$/.test(pinIdRaw) || !code) {
    return null;
  }

  return { pinId: Number(pinIdRaw), code };
}

function clearReturnLoginParams() {
  const params = new URLSearchParams(window.location.search);
  params.delete('plexPinId');
  params.delete('plexCode');
  const next = `${window.location.pathname}${params.toString() ? `?${params}` : ''}${window.location.hash || ''}`;
  window.history.replaceState({}, '', next);
}

async function loadSetupState() {
  const state = await api('GET', '/api/setup/status');
  setupReady = Boolean(state.setupComplete);
  setSetupConnection(state);

  if (!setupReady) {
    setAppVisible(false);
    setupStatus.textContent = state.plexConnected
      ? 'Plex is connected. Select servers and libraries to complete setup.'
      : 'Sign in with Plex to continue.';
    setupPlexLogin.disabled = false;
    if (state.plexConnected) {
      try {
        await loadSetupSelectionOptions(state);
      } catch (e) {
        setupStatus.textContent = `Failed to load server selection: ${e.message}`;
      }
    } else {
      setupSelection.classList.add('hidden');
    }
    return false;
  }

  setAppVisible(true);
  return true;
}

async function enterApp() {
  setupReady = true;
  setAppVisible(true);
  await loadMovies();
}

async function pollPlexLogin(pinId, code) {
  try {
    const status = await api('GET', `/api/setup/plex/login/status?pin_id=${encodeURIComponent(pinId)}&code=${encodeURIComponent(code)}`);

    if (!status.claimed) {
      setupStatus.textContent = 'Waiting for Plex approval...';
      return;
    }

    clearPlexLoginPolling();
    pendingPlexLogin = null;
    setupStatus.textContent = `Connected to ${status.accountName || 'Plex'}. Select servers and libraries below.`;
    setupPlexLogin.disabled = false;
    showToast('Plex sign-in complete');
    await loadSetupState();
  } catch (e) {
    clearPlexLoginPolling();
    setupPlexLogin.disabled = false;
    setupStatus.textContent = '';
    showToast(`Plex sign-in failed: ${e.message}`, 'error');
  }
}

function checkPendingPlexLoginSoon() {
  if (!pendingPlexLogin) return;
  pollPlexLogin(pendingPlexLogin.pinId, pendingPlexLogin.code);
}

async function startPlexLogin() {
  setupStatus.textContent = 'Requesting Plex sign-in...';
  setupPlexLogin.disabled = true;

  try {
    const cleanForwardUrl = `${window.location.origin}${window.location.pathname}`;
    const login = await api('POST', '/api/setup/plex/login', { forward_url: cleanForwardUrl });

    if (!login.authUrl || !login.pinId || !login.code) {
      throw new Error('Plex did not return a valid sign-in link');
    }

    const popup = window.open(login.authUrl, '_blank', 'noopener,noreferrer');
    if (!popup) {
      setupStatus.textContent = 'Please allow popups for Plex sign-in, then try again.';
      setupPlexLogin.disabled = false;
      return;
    }

    setupStatus.textContent = 'Plex sign-in opened in a new tab. Approve it there.';
    pendingPlexLogin = { pinId: login.pinId, code: login.code };
    clearPlexLoginPolling();
    plexLoginPollTimer = setInterval(() => {
      pollPlexLogin(login.pinId, login.code);
    }, 2000);
    await pollPlexLogin(login.pinId, login.code);
  } catch (e) {
    pendingPlexLogin = null;
    setupPlexLogin.disabled = false;
    setupStatus.textContent = '';
    showToast(`Plex sign-in failed: ${e.message}`, 'error');
  }
}

async function resetAppToSetup() {
  const confirmed = window.confirm('Reset Themearr and return to setup? This clears saved settings and synced movie data.');
  if (!confirmed) return;

  try {
    await api('POST', '/api/setup/reset');
    allMovies = [];
    activeMovieId = null;
    renderList('');
    updateStats();
    rightContent.classList.add('hidden');
    rightLoading.classList.add('hidden');
    rightEmpty.classList.remove('hidden');
    syncStatus.textContent = '';
    setupStatus.textContent = '';
    setupPlexLogin.disabled = false;
    clearPlexLoginPolling();
    pendingPlexLogin = null;
    setupSelection.classList.add('hidden');
    setAppVisible(false);
    await loadSetupState();
    showToast('App reset. Sign in with Plex to continue.');
  } catch (e) {
    showToast(`Reset failed: ${e.message}`, 'error');
  }
}

setupSaveSelection?.addEventListener('click', async () => {
  setupSelectedServerIds = collectSelectedServerIds(setupServers);
  setupSelectedLibraries = collectSelectedLibraries(setupLibraries);
  const selectedServers = availableServers.filter((server) => setupSelectedServerIds.has(server.id));

  setupSaveSelection.disabled = true;
  setupSaveSelection.textContent = 'Saving...';
  try {
    await api('POST', '/api/setup/plex/selection', {
      servers: selectedServers,
      selected_libraries: setupSelectedLibraries,
    });
    showToast('Setup saved');
    await loadSetupState();
    await enterApp();
  } catch (e) {
    showToast(`Setup save failed: ${e.message}`, 'error');
  } finally {
    setupSaveSelection.disabled = false;
    setupSaveSelection.textContent = 'Save server and library selection';
  }
});

setupPlexLogin?.addEventListener('click', startPlexLogin);
window.addEventListener('focus', checkPendingPlexLoginSoon);
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) {
    checkPendingPlexLoginSoon();
  }
});

/* ── Render movie list ───────────────────────────────────────────────────── */
function renderList(filter = '') {
  const q = filter.toLowerCase();
  const filtered = allMovies.filter((movie) => (
    (movie.title.toLowerCase().includes(q) || String(movie.year).includes(q)) &&
    (statusFilter === 'all' || movie.status === statusFilter)
  ));

  movieList.innerHTML = '';
  filtered.forEach((movie) => {
    const li = document.createElement('li');
    li.dataset.id = movie.id;
    li.className = [
      'movie-item cursor-pointer px-4 py-3 transition-colors hover:bg-gray-800',
      movie.id === activeMovieId ? 'active' : '',
    ].join(' ');

    const badge = movie.status === 'downloaded'
      ? '<span class="status-downloaded rounded-full px-1.5 py-0.5 text-xs">done</span>'
      : '<span class="status-pending rounded-full px-1.5 py-0.5 text-xs">pending</span>';

    li.innerHTML = `
      <div class="flex items-start justify-between gap-2">
        <span class="line-clamp-2 text-sm font-medium leading-snug">${esc(movie.title)}</span>
        ${badge}
      </div>
      <span class="mt-0.5 block text-xs text-gray-500">${movie.year ?? ''}</span>
    `;
    li.addEventListener('click', () => selectMovie(movie.id));
    movieList.appendChild(li);
  });
}

function applyFilterSelection(value) {
  statusFilter = value;
  renderList(searchInput.value);
  filterMenu.classList.add('hidden');
}

/* ── Stats bar ───────────────────────────────────────────────────────────── */
function updateStats() {
  const done = allMovies.filter((movie) => movie.status === 'downloaded').length;
  const pending = allMovies.filter((movie) => movie.status === 'pending').length;
  statTotal.textContent = `${allMovies.length} movies`;
  statDone.textContent = `${done} done`;
  statPending.textContent = `${pending} pending`;
}

/* ── Load movies ─────────────────────────────────────────────────────────── */
async function loadMovies() {
  try {
    allMovies = await api('GET', '/api/movies');
    updateStats();
    renderList(searchInput.value);
  } catch (e) {
    showToast(`Failed to load movies: ${e.message}`, 'error');
  }
}

/* ── Version / Update ───────────────────────────────────────────────────── */
function setUpdateModalVisible(isVisible) {
  if (isVisible) {
    updateModal.classList.remove('hidden');
    updateModal.classList.add('flex');
  } else {
    updateModal.classList.add('hidden');
    updateModal.classList.remove('flex');
  }
}

function setSyncModalVisible(isVisible) {
  if (isVisible) {
    syncModal.classList.remove('hidden');
    syncModal.classList.add('flex');
  } else {
    syncModal.classList.add('hidden');
    syncModal.classList.remove('flex');
  }
}

function setSettingsModalVisible(isVisible) {
  if (isVisible) {
    settingsModal.classList.remove('hidden');
    settingsModal.classList.add('flex');
  } else {
    settingsModal.classList.add('hidden');
    settingsModal.classList.remove('flex');
  }
}

function mappingsToText(mappings) {
  return (mappings || []).map((m) => `${m.source} => ${m.target}`).join('\n');
}

function textToMappings(value) {
  const rows = String(value || '').split('\n').map((line) => line.trim()).filter(Boolean);
  const result = [];
  rows.forEach((row) => {
    const parts = row.split('=>');
    if (parts.length !== 2) return;
    result.push({ source: parts[0].trim(), target: parts[1].trim() });
  });
  return result;
}

function pathsToText(paths) {
  return (paths || []).join('\n');
}

function textToPaths(value) {
  return String(value || '').split('\n').map((line) => line.trim()).filter(Boolean);
}

async function loadSettingsModal() {
  const [settingsPayload, serversPayload] = await Promise.all([
    api('GET', '/api/settings'),
    api('GET', '/api/setup/plex/servers'),
  ]);

  settingsState = settingsPayload;
  availableServers = serversPayload.servers || [];
  const selectedIds = new Set((settingsPayload.selectedServers || []).map((s) => s.id));

  renderServerCheckboxes(settingsServers, availableServers, selectedIds, async () => {
    const serverIds = collectSelectedServerIds(settingsServers);
    const selectedServers = availableServers.filter((s) => serverIds.has(s.id));
    const librariesPayload = await api('POST', '/api/setup/plex/libraries', { servers: selectedServers });
    availableLibrariesByServer = librariesPayload.libraries || {};
    renderLibraryCheckboxes(
      settingsLibraries,
      availableLibrariesByServer,
      serverIds,
      settingsState.selectedLibraries || {},
      () => {},
    );
  });

  const initialServers = availableServers.filter((s) => selectedIds.has(s.id));
  const librariesPayload = await api('POST', '/api/setup/plex/libraries', { servers: initialServers });
  availableLibrariesByServer = librariesPayload.libraries || {};
  renderLibraryCheckboxes(
    settingsLibraries,
    availableLibrariesByServer,
    selectedIds,
    settingsPayload.selectedLibraries || {},
    () => {},
  );

  settingsPathMappings.value = mappingsToText(settingsPayload.pathMappings || []);
  settingsLibraryPaths.value = pathsToText(settingsPayload.libraryPaths || []);
  settingsAdvancedSearchDepth.value = settingsPayload.advanced?.searchDepth ?? 4;
  settingsAdvancedMaxDirs.value = settingsPayload.advanced?.maxSearchDirs ?? 20000;
}

async function saveSettingsModal() {
  const selectedServerIds = collectSelectedServerIds(settingsServers);
  const selectedServers = availableServers.filter((s) => selectedServerIds.has(s.id));
  const selectedLibraries = collectSelectedLibraries(settingsLibraries);

  const payload = {
    selectedServers,
    selectedLibraries,
    pathMappings: textToMappings(settingsPathMappings.value),
    libraryPaths: textToPaths(settingsLibraryPaths.value),
    advanced: {
      searchDepth: Number(settingsAdvancedSearchDepth.value || 4),
      maxSearchDirs: Number(settingsAdvancedMaxDirs.value || 20000),
    },
  };

  await api('POST', '/api/settings', payload);
}

function renderUpdateModal(status) {
  updateModalStatus.textContent = status.inProgress
    ? 'Updating app and collecting logs...'
    : status.error
      ? `Update failed: ${status.error}`
      : 'Update finished successfully.';
  updateModalLogs.textContent = (status.logs || []).join('\n');
  updateModalLogs.scrollTop = updateModalLogs.scrollHeight;
}

async function refreshUpdateModal() {
  const status = await api('GET', '/api/update/status');
  renderUpdateModal(status);

  if (!status.inProgress && updateStatusTimer) {
    clearInterval(updateStatusTimer);
    updateStatusTimer = null;
    btnUpdate.disabled = false;
    btnUpdate.textContent = 'Upgrade';
    if (setupBtnUpdate) {
      setupBtnUpdate.disabled = false;
      setupBtnUpdate.textContent = 'Upgrade';
    }
    if (!status.error) {
      setTimeout(() => window.location.reload(), 1500);
    }
  }

  return status;
}

function startUpdatePolling() {
  if (updateStatusTimer) return;
  updateStatusTimer = setInterval(async () => {
    try {
      const status = await refreshUpdateModal();
      if (!status.inProgress && status.error) {
        showToast(`Update failed: ${status.error}`, 'error');
      }
    } catch (e) {
      console.warn('Failed to refresh update status', e);
    }
  }, 1500);
}

async function checkForUpdate() {
  try {
    versionState = await api('GET', '/api/version');
    renderUpdateBanner();
  } catch (e) {
    console.warn('Version check failed', e);
  }
}

function renderUpdateBanner() {
  const inSetup = !setupReady;

  if (versionState.updating) {
    updateText.textContent = 'Updating app...';
    btnUpdate.disabled = true;
    btnUpdate.textContent = 'Upgrading';
    updateBanner.classList.remove('hidden');

    if (setupUpdateBanner && setupUpdateText && setupBtnUpdate) {
      setupUpdateText.textContent = 'Updating app...';
      setupBtnUpdate.disabled = true;
      setupBtnUpdate.textContent = 'Upgrading';
      setupUpdateBanner.classList.toggle('hidden', !inSetup);
    }
    return;
  }

  if (!versionState.updateAvailable) {
    updateBanner.classList.add('hidden');
    btnUpdate.disabled = false;
    btnUpdate.textContent = 'Upgrade';

    if (setupUpdateBanner && setupBtnUpdate) {
      setupBtnUpdate.disabled = false;
      setupBtnUpdate.textContent = 'Upgrade';
      setupUpdateBanner.classList.add('hidden');
    }
    return;
  }

  updateText.textContent = `Update available: ${versionState.current} -> ${versionState.latest}`;
  btnUpdate.disabled = false;
  btnUpdate.textContent = 'Upgrade';
  updateBanner.classList.remove('hidden');

  if (setupUpdateBanner && setupUpdateText && setupBtnUpdate) {
    setupUpdateText.textContent = `Update available: ${versionState.current} -> ${versionState.latest}`;
    setupBtnUpdate.disabled = false;
    setupBtnUpdate.textContent = 'Upgrade';
    setupUpdateBanner.classList.toggle('hidden', !inSetup);
  }
}

async function triggerUpdate(button = null) {
  if (button?.disabled) return;

  btnUpdate.disabled = true;
  btnUpdate.textContent = 'Starting...';
  if (setupBtnUpdate) {
    setupBtnUpdate.disabled = true;
    setupBtnUpdate.textContent = 'Starting...';
  }
  setUpdateModalVisible(true);
  updateModalStatus.textContent = 'Starting upgrade...';
  updateModalLogs.textContent = '';

  try {
    const res = await api('POST', '/api/update');
    if (!res.started) {
      showToast(res.detail || 'Update already running', 'error');
      updateModalStatus.textContent = res.detail || 'Update already running';
      await checkForUpdate();
      return;
    }

    showToast('Upgrade started. Showing server logs...');
    versionState.updating = true;
    renderUpdateBanner();
    await refreshUpdateModal();
    startUpdatePolling();
  } catch (e) {
    showToast(`Update failed to start: ${e.message}`, 'error');
    btnUpdate.disabled = false;
    btnUpdate.textContent = 'Upgrade';
    if (setupBtnUpdate) {
      setupBtnUpdate.disabled = false;
      setupBtnUpdate.textContent = 'Upgrade';
    }
    updateModalStatus.textContent = `Update failed to start: ${e.message}`;
  }
}

btnUpdate?.addEventListener('click', () => triggerUpdate(btnUpdate));
setupBtnUpdate?.addEventListener('click', () => triggerUpdate(setupBtnUpdate));
btnSettings?.addEventListener('click', async () => {
  try {
    await loadSettingsModal();
    setSettingsModalVisible(true);
  } catch (e) {
    showToast(`Failed to open settings: ${e.message}`, 'error');
  }
});
updateModalClose?.addEventListener('click', () => {
  if (updateStatusTimer) return;
  setUpdateModalVisible(false);
});
settingsModalClose?.addEventListener('click', () => setSettingsModalVisible(false));
settingsSave?.addEventListener('click', async () => {
  settingsSave.disabled = true;
  settingsSave.textContent = 'Saving...';
  try {
    await saveSettingsModal();
    await loadSetupState();
    showToast('Settings saved');
    setSettingsModalVisible(false);
  } catch (e) {
    showToast(`Settings save failed: ${e.message}`, 'error');
  } finally {
    settingsSave.disabled = false;
    settingsSave.textContent = 'Save settings';
  }
});
settingsReset?.addEventListener('click', resetAppToSetup);

/* ── Sync Plex ─────────────────────────────────────────────────────────── */
btnSync?.addEventListener('click', async () => {
  btnSync.disabled = true;
  syncStatus.textContent = 'Syncing…';
  setSyncModalVisible(true);
  syncModalStatus.textContent = 'Starting Plex sync...';
  syncModalLogs.textContent = '';

  try {
    const res = await api('POST', '/api/sync');
    if (!res.started) {
      syncModalStatus.textContent = res.detail || 'Sync already in progress';
      await refreshSyncModal();
      startSyncPolling();
      return;
    }

    await refreshSyncModal();
    startSyncPolling();
  } catch (e) {
    syncStatus.textContent = '';
    syncModalStatus.textContent = `Sync failed to start: ${e.message}`;
    showToast(`Sync failed: ${e.message}`, 'error');
    btnSync.disabled = false;
  }
});

function renderSyncModal(status) {
  syncModalStatus.textContent = status.inProgress
    ? 'Syncing Plex libraries and matching local folders...'
    : status.error
      ? `Sync failed: ${status.error}`
      : `Sync finished. ${status.synced ?? 0} movies matched.`;
  syncModalLogs.textContent = (status.logs || []).join('\n');
  syncModalLogs.scrollTop = syncModalLogs.scrollHeight;
}

async function refreshSyncModal() {
  const status = await api('GET', '/api/sync/status');
  renderSyncModal(status);

  if (!status.inProgress && syncStatusTimer) {
    clearInterval(syncStatusTimer);
    syncStatusTimer = null;
    btnSync.disabled = false;
    if (!status.error) {
      syncStatus.textContent = `Synced ${status.synced ?? 0} movies`;
      await loadMovies();
      showToast(`Synced ${status.synced ?? 0} movies from Plex`);
    } else {
      syncStatus.textContent = '';
      showToast(`Sync failed: ${status.error}`, 'error');
    }
  }

  return status;
}

function startSyncPolling() {
  if (syncStatusTimer) return;
  syncStatusTimer = setInterval(async () => {
    try {
      await refreshSyncModal();
    } catch (e) {
      console.warn('Failed to refresh sync status', e);
    }
  }, 1200);
}

syncModalClose?.addEventListener('click', () => {
  if (syncStatusTimer) return;
  setSyncModalVisible(false);
});

/* ── Select & search ─────────────────────────────────────────────────────── */
async function selectMovie(id) {
  activeMovieId = id;
  if (urlDownloadInput) {
    urlDownloadInput.value = '';
  }
  renderList(searchInput.value);

  rightEmpty.classList.add('hidden');
  rightContent.classList.add('hidden');
  rightLoading.classList.remove('hidden');

  try {
    const data = await api('GET', `/api/search/${id}`);
    renderResults(data.movie, data.results);
  } catch (e) {
    rightLoading.classList.add('hidden');
    rightEmpty.classList.remove('hidden');
    showToast(`Search failed: ${e.message}`, 'error');
  }
}

function nextPendingMovieId(currentMovieId) {
  if (!allMovies.length) return null;

  const startIdx = allMovies.findIndex((movie) => movie.id === currentMovieId);
  if (startIdx === -1) return null;

  for (let i = startIdx + 1; i < allMovies.length; i += 1) {
    if (allMovies[i].status === 'pending') return allMovies[i].id;
  }

  for (let i = 0; i < startIdx; i += 1) {
    if (allMovies[i].status === 'pending') return allMovies[i].id;
  }

  return null;
}

async function markDownloadedAndAdvance(movieId) {
  const movie = allMovies.find((item) => item.id === movieId);
  if (movie) {
    movie.status = 'downloaded';
  }

  updateStats();
  renderList(searchInput.value);

  const nextId = nextPendingMovieId(movieId);
  if (nextId) {
    await selectMovie(nextId);
  } else {
    showToast('All pending movies are completed');
  }
}

function renderResults(movie, results) {
  rightLoading.classList.add('hidden');
  movieHeading.textContent = `${movie.title} (${movie.year ?? '?'})`;
  resultsGrid.innerHTML = '';

  const searchQuery = `${movie.title} ${movie.year ?? ''} theme song`;
  const searchUrl = `https://www.youtube.com/results?search_query=${encodeURIComponent(searchQuery.trim())}`;
  if (youtubeSearchLink) {
    youtubeSearchLink.href = searchUrl;
    youtubeSearchLink.dataset.url = searchUrl;
  }

  if (!results.length) {
    resultsGrid.innerHTML = '<p class="col-span-3 text-gray-500">No results found.</p>';
  } else {
    results.forEach((video) => {
      const card = document.createElement('div');
      card.className = 'flex flex-col overflow-hidden rounded-xl border border-gray-800 bg-gray-900';
      card.innerHTML = `
        <div class="aspect-video w-full bg-black">
          <iframe
            class="h-full w-full"
            src="https://www.youtube.com/embed/${esc(video.videoId)}"
            title="${esc(video.title)}"
            frameborder="0"
            referrerpolicy="strict-origin-when-cross-origin"
            allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
            allowfullscreen>
          </iframe>
        </div>
        <div class="flex flex-1 flex-col gap-2 p-3">
          <p class="line-clamp-2 text-sm font-medium text-white leading-snug">${esc(video.title)}</p>
          <div class="mt-auto flex items-center justify-between text-xs text-gray-500">
            <span>${esc(video.channel || '')}</span>
            <span>${esc(video.duration || '')}</span>
          </div>
          <button
            class="btn-download mt-2 flex w-full items-center justify-center gap-2 rounded-lg bg-green-600 py-2 text-sm font-semibold transition-colors hover:bg-green-500"
            data-movie-id="${movie.id}"
            data-video-id="${esc(video.videoId)}">
            <svg class="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 13l4 4L19 7"/>
            </svg>
            Accept &amp; Download
          </button>
          <a
            class="btn-open-youtube mt-1 inline-flex w-full items-center justify-center rounded-lg border border-white/15 bg-white/5 py-2 text-xs font-semibold text-gray-200 transition hover:bg-white/10"
            href="https://www.youtube.com/watch?v=${esc(video.videoId)}"
            target="_blank"
            rel="noopener noreferrer"
          >
            Open in YouTube
          </a>
        </div>
      `;
      resultsGrid.appendChild(card);
    });
  }

  rightContent.classList.remove('hidden');

  resultsGrid.querySelectorAll('.btn-download').forEach((button) => {
    button.addEventListener('click', () => handleDownload(button));
  });

  resultsGrid.querySelectorAll('.btn-open-youtube').forEach((link) => {
    link.addEventListener('click', (event) => {
      const href = link.getAttribute('href');
      if (!href) {
        event.preventDefault();
        return;
      }
      event.preventDefault();
      window.open(href, '_blank', 'noopener,noreferrer');
    });
  });
}

async function handleDownload(button) {
  const movieId = String(button.dataset.movieId || '');
  const videoId = button.dataset.videoId;

  resultsGrid.querySelectorAll('.btn-download').forEach((downloadButton) => {
    downloadButton.disabled = true;
  });

  button.innerHTML = `
    <svg class="spinner h-4 w-4" fill="none" viewBox="0 0 24 24">
      <circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4"/>
      <path class="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"/>
    </svg>
    Downloading…
  `;

  try {
    await api('POST', '/api/download', { movie_id: movieId, video_id: videoId });
    showToast('Theme downloaded successfully!');

    await markDownloadedAndAdvance(movieId);

    button.innerHTML = `
      <svg class="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 13l4 4L19 7"/>
      </svg>
      Downloaded!
    `;
    button.classList.replace('bg-green-600', 'bg-gray-600');
    button.classList.replace('hover:bg-green-500', 'hover:bg-gray-600');
  } catch (e) {
    showToast(`Download failed: ${e.message}`, 'error');
    button.innerHTML = 'Accept &amp; Download';
    resultsGrid.querySelectorAll('.btn-download').forEach((downloadButton) => {
      downloadButton.disabled = false;
    });
  }
}

urlDownloadForm?.addEventListener('submit', async (event) => {
  event.preventDefault();
  if (!activeMovieId) {
    showToast('Select a movie first', 'error');
    return;
  }

  const url = urlDownloadInput.value.trim();
  if (!url) return;

  const originalText = urlDownloadSubmit.textContent;
  urlDownloadSubmit.disabled = true;
  urlDownloadSubmit.textContent = 'Downloading...';

  try {
    await api('POST', '/api/download-url', { movie_id: activeMovieId, url });
    showToast('Theme downloaded successfully!');
    urlDownloadInput.value = '';
    await markDownloadedAndAdvance(activeMovieId);
  } catch (e) {
    showToast(`Download failed: ${e.message}`, 'error');
  } finally {
    urlDownloadSubmit.disabled = false;
    urlDownloadSubmit.textContent = originalText;
  }
});

/* ── Search filter ───────────────────────────────────────────────────────── */
searchInput?.addEventListener('input', () => renderList(searchInput.value));

btnFilter?.addEventListener('click', () => {
  filterMenu.classList.toggle('hidden');
});

filterMenu?.querySelectorAll('.filter-option').forEach((button) => {
  button.addEventListener('click', () => applyFilterSelection(button.dataset.filter || 'all'));
});

youtubeSearchLink?.addEventListener('click', (event) => {
  const url = youtubeSearchLink.dataset.url || youtubeSearchLink.getAttribute('href') || '';
  if (!url || url === '#') {
    event.preventDefault();
    return;
  }

  event.preventDefault();
  window.open(url, '_blank', 'noopener,noreferrer');
});

/* ── Boot ────────────────────────────────────────────────────────────────── */
async function bootstrap() {
  const returnedLogin = getReturnLoginParams();
  if (returnedLogin) {
    pendingPlexLogin = returnedLogin;
    clearReturnLoginParams();
  }

  if (pendingPlexLogin) {
    setupStatus.textContent = 'Finishing Plex sign-in...';
    setupPlexLogin.disabled = true;
    clearPlexLoginPolling();
    plexLoginPollTimer = setInterval(() => {
      if (!pendingPlexLogin) return;
      pollPlexLogin(pendingPlexLogin.pinId, pendingPlexLogin.code);
    }, 2000);
    await pollPlexLogin(pendingPlexLogin.pinId, pendingPlexLogin.code);
  }

  const isReady = await loadSetupState();
  await checkForUpdate();
  if (!updatePollTimer) {
    updatePollTimer = setInterval(checkForUpdate, 5 * 60 * 1000);
  }

  if (!isReady) {
    return;
  }

  await enterApp();
}

bootstrap();
