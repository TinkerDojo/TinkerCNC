// Mouse and touch navigation of the 3D views, with the bindings of Autodesk Fusion's default scheme: the middle
// button drags the view (pan), Shift + middle button orbits, the wheel zooms towards the cursor. The orbit is
// constrained: moving the mouse sideways turns the view about the vertical axis, moving it up or down tilts it, and
// never past looking straight down or up; it turns about the point under the cursor when the cursor is on something,
// otherwise about the centre of the view. The left button orbits too (mice without a middle button); one finger
// orbits, two fingers pan and pinch to zoom.
import * as THREE from '../lib/three/three.module.min.js';

const RADIANS_PER_HEIGHT = 2 * Math.PI; // a drag across the height of the view turns the view once around
const ZOOM_PER_NOTCH = 1.15;            // one wheel notch zooms by this factor
const POLE_MARGIN = 0.01;               // radians the view keeps from straight down or straight up
const PICK_PIXELS = 6;                  // the cursor counts as on a line within this many pixels

/** What a mouse button does when pressed: orbit, pan, or nothing (the right button keeps its menu). */
const MOUSE_BUTTONS = {
    0: () => 'orbit',
    1: event => event.shiftKey ? 'orbit' : 'pan',
};

export class FusionControls extends THREE.EventDispatcher {
    /**
     * @param camera a perspective camera whose up vector is the scene's vertical
     * @param element the canvas the gestures happen on
     * @param pickables () => the objects the cursor can be on (searched recursively), for the orbit pivot and the zoom point
     */
    constructor(camera, element, pickables = () => []) {
        super();
        this.camera = camera;
        this.element = element;
        this.pickables = pickables;
        this.enabled = true;

        /** What the camera looks at; orbits and pans move it, zooming keeps it under the cursor's depth. */
        this.target = new THREE.Vector3();

        /** The point the orbit in progress turns about (null between orbits). */
        this.pivot = null;

        this.pointers = new Map(); // pointerId -> { x, y }
        this.mode = null;          // 'orbit' | 'pan' while a drag is on
        this.pinch = null;         // { mid, distance } of the previous two-finger sample
        this.raycaster = new THREE.Raycaster();

        this.handlers = {
            pointerdown: event => this.pointerDown(event),
            pointermove: event => this.pointerMove(event),
            pointerup: event => this.pointerUp(event),
            pointercancel: event => this.pointerUp(event),
            wheel: event => this.wheel(event),
        };
        element.style.touchAction = 'none';
        for (const [type, handler] of Object.entries(this.handlers)) element.addEventListener(type, handler, { passive: false });
    }

    /** Points the camera at the target; for callers that set the camera and the target themselves. */
    update() {
        this.camera.lookAt(this.target);
    }

    dispose() {
        for (const [type, handler] of Object.entries(this.handlers)) this.element.removeEventListener(type, handler);
        this.pointers.clear();
    }

    // ---------- gestures ----------

    pointerDown(event) {
        if (!this.enabled) return;
        const mode = event.pointerType === 'touch' ? 'orbit' : MOUSE_BUTTONS[event.button]?.(event);
        if (!mode) return;
        event.preventDefault(); // no middle-button autoscroll, no text selection
        this.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        this.capture(event.pointerId, true);
        this.pinch = null;
        this.mode = mode;
        if (mode === 'orbit') this.pivot = this.pointAt(event.clientX, event.clientY, true) ?? this.target.clone();
    }

    pointerMove(event) {
        const pointer = this.pointers.get(event.pointerId);
        if (!pointer || !this.mode) return;
        const dx = event.clientX - pointer.x;
        const dy = event.clientY - pointer.y;
        pointer.x = event.clientX;
        pointer.y = event.clientY;
        if (this.pointers.size >= 2) this.twoFingers();
        else if (this.mode === 'orbit') this.orbit(dx, dy);
        else this.pan(dx, dy);
    }

    pointerUp(event) {
        if (!this.pointers.delete(event.pointerId)) return;
        this.capture(event.pointerId, false);
        this.pinch = null;
        if (this.pointers.size === 0) {
            this.mode = null;
            this.pivot = null;
        } else if (this.pointers.size === 1) {
            this.mode = 'orbit'; // the finger left behind carries on orbiting about the view's centre
            this.pivot = this.target.clone();
        }
    }

    wheel(event) {
        if (!this.enabled) return;
        event.preventDefault();
        const notches = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? event.deltaY / 3 : event.deltaY / 100;
        this.zoomAt(event.clientX, event.clientY, ZOOM_PER_NOTCH ** -notches);
    }

    /** Two fingers: their midpoint drags the view, their distance zooms it, towards the midpoint. */
    twoFingers() {
        const [a, b] = [...this.pointers.values()];
        const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
        const distance = Math.hypot(a.x - b.x, a.y - b.y);
        if (this.pinch) {
            this.pan(mid.x - this.pinch.mid.x, mid.y - this.pinch.mid.y);
            if (this.pinch.distance > 0) this.zoomAt(mid.x, mid.y, distance / this.pinch.distance);
        }
        this.pinch = { mid, distance };
    }

    // ---------- movements ----------

    /** Turns the view about the vertical axis through the pivot (sideways) and tilts it (up and down), short of the poles. */
    orbit(dx, dy) {
        const pivot = this.pivot ?? this.target;
        const height = this.element.clientHeight || 1;
        const up = this.camera.up;
        const offset = this.camera.position.clone().sub(this.target); // from the target to the camera
        const polar = offset.angleTo(up);
        const wanted = THREE.MathUtils.clamp(polar - RADIANS_PER_HEIGHT * dy / height, POLE_MARGIN, Math.PI - POLE_MARGIN);
        const right = new THREE.Vector3(1, 0, 0).applyQuaternion(this.camera.quaternion);
        // a positive turn about the camera's right axis takes the camera down, so the tilt is the polar change itself
        const turn = new THREE.Quaternion().setFromAxisAngle(up, -RADIANS_PER_HEIGHT * dx / height)
            .multiply(new THREE.Quaternion().setFromAxisAngle(right, wanted - polar));
        for (const point of [this.camera.position, this.target]) point.sub(pivot).applyQuaternion(turn).add(pivot);
        this.changed();
    }

    /** Drags the view: the point under the cursor at the target's depth follows the cursor. */
    pan(dx, dy) {
        const scale = this.worldPerPixel();
        const move = new THREE.Vector3(-dx * scale, dy * scale, 0).applyQuaternion(this.camera.quaternion);
        this.camera.position.add(move);
        this.target.add(move);
        this.changed();
    }

    /** Zooms by <factor> (> 1 closer) towards the point under the cursor, which stays where it is on the screen. */
    zoomAt(clientX, clientY, factor) {
        if (!(factor > 0) || factor === 1) return;
        const point = this.pointAt(clientX, clientY, false);
        const nearest = Math.max(this.camera.near * 4, 1e-3);
        const farthest = this.camera.far / 4;
        const offset = this.camera.position.clone().sub(point).divideScalar(factor);
        const distance = offset.length();
        if (distance < nearest && factor > 1 || distance > farthest && factor < 1) return;
        const position = point.clone().add(offset);
        this.target.add(position).sub(this.camera.position); // the view keeps its direction …
        this.camera.position.copy(position);
        const direction = this.target.clone().sub(this.camera.position).normalize();
        const depth = point.clone().sub(this.camera.position).dot(direction);
        if (depth > nearest) this.target.copy(this.camera.position).addScaledVector(direction, depth); // … and its centre moves to the zoomed point's depth
        this.changed();
    }

    // ---------- geometry ----------

    /**
     * The scene point under the cursor: on something pickable, or, unless <pickedOnly>, where the cursor's ray
     * crosses the plane through the target that faces the camera.
     */
    pointAt(clientX, clientY, pickedOnly) {
        this.camera.updateMatrixWorld();
        const rect = this.element.getBoundingClientRect();
        const ndc = new THREE.Vector2((clientX - rect.left) / rect.width * 2 - 1, 1 - (clientY - rect.top) / rect.height * 2);
        this.raycaster.setFromCamera(ndc, this.camera);
        this.raycaster.params.Line.threshold = this.worldPerPixel() * PICK_PIXELS;
        this.raycaster.params.Points.threshold = this.raycaster.params.Line.threshold;
        const hit = this.raycaster.intersectObjects(this.pickables(), true)[0];
        if (hit) return hit.point;
        if (pickedOnly) return null;
        const facing = new THREE.Plane().setFromNormalAndCoplanarPoint(this.target.clone().sub(this.camera.position).normalize(), this.target);
        return this.raycaster.ray.intersectPlane(facing, new THREE.Vector3()) ?? this.target.clone();
    }

    /** Scene units per pixel at the target's depth. */
    worldPerPixel() {
        const distance = this.camera.position.distanceTo(this.target);
        return 2 * distance * Math.tan(THREE.MathUtils.degToRad(this.camera.fov / 2)) / (this.element.clientHeight || 1);
    }

    changed() {
        this.camera.lookAt(this.target);
        this.camera.updateMatrixWorld();
        this.dispatchEvent({ type: 'change' });
    }

    /** Keeps the pointer's events coming while the drag leaves the canvas; a pointer that is gone already is ignored. */
    capture(pointerId, on) {
        try {
            if (on) this.element.setPointerCapture(pointerId);
            else if (this.element.hasPointerCapture(pointerId)) this.element.releasePointerCapture(pointerId);
        } catch (error) {
            if (error.name !== 'InvalidStateError' && error.name !== 'NotFoundError') throw error;
        }
    }
}
