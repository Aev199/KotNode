using System;
using System.IO;
using System.Linq;
using KotNode.Core;

class Program
{
 static int checks;
 static void Check(bool x, string name) { if (!x) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
 static void Reject(Action a, string name) { bool rejected = false; try { a(); } catch (InvalidDataException) { rejected = true; } catch (IOException) { rejected = true; } Check(rejected, name); }
 static void Main(string[] args)
 {
  var p = new Project { Code = "TEST", Name = "Котлован" }; var m = new NodeMark(); p.Marks.Add(m);
  var g = Splice.Calculate(m.Parameters);
  Check(Math.Abs(Splice.Spacing(m.Parameters, g.Angle) - 50) < 1e-8, "clear spacing solved");
  double exact = g.Angle / 2 * (g.OuterRadius*g.OuterRadius-g.InnerRadius*g.InnerRadius)*950*7850/1e9;
  Check(Math.Abs(g.PlateMass-exact)<1e-10, "sector volume and kg conversion");
  Check(p.Steel().Count == 0, "drawing mark alone adds no steel");
  p.Uses.Add(new Use { MarkId=m.Id, Count=24, Included=true, Location="Ярус 1" });
  p.Uses.Add(new Use { MarkId=m.Id, Count=10, Included=false, Location="Вариант" });
  Check(p.Steel().Single().DetailCount == 96, "draft excluded and quantity multiplied");
  p.Uses.Add(new Use { MarkId=m.Id, Count=6, Included=true, Location="Ярус 2" });
  Check(p.Steel().Single().NodeCount==30 && p.Steel("Ярус 1").Single().NodeCount==24, "project and scope totals");
  Check(Math.Abs(p.Steel().Single().TotalMass-120*g.PlateMass)<1e-8,"unrounded total");
  var butt=Json.Copy(m.Parameters);butt.ButtWeld=true;
  Check(Splice.Calculate(butt).Gap==0 && butt.EndGap==10,"butt weld closes gap without changing saved input");
  var sparse=Json.Copy(m.Parameters);sparse.PlateCount=2;
  Reject(()=>Splice.Calculate(sparse),"two plates with impossible small gap rejected");sparse.ClearSpacing=200;
  Check(Math.Abs(Splice.Spacing(sparse,Splice.Calculate(sparse).Angle)-200)<1e-8,"two plate boundary case");
  var dense=Json.Copy(m.Parameters);dense.PlateCount=12;
  Check(Math.Abs(Splice.Spacing(dense,Splice.Calculate(dense).Angle)-50)<1e-8,"twelve plate boundary case");
  var copy=Json.Copy(p); Check(copy.Id==p.Id && copy.Marks[0].DetailId==m.DetailId && copy.Marks[0].Parameters.ClearSpacing==50,"JSON roundtrip identity");
  copy.Marks[0].Parameters.ClearSpacing=1e8; Reject(()=>copy.Validate(),"impossible spacing rejected");
  copy=Json.Copy(p); copy.Marks[0].Parameters.PipeDiameter=double.NaN; Reject(()=>copy.Validate(),"NaN rejected");
  copy=Json.Copy(p); copy.Uses[0].MarkId=Guid.NewGuid(); Reject(()=>copy.Validate(),"orphan use rejected");
  copy=Json.Copy(p); copy.Marks.Add(Json.Copy(m)); Reject(()=>copy.Validate(),"duplicate mark rejected");
  copy=Json.Copy(p); copy.Schema=2; Reject(()=>copy.Validate(),"future schema rejected");
  var dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"kotnode-test-"+Guid.NewGuid()); Directory.CreateDirectory(dir);
  try {
   string path=System.IO.Path.Combine(dir,"project.json"); var a=new ProjectStore(path); var first=a.Save(p);
   Check(first.Version==1,"first atomic save");
   var b=new ProjectStore(path); var stale=b.Load(); first.Name="Изменено"; a.Save(first);
   Reject(()=>b.Save(stale),"concurrent edit protected");
   Check(new ProjectStore(path).Load().Name=="Изменено","conflict does not overwrite winner");
   Check(Json.Read<Project>(File.ReadAllText(path+".bak")).Version==1,"backup previous revision");
   using(var locked=new FileStream(path+".lock",FileMode.Open,FileAccess.ReadWrite,FileShare.None)) Reject(()=>b.Load(),"exclusive lock respected");
   Check(Report.Csv(p,"Ярус 1").Split(new[]{"\r\n","\n"},StringSplitOptions.None)[1].Split(';')[5]=="96","CSV scoped source quantities");
  } finally { Directory.Delete(dir,true); }
  Console.WriteLine(checks+" core checks passed");
  ParametricProjectionTests.Run();
  TableMergeTests.Run();
  AssemblyModelTests.Run();
  DraftingTests.Run(Path.Combine(AppContext.BaseDirectory,"fixtures","lisp07.json"));
  if(args.Length==2 && args[0]=="--projection-scene")ParametricProjectionTests.Export(args[1]);
  if(args.Length==2 && args[0]=="--scene")File.WriteAllText(args[1],Json.Write(new[]{Drafting.Build(m,"main",20),Drafting.Build(m,"section",20)}));
  if(args.Length==2 && args[0]=="--demo") { p.Code="DEMO-01";p.Name="Ограждение котлована — пример";new ProjectStore(args[1]).Save(p);Console.WriteLine("Demo plate mass: "+g.PlateMass.ToString("0.000000")); }

 }
}
