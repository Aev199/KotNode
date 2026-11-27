using System;
using System.Collections.Generic;
using System.Linq;

namespace KotNode.Core
{
    // DWG-local independent nodes. Part/instance/view IDs need only be unique
    // inside their owning node; two instances of a family may share internal IDs.
    public sealed class DrawingNodes
    {
        public int Schema { get; set; }=1;
        public Guid ActiveId { get; set; }
        public List<AssemblyMark> Nodes { get; set; }=new List<AssemblyMark>();
        public void Validate()
        {
            Guard.Require(Schema==1&&Nodes!=null&&Nodes.All(n=>n!=null),"Повреждён список узлов DWG.");
            var ids=new HashSet<Guid>();var codes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var mark in Nodes)
            {
                mark.Validate();
                Guard.Require(ids.Add(mark.Id)&&codes.Add(mark.Code.Trim()),"Повторный идентификатор или шифр узла в DWG.");
            }
            Guard.Require(Nodes.Count==0?ActiveId==Guid.Empty:ids.Contains(ActiveId),"Активный узел DWG не найден.");
        }
        public AssemblyMark Active()
        {Validate();return Nodes.SingleOrDefault(n=>n.Id==ActiveId);}
        public string UniqueCode(string requested)
        {
            string code=string.IsNullOrWhiteSpace(requested)?"У-01":requested.Trim();
            var codes=new HashSet<string>(Nodes.Select(n=>n.Code.Trim()),StringComparer.OrdinalIgnoreCase);
            if(!codes.Contains(code))return code;
            int i=2;while(codes.Contains(code+"-"+i.ToString("00")))i++;
            return code+"-"+i.ToString("00");
        }
        public void Put(AssemblyMark mark)
        {
            var draft=Json.Copy(this);var copy=Json.Copy(mark);
            int index=draft.Nodes.FindIndex(n=>n.Id==copy.Id);
            if(index<0){draft.Nodes.Add(copy);draft.ActiveId=copy.Id;}
            else draft.Nodes[index]=copy;
            draft.Validate();Nodes=draft.Nodes;ActiveId=draft.ActiveId;
        }
        public void Activate(Guid id)
        {Validate();Guard.Require(Nodes.Any(n=>n.Id==id),"Узел не найден в этом DWG.");ActiveId=id;}
        public void Remove(Guid id)
        {
            var draft=Json.Copy(this);
            Guard.Require(draft.Nodes.RemoveAll(n=>n.Id==id)==1,"Узел не найден в этом DWG.");
            if(draft.ActiveId==id)draft.ActiveId=draft.Nodes.FirstOrDefault()?.Id??Guid.Empty;
            draft.Validate();Nodes=draft.Nodes;ActiveId=draft.ActiveId;
        }
    }
    public static class NodePlacement
    {
        public static void PlaceFamily(NodeFamilyTemplate template,Vector3 target)
        {
            Guard.Require(template?.Mark!=null&&target!=null&&target.IsFinite()&&Math.Abs(target.Z)<1e-8,"Точка размещения узла должна лежать в мировой XY.");
            var mark=template.Mark;var old=mark.LayoutOrigin??new Vector3();
            var delta=new Vector3(target.X-old.X,target.Y-old.Y,0);
            TranslateMark(mark,delta);
            foreach(var v in template.Views??new List<FamilyViewTemplate>())
            {v.Frame.Origin=Add(v.Frame.Origin,delta);v.OffsetX+=delta.X;v.OffsetY+=delta.Y;}
        }
        public static Vector3 Add(Vector3 a,Vector3 b)=>new Vector3(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static void TranslateMark(AssemblyMark mark,Vector3 delta)
        {
            Guard.Require(mark!=null&&delta!=null&&delta.IsFinite()&&Math.Abs(delta.Z)<1e-8,"Перемещение узла поддерживается только в мировой XY.");
            foreach(var inst in mark.Instances)inst.Placement.Origin=Add(inst.Placement.Origin,delta);
            mark.LayoutOrigin=Add(mark.LayoutOrigin??new Vector3(),delta);mark.Schema=Math.Max(mark.Schema,4);
            mark.Validate();
        }
    }
}
