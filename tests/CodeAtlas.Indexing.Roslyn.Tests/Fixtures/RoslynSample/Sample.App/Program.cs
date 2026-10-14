using Sample.Core;

// 顶级语句：编译器生成 Program.<Main>$，分析器要给它一个符号，这里的调用才有归属
var repo = new Repo();
repo.Find(1);
