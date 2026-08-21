struct Params {
    point_vel: vec4f,
    color_radius: vec4f,
    texel_veltexel: vec4f,
    srcres_light: vec4f,
    misc_a: vec4f,
    misc_b: vec4f,
    misc_c: vec4f,
    misc_d: vec4f,
    misc_e: vec4f,
    misc_f: vec4f,
};

@group(0) @binding(0) var<uniform> P: Params;
@group(0) @binding(1) var samp: sampler;
@group(0) @binding(2) var t0: texture_2d<f32>;
@group(0) @binding(3) var t1: texture_2d<f32>;
@group(0) @binding(4) var t2: texture_2d<f32>;

struct VsOut {
    @builtin(position) clip: vec4f,
    @location(0) uv: vec2f,
};

@vertex
fn vs_main(@builtin(vertex_index) vid: u32) -> VsOut {
    var uv = vec2f(f32((vid << 1u) & 2u), f32(vid & 2u));
    var o: VsOut;
    o.uv = uv;
    o.clip = vec4f(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    return o;
}

fn sample0(uv: vec2f) -> vec4f { return textureSampleLevel(t0, samp, uv, 0.0); }
fn sample1(uv: vec2f) -> vec4f { return textureSampleLevel(t1, samp, uv, 0.0); }
fn sample2(uv: vec2f) -> vec4f { return textureSampleLevel(t2, samp, uv, 0.0); }

fn bilerp1(uv: vec2f, res: vec2f) -> vec4f {
    let px = uv * res - 0.5;
    let f = fract(px);
    let p0 = (floor(px) + 0.5) / res;
    let t = 1.0 / res;
    let a = sample1(p0);
    let b = sample1(p0 + vec2f(t.x, 0.0));
    let c = sample1(p0 + vec2f(0.0, t.y));
    let d = sample1(p0 + t);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

@fragment
fn ps_splat(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    var p = uv - P.point_vel.xy;
    p.x *= P.misc_a.x;
    let g = exp(-dot(p, p) / max(P.color_radius.w, 1e-6));
    let base = sample0(uv);
    let mode = i32(P.misc_f.x);
    if (mode == 0) {
        return base + vec4f(P.point_vel.zw * g, 0.0, 0.0);
    }
    if (mode == 1) {
        let room = 1.0 - smoothstep(0.9, 2.4, base.a);
        return base + vec4f(P.color_radius.xyz * g * 1.7, g * P.misc_a.y) * room;
    }
    let room_t = 1.0 - smoothstep(0.9, 2.4, base.r);
    return base + vec4f(P.misc_a.z * g, 0.0, 0.0, 0.0) * room_t;
}

@fragment
fn ps_advect(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    let vel = sample0(uv).xy;
    var coord = uv - P.misc_a.w * vel * P.texel_veltexel.zw;
    coord = clamp(coord, P.texel_veltexel.xy, 1.0 - P.texel_veltexel.xy);
    return bilerp1(coord, P.srcres_light.xy) * P.misc_b.x;
}

@fragment
fn ps_mac(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    let phi = sample0(uv);
    let hat = sample1(uv);
    let hathat = sample2(uv);
    var n = hat + 0.5 * (phi - hathat);
    let t = P.texel_veltexel.xy;
    let c0 = sample1(uv + vec2f(-t.x, 0.0));
    let c1 = sample1(uv + vec2f(t.x, 0.0));
    let c2 = sample1(uv + vec2f(0.0, -t.y));
    let c3 = sample1(uv + vec2f(0.0, t.y));
    let mn = min(hat, min(c0, min(c1, min(c2, c3))));
    let mx = max(hat, max(c0, max(c1, max(c2, c3))));
    n = clamp(n, mn, mx);
    return max(n * P.misc_b.x, vec4f(0.0));
}

@fragment
fn ps_buoyancy(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    var vel = sample0(uv).xy;
    let d = sample1(uv).a;
    let temp = sample2(uv).r;
    let accel = vec2f(0.0, (P.misc_b.y * temp - P.misc_b.z * d) * 0.25);
    vel += accel * P.misc_a.w / max(P.texel_veltexel.y, 1e-6);
    return vec4f(vel, 0.0, 1.0);
}

@fragment
fn ps_curl(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    let t = P.texel_veltexel.xy;
    let L = sample0(uv - vec2f(t.x, 0.0)).xy;
    let R = sample0(uv + vec2f(t.x, 0.0)).xy;
    let B = sample0(uv - vec2f(0.0, t.y)).xy;
    let T = sample0(uv + vec2f(0.0, t.y)).xy;
    let curl = R.y - L.y - T.x + B.x;
    return vec4f(0.5 * curl, 0.0, 0.0, 1.0);
}

@fragment
fn ps_vort(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    let t = P.texel_veltexel.xy;
    let L = sample1(uv - vec2f(t.x, 0.0)).r;
    let R = sample1(uv + vec2f(t.x, 0.0)).r;
    let B = sample1(uv - vec2f(0.0, t.y)).r;
    let T = sample1(uv + vec2f(0.0, t.y)).r;
    let C = sample1(uv).r;
    var force = 0.5 * vec2f(abs(T) - abs(B), abs(R) - abs(L));
    force = force / (length(force) + 1e-4) * P.misc_b.w * C;
    force.y = -force.y;
    let vel = sample0(uv).xy;
    return vec4f(vel + force * P.misc_a.w, 0.0, 1.0);
}

@fragment
fn ps_divergence(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    let t = P.texel_veltexel.xy;
    var vL = sample0(uv - vec2f(t.x, 0.0)).xy;
    var vR = sample0(uv + vec2f(t.x, 0.0)).xy;
    var vB = sample0(uv - vec2f(0.0, t.y)).xy;
    var vT = sample0(uv + vec2f(0.0, t.y)).xy;
    let vC = sample0(uv).xy;
    if (uv.x - t.x < 0.0) { vL.x = -vC.x; }
    if (uv.x + t.x > 1.0) { vR.x = -vC.x; }
    if (uv.y - t.y < 0.0) { vB.y = -vC.y; }
    if (uv.y + t.y > 1.0) { vT.y = -vC.y; }
    let div = 0.5 * (vR.x - vL.x + vT.y - vB.y);
    return vec4f(div, 0.0, 0.0, 1.0);
}

@fragment
fn ps_jacobi(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    let t = P.texel_veltexel.xy;
    let L = sample0(uv - vec2f(t.x, 0.0)).r;
    let R = sample0(uv + vec2f(t.x, 0.0)).r;
    let B = sample0(uv - vec2f(0.0, t.y)).r;
    let T = sample0(uv + vec2f(0.0, t.y)).r;
    let div = sample1(uv).r;
    let p = (L + R + B + T - div) * 0.25;
    return vec4f(p, 0.0, 0.0, 1.0);
}

@fragment
fn ps_gradient(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    let t = P.texel_veltexel.xy;
    let L = sample1(uv - vec2f(t.x, 0.0)).r;
    let R = sample1(uv + vec2f(t.x, 0.0)).r;
    let B = sample1(uv - vec2f(0.0, t.y)).r;
    let T = sample1(uv + vec2f(0.0, t.y)).r;
    var vel = sample0(uv).xy;
    vel -= vec2f(R - L, T - B) * 0.5;
    if (uv.x < t.x) { vel.x = 0.0; }
    if (uv.x > 1.0 - t.x) { vel.x = 0.0; }
    if (uv.y < t.y) { vel.y = 0.0; }
    if (uv.y > 1.0 - t.y) { vel.y = 0.0; }
    return vec4f(vel, 0.0, 1.0);
}

@fragment
fn ps_diffuse(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    let t = P.texel_veltexel.xy;
    let c = sample0(uv);
    let L = sample0(uv - vec2f(t.x, 0.0));
    let R = sample0(uv + vec2f(t.x, 0.0));
    let B = sample0(uv - vec2f(0.0, t.y));
    let T = sample0(uv + vec2f(0.0, t.y));
    return mix(c, (L + R + B + T) * 0.25, P.misc_c.x);
}

@fragment
fn ps_fade(i: VsOut) -> @location(0) vec4f {
    return sample0(i.uv) * P.misc_c.y;
}

fn fire_ramp(t_in: f32) -> vec3f {
    let t = clamp(t_in / 1.5, 0.0, 1.0);
    var c = vec3f(0.0);
    c = mix(c, vec3f(0.40, 0.02, 0.00), smoothstep(0.00, 0.18, t));
    c = mix(c, vec3f(0.92, 0.18, 0.01), smoothstep(0.18, 0.40, t));
    c = mix(c, vec3f(1.00, 0.55, 0.06), smoothstep(0.40, 0.68, t));
    c = mix(c, vec3f(1.00, 0.90, 0.45), smoothstep(0.68, 0.90, t));
    c = mix(c, vec3f(1.00, 0.98, 0.92), smoothstep(0.90, 1.00, t));
    return c;
}

fn drop(uv: vec2f) -> f32 {
    if (P.misc_d.y <= 0.0) { return 1.0; }
    let e = abs(uv * 2.0 - 1.0);
    let b = max(e.x, e.y);
    return 1.0 - smoothstep(1.0 - P.misc_d.y, 1.0, b);
}

fn shadow(uv: vec2f) -> f32 {
    let dir = normalize(P.srcres_light.zw + vec2f(1e-5)) * P.texel_veltexel.xy * 3.0;
    var t = 1.0;
    var p = uv;
    let steps = i32(P.misc_f.y);
    for (var i = 0; i < 24; i++) {
        if (i >= steps) { break; }
        p -= dir;
        t *= exp(-sample0(p).a * P.misc_c.z * 0.18);
    }
    return t;
}

@fragment
fn ps_display(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    let dye = sample0(uv);
    let d = dye.a * drop(uv);
    let mode = i32(P.misc_f.x);
    if (mode == 1) {
        let col = dye.rgb / (1.0 + dye.rgb);
        let a = d / (d + 0.55);
        return vec4f(col * a, 1.0);
    }
    if (mode == 2) {
        let v = sample1(uv).xy;
        return vec4f(v.x * 4.0 + 0.5, v.y * 4.0 + 0.5, length(v) * 8.0, 1.0);
    }
    if (mode == 3) {
        let p = sample2(uv).r;
        return vec4f(max(p, 0.0) * 4.0 + 0.05, 0.12, max(-p, 0.0) * 4.0 + 0.05, 1.0);
    }
    if (mode == 6) {
        return vec4f(fire_ramp(sample1(uv).r), 1.0);
    }
    let sh = shadow(uv);
    let temp = sample1(uv).r * drop(uv);
    var col = dye.rgb * (0.22 + P.misc_c.w + sh * 0.82);
    let luma = dot(col, vec3f(0.22, 0.72, 0.06));
    col = mix(vec3f(luma), col, 1.55);
    col += fire_ramp(temp) * P.misc_d.x * temp;
    col *= P.misc_d.z;
    let alpha = 1.0 - exp(-d * 1.7);
    var mapped = col / (1.0 + col);
    mapped = pow(max(mapped, vec3f(0.0)), vec3f(0.9));
    mapped *= alpha;
    return vec4f(mapped, 1.0);
}

@fragment
fn ps_extract(i: VsOut) -> @location(0) vec4f {
    let c = sample0(i.uv);
    let l = dot(c.rgb, vec3f(0.2126, 0.7152, 0.0722));
    let k = smoothstep(P.misc_e.x, P.misc_e.x + 0.35, l);
    return vec4f(c.rgb * k, c.a);
}

@fragment
fn ps_blur(i: VsOut) -> @location(0) vec4f {
    let uv = i.uv;
    let t = P.misc_e.zw * P.texel_veltexel.xy;
    var s = sample0(uv).rgb * 0.227027;
    s += sample0(uv + t).rgb * 0.1945946;
    s += sample0(uv - t).rgb * 0.1945946;
    s += sample0(uv + 2.0 * t).rgb * 0.1216216;
    s += sample0(uv - 2.0 * t).rgb * 0.1216216;
    s += sample0(uv + 3.0 * t).rgb * 0.054054;
    s += sample0(uv - 3.0 * t).rgb * 0.054054;
    return vec4f(s, 1.0);
}

@fragment
fn ps_composite(i: VsOut) -> @location(0) vec4f {
    let b = sample0(i.uv);
    let bloom = sample1(i.uv).rgb;
    return vec4f(b.rgb + bloom * P.misc_e.y, 1.0);
}
