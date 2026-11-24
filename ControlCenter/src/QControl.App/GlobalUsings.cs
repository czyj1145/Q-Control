// ============================================================================
// 显式声明全局 using —— 不是为了风格，是为了 **UseWPF 的标记编译**。
//
// 打开 <UseWPF> 之后，SDK 会先拿一份临时工程（obj 里的 *_wpftmp.csproj）把本项目的
// .cs 再编译一遍，供 XAML 编译器反射类型用。那一趟**不会**带上 ImplicitUsings 生成的
// 全局 using（它引的是主工程 obj 下的 GlobalUsings.g.cs，临时工程用的是另一个中间目录），
// 于是所有依赖隐式 System.IO 的文件都会在 _wpftmp 里报"找不到 File / Path / StreamWriter" ——
// 而在主工程里编译是通过的，报错看着莫名其妙。
//
// 把默认那几条全局 using 写成**源文件**，两趟编译都会带上，问题从根上没了。
// 以后要加新的全局 using 也加在这里，别去 csproj 里配 <Using Include="..."/>（同样读不到）。
// ============================================================================

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
