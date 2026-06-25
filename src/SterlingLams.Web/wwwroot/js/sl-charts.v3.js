/*
 * SLCharts v3 — a tiny, dependency-free SVG chart renderer for the admin/inventory backends.
 * Static by design: charts are plain inline SVG, so there is NO animation, ever, and no Chart.js.
 * Keeps the same API as before so the views don't change:
 *
 *   SLCharts.line(id, labels, [{label, data, money}])
 *   SLCharts.bar(id, labels, data, { money, label })
 *   SLCharts.hbar(id, labels, data, { money })
 *   SLCharts.doughnut(id, labels, data, { money, colors })
 */
(function () {
    var palette = ['#10b981', '#0ea5e9', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899', '#14b8a6', '#64748b'];
    var AXIS = '#9ca3af', GRID = '#f0f0f0', TXT = '#6b7280';

    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]; }); }
    var NAIRA = String.fromCharCode(0x20A6); // ₦ via code point — never garbles regardless of file/page charset
    function money(v) { return NAIRA + Math.round(v).toLocaleString('en-US'); }
    function num(v) { return Math.round(v).toLocaleString('en-US'); }
    function fmt(v, isMoney) { return isMoney ? money(v) : num(v); }
    // "nice" upper bound for an axis so ticks land on round numbers.
    function niceMax(max) {
        if (max <= 0) return 1;
        var pow = Math.pow(10, Math.floor(Math.log10(max)));
        var n = max / pow;
        var step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return step * pow;
    }
    // Replace the placeholder <canvas id> (or any element) with a div holding the SVG.
    function mount(id, svg) {
        var host = document.getElementById(id);
        if (!host) return;
        var box = document.createElement('div');
        box.className = 'slc-chart';
        box.innerHTML = svg;
        host.parentNode.replaceChild(box, host);
    }
    function svgOpen(h) { return '<svg viewBox="0 0 640 ' + h + '" width="100%" preserveAspectRatio="xMidYMid meet" font-family="ui-sans-serif,system-ui,-apple-system,Segoe UI,sans-serif">'; }

    // Shared cartesian frame (line + vertical bars): y grid/ticks + x labels. cb(plot) draws series.
    function cartesian(labels, maxVal, isMoney, draw) {
        var W = 640, H = 230, padL = 64, padR = 14, padT = 12, padB = 28;
        var x0 = padL, x1 = W - padR, y0 = padT, y1 = H - padB;
        var top = niceMax(maxVal), ticks = 4;
        var s = svgOpen(H);
        for (var i = 0; i <= ticks; i++) {
            var gy = y1 - (y1 - y0) * (i / ticks);
            var val = top * (i / ticks);
            s += '<line x1="' + x0 + '" y1="' + gy.toFixed(1) + '" x2="' + x1 + '" y2="' + gy.toFixed(1) + '" stroke="' + GRID + '" stroke-width="1"/>';
            s += '<text x="' + (x0 - 6) + '" y="' + (gy + 3).toFixed(1) + '" text-anchor="end" font-size="10" fill="' + TXT + '">' + fmt(val, isMoney) + '</text>';
        }
        var n = labels.length;
        var step = Math.max(1, Math.ceil(n / 8));
        for (var j = 0; j < n; j++) {
            if (j % step !== 0 && j !== n - 1) continue;
            var lx = n === 1 ? (x0 + x1) / 2 : x0 + (x1 - x0) * (j / (n - 1));
            s += '<text x="' + lx.toFixed(1) + '" y="' + (y1 + 16) + '" text-anchor="middle" font-size="10" fill="' + TXT + '">' + esc(labels[j]) + '</text>';
        }
        s += draw({ x0: x0, x1: x1, y0: y0, y1: y1, top: top, n: n });
        return s + '</svg>';
    }

    var SLCharts = {
        palette: palette,

        line: function (id, labels, series) {
            series = series || [];
            var isMoney = series.some(function (s) { return s.money; });
            var max = 0;
            series.forEach(function (se) { (se.data || []).forEach(function (v) { if (v > max) max = v; }); });
            var svg = cartesian(labels, max, isMoney, function (p) {
                var out = '';
                series.forEach(function (se, si) {
                    var col = se.color || palette[si % palette.length];
                    var data = se.data || [], n = data.length;
                    var X = function (i) { return n === 1 ? (p.x0 + p.x1) / 2 : p.x0 + (p.x1 - p.x0) * (i / (n - 1)); };
                    var Y = function (v) { return p.y1 - (p.y1 - p.y0) * (v / p.top); };
                    var pts = data.map(function (v, i) { return X(i).toFixed(1) + ',' + Y(v).toFixed(1); });
                    if (n > 1 && si === 0) {
                        out += '<polygon points="' + p.x0 + ',' + p.y1 + ' ' + pts.join(' ') + ' ' + p.x1 + ',' + p.y1 + '" fill="' + col + '" fill-opacity="0.08"/>';
                    }
                    out += '<polyline points="' + pts.join(' ') + '" fill="none" stroke="' + col + '" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>';
                    data.forEach(function (v, i) { out += '<circle cx="' + X(i).toFixed(1) + '" cy="' + Y(v).toFixed(1) + '" r="2" fill="' + col + '"><title>' + esc(labels[i]) + ': ' + fmt(v, isMoney) + '</title></circle>'; });
                });
                return out;
            });
            mount(id, svg);
            return {};
        },

        bar: function (id, labels, data, opts) {
            opts = opts || {}; data = data || [];
            var isMoney = !!opts.money, max = Math.max.apply(null, data.concat([0]));
            var svg = cartesian(labels, max, isMoney, function (p) {
                var out = '', n = data.length, slot = (p.x1 - p.x0) / Math.max(1, n);
                var bw = Math.min(48, slot * 0.6);
                data.forEach(function (v, i) {
                    var cx = p.x0 + slot * (i + 0.5), h = (p.y1 - p.y0) * (v / p.top);
                    out += '<rect x="' + (cx - bw / 2).toFixed(1) + '" y="' + (p.y1 - h).toFixed(1) + '" width="' + bw.toFixed(1) + '" height="' + h.toFixed(1) + '" rx="3" fill="' + palette[0] + '" fill-opacity="0.85"><title>' + esc(labels[i]) + ': ' + fmt(v, isMoney) + '</title></rect>';
                });
                return out;
            });
            mount(id, svg);
            return {};
        },

        hbar: function (id, labels, data, opts) {
            opts = opts || {}; data = data || [];
            var isMoney = !!opts.money, n = data.length;
            var max = niceMax(Math.max.apply(null, data.concat([0])));
            var rowH = 30, padT = 8, padB = 8, W = 640, labelW = 150, valW = 92;
            var H = padT + padB + n * rowH;
            var x0 = labelW, x1 = W - valW;
            var s = svgOpen(H);
            data.forEach(function (v, i) {
                var cy = padT + i * rowH, barY = cy + 5, bh = rowH - 12;
                var w = (x1 - x0) * (v / max);
                var col = palette[i % palette.length];
                s += '<text x="' + (labelW - 8) + '" y="' + (cy + rowH / 2 + 3).toFixed(1) + '" text-anchor="end" font-size="11" fill="#374151">' + esc(String(labels[i]).length > 22 ? String(labels[i]).slice(0, 21) + '…' : labels[i]) + '</text>';
                s += '<rect x="' + x0 + '" y="' + barY.toFixed(1) + '" width="' + Math.max(0, w).toFixed(1) + '" height="' + bh + '" rx="3" fill="' + col + '" fill-opacity="0.85"/>';
                s += '<text x="' + (x1 + 6) + '" y="' + (cy + rowH / 2 + 3).toFixed(1) + '" font-size="10" fill="' + TXT + '">' + fmt(v, isMoney) + '</text>';
            });
            mount(id, s + '</svg>');
            return {};
        },

        doughnut: function (id, labels, data, opts) {
            opts = opts || {}; data = data || []; labels = labels || [];
            var isMoney = !!opts.money;
            var colors = opts.colors || labels.map(function (_, i) { return palette[i % palette.length]; });
            var total = data.reduce(function (a, b) { return a + (b || 0); }, 0) || 1;
            var H = 200, cx = 110, cy = 100, rO = 80, rI = 50;
            var s = svgOpen(H);
            var ang = -Math.PI / 2;
            if (data.every(function (v) { return !v; })) {
                s += '<circle cx="' + cx + '" cy="' + cy + '" r="' + ((rO + rI) / 2) + '" fill="none" stroke="' + GRID + '" stroke-width="' + (rO - rI) + '"/>';
            } else {
                data.forEach(function (v, i) {
                    if (!v) return;
                    var frac = v / total, a2 = ang + frac * 2 * Math.PI, large = frac > 0.5 ? 1 : 0;
                    var p = function (r, a) { return (cx + r * Math.cos(a)).toFixed(2) + ' ' + (cy + r * Math.sin(a)).toFixed(2); };
                    s += '<path d="M ' + p(rO, ang) + ' A ' + rO + ' ' + rO + ' 0 ' + large + ' 1 ' + p(rO, a2) +
                         ' L ' + p(rI, a2) + ' A ' + rI + ' ' + rI + ' 0 ' + large + ' 0 ' + p(rI, ang) + ' Z" fill="' + colors[i % colors.length] + '">' +
                         '<title>' + esc(labels[i]) + ': ' + fmt(v, isMoney) + ' (' + Math.round(frac * 100) + '%)</title></path>';
                    ang = a2;
                });
            }
            // Legend
            var ly = 24, lx = 230;
            labels.forEach(function (lab, i) {
                s += '<rect x="' + lx + '" y="' + (ly - 9) + '" width="10" height="10" rx="2" fill="' + colors[i % colors.length] + '"/>';
                s += '<text x="' + (lx + 16) + '" y="' + ly + '" font-size="11" fill="#374151">' + esc(lab) + '</text>';
                s += '<text x="620" y="' + ly + '" text-anchor="end" font-size="11" fill="' + TXT + '">' + fmt(data[i] || 0, isMoney) + '</text>';
                ly += 22;
            });
            mount(id, s + '</svg>');
            return {};
        }
    };

    window.SLCharts = SLCharts;
})();
