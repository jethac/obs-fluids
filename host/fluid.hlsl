cbuffer Params : register(b0)
{
    float uPointX, uPointY, uVelX, uVelY;
    float uColorR, uColorG, uColorB, uRadius;
    float uTexelX, uTexelY, uVelTexelX, uVelTexelY;
    float uSrcResX, uSrcResY, uLightX, uLightY;
    float uAspect, uDensity, uTemp, uDt;
    float uDiss, uBuoy, uGrav, uSwirl;
    float uMix, uValue, uExtinction, uAmbient;
    float uIncand, uDropoff, uExposure, uAlphaMode;
    float uThresh, uBloomAmt, uDirX, uDirY;
    float uMode, uShadowSteps, uFlipY, uPad;
};

Texture2D<float4> t0 : register(t0);
Texture2D<float4> t1 : register(t1);
Texture2D<float4> t2 : register(t2);
SamplerState s0 : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * 2.0 - 1.0, 0, 1);
    return o;
}

float2 UV(VSOut i)
{
    float2 uv = i.uv;
    if (uFlipY > 0.5)
        uv.y = 1.0 - uv.y;
    return uv;
}

float4 Bilerp(Texture2D<float4> tex, float2 uv, float2 res)
{
    float2 px = uv * res - 0.5;
    float2 f = frac(px);
    float2 p0 = (floor(px) + 0.5) / res;
    float2 t = 1.0 / res;
    float4 a = tex.SampleLevel(s0, p0, 0);
    float4 b = tex.SampleLevel(s0, p0 + float2(t.x, 0), 0);
    float4 c = tex.SampleLevel(s0, p0 + float2(0, t.y), 0);
    float4 d = tex.SampleLevel(s0, p0 + t, 0);
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float4 PSSplat(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float2 p = uv - float2(uPointX, uPointY);
    p.x *= uAspect;
    float g = exp(-dot(p, p) / max(uRadius, 1e-6));
    float4 base = t0.SampleLevel(s0, uv, 0);
    int mode = (int)uMode;
    if (mode == 0)
        return base + float4(float2(uVelX, uVelY) * g, 0, 0);
    if (mode == 1)
    {
        float room = 1.0 - smoothstep(0.9, 2.4, base.a);
        return base + float4(float3(uColorR, uColorG, uColorB) * g * 1.7, g * uDensity) * room;
    }
    float roomT = 1.0 - smoothstep(0.9, 2.4, base.r);
    return base + float4(uTemp * g, 0, 0, 0) * roomT;
}

float4 PSAdvect(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float2 vel = t0.SampleLevel(s0, uv, 0).xy;
    float2 coord = uv - uDt * vel * float2(uVelTexelX, uVelTexelY);
    coord = clamp(coord, float2(uTexelX, uTexelY), 1.0 - float2(uTexelX, uTexelY));
    return Bilerp(t1, coord, float2(uSrcResX, uSrcResY)) * uDiss;
}

float4 PSMac(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float4 phi = t0.SampleLevel(s0, uv, 0);
    float4 hat = t1.SampleLevel(s0, uv, 0);
    float4 hathat = t2.SampleLevel(s0, uv, 0);
    float4 n = hat + 0.5 * (phi - hathat);
    float2 t = float2(uTexelX, uTexelY);
    float4 c0 = t1.SampleLevel(s0, uv + float2(-t.x, 0), 0);
    float4 c1 = t1.SampleLevel(s0, uv + float2(t.x, 0), 0);
    float4 c2 = t1.SampleLevel(s0, uv + float2(0, -t.y), 0);
    float4 c3 = t1.SampleLevel(s0, uv + float2(0, t.y), 0);
    float4 mn = min(hat, min(c0, min(c1, min(c2, c3))));
    float4 mx = max(hat, max(c0, max(c1, max(c2, c3))));
    n = clamp(n, mn, mx);
    return max(n * uDiss, 0.0);
}

float4 PSBuoyancy(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float2 vel = t0.SampleLevel(s0, uv, 0).xy;
    float d = t1.SampleLevel(s0, uv, 0).a;
    float temp = t2.SampleLevel(s0, uv, 0).r;
    float2 accel = float2(0, (uBuoy * temp - uGrav * d) * 0.25);
    vel += accel * uDt / max(uTexelY, 1e-6);
    return float4(vel, 0, 1);
}

float4 PSCurl(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float2 t = float2(uTexelX, uTexelY);
    float2 L = t0.SampleLevel(s0, uv - float2(t.x, 0), 0).xy;
    float2 R = t0.SampleLevel(s0, uv + float2(t.x, 0), 0).xy;
    float2 B = t0.SampleLevel(s0, uv - float2(0, t.y), 0).xy;
    float2 T = t0.SampleLevel(s0, uv + float2(0, t.y), 0).xy;
    float curl = R.y - L.y - T.x + B.x;
    return float4(0.5 * curl, 0, 0, 1);
}

float4 PSVort(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float2 t = float2(uTexelX, uTexelY);
    float L = t1.SampleLevel(s0, uv - float2(t.x, 0), 0).r;
    float R = t1.SampleLevel(s0, uv + float2(t.x, 0), 0).r;
    float B = t1.SampleLevel(s0, uv - float2(0, t.y), 0).r;
    float T = t1.SampleLevel(s0, uv + float2(0, t.y), 0).r;
    float C = t1.SampleLevel(s0, uv, 0).r;
    float2 force = 0.5 * float2(abs(T) - abs(B), abs(R) - abs(L));
    force = force / (length(force) + 1e-4) * uSwirl * C;
    force.y *= -1;
    float2 vel = t0.SampleLevel(s0, uv, 0).xy;
    return float4(vel + force * uDt, 0, 1);
}

float4 PSDivergence(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float2 t = float2(uTexelX, uTexelY);
    float2 vL = t0.SampleLevel(s0, uv - float2(t.x, 0), 0).xy;
    float2 vR = t0.SampleLevel(s0, uv + float2(t.x, 0), 0).xy;
    float2 vB = t0.SampleLevel(s0, uv - float2(0, t.y), 0).xy;
    float2 vT = t0.SampleLevel(s0, uv + float2(0, t.y), 0).xy;
    float2 vC = t0.SampleLevel(s0, uv, 0).xy;
    if (uv.x - t.x < 0) vL.x = -vC.x;
    if (uv.x + t.x > 1) vR.x = -vC.x;
    if (uv.y - t.y < 0) vB.y = -vC.y;
    if (uv.y + t.y > 1) vT.y = -vC.y;
    float div = 0.5 * (vR.x - vL.x + vT.y - vB.y);
    return float4(div, 0, 0, 1);
}

float4 PSJacobi(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float2 t = float2(uTexelX, uTexelY);
    float L = t0.SampleLevel(s0, uv - float2(t.x, 0), 0).r;
    float R = t0.SampleLevel(s0, uv + float2(t.x, 0), 0).r;
    float B = t0.SampleLevel(s0, uv - float2(0, t.y), 0).r;
    float T = t0.SampleLevel(s0, uv + float2(0, t.y), 0).r;
    float div = t1.SampleLevel(s0, uv, 0).r;
    float p = (L + R + B + T - div) * 0.25;
    return float4(p, 0, 0, 1);
}

float4 PSGradient(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float2 t = float2(uTexelX, uTexelY);
    float L = t1.SampleLevel(s0, uv - float2(t.x, 0), 0).r;
    float R = t1.SampleLevel(s0, uv + float2(t.x, 0), 0).r;
    float B = t1.SampleLevel(s0, uv - float2(0, t.y), 0).r;
    float T = t1.SampleLevel(s0, uv + float2(0, t.y), 0).r;
    float2 vel = t0.SampleLevel(s0, uv, 0).xy;
    vel -= float2(R - L, T - B) * 0.5;
    if (uv.x < t.x) vel.x = 0;
    if (uv.x > 1 - t.x) vel.x = 0;
    if (uv.y < t.y) vel.y = 0;
    if (uv.y > 1 - t.y) vel.y = 0;
    return float4(vel, 0, 1);
}

float4 PSDiffuse(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float2 t = float2(uTexelX, uTexelY);
    float4 c = t0.SampleLevel(s0, uv, 0);
    float4 L = t0.SampleLevel(s0, uv - float2(t.x, 0), 0);
    float4 R = t0.SampleLevel(s0, uv + float2(t.x, 0), 0);
    float4 B = t0.SampleLevel(s0, uv - float2(0, t.y), 0);
    float4 T = t0.SampleLevel(s0, uv + float2(0, t.y), 0);
    return lerp(c, (L + R + B + T) * 0.25, uMix);
}

float4 PSFade(VSOut i) : SV_Target
{
    return t0.SampleLevel(s0, UV(i), 0) * uValue;
}

float4 PSClear(VSOut i) : SV_Target
{
    return float4(uColorR, uColorG, uColorB, uRadius);
}

float3 FireRamp(float t)
{
    t = saturate(t / 1.5);
    float3 c = 0;
    c = lerp(c, float3(0.40, 0.02, 0.00), smoothstep(0.00, 0.18, t));
    c = lerp(c, float3(0.92, 0.18, 0.01), smoothstep(0.18, 0.40, t));
    c = lerp(c, float3(1.00, 0.55, 0.06), smoothstep(0.40, 0.68, t));
    c = lerp(c, float3(1.00, 0.90, 0.45), smoothstep(0.68, 0.90, t));
    c = lerp(c, float3(1.00, 0.98, 0.92), smoothstep(0.90, 1.00, t));
    return c;
}

float Drop(float2 uv)
{
    if (uDropoff <= 0)
        return 1;
    float2 e = abs(uv * 2 - 1);
    float b = max(e.x, e.y);
    return 1 - smoothstep(1 - uDropoff, 1, b);
}

float Shadow(float2 uv)
{
    float2 dir = normalize(float2(uLightX, uLightY) + 1e-5) * float2(uTexelX, uTexelY) * 3;
    float t = 1;
    float2 p = uv;
    int steps = (int)uShadowSteps;
    [loop]
    for (int i = 0; i < 24; i++)
    {
        if (i >= steps)
            break;
        p -= dir;
        t *= exp(-t0.SampleLevel(s0, p, 0).a * uExtinction * 0.18);
    }
    return t;
}

float4 PSDisplay(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float4 dye = t0.SampleLevel(s0, uv, 0);
    float d = dye.a * Drop(uv);
    int mode = (int)uMode;
    if (mode == 1)
    {
        float3 col = dye.rgb / (1 + dye.rgb);
        float a = d / (d + 0.55);
        return float4(col * a, uAlphaMode > 0.5 ? a : 1);
    }
    if (mode == 2)
    {
        float2 v = t1.SampleLevel(s0, uv, 0).xy;
        return float4(v.x * 4 + 0.5, v.y * 4 + 0.5, length(v) * 8, 1);
    }
    if (mode == 3)
    {
        float p = t2.SampleLevel(s0, uv, 0).r;
        return float4(max(p, 0) * 4 + 0.05, 0.12, max(-p, 0) * 4 + 0.05, 1);
    }
    if (mode == 6)
    {
        return float4(FireRamp(t1.SampleLevel(s0, uv, 0).r), 1);
    }
    float sh = Shadow(uv);
    float temp = t1.SampleLevel(s0, uv, 0).r * Drop(uv);
    float3 col = dye.rgb * (0.22 + uAmbient + sh * 0.82);
    float luma = dot(col, float3(0.22, 0.72, 0.06));
    col = lerp(luma.xxx, col, 1.55);
    col += FireRamp(temp) * uIncand * temp;
    col *= uExposure;
    float alpha = 1 - exp(-d * 1.7);
    float3 mapped = col / (1 + col);
    mapped = pow(max(mapped, 0), 0.9);
    mapped *= alpha;
    return float4(mapped, uAlphaMode > 0.5 ? alpha : 1);
}

float4 PSExtract(VSOut i) : SV_Target
{
    float4 c = t0.SampleLevel(s0, UV(i), 0);
    float l = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
    float k = smoothstep(uThresh, uThresh + 0.35, l);
    return float4(c.rgb * k, c.a);
}

float4 PSBlur(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float2 t = float2(uDirX, uDirY) * float2(uTexelX, uTexelY);
    float3 s = t0.SampleLevel(s0, uv, 0).rgb * 0.227027;
    s += t0.SampleLevel(s0, uv + t, 0).rgb * 0.1945946;
    s += t0.SampleLevel(s0, uv - t, 0).rgb * 0.1945946;
    s += t0.SampleLevel(s0, uv + 2 * t, 0).rgb * 0.1216216;
    s += t0.SampleLevel(s0, uv - 2 * t, 0).rgb * 0.1216216;
    s += t0.SampleLevel(s0, uv + 3 * t, 0).rgb * 0.054054;
    s += t0.SampleLevel(s0, uv - 3 * t, 0).rgb * 0.054054;
    return float4(s, 1);
}

float4 PSComposite(VSOut i) : SV_Target
{
    float2 uv = UV(i);
    float4 b = t0.SampleLevel(s0, uv, 0);
    float3 bloom = t1.SampleLevel(s0, uv, 0).rgb;
    float3 col = b.rgb + bloom * uBloomAmt;
    return float4(col, uAlphaMode > 0.5 ? b.a : 1);
}
