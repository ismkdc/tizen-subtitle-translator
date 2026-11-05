using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
class Runner {
 static string Root="/tmp/subtitle-probe", Home="/opt/usr/subtitle-ocr";
 static Process overlay;
 static async Task<int> Main(string[] args) {
  int seconds=args.Length>0?int.Parse(args[0]):60;
  var cache=new Dictionary<string,string>(); string previous=""; int empty=0;
  using(var http=new HttpClient()){http.Timeout=TimeSpan.FromSeconds(8);
   http.DefaultRequestHeaders.Add("Authorization","DeepL-Auth-Key "+File.ReadAllText(Home+"/deepl-key").Trim());
   var end=DateTime.UtcNow.AddSeconds(seconds);
   try {while(DateTime.UtcNow<end){
    var watch=Stopwatch.StartNew();
    if(Run("/usr/bin/dotnet",Root+"/FrameProbe.dll",5000)!=0){Console.WriteLine("capture_failed");await Task.Delay(1000);continue;}
    byte[] y=File.ReadAllBytes(Root+"/frame.y");
    // Initial fixed subtitle region for this 720x540 YouTube test.
    int x=150,top=448,w=420,h=78,scale=3;
    byte[] pixels=new byte[w*h*scale*scale]; int bright=0;
    for(int j=0;j<h;j++)for(int i=0;i<w;i++){
     byte value=y[(top+j)*720+x+i]>155?(byte)0:(byte)255;
     if(value==0)bright++;
     for(int dy=0;dy<scale;dy++)for(int dx=0;dx<scale;dx++)pixels[(j*scale+dy)*w*scale+i*scale+dx]=value;
    }
    using(var f=File.Create(Root+"/input.pgm")){var header=Encoding.ASCII.GetBytes("P5\n"+(w*scale)+" "+(h*scale)+"\n255\n");f.Write(header,0,header.Length);f.Write(pixels,0,pixels.Length);}
    string text=Ocr().Trim(); text=System.Text.RegularExpressions.Regex.Replace(text,@"\s+"," ");
    Console.WriteLine("ocr_ms="+watch.ElapsedMilliseconds+" text="+text);
    if(text.Length<8 || bright<30){if(++empty>=3)StopOverlay();await Task.Delay(600);continue;}empty=0;
    if(text==previous){await Task.Delay(600);continue;}
    if(text.Length>240){await Task.Delay(600);continue;}
    string translated;
    if(!cache.TryGetValue(text,out translated)){
     var body=JsonSerializer.Serialize(new {text=new[]{text},source_lang="EN",target_lang="TR"});
     using(var response=await http.PostAsync("https://api-free.deepl.com/v2/translate",new StringContent(body,Encoding.UTF8,"application/json"))){
      if(!response.IsSuccessStatusCode){Console.WriteLine("translation_http="+(int)response.StatusCode);await Task.Delay(1500);continue;}
      using(var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync()))translated=doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString();
     }
     cache[text]=translated;
    }
    previous=text;
    var scene=new {objects=new[]{new {type="subtitle",text=translated,size=34,x=260,width=1400,bottom=850,padding_x=24,padding_y=10,align="center",background_color="#000000FF"}}};
    File.WriteAllText(Root+"/live-scene.json",JsonSerializer.Serialize(scene));StopOverlay();
    overlay=Start("/usr/bin/dotnet",Root+"/OverlayProbe.dll --seconds 7 --scene "+Root+"/live-scene.json");
    Console.WriteLine("translation_ms="+watch.ElapsedMilliseconds+" tr="+translated);
    await Task.Delay(700);
   }}finally{StopOverlay();}
  }return 0;
 }
 static Process Start(string file,string args){var p=new Process();p.StartInfo=new ProcessStartInfo(file,args){UseShellExecute=false};p.Start();return p;}
 static int Run(string file,string args,int timeout){using(var p=Start(file,args)){if(!p.WaitForExit(timeout)){p.Kill();return -1;}return p.ExitCode;}}
 static string Ocr(){
  var p=new Process();p.StartInfo=new ProcessStartInfo("/run/subtitle-ocr/tesseract",Root+"/input.pgm stdout --tessdata-dir /run/subtitle-ocr/tessdata -l eng --psm 6 -c tessedit_create_tsv=1"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  p.Start();var err=p.StandardError.ReadToEndAsync();string text=p.StandardOutput.ReadToEnd();if(!p.WaitForExit(5000)){p.Kill();return "";}p.Dispose();
  var words=new List<string>(); double score=0; int chars=0;
  foreach(string line in text.Split('\n')){var c=line.Split('\t');if(c.Length<12 || c[0]!="5")continue;
   double conf;if(!double.TryParse(c[10],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out conf))continue;
   string word=c[11].Trim();if(conf<45 || word.Length==0)continue;words.Add(word);score+=conf*word.Length;chars+=word.Length;
  }
  if(chars<8 || score/chars<75 || words.Count<2)return "";
  return string.Join(" ",words);
 }
 static void StopOverlay(){if(overlay!=null){try{if(!overlay.HasExited)overlay.Kill();overlay.WaitForExit(2000);}catch{}overlay.Dispose();overlay=null;}}
}
