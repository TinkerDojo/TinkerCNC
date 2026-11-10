// The machine in a 3D scene, shared by the machine view (machine-view.js) and the toolpath view (toolpath-view.js):
// travel box, bed, clearance plane and, when the machine is known (Services/MachineModels.cs), its model with the
// spindle following the tool. Machine coordinates in mm; Z is up.
import * as THREE from '../lib/three/three.module.min.js';
import { loadMachineModel } from './machine-model.js';

const COLORS = { travel: 0x5b6573, bed: 0x2f3946, clearance: 0xf5c04a, stock: 0xc8a06a, stockEdge: 0xe8c48a };

/** The drawn cutter + shank are 7 tool diameters long (see createHead in toolpath-view.js). */
const STICK_OUT_DIAMETERS = 7;

export class MachineScene {
    /** @param onChange called when something was added later (the model arrived): re-fit and render. */
    constructor(scene, onChange) {
        this.group = new THREE.Group();
        scene.add(this.group);
        this.frame = new THREE.Group();   // travel box, bed, clearance plane: rebuilt when the travel changes
        this.group.add(this.frame);
        this.onChange = onChange;
        this.key = '';
        this.modelUrl = null;
        this.bedModel = null;  // the machine's model, replacing the plain bed plane
        this.machine = null;   // { moving, nose, origin } once the model is shown
        this.travelBox = null;
    }

    /**
     * { min: [x, y, z], max, clearanceZ, model: { name, url, origin, nose: [x, y, z] | null } | null }, or null when the
     * machine is unknown (the scene shows nothing). Returns true when the travel changed, so the caller re-frames.
     */
    set(machine) {
        this.group.visible = !!machine;
        if (!machine) return false;
        this.showModel(machine.model);
        const key = [...machine.min, ...machine.max, machine.clearanceZ].join(',');
        if (key === this.key) return false;
        this.key = key;
        this.buildFrame(machine);
        return true;
    }

    get known() {
        return this.group.visible && !!this.travelBox;
    }

    /** The travel and, once loaded, the model; null while the machine is unknown. */
    bounds() {
        if (!this.known) return null;
        const box = this.travelBox.clone();
        if (this.bedModel) box.union(new THREE.Box3().setFromObject(this.bedModel));
        return box;
    }

    /**
     * Height of the bed's surface as drawn: the lowest Z the spindle reaches (the machine reports no bed height; the
     * model is placed the same way). Null while the machine is unknown.
     */
    get bedZ() {
        return this.known ? this.travelBox.min.z : null;
    }

    /** Whether a model with a spindle is shown (the tool then sits in it at true size). */
    get hasSpindle() {
        return !!this.machine?.nose && this.group.visible;
    }

    /**
     * Moves the parts carrying the spindle so its nose sits above the tool tip (machine coordinates), the tool
     * sticking out by the drawn tool's length. The model shows the spindle at one end of its travel; this only shifts it.
     */
    moveSpindle(tool, diameter) {
        if (!this.machine?.nose || !tool) return;
        const { nose, origin, moving } = this.machine;
        const stickOut = STICK_OUT_DIAMETERS * diameter;
        const offset = [tool[0] - (origin[0] + nose[0]), tool[1] - (origin[1] + nose[1]), tool[2] + stickOut - (origin[2] + nose[2])];
        for (const part of moving) part.position.set(...offset);
    }

    buildFrame(machine) {
        for (const child of [...this.frame.children]) {
            this.frame.remove(child);
            child.geometry?.dispose();
            child.material?.dispose();
        }
        const min = new THREE.Vector3(...machine.min), max = new THREE.Vector3(...machine.max);
        this.frame.add(new THREE.Box3Helper(new THREE.Box3(min, max), COLORS.travel));

        const size = max.clone().sub(min);
        const bed = this.bedPlane = new THREE.Mesh(new THREE.PlaneGeometry(size.x, size.y),
            new THREE.MeshBasicMaterial({ color: COLORS.bed, transparent: true, opacity: 0.6, side: THREE.DoubleSide }));
        bed.position.set((min.x + max.x) / 2, (min.y + max.y) / 2, min.z);
        bed.visible = !this.bedModel;
        this.frame.add(bed);

        const clearance = new THREE.Mesh(new THREE.PlaneGeometry(size.x, size.y),
            new THREE.MeshBasicMaterial({ color: COLORS.clearance, transparent: true, opacity: 0.06, side: THREE.DoubleSide, depthWrite: false }));
        clearance.position.set(bed.position.x, bed.position.y, machine.clearanceZ);
        this.frame.add(clearance);
        this.travelBox = new THREE.Box3(min, max);
    }

    /** Loads the machine's model once and keeps it placed (the origin follows the travel limits). */
    showModel(model) {
        if (!model) return;
        if (this.machine) {
            this.machine.origin = model.origin;
            this.bedModel.position.set(...model.origin);
        }
        if (model.url === this.modelUrl) return;
        this.modelUrl = model.url;
        this.loadModel(model);
    }

    async loadModel(model) {
        try {
            const { root, moving } = await loadMachineModel(model.url);
            if (this.disposed) return;
            this.machine = { moving, nose: model.nose, origin: model.origin };
            this.bedModel = root;
            root.position.set(...model.origin);
            this.group.add(root);
            if (this.bedPlane) this.bedPlane.visible = false;
            this.onChange?.();
        } catch (error) {
            console.warn('Machine model could not be loaded', error);
        }
    }

    dispose() {
        this.disposed = true;
        this.group.traverse(o => { o.geometry?.dispose(); o.material?.dispose(); });
        this.group.removeFromParent();
    }
}

/**
 * The stock as a translucent block: from the bed up to work zero (its probed top), over the program's cutting area.
 * It explains why a toolpath sits above the bed: work Z0 is the top of the stock.
 */
export class StockBlock {
    constructor(parent) {
        const box = new THREE.BoxGeometry(1, 1, 1);
        this.group = new THREE.Group();
        this.group.add(new THREE.Mesh(box, new THREE.MeshStandardMaterial({
            color: COLORS.stock, transparent: true, opacity: 0.22, depthWrite: false, roughness: 0.9,
        })));
        this.group.add(new THREE.LineSegments(new THREE.EdgesGeometry(box),
            new THREE.LineBasicMaterial({ color: COLORS.stockEdge, transparent: true, opacity: 0.6 })));
        this.group.visible = false;
        parent.add(this.group);
    }

    /** Corners [x, y, z] in the parent's coordinates; hidden without them or when the block would be empty. */
    show(min, max) {
        const empty = !min || !max || max[0] <= min[0] || max[1] <= min[1] || max[2] - min[2] < 0.01;
        this.group.visible = !empty;
        if (empty) return;
        this.group.scale.set(max[0] - min[0], max[1] - min[1], max[2] - min[2]);
        this.group.position.set((min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2);
    }
}
