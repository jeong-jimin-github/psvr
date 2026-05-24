import * as THREE from 'three';
import { VRButton } from 'three/addons/webxr/VRButton.js';

/**
 * WebXR VR Video Player using Three.js.
 *
 * Supports video formats:
 *   mono360  – equirectangular 360° mono
 *   sbs360   – equirectangular 360° side-by-side stereo
 *   tb360    – equirectangular 360° top-bottom stereo
 *   mono180  – 180° mono
 *   sbs180   – 180° side-by-side stereo
 *   flat     – flat screen in VR (theater mode)
 */
export class VRPlayer {
  constructor(container, vrButtonContainer) {
    this.container = container;
    this.vrButtonContainer = vrButtonContainer;

    this.video = document.getElementById('video');
    this.format = 'mono360';
    this.texture = null;
    this.meshes = [];

    this._isDragging = false;
    this._prevMouse = { x: 0, y: 0 };
    this._euler = new THREE.Euler(0, 0, 0, 'YXZ');

    this._init();
  }

  _init() {
    // Scene
    this.scene = new THREE.Scene();

    // Camera
    this.camera = new THREE.PerspectiveCamera(75, this._aspect(), 0.1, 2000);
    this.camera.position.set(0, 0, 0);

    // Renderer
    this.renderer = new THREE.WebGLRenderer({ antialias: true });
    this.renderer.setPixelRatio(window.devicePixelRatio);
    this.renderer.setSize(this.container.clientWidth, this.container.clientHeight);
    this.renderer.xr.enabled = true;
    this.container.appendChild(this.renderer.domElement);

    // VR Button
    const vrBtn = VRButton.createButton(this.renderer);
    this.vrButtonContainer.appendChild(vrBtn);

    // Render loop
    this.renderer.setAnimationLoop(() => {
      this.renderer.render(this.scene, this.camera);
    });

    this._setupMouseControls();
    this._setupResize();
  }

  _aspect() {
    return this.container.clientWidth / this.container.clientHeight || 1;
  }

  _setupResize() {
    new ResizeObserver(() => {
      this.camera.aspect = this._aspect();
      this.camera.updateProjectionMatrix();
      this.renderer.setSize(this.container.clientWidth, this.container.clientHeight);
    }).observe(this.container);
  }

  _setupMouseControls() {
    const el = this.renderer.domElement;

    el.addEventListener('mousedown', (e) => {
      this._isDragging = true;
      this._prevMouse = { x: e.clientX, y: e.clientY };
    });

    window.addEventListener('mouseup', () => { this._isDragging = false; });

    window.addEventListener('mousemove', (e) => {
      if (!this._isDragging || this.renderer.xr.isPresenting) return;
      const dx = e.clientX - this._prevMouse.x;
      const dy = e.clientY - this._prevMouse.y;
      this._euler.y -= dx * 0.005;
      this._euler.x = Math.max(-Math.PI / 2, Math.min(Math.PI / 2, this._euler.x - dy * 0.005));
      this.camera.quaternion.setFromEuler(this._euler);
      this._prevMouse = { x: e.clientX, y: e.clientY };
    });

    // Touch
    el.addEventListener('touchstart', (e) => {
      if (e.touches.length === 1) {
        this._isDragging = true;
        this._prevMouse = { x: e.touches[0].clientX, y: e.touches[0].clientY };
      }
    }, { passive: true });

    window.addEventListener('touchend', () => { this._isDragging = false; });

    window.addEventListener('touchmove', (e) => {
      if (!this._isDragging || e.touches.length !== 1 || this.renderer.xr.isPresenting) return;
      const dx = e.touches[0].clientX - this._prevMouse.x;
      const dy = e.touches[0].clientY - this._prevMouse.y;
      this._euler.y -= dx * 0.005;
      this._euler.x = Math.max(-Math.PI / 2, Math.min(Math.PI / 2, this._euler.x - dy * 0.005));
      this.camera.quaternion.setFromEuler(this._euler);
      this._prevMouse = { x: e.touches[0].clientX, y: e.touches[0].clientY };
    }, { passive: true });
  }

  // ── Video loading ───────────────────────────────────────────────────────────

  loadVideo(file) {
    const url = URL.createObjectURL(file);
    this.video.src = url;
    this.video.load();

    if (this.texture) this.texture.dispose();

    this.texture = new THREE.VideoTexture(this.video);
    this.texture.colorSpace = THREE.SRGBColorSpace;
    this.texture.minFilter = THREE.LinearFilter;
    this.texture.magFilter = THREE.LinearFilter;
    this.texture.generateMipmaps = false;

    this._rebuildMeshes();
    document.getElementById('no-video-overlay').style.display = 'none';
  }

  setFormat(format) {
    this.format = format;
    if (this.texture) this._rebuildMeshes();
  }

  // ── Mesh builders ─────────────────────────────────────────────────────────

  _rebuildMeshes() {
    this.meshes.forEach(m => {
      this.scene.remove(m);
      m.geometry.dispose();
    });
    this.meshes = [];

    const meshes = this._buildMeshes();
    meshes.forEach(m => this.scene.add(m));
    this.meshes = meshes;
  }

  _buildMeshes() {
    const tex = this.texture;

    switch (this.format) {
      case 'sbs360':  return this._stereoSphere(tex, 'sbs', 360);
      case 'tb360':   return this._stereoSphere(tex, 'tb',  360);
      case 'sbs180':  return this._stereoSphere(tex, 'sbs', 180);
      case 'mono180': return [this._monoSphere(tex, 180)];
      case 'flat':    return [this._flatScreen(tex)];
      default:        return [this._monoSphere(tex, 360)];
    }
  }

  /** Single sphere for mono video. */
  _monoSphere(tex, degrees) {
    const geo = degrees === 180
      ? new THREE.SphereGeometry(500, 60, 40, -Math.PI / 2, Math.PI)
      : new THREE.SphereGeometry(500, 60, 40);
    geo.scale(-1, 1, 1); // invert normals to see from inside
    const mat = new THREE.MeshBasicMaterial({ map: tex });
    return new THREE.Mesh(geo, mat);
  }

  /** Two spheres for stereo (SBS or TB), one per eye layer. */
  _stereoSphere(tex, layout, degrees) {
    const makeGeo = () => {
      const g = degrees === 180
        ? new THREE.SphereGeometry(500, 60, 40, -Math.PI / 2, Math.PI)
        : new THREE.SphereGeometry(500, 60, 40);
      g.scale(-1, 1, 1);
      return g;
    };

    const geoL = makeGeo();
    const geoR = makeGeo();
    const uvL = geoL.attributes.uv;
    const uvR = geoR.attributes.uv;

    if (layout === 'sbs') {
      // Left eye → left half (U: 0→0.5), Right eye → right half (U: 0.5→1)
      for (let i = 0; i < uvL.count; i++) {
        uvL.setX(i, uvL.getX(i) * 0.5);
        uvR.setX(i, uvR.getX(i) * 0.5 + 0.5);
      }
    } else {
      // TB: Left eye → top half (V: 0.5→1), Right eye → bottom half (V: 0→0.5)
      for (let i = 0; i < uvL.count; i++) {
        uvL.setY(i, uvL.getY(i) * 0.5 + 0.5);
        uvR.setY(i, uvR.getY(i) * 0.5);
      }
    }

    uvL.needsUpdate = true;
    uvR.needsUpdate = true;

    const mat = new THREE.MeshBasicMaterial({ map: tex });

    const meshL = new THREE.Mesh(geoL, mat);
    const meshR = new THREE.Mesh(geoR, mat);

    // Layer 1 = left eye, Layer 2 = right eye (Three.js WebXR convention)
    meshL.layers.set(1);
    meshR.layers.set(2);

    return [meshL, meshR];
  }

  /** Flat screen placed 3 m in front of the viewer. */
  _flatScreen(tex) {
    // 16:9 screen, ~2.4 m wide
    const geo = new THREE.PlaneGeometry(3.2, 1.8);
    const mat = new THREE.MeshBasicMaterial({ map: tex });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.position.set(0, 0, -3);
    return mesh;
  }

  // ── Video controls ────────────────────────────────────────────────────────

  play()  { return this.video.play(); }
  pause() { this.video.pause(); }

  get currentTime() { return this.video.currentTime; }
  set currentTime(v) { this.video.currentTime = v; }

  get duration()   { return this.video.duration || 0; }
  get paused()     { return this.video.paused; }
  get volume()     { return this.video.volume; }
  set volume(v)    { this.video.volume = v; }
  get muted()      { return this.video.muted; }
  set muted(v)     { this.video.muted = v; }
}
