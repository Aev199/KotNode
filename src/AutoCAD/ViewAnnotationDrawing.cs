using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;

namespace KotNode.AutoCAD
{
    // Synchronizes native ModelSpace Dimensions and MLeaders attached to parametric views.
    // Preserves manual offsets of dimension lines and leader landings across view MOVE and NAUPDATEVIEW.
    public static class ViewAnnotationDrawing
    {
        internal const string TagKey="KOTNODE_VIEW_ANNO_1";
        internal const string AxisLayer="NA_AXIS",AnnoLayer="NA_ANNOTATION";

        public sealed class AnnoTag
        {
            public Guid ViewId { get; set; }
            public Guid AnnotationId { get; set; }
            public string Kind { get; set; } // "dim" | "leader"
            public string SelfHandle { get; set; }
            public double LastViewX { get; set; }
            public double LastViewY { get; set; }
            public double LastRefX { get; set; }
            public double LastRefY { get; set; }
            public double LastOffsetX { get; set; }
            public double LastOffsetY { get; set; }
            public int[] LeaderLines { get; set; }
        }

        internal static void EnsureStylesAndLayers(Database db,Transaction tr)
        {
            var types=(LinetypeTable)tr.GetObject(db.LinetypeTableId,OpenMode.ForRead);
            const string center="NA_CENTER";
            if(!types.Has(center))
            {
                types.UpgradeOpen();
                var lt=new LinetypeTableRecord{Name=center,AsciiDescription="KotNode axis centerline",PatternLength=18,NumDashes=4};
                lt.SetDashLengthAt(0,12);lt.SetDashLengthAt(1,-2);lt.SetDashLengthAt(2,2);lt.SetDashLengthAt(3,-2);
                types.Add(lt);tr.AddNewlyCreatedDBObject(lt,true);
            }
            var layers=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);
            if(!layers.Has(AxisLayer))
            {
                if(!layers.IsWriteEnabled)layers.UpgradeOpen();
                var l=new LayerTableRecord{Name=AxisLayer,Color=Color.FromColorIndex(ColorMethod.ByAci,1),LinetypeObjectId=types[center],LineWeight=LineWeight.LineWeight013};
                layers.Add(l);tr.AddNewlyCreatedDBObject(l,true);
            }
            if(!layers.Has(AnnoLayer))
            {
                if(!layers.IsWriteEnabled)layers.UpgradeOpen();
                var l=new LayerTableRecord{Name=AnnoLayer,Color=Color.FromColorIndex(ColorMethod.ByAci,7),LinetypeObjectId=types["Continuous"],LineWeight=LineWeight.LineWeight018};
                layers.Add(l);tr.AddNewlyCreatedDBObject(l,true);
            }
            var styles=(TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead);
            if(!styles.Has("NA_ISOCPEUR"))
            {
                styles.UpgradeOpen();
                var s=new TextStyleTableRecord{Name="NA_ISOCPEUR",FileName="isocpeur.ttf"};
                styles.Add(s);tr.AddNewlyCreatedDBObject(s,true);
            }
        }
        internal static ObjectId TextStyleId(Database db,Transaction tr)
        {
            EnsureStylesAndLayers(db,tr);
            return ((TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead))["NA_ISOCPEUR"];
        }
        internal static ObjectId DimStyleId(Database db,Transaction tr)
        {
            EnsureStylesAndLayers(db,tr);
            var styles=(DimStyleTable)tr.GetObject(db.DimStyleTableId,OpenMode.ForRead);
            if(styles.Has("NA_DIM_1"))return styles["NA_DIM_1"];
            var blocks=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
            ObjectId tick;
            if(blocks.Has("NA_TICK_1"))tick=blocks["NA_TICK_1"];
            else
            {
                blocks.UpgradeOpen();
                var arrow=new BlockTableRecord{Name="NA_TICK_1"};
                tick=blocks.Add(arrow);tr.AddNewlyCreatedDBObject(arrow,true);
                var line=new Line(new Point3d(-0.5,-0.5,0),new Point3d(0.5,0.5,0)){LineWeight=LineWeight.LineWeight030};
                arrow.AppendEntity(line);tr.AddNewlyCreatedDBObject(line,true);
            }
            styles.UpgradeOpen();
            var st=new DimStyleTableRecord
            {
                Name="NA_DIM_1",Dimtxsty=TextStyleId(db,tr),Dimtxt=2.5,Dimasz=1.875,Dimdec=0,
                Dimtad=1,Dimexe=1.125,Dimexo=0.625,Dimgap=0.625,Dimdle=0,Dimtix=false,Dimtmove=2,
                Dimblk=tick,Dimblk1=tick,Dimblk2=tick,Dimsah=true,Dimtoh=false,Dimtih=false
            };
            var id=styles.Add(st);tr.AddNewlyCreatedDBObject(st,true);
            return id;
        }
        static AnnoTag ReadTag(Entity e,Transaction tr)
        {
            if(e==null||e.ExtensionDictionary.IsNull)return null;
            var dict=(DBDictionary)tr.GetObject(e.ExtensionDictionary,OpenMode.ForRead);
            var tag=AssemblyDrawing.Read<AnnoTag>(dict,TagKey,tr);
            return tag!=null&&tag.SelfHandle==e.Handle.ToString()?tag:null;
        }
        static void WriteTag(Entity e,Transaction tr,AnnoTag tag)
        {
            if(!e.IsWriteEnabled)e.UpgradeOpen();
            if(e.ExtensionDictionary.IsNull)e.CreateExtensionDictionary();
            tag.SelfHandle=e.Handle.ToString();
            AssemblyDrawing.Write((DBDictionary)tr.GetObject(e.ExtensionDictionary,OpenMode.ForRead),TagKey,tag,tr);
        }
        static Point3d ToWorld(Matrix3d xform,Point2 p)=>new Point3d(p.X,p.Y,0).TransformBy(xform);
        static Dimension CreateDimension(Database db,Transaction tr,Matrix3d xform,ResolvedDimension rd)
        {
            var style=DimStyleId(db,tr);
            var a=ToWorld(xform,rd.A);var b=ToWorld(xform,rd.B);var line=ToWorld(xform,rd.LinePoint);
            var text=rd.Source.TextOverride??"";
            switch(rd.Source.Kind)
            {
                case ViewDimensionKind.Horizontal: return new RotatedDimension(0,a,b,line,text,style);
                case ViewDimensionKind.Vertical: return new RotatedDimension(Math.PI/2,a,b,line,text,style);
                case ViewDimensionKind.Aligned: return new AlignedDimension(a,b,line,text,style);
                case ViewDimensionKind.Diameter: return new DiametricDimension(a,b,Math.Max(10,Math.Sqrt(rd.Source.OffsetX*rd.Source.OffsetX+rd.Source.OffsetY*rd.Source.OffsetY)-rd.Radius),text,style);
                case ViewDimensionKind.Radius: return new RadialDimension(ToWorld(xform,rd.Center),a,Math.Max(10,Math.Sqrt(rd.Source.OffsetX*rd.Source.OffsetX+rd.Source.OffsetY*rd.Source.OffsetY)-rd.Radius),text,style);
                default: throw new InvalidOperationException("Неизвестный тип размера.");
            }
        }
        static void ApplyDimensionGeometry(Dimension d,Matrix3d xform,ResolvedDimension rd)
        {
            var a=ToWorld(xform,rd.A);var b=ToWorld(xform,rd.B);var line=ToWorld(xform,rd.LinePoint);
            if(d is RotatedDimension rot){rot.Rotation=rd.Source.Kind==ViewDimensionKind.Vertical?Math.PI/2:0;rot.XLine1Point=a;rot.XLine2Point=b;rot.DimLinePoint=line;}
            else if(d is AlignedDimension al){al.XLine1Point=a;al.XLine2Point=b;al.DimLinePoint=line;}
            else if(d is DiametricDimension dia){dia.ChordPoint=a;dia.FarChordPoint=b;dia.LeaderLength=Math.Max(5,Math.Sqrt(rd.Source.OffsetX*rd.Source.OffsetX+rd.Source.OffsetY*rd.Source.OffsetY)-rd.Radius);}
            else if(d is RadialDimension rad){rad.Center=ToWorld(xform,rd.Center);rad.ChordPoint=a;rad.LeaderLength=Math.Max(5,Math.Sqrt(rd.Source.OffsetX*rd.Source.OffsetX+rd.Source.OffsetY*rd.Source.OffsetY)-rd.Radius);}
        }
        internal static void CaptureOffsets(Database db,Transaction tr,AssemblyViews.Entry entry)
        {
            var existing=new List<Tuple<Entity,AnnoTag>>();
            foreach(ObjectId id in AssemblyDrawing.ModelSpace(db,tr))
            {
                if(id.IsErased)continue;
                var e=tr.GetObject(id,OpenMode.ForRead) as Entity;
                var tag=ReadTag(e,tr);
                if(tag!=null&&tag.ViewId==entry.Data.Id)existing.Add(Tuple.Create(e,tag));
            }
            CaptureOffsets(entry,existing);
        }
        static void CaptureOffsets(AssemblyViews.Entry entry,List<Tuple<Entity,AnnoTag>> existing)
        {
            var data=entry.Data;
            var toLocal=entry.Reference.BlockTransform.Inverse();
            foreach(var item in existing)
            {
                var e=item.Item1;var tag=item.Item2;
                var viewDelta=new Vector3d(entry.Reference.Position.X-tag.LastViewX,entry.Reference.Position.Y-tag.LastViewY,0).TransformBy(toLocal);
                var lastRef=new Point2(tag.LastRefX,tag.LastRefY);
                var delta=new Point2(viewDelta.X,viewDelta.Y);
                if(tag.Kind=="dim"&&e is Dimension dim)
                {
                    var modelDim=data.Dimensions?.FirstOrDefault(x=>x.Id==tag.AnnotationId);
                    if(modelDim==null)continue;
                    Point3d actualPt,reference;
                    if(dim is RotatedDimension rd){actualPt=rd.DimLinePoint;reference=rd.XLine1Point+(rd.XLine2Point-rd.XLine1Point)*0.5;}
                    else if(dim is AlignedDimension ad){actualPt=ad.DimLinePoint;reference=ad.XLine1Point+(ad.XLine2Point-ad.XLine1Point)*0.5;}
                    else continue;
                    var local=actualPt.TransformBy(toLocal);var localRef=reference.TransformBy(toLocal);
                    var offset=AnnotationPlacement.Capture(new Point2(local.X,local.Y),new Point2(localRef.X,localRef.Y),lastRef,delta);
                    modelDim.OffsetX=offset.X;modelDim.OffsetY=offset.Y;
                }
                else if(tag.Kind=="leader"&&e is MLeader ml)
                {
                    var leader=data.Leaders?.FirstOrDefault(x=>x.Id==tag.AnnotationId);
                    if(leader==null)continue;
                    var lines=ml.GetLeaderIndexes().Cast<int>().SelectMany(j=>ml.GetLeaderLineIndexes(j).Cast<int>()).ToList();
                    if(lines.Count==0)continue;
                    var local=ml.TextLocation.TransformBy(toLocal);var localRef=ml.GetFirstVertex(lines[0]).TransformBy(toLocal);
                    var offset=AnnotationPlacement.Capture(new Point2(local.X,local.Y),new Point2(localRef.X,localRef.Y),lastRef,delta);
                    leader.LandingOffsetX=offset.X;leader.LandingOffsetY=offset.Y;
                }
            }
        }
        internal static void Sync(Database db,Transaction tr,AssemblyMark mark,AssemblyViews.Entry entry,IList<ParametricProjection.Curve2> rawCurves)
        {
            EnsureStylesAndLayers(db,tr);
            var data=entry.Data;
            double scale=data.AnnotationScale>0?data.AnnotationScale:20;
            var xform=entry.Reference.BlockTransform;
            var toLocal=xform.Inverse();
            var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);

            var existing=new List<Tuple<Entity,AnnoTag>>();
            foreach(ObjectId id in space)
            {
                if(id.IsErased)continue;
                var e=tr.GetObject(id,OpenMode.ForRead) as Entity;
                var tag=ReadTag(e,tr);
                if(tag!=null&&tag.ViewId==data.Id)existing.Add(Tuple.Create(e,tag));
            }

            CaptureOffsets(entry,existing);

            var resolvedDims=ViewAnnotations.ResolveDimensions(data.Dimensions,rawCurves,data.Welds,scale,out var unmatchedDims);
            var resolvedLeaders=ViewAnnotations.ResolveLeaders(data.Leaders,mark,rawCurves,data.Welds,scale,out var unmatchedLeaders);
            data.UnmatchedAnnotations+=unmatchedDims+unmatchedLeaders;

            var keep=new HashSet<ObjectId>();
            var dimStyle=DimStyleId(db,tr);
            var textStyle=TextStyleId(db,tr);
            var viewPos=entry.Reference.Position;

            foreach(var rd in resolvedDims)
            {
                var found=existing.FirstOrDefault(x=>x.Item2.Kind=="dim"&&x.Item2.AnnotationId==rd.Source.Id);
                Dimension dim=found?.Item1 as Dimension;
                bool fresh=dim==null;
                if(fresh)
                {
                    dim=CreateDimension(db,tr,xform,rd);
                    dim.SetDatabaseDefaults(db);
                    space.AppendEntity(dim);tr.AddNewlyCreatedDBObject(dim,true);
                }
                else dim.UpgradeOpen();
                dim.DimensionStyle=dimStyle;
                dim.Layer=AnnoLayer;
                dim.Dimscale=scale;
                dim.Dimtxt=2.5;dim.Dimasz=1.875;dim.Dimlfac=1;dim.Dimdec=rd.Source.Precision;
                dim.DimensionText=rd.Source.TextOverride??"";
                dim.Annotative=AnnotativeStates.False;
                ApplyDimensionGeometry(dim,xform,rd);
                dim.RecomputeDimensionBlock(true);
                double refX=rd.Center!=null?rd.Center.X:(rd.A.X+rd.B.X)/2;
                double refY=rd.Center!=null?rd.Center.Y:(rd.A.Y+rd.B.Y)/2;
                WriteTag(dim,tr,new AnnoTag
                {
                    ViewId=data.Id,AnnotationId=rd.Source.Id,Kind="dim",
                    LastViewX=viewPos.X,LastViewY=viewPos.Y,
                    LastRefX=refX,LastRefY=refY,
                    LastOffsetX=rd.Source.OffsetX,LastOffsetY=rd.Source.OffsetY
                });
                keep.Add(dim.ObjectId);
            }

            foreach(var rl in resolvedLeaders)
            {
                var found=existing.FirstOrDefault(x=>x.Item2.Kind=="leader"&&x.Item2.AnnotationId==rl.Source.Id);
                var ml=found?.Item1 as MLeader;
                bool fresh=ml==null;
                if(fresh)
                {
                    ml=new MLeader();
                    ml.SetDatabaseDefaults(db);
                    ml.ContentType=ContentType.MTextContent;
                    ml.Layer=AnnoLayer;
                    ml.Annotative=AnnotativeStates.False;
                    ml.TextHeight=scale*2.5;
                    ml.ArrowSize=scale*2.5;
                    ml.DoglegLength=scale*5;
                    space.AppendEntity(ml);tr.AddNewlyCreatedDBObject(ml,true);
                }
                else ml.UpgradeOpen();
                using(var mtext=fresh?new MText():ml.MText)
                {
                    mtext.Contents=rl.Text;
                    if(fresh){mtext.TextHeight=scale*2.5;mtext.TextStyleId=textStyle;}
                    ml.MText=mtext;
                }
                var tipWorld=ToWorld(xform,rl.Tip);
                var landWorld=ToWorld(xform,rl.Landing);
                var present=ml.GetLeaderIndexes().Cast<int>().SelectMany(j=>ml.GetLeaderLineIndexes(j).Cast<int>()).ToList();
                int lineIdx;
                if(present.Count>0)
                {
                    lineIdx=present[0];
                    ml.SetFirstVertex(lineIdx,tipWorld);
                    ml.TextLocation=landWorld;
                }
                else
                {
                    int leaderIdx=ml.AddLeader();
                    lineIdx=ml.AddLeaderLine(leaderIdx);
                    ml.AddFirstVertex(lineIdx,tipWorld);
                    ml.AddLastVertex(lineIdx,landWorld);
                    ml.TextLocation=landWorld;
                }
                WriteTag(ml,tr,new AnnoTag
                {
                    ViewId=data.Id,AnnotationId=rl.Source.Id,Kind="leader",
                    LastViewX=viewPos.X,LastViewY=viewPos.Y,
                    LastRefX=rl.Tip.X,LastRefY=rl.Tip.Y,
                    LastOffsetX=rl.Source.LandingOffsetX,LastOffsetY=rl.Source.LandingOffsetY,
                    LeaderLines=new[]{lineIdx}
                });
                keep.Add(ml.ObjectId);
            }

            foreach(var item in existing)
            {
                if(!item.Item1.IsErased&&!keep.Contains(item.Item1.ObjectId))
                {
                    item.Item1.UpgradeOpen();
                    item.Item1.Erase();
                }
            }
        }
    }
}
