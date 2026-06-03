/* ── Three.js — lit wave grid ── */
(function () {
  const canvas = document.getElementById('bg-canvas');
  if (!canvas || typeof THREE === 'undefined') return;

  const mobile = window.innerWidth < 768;
  const lowEnd  = window.innerWidth < 480;

  const renderer = new THREE.WebGLRenderer({ canvas, alpha: false, antialias: !mobile });
  renderer.setPixelRatio(Math.min(devicePixelRatio, mobile ? 1 : 1.5));
  renderer.setClearColor(0x07080f, 1);

  const scene = new THREE.Scene();
  scene.fog = new THREE.FogExp2(0x07080f, mobile ? 0.028 : 0.019);

  const camera = new THREE.PerspectiveCamera(44, 1, 0.1, 80);
  camera.position.set(0, 4.2, 8.0);
  camera.lookAt(0, -0.5, -5);

  // ── Lights — brand colors sweep across the surface for dynamic highlights ─
  scene.add(new THREE.AmbientLight(0x0d1a30, 1.6));

  const light1 = new THREE.PointLight(0x4a85f5, mobile ? 2.2 : 3.4, 42);
  light1.position.set(5, 8, -2);
  scene.add(light1);

  const light2 = new THREE.PointLight(0x7c5ce8, mobile ? 1.6 : 2.6, 34);
  light2.position.set(-8, 6, -10);
  scene.add(light2);

  // ── Main surface — high-res PBR mesh with vertex colors ───────────────────
  const S1 = lowEnd ? 30 : mobile ? 48 : 90;
  const geo1 = new THREE.PlaneGeometry(55, 80, S1, S1);
  geo1.rotateX(-Math.PI / 2);
  const pos1  = geo1.attributes.position;
  const base1 = new Float32Array(pos1.count);
  for (let i = 0; i < pos1.count; i++) base1[i] = pos1.getY(i);

  // Vertex color buffer — height-mapped, dark navy valleys → brand blue peaks
  const vcBuf = new Float32Array(pos1.count * 3);
  geo1.setAttribute('color', new THREE.BufferAttribute(vcBuf, 3));

  const surface = new THREE.Mesh(geo1, new THREE.MeshStandardMaterial({
    vertexColors: true,
    roughness: 0.42,
    metalness: 0.38,
    side: THREE.DoubleSide,
  }));
  surface.position.set(0, -1.2, -6);
  scene.add(surface);

  // Second layer — slightly below, different phase, adds depth parallax
  if (!mobile) {
    const geo2 = new THREE.PlaneGeometry(55, 80, 40, 40);
    geo2.rotateX(-Math.PI / 2);
    const pos2b = geo2.attributes.position;
    const base2b = new Float32Array(pos2b.count);
    for (let i = 0; i < pos2b.count; i++) base2b[i] = pos2b.getY(i);
    const vcBuf2 = new Float32Array(pos2b.count * 3);
    geo2.setAttribute('color', new THREE.BufferAttribute(vcBuf2, 3));
    const layer2 = new THREE.Mesh(geo2, new THREE.MeshStandardMaterial({
      vertexColors: true, roughness: 0.55, metalness: 0.20,
      side: THREE.DoubleSide, transparent: true, opacity: 0.45,
    }));
    layer2.position.set(0, -2.2, -7);
    scene.add(layer2);
    // Store refs for tick
    surface._layer2 = { pos: pos2b, base: base2b, buf: vcBuf2, geo: geo2, mesh: layer2 };
  }

  // Far faint layer for depth
  let farPos = null, farBase = null;
  if (!mobile) {
    const geoF = new THREE.PlaneGeometry(90, 120, 24, 24);
    geoF.rotateX(-Math.PI / 2);
    farPos  = geoF.attributes.position;
    farBase = new Float32Array(farPos.count);
    for (let i = 0; i < farPos.count; i++) farBase[i] = farPos.getY(i);
    const vcFar = new Float32Array(farPos.count * 3);
    geoF.setAttribute('color', new THREE.BufferAttribute(vcFar, 3));
    const farSurf = new THREE.Mesh(geoF, new THREE.MeshStandardMaterial({
      vertexColors: true, roughness: 0.6, metalness: 0.15,
      side: THREE.DoubleSide, transparent: true, opacity: 0.30,
    }));
    farSurf.position.set(0, -4.0, -18);
    scene.add(farSurf);
    surface._far = { pos: farPos, base: farBase, buf: vcFar, geo: geoF };
  }

  // ── Height → vertex color: dark navy valleys → brand blue peaks ──────────
  function applyVC(buf, idx, h) {
    const n = Math.max(0, Math.min(1, (h + 0.85) / 1.65));
    let r, g, b;
    if (n < 0.5) {
      const s = n / 0.5;
      r = 0.016 + s * 0.044; g = 0.031 + s * 0.095; b = 0.059 + s * 0.205;
    } else {
      const s = (n - 0.5) / 0.5;
      r = 0.060 + s * 0.090; g = 0.126 + s * 0.248; b = 0.264 + s * 0.362;
    }
    buf[idx] = r; buf[idx+1] = g; buf[idx+2] = b;
  }

  // ── Wave functions ────────────────────────────────────────────────────────
  function ambientWave(x, z, t) {
    return Math.sin(x * 0.16 + t * 0.72) * 0.32
         + Math.sin(z * 0.11 + t * 0.50) * 0.24
         + Math.sin((x - z) * 0.075 + t * 0.35) * 0.14
         + Math.sin((x + z) * 0.038 + t * 0.20) * 0.09
         + Math.sin(x * 0.035 + z * 0.028 + t * 0.13) * 0.06;
  }

  function sonarWave(x, z, t) {
    const d = Math.hypot(x, z);
    return Math.max(0, Math.sin(t * 1.9 - d * 0.22)) * Math.exp(-d * 0.050) * 0.70;
  }

  // ── Resize ────────────────────────────────────────────────────────────────
  function resize() {
    renderer.setSize(window.innerWidth, window.innerHeight, false);
    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
  }
  window.addEventListener('resize', resize, { passive: true });
  resize();

  let paused = false;
  document.addEventListener('visibilitychange', () => {
    paused = document.hidden;
    if (!paused) { clock.start(); tick(); }
  });

  const clock = new THREE.Clock();
  const SPEED = mobile ? 0.130 : 0.150;

  let t = 0;
  function tick() {
    if (paused) return;
    requestAnimationFrame(tick);

    const dt = Math.min(clock.getDelta(), 0.05);
    t += dt * SPEED;

    // Main surface — heights + vertex colors
    for (let i = 0; i < pos1.count; i++) {
      const x = pos1.getX(i), z = pos1.getZ(i);
      const y = base1[i] + ambientWave(x, z, t) + (mobile ? 0 : sonarWave(x, z, t));
      pos1.setY(i, y);
      applyVC(vcBuf, i * 3, y);
    }
    pos1.needsUpdate = true;
    geo1.attributes.color.needsUpdate = true;
    geo1.computeVertexNormals();

    // Second parallax layer
    const L2 = surface._layer2;
    if (L2) {
      for (let i = 0; i < L2.pos.count; i++) {
        const x = L2.pos.getX(i), z = L2.pos.getZ(i);
        const y = L2.base[i] + ambientWave(x, z, t * 0.78 + 1.4) * 0.7;
        L2.pos.setY(i, y);
        applyVC(L2.buf, i * 3, y * 0.6);
      }
      L2.pos.needsUpdate = true;
      L2.geo.attributes.color.needsUpdate = true;
      L2.geo.computeVertexNormals();
    }

    // Far layer
    const FL = surface._far;
    if (FL) {
      for (let i = 0; i < FL.pos.count; i++) {
        const x = FL.pos.getX(i), z = FL.pos.getZ(i);
        const y = FL.base[i] + ambientWave(x, z, t * 0.38) * 0.42;
        FL.pos.setY(i, y);
        applyVC(FL.buf, i * 3, y * 0.4);
      }
      FL.pos.needsUpdate = true;
      FL.geo.attributes.color.needsUpdate = true;
      FL.geo.computeVertexNormals();
    }

    // Moving lights — sweep slowly in X/Z creating traveling highlight bands
    light1.position.x = Math.sin(t * 0.085) * 14 + Math.sin(t * 0.031) * 4;
    light1.position.z = -6 + Math.cos(t * 0.062) * 10;
    light1.position.y = 7 + Math.sin(t * 0.100) * 1.5;

    light2.position.x = Math.sin(t * 0.058 + 1.9) * 16;
    light2.position.z = -6 + Math.cos(t * 0.044 + 0.8) * 12;
    light2.position.y = 5 + Math.sin(t * 0.076 + 1.2) * 2;

    // Camera
    camera.position.x = Math.sin(t * 0.110) * 0.48 + Math.sin(t * 0.037) * 0.13;
    camera.position.y = 4.2 + Math.sin(t * 0.068) * 0.18 + Math.sin(t * 0.039) * 0.07;
    camera.position.z = 8.0 + Math.sin(t * 0.018) * 1.2;
    camera.lookAt(Math.sin(t * 0.085) * 0.16, -0.5, -5);

    renderer.render(scene, camera);
  }
  tick();
})();

/* ── Background music — starts on first interaction ── */
(function () {
  const audio = document.getElementById('bg-music');
  if (!audio) return;
  audio.volume = 0.28;

  function tryPlay() { audio.play().catch(() => {}); }

  tryPlay();
  document.addEventListener('click',      tryPlay, { once: true, passive: true });
  document.addEventListener('keydown',    tryPlay, { once: true, passive: true });
  document.addEventListener('touchstart', tryPlay, { once: true, passive: true });
})();

/* ── Nav scroll state ── */
window.addEventListener('scroll', () => {
  document.getElementById('nav').classList.toggle('scrolled', window.scrollY > 40);
}, { passive: true });

/* ── Scroll reveal ── */
const revealIO = new IntersectionObserver(entries => {
  entries.forEach(({ isIntersecting, target }) => {
    if (isIntersecting) { target.classList.add('visible'); revealIO.unobserve(target); }
  });
}, { threshold: 0.07 });

document.querySelectorAll('.reveal').forEach((el, i) => {
  el.style.transitionDelay = (i % 4) * 60 + 'ms';
  revealIO.observe(el);
});

/* ── 3D tilt on click/tap ── */
document.querySelectorAll('.gallery-item img, .screen-body img').forEach(img => {
  let busy = false;
  const box = img.closest('.gallery-item') || img.closest('.screen-frame') || img;

  function doTilt() {
    if (busy) return;
    busy = true;
    box.style.transition = 'transform 0.13s ease';
    box.style.transform = 'perspective(700px) rotateY(18deg) scale(0.95)';
    setTimeout(() => {
      box.style.transform = 'perspective(700px) rotateY(-14deg) scale(0.95)';
      setTimeout(() => {
        box.style.transition = 'transform 0.22s ease';
        box.style.transform = '';
        setTimeout(() => { busy = false; }, 220);
      }, 130);
    }, 130);
  }
  img.addEventListener('click', doTilt);
  img.addEventListener('touchend', e => { e.preventDefault(); doTilt(); }, { passive: false });
});

/* ── Smooth anchor scroll ── */
document.querySelectorAll('a[href^="#"]').forEach(a => {
  a.addEventListener('click', e => {
    const t = document.querySelector(a.getAttribute('href'));
    if (t) { e.preventDefault(); t.scrollIntoView({ behavior: 'smooth' }); }
  });
});
