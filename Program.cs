using System.Drawing;
using System.Drawing.Printing;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace UnionlinePrintBridge;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    private readonly ComboBox printerBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 390 };
    private readonly NumericUpDown widthBox = new() { Minimum = 10, Maximum = 200, Value = 40, DecimalPlaces = 1, Increment = 0.5M, Width = 90 };
    private readonly NumericUpDown heightBox = new() { Minimum = 10, Maximum = 200, Value = 30, DecimalPlaces = 1, Increment = 0.5M, Width = 90 };
    private readonly NumericUpDown gapBox = new() { Minimum = 0, Maximum = 20, Value = 2, DecimalPlaces = 1, Increment = 0.5M, Width = 90 };
    private readonly Label status = new() { AutoSize = true, Text = "Bridge nuk është nisur." };
    private readonly Button startButton = new() { Text = "Nis shërbimin lokal", AutoSize = true };
    private readonly Button testButton = new() { Text = "Printo etiketë test", AutoSize = true };
    private HttpListener? listener;
    private CancellationTokenSource? cts;
    private const int Port = 8765;
    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
    { "https://ebaumarket.com", "https://www.ebaumarket.com" };

    public MainForm()
    {
        Text = "Unionline Print Bridge";
        Width = 560; Height = 390; MinimumSize = new Size(560, 390);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 8 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);
        AddRow(root, 0, "Printeri", printerBox);
        var refresh = new Button { Text = "Rifresko printerët", AutoSize = true };
        refresh.Click += (_, _) => LoadPrinters(); AddRow(root, 1, "", refresh);
        AddRow(root, 2, "Gjerësia (mm)", widthBox);
        AddRow(root, 3, "Lartësia (mm)", heightBox);
        AddRow(root, 4, "Gap (mm)", gapBox);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(testButton); buttons.Controls.Add(startButton); AddRow(root, 5, "Veprime", buttons);
        root.Controls.Add(status, 0, 6); root.SetColumnSpan(status, 2);
        var note = new Label { AutoSize = true, MaximumSize = new Size(490, 0), ForeColor = Color.DimGray, Text = "Pa çelës lokal. Pranon vetëm kërkesa nga faqet zyrtare Unionline dhe dëgjon vetëm në këtë kompjuter (127.0.0.1). Bridge printon përmes driver-it të instaluar në Windows. Gap-i fizik konfigurohet te driver-i i printerit." };
        root.Controls.Add(note, 0, 7); root.SetColumnSpan(note, 2);
        LoadPrinters();
        testButton.Click += (_, _) => PrintJobs(new() { new LabelJob { Name = "Unionline Test", Price = "4,990 L", Code = "TR-10025", Barcode = "123456789012", Quantity = 1 } });
        startButton.Click += async (_, _) => await ToggleServer();
        FormClosing += (_, _) => StopServer();
    }

    private static void AddRow(TableLayoutPanel p, int row, string label, Control control)
    {
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 0, 0) };
        p.Controls.Add(l, 0, row); control.Anchor = AnchorStyles.Left; p.Controls.Add(control, 1, row);
    }

    private void LoadPrinters()
    {
        var selected = printerBox.SelectedItem?.ToString();
        printerBox.Items.Clear();
        foreach (string p in PrinterSettings.InstalledPrinters) printerBox.Items.Add(p);
        if (selected != null && printerBox.Items.Contains(selected)) printerBox.SelectedItem = selected;
        else if (printerBox.Items.Count > 0) printerBox.SelectedItem = new PrinterSettings().PrinterName;
        if (printerBox.SelectedIndex < 0 && printerBox.Items.Count > 0) printerBox.SelectedIndex = 0;
    }

    private async Task ToggleServer()
    {
        if (listener?.IsListening == true) { StopServer(); return; }
        if (printerBox.SelectedItem == null) { MessageBox.Show("Zgjidh printerin."); return; }
        listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try { listener.Start(); }
        catch (Exception ex) { MessageBox.Show("Nuk u hap porta lokale: " + ex.Message); listener = null; return; }
        cts = new CancellationTokenSource(); startButton.Text = "Ndalo shërbimin";
        status.Text = $"Aktiv në http://127.0.0.1:{Port}/  |  Printeri: {printerBox.SelectedItem}";
        _ = Task.Run(() => ListenLoop(cts.Token)); await Task.CompletedTask;
    }

    private void StopServer()
    {
        try { cts?.Cancel(); listener?.Stop(); listener?.Close(); } catch { }
        listener = null; cts?.Dispose(); cts = null;
        if (!IsDisposed) { startButton.Text = "Nis shërbimin lokal"; status.Text = "Bridge nuk është nisur."; }
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleRequest(ctx), token);
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var origin = ctx.Request.Headers["Origin"] ?? "";
            // CORS is not authentication; it prevents ordinary browser pages from other origins
            // from reading/sending permitted requests. The listener is loopback-only.
            if (!AllowedOrigins.Contains(origin))
            {
                await Reply(ctx, 403, new { ok = false, error = "Origin not allowed" });
                return;
            }
            ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
            ctx.Response.Headers["Vary"] = "Origin";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            if (ctx.Request.Headers["Access-Control-Request-Private-Network"] == "true")
                ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            if (ctx.Request.HttpMethod == "OPTIONS") { ctx.Response.StatusCode = 204; ctx.Response.Close(); return; }
            if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url?.AbsolutePath == "/status")
            {
                await Reply(ctx, 200, new { ok = true, service = "Unionline Print Bridge", version = "1.1.0", printer = printerBox.SelectedItem?.ToString() ?? "" });
                return;
            }
            if (ctx.Request.HttpMethod != "POST" || ctx.Request.Url?.AbsolutePath != "/print") { await Reply(ctx, 404, new { ok = false, error = "Not found" }); return; }
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<PrintRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request?.Items == null || request.Items.Count == 0) { await Reply(ctx, 400, new { ok = false, error = "Lista është bosh" }); return; }
            var jobs = request.Items.Select(x => new LabelJob { Name = x.Name ?? "", Price = x.Price ?? "", Code = x.Code ?? "", Barcode = string.IsNullOrWhiteSpace(x.Barcode) ? (x.Code ?? "") : x.Barcode!, Quantity = Math.Clamp(x.Quantity, 1, 500) }).ToList();
            if (InvokeRequired) BeginInvoke(() => PrintJobs(jobs)); else PrintJobs(jobs);
            await Reply(ctx, 200, new { ok = true, accepted = jobs.Sum(x => x.Quantity), message = "Print job accepted" });
        }
        catch (Exception ex) { try { await Reply(ctx, 500, new { ok = false, error = ex.Message }); } catch { } }
    }

    private static async Task Reply(HttpListenerContext ctx, int code, object data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data); ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json; charset=utf-8"; ctx.Response.ContentLength64 = bytes.Length; await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
    }

    private void PrintJobs(List<LabelJob> jobs)
    {
        try
        {
            var doc = new PrintDocument(); doc.PrinterSettings.PrinterName = printerBox.SelectedItem?.ToString() ?? "";
            if (!doc.PrinterSettings.IsValid) throw new InvalidOperationException("Printeri i zgjedhur nuk është i disponueshëm.");
            int w = MmToHundredthsInch((double)widthBox.Value), h = MmToHundredthsInch((double)heightBox.Value);
            var paper = new PaperSize($"Unionline_{widthBox.Value}x{heightBox.Value}mm", w, h);
            doc.DefaultPageSettings.PaperSize = paper; doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0); doc.OriginAtMargins = false;
            var pages = jobs.SelectMany(j => Enumerable.Repeat(j, j.Quantity)).ToList(); int page = 0;
            doc.PrintPage += (_, e) =>
            {
                var j = pages[page++]; DrawLabel(e.Graphics!, e.MarginBounds, j);
                e.HasMorePages = page < pages.Count;
            };
            doc.Print(); status.Text = $"U dërguan {pages.Count} etiketa te {doc.PrinterSettings.PrinterName}. Gap: {gapBox.Value} mm (driver).";
        }
        catch (Exception ex) { MessageBox.Show("Gabim printimi: " + ex.Message, "Unionline Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static int MmToHundredthsInch(double mm) => (int)Math.Round(mm / 25.4 * 100);
    private static void DrawLabel(Graphics g, Rectangle bounds, LabelJob j)
    {
        g.PageUnit = GraphicsUnit.Display;
        float scale = Math.Min(bounds.Width / 40f, bounds.Height / 30f);
        using var nameFont = new Font("Arial", 7.5f, FontStyle.Bold);
        using var priceFont = new Font("Arial", 15f, FontStyle.Bold);
        using var codeFont = new Font("Arial", 10f, FontStyle.Bold);
        using var barcodeFont = new Font("Consolas", 11f, FontStyle.Regular);
        using var tinyFont = new Font("Arial", 5.5f);
        using var black = new SolidBrush(Color.Black);
        var rect = new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(j.Name, nameFont, black, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height * .22f), sf);
        g.DrawString(j.Price, priceFont, black, new RectangleF(rect.X, rect.Y + rect.Height * .20f, rect.Width, rect.Height * .25f), sf);
        g.DrawString(j.Code, codeFont, black, new RectangleF(rect.X, rect.Y + rect.Height * .47f, rect.Width, rect.Height * .17f), sf);
        // Human-readable barcode placeholder text; true barcode rendering is added in the next iteration.
        g.DrawString(j.Barcode, barcodeFont, black, new RectangleF(rect.X, rect.Y + rect.Height * .65f, rect.Width, rect.Height * .20f), sf);
        g.DrawString(j.Barcode, tinyFont, black, new RectangleF(rect.X, rect.Y + rect.Height * .83f, rect.Width, rect.Height * .10f), sf);
    }
}

public sealed class PrintRequest { public List<PrintItem>? Items { get; set; } }
public sealed class PrintItem { public string? Name { get; set; } public string? Price { get; set; } public string? Code { get; set; } public string? Barcode { get; set; } public int Quantity { get; set; } = 1; }
public sealed class LabelJob { public string Name { get; set; } = ""; public string Price { get; set; } = ""; public string Code { get; set; } = ""; public string Barcode { get; set; } = ""; public int Quantity { get; set; } = 1; }
