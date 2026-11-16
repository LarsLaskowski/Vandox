// Time series charts on uPlot. The C# side calls create (lines) or createStacked (stacked areas) with the host
// element, and the chart object it gets back has update and destroy. Series colors and grid come from the custom
// properties of app.css. Labels and titles reach uPlot as strings, which it writes as text.
import uPlot from "../lib/uplot/uPlot.esm.js";

const plotHeight = 240;
const seriesColorCount = 6;
const fillAlpha = 0.35;

function readColors() {
    const style = getComputedStyle(document.documentElement);
    const series = [];

    for (let index = 1; index <= seriesColorCount; index++) {
        series.push(style.getPropertyValue("--vandox-chart-" + index).trim());
    }

    return {
        ink: style.getPropertyValue("--vandox-color-ink").trim(),
        grid: style.getPropertyValue("--vandox-chart-grid").trim(),
        brand: style.getPropertyValue("--vandox-color-brand").trim(),
        series: series
    };
}

function buildAxis(colors) {
    return {
        stroke: colors.ink,
        grid: { stroke: colors.grid, width: 1 },
        ticks: { stroke: colors.grid, width: 1 }
    };
}

function buildOptions(title, labels, width, colors) {
    const series = [{}];

    labels.forEach(function (label, index) {
        series.push({
            label: label,
            stroke: colors.series[index % colors.series.length],
            width: 2
        });
    });

    return {
        title: title,
        width: width,
        height: plotHeight,
        series: series,
        axes: [buildAxis(colors), buildAxis(colors)]
    };
}

// A color with an alpha: "#rgb" and "#rrggbb" become rgba(), anything else is used as it is.
function withAlpha(color, alpha) {
    const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(color);

    if (hex === null) {
        return color;
    }

    const digits = hex[1].length === 3 ? hex[1].replace(/./g, "$&$&") : hex[1];
    const red = Number.parseInt(digits.slice(0, 2), 16);
    const green = Number.parseInt(digits.slice(2, 4), 16);
    const blue = Number.parseInt(digits.slice(4, 6), 16);

    return "rgba(" + red + ", " + green + ", " + blue + ", " + alpha + ")";
}

// The cumulative sums of the series (a missing value counts as 0) and the bands between each series and the one above.
function stack(columns) {
    const sums = [];
    const bands = [];
    let total = columns[0].map(function () {
        return 0;
    });

    for (let index = 1; index < columns.length; index++) {
        const column = columns[index];

        total = total.map(function (sum, point) {
            return sum + (column[point] || 0);
        });
        sums.push(total);
    }

    for (let index = 1; index < columns.length - 1; index++) {
        bands.push({ series: [index + 1, index] });
    }

    return { data: [columns[0]].concat(sums), bands: bands };
}

function buildStackedOptions(title, labels, width, colors, raw) {
    const options = buildOptions(title, labels, width, colors);

    options.series.forEach(function (item, index) {
        if (index === 0) {
            return;
        }

        const color = colors.series[(index - 1) % colors.series.length];

        item.fill = withAlpha(color, fillAlpha);
        item.value = function (plot, value, seriesIndex, point) {
            const own = point === null || point === undefined ? null : raw()[seriesIndex][point];

            return own === null || own === undefined ? "--" : own.toFixed(1);
        };
    });

    options.scales = {
        y: {
            range: function (plot, min, max) {
                return [0, max > 0 ? max * 1.05 : 1];
            }
        }
    };

    return options;
}

// The columns with the bridged cells set to undefined: uPlot breaks a line only at null, so an undefined cell is
// skipped and the line is drawn across it. bridges holds per series the indexes of its bridged points.
function bridge(columns, bridges) {
    if (!bridges || bridges.length === 0) {
        return columns;
    }

    return columns.map(function (column, index) {
        const skipped = index === 0 ? undefined : bridges[index - 1];

        if (!skipped || skipped.length === 0) {
            return column;
        }

        const copy = column.slice();

        skipped.forEach(function (point) {
            copy[point] = undefined;
        });

        return copy;
    });
}

function createChart(element, title, labels, columns, stacked, bridges) {
    const colorScheme = window.matchMedia("(prefers-color-scheme: dark)");
    let data = columns;
    let skipped = bridges;
    let plot = null;

    function plotted() {
        return stacked ? stack(data).data : bridge(data, skipped);
    }

    function build() {
        if (plot !== null) {
            plot.destroy();
        }

        const colors = readColors();
        const width = element.clientWidth;

        if (stacked) {
            const options = buildStackedOptions(title, labels, width, colors, function () {
                return data;
            });

            options.bands = stack(data).bands;
            plot = new uPlot(options, plotted(), element);
        } else {
            plot = new uPlot(buildOptions(title, labels, width, colors), plotted(), element);
        }
    }

    function resize() {
        if (plot !== null && element.clientWidth > 0) {
            plot.setSize({ width: element.clientWidth, height: plotHeight });
        }
    }

    const observer = new ResizeObserver(resize);

    build();
    observer.observe(element);
    window.addEventListener("vandox-themechange", build);
    colorScheme.addEventListener("change", build);

    return {
        update: function (newColumns, newBridges) {
            data = newColumns;
            skipped = newBridges;
            plot.setData(plotted());
        },
        destroy: function () {
            observer.disconnect();
            window.removeEventListener("vandox-themechange", build);
            colorScheme.removeEventListener("change", build);

            if (plot !== null) {
                plot.destroy();
                plot = null;
            }
        }
    };
}

export function create(element, title, labels, columns, bridges) {
    return createChart(element, title, labels, columns, false, bridges);
}

// Stacked areas: every series is drawn on top of the ones before it, and the legend shows the value of each series itself.
export function createStacked(element, title, labels, columns, bridges) {
    return createChart(element, title, labels, columns, true, bridges);
}

// ---- History chart ----------------------------------------------------------------------------------------------
// createHistory draws up to eight scales (y0 .. y7), two drawn axes (the second one only from wideChart CSS pixels),
// the marks of the events and the moment. setup comes from HistoryChartInterop (times in Unix seconds), events is the
// DotNetObjectReference whose instance methods Select(start, end) and Pick(at, tolerance) report a drag and a click.
// Nothing here writes HTML: labels reach uPlot as strings, which it writes as text.

const wideChart = 480;
const dragPixels = 5;
const pickPixels = 8;
const maxScales = 8;
const dashLength = 6;

export function width(element) {
    return element.clientWidth;
}

function scaleName(index) {
    const number = Number.isInteger(index) && index >= 0 && index < maxScales ? index : 0;

    return "y" + number;
}

function historyAxis(colors, scale, label, side) {
    const axis = buildAxis(colors);

    axis.scale = scale;
    axis.side = side;
    axis.label = label;

    if (side === 1) {
        axis.grid = { show: false };
    }

    return axis;
}

function formatValue(value) {
    return value === null || value === undefined ? "--" : value.toFixed(1);
}

// The legend value of a line: the value, or "min / avg / max" when the line carries the extremes of a step.
function legendValue(line) {
    return function (plot, value, seriesIndex, point) {
        const minimums = line.minimums;
        const maximums = line.maximums;

        if (point === null || point === undefined || !minimums || minimums.length === 0 || value === null || value === undefined) {
            return formatValue(value);
        }

        return formatValue(minimums[point]) + " / " + formatValue(value) + " / " + formatValue(maximums[point]);
    };
}

function historySeries(setup, colors) {
    const series = [{}];

    setup.lines.forEach(function (line) {
        const item = {
            label: line.label,
            scale: scaleName(line.scale),
            stroke: colors.series[line.color % colors.series.length],
            width: 2,
            value: legendValue(line)
        };

        if (line.dashed) {
            item.dash = [dashLength, dashLength / 2];
        }

        series.push(item);
    });

    return series;
}

function historyScales() {
    const scales = { x: { time: true } };

    for (let index = 0; index < maxScales; index++) {
        scales["y" + index] = { auto: true };
    }

    return scales;
}

function drawLine(ctx, line) {
    ctx.save();
    ctx.setLineDash(line.dash.map(function (length) {
        return length * line.ratio;
    }));
    ctx.strokeStyle = line.color;
    ctx.lineWidth = line.lineWidth * line.ratio;
    ctx.beginPath();
    ctx.moveTo(line.x, line.top);
    ctx.lineTo(line.x, line.top + line.height);
    ctx.stroke();
    ctx.restore();
}

// Kills solid, boots dashed, restarts dotted, backup runs as shaded spans, the moment in the brand color.
function drawMarks(plot, setup, colors, moment) {
    const bbox = plot.bbox;
    const ctx = plot.ctx;
    const ratio = window.devicePixelRatio || 1;

    ctx.save();
    ctx.beginPath();
    ctx.rect(bbox.left, bbox.top, bbox.width, bbox.height);
    ctx.clip();

    setup.marks.forEach(function (mark) {
        const x = Math.round(plot.valToPos(mark.at, "x", true));

        if (mark.kind === "BackupRun") {
            const end = mark.endsAt === null || mark.endsAt === undefined ? mark.at : mark.endsAt;
            const right = Math.round(plot.valToPos(end, "x", true));

            ctx.fillStyle = withAlpha(colors.ink, 0.12);
            ctx.fillRect(x, bbox.top, Math.max(right - x, ratio), bbox.height);
        } else {
            let dash = [];

            if (mark.kind === "Boot") {
                dash = [8, 4];
            } else if (mark.kind === "ServiceRestart") {
                dash = [2, 3];
            }

            drawLine(ctx, { x: x, top: bbox.top, height: bbox.height, ratio: ratio, dash: dash, color: colors.ink, lineWidth: 1 });
        }
    });

    if (moment !== null) {
        drawLine(ctx, { x: Math.round(plot.valToPos(moment, "x", true)), top: bbox.top, height: bbox.height, ratio: ratio, dash: [], color: colors.brand, lineWidth: 2 });
    }

    ctx.restore();
}

function report(events, method, first, second) {
    if (events === null) {
        return;
    }

    // A call into a disposed reference fails; the page has already moved on then.
    events.invokeMethodAsync(method, first, second).catch(function () {});
}

export function createHistory(element, setup, columns, events) {
    const colorScheme = window.matchMedia("(prefers-color-scheme: dark)");
    const bridges = setup.lines.map(function (line) {
        return line.bridges;
    });
    let reference = events;
    let moment = setup.moment === null || setup.moment === undefined ? null : setup.moment;
    let plot = null;
    let wide = null;
    let colors = null;

    function onSelect(u) {
        const select = u.select;

        if (select.width > 0) {
            report(reference, "Select", u.posToVal(select.left, "x"), u.posToVal(select.left + select.width, "x"));
        }

        u.setSelect({ left: 0, top: 0, width: 0, height: 0 }, false);
    }

    function build() {
        if (plot !== null) {
            plot.destroy();
        }

        colors = readColors();

        const chartWidth = element.clientWidth;
        const axes = [buildAxis(colors), historyAxis(colors, "y0", setup.axes[0] || "", 3)];

        wide = chartWidth >= wideChart;

        if (wide && setup.axes.length > 1) {
            axes.push(historyAxis(colors, "y1", setup.axes[1], 1));
        }

        plot = new uPlot({
            width: chartWidth,
            height: plotHeight,
            scales: historyScales(),
            series: historySeries(setup, colors),
            axes: axes,
            cursor: { drag: { x: true, y: false, dist: dragPixels, setScale: false } },
            hooks: {
                setSelect: [onSelect],
                draw: [function (u) {
                    drawMarks(u, setup, colors, moment);
                }]
            }
        }, bridge(columns, bridges), element);

        listen(plot.over);
    }

    // A press that moved by less than dragPixels is a click: the time under the pointer with the tolerance of pickPixels.
    function listen(over) {
        let pressed = null;

        over.addEventListener("mousedown", function (event) {
            pressed = { x: event.clientX, y: event.clientY };
        });
        over.addEventListener("mouseup", function (event) {
            const start = pressed;

            pressed = null;

            if (start === null || Math.hypot(event.clientX - start.x, event.clientY - start.y) >= dragPixels) {
                return;
            }

            const left = event.clientX - over.getBoundingClientRect().left;

            report(reference, "Pick", plot.posToVal(left, "x"), plot.posToVal(left + pickPixels, "x") - plot.posToVal(left, "x"));
        });
    }

    function resize() {
        const chartWidth = element.clientWidth;

        if (plot === null || chartWidth <= 0) {
            return;
        }

        if ((chartWidth >= wideChart) !== wide) {
            build();
        } else {
            plot.setSize({ width: chartWidth, height: plotHeight });
        }
    }

    const observer = new ResizeObserver(resize);

    build();
    observer.observe(element);
    window.addEventListener("vandox-themechange", build);
    colorScheme.addEventListener("change", build);

    return {
        mark: function (seconds) {
            moment = seconds === null || seconds === undefined ? null : seconds;
            plot.redraw(false);
        },
        destroy: function () {
            reference = null;
            observer.disconnect();
            window.removeEventListener("vandox-themechange", build);
            colorScheme.removeEventListener("change", build);

            if (plot !== null) {
                plot.destroy();
                plot = null;
            }
        }
    };
}
