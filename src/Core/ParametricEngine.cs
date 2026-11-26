using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KotNode.Core
{
    public sealed class MarkParameter
    {
        public string Name { get; set; }="";
        public string Label { get; set; }="";
        public double Value { get; set; }
        public string Formula { get; set; }="";
        public string Unit { get; set; }="мм";
    }
    public sealed class PartBinding
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public Guid PartId { get; set; }
        public string Property { get; set; }=""; // e.g. "Pipe.OuterDiameter", "Plate.Length", "CurvedPlate.AngleDeg"
        public string Formula { get; set; }="";
    }
    public enum PlacementRuleKind { OffsetFromPart=0, OnPipeSurface=1, CircularArray=2, LinearArray=3 }
    public sealed class PlacementRule
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public PlacementRuleKind Kind { get; set; }
        public Guid TargetInstanceId { get; set; }
        public Guid ReferenceInstanceId { get; set; }
        public string ExprX { get; set; }="0";
        public string ExprY { get; set; }="0";
        public string ExprZ { get; set; }="0";
        public string ExprAngleDeg { get; set; }="0";
        public string ExprCount { get; set; }="1";
        public List<Guid> GeneratedInstanceIds { get; set; }=new List<Guid>();
    }
    public sealed class SolveReport
    {
        public Dictionary<string,double> Values { get; set; }=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
        public int UpdatedParts { get; set; }
        public int UpdatedInstances { get; set; }
        public List<Guid> AddedInstances { get; set; }=new List<Guid>();
        public List<Guid> RemovedInstances { get; set; }=new List<Guid>();
    }

    // Safe, deterministic recursive-descent formula evaluator (no reflection/scripts/eval).
    public static class FormulaEngine
    {
        sealed class Parser
        {
            readonly string src;
            readonly IDictionary<string,double> vars;
            int pos;
            public Parser(string s,IDictionary<string,double> v){src=s??"";vars=v;}
            void Skip(){while(pos<src.Length&&char.IsWhiteSpace(src[pos]))pos++;}
            bool Match(string tok)
            {
                Skip();
                if(pos+tok.Length<=src.Length&&string.CompareOrdinal(src,pos,tok,0,tok.Length)==0)
                {pos+=tok.Length;return true;}
                return false;
            }
            public double ParseAll()
            {
                double v=ParseComparison();
                Skip();
                if(pos<src.Length)throw new InvalidDataException("Лишний символ в формуле: '"+src.Substring(pos)+"'");
                if(!Guard.Finite(v))throw new InvalidDataException("Формула дала некорректное число (NaN/Infinity).");
                return v;
            }
            double ParseComparison()
            {
                double left=ParseAddSub();
                Skip();
                if(Match("<="))return left<=ParseAddSub()?1:0;
                if(Match(">="))return left>=ParseAddSub()?1:0;
                if(Match("=="))return Math.Abs(left-ParseAddSub())<1e-9?1:0;
                if(Match("!="))return Math.Abs(left-ParseAddSub())>=1e-9?1:0;
                if(Match("<"))return left<ParseAddSub()?1:0;
                if(Match(">"))return left>ParseAddSub()?1:0;
                return left;
            }
            double ParseAddSub()
            {
                double v=ParseMulDiv();
                while(true)
                {
                    if(Match("+"))v+=ParseMulDiv();
                    else if(Match("-"))v-=ParseMulDiv();
                    else return v;
                }
            }
            double ParseMulDiv()
            {
                double v=ParsePower();
                while(true)
                {
                    if(Match("*"))v*=ParsePower();
                    else if(Match("/"))
                    {
                        double d=ParsePower();
                        if(Math.Abs(d)<=1e-12)throw new InvalidDataException("Деление на ноль в формуле.");
                        v/=d;
                    }
                    else return v;
                }
            }
            double ParsePower()
            {
                double v=ParseUnary();
                if(Match("^"))v=Math.Pow(v,ParsePower());
                return v;
            }
            double ParseUnary()
            {
                if(Match("+"))return ParseUnary();
                if(Match("-"))return -ParseUnary();
                return ParsePrimary();
            }
            double ParsePrimary()
            {
                Skip();
                if(pos>=src.Length)throw new InvalidDataException("Неожиданный конец формулы.");
                if(Match("("))
                {
                    double v=ParseComparison();
                    if(!Match(")"))throw new InvalidDataException("Не закрыта скобка в формуле.");
                    return v;
                }
                if(char.IsDigit(src[pos])||src[pos]=='.')
                {
                    int start=pos;
                    while(pos<src.Length&&(char.IsDigit(src[pos])||src[pos]=='.'))pos++;
                    string num=src.Substring(start,pos-start);
                    if(!double.TryParse(num,NumberStyles.Float,CultureInfo.InvariantCulture,out var val))
                        throw new InvalidDataException("Неверное число в формуле: "+num);
                    return val;
                }
                if(char.IsLetter(src[pos])||src[pos]=='_')
                {
                    int start=pos;
                    while(pos<src.Length&&(char.IsLetterOrDigit(src[pos])||src[pos]=='_'))pos++;
                    string id=src.Substring(start,pos-start);
                    if(Match("("))
                    {
                        var args=new List<double>();
                        if(!Match(")"))
                        {
                            do{args.Add(ParseComparison());}while(Match(",")||Match(";"));
                            if(!Match(")"))throw new InvalidDataException("Не закрыта скобка функции "+id);
                        }
                        return EvalFunc(id,args);
                    }
                    if(string.Equals(id,"pi",StringComparison.OrdinalIgnoreCase))return Math.PI;
                    if(vars!=null&&vars.TryGetValue(id,out var found))return found;
                    throw new InvalidDataException("Неизвестный параметр в формуле: "+id);
                }
                throw new InvalidDataException("Недопустимый символ в формуле: '"+src[pos]+"'");
            }
            static double EvalFunc(string name,List<double> a)
            {
                switch(name.ToLowerInvariant())
                {
                    case "pi": return Math.PI;
                    case "abs": Need(a,1,name);return Math.Abs(a[0]);
                    case "sqrt": Need(a,1,name);if(a[0]<0)throw new InvalidDataException("Корень из отрицательного числа.");return Math.Sqrt(a[0]);
                    case "sin": Need(a,1,name);return Math.Sin(a[0]*Math.PI/180.0);
                    case "cos": Need(a,1,name);return Math.Cos(a[0]*Math.PI/180.0);
                    case "tan": Need(a,1,name);return Math.Tan(a[0]*Math.PI/180.0);
                    case "rad": Need(a,1,name);return a[0]*Math.PI/180.0;
                    case "deg": Need(a,1,name);return a[0]*180.0/Math.PI;
                    case "floor": Need(a,1,name);return Math.Floor(a[0]);
                    case "ceil": Need(a,1,name);return Math.Ceiling(a[0]);
                    case "round": Need(a,1,name);return Math.Round(a[0]);
                    case "min": Need(a,2,name);return Math.Min(a[0],a[1]);
                    case "max": Need(a,2,name);return Math.Max(a[0],a[1]);
                    case "if": Need(a,3,name);return Math.Abs(a[0])>1e-9?a[1]:a[2];
                    default: throw new InvalidDataException("Неизвестная функция в формуле: "+name);
                }
            }
            static void Need(List<double> a,int n,string fn){if(a.Count!=n)throw new InvalidDataException("Функция "+fn+" требует "+n+" аргумент(ов).");}
        }

        public static double Evaluate(string expression,IDictionary<string,double> variables)
            =>new Parser(expression,variables).ParseAll();
        public static HashSet<string> ReferencedIdentifiers(string expression)
        {
            var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(string.IsNullOrWhiteSpace(expression))return ids;
            int i=0;
            while(i<expression.Length)
            {
                if(char.IsLetter(expression[i])||expression[i]=='_')
                {
                    int start=i;
                    while(i<expression.Length&&(char.IsLetterOrDigit(expression[i])||expression[i]=='_'))i++;
                    string word=expression.Substring(start,i-start);
                    int j=i;while(j<expression.Length&&char.IsWhiteSpace(expression[j]))j++;
                    if(!(j<expression.Length&&expression[j]=='(')&&!string.Equals(word,"pi",StringComparison.OrdinalIgnoreCase))
                        ids.Add(word);
                }
                else i++;
            }
            return ids;
        }
    }

    public static class ParametricEngine
    {
        public static Dictionary<string,double> EvaluateParameters(IList<MarkParameter> parameters)
        {
            var values=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
            if(parameters==null||parameters.Count==0)return values;
            var byName=new Dictionary<string,MarkParameter>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in parameters)
            {
                Guard.Require(p!=null&&!string.IsNullOrWhiteSpace(p.Name),"Задайте имя параметра.");
                string name=p.Name.Trim();
                Guard.Require(byName.ContainsKey(name)==false,"Дублирующийся параметр: "+name);
                byName[name]=p;
            }
            var state=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase); // 1=visiting, 2=done
            Action<string> eval=null;
            eval=name=>{
                if(state.TryGetValue(name,out var st))
                {
                    if(st==2)return;
                    throw new InvalidDataException("Обнаружена циклическая зависимость параметров: "+name);
                }
                state[name]=1;
                var param=byName[name];
                if(string.IsNullOrWhiteSpace(param.Formula))
                {
                    Guard.Require(Guard.Finite(param.Value),"Неверное значение параметра: "+name);
                    values[name]=param.Value;
                }
                else
                {
                    foreach(var dep in FormulaEngine.ReferencedIdentifiers(param.Formula))
                        if(byName.ContainsKey(dep))eval(dep);
                    double v=FormulaEngine.Evaluate(param.Formula,values);
                    param.Value=v;
                    values[name]=v;
                }
                state[name]=2;
            };
            foreach(var name in byName.Keys)eval(name);
            return values;
        }

        static void ApplyBinding(PartDefinition part,string prop,double val)
        {
            switch(prop)
            {
                case "Pipe.OuterDiameter": Guard.Require(part.Kind==PartKind.Pipe,"Привязка Pipe.* только для трубы.");part.Pipe.OuterDiameter=val;break;
                case "Pipe.Thickness": Guard.Require(part.Kind==PartKind.Pipe,"Привязка Pipe.* только для трубы.");part.Pipe.Thickness=val;break;
                case "Pipe.Length": Guard.Require(part.Kind==PartKind.Pipe,"Привязка Pipe.* только для трубы.");part.Pipe.Length=val;break;
                case "Plate.Length": Guard.Require(part.Kind==PartKind.RectangularPlate,"Привязка Plate.* только для прямоугольного листа.");part.Plate.Length=val;break;
                case "Plate.Width": Guard.Require(part.Kind==PartKind.RectangularPlate,"Привязка Plate.* только для прямоугольного листа.");part.Plate.Width=val;break;
                case "Plate.Thickness": Guard.Require(part.Kind==PartKind.RectangularPlate,"Привязка Plate.* только для прямоугольного листа.");part.Plate.Thickness=val;break;
                case "PolygonPlate.Thickness": Guard.Require(part.Kind==PartKind.PolygonPlate,"Привязка PolygonPlate.* только для контурного листа.");part.PolygonPlate.Thickness=val;break;
                case "CurvedPlate.InnerRadius": Guard.Require(part.Kind==PartKind.CurvedPlate,"Привязка CurvedPlate.* только для криволинейного листа.");part.CurvedPlate.InnerRadius=val;break;
                case "CurvedPlate.Thickness": Guard.Require(part.Kind==PartKind.CurvedPlate,"Привязка CurvedPlate.* только для криволинейного листа.");part.CurvedPlate.Thickness=val;break;
                case "CurvedPlate.Length": Guard.Require(part.Kind==PartKind.CurvedPlate,"Привязка CurvedPlate.* только для криволинейного листа.");part.CurvedPlate.Length=val;break;
                case "CurvedPlate.AngleDeg": Guard.Require(part.Kind==PartKind.CurvedPlate,"Привязка CurvedPlate.* только для криволинейного листа.");part.CurvedPlate.AngleRadians=val*Math.PI/180.0;break;
                case "IBeam.Length": Guard.Require(part.Kind==PartKind.IBeam,"Привязка IBeam.* только для двутавра.");part.IBeam.Length=val;break;
                case "Rebar.Diameter": Guard.Require(part.Kind==PartKind.Rebar,"Привязка Rebar.* только для арматуры.");part.Rebar.Diameter=val;break;
                default: throw new InvalidDataException("Неизвестное свойство привязки: "+prop);
            }
        }
        static RigidPlacement RotateAroundZ(RigidPlacement frame,double angleRad)
        {
            double c=Math.Cos(angleRad),s=Math.Sin(angleRad);
            var rot=new RigidPlacement{Origin=new Vector3(0,0,0),AxisX=new Vector3(c,s,0),AxisY=new Vector3(-s,c,0)};
            return rot.InParent(frame);
        }
        static RigidPlacement RotateAroundAxis(RigidPlacement p,RigidPlacement axisFrame,double angleRad)
        {
            var local=p.RelativeTo(axisFrame);
            double c=Math.Cos(angleRad),s=Math.Sin(angleRad);
            Func<Vector3,Vector3> rot=v=>new Vector3(v.X*c-v.Y*s,v.X*s+v.Y*c,v.Z);
            var rotatedLocal=new RigidPlacement{Origin=rot(local.Origin),AxisX=rot(local.AxisX),AxisY=rot(local.AxisY)};
            return rotatedLocal.InParent(axisFrame);
        }

        public static SolveReport Solve(AssemblyMark mark)
        {
            Guard.Require(mark!=null,"Марка не задана.");
            var draft=Json.Copy(mark);
            var report=new SolveReport();
            report.Values=EvaluateParameters(draft.Parameters);

            if(draft.Bindings!=null)
            {
                foreach(var b in draft.Bindings)
                {
                    var part=draft.Parts.FirstOrDefault(p=>p.Id==b.PartId);
                    if(part==null)continue;
                    double v=FormulaEngine.Evaluate(b.Formula,report.Values);
                    ApplyBinding(part,b.Property,v);
                    part.Validate();
                    report.UpdatedParts++;
                }
            }

            if(draft.PlacementRules!=null)
            {
                foreach(var rule in draft.PlacementRules)
                {
                    var target=draft.Instances.FirstOrDefault(i=>i.Id==rule.TargetInstanceId);
                    if(target==null)continue;
                    var refInst=rule.ReferenceInstanceId!=Guid.Empty?draft.Instances.FirstOrDefault(i=>i.Id==rule.ReferenceInstanceId):null;
                    var refFrame=refInst!=null?refInst.Placement:new RigidPlacement();
                    if(rule.Kind==PlacementRuleKind.OffsetFromPart)
                    {
                        double dx=FormulaEngine.Evaluate(rule.ExprX,report.Values);
                        double dy=FormulaEngine.Evaluate(rule.ExprY,report.Values);
                        double dz=FormulaEngine.Evaluate(rule.ExprZ,report.Values);
                        double ang=FormulaEngine.Evaluate(rule.ExprAngleDeg,report.Values)*Math.PI/180.0;
                        double c=Math.Cos(ang),s=Math.Sin(ang);
                        var rel=new RigidPlacement{Origin=new Vector3(dx,dy,dz),AxisX=new Vector3(c,s,0),AxisY=new Vector3(-s,c,0)};
                        target.Placement=rel.InParent(refFrame);
                        report.UpdatedInstances++;
                    }
                    else if(rule.Kind==PlacementRuleKind.OnPipeSurface)
                    {
                        Guard.Require(refInst!=null,"Для OnPipeSurface укажите экземпляр трубы.");
                        var pipePart=draft.Parts.Single(p=>p.Id==refInst.PartId);
                        Guard.Require(pipePart.Kind==PartKind.Pipe,"Опорная деталь для OnPipeSurface должна быть трубой.");
                        double radius=pipePart.Pipe.OuterDiameter/2.0;
                        double radialOff=FormulaEngine.Evaluate(rule.ExprX,report.Values);
                        double axialZ=FormulaEngine.Evaluate(rule.ExprZ,report.Values);
                        double ang=FormulaEngine.Evaluate(rule.ExprAngleDeg,report.Values)*Math.PI/180.0;
                        double r=radius+radialOff;
                        double c=Math.Cos(ang),s=Math.Sin(ang);
                        var rel=new RigidPlacement{Origin=new Vector3(r*c,r*s,axialZ),AxisX=new Vector3(c,s,0),AxisY=new Vector3(-s,c,0)};
                        target.Placement=rel.InParent(refFrame);
                        report.UpdatedInstances++;
                    }
                    else if(rule.Kind==PlacementRuleKind.CircularArray||rule.Kind==PlacementRuleKind.LinearArray)
                    {
                        int totalCount=(int)Math.Round(FormulaEngine.Evaluate(rule.ExprCount,report.Values));
                        Guard.Require(totalCount>=1&&totalCount<=200,"Число элементов массива должно быть от 1 до 200.");
                        int extra=totalCount-1;
                        if(rule.GeneratedInstanceIds==null)rule.GeneratedInstanceIds=new List<Guid>();
                        // Prune missing ids
                        rule.GeneratedInstanceIds.RemoveAll(id=>!draft.Instances.Any(i=>i.Id==id));
                        // Remove excess generated instances
                        while(rule.GeneratedInstanceIds.Count>extra)
                        {
                            var remId=rule.GeneratedInstanceIds[rule.GeneratedInstanceIds.Count-1];
                            rule.GeneratedInstanceIds.RemoveAt(rule.GeneratedInstanceIds.Count-1);
                            draft.Instances.RemoveAll(i=>i.Id==remId);
                            report.RemovedInstances.Add(remId);
                        }
                        // Add needed instances
                        while(rule.GeneratedInstanceIds.Count<extra)
                        {
                            var copy=new PartInstance{Id=Guid.NewGuid(),PartId=target.PartId,Placement=Json.Copy(target.Placement)};
                            draft.Instances.Add(copy);
                            rule.GeneratedInstanceIds.Add(copy.Id);
                            report.AddedInstances.Add(copy.Id);
                        }
                        double stepX=rule.Kind==PlacementRuleKind.LinearArray?FormulaEngine.Evaluate(rule.ExprX,report.Values):0;
                        double stepY=rule.Kind==PlacementRuleKind.LinearArray?FormulaEngine.Evaluate(rule.ExprY,report.Values):0;
                        double stepZ=rule.Kind==PlacementRuleKind.LinearArray?FormulaEngine.Evaluate(rule.ExprZ,report.Values):0;
                        double stepAng=rule.Kind==PlacementRuleKind.CircularArray
                            ? (string.IsNullOrWhiteSpace(rule.ExprAngleDeg)||rule.ExprAngleDeg.Trim()=="0"
                                ? 2*Math.PI/totalCount
                                : FormulaEngine.Evaluate(rule.ExprAngleDeg,report.Values)*Math.PI/180.0)
                            : 0;
                        for(int k=0;k<extra;k++)
                        {
                            var child=draft.Instances.Single(i=>i.Id==rule.GeneratedInstanceIds[k]);
                            child.PartId=target.PartId;
                            if(rule.Kind==PlacementRuleKind.CircularArray)
                                child.Placement=RotateAroundAxis(target.Placement,refFrame,stepAng*(k+1));
                            else
                            {
                                var o=target.Placement.Origin;
                                child.Placement=new RigidPlacement
                                {
                                    Origin=new Vector3(o.X+stepX*(k+1),o.Y+stepY*(k+1),o.Z+stepZ*(k+1)),
                                    AxisX=Json.Copy(target.Placement.AxisX),
                                    AxisY=Json.Copy(target.Placement.AxisY)
                                };
                            }
                            report.UpdatedInstances++;
                        }
                    }
                }
            }

            draft.Validate();
            // Commit validated draft back into mark
            mark.Parts=draft.Parts;
            mark.Instances=draft.Instances;
            mark.Parameters=draft.Parameters;
            mark.Bindings=draft.Bindings;
            mark.PlacementRules=draft.PlacementRules;
            return report;
        }
    }
}
