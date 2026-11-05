using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using ZXing;
using ZXing.Common;

namespace UnionlinePrintBridge;
internal static class Program
{
    [STAThread] static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); }
}
public sealed class MainForm : Form
{
    private readonly ComboBox printerBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 390 };
    private readonly NumericUpDown widthBox = new() { Minimum = 10, Maximum = 200, Value = 40, DecimalPlaces = 1, Increment = 0.5M, Width = 90 };
    private readonly NumericUpDown heightBox = new() { Minimum = 10, Maximum = 200, Value = 30, DecimalPlaces = 1, Increment = 0.5M, Width = 90 };
    private readonly NumericUpDown gapBox = new() { Minimum = 0, Maximum = 20, Value = 2, DecimalPlaces = 1, Increment = 0.5M, Width = 90 };
    private readonly Label status = new() { AutoSize = true, Text = "Bridge nuk është nisur." };
    private readonly Button startButton = new() { Text = "Nis shërbimin lokal", AutoSize = true };
    private readonly Button testButton = new() { Text = "Testo kornizën 40×30", AutoSize = true };
    private readonly Button infoButton = new() { Text = "Kontrollo parametrat", AutoSize = true };
    private HttpListener? listener; private CancellationTokenSource? cts;
    private const int Port = 8765;
    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
    { "https://ebaumarket.com", "https://www.ebaumarket.com" };

    public MainForm()
    {
        Text = "Unionline Print Bridge"; Width = 580; Height = 410; MinimumSize = new Size(580,410);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI",9F);
        var root = new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(18), ColumnCount=2, RowCount=8 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,125)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); Controls.Add(root);
        AddRow(root,0,"Printeri",printerBox);
        var refresh=new Button { Text="Rifresko printerët",AutoSize=true }; refresh.Click+=(_,_)=>LoadPrinters(); AddRow(root,1,"",refresh);
        AddRow(root,2,"Gjerësia (mm)",widthBox); AddRow(root,3,"Lartësia (mm)",heightBox); AddRow(root,4,"Gap (mm)",gapBox);
        var buttons=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill }; buttons.Controls.Add(testButton); buttons.Controls.Add(infoButton); buttons.Controls.Add(startButton); AddRow(root,5,"Veprime",buttons);
        root.Controls.Add(status,0,6); root.SetColumnSpan(status,2);
        var note=new Label { AutoSize=true,MaximumSize=new Size(510,0),ForeColor=Color.DimGray,
            Text="Pa çelës lokal. Dëgjon vetëm në 127.0.0.1. Barkodi gjenerohet grafikisht Code 128. GAP-i fizik konfigurohet te driver-i." };
        root.Controls.Add(note,0,7); root.SetColumnSpan(note,2);
        LoadPrinters();
        testButton.Click+=(_,_)=>PrintCalibrationLabel();
        infoButton.Click+=(_,_)=>ShowPrinterDiagnostics();
        startButton.Click+=async(_,_)=>await ToggleServer(); FormClosing+=(_,_)=>StopServer();
    }
    private static void AddRow(TableLayoutPanel p,int row,string label,Control control)
    { p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.Controls.Add(new Label{Text=label,AutoSize=true,Anchor=AnchorStyles.Left,Padding=new Padding(0,7,0,0)},0,row); control.Anchor=AnchorStyles.Left;p.Controls.Add(control,1,row); }
    private void LoadPrinters()
    {
        var selected=printerBox.SelectedItem?.ToString(); printerBox.Items.Clear();
        foreach(string p in PrinterSettings.InstalledPrinters) printerBox.Items.Add(p);
        if(selected!=null&&printerBox.Items.Contains(selected)) printerBox.SelectedItem=selected;
        else if(printerBox.Items.Count>0) printerBox.SelectedItem=new PrinterSettings().PrinterName;
        if(printerBox.SelectedIndex<0&&printerBox.Items.Count>0) printerBox.SelectedIndex=0;
    }
    private async Task ToggleServer()
    {
        if(listener?.IsListening==true){StopServer();return;}
        if(printerBox.SelectedItem==null){MessageBox.Show("Zgjidh printerin.");return;}
        listener=new HttpListener();listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try{listener.Start();}catch(Exception ex){MessageBox.Show("Nuk u hap porta lokale: "+ex.Message);listener=null;return;}
        cts=new CancellationTokenSource();startButton.Text="Ndalo shërbimin";
        status.Text=$"Aktiv në http://127.0.0.1:{Port}/ | Printeri: {printerBox.SelectedItem}";
        _=Task.Run(()=>ListenLoop(cts.Token));await Task.CompletedTask;
    }
    private void StopServer()
    {
        try{cts?.Cancel();listener?.Stop();listener?.Close();}catch{}
        listener=null;cts?.Dispose();cts=null;
        if(!IsDisposed){startButton.Text="Nis shërbimin lokal";status.Text="Bridge nuk është nisur.";}
    }
    private async Task ListenLoop(CancellationToken token)
    {
        while(!token.IsCancellationRequested&&listener?.IsListening==true)
        { HttpListenerContext ctx;try{ctx=await listener.GetContextAsync();}catch{break;}_=Task.Run(()=>HandleRequest(ctx),token); }
    }
    private async Task HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            string origin=ctx.Request.Headers["Origin"]??"";
            if(!AllowedOrigins.Contains(origin)){await Reply(ctx,403,new{ok=false,error="Origin not allowed"});return;}
            ctx.Response.Headers["Access-Control-Allow-Origin"]=origin;ctx.Response.Headers["Vary"]="Origin";
            ctx.Response.Headers["Access-Control-Allow-Methods"]="GET, POST, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"]="Content-Type";
            if(ctx.Request.Headers["Access-Control-Request-Private-Network"]=="true")ctx.Response.Headers["Access-Control-Allow-Private-Network"]="true";
            if(ctx.Request.HttpMethod=="OPTIONS"){ctx.Response.StatusCode=204;ctx.Response.Close();return;}
            if(ctx.Request.HttpMethod=="GET"&&ctx.Request.Url?.AbsolutePath=="/status")
            {await Reply(ctx,200,new{ok=true,service="Unionline Print Bridge",version="1.2.5",printer=printerBox.SelectedItem?.ToString()??""});return;}
            if(ctx.Request.HttpMethod!="POST"||ctx.Request.Url?.AbsolutePath!="/print"){await Reply(ctx,404,new{ok=false,error="Not found"});return;}
            using var reader=new StreamReader(ctx.Request.InputStream,ctx.Request.ContentEncoding);
            var req=JsonSerializer.Deserialize<PrintRequest>(await reader.ReadToEndAsync(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(req?.Items==null||req.Items.Count==0){await Reply(ctx,400,new{ok=false,error="Lista është bosh"});return;}
            if(req.Items.Count != 1 || req.Items.Any(x => x.Quantity > 1)) { await Reply(ctx, 400, new { ok=false, error="Testimi i sigurisë: lejohet vetëm 1 etiketë për kërkesë." }); return; }
            var jobs=req.Items.Select(i=>new LabelJob{Name=i.Name??"",Price=i.Price??"",Code=i.Code??"",Barcode=string.IsNullOrWhiteSpace(i.Barcode)?(i.Code??""):i.Barcode!,Quantity=1}).ToList();
            if(IsDisposed){await Reply(ctx,503,new{ok=false,error="Bridge is closing"});return;}
            BeginInvoke(()=>PrintJobs(jobs)); await Reply(ctx,200,new{ok=true,accepted=jobs.Sum(x=>x.Quantity),message="Print job accepted"});
        }
        catch(Exception ex){try{await Reply(ctx,500,new{ok=false,error=ex.Message});}catch{}}
    }
    private static async Task Reply(HttpListenerContext ctx,int code,object data)
    {var bytes=JsonSerializer.SerializeToUtf8Bytes(data);ctx.Response.StatusCode=code;ctx.Response.ContentType="application/json; charset=utf-8";ctx.Response.ContentLength64=bytes.Length;await ctx.Response.OutputStream.WriteAsync(bytes);ctx.Response.Close();}

    private void ShowPrinterDiagnostics()
    {
        try
        {
            if (printerBox.SelectedItem == null) { MessageBox.Show("Zgjidh printerin."); return; }
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerBox.SelectedItem.ToString()!;
            if (!doc.PrinterSettings.IsValid) throw new InvalidOperationException("Printeri nuk është i disponueshëm.");
            var ps = doc.DefaultPageSettings;
            var paper = new PaperSize($"Unionline_{widthBox.Value}x{heightBox.Value}mm",
                MmToHundredthsInch((double)widthBox.Value), MmToHundredthsInch((double)heightBox.Value));
            ps.PaperSize = paper;
            ps.Margins = new Margins(0,0,0,0);
            var r = doc.PrinterSettings.DefaultPageSettings.PrinterResolution;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Printeri: {doc.PrinterSettings.PrinterName}");
            sb.AppendLine($"Printeri i Windows: {doc.PrinterSettings.PrinterName}");
            sb.AppendLine($"Formati i kërkuar: {widthBox.Value} × {heightBox.Value} mm");
            sb.AppendLine($"Formati i pranuar: {ps.PaperSize.PaperName} ({ps.PaperSize.Width} × {ps.PaperSize.Height} / 1-100 inch)");
            sb.AppendLine($"Rezolucioni: {r.X} × {r.Y} DPI ({r.Kind})");
            sb.AppendLine($"PrintableArea: {ps.PrintableArea}");
            sb.AppendLine($"HardMargins: {ps.HardMarginX:0.##}, {ps.HardMarginY:0.##}");
            MessageBox.Show(sb.ToString(), "Diagnostika e printerit");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Diagnostika"); }
    }

    private void PrintJobs(List<LabelJob> jobs)
    {
        try
        {
            if (printerBox.SelectedItem == null) throw new InvalidOperationException("Zgjidh printerin.");
            // Safety guard: during diagnosis, never advance more than one physical label.
            var job = jobs.FirstOrDefault();
            if (job == null) throw new InvalidOperationException("Nuk ka etiketë për printim.");
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerBox.SelectedItem.ToString()!;
            if (!doc.PrinterSettings.IsValid) throw new InvalidOperationException("Printeri nuk është i disponueshëm.");

            doc.DefaultPageSettings.PaperSize = new PaperSize(
                $"Unionline_{widthBox.Value}x{heightBox.Value}mm",
                MmToHundredthsInch((double)widthBox.Value),
                MmToHundredthsInch((double)heightBox.Value));
            doc.DefaultPageSettings.Margins = new Margins(0,0,0,0);
            doc.DefaultPageSettings.Landscape = false;
            doc.OriginAtMargins = false;
            doc.PrintController = new StandardPrintController();

            doc.PrintPage += (_, e) =>
            {
                Graphics g = e.Graphics!;
                var state = g.Save();
                try
                {
                    // Work in device pixels at the printer's actual resolution.
                    // This avoids mixing GDI+ display units with millimetres.
                    g.PageUnit = GraphicsUnit.Pixel;
                    g.PageScale = 1f;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

                    float dpiX = g.DpiX > 0 ? g.DpiX : 203f;
                    float dpiY = g.DpiY > 0 ? g.DpiY : 203f;
                    float targetW = (float)widthBox.Value / 25.4f * dpiX;
                    float targetH = (float)heightBox.Value / 25.4f * dpiY;
                    RectangleF clip = g.VisibleClipBounds;
                    float availableW = clip.Width;
                    float availableH = clip.Height;
                    if (availableW <= 0 || availableH <= 0)
                        throw new InvalidOperationException("Driveri nuk raporton zonë printimi të vlefshme.");

                    // Draw one label at native physical size, aligned to the printable origin.
                    // If driver page is smaller than the requested stock, stop instead of
                    // silently scaling to a tiny corner or feeding multiple blank labels.
                    if (availableW + 2 < targetW || availableH + 2 < targetH)
                        throw new InvalidOperationException(
                            $"Zona e printimit ({availableW:0} × {availableH:0} px) është më e vogël se etiketa e kërkuar ({targetW:0} × {targetH:0} px). Kontrollo Paper Size në driver.");
                    g.SetClip(new RectangleF(0, 0, targetW, targetH));
                    DrawLabelReportPixels(g, targetW, targetH, job);
                }
                finally { g.Restore(state); }
                e.HasMorePages = false;
            };

            doc.Print();
            status.Text = $"U dërgua vetëm 1 etiketë test te {doc.PrinterSettings.PrinterName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Printimi u ndal për siguri: " + ex.Message,
                "Unionline Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PrintCalibrationLabel()
    {
        try
        {
            if (printerBox.SelectedItem == null) throw new InvalidOperationException("Zgjidh printerin.");
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerBox.SelectedItem.ToString()!;
            if (!doc.PrinterSettings.IsValid) throw new InvalidOperationException("Printeri nuk është i disponueshëm.");

            // PaperSize dimensions are in hundredths of an inch: 157 × 118 ≈ 40 × 30 mm.
            doc.DefaultPageSettings.PaperSize = new PaperSize(
                $"Unionline_{widthBox.Value}x{heightBox.Value}mm",
                MmToHundredthsInch((double)widthBox.Value),
                MmToHundredthsInch((double)heightBox.Value));
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            doc.DefaultPageSettings.Landscape = false;
            doc.OriginAtMargins = false;
            doc.PrintController = new StandardPrintController();

            doc.PrintPage += (_, e) =>
            {
                var g = e.Graphics!;
                var state = g.Save();
                try
                {
                    g.PageUnit = GraphicsUnit.Pixel;
                    g.PageScale = 1f;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

                    float dpiX = g.DpiX > 0 ? g.DpiX : 203f;
                    float dpiY = g.DpiY > 0 ? g.DpiY : 203f;
                    float w = (float)widthBox.Value / 25.4f * dpiX;
                    float h = (float)heightBox.Value / 25.4f * dpiY;

                    // Calibration mark only: inset frame + diagonals + small corner marks.
                    // It deliberately contains no barcode or text to isolate graphics output.
                    using var pen = new Pen(Color.Black, Math.Max(1f, dpiX / 203f));
                    using var font = new Font("Arial", 7f, FontStyle.Bold, GraphicsUnit.Pixel);
                    using var brush = new SolidBrush(Color.Black);

                    g.SetClip(new RectangleF(0, 0, w, h));
                    float inset = Math.Max(3f, dpiX * 0.35f / 25.4f);
                    g.DrawRectangle(pen, inset, inset, w - 2 * inset, h - 2 * inset);
                    g.DrawLine(pen, inset, inset, w - inset, h - inset);
                    g.DrawLine(pen, w - inset, inset, inset, h - inset);
                    float mark = Math.Max(4f, dpiX * 0.8f / 25.4f);
                    g.FillRectangle(brush, inset, inset, mark, mark);
                    g.FillRectangle(brush, w - inset - mark, h - inset - mark, mark, mark);
                }
                finally { g.Restore(state); }
                e.HasMorePages = false;
            };

            doc.Print();
            status.Text = "U dërgua vetëm 1 etiketë kalibrimi 40 × 30 mm.";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Testi u ndal: " + ex.Message,
                "Unionline Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void DrawLabelReportPixels(Graphics g, float pixelW, float pixelH, LabelJob job)
    {
        int w = Math.Max(1, (int)Math.Round(pixelW));
        int h = Math.Max(1, (int)Math.Round(pixelH));
        using var label = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        label.SetResolution(g.DpiX > 0 ? g.DpiX : 203f, g.DpiY > 0 ? g.DpiY : 203f);
        using (var lg = Graphics.FromImage(label))
        {
            lg.Clear(Color.White);
            lg.PageUnit = GraphicsUnit.Millimeter;
            lg.PageScale = 1f;
            lg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            lg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            DrawLabelReport(lg, 40f, 30f, job);
        }
        // The bitmap is already rendered at the printer's native DPI and is copied
        // 1:1 to device pixels. No additional scaling is applied by the printer graphics.
        g.DrawImageUnscaled(label, 0, 0);
    }

    private static int MmToHundredthsInch(double mm) =>
        (int)Math.Round(mm / 25.4 * 100);

    // Report-style layout: all positions and dimensions are in millimetres,
    // matching the way a WinDev Reports page is designed (40 × 30 mm).
    private static void DrawLabelReport(Graphics g, float pageW, float pageH, LabelJob job)
    {
        g.Clear(Color.White);

        float marginX = 1.8f;
        float contentW = pageW - marginX * 2f;

        using var black = new SolidBrush(Color.Black);
        using var linePen = new Pen(Color.Black, 0.18f);

        using var titleFont = new Font("Arial", 2.15f, FontStyle.Bold, GraphicsUnit.Millimeter);
        using var priceFont = new Font("Arial", 4.2f, FontStyle.Bold, GraphicsUnit.Millimeter);
        using var codeFont = new Font("Arial", 2.1f, FontStyle.Bold, GraphicsUnit.Millimeter);
        using var digitsFont = new Font("Arial", 1.55f, FontStyle.Regular, GraphicsUnit.Millimeter);

        var titleRect = new RectangleF(marginX, 1.0f, contentW, 6.3f);
        using var titleFormat = new StringFormat(StringFormatFlags.LineLimit)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(job.Name ?? "", titleFont, black, titleRect, titleFormat);

        g.DrawLine(linePen, marginX, 7.7f, pageW - marginX, 7.7f);

        using var right = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(job.Price ?? "", priceFont, black,
            new RectangleF(marginX, 8.0f, contentW, 7.0f), right);
        g.DrawString(job.Code ?? "", codeFont, black,
            new RectangleF(marginX, 15.0f, contentW, 4.0f), right);

        string barcodeValue = string.IsNullOrWhiteSpace(job.Barcode) ? job.Code : job.Barcode;
        if (!string.IsNullOrWhiteSpace(barcodeValue))
        {
            using var barcode = CreateBarcode(barcodeValue, 480, 100);
            var barcodeRect = new RectangleF(marginX + 1.0f, 19.2f, contentW - 2.0f, 7.0f);
            g.DrawImage(barcode, barcodeRect);

            using var centered = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            g.DrawString(barcodeValue, digitsFont, black,
                new RectangleF(marginX, 26.2f, contentW, 2.4f), centered);
        }
    }
    private static Bitmap CreateBarcode(string value,int width,int height)
    {
        var writer=new BarcodeWriterPixelData{Format=BarcodeFormat.CODE_128,Options=new EncodingOptions{Width=width,Height=height,Margin=3,PureBarcode=true}};
        var pixels=writer.Write(value);var bmp=new Bitmap(pixels.Width,pixels.Height,PixelFormat.Format32bppRgb);
        var data=bmp.LockBits(new Rectangle(0,0,bmp.Width,bmp.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);
        try{Marshal.Copy(pixels.Pixels,0,data.Scan0,pixels.Pixels.Length);}finally{bmp.UnlockBits(data);}
        return bmp;
    }
}
public sealed class PrintRequest{public List<PrintItem>? Items{get;set;}}
public sealed class PrintItem{public string? Name{get;set;}public string? Price{get;set;}public string? Code{get;set;}public string? Barcode{get;set;}public int Quantity{get;set;}=1;}
public sealed class LabelJob{public string Name{get;set;}="";public string Price{get;set;}="";public string Code{get;set;}="";public string Barcode{get;set;}="";public int Quantity{get;set;}=1;}
