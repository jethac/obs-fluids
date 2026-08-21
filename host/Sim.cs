using System.Numerics;
using System.Text.Json;

namespace InkContainer;

enum Viz { Dye, Raw, Velocity, Pressure, Temperature }

sealed class Splat
{
    public float X, Y, Dx, Dy, Density, Temp, Radius;
    public Vector3 Color;
}

sealed class Ghost
{
    public float X, Y, Tx, Ty, Vx, Vy, Speed, Radius;
    public Vector3 Color;
}

sealed class Sim
{
    public string Preset = "demo";
    public string Quality = "high";
    public bool Bloom = true;
    public bool Attract = true;
    public bool Paused;
    public float Energy = 0.5f;
    public float Viscosity;
    public float Swirl = 28;
    public int PressureIters = 24;
    public float Timestep = 1;
    public float VelDiss = 0.22f;
    public float DensDiss = 0.2f;
    public float TempDiss = 0.45f;
    public float Buoyancy = 0.55f;
    public float Gravity = 0.08f;
    public float Shadow = 1.35f;
    public float Ambient = 0.16f;
    public float Incandescence = 0.55f;
    public float BloomAmt = 0.26f;
    public float Dropoff = 0.07f;
    public float SplatForce = 6400;
    public float SplatRadius = 0.28f;
    public float DensityAmt = 0.85f;
    public float TempAmt = 0.35f;
    public float AttractForce = 1;
    public Vector3 Color = new(0.24f, 0.94f, 0.91f);
    public Viz Viz = Viz.Dye;
    public string EnergySrc = "dial";

    public readonly List<Splat> Splats = [];
    readonly List<Ghost> _ghosts = [];
    readonly Random _rng = new();

    static readonly Vector3[] Dye = [
        new(0.24f, 0.94f, 0.91f),
        new(1.00f, 0.60f, 0.24f),
        new(1.00f, 0.31f, 0.55f),
        new(1.00f, 0.42f, 0.16f),
        new(0.35f, 0.66f, 1.00f),
        new(0.91f, 0.77f, 0.42f)
    ];

    public void ApplyPreset(string name)
    {
        Preset = name;
        switch (name)
        {
            case "ink":
                Viscosity = 0.04f; Swirl = 18; PressureIters = 22; Timestep = 1;
                VelDiss = 0.18f; DensDiss = 0.04f; TempDiss = 0.6f;
                Buoyancy = 0.08f; Gravity = 0.02f;
                Shadow = 0.85f; Ambient = 0.18f; Incandescence = 0.12f; BloomAmt = 0.16f; Dropoff = 0.04f;
                SplatForce = 5200; SplatRadius = 0.24f; DensityAmt = 1; TempAmt = 0.05f; AttractForce = 0.85f;
                Color = Dye[0];
                break;
            case "cloud":
                Viscosity = 0.08f; Swirl = 12; PressureIters = 20;
                VelDiss = 0.28f; DensDiss = 0.22f; TempDiss = 0.35f;
                Buoyancy = 1.4f; Gravity = 0.15f;
                Shadow = 2.2f; Ambient = 0.18f; Incandescence = 0; BloomAmt = 0.08f; Dropoff = 0.1f;
                SplatForce = 3800; SplatRadius = 0.4f; DensityAmt = 0.55f; TempAmt = 0.7f; AttractForce = 0.5f;
                Color = new(0.85f, 0.82f, 0.77f);
                break;
            case "fire":
                Viscosity = 0; Swirl = 16; PressureIters = 20;
                VelDiss = 0.38f; DensDiss = 0.72f; TempDiss = 0.55f;
                Buoyancy = 0.9f; Gravity = 0.04f;
                Shadow = 1.1f; Ambient = 0.08f; Incandescence = 1.15f; BloomAmt = 0.55f; Dropoff = 0.06f;
                SplatForce = 4200; SplatRadius = 0.22f; DensityAmt = 0.45f; TempAmt = 0.8f; AttractForce = 0.85f;
                Color = new(1f, 0.42f, 0.16f);
                break;
            case "fog":
                Viscosity = 0.18f; Swirl = 6; PressureIters = 16; Timestep = 0.9f;
                VelDiss = 0.42f; DensDiss = 0.18f; TempDiss = 0.4f;
                Buoyancy = 0.35f; Gravity = 0;
                Shadow = 1.6f; Ambient = 0.32f; Incandescence = 0; BloomAmt = 0.05f; Dropoff = 0.16f;
                SplatForce = 2400; SplatRadius = 0.5f; DensityAmt = 0.35f; TempAmt = 0.2f; AttractForce = 0.4f;
                Color = new(0.78f, 0.82f, 0.83f);
                break;
            default:
                Preset = "demo";
                Viscosity = 0; Swirl = 28; PressureIters = 24; Timestep = 1;
                VelDiss = 0.22f; DensDiss = 0.2f; TempDiss = 0.45f;
                Buoyancy = 0.55f; Gravity = 0.08f;
                Shadow = 1.35f; Ambient = 0.16f; Incandescence = 0.55f; BloomAmt = 0.26f; Dropoff = 0.07f;
                SplatForce = 6400; SplatRadius = 0.28f; DensityAmt = 0.85f; TempAmt = 0.35f; AttractForce = 1;
                Color = Dye[0];
                break;
        }
        ResetGhosts();
    }

    public float Drive()
    {
        var x = Math.Clamp(Energy, 0, 1);
        return x <= 0.5f ? 0.04f + 0.96f * (x / 0.5f) : 1 + (x - 0.5f) * 2f;
    }

    public void SetEnergy(float v, string src)
    {
        var prev = Energy;
        Energy = Math.Clamp(v, 0, 1);
        EnergySrc = src;
        var de = Energy - prev;
        if (de > 0.07f && src != "panel")
            Kick(de);
    }

    public void NudgeEnergy(float steps, string src) => SetEnergy(Energy + steps * 0.018f, src);

    public void ApplyEnergyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("delta", out var d))
                NudgeEnergy(d.GetInt32(), r.TryGetProperty("src", out var s) ? s.GetString() ?? "host" : "host");
            if (r.TryGetProperty("energy", out var e))
                SetEnergy(e.GetSingle(), "host");
        }
        catch { }
    }

    public void QueueSplat(float x, float y, float dx, float dy, Vector3 color, float density, float temp, float radius)
    {
        Splats.Add(new Splat { X = x, Y = y, Dx = dx, Dy = dy, Color = color, Density = density, Temp = temp, Radius = Math.Max(0.0002f, radius * radius) });
    }

    public void PointerSplat(float x, float y, float dx, float dy)
    {
        var f = SplatForce * (0.35f + 0.65f * Drive());
        QueueSplat(x, y, dx * f, dy * f, Color, DensityAmt, TempAmt, SplatRadius);
    }

    public void Seed()
    {
        if (Preset == "fire")
        {
            for (var i = 0; i < 3; i++)
                QueueSplat(0.3f + i * 0.2f, 0.1f, 0, 0.06f * SplatForce, new(1f, 0.42f, 0.16f), 0.22f, 0.8f, 0.12f);
            return;
        }
        var pal = Palette();
        for (var i = 0; i < 6; i++)
        {
            var ang = Rand(0, MathF.PI * 2);
            var mag = Rand(0.08f, 0.18f);
            QueueSplat(Rand(0.18f, 0.82f), Rand(0.18f, 0.82f),
                MathF.Cos(ang) * mag * SplatForce, MathF.Sin(ang) * mag * SplatForce,
                pal[i % pal.Length], DensityAmt * 0.7f, 0.15f, Rand(0.1f, 0.18f));
        }
    }

    public void ResetGhosts()
    {
        _ghosts.Clear();
        if (Preset == "fire") return;
        var pal = Palette();
        for (var i = 0; i < 2; i++)
        {
            _ghosts.Add(new Ghost
            {
                X = Rand(0.2f, 0.8f), Y = Rand(0.2f, 0.8f),
                Tx = Rand(0.15f, 0.85f), Ty = Rand(0.15f, 0.85f),
                Speed = Rand(0.35f, 0.9f), Radius = Rand(0.11f, 0.2f),
                Color = pal[i % pal.Length]
            });
        }
    }

    public void TickAttract(float dt, float time)
    {
        if (!Attract) return;
        var k = AttractForce * Drive();
        var inj = 0.2f + 0.8f * Drive();
        if (Preset == "fire")
        {
            for (var i = 0; i < 3; i++)
            {
                var x = 0.28f + i * 0.22f + 0.03f * MathF.Sin(time * 0.7f + i * 1.7f);
                var y = 0.08f + 0.015f * MathF.Sin(time * 1.3f + i);
                var col = i % 2 == 0 ? new Vector3(1f, 0.69f, 0.23f) : new Vector3(1f, 0.42f, 0.16f);
                QueueSplat(x, y, MathF.Sin(time * 2 + i) * 0.003f * SplatForce, 0.02f * k * SplatForce, col, 0.03f * inj, 0.45f * inj, 0.13f);
            }
            return;
        }
        if (Preset is "cloud" or "fog")
        {
            var col = new Vector3(0.85f, 0.82f, 0.77f);
            for (var i = 0; i < 3; i++)
            {
                var x = 0.3f + i * 0.2f + 0.05f * MathF.Sin(time * 0.25f + i);
                var y = 0.12f + 0.03f * MathF.Sin(time * 0.4f + i * 2);
                QueueSplat(x, y, 0.002f * SplatForce, 0.02f * k * SplatForce, col, DensityAmt * 0.12f * inj, 0.45f * inj, 0.42f);
            }
            return;
        }
        var pal = Palette();
        foreach (var g in _ghosts)
        {
            var ax = g.Tx - g.X;
            var ay = g.Ty - g.Y;
            if (ax * ax + ay * ay < 0.0025f)
            {
                g.Tx = Rand(0.12f, 0.88f);
                g.Ty = Rand(0.12f, 0.88f);
                if (_rng.NextDouble() < 0.12)
                    g.Color = pal[_rng.Next(pal.Length)];
            }
            var dr = Drive();
            g.Vx += ax * g.Speed * dt * 2.5f * dr;
            g.Vy += ay * g.Speed * dt * 2.5f * dr;
            g.Vx *= 0.92f; g.Vy *= 0.92f;
            var px = g.X; var py = g.Y;
            g.X += g.Vx * dt * 1.8f;
            g.Y += g.Vy * dt * 1.8f;
            QueueSplat(g.X, g.Y, (g.X - px) * SplatForce * k, (g.Y - py) * SplatForce * k, g.Color, DensityAmt * 0.1f * k * inj, TempAmt * 0.25f * inj, g.Radius);
        }
    }

    void Kick(float de)
    {
        var pal = Palette();
        var n = 2 + (int)(de * 8);
        for (var i = 0; i < n; i++)
        {
            var ang = Rand(0, MathF.PI * 2);
            var mag = (0.04f + de) * SplatForce;
            QueueSplat(Rand(0.25f, 0.75f), Rand(0.25f, 0.75f), MathF.Cos(ang) * mag, MathF.Sin(ang) * mag, pal[i % pal.Length], DensityAmt * 0.25f * de, 0.2f, 0.14f);
        }
    }

    Vector3[] Palette() => Preset switch
    {
        "fire" => [new(0.16f, 0.09f, 0.06f), new(1f, 0.42f, 0.16f), new(1f, 0.69f, 0.23f)],
        "cloud" or "fog" => [new(0.85f, 0.82f, 0.77f), new(0.66f, 0.71f, 0.75f)],
        "ink" => [Dye[0], Dye[2]],
        _ => [Dye[0], Dye[1]]
    };

    float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    public static int SimShort(string quality) => quality switch
    {
        "low" => 128,
        "medium" => 192,
        "ultra" => 384,
        _ => 256
    };
}
