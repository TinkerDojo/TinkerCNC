// Click-to-move on the machine view's top map (Components/Machine/MachineViewPanel.razor). The map is an SVG whose
// user units are millimetres from the travel's left/back corner; the wrapper carries the travel as data attributes
// (data-xmin, data-ymax, data-width, data-height) and whether moving is allowed (data-clickable). Hovering shows the
// machine coordinates under the pointer without a server round trip; a click asks .NET to move there.

export function attachMapClick(wrapper, dotnet) {
    if (wrapper.dataset.mapClickAttached) return; // one click must never send a move twice
    wrapper.dataset.mapClickAttached = 'true';
    const svg = () => wrapper.querySelector('svg.mview-top');
    const label = () => wrapper.parentElement?.querySelector('.mview-hover');

    /** Machine X/Y under the pointer, or null outside the travel area. */
    const machinePoint = e => {
        const map = svg();
        if (!map) return null;
        const p = new DOMPoint(e.clientX, e.clientY).matrixTransform(map.getScreenCTM().inverse());
        const d = wrapper.dataset;
        if (p.x < 0 || p.y < 0 || p.x > +d.width || p.y > +d.height) return null;
        return { x: round(+d.xmin + p.x), y: round(+d.ymax - p.y) };
    };

    wrapper.addEventListener('mousemove', e => {
        const point = e.target.closest('svg.mview-top') ? machinePoint(e) : null;
        const text = label();
        if (text) text.textContent = point ? `X ${point.x}  Y ${point.y}` : '';
    });
    wrapper.addEventListener('mouseleave', () => { const text = label(); if (text) text.textContent = ''; });
    wrapper.addEventListener('click', e => {
        if (wrapper.dataset.clickable !== 'true' || !e.target.closest('svg.mview-top')) return;
        const point = machinePoint(e);
        if (point) dotnet.invokeMethodAsync('MoveToAsync', point.x, point.y).catch(error => console.error('Map click failed', error));
    });
}

function round(value) {
    return Math.round(value * 10) / 10;
}
