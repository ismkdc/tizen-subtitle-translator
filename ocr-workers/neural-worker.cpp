#include <MNN/Interpreter.hpp>
#include <MNN/Tensor.hpp>
#include <iostream>
#include <fstream>
#include <string>
#include <vector>
#include <chrono>
#include <cmath>
#include <algorithm>
int main(int argc,char**argv){
 std::unique_ptr<MNN::Interpreter> net(MNN::Interpreter::createFromFile(argv[1]));if(!net)return 2;
 MNN::ScheduleConfig cfg;cfg.numThread=2;cfg.type=MNN_FORWARD_CPU;auto session=net->createSession(cfg);if(!session)return 3;
 std::vector<std::string> chars={""};std::ifstream dict(argv[2]);std::string word;while(std::getline(dict,word))chars.push_back(word);chars.push_back(" ");
 std::cout<<"READY"<<std::endl;std::string path;
 while(std::getline(std::cin,path)){
  if(path=="QUIT")break;auto start=std::chrono::steady_clock::now();std::ifstream f(path,std::ios::binary);std::string magic;int w,h,max;f>>magic>>w>>h>>max;f.get();if(!f||magic!="P5"||w<1||h<1||w>4096||h>1024){std::cout<<"RESULT\t0\t0\t"<<std::endl;continue;}
  std::vector<unsigned char> img(w*h);f.read((char*)img.data(),img.size());int ow=std::max(32,(int)std::ceil(48.0*w/h));ow=std::min(ow,1536);
  auto input=net->getSessionInput(session,nullptr);net->resizeTensor(input,{1,3,48,ow});net->resizeSession(session);MNN::Tensor host(input,MNN::Tensor::CAFFE);float* data=host.host<float>();
  for(int y=0;y<48;y++)for(int x=0;x<ow;x++){
   float sx=(x+.5f)*w/ow-.5f,sy=(y+.5f)*h/48-.5f;int x0=std::max(0,std::min(w-1,(int)std::floor(sx))),y0=std::max(0,std::min(h-1,(int)std::floor(sy)));int x1=std::min(w-1,x0+1),y1=std::min(h-1,y0+1);float ax=std::max(0.f,sx-x0),ay=std::max(0.f,sy-y0);
   float v=(1-ay)*((1-ax)*img[y0*w+x0]+ax*img[y0*w+x1])+ay*((1-ax)*img[y1*w+x0]+ax*img[y1*w+x1]);v=v/127.5f-1;
   for(int c=0;c<3;c++)data[c*48*ow+y*ow+x]=v;
  }
  input->copyFromHostTensor(&host);net->runSession(session);auto output=net->getSessionOutput(session,nullptr);MNN::Tensor out(output,MNN::Tensor::CAFFE);output->copyToHostTensor(&out);auto shape=out.shape();int classes=shape.back(),steps=out.elementSize()/classes;float* scores=out.host<float>();int last=-1,count=0;double sum=0;std::string result;
  for(int t=0;t<steps;t++){float* p=scores+t*classes;int k=std::max_element(p,p+classes)-p;if(k!=0&&k!=last&&k<(int)chars.size()){result+=chars[k];sum+=p[k];count++;}last=k;}
  for(char&c:result)if(c=='\t'||c=='\n'||c=='\r')c=' ';
  auto ms=std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-start).count();std::cout<<"RESULT\t"<<(count?(int)(100*sum/count):0)<<"\t"<<ms<<"\t"<<result<<std::endl;
 }
}
