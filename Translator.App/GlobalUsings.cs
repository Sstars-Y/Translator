// 显式全局 using。App 项目关掉了 ImplicitUsings，
// 因为 WinForms 会注入 System.Windows.Forms / System.Drawing，
// 与 WPF 的 Application / Point / TextBlock 等类型撞名。

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
