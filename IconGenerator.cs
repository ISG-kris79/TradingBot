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

            // 모던 컬러 팔레트
            Color bgDarkNavy = Color.FromArgb(10, 14, 39);       // 진한 네이비
            Color bgMidNavy = Color.FromArgb(26, 31, 74);        // 미드 네이비
            Color primaryCyan = Color.FromArgb(0, 229, 255);     // 밝은 청록색 (#00E5FF)
            Color secondaryPurple = Color.FromArgb(124, 77, 255);// 보라색 (#7C4DFF)
            Color accentGreen = Color.FromArgb(0, 230, 118);     // 초록색 (#00E676)
            Color goldYellow = Color.FromArgb(255, 193, 7);      // 금색 (#FFC107)

            // 1. 배경 원형 그라디언트
            int center = size / 2;
            using (var bgBrush = new LinearGradientBrush(
                new Point(0, 0), 
                new Point(size, size),
                bgDarkNavy, 
                bgMidNavy))
            {
                g.FillEllipse(bgBrush, 0, 0, size, size);
            }

            // 2. 외곽 링 (2중)
            int ringWidth = Math.Max(2, size / 128);
            using (var pen1 = new Pen(primaryCyan, ringWidth))
            using (var pen2 = new Pen(Color.FromArgb(180, secondaryPurple), ringWidth / 2))
            {
                int margin1 = size / 16;
                int margin2 = margin1 + ringWidth * 2;
                g.DrawEllipse(pen1, margin1, margin1, size - margin1 * 2, size - margin1 * 2);
                g.DrawEllipse(pen2, margin2, margin2, size - margin2 * 2, size - margin2 * 2);
            }

            // 3. 중앙 코인 원
            int coinSize = size / 2;
            int coinX = size / 4;
            int coinY = size / 4;
            
            // 코인 배경
            using (var coinBrush = new SolidBrush(Color.FromArgb(240, bgMidNavy)))
            using (var coinPen = new Pen(primaryCyan, Math.Max(1, size / 170)))
            {
                g.FillEllipse(coinBrush, coinX, coinY, coinSize, coinSize);
                g.DrawEllipse(coinPen, coinX, coinY, coinSize, coinSize);
            }

            // 4. 코인 심볼 (₿ 스타일 - 간소화)
            int symbolX = coinX + coinSize / 2;
            int symbolY = coinY + coinSize / 2;
            int symbolSize = coinSize / 3;
            
            // 심볼 B 형태 그리기
            using (var symbolBrush = new SolidBrush(goldYellow))
            using (var font = new Font("Arial", symbolSize, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                // B 문자 그리기 (중앙 정렬)
                StringFormat sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString("₿", font, symbolBrush, symbolX, symbolY, sf);
            }

            // 5. 차트 라인 (좌측 하단 - 상승 추세)
            if (size >= 32)
            {
                int chartMargin = size / 8;
                Point[] chartPoints = new Point[]
                {
                    new Point(chartMargin, size - chartMargin),
                    new Point(chartMargin + size/12, size - chartMargin - size/16),
                    new Point(chartMargin + size/6, size - chartMargin - size/12),
                    new Point(chartMargin + size/4, size - chartMargin - size/8)
                };
                
                using (var chartPen = new Pen(accentGreen, Math.Max(2, size / 64)))
                {
                    chartPen.LineJoin = LineJoin.Round;
                    g.DrawLines(chartPen, chartPoints);
                }
                
                // 차트 포인트
                foreach (var point in chartPoints)
                {
                    int dotSize = Math.Max(2, size / 80);
                    g.FillEllipse(new SolidBrush(accentGreen), 
                        point.X - dotSize, point.Y - dotSize, dotSize * 2, dotSize * 2);
                }
            }

            // 6. AI 뉴럴 네트워크 패턴 (우측 상단 - 간소화)
            if (size >= 48)
            {
                int netMargin = size - size / 4;
                int netSize = size / 16;
                Point[] nodes = new Point[]
                {
                    new Point(netMargin - netSize * 2, size / 6),
                    new Point(netMargin, size / 6 - netSize),
                    new Point(netMargin - netSize, size / 5 + netSize),
                    new Point(netMargin - netSize, size / 4 + netSize)
                };
                
                // 연결선
                using (var netPen = new Pen(Color.FromArgb(120, secondaryPurple), Math.Max(1, size / 128)))
                {
                    g.DrawLine(netPen, nodes[0], nodes[2]);
                    g.DrawLine(netPen, nodes[1], nodes[2]);
                    g.DrawLine(netPen, nodes[0], nodes[3]);
                }
                
                // 노드
                int nodeSize = Math.Max(2, size / 64);
                foreach (var node in nodes)
                {
                    g.FillEllipse(new SolidBrush(secondaryPurple), 
                        node.X - nodeSize, node.Y - nodeSize, nodeSize * 2, nodeSize * 2);
                    g.DrawEllipse(new Pen(primaryCyan, 1), 
                        node.X - nodeSize, node.Y - nodeSize, nodeSize * 2, nodeSize * 2);
                }
            }

            // 7. 글로우 효과 (중앙 코인 주변)
            if (size >= 64)
            {
                for (int i = 0; i < 5; i++)
                {
                    int offset = i * 2;
                    int alpha = 80 - i * 15;
                    using (var glowPen = new Pen(Color.FromArgb(alpha, primaryCyan), 1))
                    {
                        g.DrawEllipse(glowPen, 
                            coinX - offset, coinY - offset, 
                            coinSize + offset * 2, coinSize + offset * 2);
                    }
                }
            }
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
