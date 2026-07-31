// ── Chart.js Helpers ─────────────────────────────────────────────────────────
window.renderChart = function (canvasId, config) {
    const el = document.getElementById(canvasId);
    if (!el) return;
    if (el._chart) { el._chart.destroy(); el._chart = null; }
    if (typeof Chart === 'undefined') return;
    el._chart = new Chart(el, config);
};

window.destroyChart = function (canvasId) {
    const el = document.getElementById(canvasId);
    if (el && el._chart) { el._chart.destroy(); el._chart = null; }
};

window.downloadBytes = function (fileName, bytes) {
    const blob = new Blob([new Uint8Array(bytes)], { type: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    setTimeout(function () { document.body.removeChild(a); URL.revokeObjectURL(url); }, 1000);
};