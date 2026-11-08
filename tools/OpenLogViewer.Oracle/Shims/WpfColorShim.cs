// The App project is WPF and Windows-only; the oracle tool is not. Theme.cs, ColorMath.cs and
// ThemeCatalog.cs are linked in as they are (see the csproj), so the reference outputs come from
// the shipping formulas themselves, not from a copy of them. They touch WPF only through the
// handful of types below, which are stood in for here with the same behaviour.

namespace System.Windows.Media
{
    public readonly struct Color : IEquatable<Color>
    {
        private Color(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public byte R { get; }

        public byte G { get; }

        public byte B { get; }

        public static Color FromRgb(byte r, byte g, byte b) => new(r, g, b);

        public bool Equals(Color other) => R == other.R && G == other.G && B == other.B;

        public override bool Equals(object? obj) => obj is Color other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(R, G, B);

        public static bool operator ==(Color a, Color b) => a.Equals(b);

        public static bool operator !=(Color a, Color b) => !a.Equals(b);

        public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
    }

    public static class Colors
    {
        public static Color White { get; } = Color.FromRgb(255, 255, 255);

        public static Color Black { get; } = Color.FromRgb(0, 0, 0);
    }

    public static class ColorConverter
    {
        public static object? ConvertFromString(string text)
        {
            if (text.Length != 7 || text[0] != '#') throw new FormatException(text);
            return Color.FromRgb(
                Convert.ToByte(text.Substring(1, 2), 16),
                Convert.ToByte(text.Substring(3, 2), 16),
                Convert.ToByte(text.Substring(5, 2), 16));
        }
    }
}

namespace OpenLogViewer.App
{
    /// <summary>The real ThemeFonts resolves fonts through WPF; only its name for the Retro stack is used here.</summary>
    public static class ThemeFonts
    {
        public const string Retro = "./Fonts/#OLV Retro, Consolas";
    }
}
