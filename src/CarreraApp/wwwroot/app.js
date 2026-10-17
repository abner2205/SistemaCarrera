const video = document.getElementById('camera-feed');
const canvas = document.getElementById('canvas');
const btnCapture = document.getElementById('btn-capture');
const pendingQueue = document.getElementById('pending-queue');

// 1. Inicializar la cámara (con respaldo de cámara frontal/webcam si environment falla)
async function initCamera() {
    try {
        let stream;
        try {
            stream = await navigator.mediaDevices.getUserMedia({ 
                video: { facingMode: "environment" } 
            });
        } catch {
            // Respaldo para laptops o dispositivos con una sola cámara frontal/webcam
            stream = await navigator.mediaDevices.getUserMedia({ video: true });
        }
        video.srcObject = stream;
    } catch (error) {
        console.error("Error al acceder a la cámara:", error);
        alert("Por favor, permite el acceso a la cámara en tu navegador.");
    }
}

// 2. Evento de Captura con gestión limpia de memoria
btnCapture.addEventListener('click', () => {
    if (!video.videoWidth || !video.videoHeight) {
        alert("La cámara aún no está lista.");
        return;
    }

    canvas.width = video.videoWidth;
    canvas.height = video.videoHeight;
    const context = canvas.getContext('2d');
    context.drawImage(video, 0, 0, canvas.width, canvas.height);

    canvas.toBlob(async (blob) => {
        if (!blob) return;
        const tempId = Date.now().toString();
        const imageUrl = URL.createObjectURL(blob);
        
        // Mostrar miniatura al juez
        addPendingItemToUI(tempId, imageUrl);
        
        // Enviar al servidor C#
        await sendPhotoToServer(tempId, blob);
    }, 'image/jpeg', 0.85);
});

// 3. Agregar elemento a la bandeja de pendientes en la UI del teléfono
function addPendingItemToUI(tempId, imageUrl) {
    const div = document.createElement('div');
    div.className = 'pending-item';
    div.id = `item-${tempId}`;
    div.dataset.blobUrl = imageUrl; // Almacenar referencia para liberar memoria luego
    
    div.innerHTML = `
        <img src="${imageUrl}" alt="Corredor">
        <input type="number" placeholder="Dorsal" id="input-${tempId}">
        <button id="btn-assign-${tempId}" onclick="assignRunner('${tempId}')" style="margin-right: 5px; opacity: 0.6;" disabled>OK</button>
        <button onclick="discardPhoto('${tempId}')" style="background: #dc3545;">X</button>
    `;
    
    pendingQueue.insertBefore(div, pendingQueue.firstChild);
}

// 4. Enviar foto inicial y habilitar botón una vez confirmado el serverId
async function sendPhotoToServer(tempId, imageBlob) {
    const formData = new FormData();
    formData.append("foto", imageBlob, `img_${tempId}.jpg`);
    formData.append("tempId", tempId);

    try {
        const response = await fetch('/api/upload-photo', {
            method: 'POST',
            body: formData
        });
        
        if (!response.ok) throw new Error("Fallo en el servidor");
        
        const data = await response.json();
        const item = document.getElementById(`item-${tempId}`);
        if (item) {
            item.dataset.serverId = data.idFoto;
            const btnOk = document.getElementById(`btn-assign-${tempId}`);
            if (btnOk) {
                btnOk.disabled = false;
                btnOk.style.opacity = "1";
            }
        }
    } catch (error) {
        console.error("Error al subir foto:", error);
        alert("Error al conectar con la mesa de cronometraje.");
    }
}

// 5. Enviar el número de dorsal y dar feedback visual
async function assignRunner(tempId) {
    const item = document.getElementById(`item-${tempId}`);
    const inputDorsal = document.getElementById(`input-${tempId}`);
    const serverId = item ? item.dataset.serverId : null;

    if (!inputDorsal || !inputDorsal.value.trim()) {
        alert("Ingresa un número de dorsal.");
        return;
    }

    if (!serverId) {
        alert("La foto aún se está transmitiendo a la PC. Espera un momento.");
        return;
    }

    try {
        const response = await fetch('/api/assign-dorsal', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ idFoto: serverId, dorsal: parseInt(inputDorsal.value) })
        });

        if (response.ok) {
            // Feedback visual: orden aceptada
            inputDorsal.style.backgroundColor = "#d4edda";
            const btnOk = document.getElementById(`btn-assign-${tempId}`);
            if (btnOk) btnOk.disabled = true;
        } else {
            const errData = await response.json().catch(() => ({}));
            alert(errData.mensaje || "Error al procesar dorsal.");
        }
    } catch (error) {
        console.error("Error al asignar corredor:", error);
        alert("Error de red al asignar dorsal.");
    }
}

// 6. Descartar foto
async function discardPhoto(tempId) {
    const item = document.getElementById(`item-${tempId}`);
    if (!item) return;
    const serverId = item.dataset.serverId;

    eliminarItemUIRecuperandoMemoria(item);

    if (serverId) {
        try {
            await fetch('/api/discard-photo', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ idFoto: serverId })
            });
        } catch (error) {
            console.error("Error al descartar remoto:", error);
        }
    }
}

// Función auxiliar para remover elementos y liberar memoria Blob
function eliminarItemUIRecuperandoMemoria(item) {
    if (!item) return;
    if (item.dataset.blobUrl) {
        URL.revokeObjectURL(item.dataset.blobUrl);
    }
    item.remove();
}

// 7. Polling: Sincronizar cola con el servidor PC
setInterval(async () => {
    try {
        const response = await fetch('/api/pending-photos');
        if (response.ok) {
            const pendingIds = await response.json();
            
            document.querySelectorAll('.pending-item').forEach(item => {
                const serverId = item.dataset.serverId;
                // Si la foto tiene ID en el servidor pero ya no está pendiente,
                // significa que la PC la procesó o descartó con éxito.
                if (serverId && !pendingIds.includes(serverId)) {
                    eliminarItemUIRecuperandoMemoria(item);
                }
            });
        }
    } catch {
        // Tolerancia a microcortes de WiFi
    }
}, 2000);

// Iniciar cámara al cargar la página
initCamera();