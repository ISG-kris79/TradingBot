using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

class IconGenerator
{
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("🎨 TradingBot 고품질 아이콘 생성 중...");

        try
        {
            GenerateIcon();
            Console.WriteLine("✅ 아이콘 생성 완료!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 오류: {ex.Message}");
        }
    }

    static void GenerateIcon()
    {
        int[] sizes = { 256, 128, 64, 48, 32, 16 };
        List<Image> images = new List<Image>();

        // 각 크기별로 아이콘 생성
        foreach (int size in sizes)
        {
            var bmp = CreateIconImage(size);
            images.Add(bmp);
        }

        // ICO 파일로 저장
        string outputPath = @"e:\PROJECT\CoinFF\TradingBot\TradingBot\trading_bot.ico";

        // 첫 번째 이미지(가장 큼)를 기본으로 저장
        images[0].Save(outputPath, ImageFormat.Icon);

        Console.WriteLine($"✅ 아이콘 생성: {outputPath}");
        Console.WriteLine($"   포함된 크기: {string.Join(", ", sizes)}");

        // PNG도 생성 (미리보기)
        string pngPath = @"e:\PROJECT\CoinFF\TradingBot\TradingBot\trading_bot.png";
        images[0].Save(pngPath, ImageFormat.Png);
        Console.WriteLine($"✅ PNG 미리보기: {pngPath}");

        foreach (var img in images)
            img?.Dispose();
    }

    static Bitmap CreateIconImage(int size)
    {
        Bitmap bmp = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            // 배경 색상
            Color bgColor = Color.FromArgb(15, 23, 42);          // 진한 파란색
            Color gradientColor = Color.FromArgb(30, 58, 138);   // 그라디언트
            Color primaryColor = Color.FromArgb(59, 130, 246);   // 밝은 파란색
            Color accentColor = Color.FromArgb(34, 197, 94);     // 초록색 (상승)

            // 배경 채우기
            g.Clear(bgColor);

            // 라운드 코너 사각형 배경
            int margin = size / 16;
            Rectangle bgRect = new Rectangle(margin, margin, size - margin * 2, size - margin * 2);
            using (var path = RoundedRectangle(bgRect, size / 8))
            {
                g.FillPath(new SolidBrush(gradientColor), path);
                g.DrawPath(new Pen(primaryColor, Math.Max(1, size / 32)), path);
            }

            // 차트 막대 그리기 (상승 추세)
            int chartMargin = size / 4;
            int barWidth = size / 16;
            int yBottom = size - chartMargin;

            // 막대 1
            int x1 = chartMargin;
            int bar1Height = size / 5;
            g.FillRectangle(new SolidBrush(primaryColor),
                new Rectangle(x1, yBottom - bar1Height, barWidth, bar1Height));

            // 막대 2 (높음)
            int x2 = x1 + (int)(barWidth * 1.5);
            int bar2Height = size / 3;
            g.FillRectangle(new SolidBrush(accentColor),
                new Rectangle(x2, yBottom - bar2Height, barWidth, bar2Height));

            // 막대 3
            int x3 = x2 + (int)(barWidth * 1.5);
            int bar3Height = size / 4;
            g.FillRectangle(new SolidBrush(primaryColor),
                new Rectangle(x3, yBottom - bar3Height, barWidth, bar3Height));

            // 상승 화살표 그리기
            int arrowX = size / 2 + size / 8;
            int arrowY = size / 4;
            int arrowSize = size / 12;

            Point[] arrowPoints = new Point[]
            {
                new Point(arrowX - arrowSize, arrowY + arrowSize),
                new Point(arrowX + arrowSize, arrowY + arrowSize),
                new Point(arrowX, arrowY - arrowSize)
            };
            g.FillPolygon(new SolidBrush(accentColor), arrowPoints);

            // 화살표 막대
            int barX = arrowX - arrowSize / 4;
            g.FillRectangle(new SolidBrush(accentColor),
                new Rectangle(barX, arrowY + arrowSize, arrowSize / 2, arrowSize));
        }

        return bmp;
    }

    static GraphicsPath RoundedRectangle(Rectangle rect, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int x = rect.X;
        int y = rect.Y;
        int w = rect.Width;
        int h = rect.Height;
        int r = radius;

        path.AddArc(x, y, r, r, 180, 90);
        path.AddArc(x + w - r, y, r, r, 270, 90);
        path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
        path.AddArc(x, y + h - r, r, r, 90, 90);
        path.CloseFigure();

        return path;
    }
}
