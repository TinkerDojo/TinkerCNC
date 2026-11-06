// Wheel zoom on the machine view's top map (Components/Machine/MachineViewPanel.razor). The zoom is the SVG's viewBox:
// it changes here at once, towards the point under the pointer, and is then reported to .NET, which renders the same
// viewBox and sizes the markers to it. The wrapper carries the whole map's viewBox as data-full ("x y width height").

const MAX_ZOOM = 10;
const WHEEL_STEP = 1.0015;  // zoom factor per wheel delta unit
const REPORT_DELAY = 120;   // ms after the last wheel step before .NET is told

export function attachMapZoom(wrapper, dotnet) {
    if (wrapper.dataset.mapZoomAttached) return;
    wrapper.dataset.mapZoomAttached = 'true';
    let report = 0;

    wrapper.addEventListener('wheel', e => {
        const map = e.target.closest('svg.mview-top');
        if (!map) return;
        e.preventDefault();
        const next = zoomed(map, wrapper.dataset.full.split(' ').map(Number), Math.pow(WHEEL_STEP, e.deltaY), e);
        map.setAttribute('viewBox', next.join(' '));
        clearTimeout(report);
        report = setTimeout(() => dotnet.invokeMethodAsync('ZoomMap', isFull(next, wrapper) ? null : toWindow(next))
            .catch(error => console.error('Map zoom failed', error)), REPORT_DELAY);
    }, { passive: false });
}

/** The viewBox after scaling the shown part by <factor> around the pointer, kept inside the whole map. */
function zoomed(map, full, factor, e) {
    const { x, y, width, height } = map.viewBox.baseVal;
    const pointer = new DOMPoint(e.clientX, e.clientY).matrixTransform(map.getScreenCTM().inverse());
    const scale = Math.min(full[2], Math.max(full[2] / MAX_ZOOM, width * factor)) / width;
    const w = width * scale, h = height * scale;
    return [
        clamp(pointer.x - (pointer.x - x) * scale, full[0], full[0] + full[2] - w),
        clamp(pointer.y - (pointer.y - y) * scale, full[1], full[1] + full[3] - h),
        w, h,
    ];
}

function isFull(viewBox, wrapper) {
    return viewBox[2] >= +wrapper.dataset.full.split(' ')[2] - 1e-6;
}

function toWindow([x, y, width, height]) {
    return { x, y, width, height };
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}
