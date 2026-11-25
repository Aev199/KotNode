// Deliberately small API double. The production Unmerge method is linked unchanged.
// This executes the documented AutoCAD null-return contract without a CAD host.
using System;
using System.IO;
using KotNode.AutoCAD;

namespace Autodesk.AutoCAD.DatabaseServices
{
    public sealed class CellRange
    {
        public int TopRow,BottomRow,LeftColumn,RightColumn;
    }
    public sealed class Cell
    {
        public CellRange Merge;
        public CellRange GetMergeRange(){return Merge;}
    }
    public sealed class Counted {public int Count;}
    public sealed class Cells
    {
        readonly Cell[,] values;
        public Cells(int rows,int cols){values=new Cell[rows,cols];for(int r=0;r<rows;r++)for(int c=0;c<cols;c++)values[r,c]=new Cell();}
        public Cell this[int r,int c]{get{return values[r,c];}}
    }
    public sealed class Table
    {
        public readonly Counted Rows,Columns;
        public readonly Cells Cells;
        public int UnmergeCalls;
        public Table(int rows,int cols){Rows=new Counted{Count=rows};Columns=new Counted{Count=cols};Cells=new Cells(rows,cols);}
        public void Merge(int top,int left,int bottom,int right)
        {var range=new CellRange{TopRow=top,BottomRow=bottom,LeftColumn=left,RightColumn=right};for(int r=top;r<=bottom;r++)for(int c=left;c<=right;c++)Cells[r,c].Merge=range;}
        public void UnmergeCells(CellRange range)
        {UnmergeCalls++;for(int r=range.TopRow;r<=range.BottomRow;r++)for(int c=range.LeftColumn;c<=range.RightColumn;c++)Cells[r,c].Merge=null;}
    }
}
static class TableMergeTests
{
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
    internal static void Run()
    {
        var ordinary=new Autodesk.AutoCAD.DatabaseServices.Table(6,6);
        Drawing.Unmerge(ordinary);Check(ordinary.UnmergeCalls==0,"ordinary cells return null and must not be dereferenced");
        var mixed=new Autodesk.AutoCAD.DatabaseServices.Table(6,6);mixed.Merge(0,0,0,5);mixed.Merge(5,0,5,5);
        Drawing.Unmerge(mixed);Check(mixed.UnmergeCalls==2,"mixed title/body/footer table unmerges each range once");
        Check(mixed.Cells[0,0].GetMergeRange()==null&&mixed.Cells[5,5].GetMergeRange()==null,"all merged cells reset");
        Drawing.Unmerge(mixed);Check(mixed.UnmergeCalls==2,"repeat NPUPDATE unmerge is idempotent");
        Drawing.Unmerge(new Autodesk.AutoCAD.DatabaseServices.Table(0,0));Check(true,"empty table is harmless");
        var dir=Path.Combine(Path.GetTempPath(),"kotnode-log-test-"+Guid.NewGuid());
        try
        {
            Diagnostics.Begin("NPPLACE");Diagnostics.Step("Спецификация: объединения ячеек");
            var error=new InvalidOperationException("outer",new NullReferenceException("inner"));var path=Diagnostics.Write(error,dir);var log=File.ReadAllText(path);
            Check(log.Contains("NPPLACE")&&log.Contains("Спецификация"),"diagnostics retain command and drawing stage");
            Check(log.Contains("InvalidOperationException")&&log.Contains("NullReferenceException")&&log.Contains("inner"),"full exception and inner exception logged");
            Check(path==Diagnostics.LastLog,"NPLOG keeps latest error path");
            var unavailable=Path.Combine(dir,"file");File.WriteAllText(unavailable,"not a directory");
            Check(Diagnostics.Write(error,unavailable).StartsWith("Журнал не записан"),"logging failure does not hide original failure");
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
        Console.WriteLine("Table null-return regression + diagnostics: "+checks+" checks passed (API double, no native AutoCAD)");
    }
}
