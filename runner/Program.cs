using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Globalization;
using System.Text.RegularExpressions;

class Runner {
 static string Root="/tmp/subtitle-probe", Home="/opt/usr/subtitle-ocr";
 static string ConfigPath=Home+"/config.json", StatusPath=Root+"/status.json";
 static Process overlay,ocr,neural; static readonly object gate=new object();
 static string pending=null,current="",lastTranslated="";static long generation=0;
 static DateTime visibleUntil=DateTime.MinValue;
 static bool stopping=false;static readonly Dictionary<string,string> cache=new Dictionary<string,string>();

 static int lastFrameW=0,lastFrameH=0;
 static string lastOcr="",lastTr="",lastError="",lastEngine="";
 static int lastConf=0;
 static long lastOcrMs=0,lastTrMs=0;
 static readonly object statusGate=new object();

 class Cfg {
  public double RX=0.208,RY=0.861,RW=0.583,RH=0.107;
  public int Threshold=155,MinConfidence=55,NeuralMinConfidence=70;
  public int Size=34,Bottom=1000; public double Opacity=1; public string Color="#FFFFFF";
  public string Source="EN",Target="TR";
  public string Endpoint="";
  public bool Fallback=true;
 }

 static Cfg LoadConfig(){
  var c=new Cfg();
  try{
   using(var doc=JsonDocument.Parse(File.ReadAllText(ConfigPath))){
    var r=doc.RootElement;
    JsonElement e;
    if(r.TryGetProperty("region",out e)){
     c.RX=GetD(e,"x",c.RX);c.RY=GetD(e,"y",c.RY);c.RW=GetD(e,"w",c.RW);c.RH=GetD(e,"h",c.RH);
    }
    if(r.TryGetProperty("ocr",out e)){
     c.Threshold=GetI(e,"threshold",c.Threshold);c.MinConfidence=GetI(e,"minConfidence",c.MinConfidence);c.NeuralMinConfidence=GetI(e,"neuralMinConfidence",c.NeuralMinConfidence);
     JsonElement fb; if(e.ValueKind==JsonValueKind.Object&&e.TryGetProperty("fallback",out fb)) c.Fallback = fb.ValueKind!=JsonValueKind.False;
    }
    if(r.TryGetProperty("style",out e)){
     c.Size=GetI(e,"size",c.Size);c.Bottom=GetI(e,"bottom",c.Bottom);
     c.Opacity=GetD(e,"opacity",c.Opacity);
     string col=GetS(e,"color",c.Color); if(Regex.IsMatch(col,"^#[0-9A-Fa-f]{6}$"))c.Color=col.ToUpperInvariant();
    }
    if(r.TryGetProperty("translate",out e)){
     c.Source=GetS(e,"source",c.Source);c.Target=GetS(e,"target",c.Target);c.Endpoint=GetS(e,"endpoint",c.Endpoint);
    }
   }
  }catch{}
  c.RW=Math.Max(0.05,Math.Min(1,c.RW)); c.RH=Math.Max(0.03,Math.Min(0.8,c.RH));
  c.RX=Math.Max(0,Math.Min(1-c.RW,c.RX)); c.RY=Math.Max(0,Math.Min(1-c.RH,c.RY));
  c.Size=Math.Max(20,Math.Min(64,c.Size)); c.Bottom=Math.Max(60,Math.Min(1040,c.Bottom));
  c.Opacity=Math.Max(0.2,Math.Min(1,c.Opacity));
  c.Threshold=Math.Max(80,Math.Min(240,c.Threshold));
  return c;
 }

 static double GetD(JsonElement e,string name,double d){JsonElement p;if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty(name,out p)||p.ValueKind!=JsonValueKind.Number)return d;double v;return p.TryGetDouble(out v)&&!double.IsNaN(v)&&!double.IsInfinity(v)?v:d;}
 static int GetI(JsonElement e,string name,int d){JsonElement p;if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty(name,out p)||p.ValueKind!=JsonValueKind.Number)return d;int v;return p.TryGetInt32(out v)?v:d;}
 static string GetS(JsonElement e,string name,string d){JsonElement p;if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty(name,out p)||p.ValueKind!=JsonValueKind.String)return d;string v=p.GetString();return string.IsNullOrEmpty(v)?d:v;}

 static string Alpha(double opacity){
  var a=(int)Math.Round(Math.Max(0,Math.Min(1,opacity))*255);
  return a.ToString("X2",CultureInfo.InvariantCulture);
 }

 static void WriteStatus(){
  lock(statusGate){
   try{
    var st=new {
     running=!stopping,
     updated=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
     capture=lastFrameW>0?new{w=lastFrameW,h=lastFrameH}:(object)null,
     last=new{ocr=lastOcr,tr=lastTr,ocr_ms=lastOcrMs,tr_ms=lastTrMs,engine=lastEngine,conf=lastConf},
     error=lastError.Length>0?lastError:(string)null
    };
    string tmp=StatusPath+".new";
    File.WriteAllText(tmp,JsonSerializer.Serialize(st));
    File.Move(tmp,StatusPath,true);
   }catch{}
  }
 }

 static string OcrTesseract(byte[] gray,int w,int h,Cfg cfg,bool multi,out int confidence){
  confidence=0;
  const int scale=2;
  int ow=w*scale, oh=h*scale;
  var pix=new byte[ow*oh];
  for(int j=0;j<h;j++){
   for(int i=0;i<w;i++){
    byte v=(byte)(255-gray[j*w+i]);
    for(int dy=0;dy<scale;dy++){
     int row=(j*scale+dy)*ow+i*scale;
     for(int dx=0;dx<scale;dx++)pix[row+dx]=v;
    }
   }
  }
  string pgm=Root+"/input.pgm";
  using(var f=File.Create(pgm)){
   var header=Encoding.ASCII.GetBytes("P5\n"+ow+" "+oh+"\n255\n");
   f.Write(header,0,header.Length); f.Write(pix,0,pix.Length);
  }
  try{
   string args=pgm+" stdout --tessdata-dir /run/subtitle-ocr/tessdata -l eng --psm "+(multi?6:7)+" -c tessedit_create_tsv=1";
   var psi=new ProcessStartInfo("/run/subtitle-ocr/tesseract",args){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
   using(var p=Process.Start(psi)){
    string tsv=p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    if(!p.WaitForExit(6000)){try{p.Kill();}catch{}return "";}
    var words=new List<string>(); double score=0; int chars=0;
    foreach(string line in tsv.Split('\n')){
     var c=line.Split('\t');
     if(c.Length<12||c[0]!="5")continue;
     double conf;
     if(!double.TryParse(c[10],NumberStyles.Float,CultureInfo.InvariantCulture,out conf))continue;
     string word=c[11].Trim();
     if(conf<cfg.MinConfidence||word.Length==0)continue;
     words.Add(word);score+=conf*word.Length;chars+=word.Length;
    }
    if(chars<3||words.Count<1)return "";
    confidence=(int)Math.Round(score/chars);
    return Regex.Replace(string.Join(" ",words),@"\s+"," ").Trim();
   }
  }catch(Exception e){lastError="ocr_"+e.GetType().Name;return "";}
 }

 static string OcrNeural(byte[] gray,int w,int h,Cfg cfg,out int confidence){
  confidence=0;
  string pgm=Root+"/neural.pgm";
  try{
   using(var f=File.Create(pgm)){
    var header=Encoding.ASCII.GetBytes("P5\n"+w+" "+h+"\n255\n");
    f.Write(header,0,header.Length); f.Write(gray,0,w*h);
   }
   neural.StandardInput.WriteLine(pgm);
   neural.StandardInput.Flush();
   string line;
   do{
    line=neural.StandardOutput.ReadLine();
    if(line==null)return "";
   }while(!line.StartsWith("RESULT\t"));
   var parts=line.Split(new[]{'\t'},4);
   if(parts.Length<4)return "";
   int conf;
   if(!int.TryParse(parts[1],out conf))return "";
   confidence=conf;
   if(conf<cfg.NeuralMinConfidence)return "";
   return Regex.Replace(parts[3],@"\s+"," ").Trim();
  }catch(Exception e){lastError="neural_"+e.GetType().Name;try{if(neural!=null)neural.Kill();}catch{}return "";}
 }

 static void StartOcr(){
  try{ if(ocr!=null){try{ocr.Kill();}catch{} ocr.Dispose();} }catch{}
  ocr=Start("/run/subtitle-ocr/ocr-worker","/run/subtitle-ocr/tessdata",true);
  ocr.ErrorDataReceived+=(s,e)=>{};
  ocr.BeginErrorReadLine();
  string r; do{ r=ocr.StandardOutput.ReadLine(); if(r==null) break; }while(r!="READY");
 }

 static void StartNeural(){
  try{ if(neural!=null){try{neural.Kill();}catch{} neural.Dispose();} }catch{}
  neural=Start("/run/subtitle-ocr/neural-worker","/run/subtitle-ocr/rec.mnn /run/subtitle-ocr/rec-keys.txt",true);
  neural.ErrorDataReceived+=(s,e)=>{};
  neural.BeginErrorReadLine();
  string r; do{ r=neural.StandardOutput.ReadLine(); if(r==null) break; }while(r!="READY");
 }

 static async Task<int> Main(string[] args){
  int seconds=args.Length>0?int.Parse(args[0]):0;
  Directory.CreateDirectory(Root);
  var cfg=LoadConfig();
  try{
   Scene("",cfg);
   StartNeural();
   int overlaySeconds=seconds>0?seconds+5:0;
   overlay=Start("/usr/bin/dotnet","/opt/usr/subtitle-ocr/OverlayProbe.dll --seconds "+overlaySeconds+" --scene "+Root+"/live-scene.json",false);
   WriteStatus();

   Task translation=TranslateLoop();
   string candidate="",lastRead="";int repeated=0,empty=0;byte[] previousMask=null;DateTime lastOcrAt=DateTime.MinValue;
   var end=seconds>0?DateTime.UtcNow.AddSeconds(seconds):DateTime.MaxValue;
   var lastStatusWrite=DateTime.UtcNow;

   while(DateTime.UtcNow<end && !stopping){
    var timer=Stopwatch.StartNew();
    if(PpiDump.Capture()!=0){
     lastError="capture_failed";
     WriteStatus();
     await Task.Delay(500);
     continue;
    }
    int fw=PpiDump.LastWidth>0?PpiDump.LastWidth:720;
    int fh=PpiDump.LastHeight>0?PpiDump.LastHeight:540;
    lastFrameW=fw; lastFrameH=fh;

    byte[] y=File.ReadAllBytes(Root+"/frame.y");
    int stride=PpiDump.LastStride>0?PpiDump.LastStride:fw;
    if(stride<fw)stride=fw;
    int maxRows=y.Length/stride;
    if(maxRows<1){await Task.Delay(200);continue;}

    cfg=LoadConfig();

    int x=(int)Math.Round(cfg.RX*fw);
    int top=(int)Math.Round(cfg.RY*fh);
    int w=(int)Math.Round(cfg.RW*fw);
    int h=(int)Math.Round(cfg.RH*fh);
    if(x<0)x=0; if(top<0)top=0;
    int maxY=Math.Min(fh,maxRows);
    if(x>fw-1)x=fw-1; if(top>maxY-1)top=maxY-1;
    if(x+w>fw)w=fw-x;
    if(x+w>stride)w=stride-x;
    if(top+h>maxY)h=maxY-top;
    if(w<8||h<8){await Task.Delay(200);continue;}

    var gray=new byte[w*h];
    var mask=new byte[w*h]; int changed=0;
    if(previousMask!=null && previousMask.Length!=mask.Length){previousMask=null;lastRead="";}
    for(int j=0;j<h;j++)for(int i=0;i<w;i++){
     byte raw=y[(top+j)*stride+x+i];
     int k=j*w+i; gray[k]=raw;
     byte v=raw>cfg.Threshold?(byte)0:(byte)255;
     mask[k]=v; if(previousMask==null||previousMask[k]!=v)changed++;
    }

    // Crop to the bright subtitle line so the wide, empty band does not
    // confuse the OCR engines (and the HUD on the right is excluded).
    int bright=Math.Max(190,cfg.Threshold);
    int sx0=(int)(w*0.03), sx1=(int)(w*0.97);
    var rowCount=new int[h];
    int maxCount=0;
    for(int j=0;j<h;j++){
     int rc=0;
     for(int i=sx0;i<sx1;i++) if(gray[j*w+i]>bright) rc++;
     rowCount[j]=rc; if(rc>maxCount) maxCount=rc;
    }
    int bandTop=-1,bandBottom=-1;
    int need=Math.Max(8,maxCount/3);
    int bandWidth=sx1-sx0;
    long bestScore=0;
    int jj=0;
    while(jj<h){
     if(rowCount[jj]>=need){
      int bandStart=jj;
      while(jj<h && rowCount[jj]>=need) jj++;
      int bandEnd=jj-1;
      if(bandEnd-bandStart+1>=8){
       int bmax=0; for(int k=bandStart;k<=bandEnd;k++) if(rowCount[k]>bmax)bmax=rowCount[k];
       if(bmax<=bandWidth*0.70){
        int xmin=sx1,xmax=sx0;
        for(int j2=bandStart;j2<=bandEnd;j2++)for(int i2=sx0;i2<sx1;i2++) if(gray[j2*w+i2]>bright){if(i2<xmin)xmin=i2;if(i2>xmax)xmax=i2;}
        long score=(long)(xmax>=xmin?xmax-xmin+1:0)*(bandEnd-bandStart+1);
        if(score>bestScore){bestScore=score;bandTop=bandStart;bandBottom=bandEnd;}
       }
      }
     } else jj++;
    }
    int bx0=w,by0=h,bx1=-1,by1=-1;
    bool multi=false;
    if(bandBottom>=0){
     int probe=bandBottom+1, gap=0;
     while(probe<h && rowCount[probe]<need && gap<16){probe++;gap++;}
     if(probe<h && rowCount[probe]>=need) multi=true;
     bandTop=Math.Max(0,bandTop-(multi?8:4));
     bandBottom=Math.Min(h-1,bandBottom+(multi?40:10));
     for(int j=bandTop;j<=bandBottom;j++)for(int i=sx0;i<sx1;i++){
      if(gray[j*w+i]>bright){if(i<bx0)bx0=i;if(i>bx1)bx1=i;if(j<by0)by0=j;if(j>by1)by1=j;}
     }
    }
    int cw=0,ch=0; byte[] crop=null;
    if(bx1>=bx0&&by1>=by0&&(bx1-bx0)>=12&&(by1-by0)>=5){
     int xpad=Math.Max(32,(bx1-bx0)/3);
     int ypad=Math.Max(12,(by1-by0)/2);
     bx0=Math.Max(0,bx0-xpad); by0=Math.Max(0,by0-ypad);
     bx1=Math.Min(w-1,bx1+xpad); by1=Math.Min(h-1,by1+ypad);
     cw=bx1-bx0+1; ch=by1-by0+1; crop=new byte[cw*ch];
     for(int j=0;j<ch;j++)for(int i=0;i<cw;i++)crop[j*cw+i]=gray[(by0+j)*w+bx0+i];
    }

    string text="";int confidence=0;
    if(previousMask!=null&&changed<mask.Length/500&&(DateTime.UtcNow-lastOcrAt).TotalMilliseconds<700){
     text=lastRead; confidence=100;
    }else{
     lastOcrAt=DateTime.UtcNow;
     string engine="";
     text="";
     if(crop!=null){
      if(neural==null||neural.HasExited)StartNeural();
      if(multi){
       engine="tesseract";
       text=OcrTesseract(crop,cw,ch,cfg,multi,out confidence);
       if(!Valid(text) && cfg.Fallback){
        engine="neural";
        text=OcrNeural(crop,cw,ch,cfg,out confidence);
       }
      }else{
       engine="tesseract";
       text=OcrTesseract(crop,cw,ch,cfg,multi,out confidence);
       if(!Valid(text) && cfg.Fallback){
        engine="neural";
        text=OcrNeural(crop,cw,ch,cfg,out confidence);
       }
      }
      text=CleanOcr(text);
      if(!Valid(text)){text="";engine="";}
     }
     lastEngine=engine;
     lastConf=text.Length>0?confidence:0;
     lastRead=text;previousMask=mask;
    }

    lastOcr=text; lastOcrMs=timer.ElapsedMilliseconds;
    if(text.Length==0){
     candidate="";repeated=0;
     if(++empty>=8){
      lock(gate){
       if(current!=""){current="";generation++;pending=null;}
       if(DateTime.UtcNow>=visibleUntil)Scene("",cfg);
      }
     }
    }else{
     empty=0;
     if(Similar(candidate,text)>=0.88)repeated++;else{candidate=text;repeated=1;}
     if(repeated>=1)lock(gate){
      if(Similar(current,text)<0.88){current=text;generation++;pending=text;}
      else if(current!="") visibleUntil=DateTime.UtcNow.AddSeconds(4);
     }
    }

    if((DateTime.UtcNow-lastStatusWrite).TotalMilliseconds>800){lastStatusWrite=DateTime.UtcNow;WriteStatus();}
    int rest=Math.Max(60,350-(int)timer.ElapsedMilliseconds);
    await Task.Delay(rest);
   }

   lock(gate){stopping=true;pending=null;generation++;}
   await translation;
  }catch(Exception e){
   lastError=e.GetType().Name+":"+e.Message;
   Console.WriteLine("runner_error="+e.ToString());
   WriteStatus();
   return 1;
  }finally{
   lock(gate){stopping=true;}
   Stop(overlay);
   if(ocr!=null){try{ocr.StandardInput.WriteLine("QUIT");if(!ocr.WaitForExit(1500))ocr.Kill();}catch{}ocr.Dispose();}
   if(neural!=null){try{neural.StandardInput.WriteLine("QUIT");if(!neural.WaitForExit(1500))neural.Kill();}catch{}neural.Dispose();}
   lastError="";
   WriteStatus();
   Console.WriteLine("runner_done");
  }
  return 0;
 }

 static async Task TranslateLoop(){
  using(var http=new HttpClient()){
   http.Timeout=TimeSpan.FromSeconds(5);
   string key="";
   try{key=File.ReadAllText(Home+"/deepl-key").Trim();}catch{key="";}
   if(key.Length>0)http.DefaultRequestHeaders.Add("Authorization","DeepL-Auth-Key "+key);

   while(true){
    string text;long version;var cfg=LoadConfig();
    lock(gate){
     if(stopping)return;
     text=pending;pending=null;version=generation;
    }
    if(text==null){await Task.Delay(40);continue;}
    var timer=Stopwatch.StartNew();string tr;
    string cacheKey=cfg.Target+"|"+text;
    try{
     if(!cache.TryGetValue(cacheKey,out tr)){
      bool ok=false;
      if(cfg.Endpoint.Length>0){
       try{
        string lbody=JsonSerializer.Serialize(new{text=text,source=cfg.Source,target=cfg.Target});
        using(var response=await http.PostAsync(cfg.Endpoint,new StringContent(lbody,Encoding.UTF8,"application/json"))){
         if(response.IsSuccessStatusCode){
          using(var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync())){
           JsonElement je;
           if(doc.RootElement.TryGetProperty("tr",out je)&&je.ValueKind==JsonValueKind.String){
            tr=je.GetString();
            ok=!string.IsNullOrWhiteSpace(tr);
           }
          }
         }
        }
       }catch(Exception le){ lastError="local_"+le.GetType().Name; }
      }
      if(!ok){
       if(key.Length==0)throw new Exception("deepl_key_missing");
       string body=JsonSerializer.Serialize(new{text=new[]{text},source_lang=cfg.Source,target_lang=cfg.Target});
       using(var response=await http.PostAsync("https://api-free.deepl.com/v2/translate",new StringContent(body,Encoding.UTF8,"application/json"))){
        if(!response.IsSuccessStatusCode){
         lastError="translation_http_"+(int)response.StatusCode;
         lock(gate){if(version==generation)current="";}
         await Task.Delay(700);continue;
        }
        using(var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
         tr=doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString();
       }
      }
      if(cache.Count>300)cache.Clear();
      cache[cacheKey]=tr;
     }
     lock(gate){
      if(!stopping){
       Scene(tr,cfg);
       visibleUntil=DateTime.UtcNow.AddSeconds(8);
       lastTranslated=text;lastTr=tr;lastTrMs=timer.ElapsedMilliseconds;lastError="";
       Console.WriteLine("translated_ms="+timer.ElapsedMilliseconds+" en="+text+" tr="+tr);
      }
     }
    }catch(Exception e){
     lastError="translation_"+e.GetType().Name;
     Console.WriteLine("translation_error="+e.GetType().Name);
     lock(gate){if(version==generation)current="";}
     await Task.Delay(700);
    }
   }
  }
 }

 static string CleanOcr(string s){
  if(string.IsNullOrEmpty(s))return "";
  s=Regex.Replace(s,"\\s+"," ").Trim();
  int last=Math.Max(s.LastIndexOf('.'),Math.Max(s.LastIndexOf('?'),s.LastIndexOf('!')));
  if(last>0&&last<s.Length-1)s=s.Substring(0,last+1).Trim();
  var m=Regex.Match(s,@"((?:[A-Z][\w'’.-]*\s+)*[A-Z][\w'’.-]*):\s*(.+)$");
  if(m.Success)return m.Groups[1].Value.Trim()+": "+m.Groups[2].Value.Trim();
  return s;
 }

 static bool Valid(string s){
  if(s.Length<2||s.Length>240)return false;
  int letters=0,bad=0;
  foreach(char c in s){
   if(char.IsLetter(c))letters++;
   if(!char.IsLetterOrDigit(c)&&!char.IsWhiteSpace(c)&&".,!?'-\"’“”:;".IndexOf(c)<0)bad++;
  }
  if(letters<6)return false;
  var words=s.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
  if(words.Length<2)return false;
  int singles=0;
  foreach(var w in words) if(w.Length==1 && w!="I" && w!="A" && w!="a") singles++;
  if(singles>0)return false;
  return bad<=2&&letters>=s.Length/2;
 }

 static double Similar(string a,string b){
  a=Regex.Replace(a.ToLowerInvariant(),@"[^a-z0-9]","");
  b=Regex.Replace(b.ToLowerInvariant(),@"[^a-z0-9]","");
  if(a==b)return 1;if(a.Length==0||b.Length==0)return 0;
  int[] row=new int[b.Length+1];
  for(int j=0;j<=b.Length;j++)row[j]=j;
  for(int i=1;i<=a.Length;i++){
   int prev=row[0];row[0]=i;
   for(int j=1;j<=b.Length;j++){
    int old=row[j];row[j]=Math.Min(Math.Min(row[j]+1,row[j-1]+1),prev+(a[i-1]==b[j-1]?0:1));prev=old;
   }
  }
  return 1.0-(double)row[b.Length]/Math.Max(a.Length,b.Length);
 }

 static void Scene(string text,Cfg cfg){
  string path=Root+"/live-scene.json";
  string data="";
  if(text!=""){
   data=JsonSerializer.Serialize(new{objects=new[]{
    new{type="subtitle",text=text,size=cfg.Size,x=260,width=1400,bottom=cfg.Bottom,
        padding_x=24,padding_y=10,align="center",
        color=cfg.Color,background_color="#000000"+Alpha(cfg.Opacity)}
   }});
  }
  string tmp=path+".new";
  File.WriteAllText(tmp,data);
  File.Move(tmp,path,true);
 }

 static Process Start(string f,string a,bool redirect){
  var p=new Process();
  p.StartInfo=new ProcessStartInfo(f,a){UseShellExecute=false,RedirectStandardInput=redirect,RedirectStandardOutput=redirect,RedirectStandardError=redirect};
  p.Start();return p;
 }

 static void Stop(Process p){
  if(p==null)return;
  try{if(!p.HasExited)p.Kill();p.WaitForExit(2000);}catch{}
  p.Dispose();
 }
}
