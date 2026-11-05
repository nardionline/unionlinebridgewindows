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
    private readonly Button testButton = new() { Text = "Printo etiketë test", AutoSize = true };
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
        var buttons=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill }; buttons.Controls.Add(testButton); buttons.Controls.Add(startButton); AddRow(root,5,"Veprime",buttons);
        root.Controls.Add(status,0,6); root.SetColumnSpan(status,2);
        var note=new Label { AutoSize=true,MaximumSize=new Size(510,0),ForeColor=Color.DimGray,
            Text="Pa çelës lokal. Dëgjon vetëm në 127.0.0.1. Barkodi gjenerohet grafikisht Code 128. GAP-i fizik konfigurohet te driver-i." };
        root.Controls.Add(note,0,7); root.SetColumnSpan(note,2);
        LoadPrinters();
        testButton.Click+=(_,_)=>PrintJobs(new(){new LabelJob{Name="Unionline Test",Price="4,990 L",Code="TR-10025",Barcode="123456789012",Quantity=1}});
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
            {await Reply(ctx,200,new{ok=true,service="Unionline Print Bridge",version="1.2.0",printer=printerBox.SelectedItem?.ToString()??""});return;}
            if(ctx.Request.HttpMethod!="POST"||ctx.Request.Url?.AbsolutePath!="/print"){await Reply(ctx,404,new{ok=false,error="Not found"});return;}
            using var reader=new StreamReader(ctx.Request.InputStream,ctx.Request.ContentEncoding);
            var req=JsonSerializer.Deserialize<PrintRequest>(await reader.ReadToEndAsync(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(req?.Items==null||req.Items.Count==0){await Reply(ctx,400,new{ok=false,error="Lista është bosh"});return;}
            var jobs=req.Items.Select(i=>new LabelJob{Name=i.Name??"",Price=i.Price??"",Code=i.Code??"",Barcode=string.IsNullOrWhiteSpace(i.Barcode)?(i.Code??""):i.Barcode!,Quantity=Math.Clamp(i.Quantity,1,500)}).ToList();
            if(IsDisposed){await Reply(ctx,503,new{ok=false,error="Bridge is closing"});return;}
            BeginInvoke(()=>PrintJobs(jobs)); await Reply(ctx,200,new{ok=true,accepted=jobs.Sum(x=>x.Quantity),message="Print job accepted"});
        }
        catch(Exception ex){try{await Reply(ctx,500,new{ok=false,error=ex.Message});}catch{}}
    }
    private static async Task Reply(HttpListenerContext ctx,int code,object data)
    {var bytes=JsonSerializer.SerializeToUtf8Bytes(data);ctx.Response.StatusCode=code;ctx.Response.ContentType="application/json; charset=utf-8";ctx.Response.ContentLength64=bytes.Length;await ctx.Response.OutputStream.WriteAsync(bytes);ctx.Response.Close();}

    private void PrintJobs(List<LabelJob> jobs)
    {
        try
        {
            if(printerBox.SelectedItem==null)throw new InvalidOperationException("Zgjidh printerin.");
            using var doc=new PrintDocument();doc.PrinterSettings.PrinterName=printerBox.SelectedItem.ToString()!;
            if(!doc.PrinterSettings.IsValid)throw new InvalidOperationException("Printeri nuk është i disponueshëm.");
            int w=MmToHundredthsInch((double)widthBox.Value),h=MmToHundredthsInch((double)heightBox.Value);
            doc.DefaultPageSettings.PaperSize=new PaperSize($"Unionline_{widthBox.Value}x{heightBox.Value}mm",w,h);
            doc.DefaultPageSettings.Margins=new Margins(0,0,0,0);doc.OriginAtMargins=false;doc.PrintController=new StandardPrintController();
            var pages=jobs.SelectMany(j=>Enumerable.Repeat(j,j.Quantity)).ToList();int page=0;
            doc.PrintPage+=(_,e)=>{DrawLabel(e.Graphics!,e.PageBounds,pages[page++]);e.HasMorePages=page<pages.Count;};
            doc.Print();status.Text=$"U dërguan {pages.Count} etiketa te {doc.PrinterSettings.PrinterName}. Gap: {gapBox.Value} mm (driver).";
        }
        catch(Exception ex){MessageBox.Show("Gabim printimi: "+ex.Message,"Unionline Print Bridge",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
    private static int MmToHundredthsInch(double mm)=>(int)Math.Round(mm/25.4*100);
    private static void DrawLabel(Graphics g,Rectangle page,LabelJob j)
    {
        // Use PageBounds directly; MarginBounds can be empty on some thermal printer drivers.
        g.PageUnit=GraphicsUnit.Display;g.PageScale=1f;
        float x=page.Left+3.5f,y=page.Top+3f,w=Math.Max(10,page.Width-7f),h=Math.Max(10,page.Height-6f);
        using var black=new SolidBrush(Color.Black);using var titleFont=new Font("Arial",7.2f,FontStyle.Bold,GraphicsUnit.Point);
        using var priceFont=new Font("Arial",15f,FontStyle.Bold,GraphicsUnit.Point);using var codeFont=new Font("Arial",8.2f,FontStyle.Bold,GraphicsUnit.Point);
        using var digitFont=new Font("Arial",6f);using var pen=new Pen(Color.Black,0.65f);
        var titleRect=new RectangleF(x,y,w,h*0.25f);
        using var titleFmt=new StringFormat(StringFormatFlags.LineLimit){Alignment=StringAlignment.Near,LineAlignment=StringAlignment.Near,Trimming=StringTrimming.EllipsisCharacter};
        g.DrawString(j.Name??"",titleFont,black,titleRect,titleFmt);
        float lineY=y+h*0.265f;g.DrawLine(pen,x,lineY,x+w,lineY);
        using var right=new StringFormat{Alignment=StringAlignment.Far,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter};
        g.DrawString(j.Price??"",priceFont,black,new RectangleF(x,lineY+h*.025f,w,h*.29f),right);
        g.DrawString(j.Code??"",codeFont,black,new RectangleF(x,lineY+h*.315f,w,h*.16f),right);
        string value=string.IsNullOrWhiteSpace(j.Barcode)?j.Code:j.Barcode;
        if(!string.IsNullOrWhiteSpace(value))
        {
            float by=y+h*.61f,bh=h*.31f;
            using var barcode=CreateBarcode(value,Math.Max(180,(int)(w*4)),Math.Max(35,(int)(bh*4)));
            float tw=Math.Min(w*.92f,barcode.Width/4f),th=Math.Min(bh,barcode.Height/4f);
            g.DrawImage(barcode,new RectangleF(x+(w-tw)/2f,by,tw,th));
            using var center=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter};
            g.DrawString(value,digitFont,black,new RectangleF(x,y+h*.925f,w,h*.07f),center);
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
