/* ── State ───────────────────────────────────────────────────────────────── */
let allMovies = [];
let activeMovieId = null;

/* ── DOM refs ────────────────────────────────────────────────────────────── */
const movieList     = document.getElementById('movie-list');
const searchInput   = document.getElementById('search-input');
const btnFilter     = document.getElementById('btn-filter');
const filterMenu    = document.getElementById('filter-menu');
const btnSync       = document.getElementById('btn-sync');
const btnSettings   = document.getElementById('btn-settings');
const syncStatus    = document.getElementById('sync-status');
const syncModal     = document.getElementById('sync-modal');
const syncModalLogs = document.getElementById('sync-modal-logs');
const syncModalStatus = document.getElementById('sync-modal-status');
const syncModalClose = document.getElementById('sync-modal-close');
const rightEmpty    = document.getElementById('right-empty');
const rightLoading  = document.getElementById('right-loading');
const rightContent  = document.getElementById('right-content');
const movieHeading  = document.getElementById('movie-heading');
const resultsGrid   = document.getElementById('results-grid');
const statTotal     = document.getElementById('stat-total');
const statDone      = document.getElementById('stat-downloaded');
const statPending   = document.getElementById('stat-pending');
const toast         = document.getElementById('toast');
const updateBanner  = document.getElementById('update-banner');
const updateText    = document.getElementById('update-text');
const btnUpdate     = document.getElementById('btn-update');
const updateModal   = document.getElementById('update-modal');
const updateModalLogs = document.getElementById('update-modal-logs');
const updateModalStatus = document.getElementById('update-modal-status');
const updateModalClose = document.getElementById('update-modal-close');
const pathModal     = document.getElementById('path-modal');
const pathModalRows = document.getElementById('path-modal-rows');
const pathModalStatus = document.getElementById('path-modal-status');
const pathModalClose = document.getElementById('path-modal-close');
const pathModalAdd = document.getElementById('path-modal-add');
const pathModalSave = document.getElementById('path-modal-save');
const pathBrowserRoot = document.getElementById('path-browser-root');
const pathBrowserCurrent = document.getElementById('path-browser-current');
const pathBrowserList = document.getElementById('path-browser-list');
const pathBrowserUp = document.getElementById('path-browser-up');
const pathBrowserRefresh = document.getElementById('path-browser-refresh');
const pathBrowserUse = document.getElementById('path-browser-use');
const setupScreen   = document.getElementById('setup-screen');
const appShell      = document.getElementById('app-shell');
const setupForm     = document.getElementById('setup-form');
const setupStatus   = document.getElementById('setup-status');
const setupRadarrUrl = document.getElementById('setup-radarr-url');
const setupRadarrApiKey = document.getElementById('setup-radarr-api-key');
const setupMappingsSummary = document.getElementById('setup-mappings-summary');

/* ── Toast ───────────────────────────────────────────────────────────────── */
let toastTimer = null;
function showToast(msg, type = 'success') {
  clearTimeout(toastTimer);
  toast.textContent = msg;
  toast.className = [
    'fixed bottom-6 right-6 z-50 px-4 py-3 rounded-lg shadow-xl text-sm font-medium',
    type === 'success'
      ? 'bg-green-600 text-white'
      : 'bg-red-600 text-white',
  ].join(' ');
  toastTimer = setTimeout(() => { toast.classList.add('hidden'); }, 4000);
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

let libraryPathsState = [];
let pathBrowserState = { path: '', parent: '', roots: [], entries: [] };
let statusFilter = 'all';

function setPathModalVisible(isVisible) {
  if (isVisible) {
    pathModal.classList.remove('hidden');
    pathModal.classList.add('flex');
  } else {
    pathModal.classList.add('hidden');
    pathModal.classList.remove('flex');
  }
}

function renderPathBrowser() {
  pathBrowserCurrent.textContent = pathBrowserState.path || '/';

  const currentRoot = pathBrowserRoot.value;
  pathBrowserRoot.innerHTML = '';
  (pathBrowserState.roots || []).forEach(root => {
    const option = document.createElement('option');
    option.value = root;
    option.textContent = root;
    pathBrowserRoot.appendChild(option);
  });
  if (currentRoot && (pathBrowserState.roots || []).includes(currentRoot)) {
    pathBrowserRoot.value = currentRoot;
  }

  pathBrowserList.innerHTML = '';
  if (!(pathBrowserState.entries || []).length) {
    pathBrowserList.innerHTML = '<li class="rounded-lg border border-white/10 bg-black/20 px-3 py-2 text-xs text-gray-500">No subfolders found.</li>';
    return;
  }

  pathBrowserState.entries.forEach(entry => {
    const li = document.createElement('li');
    li.innerHTML = `<button type="button" class="w-full truncate rounded-lg border border-white/10 bg-black/20 px-3 py-2 text-left text-sm text-gray-100 transition hover:bg-black/40">${esc(entry.name)}</button>`;
    li.querySelector('button').addEventListener('click', () => browseFilesystem(entry.path));
    pathBrowserList.appendChild(li);
  });
}

async function browseFilesystem(path = '') {
  try {
    const query = path ? `?path=${encodeURIComponent(path)}` : '';
    pathBrowserState = await api('GET', `/api/fs/browse${query}`);
    renderPathBrowser();
    pathModalStatus.textContent = '';
  } catch (e) {
    pathModalStatus.textContent = `Browse failed: ${e.message}`;
    pathBrowserList.innerHTML = '<li class="rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-200">Unable to read folders.</li>';
  }
}

function createMappingRow(path = '') {
  const row = document.createElement('div');
  row.className = 'mapping-row grid gap-3 sm:grid-cols-[1fr_auto]';
  row.innerHTML = `
    <input type="text" class="mapping-path rounded-xl border border-white/10 bg-gray-950 px-4 py-3 text-sm text-white placeholder-gray-500 outline-none transition focus:border-indigo-400 focus:ring-2 focus:ring-indigo-400/40" placeholder="/mnt/movies" value="${esc(path)}" />
    <button type="button" class="mapping-remove rounded-xl border border-white/10 bg-white/5 px-4 py-3 text-sm font-semibold text-gray-200 transition hover:bg-white/10">Remove</button>
  `;
  row.querySelector('.mapping-remove').addEventListener('click', () => {
    row.remove();
    if (!pathModalRows.children.length) {
      pathModalRows.appendChild(createMappingRow());
    }
  });
  return row;
}

function renderMappingRows(mappings = []) {
  pathModalRows.innerHTML = '';
  if (!mappings.length) {
    pathModalRows.appendChild(createMappingRow());
    return;
  }
  mappings.forEach(path => pathModalRows.appendChild(createMappingRow(path)));
}

function collectMappings() {
  return Array.from(pathModalRows.querySelectorAll('.mapping-row'))
    .map(row => row.querySelector('.mapping-path').value.trim())
    .filter(path => path);
}

function renderMappingSummary(mappings = []) {
  setupMappingsSummary.innerHTML = '';
  if (!mappings.length) {
    setupMappingsSummary.innerHTML = '<p class="text-xs text-gray-500">No local library paths added yet.</p>';
    return;
  }

  mappings.forEach(path => {
    const pill = document.createElement('div');
    pill.className = 'flex items-center justify-between gap-3 rounded-2xl border border-white/10 bg-white/5 px-4 py-3';
    pill.innerHTML = `
      <div class="min-w-0">
        <p class="truncate text-sm font-semibold text-white">${esc(path)}</p>
        <p class="text-xs text-gray-500">Stored inside the app database</p>
      </div>
      <button type="button" class="rounded-lg border border-white/10 bg-white/5 px-3 py-2 text-xs font-semibold text-gray-100 transition hover:bg-white/10">Edit</button>
    `;
    pill.querySelector('button').addEventListener('click', () => {
      renderMappingRows(libraryPathsState);
      pathModalStatus.textContent = '';
      setPathModalVisible(true);
    });
    setupMappingsSummary.appendChild(pill);
  });
}

async function loadSetupState() {
  const state = await api('GET', '/api/setup/status');
  libraryPathsState = state.libraryPaths || [];
  if (!state.setupComplete) {
    setupRadarrUrl.value = state.radarrUrl || 'http://radarr:7878';
    setupRadarrApiKey.value = '';
    setupRadarrApiKey.placeholder = state.radarrApiKeySet ? 'Leave blank to keep existing key' : 'Enter your Radarr API key';
    renderMappingSummary(libraryPathsState);
    setAppVisible(false);
    setupStatus.textContent = 'Complete the fields below to finish setup.';
    return false;
  }

  setAppVisible(true);
  return true;
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
    setAppVisible(false);
    await loadSetupState();
    showToast('App reset. Complete setup to continue.');
  } catch (e) {
    showToast(`Reset failed: ${e.message}`, 'error');
  }
}

/* ── API helpers ─────────────────────────────────────────────────────────── */
async function api(method, path, body) {
  const opts = { method, headers: { 'Content-Type': 'application/json' } };
  if (body) opts.body = JSON.stringify(body);
  const res = await fetch(path, opts);
  if (!res.ok) {
    const err = await res.json().catch(() => ({ detail: res.statusText }));
    throw new Error(err.detail || res.statusText);
  }
  return res.json();
}

/* ── Render movie list ───────────────────────────────────────────────────── */
function renderList(filter = '') {
  const q = filter.toLowerCase();
  const filtered = allMovies.filter(m =>
    (m.title.toLowerCase().includes(q) || String(m.year).includes(q)) &&
    (statusFilter === 'all' || m.status === statusFilter)
  );

  movieList.innerHTML = '';
  filtered.forEach(m => {
    const li = document.createElement('li');
    li.dataset.id = m.id;
    li.className = [
      'movie-item cursor-pointer px-4 py-3 hover:bg-gray-800 transition-colors',
      m.id === activeMovieId ? 'active' : '',
    ].join(' ');

    const badge = m.status === 'downloaded'
      ? `<span class="status-downloaded text-xs px-1.5 py-0.5 rounded-full">done</span>`
      : `<span class="status-pending text-xs px-1.5 py-0.5 rounded-full">pending</span>`;

    li.innerHTML = `
      <div class="flex items-start justify-between gap-2">
        <span class="text-sm font-medium leading-snug line-clamp-2">${esc(m.title)}</span>
        ${badge}
      </div>
      <span class="text-xs text-gray-500 mt-0.5 block">${m.year ?? ''}</span>
    `;
    li.addEventListener('click', () => selectMovie(m.id));
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
  const done    = allMovies.filter(m => m.status === 'downloaded').length;
  const pending = allMovies.filter(m => m.status === 'pending').length;
  statTotal.textContent   = `${allMovies.length} movies`;
  statDone.textContent    = `${done} done`;
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

document.getElementById('setup-open-mappings')?.addEventListener('click', () => {
  renderMappingRows(libraryPathsState);
  pathModalStatus.textContent = '';
  setPathModalVisible(true);
  browseFilesystem();
});

pathModalClose?.addEventListener('click', () => setPathModalVisible(false));

pathModalAdd?.addEventListener('click', () => {
  pathModalRows.appendChild(createMappingRow());
});

pathModalSave?.addEventListener('click', () => {
  const paths = collectMappings();
  libraryPathsState = paths;
  renderMappingSummary(libraryPathsState);
  pathModalStatus.textContent = 'Paths updated. Save setup to apply them.';
  setPathModalVisible(false);
});

pathBrowserUp?.addEventListener('click', () => {
  if (!pathBrowserState.parent) return;
  browseFilesystem(pathBrowserState.parent);
});

pathBrowserRefresh?.addEventListener('click', () => {
  browseFilesystem(pathBrowserState.path);
});

pathBrowserRoot?.addEventListener('change', () => {
  const root = pathBrowserRoot.value;
  if (root) browseFilesystem(root);
});

pathBrowserUse?.addEventListener('click', () => {
  const selected = (pathBrowserState.path || '').trim();
  if (!selected) return;

  const exists = Array.from(pathModalRows.querySelectorAll('.mapping-path'))
    .some(input => input.value.trim().replace(/\/$/, '') === selected.replace(/\/$/, ''));

  if (!exists) {
    pathModalRows.appendChild(createMappingRow(selected));
    pathModalStatus.textContent = `Added ${selected}`;
  } else {
    pathModalStatus.textContent = 'That path is already in the list.';
  }
});

setupForm?.addEventListener('submit', async (event) => {
  event.preventDefault();
  setupStatus.textContent = 'Saving setup...';

  try {
    const payload = {
      radarr_url: setupRadarrUrl.value.trim(),
      radarr_api_key: setupRadarrApiKey.value.trim(),
      library_paths: libraryPathsState,
    };
    const state = await api('POST', '/api/setup', payload);
    setupStatus.textContent = 'Setup saved. Loading app...';
    setAppVisible(true);
    versionState = { current: '', latest: '', updateAvailable: false, updating: false };
    await loadMovies();
    await checkForUpdate();
    if (!updatePollTimer) {
      updatePollTimer = setInterval(checkForUpdate, 5 * 60 * 1000);
    }
    if (state.setupComplete) {
      showToast('Setup complete');
    }
  } catch (e) {
    setupStatus.textContent = '';
    showToast(`Setup failed: ${e.message}`, 'error');
  }
});

/* ── Version / Update ───────────────────────────────────────────────────── */
let versionState = { current: '', latest: '', updateAvailable: false, updating: false };
let updatePollTimer = null;
let updateStatusTimer = null;
let syncStatusTimer = null;

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

function renderSyncModal(status) {
  syncModalStatus.textContent = status.inProgress
    ? 'Syncing movies and matching local folders...'
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
      showToast(`Synced ${status.synced ?? 0} movies from Radarr`);
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
  if (versionState.updating) {
    updateText.textContent = 'Updating app...';
    btnUpdate.disabled = true;
    btnUpdate.textContent = 'Upgrading';
    updateBanner.classList.remove('hidden');
    return;
  }

  if (!versionState.updateAvailable) {
    updateBanner.classList.add('hidden');
    btnUpdate.disabled = false;
    btnUpdate.textContent = 'Upgrade';
    return;
  }

  updateText.textContent = `Update available: ${versionState.current} -> ${versionState.latest}`;
  btnUpdate.disabled = false;
  btnUpdate.textContent = 'Upgrade';
  updateBanner.classList.remove('hidden');
}

async function triggerUpdate() {
  if (btnUpdate.disabled) return;

  btnUpdate.disabled = true;
  btnUpdate.textContent = 'Starting...';
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
    updateModalStatus.textContent = `Update failed to start: ${e.message}`;
  }
}

btnUpdate?.addEventListener('click', triggerUpdate);
btnSettings?.addEventListener('click', resetAppToSetup);
updateModalClose?.addEventListener('click', () => {
  if (updateStatusTimer) return;
  setUpdateModalVisible(false);
});

/* ── Sync Radarr ─────────────────────────────────────────────────────────── */
btnSync.addEventListener('click', async () => {
  btnSync.disabled = true;
  syncStatus.textContent = 'Syncing…';
  setSyncModalVisible(true);
  syncModalStatus.textContent = 'Starting sync...';
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

syncModalClose?.addEventListener('click', () => {
  if (syncStatusTimer) return;
  setSyncModalVisible(false);
});

/* ── Select & search ─────────────────────────────────────────────────────── */
async function selectMovie(id) {
  activeMovieId = id;
  renderList(searchInput.value); // re-render to update active highlight

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

/* ── Render YouTube results ──────────────────────────────────────────────── */
function renderResults(movie, results) {
  rightLoading.classList.add('hidden');
  movieHeading.textContent = `${movie.title} (${movie.year ?? '?'})`;
  resultsGrid.innerHTML = '';

  if (!results.length) {
    resultsGrid.innerHTML = '<p class="text-gray-500 col-span-3">No results found.</p>';
  } else {
    results.forEach(v => {
      const card = document.createElement('div');
      card.className = 'bg-gray-900 rounded-xl overflow-hidden border border-gray-800 flex flex-col';
      card.innerHTML = `
        <div class="aspect-video w-full bg-black">
          <iframe
            class="w-full h-full"
            src="https://www.youtube.com/embed/${esc(v.videoId)}"
            title="${esc(v.title)}"
            frameborder="0"
            allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
            allowfullscreen>
          </iframe>
        </div>
        <div class="p-3 flex flex-col gap-2 flex-1">
          <p class="text-sm font-medium text-white leading-snug line-clamp-2">${esc(v.title)}</p>
          <div class="flex items-center justify-between text-xs text-gray-500 mt-auto">
            <span>${esc(v.channel || '')}</span>
            <span>${esc(v.duration || '')}</span>
          </div>
          <button
            class="btn-download mt-2 w-full py-2 rounded-lg bg-green-600 hover:bg-green-500
                   text-sm font-semibold transition-colors flex items-center justify-center gap-2"
            data-movie-id="${movie.id}"
            data-video-id="${esc(v.videoId)}">
            <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                    d="M5 13l4 4L19 7"/>
            </svg>
            Accept &amp; Download
          </button>
        </div>
      `;
      resultsGrid.appendChild(card);
    });
  }

  rightContent.classList.remove('hidden');

  // Attach download handlers
  resultsGrid.querySelectorAll('.btn-download').forEach(btn => {
    btn.addEventListener('click', () => handleDownload(btn));
  });
}

/* ── Download ────────────────────────────────────────────────────────────── */
async function handleDownload(btn) {
  const movieId = parseInt(btn.dataset.movieId, 10);
  const videoId = btn.dataset.videoId;

  // Disable all download buttons in grid during operation
  resultsGrid.querySelectorAll('.btn-download').forEach(b => { b.disabled = true; });

  btn.innerHTML = `
    <svg class="spinner w-4 h-4" fill="none" viewBox="0 0 24 24">
      <circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4"/>
      <path class="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"/>
    </svg>
    Downloading…
  `;

  try {
    await api('POST', '/api/download', { movie_id: movieId, video_id: videoId });
    showToast('Theme downloaded successfully!');

    // Update local state
    const m = allMovies.find(x => x.id === movieId);
    if (m) m.status = 'downloaded';
    updateStats();
    renderList(searchInput.value);

    btn.innerHTML = `
      <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 13l4 4L19 7"/>
      </svg>
      Downloaded!
    `;
    btn.classList.replace('bg-green-600', 'bg-gray-600');
    btn.classList.replace('hover:bg-green-500', 'hover:bg-gray-600');
  } catch (e) {
    showToast(`Download failed: ${e.message}`, 'error');
    btn.innerHTML = 'Accept &amp; Download';
    resultsGrid.querySelectorAll('.btn-download').forEach(b => { b.disabled = false; });
  }
}

/* ── Search filter ───────────────────────────────────────────────────────── */
searchInput.addEventListener('input', () => renderList(searchInput.value));

btnFilter?.addEventListener('click', () => {
  filterMenu.classList.toggle('hidden');
});

filterMenu?.querySelectorAll('.filter-option').forEach(btn => {
  btn.addEventListener('click', () => applyFilterSelection(btn.dataset.filter || 'all'));
});

/* ── Escape HTML ─────────────────────────────────────────────────────────── */
function esc(str) {
  return String(str ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/* ── Boot ────────────────────────────────────────────────────────────────── */
async function bootstrap() {
  const isReady = await loadSetupState();
  if (!isReady) return;

  await loadMovies();
  await checkForUpdate();

  if (!updatePollTimer) {
    updatePollTimer = setInterval(checkForUpdate, 5 * 60 * 1000);
  }
}

bootstrap();
