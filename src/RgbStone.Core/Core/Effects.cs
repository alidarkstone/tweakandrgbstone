using System;
using System.Collections.Generic;

namespace RgbStone.Core
{
    public sealed class EffectSettings
    {
        public Rgb Color1 = new Rgb(0, 155, 222);
        public Rgb Color2 = new Rgb(255, 0, 140);
        public double Speed = 1.0;      // 0.1 .. 5
        public double Brightness = 1.0; // 0 .. 1
    }

    /// <summary>
    /// An effect turns (time, LED position) into a color. Every device evaluates the same function with the same clock,
    /// which is what keeps all hardware in sync.
    /// </summary>
    public abstract class Effect
    {
        public abstract string Id { get; }
        public abstract string DisplayName { get; }
        /// <summary>pos: LED position 0..1 inside its zone; t: seconds since start.</summary>
        public abstract Rgb ColorAt(double pos, double t, EffectSettings s);
        /// <summary>Same as <see cref="ColorAt"/>, for effects that also need the LED's index in its zone.</summary>
        public virtual Rgb ColorAtLed(int index, double pos, double t, EffectSettings s) => ColorAt(pos, t, s);

        public static readonly IReadOnlyList<Effect> All = new Effect[]
        {
            new StaticEffect(), new BreathingEffect(), new ColorCycleEffect(), new RainbowWaveEffect(),
            new GradientWaveEffect(), new CometEffect(), new OffEffect(), new SegmentTestEffect(),
        };

        public static Effect ById(string id)
        {
            foreach (var e in All) if (e.Id == id) return e;
            return All[0];
        }
    }

    sealed class StaticEffect : Effect
    {
        public override string Id => "static";
        public override string DisplayName => "Sabit renk";
        public override Rgb ColorAt(double pos, double t, EffectSettings s) => s.Color1;
    }

    sealed class BreathingEffect : Effect
    {
        public override string Id => "breathing";
        public override string DisplayName => "Nefes";
        public override Rgb ColorAt(double pos, double t, EffectSettings s)
        {
            double k = 0.5 - 0.5 * Math.Cos(t * s.Speed * Math.PI * 0.5);
            return s.Color1.Scale(0.05 + 0.95 * k * k);
        }
    }

    sealed class ColorCycleEffect : Effect
    {
        public override string Id => "cycle";
        public override string DisplayName => "Renk döngüsü (hepsi aynı)";
        public override Rgb ColorAt(double pos, double t, EffectSettings s) => Rgb.FromHsv(t * s.Speed * 0.05);
    }

    sealed class RainbowWaveEffect : Effect
    {
        public override string Id => "rainbow";
        public override string DisplayName => "Gökkuşağı dalgası";
        public override Rgb ColorAt(double pos, double t, EffectSettings s) => Rgb.FromHsv(t * s.Speed * 0.1 - pos);
    }

    sealed class GradientWaveEffect : Effect
    {
        public override string Id => "gradient";
        public override string DisplayName => "İki renk dalgası";
        public override Rgb ColorAt(double pos, double t, EffectSettings s)
        {
            double k = 0.5 + 0.5 * Math.Sin((pos - t * s.Speed * 0.25) * Math.PI * 2);
            return Rgb.Lerp(s.Color1, s.Color2, k);
        }
    }

    sealed class CometEffect : Effect
    {
        public override string Id => "comet";
        public override string DisplayName => "Kuyruklu yıldız";
        public override Rgb ColorAt(double pos, double t, EffectSettings s)
        {
            double head = (t * s.Speed * 0.4) % 1.0;
            double d = head - pos;
            if (d < 0) d += 1;
            double k = Math.Max(0, 1 - d * 4);
            return Rgb.Lerp(s.Color2.Scale(0.08), s.Color1, k * k);
        }
    }

    sealed class OffEffect : Effect
    {
        public override string Id => "off";
        public override string DisplayName => "Kapalı";
        public override Rgb ColorAt(double pos, double t, EffectSettings s) => Rgb.Black;
    }

    /// <summary>
    /// Wiring check: every block of 12 LEDs gets its own color (red, green, blue, yellow, ...) and its first LED is white,
    /// so the user can tell which part of a daisy chain gets which LEDs and where each ring starts.
    /// </summary>
    sealed class SegmentTestEffect : Effect
    {
        static readonly Rgb[] Palette =
        {
            new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255), new Rgb(255, 200, 0),
            new Rgb(200, 0, 255), new Rgb(0, 255, 255), new Rgb(255, 80, 0), new Rgb(255, 0, 120),
        };

        public override string Id => "segtest";
        public override string DisplayName => "LED testi (12'li bölümler)";
        public override Rgb ColorAt(double pos, double t, EffectSettings s) => Palette[0];
        public override Rgb ColorAtLed(int index, double pos, double t, EffectSettings s) =>
            index % 12 == 0 ? new Rgb(255, 255, 255) : Palette[index / 12 % Palette.Length];
    }
}
