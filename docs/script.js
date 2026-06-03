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
  scene.add(new THREE.AmbientLight(0x080c18, 1.0));

  const light1 = new THREE.PointLight(0x4a85f5, mobile ? 1.8 : 2.8, 40);
  light1.position.set(5, 8, -2);
  scene.add(light1);

  const light2 = new THREE.PointLight(0x7c5ce8, mobile ? 1.2 : 2.0, 32);
  light2.position.set(-8, 6, -10);
  scene.add(light2);

  // ── Main grid ─────────────────────────────────────────────────────────────
  const S1 = lowEnd ? 22 : mobile ? 34 : 66;
  const geo1 = new THREE.PlaneGeometry(50, 72, S1, S1);
  geo1.rotateX(-Math.PI / 2);

  // Solid PBR surface — lit, responds to moving lights for realistic shading
  const solidMesh = new THREE.Mesh(geo1, new THREE.MeshStandardMaterial({
    color: 0x040810,
    emissive: 0x02050e,
    roughness: 0.55,
    metalness: 0.30,
    side: THREE.DoubleSide,
    transparent: true,
    opacity: 0.92,
    polygonOffset: true,
    polygonOffsetFactor: 1,
    polygonOffsetUnits: 1,
  }));
  solidMesh.position.set(0, -1.2, -6);
  scene.add(solidMesh);

  // Wireframe overlay on the same geometry — grid lines on top
  const wireMesh = new THREE.Mesh(geo1, new THREE.MeshBasicMaterial({
    color: 0x1a4a90,
    wireframe: true,
    transparent: true,
    opacity: mobile ? 0.38 : 0.44,
  }));
  wireMesh.position.set(0, -1.2, -6);
  scene.add(wireMesh);

  const pos1  = geo1.attributes.position;
  const base1 = new Float32Array(pos1.count);
  for (let i = 0; i < pos1.count; i++) base1[i] = pos1.getY(i);

  // ── Far sparse grid ───────────────────────────────────────────────────────
  let farPos = null, farBase = null;
  if (!mobile) {
    const geo2 = new THREE.PlaneGeometry(80, 100, 22, 22);
    geo2.rotateX(-Math.PI / 2);
    scene.add(Object.assign(new THREE.Mesh(geo2, new THREE.MeshBasicMaterial({
      color: 0x091a40, wireframe: true, transparent: true, opacity: 0.10,
    })), { position: new THREE.Vector3(0, -4.0, -18) }));
    farPos  = geo2.attributes.position;
    farBase = new Float32Array(farPos.count);
    for (let i = 0; i < farPos.count; i++) farBase[i] = farPos.getY(i);
  }

  // ── Wave functions ────────────────────────────────────────────────────────
  function ambientWave(x, z, t) {
    return Math.sin(x * 0.26 + t * 0.72) * 0.50
         + Math.sin(z * 0.17 + t * 0.50) * 0.36
         + Math.sin((x - z) * 0.11 + t * 0.35) * 0.22
         + Math.sin((x + z) * 0.058 + t * 0.20) * 0.14
         + Math.sin(x * 0.052 + z * 0.040 + t * 0.13) * 0.09;
  }

  function sonarWave(x, z, t) {
    const d = Math.hypot(x, z);
    return Math.max(0, Math.sin(t * 1.9 - d * 0.25)) * Math.exp(-d * 0.055) * 1.1;
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

    // Update vertices + recompute normals for accurate PBR shading
    for (let i = 0; i < pos1.count; i++)
      pos1.setY(i, base1[i] + ambientWave(pos1.getX(i), pos1.getZ(i), t)
                             + (mobile ? 0 : sonarWave(pos1.getX(i), pos1.getZ(i), t)));
    pos1.needsUpdate = true;
    geo1.computeVertexNormals();

    // Far grid
    if (farPos && farBase) {
      for (let i = 0; i < farPos.count; i++)
        farPos.setY(i, farBase[i] + ambientWave(farPos.getX(i), farPos.getZ(i), t * 0.38) * 0.42);
      farPos.needsUpdate = true;
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
