using System;
using System.Drawing;

class Program {
    static void Main() {
        using (Bitmap bmp = new Bitmap("current_dock.png")) {
            // Find dock border - white pixels on right edge
            int topBorderY = -1;
            for (int y = 50; y < 95; y++) {
                for (int x = 148; x < bmp.Width; x++) {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R > 180 && c.G > 180 && c.B > 180) {
                        // Could be border
                        if (x > 460 && topBorderY == -1) {
                            Console.WriteLine(string.Format("Right border candidate: x={0}, y={1}: {2},{3},{4}", x, y, c.R, c.G, c.B));
                        }
                    }
                }
            }
            // Specifically check right edge of dock 
            Console.WriteLine("--- Scan from right, y=65 ---");
            for (int x = bmp.Width - 1; x >= 100; x--) {
                Color c = bmp.GetPixel(x, 65);
                if (c.R + c.G + c.B > 60) {
                    Console.WriteLine(string.Format("x={0}: {1},{2},{3}", x, c.R, c.G, c.B));
                    if (x < 530) break;
                }
            }
            // Check x=460..540 at y=50..90
            Console.WriteLine("--- Button area scan ---");
            for (int y = 52; y < 90; y += 2) {
                for (int x = 460; x < 540; x++) {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R > 100 && c.G > 100 && c.B > 100) {
                        Console.WriteLine(string.Format("y={0}, x={1}: {2},{3},{4}", y, x, c.R, c.G, c.B));
                    }
                }
            }
        }
    }
}
