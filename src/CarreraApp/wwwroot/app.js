'use strict';

const TOKEN_STORAGE_KEY = 'carrera_auth_token';
const POLL_INTERVAL_MS = 2000;

const video = document.getElementById('camera-feed');
const btnCapture = document.getElementById('btn-capture');
const pendingQueue = document.getElementById('pending-queue');
const authBanner = document.getElementById('auth-banner');
const authMessage = document.getElementById('auth-message');
const tokenPanel = document.getElementById('token-panel');
const tokenInput = document.getElementById('token-input');
const btnSaveToken = document.getElementById('btn-save-token');
const btnToggleToken = document.getElementById('btn-toggle-token');
const connectionStatus = document.getElementById('connection-status');
const statusBanner = document.getElementById('status-banner');

let authToken = '';
let isAuthRejected = false;
let tempIdSequence = 0;
let pollingTimerId = null;
let isPolling = false;
let pendingImmediatePoll = false;

const uploadingTempIds = new Set();
const discardedTempIds = new Set();
const retryBlobs = new Map();

function readStoredToken() {
    try {
        return (sessionStorage.getItem(TOKEN_STORAGE_KEY) || '').trim();
    } catch {
        return '';
    }
}

function writeStoredToken(token) {
    try {
        if (token) {
            sessionStorage.setItem(TOKEN_STORAGE_KEY, token);
        } else {
            sessionStorage.removeItem(TOKEN_STORAGE_KEY);
        }
    } catch {
        // Ignorar restricciones de almacenamiento privado del navegador
    }
}

function updateConnectionBadge(text, stateClass) {
    if (!connectionStatus) return;
    connectionStatus.textContent = text;
    connectionStatus.className = `status-badge ${stateClass}`;
}

function showStatusBanner(message, level = 'info') {
    if (!statusBanner) return;
    if (!message) {
        statusBanner.hidden = true;
        statusBanner.textContent = '';
        return;
    }
    statusBanner.textContent = message;
    statusBanner.className = `status-banner status-${level}`;
    statusBanner.hidden = false;
}

function handleUnauthorized(message) {
    isAuthRejected = true;
    clearPollTimer();
    const displayMessage =
        message ||
        'Credencial inválida o la sesión cambió (401). Reescanea el código QR de la mesa o actualiza el token.';

    if (authMessage) {
        authMessage.textContent = displayMessage;
    }
    if (authBanner) {
        authBanner.hidden = false;
    }
    if (tokenPanel) {
        tokenPanel.hidden = false;
    }
    updateConnectionBadge('No autorizado (401)', 'status-error');
}

function clearAuthError() {
    isAuthRejected = false;
    if (authBanner) {
        authBanner.hidden = true;
    }
}

function initAuthToken() {
    const params = new URLSearchParams(window.location.search || '');
    const urlToken = (params.get('token') || '').trim();

    if (urlToken) {
        authToken = urlToken;
        writeStoredToken(authToken);
    } else {
        authToken = readStoredToken();
    }

    if (tokenInput) {
        tokenInput.value = authToken;
    }

    if (!authToken) {
        handleUnauthorized('Falta el token de sesión. Reescanea el código QR de la mesa o ingresa el token manualmente.');
        updateConnectionBadge('Sin token de sesión', 'status-error');
    } else {
        clearAuthError();
        if (tokenPanel) {
            tokenPanel.hidden = true;
        }
        updateConnectionBadge('Verificando sesión...', 'status-pending');
    }
}

function saveManualToken() {
    const candidate = tokenInput ? tokenInput.value.trim() : '';
    if (!candidate) {
        showStatusBanner('Ingresa un token de sesión válido.', 'warning');
        if (tokenInput) tokenInput.focus();
        return;
    }

    authToken = candidate;
    writeStoredToken(authToken);
    clearAuthError();
    if (tokenPanel) {
        tokenPanel.hidden = true;
    }
    showStatusBanner('Validando nuevo token de sesión...', 'info');
    updateConnectionBadge('Validando sesión...', 'status-pending');
    triggerImmediatePoll();
}

async function parseResponseJsonSafe(response) {
    try {
        const text = await response.text();
        if (!text) return {};
        return JSON.parse(text);
    } catch {
        return {};
    }
}

function extractErrorMessage(res, fallbackMessage) {
    if (res && res.data) {
        if (typeof res.data.mensaje === 'string' && res.data.mensaje.trim()) {
            return res.data.mensaje.trim();
        }
        if (typeof res.data.error === 'string' && res.data.error.trim()) {
            return res.data.error.trim();
        }
    }
    if (res && res.status === 413) {
        return 'La imagen supera el tamaño máximo permitido (10 MB).';
    }
    if (res && res.status > 0) {
        return `${fallbackMessage} (HTTP ${res.status})`;
    }
    return fallbackMessage;
}

async function apiFetch(url, options = {}) {
    if (!authToken) {
        const msg = 'Falta el token de sesión. Reescanea el código QR de la mesa o ingresa el token manualmente.';
        handleUnauthorized(msg);
        return {
            ok: false,
            status: 401,
            unauthorized: true,
            networkError: false,
            data: { mensaje: msg }
        };
    }

    const headers = new Headers(options.headers || {});
    headers.set('X-Auth-Token', authToken);

    try {
        const response = await fetch(url, { ...options, headers });
        const data = await parseResponseJsonSafe(response);

        if (response.status === 401) {
            const msg =
                (typeof data.mensaje === 'string' && data.mensaje.trim()) ||
                'Credencial inválida o la sesión cambió (401). Reescanea el código QR de la mesa o actualiza el token.';
            handleUnauthorized(msg);
            return {
                ok: false,
                status: 401,
                unauthorized: true,
                networkError: false,
                data,
                response
            };
        }

        if (response.ok) {
            clearAuthError();
            updateConnectionBadge('Conectado', 'status-ok');
        }

        return {
            ok: response.ok,
            status: response.status,
            unauthorized: false,
            networkError: false,
            data,
            response
        };
    } catch (error) {
        return {
            ok: false,
            status: 0,
            unauthorized: false,
            networkError: true,
            data: { mensaje: 'Error de red al conectar con la mesa de cronometraje.' },
            error
        };
    }
}

function generateTempId() {
    if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
        return crypto.randomUUID();
    }
    tempIdSequence += 1;
    return `${Date.now()}-${tempIdSequence}`;
}

async function initCamera() {
    if (!navigator.mediaDevices || typeof navigator.mediaDevices.getUserMedia !== 'function') {
        showStatusBanner('La cámara requiere conexión segura HTTPS (puerto 5001) o permisos habilitados en el navegador.', 'warning');
        return;
    }

    try {
        let stream;
        try {
            stream = await navigator.mediaDevices.getUserMedia({
                video: { facingMode: 'environment' }
            });
        } catch {
            stream = await navigator.mediaDevices.getUserMedia({ video: true });
        }
        video.srcObject = stream;
    } catch (error) {
        console.error('Error al acceder a la cámara:', error);
        showStatusBanner('Por favor, permite el acceso a la cámara en tu navegador para capturar llegadas.', 'error');
    }
}

function capturePhotoFrame() {
    if (!video || !video.videoWidth || !video.videoHeight) {
        showStatusBanner('La cámara aún no está lista.', 'warning');
        return;
    }

    // Canvas dedicado por cada pulsación para evitar mezcla/sobreescritura de frames en ráfagas rápidas
    const captureCanvas = document.createElement('canvas');
    captureCanvas.width = video.videoWidth;
    captureCanvas.height = video.videoHeight;
    const context = captureCanvas.getContext('2d');
    if (!context) {
        showStatusBanner('No se pudo inicializar el lienzo de captura.', 'error');
        return;
    }

    context.drawImage(video, 0, 0, captureCanvas.width, captureCanvas.height);
    const tempId = generateTempId();

    captureCanvas.toBlob((blob) => {
        captureCanvas.width = 0;
        captureCanvas.height = 0;

        if (!blob) {
            showStatusBanner('No se pudo codificar la fotografía capturada.', 'error');
            return;
        }

        const imageUrl = URL.createObjectURL(blob);
        addPendingItemToUI(tempId, imageUrl);
        void sendPhotoToServer(tempId, blob);
    }, 'image/jpeg', 0.85);
}

function setItemStatus(tempId, message, isError, isSuccess = false) {
    const statusEl = document.getElementById(`status-${tempId}`);
    if (!statusEl) return;
    statusEl.textContent = message || '';
    statusEl.className = 'item-status';
    if (isError) {
        statusEl.classList.add('is-error');
    } else if (isSuccess) {
        statusEl.classList.add('is-success');
    }
}

function setItemBusy(item, busy) {
    if (!item) return;
    const tempId = item.dataset.tempId;
    const inputDorsal = document.getElementById(`input-${tempId}`);
    const btnOk = document.getElementById(`btn-assign-${tempId}`);
    const btnDiscard = document.getElementById(`btn-discard-${tempId}`);
    const btnRetry = document.getElementById(`btn-retry-${tempId}`);

    if (busy) {
        item.dataset.busy = 'true';
        if (inputDorsal) inputDorsal.disabled = true;
        if (btnOk) btnOk.disabled = true;
        if (btnDiscard) btnDiscard.disabled = true;
        if (btnRetry) btnRetry.disabled = true;
    } else {
        delete item.dataset.busy;
    }
}

function restoreItemControls(item) {
    if (!item) return;
    const tempId = item.dataset.tempId;
    const inputDorsal = document.getElementById(`input-${tempId}`);
    const btnOk = document.getElementById(`btn-assign-${tempId}`);
    const btnDiscard = document.getElementById(`btn-discard-${tempId}`);
    const btnRetry = document.getElementById(`btn-retry-${tempId}`);

    if (inputDorsal) inputDorsal.disabled = false;
    if (btnOk) btnOk.disabled = !item.dataset.serverId;
    if (btnDiscard) btnDiscard.disabled = false;
    if (btnRetry) btnRetry.disabled = false;
}

function addPendingItemToUI(tempId, imageUrl) {
    const div = document.createElement('div');
    div.className = 'pending-item';
    div.id = `item-${tempId}`;
    div.dataset.tempId = tempId;
    div.dataset.blobUrl = imageUrl;

    const row = document.createElement('div');
    row.className = 'item-main';

    const img = document.createElement('img');
    img.src = imageUrl;
    img.alt = 'Corredor';

    const inputDorsal = document.createElement('input');
    inputDorsal.type = 'number';
    inputDorsal.min = '1';
    inputDorsal.step = '1';
    inputDorsal.inputMode = 'numeric';
    inputDorsal.placeholder = 'Dorsal';
    inputDorsal.id = `input-${tempId}`;
    inputDorsal.addEventListener('keydown', (event) => {
        if (event.key === 'Enter') {
            event.preventDefault();
            void assignRunner(tempId);
        }
    });

    const btnOk = document.createElement('button');
    btnOk.type = 'button';
    btnOk.className = 'btn-assign';
    btnOk.id = `btn-assign-${tempId}`;
    btnOk.textContent = 'OK';
    btnOk.disabled = true;
    btnOk.addEventListener('click', () => {
        void assignRunner(tempId);
    });

    const btnRetry = document.createElement('button');
    btnRetry.type = 'button';
    btnRetry.className = 'btn-retry';
    btnRetry.id = `btn-retry-${tempId}`;
    btnRetry.textContent = 'Reintentar';
    btnRetry.hidden = true;
    btnRetry.addEventListener('click', () => {
        void retryUpload(tempId);
    });

    const btnDiscard = document.createElement('button');
    btnDiscard.type = 'button';
    btnDiscard.className = 'btn-discard';
    btnDiscard.id = `btn-discard-${tempId}`;
    btnDiscard.textContent = 'X';
    btnDiscard.addEventListener('click', () => {
        void discardPhoto(tempId);
    });

    row.appendChild(img);
    row.appendChild(inputDorsal);
    row.appendChild(btnOk);
    row.appendChild(btnRetry);
    row.appendChild(btnDiscard);

    const statusEl = document.createElement('div');
    statusEl.className = 'item-status';
    statusEl.id = `status-${tempId}`;
    statusEl.textContent = 'Subiendo foto...';

    div.appendChild(row);
    div.appendChild(statusEl);

    pendingQueue.insertBefore(div, pendingQueue.firstChild);
}

async function sendPhotoToServer(tempId, imageBlob) {
    uploadingTempIds.add(tempId);

    const formData = new FormData();
    formData.append('foto', imageBlob, `img_${tempId}.jpg`);
    formData.append('tempId', tempId);

    const btnRetryExisting = document.getElementById(`btn-retry-${tempId}`);
    if (btnRetryExisting) {
        btnRetryExisting.disabled = true;
    }

    try {
        const res = await apiFetch('/api/upload-photo', {
            method: 'POST',
            body: formData
        });

        if (discardedTempIds.has(tempId)) {
            if (res.ok && res.data && res.data.idFoto) {
                await apiFetch('/api/discard-photo', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ idFoto: res.data.idFoto })
                });
            }
            retryBlobs.delete(tempId);
            discardedTempIds.delete(tempId);
            return;
        }

        const item = document.getElementById(`item-${tempId}`);
        if (!item) return;

        const btnOk = document.getElementById(`btn-assign-${tempId}`);
        const btnRetry = document.getElementById(`btn-retry-${tempId}`);

        if (res.ok && res.data && res.data.idFoto) {
            retryBlobs.delete(tempId);
            item.dataset.serverId = res.data.idFoto;
            item.classList.remove('item-error');
            if (btnRetry) {
                btnRetry.hidden = true;
                btnRetry.disabled = false;
            }
            if (btnOk) {
                btnOk.disabled = false;
            }
            setItemStatus(tempId, 'Lista para asignar dorsal.', false);
            return;
        }

        const errMsg = extractErrorMessage(res, 'No se pudo subir la fotografía a la mesa.');
        item.classList.remove('item-success');
        item.classList.add('item-error');
        if (btnOk) btnOk.disabled = true;
        setItemStatus(tempId, errMsg, true);
        showStatusBanner(errMsg, 'error');

        const esReintentable = res.unauthorized || res.networkError || res.status >= 500;
        if (esReintentable) {
            retryBlobs.set(tempId, imageBlob);
            if (btnRetry) {
                btnRetry.hidden = false;
                btnRetry.disabled = false;
            }
        } else {
            retryBlobs.delete(tempId);
            if (btnRetry) {
                btnRetry.hidden = true;
            }
        }
    } finally {
        uploadingTempIds.delete(tempId);
    }
}

async function retryUpload(tempId) {
    if (uploadingTempIds.has(tempId)) return;
    const blob = retryBlobs.get(tempId);
    if (!blob) {
        setItemStatus(tempId, 'La imagen ya no está disponible para reintento.', true);
        return;
    }

    const item = document.getElementById(`item-${tempId}`);
    if (item) {
        item.classList.remove('item-error');
    }
    setItemStatus(tempId, 'Reintentando subida...', false);
    await sendPhotoToServer(tempId, blob);
}

async function assignRunner(tempId) {
    const item = document.getElementById(`item-${tempId}`);
    if (!item || item.dataset.busy === 'true' || item.dataset.assigned === 'true') {
        return;
    }

    const inputDorsal = document.getElementById(`input-${tempId}`);
    const btnOk = document.getElementById(`btn-assign-${tempId}`);
    const btnDiscard = document.getElementById(`btn-discard-${tempId}`);
    const serverId = item.dataset.serverId;

    if (!serverId) {
        setItemStatus(tempId, 'La foto aún no se ha confirmado en la mesa. Espera un momento.', true);
        return;
    }

    const rawValue = inputDorsal ? inputDorsal.value.trim() : '';
    if (!/^\d+$/.test(rawValue)) {
        setItemStatus(tempId, 'Ingresa un número de dorsal entero positivo.', true);
        showStatusBanner('Ingresa un número de dorsal entero positivo.', 'warning');
        return;
    }

    const dorsal = Number.parseInt(rawValue, 10);
    if (!Number.isSafeInteger(dorsal) || dorsal <= 0) {
        setItemStatus(tempId, 'El dorsal debe ser mayor a 0.', true);
        showStatusBanner('El dorsal debe ser un número entero positivo.', 'warning');
        return;
    }

    setItemBusy(item, true);
    item.classList.remove('item-error');
    if (inputDorsal) {
        inputDorsal.style.backgroundColor = '';
    }
    setItemStatus(tempId, 'Registrando dorsal...', false);

    try {
        const res = await apiFetch('/api/assign-dorsal', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ idFoto: serverId, dorsal })
        });

        if (res.ok) {
            item.dataset.assigned = 'true';
            item.classList.remove('item-error');
            item.classList.add('item-success');
            if (inputDorsal) {
                inputDorsal.style.backgroundColor = '#d4edda';
                inputDorsal.disabled = true;
            }
            if (btnOk) btnOk.disabled = true;
            if (btnDiscard) btnDiscard.disabled = true;

            const tiempo = res.data && res.data.tiempoFormateado ? ` (${res.data.tiempoFormateado})` : '';
            const okMsg = (res.data && res.data.mensaje) || `Dorsal #${dorsal} registrado${tiempo}.`;
            setItemStatus(tempId, okMsg, false, true);
            showStatusBanner(`Dorsal #${dorsal} guardado${tiempo}.`, 'success');
        } else {
            const errMsg = extractErrorMessage(res, 'Error al procesar dorsal.');
            item.classList.remove('item-success');
            item.classList.add('item-error');
            if (inputDorsal) {
                inputDorsal.style.backgroundColor = '';
            }
            setItemStatus(tempId, errMsg, true);
            showStatusBanner(errMsg, 'error');
        }
    } finally {
        delete item.dataset.busy;
        if (item.dataset.assigned !== 'true') {
            restoreItemControls(item);
        }
    }
}

async function discardPhoto(tempId) {
    const item = document.getElementById(`item-${tempId}`);
    if (!item || item.dataset.busy === 'true' || item.dataset.assigned === 'true') {
        return;
    }

    const serverId = item.dataset.serverId;
    if (!serverId) {
        if (uploadingTempIds.has(tempId)) {
            discardedTempIds.add(tempId);
        }
        eliminarItemUIRecuperandoMemoria(item, tempId);
        return;
    }

    setItemBusy(item, true);
    setItemStatus(tempId, 'Descartando...', false);

    try {
        const res = await apiFetch('/api/discard-photo', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ idFoto: serverId })
        });

        if (res.ok || res.status === 404) {
            eliminarItemUIRecuperandoMemoria(item, tempId);
            return;
        }

        const errMsg = extractErrorMessage(res, 'No se pudo descartar la fotografía.');
        item.classList.remove('item-success');
        item.classList.add('item-error');
        setItemStatus(tempId, errMsg, true);
        showStatusBanner(errMsg, 'error');
    } finally {
        if (item.isConnected) {
            delete item.dataset.busy;
            restoreItemControls(item);
        }
    }
}

function eliminarItemUIRecuperandoMemoria(item, tempIdOverride) {
    if (!item) return;
    const tempId = tempIdOverride || item.dataset.tempId;
    if (tempId) {
        retryBlobs.delete(tempId);
    }
    if (item.dataset.blobUrl) {
        URL.revokeObjectURL(item.dataset.blobUrl);
    }
    item.remove();
}

function syncPendingQueueWithServer(pendingIds) {
    if (!Array.isArray(pendingIds)) return;

    document.querySelectorAll('.pending-item').forEach((item) => {
        const serverId = item.dataset.serverId;
        if (serverId && item.dataset.busy !== 'true' && !pendingIds.includes(serverId)) {
            eliminarItemUIRecuperandoMemoria(item);
        }
    });
}

function clearPollTimer() {
    if (pollingTimerId !== null) {
        clearTimeout(pollingTimerId);
        pollingTimerId = null;
    }
}

function scheduleNextPoll(delayMs = POLL_INTERVAL_MS) {
    clearPollTimer();
    if (!authToken || isAuthRejected) {
        return;
    }

    pollingTimerId = setTimeout(() => {
        pollingTimerId = null;
        void pollPendingPhotos();
    }, delayMs);
}

function triggerImmediatePoll() {
    clearPollTimer();
    if (!authToken || isAuthRejected) {
        return;
    }
    if (isPolling) {
        pendingImmediatePoll = true;
        return;
    }
    void pollPendingPhotos();
}

async function pollPendingPhotos() {
    if (isPolling || !authToken || isAuthRejected) {
        return;
    }

    isPolling = true;
    try {
        const res = await apiFetch('/api/pending-photos', { method: 'GET' });
        if (res.ok) {
            syncPendingQueueWithServer(res.data);
        } else if (res.networkError) {
            updateConnectionBadge('Sin conexión (reintentando)', 'status-pending');
        }
    } finally {
        isPolling = false;
        if (pendingImmediatePoll) {
            pendingImmediatePoll = false;
            scheduleNextPoll(0);
        } else {
            scheduleNextPoll(POLL_INTERVAL_MS);
        }
    }
}

function cleanupResources() {
    clearPollTimer();
    document.querySelectorAll('.pending-item').forEach((item) => {
        if (item.dataset.blobUrl) {
            URL.revokeObjectURL(item.dataset.blobUrl);
        }
    });
    retryBlobs.clear();
}

btnCapture.addEventListener('click', capturePhotoFrame);
if (btnSaveToken) {
    btnSaveToken.addEventListener('click', saveManualToken);
}
if (tokenInput) {
    tokenInput.addEventListener('keydown', (event) => {
        if (event.key === 'Enter') {
            event.preventDefault();
            saveManualToken();
        }
    });
}
if (btnToggleToken && tokenPanel) {
    btnToggleToken.addEventListener('click', () => {
        tokenPanel.hidden = !tokenPanel.hidden;
        if (!tokenPanel.hidden && tokenInput) {
            tokenInput.focus();
        }
    });
}

document.addEventListener('visibilitychange', () => {
    if (!document.hidden && authToken && !isAuthRejected) {
        triggerImmediatePoll();
    }
});

window.addEventListener('pagehide', cleanupResources);

initAuthToken();
void initCamera();
if (authToken && !isAuthRejected) {
    void pollPendingPhotos();
}
