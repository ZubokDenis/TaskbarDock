using System;
using System.Drawing;

class Program {
    static void Main() {
        using (Bitmap bmp = new Bitmap("current_dock.png")) {
            Console.WriteLine(string.Format("Image: {0}x{1}", bmp.Width, bmp.Height));
            // Find ALL non-background pixels (R>60 || G>60 || B>60)
            int minX=9999, maxX=-1, minY=9999, maxY=-1;
            int btnLeft=-1, btnRight=-1;
            for (int x = 0; x < bmp.Width; x++) {
                for (int y = 50; y < 90; y++) {
                    Color c = bmp.GetPixel(x, y);
                    // High-alpha white/grey pixel = border or button (not full dark background)
                    if (c.R > 100 && c.G > 100 && c.B > 100) {
                        if (minX > x) minX = x;
                        if (maxX < x) maxX = x;
                        if (minY > y) minY = y;
                        if (maxY < y) maxY = y;
                        if (x > 460 && (btnLeft == -1 || x < btnLeft)) btnLeft = x;
                        if (x > 460) btnRight = Math.Max(btnRight, x);
                    }
                }
            }
            Console.WriteLine(string.Format("Bright pixels: X={0}..{1}, Y={2}..{3}", minX, maxX, minY, maxY));
            Console.WriteLine(string.Format("Button region (x>460): {0}..{1}", btnLeft, btnRight));
            // Sample pixel at x=490-515, center dock height
            for (int x = 475; x <= 540; x += 5) {
                for (int y = 60; y <= 75; y += 5) {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R > 80 || c.G > 80 || c.B > 80) {
                        Console.WriteLine(string.Format("x={0},y={1}: R={2},G={3},B={4}", x, y, c.R, c.G, c.B));
                    }
                }
            }
        }
    }
}
