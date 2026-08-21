use crate::sim::{Sim, Splat, Viz};
use std::collections::HashMap;
use std::sync::Arc;
use winit::window::Window;

#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable, Default)]
struct GpuParams {
    point_vel: [f32; 4],
    color_radius: [f32; 4],
    texel_veltexel: [f32; 4],
    srcres_light: [f32; 4],
    misc_a: [f32; 4],
    misc_b: [f32; 4],
    misc_c: [f32; 4],
    misc_d: [f32; 4],
    misc_e: [f32; 4],
    misc_f: [f32; 4],
}

struct Rt {
    _tex: wgpu::Texture,
    view: wgpu::TextureView,
    w: u32,
    h: u32,
}

struct Ping {
    a: Rt,
    b: Rt,
}

impl Ping {
    fn read(&self) -> &Rt {
        &self.a
    }
    fn write(&self) -> &Rt {
        &self.b
    }
    fn swap(&mut self) {
        std::mem::swap(&mut self.a, &mut self.b);
    }
}

pub struct Gpu {
    surface: wgpu::Surface<'static>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    config: wgpu::SurfaceConfiguration,
    layout: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    dummy: Rt,
    ubo: wgpu::Buffer,
    sim_pipes: HashMap<&'static str, wgpu::RenderPipeline>,
    present_pipes: HashMap<&'static str, wgpu::RenderPipeline>,
    vel: Ping,
    dye: Ping,
    temp: Ping,
    pressure: Ping,
    div: Rt,
    curl: Rt,
    hat: Rt,
    hathat: Rt,
    beauty: Rt,
    bloom0: Rt,
    bloom1: Rt,
    pub vel_w: u32,
    pub vel_h: u32,
    pub dye_w: u32,
    pub dye_h: u32,
    quality: String,
}

impl Gpu {
    pub async fn new(window: Arc<Window>, quality: &str) -> Result<Self, String> {
        let size = window.inner_size();
        let w = size.width.max(8);
        let h = size.height.max(8);

        let mut desc = wgpu::InstanceDescriptor::default();
        desc.backends = preferred_backends();
        let instance = wgpu::Instance::new(&desc);
        let surface = instance
            .create_surface(window.clone())
            .map_err(|e| format!("surface: {e}"))?;
        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions {
                power_preference: wgpu::PowerPreference::HighPerformance,
                compatible_surface: Some(&surface),
                force_fallback_adapter: false,
            })
            .await
            .ok_or_else(|| "no GPU adapter (install a Vulkan driver)".to_string())?;

        {
            let info = adapter.get_info();
            let line = format!(
                "adapter={} backend={:?} vendor={:#x} device={:#x}\n",
                info.name, info.backend, info.vendor, info.device
            );
            eprint!("{line}");
            if let Some(dir) = crate::sim::config_dir() {
                let _ = std::fs::create_dir_all(&dir);
                let _ = std::fs::write(dir.join("gpu.log"), line);
            }
        }

        let (device, queue) = adapter
            .request_device(
                &wgpu::DeviceDescriptor {
                    label: Some("ink"),
                    required_features: wgpu::Features::empty(),
                    required_limits: wgpu::Limits::default(),
                    memory_hints: Default::default(),
                },
                None,
            )
            .await
            .map_err(|e| format!("device: {e}"))?;

        let caps = surface.get_capabilities(&adapter);
        let format = caps
            .formats
            .iter()
            .copied()
            .find(|f| f.is_srgb())
            .unwrap_or(caps.formats[0]);
        let config = wgpu::SurfaceConfiguration {
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            format,
            width: w,
            height: h,
            present_mode: wgpu::PresentMode::AutoVsync,
            alpha_mode: caps.alpha_modes[0],
            view_formats: vec![],
            desired_maximum_frame_latency: 2,
        };
        surface.configure(&device, &config);

        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("sim-bgl"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
                tex_entry(2),
                tex_entry(3),
                tex_entry(4),
            ],
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        let dummy = make_rt(&device, 4, 4, "dummy");
        let ubo = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ubo"),
            size: std::mem::size_of::<GpuParams>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let src = include_str!("../shaders/fluid.wgsl");
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("fluid"),
            source: wgpu::ShaderSource::Wgsl(src.into()),
        });
        let pl = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("pl"),
            bind_group_layouts: &[&layout],
            push_constant_ranges: &[],
        });

        let sim_fmt = wgpu::TextureFormat::Rgba16Float;
        let mut sim_pipes = HashMap::new();
        let mut present_pipes = HashMap::new();
        for name in [
            "ps_splat",
            "ps_advect",
            "ps_mac",
            "ps_buoyancy",
            "ps_curl",
            "ps_vort",
            "ps_divergence",
            "ps_jacobi",
            "ps_gradient",
            "ps_diffuse",
            "ps_fade",
            "ps_display",
            "ps_extract",
            "ps_blur",
            "ps_composite",
        ] {
            sim_pipes.insert(name, make_pipe(&device, &pl, &module, name, sim_fmt));
        }
        present_pipes.insert("ps_fade", make_pipe(&device, &pl, &module, "ps_fade", format));
        present_pipes.insert("ps_composite", make_pipe(&device, &pl, &module, "ps_composite", format));

        let vel = make_ping(&device, 32, 32, "vel");
        let dye = make_ping(&device, 32, 32, "dye");
        let temp = make_ping(&device, 32, 32, "temp");
        let pressure = make_ping(&device, 32, 32, "p");
        let div = make_rt(&device, 32, 32, "div");
        let curl = make_rt(&device, 32, 32, "curl");
        let hat = make_rt(&device, 32, 32, "hat");
        let hathat = make_rt(&device, 32, 32, "hathat");
        let beauty = make_rt(&device, w, h, "beauty");
        let bloom0 = make_rt(&device, 32, 32, "b0");
        let bloom1 = make_rt(&device, 32, 32, "b1");
        let mut gpu = Self {
            surface,
            device,
            queue,
            config,
            layout,
            sampler,
            dummy,
            ubo,
            sim_pipes,
            present_pipes,
            vel,
            dye,
            temp,
            pressure,
            div,
            curl,
            hat,
            hathat,
            beauty,
            bloom0,
            bloom1,
            vel_w: 1,
            vel_h: 1,
            dye_w: 1,
            dye_h: 1,
            quality: quality.to_string(),
        };
        gpu.rebuild_sim();
        Ok(gpu)
    }

    fn rebuild_sim(&mut self) {
        let w = self.config.width;
        let h = self.config.height;
        let short = Sim::sim_short(&self.quality);
        let aspect = w as f32 / h as f32;
        let (sw, sh) = if aspect >= 1.0 {
            let sh = short;
            let sw = ((short as f32 * aspect / 8.0).round() as u32 * 8).max(32);
            (sw, sh)
        } else {
            let sw = short;
            let sh = ((short as f32 / aspect / 8.0).round() as u32 * 8).max(32);
            (sw, sh)
        };
        let dw = (sw * 2 / 8 * 8).max(32);
        let dh = (sh * 2 / 8 * 8).max(32);
        self.vel_w = sw;
        self.vel_h = sh;
        self.dye_w = dw;
        self.dye_h = dh;
        let d = &self.device;
        self.vel = make_ping(d, sw, sh, "vel");
        self.dye = make_ping(d, dw, dh, "dye");
        self.temp = make_ping(d, sw, sh, "temp");
        self.pressure = make_ping(d, sw, sh, "p");
        self.div = make_rt(d, sw, sh, "div");
        self.curl = make_rt(d, sw, sh, "curl");
        self.hat = make_rt(d, dw, dh, "hat");
        self.hathat = make_rt(d, dw, dh, "hathat");
        self.beauty = make_rt(d, w, h, "beauty");
        self.bloom0 = make_rt(d, (w / 2).max(8), (h / 2).max(8), "b0");
        self.bloom1 = make_rt(d, (w / 2).max(8), (h / 2).max(8), "b1");
        self.clear_fields();
    }

    fn clear_fields(&self) {
        let mut encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("clear"),
        });
        for view in [
            &self.vel.a.view,
            &self.vel.b.view,
            &self.dye.a.view,
            &self.dye.b.view,
            &self.temp.a.view,
            &self.temp.b.view,
            &self.pressure.a.view,
            &self.pressure.b.view,
            &self.div.view,
            &self.curl.view,
            &self.hat.view,
            &self.hathat.view,
            &self.beauty.view,
            &self.bloom0.view,
            &self.bloom1.view,
        ] {
            clear_view(&mut encoder, view);
        }
        self.queue.submit(Some(encoder.finish()));
    }

    pub fn resize(&mut self, w: u32, h: u32) {
        if w < 8 || h < 8 {
            return;
        }
        self.config.width = w;
        self.config.height = h;
        self.surface.configure(&self.device, &self.config);
        self.rebuild_sim();
    }

    pub fn clear(&mut self) {
        self.rebuild_sim();
    }

    pub fn frame(&mut self, sim: &mut Sim, dt: f32) {
        let dr = sim.drive();
        let mut encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: Some("frame") });
        if !sim.paused {
            let splats: Vec<Splat> = sim.splats.drain(..).collect();
            for s in splats {
                self.splat(&mut encoder, &s);
            }
            self.step(&mut encoder, sim, dt, dr);
        }
        self.display(&mut encoder, sim);

        let ok = self.surface.get_current_texture();
        let surface = match ok {
            Ok(t) => t,
            Err(_) => {
                self.surface.configure(&self.device, &self.config);
                self.surface.get_current_texture().expect("surface")
            }
        };
        let view = surface.texture.create_view(&Default::default());
        if sim.bloom && sim.bloom_amt > 0.01 && sim.viz == Viz::Dye {
            let mut p = self.base();
            p.misc_e[1] = sim.bloom_amt;
            self.blit_to(&mut encoder, "ps_composite", true, &view, self.config.width, self.config.height, p, &[&self.beauty.view, &self.bloom0.view, &self.dummy.view]);
        } else {
            let mut p = GpuParams::default();
            p.misc_c[1] = 1.0;
            self.blit_to(&mut encoder, "ps_fade", true, &view, self.config.width, self.config.height, p, &[&self.beauty.view, &self.dummy.view, &self.dummy.view]);
        }
        self.queue.submit(Some(encoder.finish()));
        surface.present();
    }

    fn splat(&mut self, encoder: &mut wgpu::CommandEncoder, s: &Splat) {
        let aspect = self.config.width as f32 / self.config.height as f32;
        let mut p = self.base();
        p.point_vel = [s.x, s.y, s.dx, s.dy];
        p.color_radius = [s.color[0], s.color[1], s.color[2], s.radius];
        p.misc_a[0] = aspect;
        p.misc_a[1] = s.density;
        p.misc_a[2] = s.temp;
        p.misc_f[0] = 0.0;
        self.blit(encoder, "ps_splat", self.vel.write(), p, &[&self.vel.read().view, &self.dummy.view, &self.dummy.view]);
        self.vel.swap();
        p.misc_f[0] = 1.0;
        self.blit(encoder, "ps_splat", self.dye.write(), p, &[&self.dye.read().view, &self.dummy.view, &self.dummy.view]);
        self.dye.swap();
        p.misc_f[0] = 2.0;
        p.color_radius[3] = s.radius * 1.15;
        self.blit(encoder, "ps_splat", self.temp.write(), p, &[&self.temp.read().view, &self.dummy.view, &self.dummy.view]);
        self.temp.swap();
    }

    fn step(&mut self, encoder: &mut wgpu::CommandEncoder, sim: &Sim, dt: f32, dr: f32) {
        let vtex = [1.0 / self.vel_w as f32, 1.0 / self.vel_h as f32];
        let dtex = [1.0 / self.dye_w as f32, 1.0 / self.dye_h as f32];
        let mut p = self.base();
        p.misc_a[3] = dt;
        p.texel_veltexel = [vtex[0], vtex[1], vtex[0], vtex[1]];

        if sim.buoyancy > 0.001 || sim.gravity > 0.001 {
            p.misc_b[1] = sim.buoyancy;
            p.misc_b[2] = sim.gravity;
            self.blit(encoder, "ps_buoyancy", self.vel.write(), p, &[&self.vel.read().view, &self.dye.read().view, &self.temp.read().view]);
            self.vel.swap();
        }
        if sim.swirl > 0.01 {
            self.blit(encoder, "ps_curl", &self.curl, p, &[&self.vel.read().view, &self.dummy.view, &self.dummy.view]);
            p.misc_b[3] = sim.swirl * (0.12 + 0.88 * dr);
            self.blit(encoder, "ps_vort", self.vel.write(), p, &[&self.vel.read().view, &self.curl.view, &self.dummy.view]);
            self.vel.swap();
        }
        if sim.viscosity > 0.001 {
            p.misc_c[0] = sim.viscosity.min(0.95);
            self.blit(encoder, "ps_diffuse", self.vel.write(), p, &[&self.vel.read().view, &self.dummy.view, &self.dummy.view]);
            self.vel.swap();
        }
        self.blit(encoder, "ps_divergence", &self.div, p, &[&self.vel.read().view, &self.dummy.view, &self.dummy.view]);
        p.misc_c[1] = 0.8;
        self.blit(encoder, "ps_fade", self.pressure.write(), p, &[&self.pressure.read().view, &self.dummy.view, &self.dummy.view]);
        self.pressure.swap();
        let iters = sim.pressure_iters.max(4);
        for _ in 0..iters {
            self.blit(encoder, "ps_jacobi", self.pressure.write(), p, &[&self.pressure.read().view, &self.div.view, &self.dummy.view]);
            self.pressure.swap();
        }
        self.blit(encoder, "ps_gradient", self.vel.write(), p, &[&self.vel.read().view, &self.pressure.read().view, &self.dummy.view]);
        self.vel.swap();

        p.misc_b[0] = dissipate(sim.vel_diss + (1.0 - sim.energy) * 0.32, dt);
        p.srcres_light[0] = self.vel_w as f32;
        p.srcres_light[1] = self.vel_h as f32;
        self.blit(encoder, "ps_advect", self.vel.write(), p, &[&self.vel.read().view, &self.vel.read().view, &self.dummy.view]);
        self.vel.swap();

        p.misc_b[0] = 1.0;
        p.srcres_light[0] = self.dye_w as f32;
        p.srcres_light[1] = self.dye_h as f32;
        p.texel_veltexel = [dtex[0], dtex[1], vtex[0], vtex[1]];
        p.misc_a[3] = dt;
        self.blit(encoder, "ps_advect", &self.hat, p, &[&self.vel.read().view, &self.dye.read().view, &self.dummy.view]);
        p.misc_a[3] = -dt;
        self.blit(encoder, "ps_advect", &self.hathat, p, &[&self.vel.read().view, &self.hat.view, &self.dummy.view]);
        p.misc_a[3] = dt;
        p.misc_b[0] = dissipate(sim.dens_diss, dt);
        p.texel_veltexel = [dtex[0], dtex[1], vtex[0], vtex[1]];
        self.blit(encoder, "ps_mac", self.dye.write(), p, &[&self.dye.read().view, &self.hat.view, &self.hathat.view]);
        self.dye.swap();

        p.misc_b[0] = dissipate(sim.temp_diss, dt);
        p.srcres_light[0] = self.vel_w as f32;
        p.srcres_light[1] = self.vel_h as f32;
        p.texel_veltexel = [vtex[0], vtex[1], vtex[0], vtex[1]];
        self.blit(encoder, "ps_advect", self.temp.write(), p, &[&self.vel.read().view, &self.temp.read().view, &self.dummy.view]);
        self.temp.swap();
    }

    fn display(&mut self, encoder: &mut wgpu::CommandEncoder, sim: &Sim) {
        let mut p = self.base();
        p.texel_veltexel[0] = 1.0 / self.dye_w as f32;
        p.texel_veltexel[1] = 1.0 / self.dye_h as f32;
        p.srcres_light[2] = 0.55;
        p.srcres_light[3] = 0.85;
        p.misc_c[2] = sim.shadow;
        p.misc_c[3] = sim.ambient;
        p.misc_d[0] = sim.incandescence;
        p.misc_d[1] = sim.dropoff;
        p.misc_d[2] = 1.35;
        p.misc_f[0] = match sim.viz {
            Viz::Raw => 1.0,
            Viz::Velocity => 2.0,
            Viz::Pressure => 3.0,
            Viz::Temperature => 6.0,
            Viz::Dye => 0.0,
        };
        p.misc_f[1] = match sim.quality {
            "low" => 8.0,
            "ultra" => 20.0,
            _ => 16.0,
        };
        let t1 = if sim.viz == Viz::Velocity {
            &self.vel.read().view
        } else {
            &self.temp.read().view
        };
        self.blit(
            encoder,
            "ps_display",
            &self.beauty,
            p,
            &[&self.dye.read().view, t1, &self.pressure.read().view],
        );
        if sim.bloom && sim.bloom_amt > 0.01 && sim.viz == Viz::Dye {
            p.misc_e[0] = 0.72;
            self.blit(encoder, "ps_extract", &self.bloom0, p, &[&self.beauty.view, &self.dummy.view, &self.dummy.view]);
            p.texel_veltexel[0] = 1.0 / self.bloom0.w as f32;
            p.texel_veltexel[1] = 1.0 / self.bloom0.h as f32;
            p.misc_e[2] = 1.0;
            p.misc_e[3] = 0.0;
            self.blit(encoder, "ps_blur", &self.bloom1, p, &[&self.bloom0.view, &self.dummy.view, &self.dummy.view]);
            p.misc_e[2] = 0.0;
            p.misc_e[3] = 1.0;
            self.blit(encoder, "ps_blur", &self.bloom0, p, &[&self.bloom1.view, &self.dummy.view, &self.dummy.view]);
        }
    }

    fn base(&self) -> GpuParams {
        let mut p = GpuParams::default();
        p.srcres_light[2] = 0.55;
        p.srcres_light[3] = 0.85;
        p
    }

    fn blit(&self, encoder: &mut wgpu::CommandEncoder, pass: &str, dest: &Rt, params: GpuParams, views: &[&wgpu::TextureView]) {
        self.blit_to(encoder, pass, false, &dest.view, dest.w, dest.h, params, views);
    }

    fn blit_to(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        pass: &str,
        present: bool,
        dest: &wgpu::TextureView,
        w: u32,
        h: u32,
        params: GpuParams,
        views: &[&wgpu::TextureView],
    ) {
        self.queue.write_buffer(&self.ubo, 0, bytemuck::bytes_of(&params));
        let t0 = views[0];
        let t1 = views.get(1).copied().unwrap_or(&self.dummy.view);
        let t2 = views.get(2).copied().unwrap_or(&self.dummy.view);
        let bg = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: None,
            layout: &self.layout,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: self.ubo.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(&self.sampler) },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(t0) },
                wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::TextureView(t1) },
                wgpu::BindGroupEntry { binding: 4, resource: wgpu::BindingResource::TextureView(t2) },
            ],
        });
        let pipe = if present {
            &self.present_pipes[pass]
        } else {
            &self.sim_pipes[pass]
        };
        {
            let mut rp = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some(pass),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: dest,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
            });
            rp.set_pipeline(pipe);
            rp.set_bind_group(0, &bg, &[]);
            rp.set_viewport(0.0, 0.0, w as f32, h as f32, 0.0, 1.0);
            rp.draw(0..3, 0..1);
        }
    }
}

fn dissipate(value: f32, dt: f32) -> f32 {
    (-value.max(0.0) * dt * 2.5).exp()
}

fn preferred_backends() -> wgpu::Backends {
    #[cfg(target_os = "macos")]
    {
        wgpu::Backends::METAL | wgpu::Backends::VULKAN
    }
    #[cfg(not(target_os = "macos"))]
    {
        wgpu::Backends::VULKAN
    }
}

fn tex_entry(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled: false,
        },
        count: None,
    }
}

fn make_pipe(
    device: &wgpu::Device,
    layout: &wgpu::PipelineLayout,
    module: &wgpu::ShaderModule,
    entry: &'static str,
    format: wgpu::TextureFormat,
) -> wgpu::RenderPipeline {
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some(entry),
        layout: Some(layout),
        vertex: wgpu::VertexState {
            module,
            entry_point: Some("vs_main"),
            compilation_options: Default::default(),
            buffers: &[],
        },
        fragment: Some(wgpu::FragmentState {
            module,
            entry_point: Some(entry),
            compilation_options: Default::default(),
            targets: &[Some(wgpu::ColorTargetState {
                format,
                blend: None,
                write_mask: wgpu::ColorWrites::ALL,
            })],
        }),
        primitive: wgpu::PrimitiveState {
            topology: wgpu::PrimitiveTopology::TriangleList,
            ..Default::default()
        },
        depth_stencil: None,
        multisample: wgpu::MultisampleState::default(),
        multiview: None,
        cache: None,
    })
}

fn make_rt(device: &wgpu::Device, w: u32, h: u32, label: &str) -> Rt {
    let tex = device.create_texture(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Rgba16Float,
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
        view_formats: &[],
    });
    Rt {
        view: tex.create_view(&Default::default()),
        _tex: tex,
        w,
        h,
    }
}

fn clear_view(encoder: &mut wgpu::CommandEncoder, view: &wgpu::TextureView) {
    let _pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
        label: Some("clear"),
        color_attachments: &[Some(wgpu::RenderPassColorAttachment {
            view,
            resolve_target: None,
            ops: wgpu::Operations {
                load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                store: wgpu::StoreOp::Store,
            },
        })],
        depth_stencil_attachment: None,
        timestamp_writes: None,
        occlusion_query_set: None,
    });
}

fn make_ping(device: &wgpu::Device, w: u32, h: u32, label: &str) -> Ping {
    Ping {
        a: make_rt(device, w, h, &format!("{label}a")),
        b: make_rt(device, w, h, &format!("{label}b")),
    }
}


