/* ── Three.js animated background ── */
(function initThree() {
    const canvas = document.getElementById('bg-canvas');

    if (typeof THREE === 'undefined') {
        particleFallback(canvas);
        return;
    }

    const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: false });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 1.5));
    renderer.setClearColor(0x000000, 0);

    const scene  = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 1000);
    camera.position.z = 5;

    // ── Particle field — floating dots in 3D space ──
    const N = 900;
    const positions = new Float32Array(N * 3);
    const velocities = new Float32Array(N * 3);
    const alphas = new Float32Array(N);

    for (let i = 0; i < N; i++) {
        positions[i*3]     = (Math.random() - 0.5) * 18;
        positions[i*3 + 1] = (Math.random() - 0.5) * 18;
        positions[i*3 + 2] = (Math.random() - 0.5) * 8;
        velocities[i*3]     = (Math.random() - 0.5) * 0.0006;
        velocities[i*3 + 1] = (Math.random() - 0.5) * 0.0006 + 0.0002;
        velocities[i*3 + 2] = (Math.random() - 0.5) * 0.0002;
        alphas[i] = Math.random() * 0.6 + 0.15;
    }

    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));

    const mat = new THREE.PointsMaterial({
        color: 0x4A85F5,
        size: 0.045,
        transparent: true,
        opacity: 0.55,
        sizeAttenuation: true,
        depthWrite: false,
    });

    const points = new THREE.Points(geo, mat);
    scene.add(points);

    // ── Subtle connecting lines between nearby particles ──
    const lineGeo = new THREE.BufferGeometry();
    const linePositions = new Float32Array(N * N * 6); // max; we'll update dynamically
    const lineMat = new THREE.LineSegments(
        lineGeo,
        new THREE.LineBasicMaterial({ color: 0x2A4A9A, transparent: true, opacity: 0.08, depthWrite: false })
    );
    scene.add(lineMat);

    let lineCount = 0;
    const CONN_DIST = 1.8;

    function updateLines() {
        lineCount = 0;
        const pos = geo.attributes.position.array;
        for (let i = 0; i < N; i++) {
            const ix = pos[i*3], iy = pos[i*3+1], iz = pos[i*3+2];
            for (let j = i+1; j < N; j++) {
                const dx = ix - pos[j*3];
                const dy = iy - pos[j*3+1];
                const dz = iz - pos[j*3+2];
                const d2 = dx*dx + dy*dy + dz*dz;
                if (d2 < CONN_DIST * CONN_DIST) {
                    const base = lineCount * 6;
                    linePositions[base]   = ix;
                    linePositions[base+1] = iy;
                    linePositions[base+2] = iz;
                    linePositions[base+3] = pos[j*3];
                    linePositions[base+4] = pos[j*3+1];
                    linePositions[base+5] = pos[j*3+2];
                    lineCount++;
                    if (lineCount >= 1200) { i = N; break; }
                }
            }
        }
        lineGeo.setAttribute('position', new THREE.BufferAttribute(linePositions.slice(0, lineCount*6), 3));
        lineGeo.setDrawRange(0, lineCount * 2);
        lineGeo.attributes.position.needsUpdate = true;
    }

    function resize() {
        const w = window.innerWidth;
        const h = window.innerHeight;
        canvas.width  = w;
        canvas.height = h;
        renderer.setSize(w, h, false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
    }
    window.addEventListener('resize', resize, { passive: true });
    resize();

    let frame = 0;
    let mouseX = 0, mouseY = 0;
    document.addEventListener('mousemove', e => {
        mouseX = (e.clientX / window.innerWidth  - 0.5) * 0.3;
        mouseY = (e.clientY / window.innerHeight - 0.5) * 0.3;
    }, { passive: true });

    function animate() {
        requestAnimationFrame(animate);
        frame++;

        const pos = geo.attributes.position.array;
        for (let i = 0; i < N; i++) {
            pos[i*3]     += velocities[i*3];
            pos[i*3 + 1] += velocities[i*3 + 1];
            pos[i*3 + 2] += velocities[i*3 + 2];

            // Wrap around
            if (pos[i*3]     >  9) pos[i*3]     = -9;
            if (pos[i*3]     < -9) pos[i*3]     =  9;
            if (pos[i*3 + 1] >  9) pos[i*3 + 1] = -9;
            if (pos[i*3 + 1] < -9) pos[i*3 + 1] =  9;
        }
        geo.attributes.position.needsUpdate = true;

        // Update lines every 3 frames for perf
        if (frame % 3 === 0) updateLines();

        // Gentle camera drift toward mouse
        camera.position.x += (mouseX - camera.position.x) * 0.02;
        camera.position.y += (-mouseY - camera.position.y) * 0.02;
        camera.lookAt(scene.position);

        renderer.render(scene, camera);
    }
    animate();

    function particleFallback(c) {
        const ctx = c.getContext('2d');
        const pts = Array.from({ length: 200 }, () => ({
            x: Math.random(), y: Math.random(),
            vy: Math.random() * 0.00012 + 0.00004,
            r:  Math.random() * 1.3 + 0.4,
            o:  Math.random() * 0.4 + 0.1,
        }));
        function draw() {
            c.width  = window.innerWidth;
            c.height = window.innerHeight;
            ctx.clearRect(0, 0, c.width, c.height);
            for (const p of pts) {
                p.y -= p.vy; if (p.y < 0) p.y = 1;
                ctx.beginPath();
                ctx.arc(p.x * c.width, p.y * c.height, p.r, 0, Math.PI*2);
                ctx.fillStyle = `rgba(74,133,245,${p.o})`;
                ctx.fill();
            }
            requestAnimationFrame(draw);
        }
        draw();
    }
})();

/* ── Nav scroll state ── */
window.addEventListener('scroll', () => {
    document.getElementById('nav').classList.toggle('scrolled', window.scrollY > 50);
}, { passive: true });

/* ── Animated counters ── */
const io = new IntersectionObserver(entries => {
    entries.forEach(({ isIntersecting, target }) => {
        if (!isIntersecting) return;
        const end = +target.dataset.target;
        const t0 = performance.now();
        const dur = 1500;
        (function tick(now) {
            const p = Math.min((now - t0) / dur, 1);
            target.textContent = Math.round((1 - Math.pow(1-p, 3)) * end);
            if (p < 1) requestAnimationFrame(tick);
        })(t0);
        io.unobserve(target);
    });
}, { threshold: 0.5 });
document.querySelectorAll('[data-target]').forEach(el => io.observe(el));

/* ── Scroll fade-in ── */
const fadeIO = new IntersectionObserver(entries => {
    entries.forEach(({ isIntersecting, target }) => {
        if (isIntersecting) {
            target.style.opacity = '1';
            target.style.transform = 'none';
            fadeIO.unobserve(target);
        }
    });
}, { threshold: 0.08 });

document.querySelectorAll(
    '.feature-card, .tech-bullet, .gallery-item, .gallery-ph, .feature-list-col, .tl-item, .story-left, .story-right'
).forEach((el, i) => {
    el.style.cssText += `opacity:0;transform:translateY(22px);transition:opacity .5s ${i*.04}s ease,transform .5s ${i*.04}s ease`;
    fadeIO.observe(el);
});

/* ── Music player ── */
(function initPlayer() {
    const audio    = document.getElementById('bg-music');
    const btn      = document.getElementById('player-btn');
    const iconPlay  = document.getElementById('icon-play');
    const iconPause = document.getElementById('icon-pause');
    const progress = document.getElementById('player-progress');
    const player   = document.getElementById('player');
    if (!audio || !btn) return;

    audio.volume = 0.35;

    function setPlaying(playing) {
        iconPlay.style.display  = playing ? 'none'  : 'block';
        iconPause.style.display = playing ? 'block' : 'none';
        player.classList.toggle('active', playing);
    }

    // Auto-play on first user interaction anywhere on the page
    let started = false;
    function tryStart() {
        if (started) return;
        started = true;
        audio.play().then(() => setPlaying(true)).catch(() => {});
        document.removeEventListener('click', tryStart);
        document.removeEventListener('scroll', tryStart);
        document.removeEventListener('keydown', tryStart);
    }
    document.addEventListener('click',   tryStart, { once: true, passive: true });
    document.addEventListener('scroll',  tryStart, { once: true, passive: true });
    document.addEventListener('keydown', tryStart, { once: true, passive: true });

    btn.addEventListener('click', e => {
        e.stopPropagation();
        if (audio.paused) {
            audio.play(); setPlaying(true);
        } else {
            audio.pause(); setPlaying(false);
        }
    });

    // Progress bar
    audio.addEventListener('timeupdate', () => {
        if (!audio.duration) return;
        progress.style.width = (audio.currentTime / audio.duration * 100) + '%';
    }, { passive: true });
})();

/* ── Smooth anchor scroll ── */
document.querySelectorAll('a[href^="#"]').forEach(a => {
    a.addEventListener('click', e => {
        const t = document.querySelector(a.getAttribute('href'));
        if (t) { e.preventDefault(); t.scrollIntoView({ behavior: 'smooth' }); }
    });
});
