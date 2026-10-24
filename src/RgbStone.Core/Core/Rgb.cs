using System;

namespace RgbStone.Core
{
    public struct Rgb : IEquatable<Rgb>
    {
        public byte R, G, B;

        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public Rgb(int r, int g, int b) { R = Clamp(r); G = Clamp(g); B = Clamp(b); }

        public static readonly Rgb Black = new Rgb(0, 0, 0);

        public static Rgb FromHsv(double hue, double sat = 1, double val = 1)
        {
            hue = hue - Math.Floor(hue); // wrap to 0..1
            double h = hue * 6, f = h - Math.Floor(h);
            double p = val * (1 - sat), q = val * (1 - sat * f), t = val * (1 - sat * (1 - f));
            double r, g, b;
            switch ((int)h % 6)
            {
                case 0: r = val; g = t; b = p; break;
                case 1: r = q; g = val; b = p; break;
                case 2: r = p; g = val; b = t; break;
                case 3: r = p; g = q; b = val; break;
                case 4: r = t; g = p; b = val; break;
                default: r = val; g = p; b = q; break;
            }
            return new Rgb((int)(r * 255 + 0.5), (int)(g * 255 + 0.5), (int)(b * 255 + 0.5));
        }

        public static Rgb Lerp(Rgb a, Rgb b, double t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return new Rgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public Rgb Scale(double k) => new Rgb((int)(R * k + 0.5), (int)(G * k + 0.5), (int)(B * k + 0.5));

        public static Rgb Parse(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Black;
            hex = hex.TrimStart('#');
            if (hex.Length != 6) return Black;
            int v = Convert.ToInt32(hex, 16);
            return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        public override string ToString() => "#" + R.ToString("X2") + G.ToString("X2") + B.ToString("X2");

        public bool Equals(Rgb o) => R == o.R && G == o.G && B == o.B;
        public override bool Equals(object obj) => obj is Rgb o && Equals(o);
        public override int GetHashCode() => (R << 16) | (G << 8) | B;
        public static bool operator ==(Rgb a, Rgb b) => a.Equals(b);
        public static bool operator !=(Rgb a, Rgb b) => !a.Equals(b);

        static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
}
