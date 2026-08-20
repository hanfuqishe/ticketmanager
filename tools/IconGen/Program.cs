using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// 生成多尺寸 .ico：红色渐变圆角底 + 白色票券 + 绿色对勾 + 票根条纹
const string outPath = @"C:/msys64/home/hanfu/ticketmanager/src/TicketManager/TicketManager.ico";
var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };

using var master = DrawIcon(256);

var entries = new List<(int size, byte[] png)>();
foreach (var s in sizes)
{
    byte[] png;
    using var ms = new MemoryStream();
    if (s == 256)
    {
        master.Save(ms, ImageFormat.Png);
    }
    else
    {
        using var small = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(master, 0, 0, s, s);
        }
        small.Save(ms, ImageFormat.Png);
    }
    entries.Add((s, ms.ToArray()));
}

using (var fs = File.Create(outPath))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0);                 // reserved
    w.Write((ushort)1);                 // type = icon
    w.Write((ushort)entries.Count);     // count
    int offset = 6 + entries.Count * 16;
    foreach (var (size, png) in entries)
    {
        w.Write((byte)(size == 256 ? 0 : size)); // width
        w.Write((byte)(size == 256 ? 0 : size)); // height
        w.Write((byte)0);                        // color count
        w.Write((byte)0);                        // reserved
        w.Write((ushort)1);                      // planes
        w.Write((ushort)32);                     // bits per pixel
        w.Write(png.Length);                     // data size
        w.Write(offset);                         // data offset
        offset += png.Length;
    }
    foreach (var (_, png) in entries) w.Write(png);
}

// 预览 PNG
master.Save(@"C:/msys64/home/hanfu/ticketmanager/tools/IconGen/preview.png", ImageFormat.Png);

Console.WriteLine($"已生成 {outPath}，共 {entries.Count} 个尺寸");

static Bitmap DrawIcon(int s)
{
    var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    float k = s / 256f;
    var r = new Rectangle(0, 0, s, s);

    // 红色渐变圆角底
    using var bg = new LinearGradientBrush(r, Color.FromArgb(220, 38, 38), Color.FromArgb(185, 28, 28), 90f);
    using (var bgPath = RoundedRect(r, (int)(56 * k)))
        g.FillPath(bg, bgPath);

    // 白色票券
    var tk = new Rectangle((int)(44 * k), (int)(56 * k), (int)(168 * k), (int)(128 * k));
    using (var tkPath = RoundedRect(tk, (int)(16 * k)))
    using (var white = new SolidBrush(Color.White))
        g.FillPath(white, tkPath);

    // 穿孔虚线
    using (var dash = new Pen(Color.FromArgb(220, 38, 38), Math.Max(1f, 3f * k)) { DashStyle = DashStyle.Dash })
        g.DrawLine(dash, s / 2f, tk.Top + 18 * k, s / 2f, tk.Bottom - 18 * k);

    // 绿色对勾
    using (var check = new Pen(Color.FromArgb(22, 163, 74), Math.Max(1.5f, 13f * k))
    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        g.DrawLines(check, new[]
        {
            new PointF(66 * k, 128 * k),
            new PointF(96 * k, 156 * k),
            new PointF(150 * k, 96 * k)
        });

    // 票根条纹
    using (var stub = new Pen(Color.FromArgb(220, 38, 38), Math.Max(1f, 3f * k)))
        for (int i = 0; i < 4; i++)
            g.DrawLine(stub, s / 2f + 20 * k, tk.Top + 30 * k + i * 20 * k, s - 52 * k, tk.Top + 30 * k + i * 20 * k);

    return bmp;
}

static GraphicsPath RoundedRect(Rectangle r, int radius)
{
    var p = new GraphicsPath();
    int d = radius * 2;
    if (d > 0)
    {
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    }
    p.CloseFigure();
    return p;
}
