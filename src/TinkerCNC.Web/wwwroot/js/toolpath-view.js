// 3D view of a program's toolpath with the tool head, used by Components/Job/ToolpathPanel.razor.
// The toolpath comes from /toolpath/{address} (format in Services/ToolpathEndpoints.cs). It is drawn in the machine's
// space: once the machine is known, the machine (MachineScene: travel, bed, model) is around it and the toolpath and
// the tool sit at the work origin, where the program really cuts. "live" draws only the moves already executed,
// "preview" all of them, as they are once the job has run. Renders only when something changes, so an idle view
// costs no CPU.
import * as THREE from '../lib/three/three.module.min.js';
import { FusionControls } from './fusion-controls.js';
import { MachineScene, StockBlock } from './machine-scene.js';
import { uiText } from './ui-texts.js';

const MOVE_RAPID = 1; // ToolpathMove (Jump = 0 is never drawn)
const MOVE_FEED = 2;
const HEADER_BYTES = 12;
const DEFAULT_TOOL_DIAMETER = 3.175;
const MAX_TWEEN_MS = 1000;

const COLORS = {
    background: 0x0b0e12,
    grid: 0x1e252f,
    gridCentre: 0x2f3946,
    rapid: 0x8a95a5,
    noTool: 0x5b6573,
    shank: 0xb8c0cc,
    flute: 0xf08a24,
};

// Directions the camera looks from; Z is up like on the machine.
const VIEWS = {
    iso: new THREE.Vector3(0.55, -1, 0.8),
    top: new THREE.Vector3(0, -0.0001, 1),
    front: new THREE.Vector3(0, -1, 0.0001),
    right: new THREE.Vector3(1, 0, 0.0001),
};

export function createToolpathView(container) {
    try {
        return new ToolpathView(container);
    } catch (error) {
        container.textContent = uiText('viewUnavailable', '3D view unavailable: {0}', error.message);
        container.classList.add('toolpath-error');
        return null;
    }
}

class ToolpathView {
    constructor(container) {
        this.container = container;
        this.renderer = new THREE.WebGLRenderer({ antialias: true });
        this.renderer.setPixelRatio(window.devicePixelRatio);
        this.renderer.setClearColor(COLORS.background);
        container.appendChild(this.renderer.domElement);

        this.scene = new THREE.Scene();
        this.scene.add(new THREE.AmbientLight(0xffffff, 1.2));
        const sun = new THREE.DirectionalLight(0xffffff, 2);
        sun.position.set(1, -2, 3);
        this.scene.add(sun);

        this.camera = new THREE.PerspectiveCamera(40, 1, 0.1, 100000);
        this.camera.up.set(0, 0, 1);
        this.machine = new MachineScene(this.scene, () => this.onMachineChanged());
        // Everything in work coordinates (toolpath, tool, grid, work axes) sits in this group, placed at the work origin.
        this.work = new THREE.Group();
        this.scene.add(this.work);
        this.bounds = new THREE.Box3(new THREE.Vector3(0, 0, -10), new THREE.Vector3(100, 100, 10)); // of the toolpath, work coordinates
        this.decor = new THREE.Group();
        this.work.add(this.decor);
        this.cutBounds = null; // of the feed moves, work coordinates: the stock's area
        this.stock = new StockBlock(this.work);
        this.passes = [];
        // the cursor is on the toolpath, the grid or the machine: orbits turn about that point, zooms head for it
        this.controls = new FusionControls(this.camera, this.renderer.domElement,
            () => [...this.passes.filter(p => p.object.visible).map(p => p.object), this.decor, this.machine.group]);
        this.controls.addEventListener('change', () => this.requestRender());
        this.rapidsVisible = true;
        this.mode = 'live';
        this.framing = 'iso';
        this.placed = false; // framed since the work origin became known

        this.head = createHead();
        this.head.visible = false;
        this.work.add(this.head);
        this.headTarget = null;
        this.tween = null;
        this.lastHeadUpdate = 0;

        this.state = { doneLine: null, head: null, toolDiameter: null, workOrigin: null, machine: null };
        this.cutLine = 0; // moves before this line are drawn
        this.loadToken = 0;
        this.frame = 0;

        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(container);
        this.buildDecor();
        this.setView('iso');
    }

    /** Loads the toolpath; <segments> colour the feed moves by tool: [{ firstLine, lastLine, color }]. */
    async load(url, segments) {
        const token = ++this.loadToken;
        const response = await fetch(url, { cache: 'no-store' });
        if (token !== this.loadToken || this.disposed) return;
        if (!response.ok) {
            this.clear();
            return;
        }
        const path = parseToolpath(await response.arrayBuffer());
        if (token !== this.loadToken || this.disposed) return;
        this.build(path, segments);
    }

    clear() {
        this.loadToken++;
        this.removePasses();
        this.requestRender();
    }

    /**
     * Progress, head and where it is: { doneLine, head: [x, y, z] | null (work coordinates, on the part), toolDiameter,
     * workOrigin: [x, y, z] | null (machine coordinates of work zero), machine: see MachineScene.set | null }.
     * doneLine is the line being executed; null (no job running) keeps what was cut, so a finished job stays visible.
     */
    update(state) {
        this.state = state;
        if (state.doneLine != null) this.cutLine = state.doneLine;
        const travelChanged = this.machine.set(state.machine);
        this.work.position.set(...(this.inMachine ? state.workOrigin : [0, 0, 0]));
        if (this.grid) this.grid.visible = !this.inMachine; // the machine's bed takes its place
        this.applyProgress();
        this.showStock();
        this.moveHead(state.head);
        this.sizeHead();
        this.moveSpindle();
        if (travelChanged || (this.inMachine && !this.placed)) {
            this.placed = this.inMachine;
            this.setView(this.framing);
        }
        this.requestRender();
    }

    /** Whether the toolpath is shown in the machine (its travel and the work origin are known). */
    get inMachine() {
        return this.machine.known && !!this.state.workOrigin;
    }

    /** 'live': the moves executed so far; 'preview': every move, as once the job has run. */
    setMode(mode) {
        this.mode = mode;
        this.applyProgress();
        this.requestRender();
    }

    showRapids(visible) {
        this.rapidsVisible = visible;
        for (const pass of this.passes) pass.object.visible = !pass.rapid || visible;
        this.requestRender();
    }

    /**
     * 'iso', 'top', 'front', 'right' (frame the path from there), 'fit' (keep the direction, frame the whole path) or
     * 'machine' (keep the direction, frame the whole machine).
     */
    setView(name) {
        this.framing = name === 'fit' ? this.framing : name;
        const direction = (VIEWS[name] ?? this.camera.position.clone().sub(this.controls.target)).clone().normalize();
        const box = (name === 'machine' && this.machine.bounds()) || this.pathBox();
        const centre = box.getCenter(new THREE.Vector3());
        const distance = this.fitDistance(box, centre, direction);

        this.camera.near = distance / 1000;
        this.camera.far = distance * 100;
        this.camera.position.copy(centre).addScaledVector(direction, distance);
        this.camera.lookAt(centre);
        this.camera.updateProjectionMatrix();
        this.controls.target.copy(centre);
        this.controls.update();
        this.requestRender();
    }

    /**
     * Camera distance at which every corner of the bounds is inside the view: seen from a camera one unit from the
     * centre, a corner at view (x, y, z) fits once the camera is |x| / tan(half width) + z + 1 away (z < 0 in front).
     */
    fitDistance(box, centre, direction) {
        const tanV = Math.tan(THREE.MathUtils.degToRad(this.camera.fov / 2));
        const tanH = tanV * this.camera.aspect;
        this.camera.position.copy(centre).add(direction);
        this.camera.lookAt(centre);
        this.camera.updateMatrixWorld();
        const { min, max } = box;
        let distance = 1;
        for (const x of [min.x, max.x]) for (const y of [min.y, max.y]) for (const z of [min.z, max.z]) {
            const v = new THREE.Vector3(x, y, z).applyMatrix4(this.camera.matrixWorldInverse);
            distance = Math.max(distance, Math.max(Math.abs(v.x) / tanH, Math.abs(v.y) / tanV) + v.z + 1);
        }
        return distance * 1.08;
    }

    /** In the machine, the stock from the bed up to work zero under the cutting moves (work coordinates). */
    showStock() {
        const bedZ = this.machine.bedZ;
        if (!this.inMachine || bedZ == null || !this.cutBounds) {
            this.stock.show(null, null);
            return;
        }
        const { min, max } = this.cutBounds;
        this.stock.show([min.x, min.y, bedZ - this.work.position.z], [max.x, max.y, 0]);
    }

    /** The toolpath's extent where it is drawn (at the work origin in the machine). */
    pathBox() {
        return this.bounds.clone().translate(this.work.position);
    }

    /** The model arrived: frame again when the view shows the whole machine. */
    onMachineChanged() {
        if (this.framing === 'machine') this.setView('machine');
        this.sizeHead();
        this.moveSpindle();
        this.requestRender();
    }

    dispose() {
        this.disposed = true;
        cancelAnimationFrame(this.frame);
        this.resizeObserver.disconnect();
        this.controls.dispose();
        this.machine.dispose();
        this.removePasses();
        this.scene.traverse(o => { o.geometry?.dispose(); o.material?.dispose(); });
        this.renderer.dispose();
        this.renderer.domElement.remove();
    }

    // ---------- toolpath ----------

    build(path, segments) {
        this.removePasses();
        const position = new THREE.BufferAttribute(path.positions, 3);
        const color = new THREE.BufferAttribute(toolColors(path, segments), 3);

        const feeds = pairsOf(path, MOVE_FEED);
        const rapids = pairsOf(path, MOVE_RAPID);
        this.addPass(feeds, position, color, false, new THREE.LineBasicMaterial({ vertexColors: true }));
        this.addPass(rapids, position, null, true,
            new THREE.LineBasicMaterial({ color: COLORS.rapid, transparent: true, opacity: 0.45, depthWrite: false }));

        this.cutLine = this.state.doneLine ?? 0;
        this.bounds = boundsOf(path);
        this.cutBounds = boundsOf(path, MOVE_FEED);
        this.showStock();
        this.buildDecor();
        this.applyProgress();
        this.sizeHead();
        this.setView(this.framing === 'machine' ? 'machine' : 'fit');
    }

    /** The moves of one kind; the draw range grows with the progress. */
    addPass(pairs, position, color, rapid, material) {
        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', position);
        if (color) geometry.setAttribute('color', color);
        geometry.setIndex(new THREE.BufferAttribute(pairs.index, 1));
        const object = new THREE.LineSegments(geometry, material);
        object.frustumCulled = false;
        object.visible = !rapid || this.rapidsVisible;
        this.work.add(object);
        this.passes.push({ object, rapid, lines: pairs.lines });
    }

    removePasses() {
        for (const pass of this.passes) {
            this.work.remove(pass.object);
            pass.object.geometry.dispose();
            pass.object.material.dispose();
        }
        this.passes = [];
    }

    applyProgress() {
        for (const pass of this.passes) {
            const pairs = this.mode === 'preview' ? pass.lines.length : countBelow(pass.lines, this.cutLine);
            pass.object.geometry.setDrawRange(0, pairs * 2);
        }
    }

    /** A grid under the lowest point (outside the machine) and the work origin's axes, sized to the toolpath. */
    buildDecor() {
        for (const child of [...this.decor.children]) {
            this.decor.remove(child);
            child.geometry?.dispose();
            child.material?.dispose();
        }
        const size = this.bounds.getSize(new THREE.Vector3());
        const centre = this.bounds.getCenter(new THREE.Vector3());
        const step = niceStep(Math.max(size.x, size.y) / 12);
        const extent = Math.ceil(Math.max(size.x, size.y) * 1.3 / step / 2) * step * 2;
        const grid = new THREE.GridHelper(extent, Math.round(extent / step), COLORS.gridCentre, COLORS.grid);
        grid.rotation.x = Math.PI / 2;
        grid.position.set(Math.round(centre.x / step) * step, Math.round(centre.y / step) * step, this.bounds.min.z);
        grid.visible = !this.inMachine;
        this.decor.add(grid);
        this.grid = grid;

        const axes = new THREE.AxesHelper(Math.max(size.length() * 0.08, 5));
        axes.material.depthTest = false;
        axes.renderOrder = 2;
        this.decor.add(axes);
    }

    // ---------- head ----------

    /** Glides to each new position over the time since the previous one, so 2 Hz status reports look smooth. */
    moveHead(head) {
        const now = performance.now();
        if (!head) {
            this.head.visible = false;
            this.tween = null;
            return;
        }
        const target = new THREE.Vector3(...head);
        if (!this.head.visible) {
            this.head.position.copy(target);
            this.head.visible = true;
        } else if (!this.headTarget || !target.equals(this.headTarget)) {
            const duration = Math.min(now - this.lastHeadUpdate, MAX_TWEEN_MS);
            this.tween = { from: this.head.position.clone(), to: target, start: now, duration };
        }
        this.headTarget = target;
        this.lastHeadUpdate = now;
    }

    /**
     * True diameter; in the machine's spindle exactly that, otherwise never smaller than 0.4 % of the part so it stays
     * findable on large jobs.
     */
    sizeHead() {
        const diameter = this.state.toolDiameter ?? DEFAULT_TOOL_DIAMETER;
        const visible = this.inMachine && this.machine.hasSpindle
            ? diameter
            : Math.max(diameter, this.bounds.getSize(new THREE.Vector3()).length() * 0.004);
        this.head.scale.setScalar(visible);
    }

    /** The model's spindle follows the drawn tool (also while it glides), in machine coordinates. */
    moveSpindle() {
        if (!this.inMachine || !this.head.visible) return;
        const tool = this.head.position.clone().add(this.work.position);
        this.machine.moveSpindle(tool.toArray(), this.state.toolDiameter ?? DEFAULT_TOOL_DIAMETER);
    }

    // ---------- rendering ----------

    resize() {
        const width = this.container.clientWidth;
        const height = this.container.clientHeight;
        if (!width || !height) return;
        this.renderer.setSize(width, height, false);
        this.camera.aspect = width / height;
        this.camera.updateProjectionMatrix();
        this.requestRender();
    }

    requestRender() {
        if (this.frame || this.disposed) return;
        this.frame = requestAnimationFrame(time => {
            this.frame = 0;
            this.animate(time);
            this.renderer.render(this.scene, this.camera);
        });
    }

    animate(time) {
        if (!this.tween) return;
        const { from, to, start, duration } = this.tween;
        const fraction = duration > 0 ? Math.min(1, (time - start) / duration) : 1;
        this.head.position.lerpVectors(from, to, fraction);
        this.moveSpindle();
        if (fraction < 1) this.requestRender();
        else this.tween = null;
    }
}

// ---------- helpers ----------

function parseToolpath(buffer) {
    const view = new DataView(buffer);
    const magic = String.fromCharCode(...new Uint8Array(buffer, 0, 4));
    if (magic !== 'TPTH' || view.getUint32(4, true) !== 1) throw new Error('Unknown toolpath format');
    const count = view.getUint32(8, true);
    const linesOffset = HEADER_BYTES + count * 12;
    return {
        count,
        positions: new Float32Array(buffer, HEADER_BYTES, count * 3),
        lines: new Int32Array(buffer, linesOffset, count),
        moves: new Uint8Array(buffer, linesOffset + count * 4, count),
    };
}

/** Index pairs (k − 1, k) of every move of one kind, with the line of each pair (ascending). */
function pairsOf(path, move) {
    let total = 0;
    for (let k = 1; k < path.count; k++) if (path.moves[k] === move) total++;
    const index = new Uint32Array(total * 2);
    const lines = new Int32Array(total);
    for (let k = 1, n = 0; k < path.count; k++) {
        if (path.moves[k] !== move) continue;
        index[2 * n] = k - 1;
        index[2 * n + 1] = k;
        lines[n++] = path.lines[k];
    }
    return { index, lines };
}

/** Colour of every point: the tool of the operation its line belongs to. */
function toolColors(path, segments) {
    const colors = new Float32Array(path.count * 3);
    const palette = segments.map(s => new THREE.Color(s.color));
    const noTool = new THREE.Color(COLORS.noTool);
    for (let k = 0, s = 0; k < path.count; k++) {
        const line = path.lines[k];
        while (s < segments.length - 1 && line > segments[s].lastLine) s++;
        const inSegment = segments.length > 0 && line >= segments[s].firstLine && line <= segments[s].lastLine;
        (inSegment ? palette[s] : noTool).toArray(colors, k * 3);
    }
    return colors;
}

/** Extent of every point, or of the ends of one kind of move (a feed move's start is the previous point). */
function boundsOf(path, move) {
    const box = new THREE.Box3();
    const point = new THREE.Vector3();
    for (let k = 0; k < path.count; k++) {
        if (move === undefined) box.expandByPoint(point.fromArray(path.positions, k * 3));
        else if (k > 0 && path.moves[k] === move) {
            box.expandByPoint(point.fromArray(path.positions, (k - 1) * 3));
            box.expandByPoint(point.fromArray(path.positions, k * 3));
        }
    }
    if (move !== undefined) return box.isEmpty() ? null : box;
    return box.isEmpty() ? new THREE.Box3(new THREE.Vector3(0, 0, -10), new THREE.Vector3(100, 100, 10)) : box;
}

/** Number of entries below <value> in an ascending array. */
function countBelow(sorted, value) {
    let low = 0, high = sorted.length;
    while (low < high) {
        const mid = (low + high) >> 1;
        if (sorted[mid] < value) low = mid + 1; else high = mid;
    }
    return low;
}

/** 1, 2 or 5 × a power of ten, at least <raw>. */
function niceStep(raw) {
    const power = 10 ** Math.floor(Math.log10(Math.max(raw, 1e-3)));
    return [1, 2, 5, 10].map(f => f * power).find(s => s >= raw);
}

/**
 * Cutter and shank of a 1 mm tool, tip at the origin, pointing down -Z; scaled to the tool. Translucent and without
 * depth writes, so the toolpath under and behind it stays visible.
 */
export function createHead() {
    const head = new THREE.Group();
    const material = color => new THREE.MeshStandardMaterial({
        color, metalness: 0.35, roughness: 0.45, transparent: true, opacity: 0.55, depthWrite: false,
    });
    const part = (radiusBottom, radiusTop, height, z, color) => {
        const geometry = new THREE.CylinderGeometry(radiusTop, radiusBottom, height, 24);
        geometry.rotateX(Math.PI / 2); // cylinders run along Y; the tool runs along Z
        const mesh = new THREE.Mesh(geometry, material(color));
        mesh.position.z = z + height / 2;
        head.add(mesh);
    };
    part(0.5, 0.5, 3, 0, COLORS.flute);
    part(0.5, 0.5, 4, 3, COLORS.shank);
    return head;
}
