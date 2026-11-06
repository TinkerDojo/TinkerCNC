// Zoom and pan for an image filling a stage element (the live camera, Components/Machine/CameraPanel.razor).
// The transform lives in CSS variables on the stage (--zoom, --pan-x, --pan-y), which the stylesheet applies to
// the image, so it survives the image element being re-created when the stream restarts.
// Wheel zooms towards the cursor, drag pans, two fingers pinch, double-click toggles a close-up.

const MIN_ZOOM = 1;
const MAX_ZOOM = 8;
const WHEEL_STEP = 1.0015;   // zoom factor per wheel delta unit
const CLOSE_UP = 2.5;        // double-click zoom

/** <labelSelector> finds the zoom level text near the stage; it is looked up each time, as it comes and goes. */
export function createZoomView(stage, labelSelector) {
    return new ZoomView(stage, labelSelector);
}

class ZoomView {
    constructor(stage, labelSelector) {
        this.stage = stage;
        this.labelSelector = labelSelector;
        this.zoom = 1;
        this.x = 0;
        this.y = 0;
        this.pointers = new Map(); // pointerId → {x, y}, for drag and pinch
        this.handlers = {
            wheel: e => this.onWheel(e),
            pointerdown: e => this.onPointerDown(e),
            pointermove: e => this.onPointerMove(e),
            pointerup: e => this.onPointerUp(e),
            pointercancel: e => this.onPointerUp(e),
            dblclick: e => this.onDoubleClick(e),
        };
        for (const [name, handler] of Object.entries(this.handlers))
            stage.addEventListener(name, handler, name === 'wheel' ? { passive: false } : undefined);
        this.resizeObserver = new ResizeObserver(() => this.apply()); // fullscreen, window size
        this.resizeObserver.observe(stage);
        this.apply();
    }

    /** Zooms by a factor around the stage centre (the +/- buttons). */
    zoomBy(factor) {
        const { width, height } = this.stage.getBoundingClientRect();
        this.zoomAt(this.zoom * factor, width / 2, height / 2);
    }

    reset() {
        this.zoom = 1;
        this.x = 0;
        this.y = 0;
        this.apply();
    }

    dispose() {
        this.resizeObserver.disconnect();
        for (const [name, handler] of Object.entries(this.handlers)) this.stage.removeEventListener(name, handler);
    }

    // ---------- input ----------

    onWheel(e) {
        if (!this.hasImage()) return;
        e.preventDefault();
        const p = this.local(e);
        this.zoomAt(this.zoom * Math.pow(WHEEL_STEP, -e.deltaY), p.x, p.y);
    }

    onPointerDown(e) {
        if (!this.hasImage() || e.button > 0 || e.target.closest('button, a')) return;
        this.stage.setPointerCapture(e.pointerId);
        this.pointers.set(e.pointerId, this.local(e));
        this.stage.classList.add('dragging');
    }

    onPointerMove(e) {
        const previous = this.pointers.get(e.pointerId);
        if (!previous) return;
        const current = this.local(e);
        if (this.pointers.size === 1) {
            this.panBy(current.x - previous.x, current.y - previous.y);
        } else if (this.pointers.size === 2) {
            const other = [...this.pointers.entries()].find(([id]) => id !== e.pointerId)[1];
            const before = distance(previous, other);
            const after = distance(current, other);
            const centre = midpoint(current, other);
            if (before > 0) this.zoomAt(this.zoom * after / before, centre.x, centre.y);
        }
        this.pointers.set(e.pointerId, current);
    }

    onPointerUp(e) {
        this.pointers.delete(e.pointerId);
        if (this.pointers.size === 0) this.stage.classList.remove('dragging');
    }

    onDoubleClick(e) {
        if (!this.hasImage() || e.target.closest('button, a')) return;
        const p = this.local(e);
        if (this.zoom > MIN_ZOOM) this.reset();
        else this.zoomAt(CLOSE_UP, p.x, p.y);
    }

    // ---------- geometry ----------

    /** Keeps the image point under (px, py) in place while changing the zoom. */
    zoomAt(zoom, px, py) {
        const next = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, zoom));
        const u = (px - this.x) / this.zoom;
        const v = (py - this.y) / this.zoom;
        this.zoom = next;
        this.x = px - u * next;
        this.y = py - v * next;
        this.apply();
    }

    panBy(dx, dy) {
        this.x += dx;
        this.y += dy;
        this.apply();
    }

    /** Clamps the pan so the image always covers the stage, then publishes the transform. */
    apply() {
        const { width, height } = this.stage.getBoundingClientRect();
        this.x = Math.min(0, Math.max(width - width * this.zoom, this.x));
        this.y = Math.min(0, Math.max(height - height * this.zoom, this.y));
        const style = this.stage.style;
        style.setProperty('--zoom', this.zoom);
        style.setProperty('--pan-x', `${this.x}px`);
        style.setProperty('--pan-y', `${this.y}px`);
        this.stage.classList.toggle('zoomed', this.zoom > MIN_ZOOM);
        const label = this.stage.parentElement?.querySelector(this.labelSelector);
        if (label) label.textContent = `${this.zoom.toFixed(1)}×`;
    }

    local(e) {
        const rect = this.stage.getBoundingClientRect();
        return { x: e.clientX - rect.left, y: e.clientY - rect.top };
    }

    hasImage() {
        return this.stage.querySelector('img') !== null;
    }
}

function distance(a, b) {
    return Math.hypot(a.x - b.x, a.y - b.y);
}

function midpoint(a, b) {
    return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}
