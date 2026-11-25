using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Linq;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using Autodesk.AutoCAD.DatabaseServices;
using KotNode.Core;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
[assembly: CommandClass(typeof(KotNode.AutoCAD.Commands))]
namespace KotNode.AutoCAD
{
 public sealed class Commands
 {
  static PaletteSet palette;
  static ProjectPanel panel;
  static void Run(Action action,[CallerMemberName]string command=null)
  {
   Diagnostics.Begin(command);
   try{action();}
   catch(System.Exception e)
   {
    var log=Diagnostics.Write(e);
    AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nУзлы "+Diagnostics.Version+": "+e.GetType().Name+": "+e.Message+"\nЭтап: "+Diagnostics.Stage+"\nЖурнал: "+log);
   }
  }
  [CommandMethod("NPLOG",CommandFlags.Modal)]
  public void Log(){AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n"+(Diagnostics.LastLog??"В этом сеансе ошибок с журналом еще не было."));}
  [CommandMethod("NPVERSION",CommandFlags.Modal)]
  public void Version(){AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nKotNode "+Diagnostics.Version+"\nDLL: "+typeof(Commands).Assembly.Location);}

  static void Need(){if(panel==null||panel.Project==null)throw new InvalidOperationException("Откройте проект командой NPROJECT.");}
  static void Latest(){Need();panel.ReloadForCommand();}
  [CommandMethod("NPROJECT",CommandFlags.Modal)]
  public void Open(){Run(()=>{if(palette==null){panel=new ProjectPanel();palette=new PaletteSet("Узлы — проект",new Guid("f5c504a1-9a1a-45a4-ad25-c1f0d82ad803")){Size=new Size(640,720),MinimumSize=new Size(470,450),Style=PaletteSetStyles.ShowCloseButton|PaletteSetStyles.ShowAutoHideButton|PaletteSetStyles.ShowPropertiesMenu};palette.Add("Проект",panel);}palette.Visible=true;});}
  [CommandMethod("NPPLACE",CommandFlags.Modal)]
  public void Place(){Run(()=>{
   Latest();var doc=AcApp.DocumentManager.MdiActiveDocument;var ed=doc.Editor;
   var id=ed.GetString("\nИдентификатор марки (вставляйте из диспетчера): ");if(id.Status!=PromptStatus.OK)return;
   var guid=Guid.Parse(id.StringResult);var mark=panel.Project.Marks.Single(m=>m.Id==guid);
   if(doc.Database.Insunits!=UnitsValue.Millimeters)throw new InvalidOperationException("Установите единицы DWG — миллиметры (INSUNITS=4). Геометрия строится 1:1.");
   if(!ed.CurrentUserCoordinateSystem.IsEqualTo(Autodesk.AutoCAD.Geometry.Matrix3d.Identity))throw new InvalidOperationException("Для вставки переключитесь в мировую ПСК: UCS → World.");
   var scale=ed.GetDouble(new PromptDoubleOptions("\nМасштаб оформления 1:"){DefaultValue=20,UseDefaultValue=true,AllowNegative=false,AllowZero=false});if(scale.Status!=PromptStatus.OK)return;if(scale.Value>1000)throw new InvalidOperationException("Масштаб оформления не более 1000.");
   var at=ed.GetPoint("\nТочка размещения продольного вида: ");if(at.Status!=PromptStatus.OK)return;if(Math.Abs(at.Value.Z)>1e-8)throw new InvalidOperationException("Выберите точку в мировой плоскости XY (Z=0).");
   Drawing.Place(doc.Database,panel.Project,panel.Store.Path,mark,at.Value,scale.Value);ed.Regen();ed.WriteMessage("\nВиды созданы. Количество узлов задайте отдельно на вкладке «Применения». Для привязок после MOVE выполните NPUPDATE.");
  });}
  [CommandMethod("NPUPDATE",CommandFlags.Modal)]
  public void Update(){Run(()=>{Latest();var doc=AcApp.DocumentManager.MdiActiveDocument;int n=Drawing.Update(doc.Database,panel.Project);doc.Editor.Regen();doc.Editor.WriteMessage("\nОбновлено видов: "+n+". Связанные спецификации и ведомости также обновлены.");panel.SetStatus("Обновлено видов: "+n);});}
  [CommandMethod("NPSTATUS",CommandFlags.Modal)]
  public void Status(){Run(()=>{Latest();var doc=AcApp.DocumentManager.MdiActiveDocument;var s=Drawing.Status(doc.Database,panel.Project);doc.Editor.WriteMessage("\n"+s);panel.SetStatus(s);});}
  [CommandMethod("NPSPEC",CommandFlags.Modal)]
  public void Spec(){Run(()=>{Latest();var doc=AcApp.DocumentManager.MdiActiveDocument;var id=doc.Editor.GetString("\nИдентификатор марки: ");if(id.Status!=PromptStatus.OK)return;var mark=panel.Project.Marks.Single(m=>m.Id==Guid.Parse(id.StringResult));var at=doc.Editor.GetPoint("\nТочка спецификации (оформление 1:20): ");if(at.Status!=PromptStatus.OK)return;if(doc.Database.Insunits!=UnitsValue.Millimeters)throw new InvalidOperationException("Установите INSUNITS=4.");Drawing.SpecTable(doc.Database,panel.Project,panel.Store.Path,mark,at.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem));doc.Editor.Regen();});}
  [CommandMethod("NPTABLE",CommandFlags.Modal)]
  public void Table(){Run(()=>{Latest();var doc=AcApp.DocumentManager.MdiActiveDocument;var result=doc.Editor.GetPoint("\nТочка сводной таблицы всего проекта (оформление 1:20): ");if(result.Status!=PromptStatus.OK)return;if(doc.Database.Insunits!=UnitsValue.Millimeters)throw new InvalidOperationException("Установите INSUNITS=4 (мм).");Drawing.SteelTable(doc.Database,panel.Project,panel.Store.Path,result.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem));doc.Editor.Regen();});}
  [CommandMethod("NPINFO",CommandFlags.Modal)]
  public void Info(){Run(()=>{var doc=AcApp.DocumentManager.MdiActiveDocument;var selected=doc.Editor.GetEntity("\nВыберите вид, размер, выноску или таблицу: ");if(selected.Status!=PromptStatus.OK)return;using(var tr=doc.Database.TransactionManager.StartTransaction()){
   var e=(Entity)tr.GetObject(selected.ObjectId,OpenMode.ForRead);var l=Drawing.Read(e,tr);if(l==null)throw new InvalidOperationException("Объект не связан с проектом. Обычная COPY создает несвязанное изображение.");
   doc.Editor.WriteMessage("\nПроект: "+l.ProjectCode+" / "+l.ProjectId+"\nФайл проекта: "+l.ProjectPath+"\nРоль: "+l.Role);
   if(l.Mark!=null){var g=Splice.Calculate(l.Mark.Parameters);doc.Editor.WriteMessage("\nМарка: "+l.Mark.Code+"; редакция: "+l.Mark.Revision+"; накладок: "+l.Mark.Parameters.PlateCount+"; масса на узел: "+(g.PlateMass*l.Mark.Parameters.PlateCount).ToString("0.00")+" кг.");}else doc.Editor.WriteMessage("\nТаблица по версии проекта: "+l.ReportVersion);
  }});}
 }
}
