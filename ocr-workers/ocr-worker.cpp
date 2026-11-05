#include <tesseract/baseapi.h>
#include <leptonica/allheaders.h>
#include <iostream>
#include <string>
#include <chrono>
int main(int argc,char**argv){
 tesseract::TessBaseAPI api;
 if(api.Init(argc>1?argv[1]:"/run/subtitle-ocr/tessdata","eng",tesseract::OEM_LSTM_ONLY))return 2;
 api.SetPageSegMode(tesseract::PSM_SINGLE_BLOCK);
 std::cout<<"READY"<<std::endl;
 std::string path;
 while(std::getline(std::cin,path)){
  if(path=="QUIT")break;
  auto start=std::chrono::steady_clock::now();
  PIX* pix=pixRead(path.c_str());
  if(!pix){std::cout<<"RESULT\t0\t0\t"<<std::endl;continue;}
  api.SetImage(pix);api.Recognize(nullptr);
  char* text=api.GetUTF8Text();std::string s=text?text:"";delete[] text;
  for(char &c:s)if(c=='\n'||c=='\r'||c=='\t')c=' ';
  auto ms=std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-start).count();
  std::cout<<"RESULT\t"<<api.MeanTextConf()<<"\t"<<ms<<"\t"<<s<<std::endl;
  api.Clear();pixDestroy(&pix);
 }
 api.End();return 0;
}
