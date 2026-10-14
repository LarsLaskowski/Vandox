// Time series charts on uPlot. The C# side calls create with the host element, and the chart object it
// gets back has update and destroy. Series colors and grid come from the custom properties of app.css.
import uPlot from "../lib/uplot/uPlot.esm.js";

const plotHeight = 240;
const seriesColorCount = 4;

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

export function create(element, title, labels, columns) {
    const colorScheme = window.matchMedia("(prefers-color-scheme: dark)");
    let data = columns;
    let plot = null;

    function build() {
        if (plot !== null) {
            plot.destroy();
        }

        plot = new uPlot(buildOptions(title, labels, element.clientWidth, readColors()), data, element);
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
            plot.setData(data);
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

export function createStacked(element, title, labels, columns) {
    throw new Error("not implemented");
}
