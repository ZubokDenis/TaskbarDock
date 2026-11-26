using System;
using System.Drawing;

class Program {
    static void Main() {
        using (Bitmap bmp = new Bitmap("current_dock.png")) {
            Console.WriteLine(string.Format("Image size: {0}x{1}", bmp.Width, bmp.Height));
            for (int y = 58; y <= 70; y += 3) {
                for (int x = 540; x <= 570; x += 5) {
                    Color c = bmp.GetPixel(x, y);
                    Console.WriteLine(string.Format("x={0}, y={1}: R={2}, G={3}, B={4}", x, y, c.R, c.G, c.B));
                }
            }
        }
    }
}
