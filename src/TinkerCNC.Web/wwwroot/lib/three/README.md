# three.js r170 (MIT, see LICENSE)

Vendored so the 3D toolpath view works without internet access.

- `three.module.min.js` – `three@0.170.0/build/three.module.min.js`, unchanged.
- `OrbitControls.js` – `three@0.170.0/examples/jsm/controls/OrbitControls.js`; the only change is its import
  (`from 'three'` → `from './three.module.min.js'`), so no import map is needed.
- `GLTFLoader.js` / `BufferGeometryUtils.js` – from `three@0.170.0/examples/jsm/loaders` and `/utils`; only their imports
  are changed the same way (and `../utils/` → `./`).
