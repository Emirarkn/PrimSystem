// ── Chart.js Helpers ─────────────────────────────────────────────────────────
// Chart.js (~200 KB) yalnız grafik çizilen sayfalarda gerekir. Eskiden her sayfada <script> ile
// eşzamanlı yükleniyordu; giriş sayfası dahil ilk boyamayı ve Blazor bağlantısını yüzlerce ms
// geciktiriyordu. Artık ilk renderChart çağrısında yüklenir ve bir kez önbelleğe alınır.
window.ensureChartJs = function () {
    if (typeof Chart !== 'undefined') return Promise.resolve(true);
    if (window._chartJsPromise) return window._chartJsPromise;
    window._chartJsPromise = new Promise(function (resolve) {
        var s = document.createElement('script');
        s.src = 'lib/chart.umd.min.js';
        s.onload = function () { resolve(true); };
        s.onerror = function () { window._chartJsPromise = null; resolve(false); };
        document.head.appendChild(s);
    });
    return window._chartJsPromise;
};

window.renderChart = async function (canvasId, config) {
    if (!(await window.ensureChartJs())) return;
    // Yükleme beklenirken sayfa yeniden çizilmiş olabilir: elemanı yükleme sonrası al.
    const el = document.getElementById(canvasId);
    if (!el) return;
    if (el._chart) { el._chart.destroy(); el._chart = null; }
    el._chart = new Chart(el, config);
};

window.destroyChart = function (canvasId) {
    const el = document.getElementById(canvasId);
    if (el && el._chart) { el._chart.destroy(); el._chart = null; }
};

window.downloadBytes = function (fileName, bytes) {
    const type = /\.csv$/i.test(fileName)
        ? "text/csv;charset=utf-8"
        : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    const blob = new Blob([new Uint8Array(bytes)], { type: type });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    setTimeout(function () { document.body.removeChild(a); URL.revokeObjectURL(url); }, 1000);
};