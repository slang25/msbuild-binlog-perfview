// Progress bridge module for C# interop
// This module is imported by both worker.js and the C# runtime

export function postProgress(message, current, total) {
    self.postMessage({
        type: "progress",
        message: message,
        current: current,
        total: total
    });
}
