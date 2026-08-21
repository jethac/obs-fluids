use std::f32::consts::PI;
use std::path::PathBuf;

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Viz {
    Dye,
    Raw,
    Velocity,
    Pressure,
    Temperature,
}

#[derive(Clone, Copy)]
pub struct Splat {
    pub x: f32,
    pub y: f32,
    pub dx: f32,
    pub dy: f32,
    pub density: f32,
    pub temp: f32,
    pub radius: f32,
    pub color: [f32; 3],
}

struct Ghost {
    x: f32,
    y: f32,
    tx: f32,
    ty: f32,
    vx: f32,
    vy: f32,
    speed: f32,
    radius: f32,
    color: [f32; 3],
}

pub struct Sim {
    pub preset: &'static str,
    pub quality: &'static str,
    pub bloom: bool,
    pub attract: bool,
    pub paused: bool,
    pub energy: f32,
    pub viscosity: f32,
    pub swirl: f32,
    pub pressure_iters: i32,
    pub timestep: f32,
    pub vel_diss: f32,
    pub dens_diss: f32,
    pub temp_diss: f32,
    pub buoyancy: f32,
    pub gravity: f32,
    pub shadow: f32,
    pub ambient: f32,
    pub incandescence: f32,
    pub bloom_amt: f32,
    pub dropoff: f32,
    pub splat_force: f32,
    pub splat_radius: f32,
    pub density_amt: f32,
    pub temp_amt: f32,
    pub attract_force: f32,
    pub color: [f32; 3],
    pub viz: Viz,
    pub energy_src: &'static str,
    pub splats: Vec<Splat>,
    ghosts: Vec<Ghost>,
}

const DYE: [[f32; 3]; 6] = [
    [0.24, 0.94, 0.91],
    [1.00, 0.60, 0.24],
    [1.00, 0.31, 0.55],
    [1.00, 0.42, 0.16],
    [0.35, 0.66, 1.00],
    [0.91, 0.77, 0.42],
];

impl Default for Sim {
    fn default() -> Self {
        let mut s = Self {
            preset: "demo",
            quality: "high",
            bloom: true,
            attract: true,
            paused: false,
            energy: 0.5,
            viscosity: 0.0,
            swirl: 28.0,
            pressure_iters: 24,
            timestep: 1.0,
            vel_diss: 0.22,
            dens_diss: 0.2,
            temp_diss: 0.45,
            buoyancy: 0.55,
            gravity: 0.08,
            shadow: 1.35,
            ambient: 0.16,
            incandescence: 0.55,
            bloom_amt: 0.26,
            dropoff: 0.07,
            splat_force: 6400.0,
            splat_radius: 0.28,
            density_amt: 0.85,
            temp_amt: 0.35,
            attract_force: 1.0,
            color: DYE[0],
            viz: Viz::Dye,
            energy_src: "dial",
            splats: Vec::new(),
            ghosts: Vec::new(),
        };
        s.reset_ghosts();
        s
    }
}

impl Sim {
    pub fn apply_preset(&mut self, name: &str) {
        match name {
            "ink" => {
                self.preset = "ink";
                self.viscosity = 0.04;
                self.swirl = 18.0;
                self.pressure_iters = 22;
                self.vel_diss = 0.18;
                self.dens_diss = 0.04;
                self.temp_diss = 0.6;
                self.buoyancy = 0.08;
                self.gravity = 0.02;
                self.shadow = 0.85;
                self.ambient = 0.18;
                self.incandescence = 0.12;
                self.bloom_amt = 0.16;
                self.dropoff = 0.04;
                self.splat_force = 5200.0;
                self.splat_radius = 0.24;
                self.density_amt = 1.0;
                self.temp_amt = 0.05;
                self.attract_force = 0.85;
                self.color = DYE[0];
            }
            "cloud" => {
                self.preset = "cloud";
                self.viscosity = 0.08;
                self.swirl = 12.0;
                self.pressure_iters = 20;
                self.vel_diss = 0.28;
                self.dens_diss = 0.22;
                self.temp_diss = 0.35;
                self.buoyancy = 1.4;
                self.gravity = 0.15;
                self.shadow = 2.2;
                self.ambient = 0.18;
                self.incandescence = 0.0;
                self.bloom_amt = 0.08;
                self.dropoff = 0.1;
                self.splat_force = 3800.0;
                self.splat_radius = 0.4;
                self.density_amt = 0.55;
                self.temp_amt = 0.7;
                self.attract_force = 0.5;
                self.color = [0.85, 0.82, 0.77];
            }
            "fire" => {
                self.preset = "fire";
                self.viscosity = 0.0;
                self.swirl = 16.0;
                self.pressure_iters = 20;
                self.vel_diss = 0.38;
                self.dens_diss = 0.72;
                self.temp_diss = 0.55;
                self.buoyancy = 0.9;
                self.gravity = 0.04;
                self.shadow = 1.1;
                self.ambient = 0.08;
                self.incandescence = 1.15;
                self.bloom_amt = 0.55;
                self.dropoff = 0.06;
                self.splat_force = 4200.0;
                self.splat_radius = 0.22;
                self.density_amt = 0.45;
                self.temp_amt = 0.8;
                self.attract_force = 0.85;
                self.color = [1.0, 0.42, 0.16];
            }
            "fog" => {
                self.preset = "fog";
                self.viscosity = 0.18;
                self.swirl = 6.0;
                self.pressure_iters = 16;
                self.timestep = 0.9;
                self.vel_diss = 0.42;
                self.dens_diss = 0.18;
                self.temp_diss = 0.4;
                self.buoyancy = 0.35;
                self.gravity = 0.0;
                self.shadow = 1.6;
                self.ambient = 0.32;
                self.incandescence = 0.0;
                self.bloom_amt = 0.05;
                self.dropoff = 0.16;
                self.splat_force = 2400.0;
                self.splat_radius = 0.5;
                self.density_amt = 0.35;
                self.temp_amt = 0.2;
                self.attract_force = 0.4;
                self.color = [0.78, 0.82, 0.83];
            }
            _ => {
                self.preset = "demo";
                self.viscosity = 0.0;
                self.swirl = 28.0;
                self.pressure_iters = 24;
                self.timestep = 1.0;
                self.vel_diss = 0.22;
                self.dens_diss = 0.2;
                self.temp_diss = 0.45;
                self.buoyancy = 0.55;
                self.gravity = 0.08;
                self.shadow = 1.35;
                self.ambient = 0.16;
                self.incandescence = 0.55;
                self.bloom_amt = 0.26;
                self.dropoff = 0.07;
                self.splat_force = 6400.0;
                self.splat_radius = 0.28;
                self.density_amt = 0.85;
                self.temp_amt = 0.35;
                self.attract_force = 1.0;
                self.color = DYE[0];
            }
        }
        self.reset_ghosts();
    }

    pub fn drive(&self) -> f32 {
        let x = self.energy.clamp(0.0, 1.0);
        if x <= 0.5 {
            0.04 + 0.96 * (x / 0.5)
        } else {
            1.0 + (x - 0.5) * 2.0
        }
    }

    pub fn set_energy(&mut self, v: f32, src: &'static str) {
        let prev = self.energy;
        self.energy = v.clamp(0.0, 1.0);
        self.energy_src = src;
        let de = self.energy - prev;
        if de > 0.07 && src != "panel" {
            self.kick(de);
        }
    }

    pub fn nudge_energy(&mut self, steps: f32, src: &'static str) {
        self.set_energy(self.energy + steps * 0.018, src);
    }

    pub fn queue_splat(&mut self, x: f32, y: f32, dx: f32, dy: f32, color: [f32; 3], density: f32, temp: f32, radius: f32) {
        self.splats.push(Splat {
            x,
            y,
            dx,
            dy,
            color,
            density,
            temp,
            radius: (radius * radius).max(0.0002),
        });
    }

    pub fn pointer_splat(&mut self, x: f32, y: f32, dx: f32, dy: f32) {
        let f = self.splat_force * (0.35 + 0.65 * self.drive());
        self.queue_splat(x, y, dx * f, dy * f, self.color, self.density_amt, self.temp_amt, self.splat_radius);
    }

    pub fn seed(&mut self) {
        if self.preset == "fire" {
            for i in 0..3 {
                self.queue_splat(
                    0.3 + i as f32 * 0.2,
                    0.1,
                    0.0,
                    0.06 * self.splat_force,
                    [1.0, 0.42, 0.16],
                    0.22,
                    0.8,
                    0.12,
                );
            }
            return;
        }
        let pal = self.palette();
        for i in 0..6 {
            let ang = rand(0.0, PI * 2.0);
            let mag = rand(0.08, 0.18);
            self.queue_splat(
                rand(0.18, 0.82),
                rand(0.18, 0.82),
                ang.cos() * mag * self.splat_force,
                ang.sin() * mag * self.splat_force,
                pal[i % pal.len()],
                self.density_amt * 0.7,
                0.15,
                rand(0.1, 0.18),
            );
        }
    }

    pub fn reset_ghosts(&mut self) {
        self.ghosts.clear();
        if self.preset == "fire" {
            return;
        }
        let pal = self.palette();
        for i in 0..2 {
            self.ghosts.push(Ghost {
                x: rand(0.2, 0.8),
                y: rand(0.2, 0.8),
                tx: rand(0.15, 0.85),
                ty: rand(0.15, 0.85),
                vx: 0.0,
                vy: 0.0,
                speed: rand(0.35, 0.9),
                radius: rand(0.11, 0.2),
                color: pal[i % pal.len()],
            });
        }
    }

    pub fn tick_attract(&mut self, dt: f32, time: f32) {
        if !self.attract {
            return;
        }
        let k = self.attract_force * self.drive();
        let inj = 0.2 + 0.8 * self.drive();
        if self.preset == "fire" {
            for i in 0..3 {
                let x = 0.28 + i as f32 * 0.22 + 0.03 * (time * 0.7 + i as f32 * 1.7).sin();
                let y = 0.08 + 0.015 * (time * 1.3 + i as f32).sin();
                let col = if i % 2 == 0 { [1.0, 0.69, 0.23] } else { [1.0, 0.42, 0.16] };
                self.queue_splat(
                    x,
                    y,
                    (time * 2.0 + i as f32).sin() * 0.003 * self.splat_force,
                    0.02 * k * self.splat_force,
                    col,
                    0.03 * inj,
                    0.45 * inj,
                    0.13,
                );
            }
            return;
        }
        if self.preset == "cloud" || self.preset == "fog" {
            let col = [0.85, 0.82, 0.77];
            for i in 0..3 {
                let x = 0.3 + i as f32 * 0.2 + 0.05 * (time * 0.25 + i as f32).sin();
                let y = 0.12 + 0.03 * (time * 0.4 + i as f32 * 2.0).sin();
                self.queue_splat(
                    x,
                    y,
                    0.002 * self.splat_force,
                    0.02 * k * self.splat_force,
                    col,
                    self.density_amt * 0.12 * inj,
                    0.45 * inj,
                    0.42,
                );
            }
            return;
        }
        let pal = self.palette();
        let dr = self.drive();
        let force = self.splat_force;
        let dens = self.density_amt;
        let temp = self.temp_amt;
        for g in &mut self.ghosts {
            let ax = g.tx - g.x;
            let ay = g.ty - g.y;
            if ax * ax + ay * ay < 0.0025 {
                g.tx = rand(0.12, 0.88);
                g.ty = rand(0.12, 0.88);
                if fastrand() < 0.12 {
                    g.color = pal[hash_idx(pal.len())];
                }
            }
            g.vx += ax * g.speed * dt * 2.5 * dr;
            g.vy += ay * g.speed * dt * 2.5 * dr;
            g.vx *= 0.92;
            g.vy *= 0.92;
            let px = g.x;
            let py = g.y;
            g.x += g.vx * dt * 1.8;
            g.y += g.vy * dt * 1.8;
            self.splats.push(Splat {
                x: g.x,
                y: g.y,
                dx: (g.x - px) * force * k,
                dy: (g.y - py) * force * k,
                color: g.color,
                density: dens * 0.1 * k * inj,
                temp: temp * 0.25 * inj,
                radius: (g.radius * g.radius).max(0.0002),
            });
        }
    }

    fn kick(&mut self, de: f32) {
        let pal = self.palette();
        let n = 2 + (de * 8.0) as i32;
        for i in 0..n {
            let ang = rand(0.0, PI * 2.0);
            let mag = (0.04 + de) * self.splat_force;
            self.queue_splat(
                rand(0.25, 0.75),
                rand(0.25, 0.75),
                ang.cos() * mag,
                ang.sin() * mag,
                pal[i as usize % pal.len()],
                self.density_amt * 0.25 * de,
                0.2,
                0.14,
            );
        }
    }

    fn palette(&self) -> Vec<[f32; 3]> {
        match self.preset {
            "fire" => vec![[0.16, 0.09, 0.06], [1.0, 0.42, 0.16], [1.0, 0.69, 0.23]],
            "cloud" | "fog" => vec![[0.85, 0.82, 0.77], [0.66, 0.71, 0.75]],
            "ink" => vec![DYE[0], DYE[2]],
            _ => vec![DYE[0], DYE[1]],
        }
    }

    pub fn sim_short(quality: &str) -> u32 {
        match quality {
            "low" => 128,
            "medium" => 192,
            "ultra" => 384,
            _ => 256,
        }
    }

    pub fn next_preset(&mut self) {
        let n = match self.preset {
            "demo" => "ink",
            "ink" => "cloud",
            "cloud" => "fire",
            "fire" => "fog",
            _ => "demo",
        };
        self.apply_preset(n);
        self.save_settings();
    }

    pub fn load_settings(&mut self) {
        let Some(dir) = config_dir() else { return };
        let path = dir.join("settings.json");
        let Ok(text) = std::fs::read_to_string(path) else { return };
        let Ok(v) = serde_json::from_str::<serde_json::Value>(&text) else { return };
        if let Some(p) = v.get("preset").and_then(|x| x.as_str()) {
            self.apply_preset(p);
        }
        if let Some(q) = v.get("quality").and_then(|x| x.as_str()) {
            self.quality = match q {
                "low" => "low",
                "medium" => "medium",
                "ultra" => "ultra",
                _ => "high",
            };
        }
        if let Some(e) = v.get("energy").and_then(|x| x.as_f64()) {
            self.energy = (e as f32).clamp(0.0, 1.0);
        }
        if let Some(a) = v.get("attract").and_then(|x| x.as_bool()) {
            self.attract = a;
        }
        if let Some(b) = v.get("bloom").and_then(|x| x.as_bool()) {
            self.bloom = b;
        }
    }

    pub fn save_settings(&self) {
        let Some(dir) = config_dir() else { return };
        let _ = std::fs::create_dir_all(&dir);
        let v = serde_json::json!({
            "preset": self.preset,
            "quality": self.quality,
            "energy": self.energy,
            "attract": self.attract,
            "bloom": self.bloom,
        });
        let _ = std::fs::write(dir.join("settings.json"), v.to_string());
    }
}

pub fn config_dir() -> Option<PathBuf> {
    if let Ok(p) = std::env::var("LOCALAPPDATA") {
        return Some(PathBuf::from(p).join("InkContainer"));
    }
    if let Ok(p) = std::env::var("XDG_CONFIG_HOME") {
        return Some(PathBuf::from(p).join("ink-container"));
    }
    if let Ok(home) = std::env::var("HOME") {
        return Some(PathBuf::from(home).join(".config").join("ink-container"));
    }
    None
}

fn fastrand() -> f32 {
    rand(0.0, 1.0)
}

fn hash_idx(n: usize) -> usize {
    (rand(0.0, 1.0) * n as f32) as usize % n
}

fn rand(a: f32, b: f32) -> f32 {
    thread_rng(a, b)
}

fn thread_rng(a: f32, b: f32) -> f32 {
    use std::cell::Cell;
    thread_local! {
        static S: Cell<u64> = const { Cell::new(0x4d595df4d0f33173) };
    }
    S.with(|s| {
        let mut x = s.get();
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        s.set(x);
        a + (x as f32 / u64::MAX as f32) * (b - a)
    })
}
