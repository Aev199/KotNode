using System;
using System.Collections.Generic;
using System.Linq;
namespace KotNode.Core
{
    // One leg/style definition per logical weld; its graphical presentations live
    // in views and sections. Removing a presentation never deletes the definition.
    public sealed class WeldDefinition
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public double LegMm { get; set; }=6;
        public ViewWeldStyle Style { get; set; }=ViewWeldStyle.Site;
        public void Validate()
        {
            Guard.Require(Id!=Guid.Empty&&Guard.Finite(LegMm)&&LegMm>0&&LegMm<=100&&Enum.IsDefined(typeof(ViewWeldStyle),Style),"Неверное определение сварного шва.");
        }
    }
    public static class WeldCatalog
    {
        public static Guid Register(AssemblyMark mark,ViewWeldOverlay w)
        {
            ViewWelds.Validate(w);
            if(mark.WeldDefinitions==null)mark.WeldDefinitions=new List<WeldDefinition>();
            if(w.DefinitionId==Guid.Empty)w.DefinitionId=w.GroupId==Guid.Empty?w.Id:w.GroupId;
            if(!mark.WeldDefinitions.Any(d=>d.Id==w.DefinitionId))
            {
                mark.Schema=Math.Max(mark.Schema,2);
                mark.WeldDefinitions.Add(new WeldDefinition{Id=w.DefinitionId,LegMm=w.LegMm,Style=w.Style});
            }
            return w.DefinitionId;
        }
        public static ViewWeldOverlay Effective(AssemblyMark mark,ViewWeldOverlay w)
        {
            var copy=Json.Copy(w);
            if(copy.DefinitionId!=Guid.Empty)
            {
                var definition=mark.WeldDefinitions?.SingleOrDefault(d=>d.Id==copy.DefinitionId);
                Guard.Require(definition!=null,"Отсутствует общее определение сварного шва.");
                definition.Validate();copy.LegMm=definition.LegMm;copy.Style=definition.Style;
            }
            return copy;
        }
        public static void Set(AssemblyMark mark,Guid id,double leg,ViewWeldStyle style)
        {
            var definition=mark.WeldDefinitions?.SingleOrDefault(d=>d.Id==id);
            Guard.Require(definition!=null,"Общее определение сварного шва не найдено.");
            var draft=new WeldDefinition{Id=id,LegMm=leg,Style=style};draft.Validate();
            definition.LegMm=leg;definition.Style=style;
        }
    }
}
