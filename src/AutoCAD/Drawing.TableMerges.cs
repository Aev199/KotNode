using Autodesk.AutoCAD.DatabaseServices;

namespace KotNode.AutoCAD
{
    public static partial class Drawing
    {
        internal static void Unmerge(Table table)
        {
            for(int row=0;row<table.Rows.Count;row++)
            for(int col=0;col<table.Columns.Count;col++)
            {
                // AutoCAD returns null for every cell outside a merged range.
                // This includes ordinary cells in newly created tables.
                var merged=table.Cells[row,col].GetMergeRange();
                if(merged==null)continue;
                if(merged.TopRow!=merged.BottomRow||merged.LeftColumn!=merged.RightColumn)
                    table.UnmergeCells(merged);
            }
        }
    }
}
