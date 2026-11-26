// The lens math mirrors LiquidGlass.cs, derived from Kyant's AndroidLiquidGlass (Apache-2.0).
// See licenses/AndroidLiquidGlass-LICENSE.txt.
#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace
{
struct Vec3
{
    double x, y, z;
};

Vec3 Normalize(double x, double y, double z)
{
    const double length = std::sqrt(x * x + y * y + z * z);
    return {x / length, y / length, z / length};
}

const auto keyLight = Normalize(-0.3, -0.85, 0.55);
const auto fillLight = Normalize(0.35, 0.8, 0.5);
const auto keyHalf = Normalize(keyLight.x, keyLight.y, keyLight.z + 1);
const auto fillHalf = Normalize(fillLight.x, fillLight.y, fillLight.z + 1);

double RoundedRect(double x, double y, double hx, double hy, double radius)
{
    const double qx = std::abs(x) - (hx - radius);
    const double qy = std::abs(y) - (hy - radius);
    if (qx <= 0 && qy <= 0)
        return -radius + std::max(qx, qy);
    const double ox = std::max(qx, 0.0), oy = std::max(qy, 0.0);
    return std::sqrt(ox * ox + oy * oy) - radius;
}

double Sign(double x)
{
    return (x > 0) - (x < 0);
}

void Gradient(double x, double y, double hx, double hy, double radius, double& gx, double& gy)
{
    const double qx = std::abs(x) - (hx - radius);
    const double qy = std::abs(y) - (hy - radius);
    if (qx >= 0 || qy >= 0)
    {
        const double mx = std::max(qx, 0.0), my = std::max(qy, 0.0);
        const double length = std::sqrt(mx * mx + my * my);
        gx = length < 1e-9 ? 0 : Sign(x) * mx / length;
        gy = length < 1e-9 ? 0 : Sign(y) * my / length;
    }
    else
    {
        const double sx = qx >= qy ? 1 : 0;
        gx = Sign(x) * sx;
        gy = Sign(y) * (1 - sx);
    }
}

void Bevel(double x, double nx, double ny, double glint, double& add, double& mul)
{
    const double s = std::min(1.0, x);
    const double X = nx * s, Y = ny * s, Z = std::sqrt(std::max(0.0, 1 - s * s));
    const double lit = (X * keyLight.x + Y * keyLight.y + Z * keyLight.z) - keyLight.z;
    double spec = 0;
    if (glint != 0)
    {
        const double k = std::max(0.0, X * keyHalf.x + Y * keyHalf.y + Z * keyHalf.z);
        const double f = std::max(0.0, X * fillHalf.x + Y * fillHalf.y + Z * fillHalf.z);
        spec = (std::pow(k, 60) * 0.7 + std::pow(f, 40) * 0.3) * glint;
    }
    const double fresnel = std::pow(s, 6) * 0.22;
    add = (std::max(0.0, lit) * 0.45 + spec + fresnel) * 255;
    mul = 1 - std::max(0.0, -lit) * 0.45;
}

double Mirror(double value, int size)
{
    if (size <= 1)
        return 0.5;
    const double period = 2.0 * size;
    value = std::fmod(value, period);
    if (value < 0)
        value += period;
    return value < size ? value : period - value;
}

// Calculate the bilinear weights and offsets once for the channels sharing a sample.
struct Sample
{
    const std::uint8_t* src;
    std::size_t a, b, d, e;
    double fx, fy;

    Sample(const std::uint8_t* pixels, int width, int height, double x, double y) : src(pixels)
    {
        if (x < 0 || x > width)
            x = Mirror(x, width);
        if (y < 0 || y > height)
            y = Mirror(y, height);
        x -= 0.5;
        y -= 0.5;
        int x0 = static_cast<int>(std::floor(x)), y0 = static_cast<int>(std::floor(y));
        fx = x - x0;
        fy = y - y0;
        const int x1 = std::clamp(x0 + 1, 0, width - 1);
        const int y1 = std::clamp(y0 + 1, 0, height - 1);
        x0 = std::clamp(x0, 0, width - 1);
        y0 = std::clamp(y0, 0, height - 1);
        const auto top = static_cast<std::size_t>(y0) * width;
        const auto bottom = static_cast<std::size_t>(y1) * width;
        a = (top + x0) * 4;
        b = (top + x1) * 4;
        d = (bottom + x0) * 4;
        e = (bottom + x1) * 4;
    }

    double Channel(int c) const
    {
        const double first = src[a + c], last = src[d + c];
        const double top = first + (src[b + c] - first) * fx;
        const double bottom = last + (src[e + c] - last) * fx;
        return top + (bottom - top) * fy;
    }
};

std::uint8_t ColorByte(double v)
{
    return static_cast<std::uint8_t>(v < 0 ? 0 : v > 255 ? 255 : v + 0.5);
}

struct LensOptions
{
    double x, y, width, height, radius, band, amount, depth, saturation, bevel, glint, falloff;
    int dispersion;
};

void LensRow(const std::uint8_t* src, std::uint8_t* dst, int sw, int sh, int w,
             const LensOptions& lens, int row)
{
    const double hx = lens.width / 2, hy = lens.height / 2;
    const double cx = lens.x + hx, cy = lens.y + hy;
    const double py = lens.y + row + 0.5, uy = py - cy;
    const double radius = std::clamp(lens.radius, 0.0, std::min(hx, hy));
    const double gradRadius = std::min(radius * 1.5, std::min(hx, hy));
    const double height = std::max(0.5, lens.band);
    auto* out = dst + static_cast<std::size_t>(row) * w * 4;
    for (int i = 0; i < w; ++i, out += 4)
    {
        const double px = lens.x + i + 0.5, ux = px - cx;
        const double sd = RoundedRect(ux, uy, hx, hy, radius);
        double sx = px, sy = py, dx = 0, dy = 0, rimX = 0, nx = 0, ny = 0;
        if (-sd < height)
        {
            const double x = 1 + std::min(sd, 0.0) / height;
            const double d = lens.falloff > 0 ? std::pow(x, lens.falloff) * lens.amount
                : (0.6 * (1 - std::sqrt(std::max(0.0, 1 - x * x))) + 0.4 * x * x) * lens.amount;
            double gx, gy;
            Gradient(ux, uy, hx, hy, gradRadius, gx, gy);
            rimX = x;
            nx = gx;
            ny = gy;
            if (lens.depth != 0)
            {
                const double length = std::sqrt(ux * ux + uy * uy);
                if (length > 1e-6)
                {
                    gx += lens.depth * ux / length;
                    gy += lens.depth * uy / length;
                }
            }
            const double gl = std::sqrt(gx * gx + gy * gy);
            if (gl > 1e-6)
            {
                gx /= gl;
                gy /= gl;
            }
            sx = px + d * gx;
            sy = py + d * gy;
            if (lens.dispersion)
            {
                const double k = (ux * uy) / (hx * hy);
                dx = d * gx * k;
                dy = d * gy * k;
            }
        }
        double blue, green, red;
        const Sample center(src, sw, sh, sx, sy);
        if (dx != 0 || dy != 0)
        {
            const Sample redSample(src, sw, sh, sx + dx, sy + dy);
            const Sample orange(src, sw, sh, sx + dx * 2 / 3, sy + dy * 2 / 3);
            const Sample yellow(src, sw, sh, sx + dx / 3, sy + dy / 3);
            const Sample cyan(src, sw, sh, sx - dx / 3, sy - dy / 3);
            const Sample blueSample(src, sw, sh, sx - dx * 2 / 3, sy - dy * 2 / 3);
            const Sample purple(src, sw, sh, sx - dx, sy - dy);
            red = redSample.Channel(2) / 3.5 + orange.Channel(2) / 3.5
                + yellow.Channel(2) / 3.5 + purple.Channel(2) / 7.0;
            green = orange.Channel(1) / 7.0 + yellow.Channel(1) / 3.5
                + center.Channel(1) / 3.5 + cyan.Channel(1) / 3.5;
            blue = cyan.Channel(0) / 3.0 + blueSample.Channel(0) / 3.0 + purple.Channel(0) / 3.0;
        }
        else
        {
            blue = center.Channel(0);
            green = center.Channel(1);
            red = center.Channel(2);
        }
        const double lum = 0.213 * red + 0.715 * green + 0.072 * blue;
        double channels[] = {blue, green, red};
        double add = 0, mul = 1;
        const bool lit = lens.bevel > 0 && rimX > 0;
        if (lit)
        {
            Bevel(rimX, nx, ny, lens.glint, add, mul);
            add *= lens.bevel;
            mul = 1 - (1 - mul) * lens.bevel;
        }
        for (int c = 0; c < 3; ++c)
        {
            double value = lum + (channels[c] - lum) * lens.saturation;
            if (lit)
            {
                value *= mul;
                value += add * (1 - std::min(255.0, value) / 255);
            }
            out[c] = ColorByte(value);
        }
        out[3] = 255;
    }
}

void SmearRow(const std::uint8_t* src, std::uint8_t* dst, int w, int h, int y,
              double radius, double band, int blur)
{
    const int reach = static_cast<int>(std::ceil(band)) + 1;
    const double hx = w / 2.0, hy = h / 2.0;
    const bool full = y < radius + reach || y >= h - radius - reach;
    const int count = (2 * blur + 1) * (2 * blur + 1);
    const auto stride = static_cast<std::size_t>(w) * 4;
    auto* out = dst + y * stride;
    const auto* in = src + y * stride;

    for (int x = 0; x < w; ++x)
    {
        if (!full && x >= reach && x < w - reach)
        {
            x = w - reach - 1;
            continue;
        }
        const double inside = -RoundedRect(x + 0.5 - hx, y + 0.5 - hy, hx, hy, radius);
        if (inside >= band)
            continue;
        double k = std::min(1.0, 1 - inside / band);
        k = k * k * (3 - 2 * k);
        int sum[4] = {};
        for (int dy = -blur; dy <= blur; ++dy)
        {
            const auto* row = src + std::clamp(y + dy, 0, h - 1) * stride;
            if (x >= blur && x < w - blur)
            {
                const auto* pixel = row + static_cast<std::size_t>(x - blur) * 4;
                for (int dx = -blur; dx <= blur; ++dx, pixel += 4)
                    for (int c = 0; c < 4; ++c)
                        sum[c] += pixel[c];
            }
            else
            {
                for (int dx = -blur; dx <= blur; ++dx)
                {
                    const auto* pixel = row + static_cast<std::size_t>(std::clamp(x + dx, 0, w - 1)) * 4;
                    for (int c = 0; c < 4; ++c)
                        sum[c] += pixel[c];
                }
            }
        }
        const auto index = static_cast<std::size_t>(x) * 4;
        for (int c = 0; c < 4; ++c)
            out[index + c] = static_cast<std::uint8_t>(in[index + c] + (sum[c] / count - in[index + c]) * k);
    }
}
}

extern "C" __declspec(dllexport) void GlassLensRows(const std::uint8_t* src, std::uint8_t* dst,
    int sw, int sh, int w, const LensOptions* lens, int begin, int end)
{
    for (int y = begin; y < end; ++y)
        LensRow(src, dst, sw, sh, w, *lens, y);
}

extern "C" __declspec(dllexport) void GlassSmearRows(const std::uint8_t* src, std::uint8_t* dst,
    int w, int h, double radius, double band, int blur, int begin, int end)
{
    for (int y = begin; y < end; ++y)
        SmearRow(src, dst, w, h, y, radius, band, blur);
}
