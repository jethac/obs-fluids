mod gpu;
mod sim;

use gpu::Gpu;
use sim::{Sim, Viz};
use std::sync::mpsc::{self, Receiver, TryRecvError};
use std::sync::Arc;
use std::time::Instant;
use winit::application::ApplicationHandler;
use winit::event::{ElementState, MouseButton, MouseScrollDelta, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
use winit::keyboard::{KeyCode, PhysicalKey};
use winit::window::{Fullscreen, Window, WindowId};

#[derive(Clone, Copy, PartialEq, Eq)]
enum Mode {
    App,
    Screensaver,
    Obs,
    Config,
}

enum Cmd {
    EnergyDelta(i32),
    EnergyAbs(f32),
}

struct App {
    mode: Mode,
    window: Option<Arc<Window>>,
    gpu: Option<Gpu>,
    sim: Sim,
    last: Instant,
    t0: Instant,
    dragging: bool,
    px: f32,
    py: f32,
    saver_armed: bool,
    origin: (f64, f64),
    cmds: Receiver<Cmd>,
    fps: f64,
}

fn parse_mode() -> Mode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() {
        return Mode::App;
    }
    for a in &args {
        let l = a.to_ascii_lowercase();
        if l == "--obs" || l == "/obs" || l == "--source" {
            return Mode::Obs;
        }
        if l == "--config" || l.starts_with("/c") {
            return Mode::Config;
        }
        if l.starts_with("/s") {
            return Mode::Screensaver;
        }
        if l.starts_with("/p") {
            // Windows preview HWND: don't steal the settings dialog.
            std::process::exit(0);
        }
    }
    Mode::App
}

fn spawn_energy() -> Receiver<Cmd> {
    let (tx, rx) = mpsc::channel();
    let tx2 = tx.clone();
    std::thread::spawn(move || {
        use std::io::{Read, Write};
        let Ok(listener) = std::net::TcpListener::bind("127.0.0.1:17331") else {
            return;
        };
        for mut s in listener.incoming().flatten() {
            let _ = s.set_read_timeout(Some(std::time::Duration::from_millis(400)));
            let mut buf = Vec::new();
            let mut tmp = [0u8; 512];
            loop {
                match s.read(&mut tmp) {
                    Ok(0) => break,
                    Ok(n) => {
                        buf.extend_from_slice(&tmp[..n]);
                        if buf.windows(4).any(|w| w == b"\r\n\r\n") || buf.len() > 8192 {
                            break;
                        }
                    }
                    Err(_) => break,
                }
            }
            let req = String::from_utf8_lossy(&buf);
            let body = req
                .split("\r\n\r\n")
                .nth(1)
                .unwrap_or(&req);
            if let Some(i) = body.find('{') {
                if let Ok(v) = serde_json::from_str::<serde_json::Value>(&body[i..]) {
                    if let Some(d) = v.get("delta").and_then(|x| x.as_i64()) {
                        let _ = tx.send(Cmd::EnergyDelta(d as i32));
                    }
                    if let Some(e) = v.get("energy").and_then(|x| x.as_f64()) {
                        let _ = tx.send(Cmd::EnergyAbs(e as f32));
                    }
                }
            }
            let _ = s.write_all(b"HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
            let _ = s.shutdown(std::net::Shutdown::Both);
        }
    });
    std::thread::spawn(move || {
        let Ok(api) = hidapi::HidApi::new() else { return };
        for d in api.device_list() {
            if d.vendor_id() != 0x046d || (d.product_id() != 0xbc00 && d.product_id() != 0xc354) {
                continue;
            }
            let Ok(dev) = d.open_device(&api) else { continue };
            let mut prev: Option<[u8; 64]> = None;
            let mut buf = [0u8; 64];
            loop {
                let Ok(n) = dev.read_timeout(&mut buf, 250) else { break };
                if n <= 0 {
                    continue;
                }
                if let Some(p) = prev {
                    let mut best = 0i32;
                    for i in 0..n.min(64) {
                        let mut dlt = buf[i] as i32 - p[i] as i32;
                        if dlt > 127 {
                            dlt -= 256;
                        }
                        if dlt < -128 {
                            dlt += 256;
                        }
                        if dlt != 0 && dlt.abs() <= 24 && dlt.abs() > best.abs() {
                            best = dlt;
                        }
                    }
                    if best != 0 {
                        let _ = tx2.send(Cmd::EnergyDelta(best));
                    }
                }
                prev = Some(buf);
            }
        }
    });
    rx
}

fn main() {
    std::panic::set_hook(Box::new(|info| {
        let msg = format!("panic: {info}\n");
        eprint!("{msg}");
        if let Some(dir) = sim::config_dir() {
            let _ = std::fs::create_dir_all(&dir);
            let _ = std::fs::write(dir.join("crash.log"), msg);
        }
    }));
    let mode = parse_mode();
    let event_loop = EventLoop::new().expect("event loop");
    event_loop.set_control_flow(ControlFlow::Poll);
    let mut sim = Sim::default();
    sim.load_settings();
    let mut app = App {
        mode,
        window: None,
        gpu: None,
        sim,
        last: Instant::now(),
        t0: Instant::now(),
        dragging: false,
        px: 0.5,
        py: 0.5,
        saver_armed: false,
        origin: (0.0, 0.0),
        cmds: spawn_energy(),
        fps: 60.0,
    };
    event_loop.run_app(&mut app).expect("run");
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.window.is_some() {
            return;
        }
        let mut attrs = Window::default_attributes()
            .with_title("Ink Container")
            .with_inner_size(winit::dpi::LogicalSize::new(1440.0, 900.0));
        match self.mode {
            Mode::Screensaver => {
                attrs = attrs.with_fullscreen(Some(Fullscreen::Borderless(None)));
            }
            Mode::Obs => {
                attrs = attrs
                    .with_decorations(false)
                    .with_inner_size(winit::dpi::LogicalSize::new(1920.0, 1080.0));
            }
            Mode::Config => {
                attrs = attrs.with_title("Ink Container — settings").with_inner_size(winit::dpi::LogicalSize::new(960.0, 600.0));
            }
            Mode::App => {}
        }
        let window = Arc::new(event_loop.create_window(attrs).expect("window"));
        if self.mode == Mode::Screensaver {
            window.set_cursor_visible(false);
        }
        match pollster::block_on(Gpu::new(window.clone(), self.sim.quality)) {
            Ok(gpu) => {
                self.sim.reset_ghosts();
                self.sim.seed();
                self.gpu = Some(gpu);
            }
            Err(e) => {
                eprintln!("{e}");
                #[cfg(target_os = "windows")]
                {
                    let _ = std::process::Command::new("msg").arg("*").arg(&e).status();
                }
                event_loop.exit();
                return;
            }
        }
        self.window = Some(window);
        self.last = Instant::now();
        if self.mode == Mode::Screensaver {
            self.origin = (0.0, 0.0);
            self.saver_armed = false;
        }
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, _id: WindowId, event: WindowEvent) {
        match event {
            WindowEvent::CloseRequested => event_loop.exit(),
            WindowEvent::Resized(sz) => {
                if let Some(gpu) = self.gpu.as_mut() {
                    gpu.resize(sz.width.max(8), sz.height.max(8));
                    self.sim.seed();
                }
            }
            WindowEvent::RedrawRequested => self.redraw(),
            WindowEvent::MouseInput { state, button, .. } => {
                if self.mode == Mode::Screensaver && self.saver_armed && state == ElementState::Pressed {
                    event_loop.exit();
                    return;
                }
                if button == MouseButton::Left {
                    self.dragging = state == ElementState::Pressed;
                }
            }
            WindowEvent::CursorMoved { position, .. } => {
                if self.mode == Mode::Screensaver {
                    if !self.saver_armed {
                        self.origin = (position.x, position.y);
                        self.saver_armed = true;
                    } else if (position.x - self.origin.0).abs() + (position.y - self.origin.1).abs() > 16.0 {
                        event_loop.exit();
                    }
                    return;
                }
                let Some(win) = self.window.as_ref() else { return };
                let sz = win.inner_size();
                let x = (position.x as f32 / sz.width as f32).clamp(0.0, 1.0);
                let y = 1.0 - (position.y as f32 / sz.height as f32).clamp(0.0, 1.0);
                if self.dragging {
                    self.sim.pointer_splat(x, y, x - self.px, y - self.py);
                }
                self.px = x;
                self.py = y;
            }
            WindowEvent::MouseWheel { delta, .. } => {
                let steps = match delta {
                    MouseScrollDelta::LineDelta(_, y) => y.signum(),
                    MouseScrollDelta::PixelDelta(p) => p.y.signum() as f32,
                };
                self.sim.nudge_energy(steps, "wheel");
            }
            WindowEvent::KeyboardInput { event, .. } => {
                if event.state != ElementState::Pressed {
                    return;
                }
                let PhysicalKey::Code(code) = event.physical_key else { return };
                if matches!(
                    code,
                    KeyCode::BracketLeft
                        | KeyCode::BracketRight
                        | KeyCode::Minus
                        | KeyCode::Equal
                        | KeyCode::Comma
                        | KeyCode::Period
                        | KeyCode::PageUp
                        | KeyCode::PageDown
                ) {
                    let dir = if matches!(code, KeyCode::BracketRight | KeyCode::Equal | KeyCode::Period | KeyCode::PageUp) {
                        1.0
                    } else {
                        -1.0
                    };
                    self.sim.nudge_energy(dir, "key");
                    return;
                }
                if self.mode == Mode::Screensaver {
                    event_loop.exit();
                    return;
                }
                match code {
                    KeyCode::Escape => event_loop.exit(),
                    KeyCode::Space => self.sim.paused = !self.sim.paused,
                    KeyCode::KeyR => {
                        if let Some(g) = self.gpu.as_mut() {
                            g.clear();
                        }
                        self.sim.reset_ghosts();
                        self.sim.seed();
                    }
                    KeyCode::KeyC => {
                        if let Some(g) = self.gpu.as_mut() {
                            g.clear();
                        }
                    }
                    KeyCode::KeyA => {
                        self.sim.attract = !self.sim.attract;
                        self.sim.save_settings();
                    }
                    KeyCode::KeyP => self.sim.next_preset(),
                    KeyCode::Digit1 => self.sim.viz = Viz::Dye,
                    KeyCode::Digit2 => self.sim.viz = Viz::Raw,
                    KeyCode::Digit3 => self.sim.viz = Viz::Velocity,
                    KeyCode::Digit4 => self.sim.viz = Viz::Pressure,
                    KeyCode::Digit5 => self.sim.viz = Viz::Temperature,
                    _ => {}
                }
            }
            _ => {}
        }
    }

    fn about_to_wait(&mut self, _event_loop: &ActiveEventLoop) {
        if let Some(w) = &self.window {
            w.request_redraw();
        }
    }

    fn exiting(&mut self, _event_loop: &ActiveEventLoop) {
        self.sim.save_settings();
    }
}

impl App {
    fn redraw(&mut self) {
        loop {
            match self.cmds.try_recv() {
                Ok(Cmd::EnergyDelta(d)) => self.sim.nudge_energy(d as f32, "mx"),
                Ok(Cmd::EnergyAbs(v)) => self.sim.set_energy(v, "host"),
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => break,
            }
        }
        let now = Instant::now();
        let raw = (now - self.last).as_secs_f32().min(0.05).max(0.001);
        self.last = now;
        self.fps = self.fps * 0.9 + (1.0 / raw as f64) * 0.1;
        let t = now.duration_since(self.t0).as_secs_f32();
        if !self.sim.paused {
            self.sim.tick_attract(raw * self.sim.timestep, t);
        }
        let dt = raw * self.sim.timestep;
        if let Some(gpu) = self.gpu.as_mut() {
            gpu.frame(&mut self.sim, dt);
            if let Some(w) = &self.window {
                w.set_title(&format!(
                    "Ink Container  {}×{}  {:.0} fps  ENERGY {:.1}  [{}]",
                    gpu.vel_w,
                    gpu.vel_h,
                    self.fps,
                    self.sim.energy * 10.0,
                    self.sim.preset
                ));
            }
        }
    }
}
