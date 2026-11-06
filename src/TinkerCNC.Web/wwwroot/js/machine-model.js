// Loads a machine model (glTF, wwwroot/models) for the machine views. Part names carry roles (see
// Services/MachineModels.cs): "moving__" parts carry the spindle and follow the machine position, "shell__" parts are
// the enclosure and are drawn from the inside, so the walls facing the camera disappear.
import * as THREE from '../lib/three/three.module.min.js';
import { GLTFLoader } from '../lib/three/GLTFLoader.js';

const MOVING = 'moving__';
const SHELL = 'shell__';

const loaded = new Map(); // url → Promise<glTF>: the 3D view and the top view share one download

/** @returns { root: Object3D, moving: Object3D[] } (a fresh copy for every caller) */
export async function loadMachineModel(url) {
    if (!loaded.has(url)) loaded.set(url, new GLTFLoader().loadAsync(url));
    let gltf;
    try {
        gltf = await loaded.get(url);
    } catch (error) {
        loaded.delete(url);
        throw error;
    }
    return withRoles(gltf.scene.clone(true));
}

/**
 * Paints a top-down picture of the machine model into an SVG <image> covering the given machine region, without the
 * enclosure and the parts that move with the spindle.
 * @param model { url, origin }  origin: machine coordinates of the model's origin
 * @param region { minX, maxX, minY, maxY } in machine coordinates (mm)
 */
export async function paintTopView(image, model, region) {
    const { root } = await loadMachineModel(model.url);
    root.traverse(node => { if (node.name.startsWith(MOVING) || node.name.startsWith(SHELL)) node.visible = false; });
    root.position.set(...model.origin);
    const scene = new THREE.Scene();
    scene.add(root);
    scene.add(new THREE.AmbientLight(0xffffff, 1.6));
    const sun = new THREE.DirectionalLight(0xffffff, 1.6);
    sun.position.set(0.4, -0.6, 1);
    scene.add(sun);

    const camera = new THREE.OrthographicCamera(region.minX, region.maxX, region.maxY, region.minY, 0.1, 5000);
    camera.position.set(0, 0, 2000); // looking straight down: X right, Y up (machine back at the top)
    camera.lookAt(0, 0, 0);

    const pixelsPerMm = 4;
    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, preserveDrawingBuffer: true });
    renderer.setSize(Math.round((region.maxX - region.minX) * pixelsPerMm), Math.round((region.maxY - region.minY) * pixelsPerMm), false);
    renderer.setClearColor(0x000000, 0);
    renderer.render(scene, camera);
    image.setAttribute('href', renderer.domElement.toDataURL('image/png'));
    renderer.dispose();
    renderer.forceContextLoss();
}

function withRoles(root) {
    const moving = [];
    root.traverse(node => {
        if (node.name.startsWith(MOVING)) moving.push(node);
        if (node.name.startsWith(SHELL) && node.material) {
            node.material = node.material.clone();
            node.material.side = THREE.BackSide;
        }
    });
    return { root, moving };
}
