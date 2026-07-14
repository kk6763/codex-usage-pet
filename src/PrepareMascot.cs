using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

internal static class PrepareMascot
{
    private static bool IsBackground(Color c)
    {
        int r = c.R;
        int g = c.G;
        int b = c.B;

        // Pale cyan/blue background, including the blue diagonal band.
        bool cyan = g > 155 && b > 185 && b > r + 7 && b >= g - 18;

        // Pink diagonal band. The character's red cheeks do not meet the blue threshold.
        bool pink = r > 185 && b > 150 && g < 210 && r > g + 10 && b > g + 2;

        // Very pale blue-white background around the upper-right corner.
        bool paleBlue = r > 175 && g > 205 && b > 220 && b > r + 4;

        return cyan || pink || paleBlue;
    }

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: PrepareMascot <input.png> <output.png>");
            return 2;
        }

        string input = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(output));

        using (var source = new Bitmap(input))
        using (var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
        {
            int opaque = 0;
            int transparent = 0;

            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    Color c = source.GetPixel(x, y);
                    if (IsBackground(c))
                    {
                        result.SetPixel(x, y, Color.FromArgb(0, c.R, c.G, c.B));
                        transparent++;
                    }
                    else
                    {
                        result.SetPixel(x, y, Color.FromArgb(255, c.R, c.G, c.B));
                        opaque++;
                    }
                }
            }

            result.Save(output, ImageFormat.Png);
            Console.WriteLine("Wrote {0} ({1} opaque, {2} transparent pixels)", output, opaque, transparent);
        }

        return 0;
    }
}
