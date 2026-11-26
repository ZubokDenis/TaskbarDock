using System;
using System.Drawing;

class Program {
    static void Main() {
        using (Bitmap bmp = new Bitmap("dock_full.png")) {
            // Find max white x in top region (y=3..20) - the right edge of dock border
            int maxBorderX = -1;
            for (int y = 3; y < 25; y++) {
                for (int x = 0; x < bmp.Width; x++) {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R > 150 && c.G > 150 && c.B > 150) {
                        maxBorderX = Math.Max(maxBorderX, x);
                    }
                }
            }
            Console.WriteLine("Right edge of dock border in top region: " + maxBorderX);
            
            // Min white X in top region (left edge)
            int minBorderX = 9999;
            for (int y = 3; y < 25; y++) {
                for (int x = 0; x < bmp.Width; x++) {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R > 150 && c.G > 150 && c.B > 150) {
                        minBorderX = Math.Min(minBorderX, x);
                        break;
                    }
                }
            }
            Console.WriteLine("Left edge of dock border in top region: " + minBorderX);
            Console.WriteLine("Dock width from border: " + (maxBorderX - minBorderX));
            
            // The button should be near maxBorderX - 16 - 26 = maxBorderX - 42
            int btnX = maxBorderX - 42;
            Console.WriteLine(string.Format("Expected button X: {0}..{1}", btnX, btnX + 26));
        }
    }
}
