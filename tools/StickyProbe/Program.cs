using TicketManager.Services;

// 验证“逐级卷出”的顺序（纯计算，不需要起界面）：
// 场景都是三行链：客户 h=32 / 产品 h=34 / 线索 h=36（槽位 0-32 / 32-66 / 66-102）。
// boundaries[i] = 第 i 级“子树末尾那一行”的表头顶（越小说明内容滚得越靠上，≥ 102 表示还没进钉住区）。
var heights = new[] { 32.0, 34.0, 36.0 };
const double inf = double.PositiveInfinity;

static string Fmt(double[] s) => string.Join(" / ", s.Select(v => v.ToString("F0").PadLeft(3)));

void Case(string title, double[] b)
{
    var slides = StickyLayout.ComputeSlides(heights, b);
    Console.WriteLine($"{title,-46} boundaries={string.Join("/", b.Select(v => double.IsPositiveInfinity(v) ? "∞" : v.ToString("F0")))}  → slides {Fmt(slides)}");
}

Console.WriteLine("  客户 h=32 / 产品 h=34 / 线索 h=36；slides 单位 px（36 = 该级已完全滑出）\n");

// A. 整段只有一条线索：三级子树末尾同处一处 → 应该依次卷出
Console.WriteLine("A. 整段内容滚完（三级边界相同，从 102 到 0）：");
for (double b = 102; b >= -40; b -= 18) Case($"   边界={b:F0}", new[] { b, b, b });

// B. 同级换位：只有线索的子树到头了，产品/客户内容还长（边界很远）→ 只有线索卷出
Console.WriteLine("\nB. 只有线索到头（产品还有后续线索）：");
foreach (var b in new[] { 102.0, 80, 60, 40, 20, 0, -20 })
    Case($"   线索边界={b:F0}", new[] { inf, inf, b });

// C. 产品到头（它下面最后一封邮件滚完），客户后面还有很多产品 → 线索+产品依次卷出，客户不动
Console.WriteLine("\nC. 产品到头、客户还有后续产品：");
foreach (var b in new[] { 102.0, 80, 60, 40, 20, 0 })
    Case($"   产品/线索边界={b:F0}", new[] { inf, b, b });

// D. 最后一段内容（边界都是 ∞）→ 都不动
Console.WriteLine("\nD. 后面没有同级内容：");
Case("   全部 ∞", new[] { inf, inf, inf });
