// Small 3D view of the machine's travel with the tool, work origin, program area and Go-to target
// (Components/Machine/MachineViewPanel.razor). Coordinates are machine coordinates in mm; Z is up. When the machine
// is known (Services/MachineModels.cs), its model is shown; a model with a spindle moves it along with the tool.
import * as THREE from '../lib/three/three.module.min.js';
import { FusionControls } from './fusion-controls.js';
import { loadMachineModel } from './machine-model.js';
import { createHead } from './toolpath-view.js';

const COLORS = { background: 0x0b0e12, travel: 0x5b6573, bed: 0x2f3946, job: 0x3fa7ff, target: 0x3ccf8e, clearance: 0xf5c04a };

export function createMachineView(container) {
    try {
        return new MachineView(container);
    } catch (error) {
        container.textContent = `3D view unavailable: ${error.message}`;
        return null;
    }
}

class MachineView {
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

        this.camera = new THREE.PerspectiveCamera(40, 1, 1, 10000);
        this.camera.up.set(0, 0, 1);
        this.controls = new FusionControls(this.camera, this.renderer.domElement, () => this.scene.children);
        this.controls.addEventListener('change', () => this.render());

        this.frame = new THREE.Group();   // travel box, bed, clearance plane: rebuilt when the travel changes
        this.scene.add(this.frame);
        this.head = createHead();
        this.scene.add(this.head);
        this.origin = new THREE.AxesHelper(30);
        this.scene.add(this.origin);
        this.job = new THREE.Box3Helper(new THREE.Box3(), COLORS.job);
        this.scene.add(this.job);
        this.target = new THREE.Mesh(new THREE.SphereGeometry(1, 16, 12),
            new THREE.MeshBasicMaterial({ color: COLORS.target, transparent: true, opacity: 0.7 }));
        this.scene.add(this.target);

        this.travelKey = '';
        this.modelUrl = null;
        this.bedModel = null;  // the machine's model, replacing the plain bed plane
        this.machine = null;   // { moving, nose, origin } once the model is shown
        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(container);
    }

    /** { min, max, tool, workOrigin, bedCorner, clearanceZ, jobMin, jobMax, target, toolDiameter,
     *    model: { name, url, origin, nose: [x, y, z] | null } | null } */
    update(s) {
        this.showModel(s.model);
        const key = [...s.min, ...s.max, s.clearanceZ].join(',');
        if (key !== this.travelKey) {
            this.travelKey = key;
            this.buildFrame(s);
        }
        const size = Math.max(s.max[0] - s.min[0], s.max[1] - s.min[1]);
        this.head.position.set(...s.tool);
        // In a spindle the tool is drawn at true size; alone, enlarged so it is not lost in the box.
        const diameter = s.toolDiameter ?? 3.175;
        this.head.scale.setScalar(this.machine?.nose ? diameter : size * 0.02);
        this.moveSpindle(s.tool, diameter);
        this.origin.visible = !!s.workOrigin;
        if (s.workOrigin) this.origin.position.set(...s.workOrigin);
        this.job.visible = !!s.jobMin;
        if (s.jobMin) this.job.box.set(new THREE.Vector3(...s.jobMin), new THREE.Vector3(...s.jobMax));
        this.target.visible = !!s.target;
        if (s.target) {
            this.target.position.set(...s.target);
            this.target.scale.setScalar(size * 0.012);
        }
        this.render();
    }

    buildFrame(s) {
        for (const child of [...this.frame.children]) {
            this.frame.remove(child);
            child.geometry?.dispose();
            child.material?.dispose();
        }
        const min = new THREE.Vector3(...s.min), max = new THREE.Vector3(...s.max);
        this.frame.add(new THREE.Box3Helper(new THREE.Box3(min, max), COLORS.travel));

        const size = max.clone().sub(min);
        const bed = this.bedPlane = new THREE.Mesh(new THREE.PlaneGeometry(size.x, size.y),
            new THREE.MeshBasicMaterial({ color: COLORS.bed, transparent: true, opacity: 0.6, side: THREE.DoubleSide }));
        bed.position.set((min.x + max.x) / 2, (min.y + max.y) / 2, min.z);
        bed.visible = !this.bedModel;
        this.frame.add(bed);

        const clearance = new THREE.Mesh(new THREE.PlaneGeometry(size.x, size.y),
            new THREE.MeshBasicMaterial({ color: COLORS.clearance, transparent: true, opacity: 0.06, side: THREE.DoubleSide, depthWrite: false }));
        clearance.position.set(bed.position.x, bed.position.y, s.clearanceZ);
        this.frame.add(clearance);

        this.origin.scale.setScalar(Math.max(size.x, size.y) / 300);
        this.travelBox = new THREE.Box3(min, max);
        this.fitAll();
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
            this.machine = { moving, nose: model.nose, origin: model.origin };
            this.place(root, model.origin);
        } catch (error) {
            console.warn('Machine model could not be loaded', error);
        }
    }

    place(root, origin) {
        if (this.disposed) return;
        this.bedModel = root;
        root.position.set(...origin);
        this.scene.add(root);
        if (this.bedPlane) this.bedPlane.visible = false;
        this.fitAll();
        this.render();
    }

    /**
     * Moves the parts carrying the spindle so its nose sits above the tool tip (machine coordinates), the tool
     * sticking out by the drawn tool's length. The model shows the spindle at one end of its travel; this only shifts it.
     */
    moveSpindle(tool, diameter) {
        if (!this.machine?.nose) return;
        const { nose, origin, moving } = this.machine;
        const stickOut = 7 * diameter; // the drawn cutter + shank are 7 tool diameters long
        const offset = [tool[0] - (origin[0] + nose[0]), tool[1] - (origin[1] + nose[1]), tool[2] + stickOut - (origin[2] + nose[2])];
        for (const part of moving) part.position.set(...offset);
    }


    /** Frames the travel and, once loaded, the bed model. */
    fitAll() {
        if (!this.travelBox) return;
        const box = this.travelBox.clone();
        if (this.bedModel) box.union(new THREE.Box3().setFromObject(this.bedModel));
        this.fit(box.min, box.max);
    }

    fit(min, max) {
        const centre = min.clone().add(max).multiplyScalar(0.5);
        const radius = max.clone().sub(min).length() / 2;
        this.camera.position.copy(centre).add(new THREE.Vector3(0.6, -1.2, 0.9).normalize().multiplyScalar(radius * 2.4));
        this.camera.near = radius / 100;
        this.camera.far = radius * 20;
        this.camera.updateProjectionMatrix();
        this.controls.target.copy(centre);
        this.controls.update();
    }

    resize() {
        const width = this.container.clientWidth, height = this.container.clientHeight;
        if (!width || !height) return;
        this.renderer.setSize(width, height, false);
        this.camera.aspect = width / height;
        this.camera.updateProjectionMatrix();
        this.render();
    }

    render() {
        if (this.pending || this.disposed) return;
        this.pending = requestAnimationFrame(() => {
            this.pending = 0;
            this.renderer.render(this.scene, this.camera);
        });
    }

    dispose() {
        this.disposed = true;
        this.resizeObserver.disconnect();
        this.controls.dispose();
        this.scene.traverse(o => { o.geometry?.dispose(); o.material?.dispose(); });
        this.renderer.dispose();
        this.renderer.domElement.remove();
    }
}
