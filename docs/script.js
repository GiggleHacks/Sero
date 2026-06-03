/* ── Three.js — lit wave surface ── */
(function () {
  const canvas = document.getElementById('bg-canvas');
  if (!canvas || typeof THREE === 'undefined') return;

  const mobile = window.innerWidth < 768;
  const lowEnd  = window.innerWidth < 480;

  const renderer = new THREE.WebGLRenderer({ canvas, alpha: false, antialias: !mobile });
  renderer.setPixelRatio(Math.min(devicePixelRatio, mobile ? 1 : 1.5));
  renderer.setClearColor(0x07080f, 1);

  const scene = new THREE.Scene();
  scene.fog = new THREE.FogExp2(0x07080f, mobile ? 0.026 : 0.017);

  const camera = new THREE.PerspectiveCamera(46, 1, 0.1, 80);
  camera.position.set(0, 3.8, 8.2);
  camera.lookAt(0, -0.8, -5);

  // ── Lights ─────────────────────────────────────────────────────────────────
  scene.add(new THREE.AmbientLight(0x06101e, 0.9));

  // Directional from upper-left — crisp shadows across surface peaks
  const dirLight = new THREE.DirectionalLight(0x6090e0, mobile ? 1.0 : 1.6);
  dirLight.position.set(-6, 12, 2);
  scene.add(dirLight);

  // Brand-color point lights — sweep slowly for dynamic highlights
  const light1 = new THREE.PointLight(0x4a85f5, mobile ? 2.6 : 4.2, 44);
  light1.position.set(4, 7, -3);
  scene.add(light1);

  const light2 = new THREE.PointLight(0x7c5ce8, mobile ? 1.8 : 3.0, 36);
  light2.position.set(-9, 5, -11);
  scene.add(light2);

  // ── Main surface ───────────────────────────────────────────────────────────
  const S1 = lowEnd ? 32 : mobile ? 52 : 96;
  const geo1 = new THREE.PlaneGeometry(56, 82, S1, S1);
  geo1.rotateX(-Math.PI / 2);
  const pos1  = geo1.attributes.position;
  const base1 = new Float32Array(pos1.count);
  for (let i = 0; i < pos1.count; i++) base1[i] = pos1.getY(i);

  const vcBuf = new Float32Array(pos1.count * 3);
  geo1.setAttribute('color', new THREE.BufferAttribute(vcBuf, 3));

  const surface = new THREE.Mesh(geo1, new THREE.MeshStandardMaterial({
    vertexColors: true,
    roughness: 0.28,
    metalness: 0.52,
    side: THREE.DoubleSide,
    envMapIntensity: 0,
  }));
  surface.position.set(0, -1.2, -6);
  scene.add(surface);

  // Far depth layer — faint, slower waves, atmospheric recession
  let farData = null;
  if (!mobile) {
    const geoF = new THREE.PlaneGeometry(90, 120, 28, 28);
    geoF.rotateX(-Math.PI / 2);
    const posF  = geoF.attributes.position;
    const baseF = new Float32Array(posF.count);
    for (let i = 0; i < posF.count; i++) baseF[i] = posF.getY(i);
    const vcF = new Float32Array(posF.count * 3);
    geoF.setAttribute('color', new THREE.BufferAttribute(vcF, 3));
    const farSurf = new THREE.Mesh(geoF, new THREE.MeshStandardMaterial({
      vertexColors: true, roughness: 0.40, metalness: 0.30,
      side: THREE.DoubleSide, transparent: true, opacity: 0.35,
    }));
    farSurf.position.set(0, -3.8, -18);
    scene.add(farSurf);
    farData = { pos: posF, base: baseF, buf: vcF, geo: geoF };
  }

  // ── Vertex color: navy depths → sharp bright blue peaks ───────────────────
  function applyVC(buf, idx, h) {
    // gamma-boosted mapping — dark valleys, vivid peaks
    const n = Math.pow(Math.max(0, Math.min(1, (h + 0.9) / 1.7)), 0.55);
    let r, g, b;
    if (n < 0.38) {
      const s = n / 0.38;
      r = 0.012 + s * 0.028; g = 0.022 + s * 0.068; b = 0.048 + s * 0.182;
    } else if (n < 0.72) {
      const s = (n - 0.38) / 0.34;
      r = 0.040 + s * 0.115; g = 0.090 + s * 0.235; b = 0.230 + s * 0.295;
    } else {
      const s = (n - 0.72) / 0.28;
      r = 0.155 + s * 0.310; g = 0.325 + s * 0.385; b = 0.525 + s * 0.305;
    }
    buf[idx] = r; buf[idx+1] = g; buf[idx+2] = b;
  }

  // ── Wave functions — broad swell + fine surface ripples ───────────────────
  function ambientWave(x, z, t) {
    // Main swell
    const swell = Math.sin(x * 0.16 + t * 0.72) * 0.30
                + Math.sin(z * 0.11 + t * 0.50) * 0.22
                + Math.sin((x - z) * 0.072 + t * 0.35) * 0.14
                + Math.sin((x + z) * 0.036 + t * 0.19) * 0.09;
    // Fine ripple detail — adds visible texture without chaos
    const ripple = Math.sin(x * 0.46 + z * 0.34 + t * 1.10) * 0.048
                 + Math.sin(x * 0.62 - z * 0.44 + t * 0.88) * 0.034
                 + Math.sin(x * 0.31 + z * 0.55 + t * 1.38) * 0.024;
    return swell + ripple;
  }

  function sonarWave(x, z, t) {
    const d = Math.hypot(x, z);
    return Math.max(0, Math.sin(t * 1.9 - d * 0.22)) * Math.exp(-d * 0.048) * 0.65;
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

    // Main surface
    for (let i = 0; i < pos1.count; i++) {
      const x = pos1.getX(i), z = pos1.getZ(i);
      const y = base1[i] + ambientWave(x, z, t) + (mobile ? 0 : sonarWave(x, z, t));
      pos1.setY(i, y);
      applyVC(vcBuf, i * 3, y);
    }
    pos1.needsUpdate = true;
    geo1.attributes.color.needsUpdate = true;
    geo1.computeVertexNormals();

    // Far layer
    if (farData) {
      const { pos, base, buf, geo } = farData;
      for (let i = 0; i < pos.count; i++) {
        const x = pos.getX(i), z = pos.getZ(i);
        const y = base[i] + ambientWave(x, z, t * 0.36) * 0.38;
        pos.setY(i, y);
        applyVC(buf, i * 3, y * 0.38);
      }
      pos.needsUpdate = true;
      geo.attributes.color.needsUpdate = true;
      geo.computeVertexNormals();
    }

    // Moving point lights
    light1.position.x = Math.sin(t * 0.085) * 14 + Math.sin(t * 0.031) * 4;
    light1.position.z = -6 + Math.cos(t * 0.062) * 10;
    light1.position.y = 7  + Math.sin(t * 0.100) * 1.5;
    light2.position.x = Math.sin(t * 0.058 + 1.9) * 16;
    light2.position.z = -6 + Math.cos(t * 0.044 + 0.8) * 12;
    light2.position.y = 5  + Math.sin(t * 0.076 + 1.2) * 2;

    // Directional light gentle rotation
    dirLight.position.x = -6 + Math.sin(t * 0.030) * 3;
    dirLight.position.z =  2 + Math.cos(t * 0.024) * 4;

    // Camera
    camera.position.x = Math.sin(t * 0.110) * 0.50 + Math.sin(t * 0.037) * 0.14;
    camera.position.y = 3.8 + Math.sin(t * 0.068) * 0.18 + Math.sin(t * 0.039) * 0.07;
    camera.position.z = 8.2 + Math.sin(t * 0.018) * 1.1;
    camera.lookAt(Math.sin(t * 0.085) * 0.16, -0.8, -5);

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
