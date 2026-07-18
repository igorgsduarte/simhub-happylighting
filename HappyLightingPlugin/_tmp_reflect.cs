using System;
using System.Linq;
using System.Reflection;
using SimHub.Plugins;

class X {
  static void Main(){
    var t = typeof(PluginManager);
    foreach(var m in t.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Where(m=>m.Name.IndexOf("Property", StringComparison.OrdinalIgnoreCase)>=0).OrderBy(m=>m.Name)){
      Console.WriteLine(m.Name+"("+string.Join(",", m.GetParameters().Select(p=>p.ParameterType.Name+" "+p.Name))+") -> "+m.ReturnType.Name);
    }
  }
}
