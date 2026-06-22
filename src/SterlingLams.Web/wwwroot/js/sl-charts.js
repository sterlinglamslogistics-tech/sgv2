/*
 * SLCharts — a thin, consistent wrapper over Chart.js for the admin/inventory backends.
 * One palette, one set of fonts/grid styling, ₦ axis formatting. Keeps every backend chart
 * looking the same and the per-page init code tiny:
 *
 *   SLCharts.line('salesTrend', labels, [{label:'Revenue', data:[...], money:true}]);
 *   SLCharts.bar('perBranch', labels, data, { money:false });
 *   SLCharts.hbar('topProducts', labels, data, { money:false });
 *   SLCharts.doughnut('stockHealth', labels, data, { colors:[...] });
 *
 * Requires Chart.js (lib/chart/chart.umd.min.js) loaded first — see _ChartScripts.cshtml.
 */
(function () {
    if (typeof Chart === 'undefined') { console.error('SLCharts: Chart.js not loaded'); return; }

    // Brand-leaning palette: emerald first (matches the storefront/admin accent), then a calm spread.
    var palette = ['#10b981', '#0ea5e9', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899', '#14b8a6', '#64748b'];
    var grid = '#f5f5f5';

    Chart.defaults.font.family = 'ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif';
    Chart.defaults.font.size = 10;
    Chart.defaults.color = '#737373';

    function naira(v) { return '₦' + Number(v).toLocaleString(); }
    function compact(v) { return Number(v).toLocaleString(); }

    function el(id) {
        var c = document.getElementById(id);
        if (!c) { console.warn('SLCharts: no canvas #' + id); return null; }
        return c;
    }

    function withAlpha(hex, a) {
        var n = parseInt(hex.slice(1), 16);
        return 'rgba(' + ((n >> 16) & 255) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + a + ')';
    }

    function valueAxis(money) {
        return {
            grid: { color: grid }, border: { display: false },
            ticks: { font: { size: 10 }, callback: function (v) { return money ? naira(v) : compact(v); } }
        };
    }
    function catAxis() {
        return { grid: { display: false }, border: { display: false }, ticks: { font: { size: 10 }, maxTicksLimit: 12 } };
    }
    function moneyTooltip(money) {
        return { callbacks: { label: function (ctx) {
            var v = ctx.parsed.y != null ? ctx.parsed.y : ctx.parsed.x != null ? ctx.parsed.x : ctx.parsed;
            return (ctx.dataset.label ? ctx.dataset.label + ': ' : '') + (money ? naira(v) : compact(v));
        } } };
    }

    var SLCharts = {
        palette: palette,
        naira: naira,

        // series: array of { label, data, money?, color? } — one or more lines.
        line: function (id, labels, series, opts) {
            var c = el(id); if (!c) return null; opts = opts || {};
            var anyMoney = series.some(function (s) { return s.money; });
            return new Chart(c, {
                type: 'line',
                data: { labels: labels, datasets: series.map(function (s, i) {
                    var col = s.color || palette[i % palette.length];
                    return {
                        label: s.label, data: s.data, borderColor: col,
                        backgroundColor: withAlpha(col, 0.08), borderWidth: 2,
                        pointRadius: 0, pointHoverRadius: 4, tension: 0.35, fill: true
                    };
                }) },
                options: {
                    responsive: true, maintainAspectRatio: true, interaction: { mode: 'index', intersect: false },
                    plugins: { legend: { display: series.length > 1, labels: { boxWidth: 10, font: { size: 11 } } }, tooltip: moneyTooltip(anyMoney) },
                    scales: { x: catAxis(), y: Object.assign({ beginAtZero: true }, valueAxis(anyMoney)) }
                }
            });
        },

        bar: function (id, labels, data, opts) {
            var c = el(id); if (!c) return null; opts = opts || {};
            return new Chart(c, {
                type: 'bar',
                data: { labels: labels, datasets: [{
                    label: opts.label || '', data: data,
                    backgroundColor: withAlpha(palette[0], 0.85), borderRadius: 3, maxBarThickness: 48
                }] },
                options: {
                    responsive: true, maintainAspectRatio: true,
                    plugins: { legend: { display: false }, tooltip: moneyTooltip(opts.money) },
                    scales: { x: catAxis(), y: Object.assign({ beginAtZero: true }, valueAxis(opts.money)) }
                }
            });
        },

        // Horizontal bars — good for ranked lists (top products/staff). Height tracks the row count
        // (a fixed aspect ratio leaves few bars floating in a tall canvas), so the card's parent box
        // is sized here and the chart fills it.
        hbar: function (id, labels, data, opts) {
            var c = el(id); if (!c) return null; opts = opts || {};
            // +70 covers the card's border-box padding plus the value axis; ~42px per row.
            if (c.parentElement) c.parentElement.style.height = Math.max(140, labels.length * 42 + 70) + 'px';
            return new Chart(c, {
                type: 'bar',
                data: { labels: labels, datasets: [{
                    label: opts.label || '', data: data,
                    backgroundColor: labels.map(function (_, i) { return withAlpha(palette[i % palette.length], 0.85); }),
                    borderRadius: 3, maxBarThickness: 26
                }] },
                options: {
                    indexAxis: 'y', responsive: true, maintainAspectRatio: false,
                    plugins: { legend: { display: false }, tooltip: moneyTooltip(opts.money) },
                    scales: { x: Object.assign({ beginAtZero: true }, valueAxis(opts.money)), y: catAxis() }
                }
            });
        },

        doughnut: function (id, labels, data, opts) {
            var c = el(id); if (!c) return null; opts = opts || {};
            var colors = opts.colors || labels.map(function (_, i) { return palette[i % palette.length]; });
            return new Chart(c, {
                type: 'doughnut',
                data: { labels: labels, datasets: [{ data: data, backgroundColor: colors, borderWidth: 0, hoverOffset: 4 }] },
                options: {
                    // Chart.js defaults doughnuts to a 1:1 box (tall). Flatten it so the card matches
                    // the bar/line cards beside it; callers can override via opts.aspectRatio.
                    responsive: true, maintainAspectRatio: true, aspectRatio: opts.aspectRatio || 2.4, cutout: '62%',
                    plugins: {
                        legend: { position: 'right', labels: { boxWidth: 10, font: { size: 11 }, padding: 10 } },
                        tooltip: { callbacks: { label: function (ctx) {
                            return ctx.label + ': ' + (opts.money ? naira(ctx.parsed) : compact(ctx.parsed));
                        } } }
                    }
                }
            });
        }
    };

    window.SLCharts = SLCharts;
})();
