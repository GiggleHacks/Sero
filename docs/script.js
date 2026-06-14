/* ── Three.js — C2 node network ── */
(function () {
  const canvas = document.getElementById('bg-canvas');
  if (!canvas || typeof THREE === 'undefined') return;

  const mobile  = window.innerWidth < 768;
  const N       = mobile ? 60  : 200;   // particle count
  const CONNECT = mobile ? 7.5 : 10.5;  // connection distance threshold
  const MAXSEG  = mobile ? 300 : 1400;  // pre-allocated line segments

  const renderer = new THREE.WebGLRenderer({ canvas, alpha: false, antialias: false });
  renderer.setPixelRatio(Math.min(devicePixelRatio, mobile ? 1 : 1.5));
  renderer.setClearColor(0x07080f, 1);

  const scene = new THREE.Scene();
  scene.fog = new THREE.FogExp2(0x07080f, mobile ? 0.038 : 0.024);

  const camera = new THREE.PerspectiveCamera(52, 1, 0.1, 60);
  camera.position.set(0, 0, 22);

  // ── Particle positions + velocities ───────────────────────────────────────
  const ptPos = new Float32Array(N * 3);
  const vel   = new Float32Array(N * 3);
  const BOUNDS = [20, 13, 14]; // half-extents x y z

  for (let i = 0; i < N; i++) {
    ptPos[i*3]   = (Math.random() - 0.5) * BOUNDS[0] * 2;
    ptPos[i*3+1] = (Math.random() - 0.5) * BOUNDS[1] * 2;
    ptPos[i*3+2] = (Math.random() - 0.5) * BOUNDS[2] * 2;
    vel[i*3]     = (Math.random() - 0.5) * 0.010;
    vel[i*3+1]   = (Math.random() - 0.5) * 0.008;
    vel[i*3+2]   = (Math.random() - 0.5) * 0.006;
  }

  // Points — regular nodes
  const ptGeo = new THREE.BufferGeometry();
  ptGeo.setAttribute('position', new THREE.BufferAttribute(ptPos, 3));
  scene.add(new THREE.Points(ptGeo, new THREE.PointsMaterial({
    color: 0x2a5fc4, size: mobile ? 0.16 : 0.13,
    sizeAttenuation: true, transparent: true, opacity: 0.85,
  })));

  // Pick ~8% of nodes as "active hubs" (brighter)
  const HUB_N   = Math.max(4, Math.floor(N * 0.08));
  const hubIdx  = new Set();
  while (hubIdx.size < HUB_N) hubIdx.add(Math.floor(Math.random() * N));
  const hubPos = new Float32Array(HUB_N * 3);
  const hubArr = [...hubIdx];
  for (let h = 0; h < HUB_N; h++) {
    const i = hubArr[h];
    hubPos[h*3]   = ptPos[i*3];
    hubPos[h*3+1] = ptPos[i*3+1];
    hubPos[h*3+2] = ptPos[i*3+2];
  }
  const hubGeo = new THREE.BufferGeometry();
  hubGeo.setAttribute('position', new THREE.BufferAttribute(hubPos, 3));
  scene.add(new THREE.Points(hubGeo, new THREE.PointsMaterial({
    color: 0x4a85f5, size: mobile ? 0.28 : 0.22,
    sizeAttenuation: true, transparent: true, opacity: 1.0,
  })));

  // ── Connection lines ───────────────────────────────────────────────────────
  const linePosArr = new Float32Array(MAXSEG * 6); // 2 verts × 3 floats per segment
  const lineGeo = new THREE.BufferGeometry();
  lineGeo.setAttribute('position', new THREE.BufferAttribute(linePosArr, 3));
  const lineObj = new THREE.LineSegments(lineGeo, new THREE.LineBasicMaterial({
    color: 0x1a3a70, transparent: true, opacity: mobile ? 0.40 : 0.35,
  }));
  scene.add(lineObj);

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
  let t = 0;

  function tick() {
    if (paused) return;
    requestAnimationFrame(tick);
    const dt = Math.min(clock.getDelta(), 0.05);
    t += dt;

    // Move particles
    for (let i = 0; i < N; i++) {
      ptPos[i*3]   += vel[i*3]   * dt * 60;
      ptPos[i*3+1] += vel[i*3+1] * dt * 60;
      ptPos[i*3+2] += vel[i*3+2] * dt * 60;
      for (let ax = 0; ax < 3; ax++) {
        if (Math.abs(ptPos[i*3+ax]) > BOUNDS[ax]) vel[i*3+ax] *= -1;
      }
    }
    ptGeo.attributes.position.needsUpdate = true;

    // Sync hub positions
    for (let h = 0; h < HUB_N; h++) {
      const i = hubArr[h];
      hubPos[h*3]   = ptPos[i*3];
      hubPos[h*3+1] = ptPos[i*3+1];
      hubPos[h*3+2] = ptPos[i*3+2];
    }
    hubGeo.attributes.position.needsUpdate = true;

    // Rebuild connections
    let seg = 0;
    outer: for (let i = 0; i < N; i++) {
      for (let j = i + 1; j < N; j++) {
        const dx = ptPos[i*3]   - ptPos[j*3];
        const dy = ptPos[i*3+1] - ptPos[j*3+1];
        const dz = ptPos[i*3+2] - ptPos[j*3+2];
        if (dx*dx + dy*dy + dz*dz < CONNECT*CONNECT) {
          linePosArr[seg*6]   = ptPos[i*3];
          linePosArr[seg*6+1] = ptPos[i*3+1];
          linePosArr[seg*6+2] = ptPos[i*3+2];
          linePosArr[seg*6+3] = ptPos[j*3];
          linePosArr[seg*6+4] = ptPos[j*3+1];
          linePosArr[seg*6+5] = ptPos[j*3+2];
          if (++seg >= MAXSEG) break outer;
        }
      }
    }
    lineGeo.attributes.position.needsUpdate = true;
    lineGeo.setDrawRange(0, seg * 2);

    // Slow camera drift — subtle arc, no spin
    if (!mobile) {
      camera.position.x = Math.sin(t * 0.055) * 3.0 + Math.sin(t * 0.021) * 0.8;
      camera.position.y = Math.sin(t * 0.038) * 1.4 + Math.sin(t * 0.017) * 0.5;
      camera.position.z = 22 + Math.sin(t * 0.028) * 1.8;
      camera.lookAt(Math.sin(t * 0.042) * 0.6, Math.sin(t * 0.029) * 0.3, 0);
    }

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
    box.style.transform  = 'perspective(700px) rotateY(18deg) scale(0.95)';
    setTimeout(() => {
      box.style.transform = 'perspective(700px) rotateY(-14deg) scale(0.95)';
      setTimeout(() => {
        box.style.transition = 'transform 0.22s ease';
        box.style.transform  = '';
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
    const target = document.querySelector(a.getAttribute('href'));
    if (target) { e.preventDefault(); target.scrollIntoView({ behavior: 'smooth' }); }
  });
});
