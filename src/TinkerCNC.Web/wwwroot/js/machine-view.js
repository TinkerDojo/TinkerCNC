// Small 3D view of the machine's travel with the tool, work origin, program area and Go-to target
// (Components/Machine/MachineViewPanel.razor). Coordinates are machine coordinates in mm; Z is up. The machine itself
// (travel, bed, model with moving spindle) is the shared MachineScene.
import * as THREE from '../lib/three/three.module.min.js';
import { FusionControls } from './fusion-controls.js';
import { MachineScene, StockBlock } from './machine-scene.js';
import { createHead } from './toolpath-view.js';
import { uiText } from './ui-texts.js';

const COLORS = { background: 0x0b0e12, job: 0x3fa7ff, target: 0x3ccf8e };

export function createMachineView(container) {
    try {
        return new MachineView(container);
    } catch (error) {
        container.textContent = uiText('viewUnavailable', '3D view unavailable: {0}', error.message);
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

        this.machine = new MachineScene(this.scene, () => { this.fitAll(); this.render(); });
        this.head = createHead();
        this.scene.add(this.head);
        this.origin = new THREE.AxesHelper(30);
        this.scene.add(this.origin);
        this.job = new THREE.Box3Helper(new THREE.Box3(), COLORS.job);
        this.scene.add(this.job);
        this.stock = new StockBlock(this.scene);
        this.target = new THREE.Mesh(new THREE.SphereGeometry(1, 16, 12),
            new THREE.MeshBasicMaterial({ color: COLORS.target, transparent: true, opacity: 0.7 }));
        this.scene.add(this.target);

        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(container);
    }

    /** { machine: { min, max, clearanceZ, model }, tool, workOrigin, jobMin, jobMax, target, toolDiameter } */
    update(s) {
        if (this.machine.set(s.machine)) {
            this.origin.scale.setScalar(Math.max(s.machine.max[0] - s.machine.min[0], s.machine.max[1] - s.machine.min[1]) / 300);
            this.fitAll();
        }
        const size = Math.max(s.machine.max[0] - s.machine.min[0], s.machine.max[1] - s.machine.min[1]);
        this.head.position.set(...s.tool);
        // In a spindle the tool is drawn at true size; alone, enlarged so it is not lost in the box.
        const diameter = s.toolDiameter ?? 3.175;
        this.head.scale.setScalar(this.machine.hasSpindle ? diameter : size * 0.02);
        this.machine.moveSpindle(s.tool, diameter);
        this.origin.visible = !!s.workOrigin;
        if (s.workOrigin) this.origin.position.set(...s.workOrigin);
        this.job.visible = !!s.jobMin;
        if (s.jobMin) this.job.box.set(new THREE.Vector3(...s.jobMin), new THREE.Vector3(...s.jobMax));
        // the stock under the cutting area, from the bed up to its probed top (work zero)
        const bedZ = this.machine.bedZ;
        this.stock.show(s.jobMin && s.workOrigin && bedZ != null ? [s.jobMin[0], s.jobMin[1], bedZ] : null,
            s.jobMax && s.workOrigin ? [s.jobMax[0], s.jobMax[1], s.workOrigin[2]] : null);
        this.target.visible = !!s.target;
        if (s.target) {
            this.target.position.set(...s.target);
            this.target.scale.setScalar(size * 0.012);
        }
        this.render();
    }

    /** Frames the travel and, once loaded, the model. */
    fitAll() {
        const box = this.machine.bounds();
        if (!box) return;
        const centre = box.getCenter(new THREE.Vector3());
        const radius = box.getSize(new THREE.Vector3()).length() / 2;
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
        this.machine.dispose();
        this.scene.traverse(o => { o.geometry?.dispose(); o.material?.dispose(); });
        this.renderer.dispose();
        this.renderer.domElement.remove();
    }
}
