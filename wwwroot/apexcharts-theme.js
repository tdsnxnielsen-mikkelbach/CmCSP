/**
 * updateApexChartsTheme(isDark)
 *
 * 1. Sets window.Apex.theme so any chart created *after* this call (e.g. on
 *    Blazor navigation) picks up the correct mode automatically.
 * 2. Calls ApexCharts.exec() on every chart already mounted in the DOM so the
 *    current page updates instantly without needing a page reload.
 */
window.updateApexChartsTheme = function (isDark) {
    var mode = isDark ? 'dark' : 'light';

    // Global default for charts created later (Blazor navigation)
    window.Apex = window.Apex || {};
    window.Apex.theme = { mode: mode };

    // Update every chart currently in the DOM
    document.querySelectorAll('.apexcharts-canvas').forEach(function (el) {
        var id = el.id.replace('apexcharts', '');
        try {
            ApexCharts.exec(id, 'updateOptions', { theme: { mode: mode } }, false, false);
        } catch (e) { /* chart may be mid-mount/unmount – safe to ignore */ }
    });
};
