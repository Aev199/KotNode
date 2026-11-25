using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;

namespace KotNode.AutoCAD
{
    // An interactive, reproducible fallback to AutoCAD's native FLATSHOT.
    // The built-in FLATSHOT dialog cannot be driven silently in AutoCAD 2021.
    internal static class FlatshotWorkflow
    {
        internal const string JobKey="KOTNODE_FLATSHOT_JOB_1";

        [DataContract]
        public sealed class Job
        {
            [DataMember(Order=1)] public Guid MarkId { get; set; }
            [DataMember(Order=2)] public string MarkHash { get; set; }
            [DataMember(Order=3)] public string Name { get; set; }
            [DataMember(Order=4)] public bool IsSection { get; set; }
            [DataMember(Order=5)] public bool Hidden { get; set; }
            [DataMember(Order=6)] public bool NoHatch { get; set; }
            [DataMember(Order=7)] public double AnnotationScale { get; set; }
            [DataMember(Order=8)] public RigidPlacement Frame { get; set; }
        }

        internal static void Prepare(Document doc,Transaction tr,bool section)
        {
            var mark=AssemblyDrawing.Load(doc.Database,tr);
            if(mark==null)throw new InvalidOperationException("Создайте марку командой NANEW.");
            var refs=AssemblyDrawing.Synchronize(doc.Database,tr,mark);
            if(refs.Count==0)throw new InvalidOperationException("В марке нет деталей.");
            var frame=AssemblyCommands.ViewFrame(doc,mark,section);
            if(frame==null)return;
            var ed=doc.Editor;
            var label=ed.GetString(new PromptStringOptions(section?"\nНазвание разреза: ":"\nНазвание вида: "){AllowSpaces=true});
            if(label.Status!=PromptStatus.OK)return;
            if(string.IsNullOrWhiteSpace(label.StringResult))throw new InvalidOperationException("Введите название.");
            var hiddenOptions=new PromptKeywordOptions("\nНевидимые линии в FLATSHOT [Yes/No] <No>: "){AllowNone=true};
            hiddenOptions.Keywords.Add("Yes");hiddenOptions.Keywords.Add("No");hiddenOptions.Keywords.Default="No";
            var hidden=ed.GetKeywords(hiddenOptions);
            if(hidden.Status!=PromptStatus.OK&&hidden.Status!=PromptStatus.None)return;
            bool showHidden=hidden.StringResult=="Yes";
            bool hatch=true;
            if(section)
            {
                var hatchOptions=new PromptKeywordOptions("\nШтриховать разрез после импорта [Yes/No] <Yes>: "){AllowNone=true};
                hatchOptions.Keywords.Add("Yes");hatchOptions.Keywords.Add("No");hatchOptions.Keywords.Default="Yes";
                var answer=ed.GetKeywords(hatchOptions);
                if(answer.Status!=PromptStatus.OK&&answer.Status!=PromptStatus.None)return;
                hatch=answer.StringResult!="No";
            }
            var scale=ed.GetDouble(new PromptDoubleOptions("\nМасштаб оформления 1 <20>: "){AllowNone=true,AllowZero=false,AllowNegative=false,DefaultValue=20,UseDefaultValue=true});
            if(scale.Status!=PromptStatus.OK&&scale.Status!=PromptStatus.None)return;
            double value=scale.Status==PromptStatus.None?20:scale.Value;
            if(value<=0||value>1000||double.IsNaN(value)||double.IsInfinity(value))throw new InvalidOperationException("Масштаб от 0 до 1000.");
            using(var dlg=new System.Windows.Forms.SaveFileDialog{
                Title="Сохранить отдельную модель для FLATSHOT",
                Filter="AutoCAD DWG (*.dwg)|*.dwg",DefaultExt="dwg",AddExtension=true,
                OverwritePrompt=true,FileName=section?"KotNode-cut.dwg":"KotNode-view.dwg"})
            {
                if(dlg.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
                if(File.Exists(dlg.FileName))throw new InvalidOperationException("Выберите новый DWG: перезапись ранее подготовленного FLATSHOT запрещена.");
                if(!string.IsNullOrWhiteSpace(doc.Name)&&Path.GetFullPath(doc.Name).Equals(Path.GetFullPath(dlg.FileName),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Исходный DWG нельзя перезаписывать.");
                var job=new Job{MarkId=mark.Id,MarkHash=AssemblyViews.Signature(mark),Name=label.StringResult.Trim(),IsSection=section,
                    Hidden=showHidden,NoHatch=!hatch,AnnotationScale=value,Frame=frame};
                // Fail before creating a DWG if the metadata contract ever becomes incompatible.
                var serialized=KotNode.Core.Json.Write(job);
                var roundTrip=KotNode.Core.Json.Read<Job>(serialized);
                if(roundTrip==null||roundTrip.MarkId!=job.MarkId||roundTrip.MarkHash!=job.MarkHash||roundTrip.Frame==null)
                    throw new InvalidOperationException("Не удалось проверить метаданные задания FLATSHOT.");
                Diagnostics.Step("Метаданные FLATSHOT проверены; экспорт изолированной модели");
                AssemblyViews.ExportForFlatshot(tr,refs,job,dlg.FileName);
                AssemblyDrawing.Save(doc.Database,tr,mark);
                ed.WriteMessage("\nСоздан файл "+dlg.FileName+
                    "\n1. Откройте его в AutoCAD, установите ПСК World и вид Top (PLAN World)."+
                    "\n2. FLATSHOT → Insert as new block; Insert 0,0,0; scale 1; rotation 0. Настройте невидимые линии по запросу."+
                    "\n3. Сохраните DWG, вернитесь в исходный чертёж и запустите NAFLATFINISH.");
            }
        }

        internal static void Finish(Document doc,Transaction tr)
        {
            var mark=AssemblyDrawing.Load(doc.Database,tr);
            if(mark==null)throw new InvalidOperationException("В текущем DWG нет марки.");
            var refs=AssemblyDrawing.Synchronize(doc.Database,tr,mark);
            using(var dlg=new System.Windows.Forms.OpenFileDialog{
                Title="Укажите подготовленный DWG после выполнения FLATSHOT",
                Filter="AutoCAD DWG (*.dwg)|*.dwg",CheckFileExists=true})
            {
                if(dlg.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
                if(!string.IsNullOrWhiteSpace(doc.Name)&&Path.GetFullPath(doc.Name).Equals(Path.GetFullPath(dlg.FileName),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Нельзя импортировать из текущего DWG.");
                using(var source=new Database(false,true))
                {
                    source.ReadDwgFile(dlg.FileName,FileShare.ReadWrite,true,null);
                    source.CloseInput(true);
                    using(var sourceTx=source.TransactionManager.StartTransaction())
                    {
                        var root=(DBDictionary)sourceTx.GetObject(source.NamedObjectsDictionaryId,OpenMode.ForRead);
                        var job=AssemblyDrawing.Read<Job>(root,JobKey,sourceTx);
                        if(job==null)throw new InvalidOperationException("DWG не подготовлен конструктором (нет задания FLATSHOT).");
                        if(job.MarkId!=mark.Id)throw new InvalidOperationException("Подготовленный вид относится к другой марке.");
                        if(job.MarkHash!=AssemblyViews.Signature(mark))
                            throw new InvalidOperationException("Модель марки изменилась после подготовки DWG. Создайте новое задание.");
                        var space=AssemblyDrawing.ModelSpace(source,sourceTx);
                        var candidate=new List<BlockReference>();
                        foreach(ObjectId id in space)
                        {
                            var block=sourceTx.GetObject(id,OpenMode.ForRead) as BlockReference;
                            if(block==null)continue;
                            var definition=(BlockTableRecord)sourceTx.GetObject(block.BlockTableRecord,OpenMode.ForRead);
                            if(definition.Cast<ObjectId>().Any()&&definition.Cast<ObjectId>().All(eid=>sourceTx.GetObject(eid,OpenMode.ForRead) is Curve))
                                candidate.Add(block);
                        }
                        if(candidate.Count!=1)
                            throw new InvalidOperationException("В подготовленном DWG должен быть ровно один плоский блок FLATSHOT; найдено: "+candidate.Count);
                        var srcRef=candidate[0];
                        var placement=AssemblyDrawing.Placement(srcRef);
                        if(Math.Abs(placement.Origin.Z)>1e-5||Math.Abs(placement.AxisX.Z)>1e-8||Math.Abs(placement.AxisY.Z)>1e-8)
                            throw new InvalidOperationException("Блок FLATSHOT не лежит в мировой XY.");
                        var definitionRef=(BlockTableRecord)sourceTx.GetObject(srcRef.BlockTableRecord,OpenMode.ForRead);
                        var ids=new ObjectIdCollection(definitionRef.Cast<ObjectId>().ToArray());
                        // Coordinates in the block definition are relative to the FLATSHOT insertion.
                        var point=doc.Editor.GetPoint("\nТочка размещения вида в исходном чертеже: ");
                        if(point.Status!=PromptStatus.OK)return;
                        var at=point.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem);
                        Diagnostics.Step("Импорт геометрии FLATSHOT в управляемый блок");
                        var bt=(BlockTable)tr.GetObject(doc.Database.BlockTableId,OpenMode.ForWrite);
                        var target=new BlockTableRecord{Name="NA_VIEW_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters};
                        bt.Add(target);tr.AddNewlyCreatedDBObject(target,true);
                        var mapping=new IdMapping();
                        source.WblockCloneObjects(ids,target.ObjectId,mapping,DuplicateRecordCloning.Ignore,false);
                        int count=0;
                        foreach(IdPair pair in mapping)
                        {
                            if(!pair.IsCloned||pair.Value.IsNull)continue;
                            var entity=tr.GetObject(pair.Value,OpenMode.ForWrite) as Curve;
                            if(entity==null)continue;
                            entity.TransformBy(srcRef.BlockTransform);
                            var ext=entity.GeometricExtents;
                            if(Math.Abs(ext.MinPoint.Z)>1e-5||Math.Abs(ext.MaxPoint.Z)>1e-5)
                                throw new InvalidOperationException("FLATSHOT содержит неплоские линии.");
                            count++;
                        }
                        if(count==0)throw new InvalidOperationException("Не удалось перенести кривые FLATSHOT.");
                        var data=new AssemblyViews.ViewData{Name=job.Name,IsSection=job.IsSection,FullCut=job.IsSection,
                            Hidden=job.Hidden,NoHatch=job.NoHatch,AnnotationScale=job.AnnotationScale,Frame=job.Frame};
                        if(job.IsSection)
                            AssemblyViews.AddSectionOverlay(doc.Database,tr,refs,job.Frame,target,data);
                        AssemblyViews.CreateImported(doc.Database,tr,mark,target.ObjectId,data,new Point3d(at.X,at.Y,0));
                        AssemblyDrawing.Save(doc.Database,tr,mark);
                        doc.Editor.WriteMessage("\nИмпортировано "+count+" кривых. Вид связан с маркой как снимок FLATSHOT. После изменения 3D-модели его необходимо подготовить заново.");
                        sourceTx.Commit();
                    }
                }
            }
        }
    }
}
