import { dotnet } from './_framework/dotnet.js'

// DOM elements
const dropZone = document.getElementById('drop-zone');
const fileInput = document.getElementById('file-input');
const status = document.getElementById('status');
const fileInfo = document.getElementById('file-info');
const downloadBtn = document.getElementById('download-btn');

// Option checkboxes
const optProjects = document.getElementById('opt-projects');
const optTargets = document.getElementById('opt-targets');
const optTasks = document.getElementById('opt-tasks');
const optMessages = document.getElementById('opt-messages');
const optWarnings = document.getElementById('opt-warnings');
const optErrors = document.getElementById('opt-errors');

// Format radio buttons
const fmtJson = document.getElementById('fmt-json');
const fmtProto = document.getElementById('fmt-proto');

// State
let currentTraceData = null; // Can be JSON string or Uint8Array
let currentTraceFormat = 'json';
let currentFileName = null;
let wasmExports = null;

// Initialize WASM
async function initWasm() {
    try {
        setStatus('loading', '<span class="spinner"></span>Loading WebAssembly runtime...');

        const { getAssemblyExports, getConfig, runMain } = await dotnet.create();

        const config = getConfig();
        wasmExports = await getAssemblyExports(config.mainAssemblyName);

        // Start the runtime (keeps it alive for subsequent calls)
        runMain();

        setStatus('', '');
        console.log('WASM runtime initialized');
    } catch (err) {
        setStatus('error', `Failed to load WebAssembly: ${err.message}`);
        console.error('WASM init error:', err);
    }
}

// Status helpers
function setStatus(type, message) {
    status.className = type;
    status.innerHTML = message;
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

// Get selected format
function getFormat() {
    return fmtProto.checked ? 'proto' : 'json';
}

// Yield to allow UI updates (prevents unresponsive tab warning)
function yieldToMain() {
    return new Promise(resolve => setTimeout(resolve, 0));
}

// File handling
async function handleFile(file) {
    if (!file.name.toLowerCase().endsWith('.binlog')) {
        setStatus('error', 'Please drop a .binlog file');
        return;
    }

    if (!wasmExports) {
        setStatus('error', 'WebAssembly runtime not ready. Please wait and try again.');
        return;
    }

    currentFileName = file.name;
    currentTraceFormat = getFormat();
    dropZone.classList.add('processing');
    setStatus('loading', '<span class="spinner"></span>Reading file...');

    try {
        // Read file as array buffer
        const arrayBuffer = await file.arrayBuffer();
        const bytes = new Uint8Array(arrayBuffer);

        // Yield to update UI before heavy processing
        await yieldToMain();

        const formatLabel = currentTraceFormat === 'proto' ? 'Perfetto protobuf' : 'Chrome JSON';
        setStatus('loading', `<span class="spinner"></span>Converting binlog to ${formatLabel} format...`);

        // Another yield before the heavy WASM call
        await yieldToMain();

        // Get options
        const opts = getOptions();

        let eventCount = 0;

        if (currentTraceFormat === 'proto') {
            // Call protobuf converter
            const protoBytes = wasmExports.BinlogConverter.ConvertToProtobuf(
                bytes,
                opts.projects,
                opts.targets,
                opts.tasks,
                opts.messages,
                opts.warnings,
                opts.errors
            );

            if (protoBytes.length === 0) {
                throw new Error('Conversion failed');
            }

            currentTraceData = protoBytes;
            eventCount = '(protobuf)';

            // Update download button text
            downloadBtn.textContent = 'Download Perfetto Trace';
        } else {
            // Call JSON converter
            const jsonResult = wasmExports.BinlogConverter.ConvertToTrace(
                bytes,
                opts.projects,
                opts.targets,
                opts.tasks,
                opts.messages,
                opts.warnings,
                opts.errors
            );

            // Yield after heavy processing
            await yieldToMain();

            // Check for error
            const result = JSON.parse(jsonResult);
            if (result.error) {
                throw new Error(result.error);
            }

            currentTraceData = jsonResult;
            eventCount = result.traceEvents?.length || 0;

            // Update download button text
            downloadBtn.textContent = 'Download JSON Trace';
        }

        // Show file info
        fileInfo.innerHTML = `<strong>${file.name}</strong> (${formatFileSize(file.size)}) - ${eventCount} trace events`;
        fileInfo.classList.add('visible');
        downloadBtn.classList.add('visible');

        setStatus('success', 'Conversion complete! Opening Perfetto...');

        // Open Perfetto with the trace
        await openPerfetto(currentTraceData, file.name, currentTraceFormat);

    } catch (err) {
        setStatus('error', `Error: ${err.message}`);
        console.error('Conversion error:', err);
    } finally {
        dropZone.classList.remove('processing');
    }
}

// Perfetto integration using postMessage API
async function openPerfetto(traceData, fileName, format) {
    const PERFETTO_UI = 'https://ui.perfetto.dev';

    // Get the trace buffer
    let traceBuffer;
    let traceFileName;

    if (format === 'proto') {
        // traceData is already a Uint8Array
        traceBuffer = traceData.buffer;
        traceFileName = fileName.replace('.binlog', '.pftrace');
    } else {
        // Convert JSON string to ArrayBuffer
        const encoder = new TextEncoder();
        traceBuffer = encoder.encode(traceData).buffer;
        traceFileName = fileName.replace('.binlog', '.json');
    }

    // Open Perfetto in new window
    const perfettoWindow = window.open(PERFETTO_UI);

    if (!perfettoWindow) {
        setStatus('error', 'Popup blocked! Please allow popups for this site, or use the download button.');
        return;
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

            setStatus('success', 'Trace opened in Perfetto!');
            resolve();
        }

        window.addEventListener('message', messageHandler);
    });
}

// Download trace file
function downloadTrace() {
    if (!currentTraceData) return;

    let blob;
    let extension;

    if (currentTraceFormat === 'proto') {
        blob = new Blob([currentTraceData], { type: 'application/octet-stream' });
        extension = '.pftrace';
    } else {
        blob = new Blob([currentTraceData], { type: 'application/json' });
        extension = '.json';
    }

    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = currentFileName.replace('.binlog', extension);
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
}

// Event listeners
dropZone.addEventListener('click', () => fileInput.click());

dropZone.addEventListener('dragover', (e) => {
    e.preventDefault();
    e.stopPropagation();
    dropZone.classList.add('drag-over');
});

dropZone.addEventListener('dragleave', (e) => {
    e.preventDefault();
    e.stopPropagation();
    dropZone.classList.remove('drag-over');
});

dropZone.addEventListener('drop', (e) => {
    e.preventDefault();
    e.stopPropagation();
    dropZone.classList.remove('drag-over');

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

downloadBtn.addEventListener('click', downloadTrace);

// Initialize
initWasm();
