// Web Worker entry point for .NET WASM binlog conversion
// Based on dotnet/aspnetcore webworker template pattern

import { dotnet } from './_framework/dotnet.js'
import { postProgress } from './progress.js'

// Make postProgress available globally for the dotnet runtime to find
globalThis.postProgress = postProgress;

// Re-export for compatibility
export { postProgress };

// Collect transferable buffers for zero-copy transfer
function collectTransferables(value) {
    const transferables = [];
    if (value instanceof Uint8Array || value instanceof Int8Array ||
        value instanceof Uint16Array || value instanceof Int16Array ||
        value instanceof Uint32Array || value instanceof Int32Array ||
        value instanceof Float32Array || value instanceof Float64Array) {
        transferables.push(value.buffer);
    } else if (value instanceof ArrayBuffer) {
        transferables.push(value);
    }
    return transferables;
}

let workerExports = null;
let startupError = undefined;

// Initialize .NET runtime in worker context
try {
    const { getAssemblyExports, getConfig } = await dotnet.create();
    const config = getConfig();
    workerExports = await getAssemblyExports(config.mainAssemblyName);
    console.log('[Worker] .NET runtime initialized');
    self.postMessage({ type: "ready" });
} catch (err) {
    startupError = err.message;
    console.error("[Worker] Failed to initialize .NET:", err);
    self.postMessage({ type: "ready", error: err.message });
}

// Handle method invocations from main thread
self.addEventListener('message', async function (e) {
    try {
        if (!workerExports) {
            throw new Error(startupError || "Worker .NET runtime not loaded");
        }

        const { method, args, requestId } = e.data;

        // Parse "ClassName.MethodName" format
        const parts = method.split('.');
        if (parts.length < 2) {
            throw new Error(`Invalid method format: ${method}. Expected "ClassName.MethodName"`);
        }

        const className = parts[0];
        const methodName = parts[1];

        const workerClass = workerExports[className];
        if (!workerClass) {
            console.error('[Worker] Available exports:', Object.keys(workerExports || {}));
            throw new Error(`Class not found: ${className}`);
        }

        const targetMethod = workerClass[methodName];
        if (!targetMethod) {
            console.error('[Worker] Available methods:', Object.keys(workerClass || {}));
            throw new Error(`Method not found: ${methodName} on ${className}`);
        }

        const startTime = performance.now();
        let result = targetMethod(...args);

        // Handle async methods
        if (result && typeof result.then === 'function') {
            result = await result;
        }

        const workerTime = performance.now() - startTime;
        const transferables = collectTransferables(result);

        console.log(`[Worker] ${method} completed in ${workerTime.toFixed(0)}ms`);

        self.postMessage({
            type: "result",
            requestId: requestId,
            result: result,
            workerTime: workerTime,
        }, transferables);
    } catch (err) {
        const errorMessage = err instanceof Error ? err.message : String(err);
        console.error('[Worker] Error:', errorMessage);
        self.postMessage({
            type: "result",
            requestId: e.data.requestId,
            error: errorMessage,
        });
    }
}, false);
