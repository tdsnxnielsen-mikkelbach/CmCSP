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
    var foreColor = isDark ? '#CFD8DC' : '#455A64';

    // Global default for charts created later (Blazor navigation)
    window.Apex = window.Apex || {};
    window.Apex.theme = Object.assign({}, window.Apex.theme || {}, { mode: mode });
    window.Apex.chart = Object.assign({}, window.Apex.chart || {}, { foreColor: foreColor });

    // Update every chart currently in the DOM
    document.querySelectorAll('.apexcharts-canvas').forEach(function (el) {
        var id = el.id.replace('apexcharts', '');
        try {
            ApexCharts.exec(
                id,
                'updateOptions',
                { theme: { mode: mode }, chart: { foreColor: foreColor } },
                false,
                false
            );
        } catch (e) { /* chart may be mid-mount/unmount – safe to ignore */ }
    });
};

window.getThemePreference = function (key) {
    try {
        return window.localStorage.getItem(key);
    } catch (e) {
        return null;
    }
};

window.setThemePreference = function (key, isDark) {
    try {
        window.localStorage.setItem(key, isDark ? 'dark' : 'light');
    } catch (e) {
        // Storage can fail in private mode or when browser policies block it.
    }
};
