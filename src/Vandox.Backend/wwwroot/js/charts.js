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

function createChart(element, title, labels, columns, stacked) {
    const colorScheme = window.matchMedia("(prefers-color-scheme: dark)");
    let data = columns;
    let plot = null;

    function plotted() {
        return stacked ? stack(data).data : data;
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
        update: function (newColumns) {
            data = newColumns;
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

export function create(element, title, labels, columns) {
    return createChart(element, title, labels, columns, false);
}

// Stacked areas: every series is drawn on top of the ones before it, and the legend shows the value of each series itself.
export function createStacked(element, title, labels, columns) {
    return createChart(element, title, labels, columns, true);
}
