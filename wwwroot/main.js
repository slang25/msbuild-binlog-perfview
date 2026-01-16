import { invoke, waitForReady, setProgressCallback } from './worker-client.js'

// DOM elements
const dropZone = document.getElementById('drop-zone');
const fileInput = document.getElementById('file-input');

// Option checkboxes
const optProjects = document.getElementById('opt-projects');
const optTargets = document.getElementById('opt-targets');
const optTasks = document.getElementById('opt-tasks');
const optMessages = document.getElementById('opt-messages');
const optWarnings = document.getElementById('opt-warnings');
const optErrors = document.getElementById('opt-errors');

// State
let currentTraceData = null;
let currentFileName = null;
let isProcessing = false;

// Store original drop zone content
const originalDropZoneContent = dropZone.innerHTML;

// Initialize WASM worker
async function initWasm() {
    try {
        showProcessingState('Loading WebAssembly...', 0);

        // Set up progress callback to show conversion progress
        setProgressCallback((message, current, total) => {
            const percent = Math.round((current / total) * 100);
            showProcessingState(message, percent);
        });

        await waitForReady();

        resetDropZone();
        console.log('WASM worker ready');
    } catch (err) {
        showErrorState(`Failed to load WebAssembly: ${err.message}`);
        console.error('Worker init error:', err);
    }
}

// Show processing state in drop zone
function showProcessingState(message, percent) {
    isProcessing = true;
    dropZone.className = 'processing';
    dropZone.innerHTML = `
        <div class="processing-content">
            <div class="processing-spinner"></div>
            ${currentFileName ? `<div class="processing-filename">${currentFileName}</div>` : ''}
            <div class="processing-percent">${percent}%</div>
            <div class="processing-message">${message}</div>
        </div>
    `;
}

// Show success state in drop zone
function showSuccessState(fileName, fileSize) {
    isProcessing = false;
    dropZone.className = 'success';
    dropZone.innerHTML = `
        <div class="success-content">
            <div class="success-icon">✓</div>
            <div class="success-message">Conversion complete!</div>
            <div class="success-filename">${fileName} (${formatFileSize(fileSize)})</div>
            <div class="success-actions">
                <button class="btn btn-primary" id="open-perfetto-btn">Open in Perfetto</button>
                <button class="btn btn-secondary" id="download-btn">Download</button>
            </div>
            <button class="btn-link" id="convert-another-btn">Convert another file</button>
        </div>
    `;

    // Add event listeners for buttons
    // Open in Perfetto - must be user-initiated click to avoid popup blocker
    document.getElementById('open-perfetto-btn').addEventListener('click', () => {
        openPerfetto(currentTraceData, currentFileName);
    });
    document.getElementById('download-btn').addEventListener('click', downloadTrace);
    document.getElementById('convert-another-btn').addEventListener('click', resetDropZone);
}

// Show error state in drop zone
function showErrorState(message) {
    isProcessing = false;
    dropZone.className = 'error';
    dropZone.innerHTML = `
        <div class="error-content">
            <div class="error-icon">✕</div>
            <div class="error-message">${message}</div>
            <div class="success-actions">
                <button class="btn btn-secondary" id="try-again-btn">Try Again</button>
            </div>
        </div>
    `;

    document.getElementById('try-again-btn').addEventListener('click', resetDropZone);
}

// Reset drop zone to original state
function resetDropZone() {
    isProcessing = false;
    currentTraceData = null;
    currentFileName = null;
    dropZone.className = '';
    dropZone.innerHTML = originalDropZoneContent;
}

function formatFileSize(bytes) {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
}

// Get current options from checkboxes
function getOptions() {
    return {
        projects: optProjects.checked,
        targets: optTargets.checked,
        tasks: optTasks.checked,
        messages: optMessages.checked,
        warnings: optWarnings.checked,
        errors: optErrors.checked
    };
}

// File handling
async function handleFile(file) {
    if (!file.name.toLowerCase().endsWith('.binlog')) {
        showErrorState('Please select a .binlog file');
        return;
    }

    currentFileName = file.name;

    try {
        showProcessingState('Reading file...', 0);

        // Read file as array buffer
        const arrayBuffer = await file.arrayBuffer();
        const bytes = new Uint8Array(arrayBuffer);

        showProcessingState('Converting to Perfetto format...', 5);

        // Get options
        const opts = getOptions();

        // Call protobuf converter via worker
        const protoBytes = await invoke('BinlogConverter.ConvertToProtobuf', [
            bytes,
            opts.projects,
            opts.targets,
            opts.tasks,
            opts.messages,
            opts.warnings,
            opts.errors
        ]);

        if (protoBytes.length === 0) {
            throw new Error('Conversion failed - no data returned');
        }

        currentTraceData = protoBytes;

        showSuccessState(file.name, file.size);

    } catch (err) {
        showErrorState(`Error: ${err.message}`);
        console.error('Conversion error:', err);
    }
}

// Perfetto integration using postMessage API
async function openPerfetto(traceData, fileName) {
    const PERFETTO_UI = 'https://ui.perfetto.dev';

    // traceData is a Uint8Array
    const traceBuffer = traceData.buffer;
    const traceFileName = fileName.replace('.binlog', '.pftrace');

    // Open Perfetto in new window
    const perfettoWindow = window.open(PERFETTO_UI);

    if (!perfettoWindow) {
        throw new Error('Popup blocked! Please allow popups for this site.');
    }

    // Wait for Perfetto to be ready using PING/PONG handshake
    return new Promise((resolve, reject) => {
        const timeout = setTimeout(() => {
            window.removeEventListener('message', messageHandler);
            reject(new Error('Timeout waiting for Perfetto UI'));
        }, 30000);

        // Send PINGs until we get a PONG
        const pingInterval = setInterval(() => {
            perfettoWindow.postMessage('PING', PERFETTO_UI);
        }, 50);

        function messageHandler(evt) {
            if (evt.data !== 'PONG') return;

            // Perfetto is ready
            clearInterval(pingInterval);
            clearTimeout(timeout);
            window.removeEventListener('message', messageHandler);

            // Send the trace data
            perfettoWindow.postMessage({
                perfetto: {
                    buffer: traceBuffer,
                    title: fileName.replace('.binlog', ''),
                    fileName: traceFileName
                }
            }, PERFETTO_UI);

            resolve();
        }

        window.addEventListener('message', messageHandler);
    });
}

// Download trace file
function downloadTrace() {
    if (!currentTraceData || !currentFileName) return;

    const blob = new Blob([currentTraceData], { type: 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = currentFileName.replace('.binlog', '.pftrace');
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
}

// Event listeners
dropZone.addEventListener('click', (e) => {
    // Don't trigger file input if clicking on buttons or if processing
    if (isProcessing || e.target.tagName === 'BUTTON') return;
    fileInput.click();
});

dropZone.addEventListener('dragover', (e) => {
    e.preventDefault();
    e.stopPropagation();
    if (!isProcessing) {
        dropZone.classList.add('drag-over');
    }
});

dropZone.addEventListener('dragleave', (e) => {
    e.preventDefault();
    e.stopPropagation();
    if (!isProcessing) {
        dropZone.classList.remove('drag-over');
    }
});

dropZone.addEventListener('drop', (e) => {
    e.preventDefault();
    e.stopPropagation();
    dropZone.classList.remove('drag-over');

    if (isProcessing) return;

    const files = e.dataTransfer.files;
    if (files.length > 0) {
        handleFile(files[0]);
    }
});

fileInput.addEventListener('change', (e) => {
    if (e.target.files.length > 0) {
        handleFile(e.target.files[0]);
    }
});

// Initialize
initWasm();
