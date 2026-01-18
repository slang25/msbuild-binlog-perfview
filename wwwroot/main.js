import { invoke, waitForReady, setProgressCallback, cancel as cancelWorker } from './worker-client.js'

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

// Show processing state in drop zone with circular progress
function showProcessingState(message, percent) {
    isProcessing = true;
    dropZone.className = 'processing';

    // Build DOM safely to avoid XSS
    const container = document.createElement('div');
    container.className = 'processing-content';

    // Circular progress indicator
    const circleContainer = document.createElement('div');
    circleContainer.className = 'progress-circle';

    const radius = 52;
    const circumference = 2 * Math.PI * radius;
    const offset = circumference - (percent / 100) * circumference;

    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 120 120');

    const bgCircle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
    bgCircle.setAttribute('class', 'bg');
    bgCircle.setAttribute('cx', '60');
    bgCircle.setAttribute('cy', '60');
    bgCircle.setAttribute('r', radius.toString());
    svg.appendChild(bgCircle);

    const progressCircle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
    progressCircle.setAttribute('class', 'progress');
    progressCircle.setAttribute('cx', '60');
    progressCircle.setAttribute('cy', '60');
    progressCircle.setAttribute('r', radius.toString());
    progressCircle.style.strokeDasharray = circumference.toString();
    progressCircle.style.strokeDashoffset = offset.toString();
    svg.appendChild(progressCircle);

    circleContainer.appendChild(svg);

    const percentText = document.createElement('div');
    percentText.className = 'percent-text';
    percentText.textContent = `${percent}%`;
    circleContainer.appendChild(percentText);

    container.appendChild(circleContainer);

    if (currentFileName) {
        const filenameDiv = document.createElement('div');
        filenameDiv.className = 'processing-filename';
        filenameDiv.textContent = currentFileName;
        container.appendChild(filenameDiv);
    }

    const messageDiv = document.createElement('div');
    messageDiv.className = 'processing-message';
    messageDiv.textContent = message;
    container.appendChild(messageDiv);

    const cancelBtn = document.createElement('button');
    cancelBtn.className = 'btn btn-secondary btn-cancel';
    cancelBtn.textContent = 'Cancel';
    cancelBtn.addEventListener('click', handleCancel);
    container.appendChild(cancelBtn);

    dropZone.innerHTML = '';
    dropZone.appendChild(container);
}

// Handle cancel button click
function handleCancel() {
    cancelWorker();
}

// Show success state in drop zone
function showSuccessState(fileName, fileSize) {
    isProcessing = false;
    dropZone.className = 'success';

    // Build DOM safely to avoid XSS
    const container = document.createElement('div');
    container.className = 'success-content';

    const icon = document.createElement('div');
    icon.className = 'success-icon';
    icon.textContent = '\u2713'; // checkmark
    container.appendChild(icon);

    const msgDiv = document.createElement('div');
    msgDiv.className = 'success-message';
    msgDiv.textContent = 'Conversion complete!';
    container.appendChild(msgDiv);

    const filenameDiv = document.createElement('div');
    filenameDiv.className = 'success-filename';
    filenameDiv.textContent = `${fileName} (${formatFileSize(fileSize)})`;
    container.appendChild(filenameDiv);

    const actionsDiv = document.createElement('div');
    actionsDiv.className = 'success-actions';

    const openBtn = document.createElement('button');
    openBtn.className = 'btn btn-primary';
    openBtn.textContent = 'Open in Perfetto';
    openBtn.addEventListener('click', () => {
        openPerfetto(currentTraceData, currentFileName);
    });
    actionsDiv.appendChild(openBtn);

    const secondaryActions = document.createElement('div');
    secondaryActions.className = 'secondary-actions';

    const downloadBtn = document.createElement('button');
    downloadBtn.className = 'btn-link';
    downloadBtn.textContent = 'Download trace';
    downloadBtn.addEventListener('click', downloadTrace);
    secondaryActions.appendChild(downloadBtn);

    const separator = document.createElement('span');
    separator.textContent = '·';
    separator.style.color = '#555';
    secondaryActions.appendChild(separator);

    const convertAnotherBtn = document.createElement('button');
    convertAnotherBtn.className = 'btn-link';
    convertAnotherBtn.textContent = 'Convert another';
    convertAnotherBtn.addEventListener('click', resetDropZone);
    secondaryActions.appendChild(convertAnotherBtn);

    actionsDiv.appendChild(secondaryActions);
    container.appendChild(actionsDiv);

    dropZone.innerHTML = '';
    dropZone.appendChild(container);
}

// Show error state in drop zone
function showErrorState(message) {
    isProcessing = false;
    dropZone.className = 'error';

    // Build DOM safely to avoid XSS
    const container = document.createElement('div');
    container.className = 'error-content';

    const icon = document.createElement('div');
    icon.className = 'error-icon';
    icon.textContent = '\u2715'; // X mark
    container.appendChild(icon);

    const msgDiv = document.createElement('div');
    msgDiv.className = 'error-message';
    msgDiv.textContent = message;
    container.appendChild(msgDiv);

    const actionsDiv = document.createElement('div');
    actionsDiv.className = 'success-actions';

    const tryAgainBtn = document.createElement('button');
    tryAgainBtn.className = 'btn btn-secondary';
    tryAgainBtn.textContent = 'Try Again';
    tryAgainBtn.addEventListener('click', resetDropZone);
    actionsDiv.appendChild(tryAgainBtn);

    container.appendChild(actionsDiv);

    dropZone.innerHTML = '';
    dropZone.appendChild(container);
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

        // Call protobuf converter via worker - returns byte array, throws on error
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
        // Check if the operation was cancelled
        if (err.message && err.message.includes('canceled')) {
            resetDropZone();
            return;
        }
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
            clearInterval(pingInterval);
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

// Welcome modal
const welcomeModal = document.getElementById('welcome-modal');
const modalCloseBtn = document.getElementById('modal-close-btn');
const dontShowAgainCheckbox = document.getElementById('dont-show-again');

function showWelcomeModal() {
    if (localStorage.getItem('hideWelcomeModal') === 'true') {
        return;
    }
    welcomeModal.classList.add('visible');
}

function hideWelcomeModal() {
    if (dontShowAgainCheckbox.checked) {
        localStorage.setItem('hideWelcomeModal', 'true');
    }
    welcomeModal.classList.remove('visible');
}

modalCloseBtn.addEventListener('click', hideWelcomeModal);

// Close modal when clicking overlay (but not the modal itself)
welcomeModal.addEventListener('click', (e) => {
    if (e.target === welcomeModal) {
        hideWelcomeModal();
    }
});

// Close modal with Escape key
document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && welcomeModal.classList.contains('visible')) {
        hideWelcomeModal();
    }
});

// Copy command button
const copyBtn = document.getElementById('copy-cmd-btn');
copyBtn.addEventListener('click', async () => {
    try {
        await navigator.clipboard.writeText('dotnet build -bl');
        copyBtn.classList.add('copied');
        setTimeout(() => copyBtn.classList.remove('copied'), 1500);
    } catch (err) {
        console.error('Failed to copy:', err);
    }
});

// Show welcome modal on load
showWelcomeModal();

// Initialize
initWasm();
