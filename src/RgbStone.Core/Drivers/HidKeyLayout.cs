using System.Collections.Generic;
using System.Linq;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Horizontal position (0..1) of a key on a full-size ANSI/ISO board by its HID usage code, so effects sweep across
    /// keyboards whose drivers address keys by usage. Unknown codes fall back to the middle.
    /// </summary>
    static class HidKeyLayout
    {
        static readonly Dictionary<byte, double> Units = Build();

        static Dictionary<byte, double> Build()
        {
            var d = new Dictionary<byte, double>();
            void Row(double start, double step, params byte[] keys) { for (int i = 0; i < keys.Length; i++) d[keys[i]] = start + i * step; }
            d[0x29] = 0;                                                                   // Esc
            Row(2, 1, 0x3A, 0x3B, 0x3C, 0x3D); Row(6.5, 1, 0x3E, 0x3F, 0x40, 0x41); Row(11, 1, 0x42, 0x43, 0x44, 0x45); // F1-F12
            Row(15.5, 1, 0x46, 0x47, 0x48);                                                // PrtSc ScrLk Pause
            d[0x35] = 0; Row(1, 1, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2D, 0x2E); d[0x2A] = 13.5; // number row
            d[0x2B] = 0.25; Row(1.5, 1, 0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30); d[0x31] = 13.75; // QWERTY
            d[0x39] = 0.4; Row(1.75, 1, 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F, 0x33, 0x34); d[0x32] = 12.75; d[0x28] = 13.6; // ASDF
            d[0xE1] = 0.6; d[0x64] = 1.25; Row(2.25, 1, 0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38); d[0xE5] = 13.4; // ZXCV
            d[0xE0] = 0.3; d[0xE3] = 1.5; d[0xE2] = 2.8; d[0x2C] = 6.5; d[0xE6] = 10.2; d[0xE7] = 11.5; d[0x65] = 12.6; d[0xE4] = 13.8; d[0xF0] = 11.5; // bottom row
            Row(15.5, 1, 0x49, 0x4A, 0x4B); Row(15.5, 1, 0x4C, 0x4D, 0x4E);                // Ins Home PgUp / Del End PgDn
            d[0x52] = 16.5; d[0x50] = 15.5; d[0x51] = 16.5; d[0x4F] = 17.5;                // arrows
            Row(19, 1, 0x53, 0x54, 0x55, 0x56); Row(19, 1, 0x5F, 0x60, 0x61, 0x57); Row(19, 1, 0x5C, 0x5D, 0x5E);
            Row(19, 1, 0x59, 0x5A, 0x5B, 0x58); d[0x62] = 19.5; d[0x63] = 21;              // numpad
            return d;
        }

        public static double X(byte usage) => Units.TryGetValue(usage, out var u) ? u / 22 : 0.5;

        public static double[] X(IEnumerable<byte> usages) => usages.Select(X).ToArray();
    }
}
